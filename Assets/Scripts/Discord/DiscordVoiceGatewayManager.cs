using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Discord Voice Gateway専用のWebSocket通信管理クラス
/// </summary>
public class DiscordVoiceGatewayManager : IDisposable {
    // イベント
    public delegate void DiscordLogDelegate(string logMessage);
    public event DiscordLogDelegate OnDiscordLog;
    
    public delegate void MessageReceivedDelegate(string message);
    public event MessageReceivedDelegate OnMessageReceived;
    
    public delegate void ConnectionStateChangedDelegate(bool isConnected);
    public event ConnectionStateChangedDelegate OnConnectionStateChanged;
    
    // Voice Gateway メッセージ処理イベント
    public delegate void VoiceHelloReceivedDelegate(double heartbeatInterval);
    public event VoiceHelloReceivedDelegate OnVoiceHelloReceived;
    
    public delegate void VoiceReadyReceivedDelegate(uint ssrc, string ip, int port, string[] modes);
    public event VoiceReadyReceivedDelegate OnVoiceReadyReceived;
    
    public delegate void VoiceSessionDescriptionReceivedDelegate(byte[] secretKey, string mode);
    public event VoiceSessionDescriptionReceivedDelegate OnVoiceSessionDescriptionReceived;
    
    public delegate void VoiceHeartbeatAckReceivedDelegate();
    public event VoiceHeartbeatAckReceivedDelegate OnVoiceHeartbeatAckReceived;
    
    public delegate void VoiceSpeakingReceivedDelegate(bool speaking, uint ssrc, string userId);
    public event VoiceSpeakingReceivedDelegate OnVoiceSpeakingReceived;
    
    // 接続関連
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isConnected = false;
    
    // ハートビート管理
    private System.Timers.Timer _heartbeatTimer;
    private long _lastHeartbeatAck = 0;
    private long _lastHeartbeatSend = 0;
    private int _missedHeartbeats = 0;
    private int? _ping = null;

    // Voice Gateway v8 の Buffered Resume 用に、サーバから来た最新の `seq` を記録する。
    // 次の Heartbeat の `seq_ack` フィールドにこの値を入れて返す必要があり、
    // 返さないと Discord は HB を無効扱いし `4006 Session is no longer valid` で切断する
    // （docs/integrations/discord.md § 12.4 で真因確定）。
    //
    // **初期値は -1**（@discordjs/voice 公式実装に準拠）。Discord 公式仕様では `seq_ack` は number 型で
    // 必須なので、null を送ると未定義動作となる。「まだ何も受信していない」ことを表す番兵値として -1 を使う。
    // 接続単位でリセットしたいので、Hello 受信時に -1 に戻す
    // （@discordjs/voice は VoiceWebSocket インスタンスを毎回作り直すので、こちらは明示リセットが必要）。
    private long _lastSeqAck = -1L;
    private readonly object _seqAckLock = new object();
    
    // Voice Identify 用情報
    private string _guildId;
    private string _userId;
    private string _sessionId;
    private string _token;

    // 自動再接続関連（docs/integrations/discord.md § 12.4 で実装した二段構えの 1 段目）
    // 致命的な close code（再接続しても直らない token 不正・kick・仕様非対応）は再接続しない。
    // それ以外は無限ループで指数バックオフ再接続を試行し、StopBot/Dispose で停止する。
    private string _voiceEndpoint;
    private int _reconnectAttempts = 0;
    private bool _autoReconnectEnabled = true;
    private static readonly HashSet<int> FatalVoiceCloseCodes = new HashSet<int> {
        4001, 4002, 4003, 4004, 4005, 4011, 4012, 4014, 4016, 4017
    };
    // バックオフのジッタ用。UnityEngine.Random はメインスレッド限定なので System.Random を使う。
    private static readonly System.Random _backoffRng = new System.Random();

    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;

    /// <summary>
    /// Voice Gateway用のJSONオブジェクト作成ヘルパー
    /// </summary>
    public static class VoicePayloadHelper {
        /// <summary>
        /// Voice Gateway用ハートビートペイロードを作成（v8 形式）。
        ///
        /// **重要**: Voice Gateway v8 以降では `d` がオブジェクトになり、`t` (nonce) と
        /// `seq_ack` (gateway から受け取った最後の numbered message の seq) を必須で含める。
        /// v8 の旧形式 `d = &lt;nonce&gt;` で送ると Discord は HB を無効と判定して ACK を返さず、
        /// `heartbeat_interval` 経過時に `4006 Session is no longer valid` で session を破棄する
        /// （実機ログで確定、docs/integrations/discord.md § 12.4）。
        ///
        /// `seqAck` は Gateway から numbered message を受信していない初期段階では **`-1`** を送る
        /// （@discordjs/voice 公式実装に準拠）。null を送る挙動は公式仕様にも公式実装にも存在しないため避ける。
        /// 公式仕様: https://discord.com/developers/docs/topics/voice-connections#heartbeating
        /// 公式実装: https://github.com/discordjs/discord.js/blob/%40discordjs/voice%400.19.0/packages/voice/src/networking/VoiceWebSocket.ts
        /// </summary>
        public static object CreateHeartbeatPayload(long nonce, long seqAck) => new {
            op = 3,
            d = new {
                t = nonce,
                seq_ack = seqAck
            }
        };
        
        /// <summary>
        /// Voice Gateway用Identifyペイロードを作成。
        /// 
        /// `max_dave_protocol_version` は Discord が 2024 年に導入した E2EE プロトコル
        /// (DAVE: Discord Audio & Video End-to-End Encryption) の対応宣言フィールド。
        /// - 値 0 = DAVE 非対応（従来の transport-only encryption だけ使う）
        /// - 値 1 = DAVE v1 対応
        /// このフィールドを送らないと Discord は `4017 E2EE/DAVE protocol required` で
        /// Identify を拒否してくる（観測済み）。zagaroid は DAVE 復号を実装していないため
        /// 0 を送って「従来モードで繋がせてくれ」と宣言する。
        /// 詳細は docs/integrations/discord.md § 4.1 / § 11.1。
        /// </summary>
        public static object CreateVoiceIdentifyPayload(string guildId, string userId, string sessionId, string token) => new {
            op = 0,
            d = new {
                server_id = guildId,
                user_id = userId,
                session_id = sessionId,
                token = token,
                max_dave_protocol_version = 0
            }
        };
        
        /// <summary>
        /// プロトコル選択ペイロードを作成
        /// </summary>
        public static object CreateSelectProtocolPayload(string ip, int port, string mode) => new {
            op = 1,
            d = new {
                protocol = "udp",
                data = new {
                    address = ip,
                    port = port,
                    mode = mode
                }
            }
        };
    }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public DiscordVoiceGatewayManager(bool enableDebugLogging = true) {
        _enableDebugLogging = enableDebugLogging;
        _cancellationTokenSource = new CancellationTokenSource();
    }
    
    /// <summary>
    /// Voice Identify に使用する情報を設定
    /// </summary>
    public void SetIdentity(string guildId, string userId, string sessionId, string token)
    {
        _guildId = guildId;
        _userId = userId;
        _sessionId = sessionId;
        _token = token;
        LogMessage("🔐 Voice Identify parameters set", LogLevel.Debug);
    }
    
    /// <summary>
    /// Voice Gatewayに接続
    /// </summary>
    public async Task<bool> Connect(string endpoint)
    {
        try
        {
            // 競合ガード: Dispose と TryAutoReconnect が並走した場合、
            // ここに来た時点で _cancellationTokenSource が null になっている可能性がある。
            var cts = _cancellationTokenSource;
            if (cts == null || cts.IsCancellationRequested) {
                LogMessage("⚠️ Voice Gateway Connect aborted: manager already disposed/cancelled", LogLevel.Warning);
                _isConnected = false;
                OnConnectionStateChanged?.Invoke(false);
                return false;
            }

            LogMessage($"🔌 Connecting to Voice Gateway: {endpoint}...", LogLevel.Info);

            // 自動再接続時に同じ endpoint を再利用するため保存する。
            // 注意: Discord は voice_server_update で endpoint を更新することがあるが、
            // 4006/4015 の自動再接続は同一 endpoint で十分と判断（ダメなら voice_state 再発行に委ねる）。
            _voiceEndpoint = endpoint;

            // 既存ソケットが残っている場合は破棄してから新規 ClientWebSocket を作る（再接続経路で必要）
            try { _webSocket?.Abort(); } catch { /* best-effort */ }
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            
            // 接続タイムアウトを設定（30秒）
            // cts は冒頭でローカル変数に取った CancellationTokenSource を使い、
            // Dispose 並走時の NRE を防ぐ
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token))
            {
                // Discord Voice Gateway は v8 で DAVE protocol（E2EE）を導入した。
                // 2024 年後半から DAVE 非対応の Identify は `4017 E2EE/DAVE protocol required` で拒否されるため、
                // v8 まで上げたうえで Identify に `max_dave_protocol_version = 0` を明示する必要がある。
                // 詳細は docs/integrations/discord.md § 11.1 / § 4.1。
                await _webSocket.ConnectAsync(new Uri($"wss://{endpoint}/?v=8"), combinedCts.Token);
            }
            
            _isConnected = true;
            OnConnectionStateChanged?.Invoke(true);
            
            LogMessage("✅ Voice Gateway connected successfully", LogLevel.Info);
            
            // メッセージ受信ループを開始（cts は冒頭で取得済みのローカル変数）
            _ = Task.Run(ReceiveMessages, cts.Token);
            
            return true;
        }
        catch (OperationCanceledException ex)
        {
            LogMessage($"❌ Voice Gateway connection timeout: {ex.Message}", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
        catch (WebSocketException ex)
        {
            LogMessage($"❌ Voice Gateway WebSocket error: {ex.Message} (ErrorCode: {ex.WebSocketErrorCode})", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway connection failed: {ex.Message}", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
    }
    
    /// <summary>
    /// メッセージを送信
    /// </summary>
    public async Task SendMessage(string message)
    {
        try
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                LogMessage($"📤 Voice Gateway message sent: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
            }
            else
            {
                LogMessage("❌ Voice Gateway is not connected", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Send voice gateway message error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// メッセージを受信
    /// </summary>
    private async Task ReceiveMessages() {
        var buffer = new byte[DiscordConstants.WEBSOCKET_BUFFER_SIZE];
        var messageBuffer = new List<byte>();

        // Dispose 並走時に _cancellationTokenSource = null になる窓があるため、ローカル変数に固定する
        var cts = _cancellationTokenSource;
        if (cts == null) return;

        // 切断時の close code を保持して、ループ脱出後の自動再接続で参照する。
        // null = ネットワーク瞬断や受信例外（CloseStatus が取れないケース）扱い。
        int? closeCodeCaptured = null;
        bool closeWasObserved = false;

        while (_webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested) {
            try {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                
                if (result.MessageType == WebSocketMessageType.Text) {
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    if (result.EndOfMessage) {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        
                        LogMessage($"📥 Voice Gateway message received: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
                        OnMessageReceived?.Invoke(message);
                        
                        // Voice Gatewayメッセージを処理
                        _ = ProcessVoiceMessage(message);
                    }
                } else if (result.MessageType == WebSocketMessageType.Close) {
                    // Discord は Identify 拒否時などに 4001/4004/4011/4014 等の close code を返す。
                    // ここを潰すと「Voice Gateway connection closed」だけが残り原因不明になるため、
                    // 必ず code / status / reason をセットで出すこと（docs/integrations/discord.md § 11.1）。
                    int? code = result.CloseStatus.HasValue ? (int?)(int)result.CloseStatus.Value : null;
                    string reason = string.IsNullOrEmpty(result.CloseStatusDescription) ? "(none)" : result.CloseStatusDescription;
                    LogMessage(
                        $"⚠️ Voice Gateway connection closed by server: code={(code?.ToString() ?? "null")} status={result.CloseStatus} reason='{reason}'",
                        LogLevel.Warning);
                    closeCodeCaptured = code;
                    closeWasObserved = true;
                    break;
                }
            } catch (WebSocketException wsex) {
                // 切断直後の例外ルート。WebSocketException 経由でも _webSocket.CloseStatus から close code が拾える場合がある。
                int? code = _webSocket?.CloseStatus.HasValue == true ? (int?)(int)_webSocket.CloseStatus.Value : null;
                LogMessage(
                    $"⚠️ Voice Gateway receive WebSocketException: {wsex.Message} (WebSocketErrorCode={wsex.WebSocketErrorCode}, closeCode={(code?.ToString() ?? "null")})",
                    LogLevel.Warning);
                closeCodeCaptured = code;
                closeWasObserved = true;
                break;
            } catch (Exception ex) {
                LogMessage($"Voice Gateway receive error: {ex.Message}", LogLevel.Error);
                closeCodeCaptured = null;
                closeWasObserved = true;
                break;
            }
        }

        _isConnected = false;
        OnConnectionStateChanged?.Invoke(false);

        // 受信ループを抜けた = 切断発生。Dispose 経由でなく、自動再接続が許可されている場合のみ再接続を試みる。
        // Task.Run で別タスクに逃がすのは、ReceiveMessages 自身が `_ = Task.Run(ReceiveMessages, ...)` で動いており
        // ここでブロッキング再接続するとそのまま Connect 内で新しい受信ループが入れ子になるのを避けるため。
        if (closeWasObserved && _autoReconnectEnabled && !cts.IsCancellationRequested) {
            _ = Task.Run(() => TryAutoReconnect(closeCodeCaptured));
        }
    }

    /// <summary>
    /// 自動再接続を試みる。
    /// 致命的 close code は何度繋いでも直らないため停止し、それ以外は無限ループで指数バックオフ再接続。
    /// バックオフは 1s → 2s → 4s → 8s → 16s → 30s で頭打ち（以降は 30s 間隔で永続再試行）。
    /// バックオフカウンタは Session Description 受信（= Voice Gateway フロー完走）でリセットされる
    /// （docs/integrations/discord.md § 12.4）。
    /// </summary>
    private async Task TryAutoReconnect(int? closeCode) {
        if (!_autoReconnectEnabled) return;

        var cts = _cancellationTokenSource;
        if (cts == null || cts.IsCancellationRequested) {
            // Dispose / StopBot 経由で停止済み
            return;
        }

        if (FatalVoiceCloseCodes.Contains(closeCode ?? -1)) {
            // 致命的: 4004 (token不正) / 4014 (BOT kick) / 4017 (DAVE要求) など。
            // 再接続しても同じエラーが返るので、ユーザーが UI から手動で再起動するまで待つ。
            LogMessage(
                $"❌ Voice Gateway closed with non-recoverable code={closeCode}; auto-reconnect aborted (manual Bot restart required)",
                LogLevel.Error);
            return;
        }

        if (string.IsNullOrEmpty(_voiceEndpoint)) {
            LogMessage("❌ Voice Gateway endpoint is empty; cannot auto-reconnect", LogLevel.Error);
            return;
        }

        int delayMs = ComputeBackoffDelayMs(_reconnectAttempts);
        _reconnectAttempts++;
        LogMessage(
            $"🔄 Auto-reconnecting Voice Gateway in {delayMs}ms (attempt={_reconnectAttempts}, closeCode={closeCode?.ToString() ?? "null"}, endpoint={_voiceEndpoint})",
            LogLevel.Info);

        try {
            await Task.Delay(delayMs, cts.Token);
        } catch (OperationCanceledException) {
            return;
        }

        if (cts.IsCancellationRequested || !_autoReconnectEnabled) return;

        try {
            // ハートビートタイマー等の前世代リソースを止める
            _heartbeatTimer?.Stop();
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _missedHeartbeats = 0;
            _lastHeartbeatSend = 0;
            _lastHeartbeatAck = 0;

            await Connect(_voiceEndpoint);
        } catch (Exception ex) {
            LogMessage($"❌ Voice Gateway auto-reconnect failed: {ex.Message}", LogLevel.Error);
            // 例外が出ても受信ループは Connect 内で再起動されないので、
            // ここでもう一度バックオフ再接続を仕掛ける（無限ループ意図、StopBot で止まる）。
            _ = Task.Run(() => TryAutoReconnect(null));
        }
    }

    /// <summary>
    /// 指数バックオフの待ち時間 (ms) を計算。1s→2s→4s→8s→16s→30s で頭打ち、各回 ±20% のジッタ。
    /// </summary>
    private static int ComputeBackoffDelayMs(int attempt) {
        int[] schedule = { 1000, 2000, 4000, 8000, 16000 };
        int baseMs = (attempt >= 0 && attempt < schedule.Length) ? schedule[attempt] : 30000;
        // 0.8 〜 1.2 の範囲でジッタ
        double jitter;
        lock (_backoffRng) {
            jitter = 0.8 + (_backoffRng.NextDouble() * 0.4);
        }
        return (int)(baseMs * jitter);
    }

        /// <summary>
    /// Voice Gatewayメッセージを処理
    /// </summary>
    private async Task ProcessVoiceMessage(string message) {
        try {
            var payload = JsonConvert.DeserializeObject<VoiceGatewayPayload>(message);
            LogMessage($"📥 Processing Voice Gateway message: op={payload.op}", LogLevel.Debug);

            // v8 Buffered Resume: サーバから来た numbered message の seq を記録して、
            // 次の Heartbeat の seq_ack で返す。これを怠ると Discord は HB を無効扱いして 4006 で切る。
            if (payload.seq.HasValue) {
                lock (_seqAckLock) {
                    _lastSeqAck = payload.seq.Value;
                }
            }

            switch (payload.op) {
                case 8: await HandleVoiceHello(payload); break;
                case 2: await HandleVoiceReady(payload); break;
                case 4: await HandleVoiceSessionDescription(payload); break;
                case 6: HandleVoiceHeartbeatAck(); break;
                case 5: 
                    // LogMessage($"🎤 Received op5 (Speaking) message", LogLevel.Info);
                    HandleVoiceSpeaking(payload); 
                    break;
                case 3: LogMessage($"📤 Voice Gateway heartbeat echo received (ignored) at {DateTime.Now:HH:mm:ss.fff}"); break;
                case 11: case 15: case 18: case 20:
                    // Voice Gateway v8 で増えた非公開 op 群。応答不要のため完全無視。
                    //   op=11: Client Connect / Disconnect 系（推定）
                    //   op=15: クライアント別音声品質設定（{"any":N,"<userId 下4桁>":N}）。データ構造未公開
                    //   op=18 / 20: DAVE プロトコル関連。zagaroid は max_dave_protocol_version=0 で非対応宣言済み
                    // 詳細は docs/integrations/discord.md § 11.1 / § 12.4 参照
                    break;
                default: LogUnknownVoiceMessage(payload.op, payload.d); break;
            }
        } catch (Exception ex) {
            LogMessage($"Voice message processing error: {ex.Message}", LogLevel.Error);
            LogMessage($"Raw message: {message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// Voice GatewayのHelloメッセージを処理
    /// </summary>
    private async Task HandleVoiceHello(VoiceGatewayPayload payload) {
        // 新しい Voice Gateway 接続が始まる = 旧 session の seq 系列はリセットされる。
        // @discordjs/voice は VoiceWebSocket インスタンスを毎回作り直す設計なので明示リセット不要だが、
        // zagaroid は DiscordVoiceGatewayManager を使い回すため、再接続時にもここで -1 に戻す必要がある。
        // 古い seq を返してしまうと Discord 側で session 不整合と判定され再び 4006 で切られる可能性がある。
        lock (_seqAckLock) {
            _lastSeqAck = -1L;
        }

        LogMessage($"🔌 Voice Gateway Hello received at {DateTime.Now:HH:mm:ss.fff}", LogLevel.Info);
        var helloData = JsonConvert.DeserializeObject<VoiceHelloData>(payload.d.ToString());
        // Hello受信時に内部でハートビートを開始
        StartHeartbeat(helloData.heartbeat_interval);
        OnVoiceHelloReceived?.Invoke(helloData.heartbeat_interval);
        
        // Hello 後に Identify を自動送信（必要情報が揃っている場合）
        if (!string.IsNullOrEmpty(_guildId) && !string.IsNullOrEmpty(_userId) &&
            !string.IsNullOrEmpty(_sessionId) && !string.IsNullOrEmpty(_token))
        {
            try
            {
                var identify = VoicePayloadHelper.CreateVoiceIdentifyPayload(_guildId, _userId, _sessionId, _token);
                var identifyJson = JsonConvert.SerializeObject(identify);
                LogMessage($"📤 Sending Voice Identify after Hello", LogLevel.Info);
                await SendMessage(identifyJson);
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Failed to send Voice Identify: {ex.Message}", LogLevel.Error);
            }
        }
        else
        {
            LogMessage("⚠️ Voice Identify parameters are not fully set; skipping Identify send", LogLevel.Warning);
        }
    }
    
    /// <summary>
    /// Voice GatewayのReadyメッセージを処理
    /// </summary>
    private async Task HandleVoiceReady(VoiceGatewayPayload payload) {
        LogMessage($"🔌 Voice Gateway Ready received at {DateTime.Now:HH:mm:ss.fff}", LogLevel.Info);
        var readyData = JsonConvert.DeserializeObject<VoiceReadyData>(payload.d.ToString());
        OnVoiceReadyReceived?.Invoke(readyData.ssrc, readyData.ip, readyData.port, readyData.modes);
    }
    
    /// <summary>
    /// Voice GatewayのSession Descriptionメッセージを処理
    /// </summary>
    private async Task HandleVoiceSessionDescription(VoiceGatewayPayload payload) {
        LogMessage($"🔌 Voice Gateway Session Description received at {DateTime.Now:HH:mm:ss.fff}", LogLevel.Info);
        var sessionData = JsonConvert.DeserializeObject<VoiceSessionDescriptionData>(payload.d.ToString());

        // Session Description = Voice Gateway フローが最後まで成功した証。
        // ここまで到達したら自動再接続のバックオフカウンタをリセットする。
        // 4006 が永続するケース（毎回同じ session で切られる）ではここに到達しないため、
        // バックオフは累積して 30s に張り付く（意図、docs/integrations/discord.md § 12.4）。
        _reconnectAttempts = 0;

        OnVoiceSessionDescriptionReceived?.Invoke(sessionData.secret_key, sessionData.mode);
    }
    
    /// <summary>
    /// Voice GatewayのHeartbeat ACKを処理
    /// </summary>
    private void HandleVoiceHeartbeatAck() {
        HandleHeartbeatAck();
        OnVoiceHeartbeatAckReceived?.Invoke();
    }
    
    /// <summary>
    /// Voice GatewayのSpeakingメッセージを処理
    /// </summary>
    private void HandleVoiceSpeaking(VoiceGatewayPayload payload) {
        var speakingData = JsonConvert.DeserializeObject<VoiceSpeakingData>(payload.d.ToString());
        LogMessage($"🎤 Voice Gateway Speaking received: user_id={speakingData.user_id}, ssrc={speakingData.ssrc}, speaking={speakingData.speaking} at {DateTime.Now:HH:mm:ss.fff}", LogLevel.Info);
        
        if (speakingData.user_id != null) {
            OnVoiceSpeakingReceived?.Invoke(speakingData.speaking, speakingData.ssrc, speakingData.user_id);
        }
    }
    
    /// <summary>
    /// 未知のVoice Gatewayメッセージをログ出力
    /// </summary>
    private void LogUnknownVoiceMessage(int opCode, object data) {
        LogMessage($"❓ Unknown Voice Gateway message: op={opCode}, data={JsonConvert.SerializeObject(data)}", LogLevel.Warning);
    }
    
    /// <summary>
    /// ハートビートを開始
    /// </summary>
    public void StartHeartbeat(double interval)
    {
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        
        int intervalMs = (int)interval;
        _heartbeatTimer = new System.Timers.Timer(intervalMs);
        _heartbeatTimer.Elapsed += async (sender, e) => {
            if (_isConnected && _webSocket?.State == WebSocketState.Open)
            {
                await SendHeartbeat();
            }
            else
            {
                _heartbeatTimer?.Stop();
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
            }
        };
        _heartbeatTimer.Start();
        LogMessage($"💓 Voice Gateway heartbeat started (interval: {intervalMs}ms)", LogLevel.Info);
    }
    
    /// <summary>
    /// ハートビートを送信
    /// </summary>
    private async Task SendHeartbeat()
    {
        try
        {
            // ACKタイムアウト検出（15秒）
            if (_lastHeartbeatSend != 0)
            {
                var timeSinceLastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastHeartbeatSend;
                if (timeSinceLastHeartbeat > 15000 && _missedHeartbeats >= 1)
                {
                    LogMessage($"❌ Voice Gateway heartbeat ACK timeout ({timeSinceLastHeartbeat}ms > 15000ms)", LogLevel.Error);
                    await _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Heartbeat ACK timeout", CancellationToken.None);
                    return;
                }
            }
            
            // ミスしたハートビート数チェック
            if (_lastHeartbeatSend != 0 && _missedHeartbeats >= 3)
            {
                LogMessage($"❌ Voice Gateway missed too many heartbeats ({_missedHeartbeats}/3)", LogLevel.Error);
                await _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Too many missed heartbeats", CancellationToken.None);
                return;
            }
            
            _lastHeartbeatSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _missedHeartbeats++;

            // Voice Gateway v8: Heartbeat には nonce (`t`) と最新 seq (`seq_ack`) を含める。
            // 受信前は `_lastSeqAck = -1` で始まり、以降は受信した seq の最新値を保持する
            // （@discordjs/voice 公式実装に準拠、docs/integrations/discord.md § 4.1.2 / § 12.4）。
            var nonce = _lastHeartbeatSend;
            long seqAckSnapshot;
            lock (_seqAckLock) {
                seqAckSnapshot = _lastSeqAck;
            }
            var heartbeat = VoicePayloadHelper.CreateHeartbeatPayload(nonce, seqAckSnapshot);
            var heartbeatJson = JsonConvert.SerializeObject(heartbeat);
            await SendMessage(heartbeatJson);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway heartbeat error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// ハートビートACKを処理
    /// </summary>
    public void HandleHeartbeatAck()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // 前回のACKから短時間で重複ACKが来た場合は無視
        if (_lastHeartbeatAck != 0 && (currentTime - _lastHeartbeatAck) < 100)
        {
            return;
        }
        
        _lastHeartbeatAck = currentTime;
        _missedHeartbeats = 0;
        
        if (_lastHeartbeatSend != 0)
        {
            _ping = (int)(_lastHeartbeatAck - _lastHeartbeatSend);
        }
    }
    
    /// <summary>
    /// 再接続
    /// </summary>
    public async Task Reconnect(string endpoint)
    {
        LogMessage("🔄 Attempting to reconnect to Voice Gateway...", LogLevel.Info);
        
        try
        {
            _webSocket?.Dispose();
            _webSocket = null;
            _isConnected = false;
            
            await Task.Delay(DiscordConstants.RECONNECT_DELAY);
            await Connect(endpoint);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway reconnection failed: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 接続状態を取得
    /// </summary>
    public bool IsConnected => _isConnected;
    
    /// <summary>
    /// Pingを取得
    /// </summary>
    public int? Ping => _ping;
    
    /// <summary>
    /// ログメッセージを生成し、イベントを発行
    /// </summary>
    private void LogMessage(string message, LogLevel level = LogLevel.Info)
    {
        if (!_enableDebugLogging && level == LogLevel.Debug) return;
        
        string prefix;
        switch (level)
        {
            case LogLevel.Debug:
                prefix = "🔍";
                break;
            case LogLevel.Warning:
                prefix = "⚠️";
                break;
            case LogLevel.Error:
                prefix = "❌";
                break;
            default:
                prefix = "ℹ️";
                break;
        }
        
        string logMessage = $"[DiscordVoiceGateway] {DateTime.Now:HH:mm:ss} {prefix} {message}";
        OnDiscordLog?.Invoke(logMessage);
    }
    
    /// <summary>
    /// リソースをクリーンアップ
    /// </summary>
    public void Dispose()
    {
        LogMessage("🗑️ DiscordVoiceGatewayManager disposing - performing cleanup", LogLevel.Info);

        // 自動再接続ループを止めるフラグを先に倒す。これがないと TryAutoReconnect が
        // バックオフ中に CancellationToken をすり抜けて Connect を呼び直すリスクがある。
        _autoReconnectEnabled = false;

        _cancellationTokenSource?.Cancel();

        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        
        _webSocket?.Dispose();
        _webSocket = null;
        
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        LogMessage("✅ DiscordVoiceGatewayManager cleanup completed", LogLevel.Info);
    }
} 

[Serializable]
public class VoiceGatewayPayload {
    public int op;
    public object d;
    // Voice Gateway v8 で server -> client メッセージに振られる sequence number。
    // 全ての server-sent opcode に付くわけではないので nullable。
    // 受信したらクライアント側は最新値を保持して次の Heartbeat の seq_ack で返す
    // （docs/integrations/discord.md § 4.2.1 / § 12.4）。
    public long? seq;
}

[Serializable]
public class VoiceReadyData { public uint ssrc; public string ip; public int port; public string[] modes; }

[Serializable]
public class VoiceSessionDescriptionData { public byte[] secret_key; public string mode; }

[Serializable]
public class VoiceSpeakingData { public bool speaking; public uint ssrc; public string user_id; }

[Serializable]
public class VoiceHelloData { public double heartbeat_interval; }