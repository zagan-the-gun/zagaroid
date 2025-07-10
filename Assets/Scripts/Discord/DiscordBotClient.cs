using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;
using System.Net.Http;
using Concentus;
using Newtonsoft.Json.Linq;

/// <summary>
/// Discord Bot関連の定数定義
/// </summary>
public static class DiscordConstants
{
    // ネットワーク関連
    public const int WEBSOCKET_BUFFER_SIZE = 4096;
    public const int UDP_BUFFER_SIZE = 65536;
    public const int UDP_SEND_TIMEOUT = 5000;
    public const int UDP_DISCOVERY_TIMEOUT = 3000;
    public const int UDP_RECEIVE_TIMEOUT = 1000;
    public const int UDP_DISCOVERY_PACKET_SIZE = 74;
    public const int RTP_HEADER_SIZE = 12;
    public const int MIN_ENCRYPTED_DATA_SIZE = 40;
    public const int MIN_AUDIO_PACKET_SIZE = 60;
    public const int DISCORD_HEADER_SIZE = 12;
    
    // 音声処理関連
    public const int OPUS_FRAME_SIZE = 960;
    public const int SAMPLE_RATE_48K = 48000;
    public const int SAMPLE_RATE_16K = 16000;
    public const int CHANNELS_STEREO = 2;
    public const int CHANNELS_MONO = 1;
    public const float PCM_SCALE_FACTOR = 32768.0f;
    public const int AUDIO_BUFFER_THRESHOLD = 16000 * 2; // 2秒分
    public const int AUDIO_BUFFER_MIN_SIZE = 1600; // 0.1秒分
    
    // タイムアウト関連
    public const int RECONNECT_DELAY = 5000;
    public const int UDP_PACKET_TIMEOUT = 30;
    public const int UDP_IDLE_TIMEOUT = 60;
    
    // 音声認識関連
    public const int WITA_API_SAMPLE_RATE = 16000;
    public const int WITA_API_CHANNELS = 1;
    
    // Discord Gateway関連
    public const int DISCORD_INTENTS = 32509;
    public const string DISCORD_OS = "unity";
    public const string DISCORD_BROWSER = "unity-bot";
    public const string DISCORD_DEVICE = "unity-bot";
    public const string DISCORD_PROTOCOL = "udp";
    
    // Discord.js準拠の暗号化モード
    public static readonly string[] SUPPORTED_ENCRYPTION_MODES = { 
        "xsalsa20_poly1305", 
        "xsalsa20_poly1305_suffix", 
        "aead_xchacha20_poly1305_rtpsize", 
        "aead_aes256_gcm_rtpsize" 
    };
    
    public const string DEFAULT_ENCRYPTION_MODE = "xsalsa20_poly1305";
}

/// <summary>
/// Discord Gateway用のJSONオブジェクト作成ヘルパー
/// </summary>
public static class DiscordPayloadHelper
{
    /// <summary>
    /// Identifyペイロードを作成
    /// </summary>
    public static object CreateIdentifyPayload(string token) => new {
        op = 2,
        d = new {
            token = token,
            intents = DiscordConstants.DISCORD_INTENTS,
            properties = new {
                os = DiscordConstants.DISCORD_OS,
                browser = DiscordConstants.DISCORD_BROWSER,
                device = DiscordConstants.DISCORD_DEVICE
            }
        }
    };

    /// <summary>
    /// ハートビートペイロードを作成
    /// </summary>
    public static object CreateHeartbeatPayload(int? sequence) => new {
        op = 1,
        d = sequence
    };

    /// <summary>
    /// ボイスチャンネル参加ペイロードを作成
    /// </summary>
    public static object CreateVoiceStateUpdatePayload(string guildId, string channelId) => new {
        op = 4,
        d = new {
            guild_id = guildId,
            channel_id = channelId,
            self_mute = true,
            self_deaf = false
        }
    };

    /// <summary>
    /// Voice Gateway用Identifyペイロードを作成
    /// </summary>
    public static object CreateVoiceIdentifyPayload(string guildId, string userId, string sessionId, string token) => new {
        op = 0,
        d = new {
            server_id = guildId,
            user_id = userId,
            session_id = sessionId,
            token = token
        }
    };

    /// <summary>
    /// プロトコル選択ペイロードを作成
    /// </summary>
    public static object CreateSelectProtocolPayload(string ip, int port, string mode) => new {
        op = 1,
        d = new {
            protocol = DiscordConstants.DISCORD_PROTOCOL,
            data = new {
                address = ip,
                port = port,
                mode = mode
            }
        }
    };

    /// <summary>
    /// Voice Gateway用ハートビートペイロードを作成
    /// </summary>
    public static object CreateVoiceHeartbeatPayload(long nonce, int? sequence) => new {
        op = 3,
        d = new {
            t = nonce,
            seq_ack = sequence
        }
    };
}

/// <summary>
/// エラーハンドリング用のヘルパークラス
/// </summary>
public static class ErrorHandler
{
    /// <summary>
    /// 非同期操作を安全に実行し、エラーをログに記録
    /// </summary>
    public static async Task<T> SafeExecuteAsync<T>(Func<Task<T>> operation, string context, Action<string> logCallback)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            logCallback($"❌ {context} error: {ex.Message}");
            return default(T);
        }
    }

    /// <summary>
    /// 非同期操作を安全に実行（戻り値なし）
    /// </summary>
    public static async Task SafeExecuteAsync(Func<Task> operation, string context, Action<string> logCallback)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            logCallback($"❌ {context} error: {ex.Message}");
        }
    }

    /// <summary>
    /// 同期操作を安全に実行
    /// </summary>
    public static T SafeExecute<T>(Func<T> operation, string context, Action<string> logCallback)
    {
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            logCallback($"❌ {context} error: {ex.Message}");
            return default(T);
        }
    }

    /// <summary>
    /// 同期操作を安全に実行（戻り値なし）
    /// </summary>
    public static void SafeExecute(Action operation, string context, Action<string> logCallback)
    {
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            logCallback($"❌ {context} error: {ex.Message}");
        }
    }
}

public class DiscordBotClient : MonoBehaviour, IDisposable {
    [Header("Debug Settings")]
    public bool enableDebugLogging = true;
    
    [Header("Discord Settings")]
    private string discordToken;
    private string guildId;
    private string voiceChannelId;
    private string targetUserId;
    private string inputName = "Discord";
    private string witaiToken;
    
    // Bot自身の情報
    private string botUserId;

    // イベント
    public delegate void VoiceRecognizedDelegate(string inputName, string recognizedText);
    public static event VoiceRecognizedDelegate OnVoiceRecognized;

    public delegate void DiscordLogDelegate(string logMessage);
    public static event DiscordLogDelegate OnDiscordLog;

    // 接続関連
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isConnected = false;
    private string _sessionId;
    private int _mainSequence = 0;
    private System.Timers.Timer _heartbeatTimer;
    private bool _heartbeatAcknowledged = true;

    // Voice Gateway関連
    private ClientWebSocket _voiceWebSocket;
    private UdpClient _voiceUdpClient;
    private bool _voiceConnected = false;
    private string _voiceToken;
    private string _voiceEndpoint;
    private string _voiceSessionId;
    private System.Timers.Timer _voiceHeartbeatTimer;
    private IPEndPoint _voiceServerEndpoint;
    private Dictionary<uint, string> _ssrcToUserMap = new Dictionary<uint, string>();
    private uint _ourSSRC;
    private byte[] _secretKey;
    
    // Discord.js状態管理
    private enum NetworkingState {
        OpeningWs,
        Identifying,
        UdpHandshaking,
        SelectingProtocol,
        Ready,
        Closed
    }
    
    // Discord.js状態管理
    private NetworkingState _networkingState = NetworkingState.OpeningWs;
    
    // Discord.js準拠の接続データ
    private string _encryptionMode;
    private string[] _availableModes;
    
    // Discord.js VoiceWebSocket.ts準拠のハートビート管理
    private long _lastHeartbeatAck = 0;
    private long _lastHeartbeatSend = 0;
    private int _missedHeartbeats = 0;
    private int _voiceSequence = -1;
    private int? _ping = null;
    
    // Discord.js VoiceUDPSocket.ts準拠のKeep Alive
    private System.Timers.Timer _keepAliveTimer;
    private uint _keepAliveCounter = 0;
    private const int KEEP_ALIVE_INTERVAL = DiscordConstants.UDP_SEND_TIMEOUT; // 5秒
    private const uint MAX_COUNTER_VALUE = uint.MaxValue;

    // 音声処理統計
    private static int _successfulDecryptions = 0;
    private static int _failedDecryptions = 0;
    private static int _opusSuccesses = 0;
    private static int _opusErrors = 0;

    // 音声処理関連
    private List<float> _audioBuffer = new List<float>();
    private IOpusDecoder _opusDecoder;
    private Queue<OpusPacket> _opusPacketQueue = new Queue<OpusPacket>();
    private HttpClient _httpClient;
    private bool _isTargetUserSpeaking = false;

    private struct OpusPacket {
        public byte[] data;
        public string userId;
    }

    /// <summary>
    /// Discord Gatewayへの再接続を試みます。
    /// 接続が失われた場合に呼び出されます。
    /// </summary>
    private async Task ReconnectAsync() {
        LogMessage("Attempting to reconnect...");
        StopBot();
        await Task.Delay(DiscordConstants.RECONNECT_DELAY);
        StartBot();
    }

    /// <summary>
    /// ログメッセージを生成し、イベントを発行します。
    /// Unityのメインスレッドで実行されるように保証されます。
    /// </summary>
    /// <param name="message">ログに記録するメッセージ。</param>
    private void LogMessage(string message) {
        string logMessage = $"[DiscordBot] {DateTime.Now:HH:mm:ss} :: {message}";
        
        if (UnityMainThreadDispatcher.Instance() != null) {
            UnityMainThreadDispatcher.Instance().Enqueue(() => {
                OnDiscordLog?.Invoke(logMessage);
            });
        } else {
            OnDiscordLog?.Invoke(logMessage);
        }
    }

    /// <summary>
    /// Unityのライフサイクルメソッド。
    /// オブジェクトの初期化時に呼び出され、Opusデコーダーを準備します。
    /// </summary>
    private void Awake() {
        InitializeOpusDecoder();
    }

    /// <summary>
    /// Unityのライフサイクルメソッド。
    /// オブジェクトが破棄される際に呼び出され、リソースをクリーンアップします。
    /// </summary>
    private void OnDestroy() {
        LogMessage("🗑️ DiscordBotClient being destroyed - performing cleanup");
        StopBot();
    }

    /// <summary>
    /// Opusデコーダーを初期化します。
    /// 48kHz、ステレオの音声をデコードするように設定されます。
    /// </summary>
    private void InitializeOpusDecoder() {
        ErrorHandler.SafeExecute(() => {
            _opusDecoder = OpusCodecFactory.CreateDecoder(DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.CHANNELS_STEREO);
            LogMessage("Opus decoder initialized");
        }, "Opus decoder initialization", LogMessage);
    }

    /// <summary>
    /// Unityのライフサイクルメソッド。
    /// フレームごとに呼び出され、Opusパケットキューを処理します。
    /// </summary>
    private void Update() {
        lock (_opusPacketQueue) {
            while (_opusPacketQueue.Count > 0) {
                var packet = _opusPacketQueue.Dequeue();
                ProcessOpusData(packet.data, packet.userId);
            }
        }
    }

    /// <summary>
    /// Discordボットを起動します。
    /// 設定を読み込み、Discord Gatewayへの接続を開始します。
    /// </summary>
    public async void StartBot() {
        await ErrorHandler.SafeExecuteAsync(async () => {
            LoadSettingsFromCentralManager();
            
            if (string.IsNullOrEmpty(discordToken)) {
                LogMessage("❌ Discord token is not set");
                return;
            }
            
            _cancellationTokenSource = new CancellationTokenSource();
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {witaiToken}");
            
            InitializeOpusDecoder();
            
            await ConnectToDiscord();
        }, "StartBot", LogMessage);
    }

    /// <summary>
    /// DiscordのメインGatewayにWebSocketで接続します。
    /// </summary>
    private async Task ConnectToDiscord() {
        await ErrorHandler.SafeExecuteAsync(async () => {
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), _cancellationTokenSource.Token);
            _isConnected = true;
            
            LogMessage("✅ Connected to Discord Gateway");
            
            _ = Task.Run(ReceiveMessages, _cancellationTokenSource.Token);
        }, "Discord connection", LogMessage);
    }

    /// <summary>
    /// Discord Voice Gatewayからのメッセージを受信し続けます。
    /// </summary>
    private async Task ReceiveVoiceMessages() {
        var buffer = new byte[DiscordConstants.WEBSOCKET_BUFFER_SIZE];
        var messageBuffer = new List<byte>();
        
        while (_voiceConnected && !_cancellationTokenSource.Token.IsCancellationRequested) {
            try {
                var result = await _voiceWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
                if (result.MessageType == WebSocketMessageType.Text) {
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    
                    if (result.EndOfMessage) {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        await ProcessVoiceMessage(message);
                    }
                } else if (result.MessageType == WebSocketMessageType.Close) {
                    _voiceConnected = false;
                    break;
                }
                    } catch (Exception ex) {
            if (_voiceConnected) {
                LogMessage($"❌ Voice message error: {ex.Message}");
            }
            break;
        }
        }
    }

    /// <summary>
    /// Voice Gatewayから受信した単一のメッセージペイロードを処理します。
    /// オペレーションコードに基づいて処理を分岐します。
    /// </summary>
    /// <param name="message">受信したJSON形式のメッセージ文字列。</param>
    private async Task ProcessVoiceMessage(string message) {
        try {
            var payload = JsonConvert.DeserializeObject<VoiceGatewayPayload>(message);
            
            // Discord.js VoiceWebSocket.ts準拠のシーケンス管理
            var jsonPayload = JObject.Parse(message);
            if (jsonPayload["seq"] != null) {
                _voiceSequence = jsonPayload["seq"].ToObject<int>();
            }
            
            switch (payload.op) {
                case 8: // Hello - Discord.js Networking.ts準拠
                    // Discord.js実装通り：OpeningWs → Identifying状態遷移
                    _networkingState = NetworkingState.Identifying;
                    
                    var helloData = JsonConvert.DeserializeObject<VoiceHelloData>(payload.d.ToString());
                    await StartVoiceHeartbeat(helloData.heartbeat_interval);
                    await SendVoiceIdentify();
                    break;
                    
                case 2: // Ready - Discord.js Networking.ts準拠
                    // Discord.js実装通り：Identifying → UdpHandshaking状態遷移
                    _networkingState = NetworkingState.Identifying;
                    
                    var readyData = JsonConvert.DeserializeObject<VoiceReadyData>(payload.d.ToString());
                    _ourSSRC = readyData.ssrc;
                    _voiceServerEndpoint = new IPEndPoint(IPAddress.Parse(readyData.ip), readyData.port);
                    _availableModes = readyData.modes; // Discord.js実装通り：暗号化モード保存
                    
                    LogMessage($"🎯 Voice Ready - BOT SSRC: {_ourSSRC}, Server: {readyData.ip}:{readyData.port}");
                    
                    await SetupUdpClient();
                    
                    bool discoverySuccess = await PerformUdpIpDiscovery();
                    
                    if (!discoverySuccess) {
                        LogMessage("⚠️ UDP IP Discovery failed, attempting fallback approach");
                        
                        // Discord.js実装通り：IP discoveryが失敗した場合のフォールバック
                        var localEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
                        string fallbackIP = GetLocalIPAddress();
                        
                        bool fallbackSuccess = await CompleteUdpDiscovery(fallbackIP, localEndpoint.Port);
                        
                        if (!fallbackSuccess) {
                            LogMessage("❌ WARNING: Both IP discovery and fallback failed. Voice may not work.");
                        }
                    }
                    break;
                    
                case 4: // Session Description - Discord.js Networking.ts準拠
                    // Discord.js実装通り：SelectingProtocol → Ready状態遷移
                    _networkingState = NetworkingState.Ready;
                    
                    var sessionData = JsonConvert.DeserializeObject<VoiceSessionDescriptionData>(payload.d.ToString());
                    _secretKey = sessionData.secret_key;
                    _encryptionMode = sessionData.mode; // Discord.js実装通り：暗号化モード保存
                    
                    LogMessage($"🔐 Encryption mode: {_encryptionMode}");
                    LogMessage($"🎯 Voice connection ready! (Discord.js style)");
                    
                    await StartUdpAudioReceive();
                    break;
                    
                case 3: // Heartbeat ACK - Discord.js VoiceWebSocket.ts準拠
                    // Discord.js VoiceWebSocket.ts準拠のハートビートACK処理
                    _lastHeartbeatAck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _missedHeartbeats = 0;
                    
                    if (_lastHeartbeatSend != 0) {
                        _ping = (int)(_lastHeartbeatAck - _lastHeartbeatSend);
                    }
                    break;
                    
                case 5: // Speaking
                    var speakingData = JsonConvert.DeserializeObject<VoiceSpeakingData>(payload.d.ToString());
                    if (speakingData.user_id != null) {
                        _ssrcToUserMap[speakingData.ssrc] = speakingData.user_id;
                    
                        if (speakingData.user_id == targetUserId) {
                            LogMessage($"🎯 Target user {(speakingData.speaking ? "started" : "stopped")} speaking (SSRC: {speakingData.ssrc})");
                            _isTargetUserSpeaking = speakingData.speaking;

                            if (!_isTargetUserSpeaking)
                            {
                                ProcessAudioBuffer(true);
                            }
                        }
                    }
                    break;
                    
                case 6: // Heartbeat (Discord.jsでは一般的に無視)
                case 11: // Voice State Update
                case 18: // Client Flags Update
                case 20: // Platform Update
                    // Discord.jsの実装を参考に、これらは静かに無視
                    break;
                    
                default:
                    LogMessage($"Unknown voice OP code: {payload.op}");
                    LogMessage($"Voice message data: {payload.d?.ToString() ?? "null"}");
                    break;
            }
        } catch (Exception ex) {
            LogMessage($"Voice message processing error: {ex.Message}");
            LogMessage($"Raw message: {message}");
        }
    }

    /// <summary>
    /// メインGatewayからのDispatchイベントを処理します。
    /// イベントタイプに応じて、セッション情報やVoice Server情報を更新します。
    /// </summary>
    /// <param name="eventType">イベントのタイプ (例: "READY")。</param>
    /// <param name="data">イベントに関連するデータ。</param>
    private async Task HandleDispatchEvent(string eventType, string data) {
        switch (eventType) {
            case "READY":
                var readyData = JsonConvert.DeserializeObject<ReadyData>(data);
                _sessionId = readyData.session_id;
                botUserId = readyData.user.id;
                LogMessage($"Bot logged in: {readyData.user.username}");
                
                if (!string.IsNullOrEmpty(voiceChannelId)) {
                    await JoinVoiceChannel();
                }
                break;
                
            case "VOICE_STATE_UPDATE":
                var voiceStateData = JsonConvert.DeserializeObject<VoiceStateData>(data);
                _voiceSessionId = voiceStateData.session_id;
                break;
                
            case "VOICE_SERVER_UPDATE":
                var voiceServerData = JsonConvert.DeserializeObject<VoiceServerData>(data);
                _voiceToken = voiceServerData.token;
                _voiceEndpoint = voiceServerData.endpoint;
                
                if (!string.IsNullOrEmpty(_voiceToken) && !string.IsNullOrEmpty(_voiceEndpoint) && !string.IsNullOrEmpty(_voiceSessionId)) {
                    _ = Task.Run(ConnectToVoiceGateway);
                }
                break;
        }
    }

    /// <summary>
    /// Discord Voice Gatewayに接続します。
    /// 既存の接続がある場合は一旦切断し、再接続します。
    /// </summary>
    private async Task ConnectToVoiceGateway() {
        await ErrorHandler.SafeExecuteAsync(async () => {
            _networkingState = NetworkingState.OpeningWs; // 初期状態を設定
            
            // 既存のVoice WebSocketがある場合はクローズ
            if (_voiceWebSocket != null) {
                if (_voiceWebSocket.State == WebSocketState.Open) {
                    await _voiceWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                }
                _voiceWebSocket?.Dispose();
                _voiceWebSocket = null;
            }
            
            _voiceWebSocket = new ClientWebSocket();
            var voiceGatewayUrl = $"wss://{_voiceEndpoint}/?v=4";
            
            await _voiceWebSocket.ConnectAsync(new Uri(voiceGatewayUrl), _cancellationTokenSource.Token);
            _voiceConnected = true;
            
            LogMessage("✅ Voice WebSocket connected successfully");
            
            _ = Task.Run(ReceiveVoiceMessages, _cancellationTokenSource.Token);
        }, "Voice connection", LogMessage);
        
        if (!_voiceConnected) {
            _voiceConnected = false;
        }
    }

    /// <summary>
    /// メインGatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendMessage(string message) {
        try {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open) {
                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            } else {
                LogMessage("❌ WebSocket is not connected");
            }
        } catch (Exception ex) {
            LogMessage($"❌ Send message error: {ex.Message}");
        }
    }

    /// <summary>
    /// Voice Gatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendVoiceMessage(string message) {
        try {
            if (_voiceWebSocket != null && _voiceWebSocket.State == WebSocketState.Open) {
                var buffer = Encoding.UTF8.GetBytes(message);
                await _voiceWebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            } else {
                LogMessage("❌ Voice WebSocket is not connected");
            }
        } catch (Exception ex) {
            LogMessage($"❌ Send voice message error: {ex.Message}");
        }
    }

    /// <summary>
    /// CentralManagerからDiscord関連の設定を読み込みます。
    /// </summary>
    private void LoadSettingsFromCentralManager() {
        if (CentralManager.Instance == null) return;

        discordToken = CentralManager.Instance.GetDiscordToken();
        guildId = CentralManager.Instance.GetDiscordGuildId();
        voiceChannelId = CentralManager.Instance.GetDiscordVoiceChannelId();
        targetUserId = CentralManager.Instance.GetDiscordTargetUserId();
        inputName = CentralManager.Instance.GetDiscordInputName();
        witaiToken = CentralManager.Instance.GetDiscordWitaiToken();

        if (string.IsNullOrEmpty(inputName)) {
            inputName = "Discord";
        }
    }

    /// <summary>
    /// ボットを停止し、すべての接続とリソースをクリーンアップします。
    /// </summary>
    public void StopBot() {
        LogMessage("🛑 Starting bot shutdown process...");
        _isConnected = false;
        _voiceConnected = false;
        lock (_audioBuffer) _audioBuffer.Clear();
        lock (_opusPacketQueue) _opusPacketQueue.Clear();
        DisposeResources();
        // Discord.js準拠の状態リセット
        _networkingState = NetworkingState.Closed;
        _lastHeartbeatAck = 0;
        _lastHeartbeatSend = 0;
        _missedHeartbeats = 0;
        _voiceSequence = -1;
        _ping = null;
        _keepAliveCounter = 0;
        _successfulDecryptions = 0;
        _failedDecryptions = 0;
        _opusSuccesses = 0;
        _opusErrors = 0;
        LogMessage("✅ Bot shutdown completed - all resources cleaned up");
    }

    /// <summary>
    /// メインGatewayからのメッセージを受信し続けます。
    /// </summary>
    private async Task ReceiveMessages() {
        var buffer = new byte[DiscordConstants.WEBSOCKET_BUFFER_SIZE];
        var messageBuilder = new StringBuilder();
        
        while (_isConnected && !_cancellationTokenSource.Token.IsCancellationRequested) {
            try {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
                if (result.MessageType == WebSocketMessageType.Text) {
                    var messageChunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(messageChunk);
                    
                    if (result.EndOfMessage) {
                        var message = messageBuilder.ToString();
                        messageBuilder.Clear();
                        
                        UnityMainThreadDispatcher.Instance().Enqueue(() => {
                            _ = ProcessDiscordMessage(message);
                        });
                    }
                } else if (result.MessageType == WebSocketMessageType.Close) {
                    _isConnected = false;
                    break;
                }
                    } catch (Exception ex) {
            if (_isConnected) {
                LogMessage($"❌ Message receive error: {ex.Message}");
            }
            break;
        }
        }
    }

    /// <summary>
    /// メインGatewayから受信した単一のメッセージペイロードを処理します。
    /// </summary>
    /// <param name="message">受信したJSON形式のメッセージ文字列。</param>
    private async Task ProcessDiscordMessage(string message) {
        try {
            
            var payload = JsonConvert.DeserializeObject<DiscordGatewayPayload>(message);
            if (payload == null) return;
            
            if (payload.s.HasValue) {
                _mainSequence = payload.s.Value;
            }

            switch (payload.op) {
                case 10: // Hello
                    var helloData = JsonConvert.DeserializeObject<HelloData>(payload.d.ToString());
                    await StartHeartbeat(helloData.heartbeat_interval);
                    await SendIdentify();
                    break;
                    
                case 11: // Heartbeat ACK
                    _heartbeatAcknowledged = true;
                    break;
                    
                case 0: // Dispatch
                    await HandleDispatchEvent(payload.t, payload.d.ToString());
                    break;
            }
        } catch (Exception ex) {
            LogMessage($"❌ Message processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// メインGatewayへのハートビート送信を定期的に開始します。
    /// </summary>
    /// <param name="interval">ハートビートの間隔（ミリ秒）。</param>
    private async Task StartHeartbeat(int interval) {
        _heartbeatTimer = new System.Timers.Timer(interval);
        _heartbeatTimer.Elapsed += async (sender, e) => {
            if (!_heartbeatAcknowledged) {
                await ReconnectAsync();
            } else {
                _heartbeatAcknowledged = false;
                await SendHeartbeat();
            }
        };
        _heartbeatTimer.Start();
    }

    /// <summary>
    /// メインGatewayにIdentifyペイロードを送信し、セッションを確立します。
    /// </summary>
    private async Task SendIdentify() {
        var identify = DiscordPayloadHelper.CreateIdentifyPayload(discordToken);
        await SendMessage(JsonConvert.SerializeObject(identify));
    }

    /// <summary>
    /// メインGatewayにハートビートを送信します。
    /// </summary>
    private async Task SendHeartbeat() {
        var heartbeat = DiscordPayloadHelper.CreateHeartbeatPayload(_mainSequence);
        await SendMessage(JsonConvert.SerializeObject(heartbeat));
    }

    /// <summary>
    /// 指定されたボイスチャンネルに参加するためのリクエストを送信します。
    /// </summary>
    private async Task JoinVoiceChannel() {
        var voiceStateUpdate = DiscordPayloadHelper.CreateVoiceStateUpdatePayload(guildId, voiceChannelId);
        await SendMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }

    /// <summary>
    /// Voice GatewayにIdentifyペイロードを送信し、音声セッションを確立します。
    /// </summary>
    private async Task SendVoiceIdentify() {
        var identify = DiscordPayloadHelper.CreateVoiceIdentifyPayload(guildId, botUserId, _voiceSessionId, _voiceToken);
        await SendVoiceMessage(JsonConvert.SerializeObject(identify));
    }

    /// <summary>
    /// UDPクライアントをセットアップします。
    /// バッファサイズやタイムアウトなどのソケットオプションを設定します。
    /// </summary>
    private async Task SetupUdpClient() {
        await ErrorHandler.SafeExecuteAsync(async () => {
            _voiceUdpClient?.Close();
            _voiceUdpClient?.Dispose();
            
            // Discord.jsの実装を参考に、UDPクライアントを作成（バインドは後で行う）
            _voiceUdpClient = new UdpClient();
            _voiceUdpClient.Client.ReceiveBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            _voiceUdpClient.Client.SendBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            
            // UDPソケットの設定を最適化
            _voiceUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _voiceUdpClient.Client.ReceiveTimeout = 0; // ノンブロッキング
            _voiceUdpClient.Client.SendTimeout = DiscordConstants.UDP_SEND_TIMEOUT;
            
            LogMessage("UDP client set up successfully");
        }, "UDP setup", LogMessage);
    }

    /// <summary>
    /// ローカルのIPアドレスを取得します。
    /// 外部への接続を試みる方法と、ネットワークインターフェースから取得する方法をフォールバックとして使用します。
    /// </summary>
    /// <returns>検出されたローカルIPアドレスの文字列。</returns>
    private string GetLocalIPAddress() {
        return ErrorHandler.SafeExecute(() => {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0)) {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                string ip = endPoint?.Address.ToString() ?? "192.168.1.1";
                
                // VPN環境の診断情報を追加
                if (ip.StartsWith("172.") || ip.StartsWith("10.") || ip.StartsWith("192.168.")) {
                    LogMessage($"Detected private IP address: {ip} (may be behind NAT/VPN)");
                } else {
                    LogMessage($"Detected public IP address: {ip}");
                }
                
                return ip;
            }
        }, "Primary IP detection", LogMessage) ?? ErrorHandler.SafeExecute(() => {
            // フォールバック: ネットワークインターフェースから取得
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) {
                    LogMessage($"Fallback IP detected: {ip}");
                    return ip.ToString();
                }
            }
            return null;
        }, "Fallback IP detection", LogMessage) ?? "192.168.1.1";
    }

    /// <summary>
    /// DiscordのVoice Serverに対してUDP IP Discoveryを実行し、
    /// 外部から見た自身のIPアドレスとポートを取得します。
    /// </summary>
    /// <returns>IP Discoveryが成功した場合はtrue、それ以外はfalse。</returns>
    private async Task<bool> PerformUdpIpDiscovery() {
        try {
            _networkingState = NetworkingState.UdpHandshaking; // 状態遷移を記録
            
            // UDPクライアントを任意のポートにバインド
            _voiceUdpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var boundEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
            
            // Discord.js VoiceUDPSocket.ts完全準拠の74バイトパケット
            var discoveryBuffer = new byte[DiscordConstants.UDP_DISCOVERY_PACKET_SIZE];
            
            // writeUInt16BE(1, 0) - Type: 1
            discoveryBuffer[0] = 0x00;
            discoveryBuffer[1] = 0x01;
            
            // writeUInt16BE(70, 2) - Length: 70
            discoveryBuffer[2] = 0x00;
            discoveryBuffer[3] = 0x46;
            
            // writeUInt32BE(ssrc, 4) - SSRC (Big Endian)
            var ssrcBytes = BitConverter.GetBytes(_ourSSRC);
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(ssrcBytes);
            }
            Array.Copy(ssrcBytes, 0, discoveryBuffer, 4, 4);
            
            // パケット送信
            await _voiceUdpClient.SendAsync(discoveryBuffer, discoveryBuffer.Length, _voiceServerEndpoint);
            
            // Discord.js VoiceUDPSocket.ts準拠の応答待機
            var receiveTask = _voiceUdpClient.ReceiveAsync();
            var timeoutTask = Task.Delay(DiscordConstants.UDP_DISCOVERY_TIMEOUT);
            
            var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
            
            if (completedTask == receiveTask) {
                var result = await receiveTask;
                var message = result.Buffer;
                
                if (message.Length >= DiscordConstants.UDP_DISCOVERY_PACKET_SIZE) {
                    // Discord.js VoiceUDPSocket.ts準拠の応答解析
                    var localConfig = ParseLocalPacket(message);
                    
                    if (localConfig != null) {
                        return await CompleteUdpDiscovery(localConfig.ip, localConfig.port);
                    }
                } else {
                    LogMessage($"❌ Discovery response too short: {message.Length} bytes (expected {DiscordConstants.UDP_DISCOVERY_PACKET_SIZE})");
                }
            } else {
                LogMessage($"❌ Discovery timeout after {DiscordConstants.UDP_DISCOVERY_TIMEOUT}ms");
            }
            
            // Discord.js フォールバック実装
            LogMessage("🔄 Using Discord.js fallback approach");
            return await UseDiscordJsFallback();
            
        } catch (Exception ex) {
            LogMessage($"❌ UDP discovery error: {ex.Message}");
            LogMessage($"Stack trace: {ex.StackTrace}");
            
            return await UseDiscordJsFallback();
        }
    }

    /// <summary>
    /// 受信したRTPパケットを処理します。
    /// SSRCからユーザーを特定し、暗号化された音声データを復号してOpusパケットキューに追加します。
    /// </summary>
    /// <param name="packet">受信したRTPパケットのバイト配列。</param>
    private async Task ProcessRtpPacket(byte[] packet) {
        try {
            var ssrcBytes = new byte[4];
            Array.Copy(packet, 8, ssrcBytes, 0, 4);
            
            if (BitConverter.IsLittleEndian) {
                Array.Reverse(ssrcBytes);
            }
            var ssrc = BitConverter.ToUInt32(ssrcBytes, 0);
            
            // BOT自身のSSRCかチェック
            if (ssrc == _ourSSRC) {
                return; // BOT自身のパケットは静かに無視
            }
            
            if (_ssrcToUserMap.TryGetValue(ssrc, out string userId)) {
                var rtpHeader = new byte[DiscordConstants.RTP_HEADER_SIZE];
                Array.Copy(packet, 0, rtpHeader, 0, DiscordConstants.RTP_HEADER_SIZE);
                
                var encryptedData = new byte[packet.Length - DiscordConstants.RTP_HEADER_SIZE];
                Array.Copy(packet, DiscordConstants.RTP_HEADER_SIZE, encryptedData, 0, encryptedData.Length);
                
                if (encryptedData.Length >= DiscordConstants.MIN_ENCRYPTED_DATA_SIZE && _secretKey != null) {
                    try {
                        byte[] decryptedOpusData = DiscordCrypto.DecryptVoicePacket(encryptedData, rtpHeader, _secretKey, _encryptionMode);
                
                        if (decryptedOpusData != null) {
                            _successfulDecryptions++;
                            
                            // Discordヘッダーをスキップして純粋なOpusデータを抽出
                            byte[] actualOpusData = ExtractOpusFromDiscordPacket(decryptedOpusData);
                            if (actualOpusData == null) {
                                LogMessage($"⚠️ Failed to extract Opus data from Discord packet");
                                return;
                            }
                            
                            var opusDataCopy = new byte[actualOpusData.Length];
                            Array.Copy(actualOpusData, opusDataCopy, actualOpusData.Length);
                
                            lock (_opusPacketQueue) {
                                _opusPacketQueue.Enqueue(new OpusPacket { 
                                    data = opusDataCopy, 
                                    userId = userId 
                                });
                            }
                        } else {
                            _failedDecryptions++;
                            LogMessage($"❌ Decryption failed ({_failedDecryptions} total failures)");
                        }
                    } catch (Exception decryptEx) {
                        _failedDecryptions++;
                        LogMessage($"❌ Decryption error: {decryptEx.Message}");
                    }
                } else {
                    LogMessage($"⚠️ Skipping packet - encrypted data too small ({encryptedData.Length}) or no secret key");
                }
            } else {
                LogMessage($"⚠️ No user found for SSRC {ssrc} (available: {string.Join(", ", _ssrcToUserMap.Keys)})");
            }
        } catch (Exception ex) {
            LogMessage($"❌ RTP processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Discordの音声パケットから純粋なOpusデータを抽出します。
    /// Discord独自のヘッダーを取り除きます。
    /// </summary>
    /// <param name="discordPacket">Discordから受信した音声パケット。</param>
    /// <returns>抽出されたOpusデータ。抽出に失敗した場合はnull。</returns>
    private byte[] ExtractOpusFromDiscordPacket(byte[] discordPacket) {
        try {
            if (discordPacket == null || discordPacket.Length < DiscordConstants.DISCORD_HEADER_SIZE) {
                return null;
            }
            
            // Discord音声パケットの構造解析
            // BE-DE で始まるDiscord独自ヘッダーをスキップ
            if (discordPacket.Length >= 2 && discordPacket[0] == 0xBE && discordPacket[1] == 0xDE) {
                // Discord拡張ヘッダーは12バイト固定
                
                if (discordPacket.Length <= DiscordConstants.DISCORD_HEADER_SIZE) {
                    LogMessage($"⚠️ Discord packet too small: {discordPacket.Length} bytes");
                    return null;
                }
                
                // Opusデータ部分を抽出（12バイト後から）
                int opusDataSize = discordPacket.Length - DiscordConstants.DISCORD_HEADER_SIZE;
                byte[] opusData = new byte[opusDataSize];
                Array.Copy(discordPacket, DiscordConstants.DISCORD_HEADER_SIZE, opusData, 0, opusDataSize);
                
                return opusData;
            }
            
            // BE-DEヘッダーがない場合、そのままOpusデータとして扱う
            return discordPacket;
            
        } catch (Exception ex) {
            LogMessage($"❌ Discord packet extraction error: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Opus音声データをデコードし、処理可能なPCM形式に変換します。
    /// 対象ユーザーの音声のみを処理し、オーディオバッファに追加します。
    /// </summary>
    /// <param name="opusData">デコードするOpusデータのバイト配列。</param>
    /// <param name="userId">音声の送信元ユーザーID。</param>
    private void ProcessOpusData(byte[] opusData, string userId) {
        try {
            if (_opusDecoder == null) {
                LogMessage("❌ Opus decoder is null");
                return;
            }
            
            if (userId != targetUserId) {
                return; // 対象外ユーザーは静かにスキップ
            }
            
            // Opusデータの最小サイズチェック
            if (opusData.Length < 1) {
                _opusErrors++;
                if (_opusErrors <= 3 || _opusErrors % 10 == 0) {
                    LogMessage($"❌ Opus data too small: {opusData.Length} bytes ({_opusErrors} errors)");
                }
                return;
            }
            
            short[] pcmData = new short[DiscordConstants.OPUS_FRAME_SIZE * DiscordConstants.CHANNELS_STEREO];
            int decodedSamples = _opusDecoder.Decode(opusData, pcmData, DiscordConstants.OPUS_FRAME_SIZE, false);
            
            if (decodedSamples > 0) {
                _opusSuccesses++;
                
                short[] actualPcmData = new short[decodedSamples * DiscordConstants.CHANNELS_STEREO];
                Array.Copy(pcmData, actualPcmData, decodedSamples * DiscordConstants.CHANNELS_STEREO);
                
                short[] monoData = ConvertStereoToMono(actualPcmData, decodedSamples * DiscordConstants.CHANNELS_STEREO);
                float[] resampledData = ConvertToFloatAndResample(monoData, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);
                
                lock (_audioBuffer) {
                    _audioBuffer.AddRange(resampledData);
                }
                ProcessAudioBuffer(false);

            } else {
                _opusErrors++;
                if (_opusErrors <= 3 || _opusErrors % 10 == 0) {
                    LogMessage($"❌ Opus decode failed: {decodedSamples} samples ({_opusErrors} errors)");
                }
            }
        } catch (Exception ex) {
            _opusErrors++;
            // "corrupted stream" や "buffer too small" エラーは最初の3回と10回に1回だけログ出力
            if (_opusErrors <= 3 || _opusErrors % 10 == 0) {
                LogMessage($"❌ Opus error: {ex.Message} ({_opusErrors} total errors)");
            }
            
            // 深刻なエラーの場合はOpusデコーダーをリセット
            if (ex.Message.Contains("corrupted") && _opusErrors % 50 == 0) {
                try {
                    _opusDecoder?.Dispose();
                    InitializeOpusDecoder();
                    LogMessage("🔄 Opus decoder reset due to persistent errors");
                } catch (Exception resetEx) {
                    LogMessage($"❌ Opus decoder reset failed: {resetEx.Message}");
                }
            }
        }
    }

    /// <summary>
    /// ステレオPCMデータをモノラルに変換します。
    /// </summary>
    /// <param name="stereoData">ステレオPCMデータ。</param>
    /// <param name="totalSamples">合計サンプル数。</param>
    /// <returns>モノラルに変換されたPCMデータ。</returns>
    private short[] ConvertStereoToMono(short[] stereoData, int totalSamples) {
        short[] monoData = new short[totalSamples / 2];
        for (int i = 0; i < monoData.Length; i++) {
            monoData[i] = stereoData[i * 2];
        }
        return monoData;
    }

    /// <summary>
    /// short形式のPCMデータをfloat形式に変換し、リサンプリングします。
    /// 48kHzから16kHzへのリサンプリングを簡易的に行います。
    /// </summary>
    /// <param name="shortData">変換元のshort配列。</param>
    /// <param name="fromSampleRate">変換元のサンプルレート。</param>
    /// <param name="toSampleRate">変換先のサンプルレート。</param>
    /// <returns>変換後のfloat配列。</returns>
    private float[] ConvertToFloatAndResample(short[] shortData, int fromSampleRate, int toSampleRate) {
        if (fromSampleRate == DiscordConstants.SAMPLE_RATE_48K && toSampleRate == DiscordConstants.SAMPLE_RATE_16K) {
            float[] resampledData = new float[shortData.Length / 3];
            for (int i = 0; i < resampledData.Length; i++) {
                resampledData[i] = shortData[i * 3] / DiscordConstants.PCM_SCALE_FACTOR;
            }
            return resampledData;
        } else {
            float[] floatData = new float[shortData.Length];
            for (int i = 0; i < shortData.Length; i++) {
                floatData[i] = shortData[i] / DiscordConstants.PCM_SCALE_FACTOR;
            }
            return floatData;
        }
    }

    /// <summary>
    /// 音声データを非同期で処理するためのコルーチン。
    /// バックグラウンドで文字起こしを実行し、結果をメインスレッドで処理します。
    /// </summary>
    /// <param name="audioData">処理対象の音声データ。</param>
    private IEnumerator ProcessAudioCoroutine(float[] audioData) {
        string recognizedText = "";
        bool completed = false;
        Exception error = null;

        Task.Run(async () => {
            try {
                // CancellationTokenをチェック
                if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
                    LogMessage("🛑 Audio processing cancelled before start");
                    return;
                }
                recognizedText = await TranscribeWithWitAI(audioData);
            } catch (OperationCanceledException) {
                // キャンセルされた場合は静かに終了
                LogMessage("🛑 Audio processing cancelled during transcription");
                return;
            } catch (Exception ex) {
                error = ex;
                LogMessage($"❌ Audio processing error: {ex.Message}");
            } finally {
                completed = true;
            }
        });

        while (!completed) {
            // CancellationTokenをチェック
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
                LogMessage("🛑 Audio processing cancelled during wait");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
        }

        // キャンセルされた場合は処理をスキップ
        if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
            LogMessage("🛑 Audio processing cancelled before final processing");
            yield break;
        }

        if (error != null) {
            LogMessage($"❌ Speech recognition error: {error.Message}");
        } else if (!string.IsNullOrEmpty(recognizedText)) {
            LogMessage($"🎯 Recognized: {recognizedText}");
            OnVoiceRecognized?.Invoke(inputName, recognizedText);
        } else {
            LogMessage("🤔 No speech recognized");
        }
    }

    /// <summary>
    /// Wit.AI APIを使用して音声データを文字に変換します。
    /// </summary>
    /// <param name="audioData">文字起こしするfloat形式の音声データ。</param>
    /// <returns>認識されたテキスト文字列。</returns>
    private async Task<string> TranscribeWithWitAI(float[] audioData) {
        try {
            // CancellationTokenをチェック
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
                return "";
            }

            if (_httpClient == null || string.IsNullOrEmpty(witaiToken))
            {
                LogMessage("❌ HttpClient is not initialized or witaiToken is missing.");
                return "";
            }

            // Node.js準拠: 生のPCMデータに変換（48kHz → 16kHz）
            byte[] rawPcmData = ConvertToRawPcm(audioData, DiscordConstants.WITA_API_SAMPLE_RATE, DiscordConstants.WITA_API_CHANNELS);
            
            using (var content = new ByteArrayContent(rawPcmData))
            {
                // Node.js準拠のContent-Type
                content.Headers.Add("Content-Type", "audio/raw;encoding=signed-integer;bits=16;rate=16k;endian=little");
                
                // CancellationTokenを使用してHTTPリクエストをキャンセル可能にする
                var response = await _httpClient.PostAsync("https://api.wit.ai/speech", content, _cancellationTokenSource?.Token ?? CancellationToken.None);
                
                if (response.IsSuccessStatusCode) {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    
                    // Node.js準拠: 複数のJSONオブジェクトを配列化
                    if (!string.IsNullOrWhiteSpace(jsonResponse)) {
                        try {
                            // Node.js: output.replace(/}\s*{/g, '},{')}
                            string jsonArrayString = $"[{jsonResponse.Replace("}\r\n{", "},{").Replace("}\n{", "},{").Replace("} {", "},{")}]";
                            var dataArray = JsonConvert.DeserializeObject<WitAIResponse[]>(jsonArrayString);
                            
                            // Node.js準拠: type === "FINAL_UNDERSTANDING"をフィルタリング
                            var finalUnderstanding = dataArray?.FirstOrDefault(item => item.type == "FINAL_UNDERSTANDING");
                            if (finalUnderstanding != null && !string.IsNullOrEmpty(finalUnderstanding.text)) {
                                return finalUnderstanding.text;
                            }
                            
                            // フォールバック: 最初のテキストを使用
                            var firstText = dataArray?.FirstOrDefault(item => !string.IsNullOrEmpty(item.text));
                            if (firstText != null) {
                                return firstText.text;
                            }
                            
                        } catch (JsonException) {
                            // 単一のJSONオブジェクトの場合のフォールバック
                            try {
                                var witResponse = JsonConvert.DeserializeObject<WitAIResponse>(jsonResponse);
                                if (!string.IsNullOrEmpty(witResponse?.text)) {
                                    return witResponse.text;
                                }
                            } catch (JsonException) {
                                LogMessage($"Wit.AI JSON parse error. Raw response: {jsonResponse}");
                            }
                        }
                    }
                    
                    LogMessage($"Wit.AI no text found. Response: {jsonResponse}");
                } else {
                    LogMessage($"Wit.AI HTTP error: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
        } catch (OperationCanceledException) {
            // キャンセルされた場合は静かに終了
            return "";
        } catch (Exception ex) {
            LogMessage($"Wit.AI error: {ex.Message}");
        }
        return "";
    }

    /// <summary>
    /// float形式の音声データを生のPCMデータ（16-bit little-endian）に変換します。
    /// </summary>
    /// <param name="audioData">変換元の音声データ。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="channels">チャンネル数。</param>
    /// <returns>変換後のPCMバイト配列。</returns>
    private byte[] ConvertToRawPcm(float[] audioData, int sampleRate, int channels) {
        short[] pcmData = new short[audioData.Length];
        for (int i = 0; i < audioData.Length; i++) {
            float sample = Mathf.Clamp(audioData[i], -1.0f, 1.0f);
            pcmData[i] = (short)(sample * 32767);
        }

        List<byte> rawData = new List<byte>();
        foreach (short sample in pcmData) {
            rawData.AddRange(BitConverter.GetBytes(sample));
        }

        return rawData.ToArray();
    }

    /// <summary>
    /// オーディオバッファを処理し、十分なデータが溜まった場合や強制フラグが立った場合に音声認識を開始します。
    /// </summary>
    /// <param name="force">trueの場合、バッファサイズに関わらず処理を強制します。</param>
    private void ProcessAudioBuffer(bool force)
    {
        lock (_audioBuffer)
        {
            // 2秒以上のデータがある場合、または強制的に処理する場合（かつデータが少しでもある場合）
            if (_audioBuffer.Count >= DiscordConstants.AUDIO_BUFFER_THRESHOLD || (force && _audioBuffer.Count > DiscordConstants.AUDIO_BUFFER_MIN_SIZE)) // 0.1秒以上
            {
                float[] audioData = _audioBuffer.ToArray();
                _audioBuffer.Clear();
                
                StartCoroutine(ProcessAudioCoroutine(audioData));
            }
            else if (force && _audioBuffer.Count > 0)
            {
                // 強制処理の場合、少量のデータでも処理
                float[] audioData = _audioBuffer.ToArray();
                _audioBuffer.Clear();
                
                StartCoroutine(ProcessAudioCoroutine(audioData));
            }
        }
    }

    // Data structures - 統合版
    [Serializable]
    public class DiscordGatewayPayload {
        public int op;
        public object d;
        public int? s;
        public string t;
    }

    [Serializable]
    public class VoiceGatewayPayload {
        public int op;
        public object d;
    }

    [Serializable]
    public class DiscordUser {
        public string id;
        public string username;
        public string discriminator;
    }

    // Discord Gateway Data Structures
    [Serializable] public class HelloData { public int heartbeat_interval; }
    [Serializable] public class ReadyData { public string session_id; public DiscordUser user; }
    [Serializable] public class VoiceServerData { public string endpoint; public string token; }
    [Serializable] public class VoiceStateData { public string user_id; public string session_id; }

    // Voice Gateway Data Structures
    [Serializable] public class VoiceReadyData { public uint ssrc; public string ip; public int port; public string[] modes; }
    [Serializable] public class VoiceSpeakingData { public bool speaking; public uint ssrc; public string user_id; }
    [Serializable] public class VoiceHelloData { public double heartbeat_interval; }
    [Serializable] public class VoiceSessionDescriptionData { public byte[] secret_key; public string mode; }

    // External API Data Structures
    [Serializable] public class WitAIResponse { public string text; public string type; }

    // Discord.js準拠の暗号化モード（XSalsa20対応のため古いモードを優先）
    // 暗号化モードはDiscordConstantsクラスで管理

    /// <summary>
    /// IP Discoveryに失敗した場合のフォールバック処理。
    /// ローカルIPアドレスを使用してUDP接続を試みます。
    /// </summary>
    private async Task<bool> UseDiscordJsFallback() {
        var result = await ErrorHandler.SafeExecuteAsync(async () => {
            LogMessage("📋 Using Discord.js fallback approach...");
            
            // Discord.js フォールバック: ローカルエンドポイントを使用
            var localEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
            string fallbackIP = GetLocalIPAddress();
            
            LogMessage($"⚠️ Using bound endpoint: {fallbackIP}:{localEndpoint.Port}");
            
            return await CompleteUdpDiscovery(fallbackIP, localEndpoint.Port);
        }, "Discord.js fallback", LogMessage);
        
        return result;
    }

    /// <summary>
    /// UDPのIP Discoveryを完了し、選択した暗号化プロトコルをサーバーに通知します。
    /// </summary>
    /// <param name="detectedIP">検出されたIPアドレス。</param>
    /// <param name="detectedPort">検出されたポート番号。</param>
    /// <returns>成功した場合はtrue、それ以外はfalse。</returns>
    private async Task<bool> CompleteUdpDiscovery(string detectedIP, int detectedPort) {
        var result = await ErrorHandler.SafeExecuteAsync(async () => {
            LogMessage($"🔄 Completing UDP discovery with IP: {detectedIP}, Port: {detectedPort}");
            
            // Discord.js Networking.ts準拠の状態遷移
            _networkingState = NetworkingState.SelectingProtocol;
            
            // Discord.js実装通りの暗号化モード選択
            string selectedMode = ChooseEncryptionMode(_availableModes);
            
            var selectProtocolData = DiscordPayloadHelper.CreateSelectProtocolPayload(detectedIP, detectedPort, selectedMode);
            var jsonData = JsonConvert.SerializeObject(selectProtocolData);
            
            if (_voiceWebSocket == null) {
                LogMessage("❌ Voice WebSocket is null!");
                return false;
            }
            
            if (_voiceWebSocket.State != WebSocketState.Open) {
                LogMessage($"❌ Voice WebSocket state: {_voiceWebSocket.State}");
                return false;
            }
            
            await _voiceWebSocket.SendAsync(
                Encoding.UTF8.GetBytes(jsonData), 
                WebSocketMessageType.Text, true, CancellationToken.None);
            
            return true;
        }, "UDP discovery completion", LogMessage);
        
        return result;
    }
    
    /// <summary>
    /// 利用可能な暗号化モードの中から、サポートされているものを選択します。
    /// </summary>
    /// <param name="availableModes">サーバーから提供された利用可能なモードの配列。</param>
    /// <returns>選択された暗号化モードの文字列。</returns>
    private string ChooseEncryptionMode(string[] availableModes) {
        if (availableModes == null) {
            LogMessage("⚠️ No encryption modes available, using default");
            return "xsalsa20_poly1305";
        }
        
        foreach (var supportedMode in DiscordConstants.SUPPORTED_ENCRYPTION_MODES) {
            if (availableModes.Contains(supportedMode)) {
                LogMessage($"🔐 Selected encryption mode: {supportedMode} (Discord.js preferred)");
                return supportedMode;
            }
        }
        
        // フォールバック：利用可能なモードの最初のもの
        var fallbackMode = availableModes.Length > 0 ? availableModes[0] : DiscordConstants.DEFAULT_ENCRYPTION_MODE;
        LogMessage($"⚠️ Using fallback encryption mode: {fallbackMode}");
        return fallbackMode;
    }

    /// <summary>
    /// UDPによる音声データ受信を開始します。
    /// </summary>
    private async Task StartUdpAudioReceive() {
        try {
            // Discord.js VoiceUDPSocket.ts準拠の実装
            await SetupUdpClientForAudio();
            
            // Discord.js VoiceUDPSocket.ts準拠のKeep Alive開始
            StartKeepAlive();
            
            _ = Task.Run(ReceiveUdpAudio);
        } catch (Exception ex) {
            LogMessage($"❌ UDP audio receive start error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// UDP接続を維持するためのKeep-Aliveパケット送信を定期的に開始します。
    /// </summary>
    private void StartKeepAlive() {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = new System.Timers.Timer(KEEP_ALIVE_INTERVAL);
        _keepAliveTimer.Elapsed += async (sender, e) => await SendKeepAlive();
        _keepAliveTimer.Start();
        
        // Discord.js VoiceUDPSocket.ts準拠：即座に最初のKeep Aliveを送信
        _ = Task.Run(SendKeepAlive);
    }
    
    /// <summary>
    /// Keep-AliveパケットをVoice Serverに送信します。
    /// </summary>
    private async Task SendKeepAlive() {
        try {
            if (_voiceUdpClient == null || _voiceServerEndpoint == null) {
                return;
            }
            
            // Discord.js VoiceUDPSocket.ts準拠：8バイトKeep Aliveバッファ
            var keepAliveBuffer = new byte[8];
            
            // カウンターを書き込み（Little Endian）
            var counterBytes = BitConverter.GetBytes(_keepAliveCounter);
            Array.Copy(counterBytes, 0, keepAliveBuffer, 0, 4);
            
            await _voiceUdpClient.SendAsync(keepAliveBuffer, keepAliveBuffer.Length, _voiceServerEndpoint);
            
            // Discord.js VoiceUDPSocket.ts準拠：カウンター増加とオーバーフロー処理
            _keepAliveCounter++;
            if (_keepAliveCounter > MAX_COUNTER_VALUE) {
                _keepAliveCounter = 0;
            }
            
        } catch (Exception ex) {
            LogMessage($"❌ Keep alive error: {ex.Message}");
        }
    }

    /// <summary>
    /// 音声受信用にUDPクライアントをセットアップします。
    /// </summary>
    private async Task SetupUdpClientForAudio() {
        await ErrorHandler.SafeExecuteAsync(async () => {
            // 既存のUDPクライアントがある場合は適切に処理
            if (_voiceUdpClient != null) {
                return;
            }
            
            // 新しいUDPクライアントを作成（Discord.jsパターンを参考）
            _voiceUdpClient = new UdpClient();
            _voiceUdpClient.Client.ReceiveBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            _voiceUdpClient.Client.SendBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            
            // Discord.jsの推奨設定を適用
            _voiceUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _voiceUdpClient.Client.ReceiveTimeout = 0; // ノンブロッキング
            _voiceUdpClient.Client.SendTimeout = DiscordConstants.UDP_SEND_TIMEOUT;
        }, "UDP audio client setup", LogMessage);
    }

    /// <summary>
    /// UDP経由で音声データを受信し続けるループ。
    /// </summary>
    private async Task ReceiveUdpAudio() {
        int packetCount = 0;
        int timeoutCount = 0;
        
        while (_voiceConnected && _voiceUdpClient != null && !_cancellationTokenSource.Token.IsCancellationRequested) {
            try {
                var receiveTask = _voiceUdpClient.ReceiveAsync();
                var timeoutTask = Task.Delay(DiscordConstants.UDP_RECEIVE_TIMEOUT);
                
                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                
                if (completedTask == receiveTask) {
                    var result = await receiveTask;
                    var packet = result.Buffer;
                    packetCount++;
                    timeoutCount = 0; // リセット
                    
                    if (packet.Length >= DiscordConstants.RTP_HEADER_SIZE) {
                        // 音声パケットは通常60バイト以上
                        if (packet.Length >= DiscordConstants.MIN_AUDIO_PACKET_SIZE) {
                            await ProcessRtpPacket(packet);
                        }
                    } else {
                        LogMessage($"⚠️ Packet too small #{packetCount}: {packet.Length} bytes");
                    }
                } else {
                    timeoutCount++;
                    
                    // 30秒経過してもパケットが受信されない場合、再接続を試行
                    if (packetCount == 0 && timeoutCount >= DiscordConstants.UDP_PACKET_TIMEOUT) {
                        LogMessage($"⚠️ No packets received for {DiscordConstants.UDP_PACKET_TIMEOUT} seconds, attempting reconnection...");
                        break;
                    }
                    
                    // 長時間アイドル状態でも接続を維持
                    if (packetCount > 0 && timeoutCount >= DiscordConstants.UDP_IDLE_TIMEOUT) {
                        timeoutCount = 0; // リセットして継続
                    }
                }
            } catch (Exception ex) {
                if (_voiceConnected) {
                    LogMessage($"UDP receive error: {ex.Message}");
                }
                await Task.Delay(DiscordConstants.UDP_RECEIVE_TIMEOUT);
            }
        }
        
    }

    /// <summary>
    /// Voice Gatewayへのハートビート送信を定期的に開始します。
    /// </summary>
    /// <param name="interval">ハートビートの間隔（ミリ秒）。</param>
    private async Task StartVoiceHeartbeat(double interval) {
        int intervalMs = (int)interval;
        _voiceHeartbeatTimer = new System.Timers.Timer(intervalMs);
        _voiceHeartbeatTimer.Elapsed += async (sender, e) => {
            if (_voiceConnected) {
                await SendVoiceHeartbeat();
            }
        };
        _voiceHeartbeatTimer.Start();
        
        LogMessage($"🔄 Voice heartbeat started (interval: {intervalMs}ms) - Discord.js style");
    }

    /// <summary>
    /// Voice Gatewayにハートビートを送信します。
    /// </summary>
    private async Task SendVoiceHeartbeat() {
        try {
            // Discord.js VoiceWebSocket.ts準拠の実装
            if (_lastHeartbeatSend != 0 && _missedHeartbeats >= 3) {
                LogMessage("❌ Missed too many heartbeats (3) - disconnecting");
                await _voiceWebSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Too many missed heartbeats", CancellationToken.None);
                return;
            }
            
            _lastHeartbeatSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _missedHeartbeats++;
            
            var nonce = _lastHeartbeatSend;
            var heartbeat = DiscordPayloadHelper.CreateVoiceHeartbeatPayload(nonce, _voiceSequence);
            await SendVoiceMessage(JsonConvert.SerializeObject(heartbeat));
            
        } catch (Exception ex) {
            LogMessage($"❌ Voice heartbeat error: {ex.Message}");
        }
    }

    // Discord.js VoiceUDPSocket.ts準拠のSocketConfig構造体
    private class SocketConfig {
        public string ip { get; set; }
        public int port { get; set; }
    }
    
    /// <summary>
    /// DiscordのIP Discovery応答パケットを解析し、IPアドレスとポートを抽出します。
    /// Discord.jsの`parseLocalPacket`互換メソッドです。
    /// </summary>
    /// <param name="message">サーバーからの74バイトの応答パケット。</param>
    /// <returns>IPとポートを含むSocketConfigオブジェクト。解析に失敗した場合はnull。</returns>
    private SocketConfig ParseLocalPacket(byte[] message) {
        try {
            var packet = message;
            
            // Discord.js VoiceUDPSocket.ts準拠の応答検証
                    if (packet.Length < DiscordConstants.UDP_DISCOVERY_PACKET_SIZE) {
            LogMessage($"❌ Invalid packet length: {packet.Length} (expected {DiscordConstants.UDP_DISCOVERY_PACKET_SIZE})");
            return null;
        }
            
            // Discord.js実装: if (message.readUInt16BE(0) !== 2) return;
            var responseType = (packet[0] << 8) | packet[1];
            if (responseType != 2) {
                LogMessage($"❌ Invalid response type: {responseType} (expected 2)");
                return null;
            }
            
            // Discord.js実装: packet.slice(8, packet.indexOf(0, 8)).toString('utf8')
            var ipEndIndex = Array.IndexOf(packet, (byte)0, 8);
            if (ipEndIndex == -1) ipEndIndex = packet.Length;
            
            var ipLength = ipEndIndex - 8;
            var ipBytes = new byte[ipLength];
            Array.Copy(packet, 8, ipBytes, 0, ipLength);
            var ip = Encoding.UTF8.GetString(ipBytes);
            
            // Discord.js実装: packet.readUInt16BE(packet.length - 2)
            var port = (packet[packet.Length - 2] << 8) | packet[packet.Length - 1];
            
            if (string.IsNullOrEmpty(ip) || port <= 0) {
                LogMessage("❌ Invalid IP or port from parseLocalPacket");
                return null;
            }
            
            return new SocketConfig { ip = ip, port = port };
            
        } catch (Exception ex) {
            LogMessage($"❌ parseLocalPacket error: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        DisposeResources();
    }

    private void DisposeResources()
    {
        // Discord.js VoiceWebSocket.ts準拠のクリーンアップ
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _voiceHeartbeatTimer?.Dispose();
        _voiceHeartbeatTimer = null;
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        // WebSocket接続を閉じる
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            LogMessage("🔄 Closing main WebSocket...");
            _ = _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
        }
        _webSocket?.Dispose();
        _webSocket = null;

        if (_voiceWebSocket != null && _voiceWebSocket.State == WebSocketState.Open)
        {
            LogMessage("🔄 Closing voice WebSocket...");
            _ = _voiceWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
        }
        _voiceWebSocket?.Dispose();
        _voiceWebSocket = null;

        // UDPクライアントを閉じる
        if (_voiceUdpClient != null)
        {
            LogMessage("🔄 Closing UDP client...");
            _voiceUdpClient.Close();
            _voiceUdpClient.Dispose();
            _voiceUdpClient = null;
        }

        // HttpClientを破棄
        _httpClient?.Dispose();
        _httpClient = null;

        // CancellationTokenSourceを破棄
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        // Opusデコーダーを破棄
        _opusDecoder?.Dispose();
        _opusDecoder = null;
    }
}
