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
    
    // Voice Identify 用情報
    private string _guildId;
    private string _userId;
    private string _sessionId;
    private string _token;
    
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;

    /// <summary>
    /// Voice Gateway用のJSONオブジェクト作成ヘルパー
    /// </summary>
    public static class VoicePayloadHelper {
        /// <summary>
        /// Voice Gateway用ハートビートペイロードを作成
        /// </summary>
        public static object CreateHeartbeatPayload(long nonce) => new {
            op = 3,
            d = nonce
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
            LogMessage($"🔌 Connecting to Voice Gateway: {endpoint}...", LogLevel.Info);
            
            _webSocket = new ClientWebSocket();
            
            // 接続タイムアウトを設定（30秒）
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token))
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
            
            // メッセージ受信ループを開始
            _ = Task.Run(ReceiveMessages, _cancellationTokenSource.Token);
            
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
        
        while (_webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested) {
            try {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
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
                    break;
                }
            } catch (WebSocketException wsex) {
                // 切断直後の例外ルート。WebSocketException 経由でも _webSocket.CloseStatus から close code が拾える場合がある。
                int? code = _webSocket?.CloseStatus.HasValue == true ? (int?)(int)_webSocket.CloseStatus.Value : null;
                LogMessage(
                    $"⚠️ Voice Gateway receive WebSocketException: {wsex.Message} (WebSocketErrorCode={wsex.WebSocketErrorCode}, closeCode={(code?.ToString() ?? "null")})",
                    LogLevel.Warning);
                break;
            } catch (Exception ex) {
                LogMessage($"Voice Gateway receive error: {ex.Message}", LogLevel.Error);
                break;
            }
        }
        
        _isConnected = false;
        OnConnectionStateChanged?.Invoke(false);
    }
    
    /// <summary>
    /// Voice Gatewayメッセージを処理
    /// </summary>
    private async Task ProcessVoiceMessage(string message) {
        try {
            var payload = JsonConvert.DeserializeObject<VoiceGatewayPayload>(message);
            LogMessage($"📥 Processing Voice Gateway message: op={payload.op}", LogLevel.Debug);
            
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
                case 11: case 18: case 20: 
                    // LogMessage($"DEAD BEEF Received op{payload.op} message: {payload.d}", LogLevel.Info);
                    break; // 無視するメッセージ
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
            
            // Voice Gateway準拠：nonceのみでハートビート送信
            var nonce = _lastHeartbeatSend;
            var heartbeat = VoicePayloadHelper.CreateHeartbeatPayload(nonce);
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
}

[Serializable]
public class VoiceReadyData { public uint ssrc; public string ip; public int port; public string[] modes; }

[Serializable]
public class VoiceSessionDescriptionData { public byte[] secret_key; public string mode; }

[Serializable]
public class VoiceSpeakingData { public bool speaking; public uint ssrc; public string user_id; }

[Serializable]
public class VoiceHelloData { public double heartbeat_interval; }