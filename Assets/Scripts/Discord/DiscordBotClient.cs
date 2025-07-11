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
    /// 同期操作を安全に実行し、エラーをログに記録
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
        ErrorHandler.SafeExecute<bool>(() => {
            _opusDecoder = OpusCodecFactory.CreateDecoder(DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.CHANNELS_STEREO);
            LogMessage("Opus decoder initialized");
            return true;
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
        await ErrorHandler.SafeExecuteAsync<bool>(async () => {
            LoadSettingsFromCentralManager();
            
            if (string.IsNullOrEmpty(discordToken)) {
                LogMessage("❌ Discord token is not set");
                return false;
            }
            
            _cancellationTokenSource = new CancellationTokenSource();
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {witaiToken}");
            
            InitializeOpusDecoder();
            
            await ConnectToDiscord();
            return true;
        }, "StartBot", LogMessage);
    }

    /// <summary>
    /// WebSocketにメッセージを送信する統合メソッド
    /// </summary>
    /// <param name="message">送信するJSON文字列</param>
    /// <param name="isVoice">Voice Gatewayかどうか</param>
    private async Task SendWebSocketMessage(string message, bool isVoice = false) {
        var webSocket = isVoice ? _voiceWebSocket : _webSocket;
        var socketName = isVoice ? "Voice WebSocket" : "WebSocket";
        try {
            if (webSocket != null && webSocket.State == WebSocketState.Open) {
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            } else {
                LogMessage($"❌ {socketName} is not connected");
            }
        } catch (Exception ex) {
            LogMessage($"❌ Send {socketName.ToLower()} message error: {ex.Message}");
        }
    }

    /// <summary>
    /// メインGatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendMessage(string message) {
        await SendWebSocketMessage(message, false);
    }

    /// <summary>
    /// Voice Gatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendVoiceMessage(string message) {
        await SendWebSocketMessage(message, true);
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
            UpdateVoiceSequence(message);
            
            switch (payload.op) {
                case 8: await HandleVoiceHello(payload); break;
                case 2: await HandleVoiceReady(payload); break;
                case 4: await HandleVoiceSessionDescription(payload); break;
                case 3: HandleVoiceHeartbeatAck(); break;
                case 5: HandleVoiceSpeaking(payload); break;
                case 6: case 11: case 18: case 20: break; // 無視するメッセージ
                default: LogUnknownVoiceMessage(payload.op, payload.d); break;
            }
        } catch (Exception ex) {
            LogMessage($"Voice message processing error: {ex.Message}");
            LogMessage($"Raw message: {message}");
        }
    }

    /// <summary>
    /// Voice GatewayのHelloメッセージを処理
    /// </summary>
    private async Task HandleVoiceHello(VoiceGatewayPayload payload) {
        _networkingState = NetworkingState.Identifying;
        var helloData = JsonConvert.DeserializeObject<VoiceHelloData>(payload.d.ToString());
        await StartVoiceHeartbeat(helloData.heartbeat_interval);
        await SendVoiceIdentify();
    }

    /// <summary>
    /// Voice GatewayのReadyメッセージを処理
    /// </summary>
    private async Task HandleVoiceReady(VoiceGatewayPayload payload) {
        _networkingState = NetworkingState.Identifying;
        var readyData = JsonConvert.DeserializeObject<VoiceReadyData>(payload.d.ToString());
        
        await InitializeVoiceConnection(readyData);
        await PerformUdpDiscovery();
    }

    /// <summary>
    /// Voice GatewayのSession Descriptionメッセージを処理
    /// </summary>
    private async Task HandleVoiceSessionDescription(VoiceGatewayPayload payload) {
        _networkingState = NetworkingState.Ready;
        var sessionData = JsonConvert.DeserializeObject<VoiceSessionDescriptionData>(payload.d.ToString());
        
        _secretKey = sessionData.secret_key;
        _encryptionMode = sessionData.mode;
        
        LogMessage($"🔐 Encryption mode: {_encryptionMode}");
        LogMessage($"🎯 Voice connection ready! (Discord.js style)");
        
        await StartUdpAudioReceive();
    }

    /// <summary>
    /// Voice GatewayのHeartbeat ACKを処理
    /// </summary>
    private void HandleVoiceHeartbeatAck() {
        _lastHeartbeatAck = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _missedHeartbeats = 0;
        
        if (_lastHeartbeatSend != 0) {
            _ping = (int)(_lastHeartbeatAck - _lastHeartbeatSend);
        }
    }

    /// <summary>
    /// Voice GatewayのSpeakingメッセージを処理
    /// </summary>
    private void HandleVoiceSpeaking(VoiceGatewayPayload payload) {
        var speakingData = JsonConvert.DeserializeObject<VoiceSpeakingData>(payload.d.ToString());
        if (speakingData.user_id == null) return;
        
        _ssrcToUserMap[speakingData.ssrc] = speakingData.user_id;
        
        if (speakingData.user_id == targetUserId) {
            LogMessage($"🎯 Target user {(speakingData.speaking ? "started" : "stopped")} speaking (SSRC: {speakingData.ssrc})");
            _isTargetUserSpeaking = speakingData.speaking;

            if (!_isTargetUserSpeaking) {
                ProcessAudioBuffer(true);
            }
        }
    }

    /// <summary>
    /// 音声接続を初期化
    /// </summary>
    private async Task InitializeVoiceConnection(VoiceReadyData readyData) {
        _ourSSRC = readyData.ssrc;
        _voiceServerEndpoint = new IPEndPoint(IPAddress.Parse(readyData.ip), readyData.port);
        _availableModes = readyData.modes;
        
        LogMessage($"🎯 Voice Ready - BOT SSRC: {_ourSSRC}, Server: {readyData.ip}:{readyData.port}");
        
        await SetupUdpClient();
    }

    /// <summary>
    /// UDP発見処理を実行
    /// </summary>
    private async Task<bool> PerformUdpDiscovery() {
        bool discoverySuccess = await PerformUdpIpDiscovery();
        
        if (!discoverySuccess) {
            LogMessage("⚠️ UDP IP Discovery failed, attempting fallback approach");
            await PerformUdpFallback();
        }
        
        return discoverySuccess;
    }

    /// <summary>
    /// UDP発見のフォールバック処理
    /// </summary>
    private async Task PerformUdpFallback() {
        var localEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
        string fallbackIP = GetLocalIPAddress();
        
        bool fallbackSuccess = await CompleteUdpDiscovery(fallbackIP, localEndpoint.Port);
        
        if (!fallbackSuccess) {
            LogMessage("❌ WARNING: Both IP discovery and fallback failed. Voice may not work.");
        }
    }

    /// <summary>
    /// DiscordのVoice Serverに対してUDP IP Discoveryを実行し、
    /// 外部から見た自身のIPアドレスとポートを取得します。
    /// </summary>
    /// <returns>IP Discoveryが成功した場合はtrue、それ以外はfalse。</returns>
    private async Task<bool> PerformUdpIpDiscovery() {
        try {
            _networkingState = NetworkingState.UdpHandshaking;
            
            await SetupUdpClientForDiscovery();
            var discoveryPacket = CreateDiscoveryPacket();
            await SendDiscoveryPacket(discoveryPacket);
            
            return await WaitForDiscoveryResponse();
            
        } catch (Exception ex) {
            LogMessage($"❌ UDP discovery error: {ex.Message}");
            return await UseDiscordJsFallback();
        }
    }

    /// <summary>
    /// UDP発見用のクライアントをセットアップ
    /// </summary>
    private async Task SetupUdpClientForDiscovery() {
        _voiceUdpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var boundEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
        LogMessage($"UDP client bound to: {boundEndpoint}");
    }

    /// <summary>
    /// 発見パケットを作成
    /// </summary>
    private byte[] CreateDiscoveryPacket() {
        var discoveryBuffer = new byte[DiscordConstants.UDP_DISCOVERY_PACKET_SIZE];
        
        // Type: 1
        discoveryBuffer[0] = 0x00;
        discoveryBuffer[1] = 0x01;
        
        // Length: 70
        discoveryBuffer[2] = 0x00;
        discoveryBuffer[3] = 0x46;
        
        // SSRC (Big Endian)
        var ssrcBytes = BitConverter.GetBytes(_ourSSRC);
        if (BitConverter.IsLittleEndian) {
            Array.Reverse(ssrcBytes);
        }
        Array.Copy(ssrcBytes, 0, discoveryBuffer, 4, 4);
        
        return discoveryBuffer;
    }

    /// <summary>
    /// 発見パケットを送信
    /// </summary>
    private async Task SendDiscoveryPacket(byte[] packet) {
        await _voiceUdpClient.SendAsync(packet, packet.Length, _voiceServerEndpoint);
        LogMessage("Discovery packet sent");
    }

    /// <summary>
    /// 発見応答を待機
    /// </summary>
    private async Task<bool> WaitForDiscoveryResponse() {
        var receiveTask = _voiceUdpClient.ReceiveAsync();
        var timeoutTask = Task.Delay(DiscordConstants.UDP_DISCOVERY_TIMEOUT);
        
        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
        
        if (completedTask == receiveTask) {
            return await ProcessDiscoveryResponse(await receiveTask);
        } else {
            LogMessage($"❌ Discovery timeout after {DiscordConstants.UDP_DISCOVERY_TIMEOUT}ms");
            return await UseDiscordJsFallback();
        }
    }

    /// <summary>
    /// 発見応答を処理
    /// </summary>
    private async Task<bool> ProcessDiscoveryResponse(UdpReceiveResult result) {
        var message = result.Buffer;
        
        if (message.Length >= DiscordConstants.UDP_DISCOVERY_PACKET_SIZE) {
            var localConfig = ParseLocalPacket(message);
            if (localConfig != null) {
                return await CompleteUdpDiscovery(localConfig.ip, localConfig.port);
            }
        } else {
            LogMessage($"❌ Discovery response too short: {message.Length} bytes");
        }
        
        return await UseDiscordJsFallback();
    }

    /// <summary>
    /// 受信したRTPパケットを処理します。
    /// SSRCからユーザーを特定し、暗号化された音声データを復号してOpusパケットキューに追加します。
    /// </summary>
    /// <param name="packet">受信したRTPパケットのバイト配列。</param>
    private async Task ProcessRtpPacket(byte[] packet) {
        try {
            var ssrc = ExtractSsrcFromPacket(packet);
            
            if (ssrc == _ourSSRC) {
                return; // BOT自身のパケットは静かに無視
            }
            
            if (_ssrcToUserMap.TryGetValue(ssrc, out string userId)) {
                await ProcessUserAudioPacket(packet, userId);
            } else {
                LogMessage($"⚠️ No user found for SSRC {ssrc}");
            }
        } catch (Exception ex) {
            LogMessage($"❌ RTP processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// パケットからSSRCを抽出
    /// </summary>
    private uint ExtractSsrcFromPacket(byte[] packet) {
        var ssrcBytes = new byte[4];
        Array.Copy(packet, 8, ssrcBytes, 0, 4);
        
        if (BitConverter.IsLittleEndian) {
            Array.Reverse(ssrcBytes);
        }
        return BitConverter.ToUInt32(ssrcBytes, 0);
    }

    /// <summary>
    /// ユーザーの音声パケットを処理
    /// </summary>
    private async Task ProcessUserAudioPacket(byte[] packet, string userId) {
        var rtpHeader = ExtractRtpHeader(packet);
        var encryptedData = ExtractEncryptedData(packet);
        
        if (IsValidEncryptedData(encryptedData)) {
            await DecryptAndQueueAudio(encryptedData, rtpHeader, userId);
        } else {
            LogMessage($"⚠️ Skipping packet - encrypted data too small ({encryptedData.Length}) or no secret key");
        }
    }

    /// <summary>
    /// RTPヘッダーを抽出
    /// </summary>
    private byte[] ExtractRtpHeader(byte[] packet) {
        var rtpHeader = new byte[DiscordConstants.RTP_HEADER_SIZE];
        Array.Copy(packet, 0, rtpHeader, 0, DiscordConstants.RTP_HEADER_SIZE);
        return rtpHeader;
    }

    /// <summary>
    /// 暗号化されたデータを抽出
    /// </summary>
    private byte[] ExtractEncryptedData(byte[] packet) {
        var encryptedData = new byte[packet.Length - DiscordConstants.RTP_HEADER_SIZE];
        Array.Copy(packet, DiscordConstants.RTP_HEADER_SIZE, encryptedData, 0, encryptedData.Length);
        return encryptedData;
    }

    /// <summary>
    /// 暗号化されたデータが有効かチェック
    /// </summary>
    private bool IsValidEncryptedData(byte[] encryptedData) {
        return encryptedData.Length >= DiscordConstants.MIN_ENCRYPTED_DATA_SIZE && _secretKey != null;
    }

    /// <summary>
    /// 音声データを復号してキューに追加
    /// </summary>
    private async Task DecryptAndQueueAudio(byte[] encryptedData, byte[] rtpHeader, string userId) {
        try {
            byte[] decryptedOpusData = DiscordCrypto.DecryptVoicePacket(encryptedData, rtpHeader, _secretKey, _encryptionMode);
            
            if (decryptedOpusData != null) {
                _successfulDecryptions++;
                await QueueOpusData(decryptedOpusData, userId);
            } else {
                _failedDecryptions++;
                LogMessage($"❌ Decryption failed ({_failedDecryptions} total failures)");
            }
        } catch (Exception decryptEx) {
            _failedDecryptions++;
            LogMessage($"❌ Decryption error: {decryptEx.Message}");
        }
    }

    /// <summary>
    /// Opusデータをキューに追加
    /// </summary>
    private async Task QueueOpusData(byte[] decryptedOpusData, string userId) {
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
    }

    /// <summary>
    /// 音声シーケンスを更新
    /// </summary>
    private void UpdateVoiceSequence(string message) {
        var jsonPayload = JObject.Parse(message);
        if (jsonPayload["seq"] != null) {
            _voiceSequence = jsonPayload["seq"].ToObject<int>();
        }
    }

    /// <summary>
    /// 未知のVoiceメッセージをログ出力
    /// </summary>
    private void LogUnknownVoiceMessage(int opCode, object data) {
        LogMessage($"Unknown voice OP code: {opCode}");
        LogMessage($"Voice message data: {data?.ToString() ?? "null"}");
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
                await HandleReadyEvent(data);
                break;
            case "VOICE_STATE_UPDATE":
                await HandleVoiceStateUpdateEvent(data);
                break;
            case "VOICE_SERVER_UPDATE":
                await HandleVoiceServerUpdateEvent(data);
                break;
        }
    }

    /// <summary>
    /// READYイベントを処理
    /// </summary>
    private async Task HandleReadyEvent(string data) {
        var readyData = JsonConvert.DeserializeObject<ReadyData>(data);
        _sessionId = readyData.session_id;
        botUserId = readyData.user.id;
        LogMessage($"Bot logged in: {readyData.user.username}");
        
        if (!string.IsNullOrEmpty(voiceChannelId)) {
            await JoinVoiceChannel();
        }
    }

    /// <summary>
    /// VOICE_STATE_UPDATEイベントを処理
    /// </summary>
    private async Task HandleVoiceStateUpdateEvent(string data) {
        var voiceStateData = JsonConvert.DeserializeObject<VoiceStateData>(data);
        _voiceSessionId = voiceStateData.session_id;
    }

    /// <summary>
    /// VOICE_SERVER_UPDATEイベントを処理
    /// </summary>
    private async Task HandleVoiceServerUpdateEvent(string data) {
        var voiceServerData = JsonConvert.DeserializeObject<VoiceServerData>(data);
        _voiceToken = voiceServerData.token;
        _voiceEndpoint = voiceServerData.endpoint;
        
        if (!string.IsNullOrEmpty(_voiceToken) && !string.IsNullOrEmpty(_voiceEndpoint) && !string.IsNullOrEmpty(_voiceSessionId)) {
            _ = Task.Run(ConnectToVoiceGateway);
        }
    }

    /// <summary>
    /// Discord Voice Gatewayに接続します。
    /// 既存の接続がある場合は一旦切断し、再接続します。
    /// </summary>
    private async Task ConnectToVoiceGateway() {
        await ErrorHandler.SafeExecuteAsync<bool>(async () => {
            _networkingState = NetworkingState.OpeningWs;
            
            await CleanupExistingVoiceConnection();
            
            _voiceWebSocket = await CreateWebSocketConnection(
                $"wss://{_voiceEndpoint}/?v=4",
                true,
                "Voice WebSocket"
            );
            
            _ = Task.Run(ReceiveVoiceMessages, _cancellationTokenSource.Token);
            
            return true;
        }, "Voice connection", LogMessage);
        
        if (!_voiceConnected) {
            _voiceConnected = false;
        }
    }

    /// <summary>
    /// 既存のVoice接続をクリーンアップ
    /// </summary>
    private async Task CleanupExistingVoiceConnection() {
        if (_voiceWebSocket != null) {
            if (_voiceWebSocket.State == WebSocketState.Open) {
                await _voiceWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            }
            _voiceWebSocket?.Dispose();
            _voiceWebSocket = null;
        }
    }

    /// <summary>
    /// 新しいVoice接続を作成
    /// </summary>
    private async Task CreateNewVoiceConnection() {
        _voiceWebSocket = new ClientWebSocket();
        var voiceGatewayUrl = $"wss://{_voiceEndpoint}/?v=4";
        
        await _voiceWebSocket.ConnectAsync(new Uri(voiceGatewayUrl), _cancellationTokenSource.Token);
        _voiceConnected = true;
        
        LogMessage("✅ Voice WebSocket connected successfully");
        
        _ = Task.Run(ReceiveVoiceMessages, _cancellationTokenSource.Token);
    }

    /// <summary>
    /// メインGatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
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
    /// UDPクライアントをセットアップする統合メソッド
    /// </summary>
    /// <param name="forAudio">音声受信用かどうか</param>
    private async Task SetupUdpClient(bool forAudio = false) {
        await ErrorHandler.SafeExecuteAsync<bool>(async () => {
            // 音声受信用の場合は既存クライアントをチェック
            if (forAudio && _voiceUdpClient != null) {
                return true;
            }
            
            _voiceUdpClient?.Close();
            _voiceUdpClient?.Dispose();
            
            _voiceUdpClient = new UdpClient();
            _voiceUdpClient.Client.ReceiveBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            _voiceUdpClient.Client.SendBufferSize = DiscordConstants.UDP_BUFFER_SIZE;
            
            _voiceUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _voiceUdpClient.Client.ReceiveTimeout = 0;
            _voiceUdpClient.Client.SendTimeout = DiscordConstants.UDP_SEND_TIMEOUT;
            
            LogMessage($"UDP client set up successfully (forAudio: {forAudio})");
            return true;
        }, "UDP setup", LogMessage);
    }

    /// <summary>
    /// 音声受信用にUDPクライアントをセットアップします。
    /// </summary>
    private async Task SetupUdpClientForAudio() {
        await SetupUdpClient(true);
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
                
                if (ip.StartsWith("172.") || ip.StartsWith("10.") || ip.StartsWith("192.168.")) {
                    LogMessage($"Detected private IP address: {ip} (may be behind NAT/VPN)");
                } else {
                    LogMessage($"Detected public IP address: {ip}");
                }
                
                return ip;
            }
        }, "Primary IP detection", LogMessage) ?? ErrorHandler.SafeExecute(() => {
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
    /// 音声処理の最適化された統合メソッド
    /// Opusデータのデコードから音声認識までの一連の処理を効率化
    /// </summary>
    /// <param name="opusData">デコードするOpusデータ</param>
    /// <param name="userId">音声の送信元ユーザーID</param>
    private void ProcessOpusData(byte[] opusData, string userId) {
        try {
            if (_opusDecoder == null || userId != targetUserId) {
                return; // 早期リターンで効率化
            }
            
            if (opusData.Length < 1) {
                LogOpusError("Opus data too small", opusData.Length);
                return;
            }
            
            // 統合された音声処理パイプライン
            var processedAudio = ProcessAudioPipeline(opusData);
            if (processedAudio != null) {
                AddToAudioBuffer(processedAudio);
                ProcessAudioBuffer(false);
            }
            
        } catch (Exception ex) {
            LogOpusError($"Opus processing error: {ex.Message}");
            HandleOpusDecoderReset(ex);
        }
    }

    /// <summary>
    /// 統合された音声処理パイプライン
    /// Opusデコードからリサンプリングまでを一括処理
    /// </summary>
    /// <param name="opusData">Opusデータ</param>
    /// <returns>処理済みのfloat音声データ</returns>
    private float[] ProcessAudioPipeline(byte[] opusData) {
        try {
            // Opusデコード
            short[] pcmData = new short[DiscordConstants.OPUS_FRAME_SIZE * DiscordConstants.CHANNELS_STEREO];
            int decodedSamples = _opusDecoder.Decode(opusData, pcmData, DiscordConstants.OPUS_FRAME_SIZE, false);
            
            if (decodedSamples <= 0) {
                LogOpusError("Opus decode failed", decodedSamples);
                return null;
            }
            
            _opusSuccesses++;
            
            // 統合された音声変換処理
            return ConvertAudioData(pcmData, decodedSamples);
            
        } catch (Exception ex) {
            LogOpusError($"Audio pipeline error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 統合された音声データ変換処理
    /// ステレオ→モノラル→リサンプリングを一括で実行
    /// </summary>
    /// <param name="pcmData">PCMデータ</param>
    /// <param name="decodedSamples">デコードされたサンプル数</param>
    /// <returns>変換済みのfloat音声データ</returns>
    private float[] ConvertAudioData(short[] pcmData, int decodedSamples) {
        try {
            // 実際のPCMデータを抽出
            short[] actualPcmData = new short[decodedSamples * DiscordConstants.CHANNELS_STEREO];
            Array.Copy(pcmData, actualPcmData, decodedSamples * DiscordConstants.CHANNELS_STEREO);
            
            // ステレオ→モノラル変換
            short[] monoData = ConvertStereoToMono(actualPcmData, decodedSamples * DiscordConstants.CHANNELS_STEREO);
            
            // リサンプリング（48kHz→16kHz）
            return ResampleAudioData(monoData, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);
            
        } catch (Exception ex) {
            LogOpusError($"Audio conversion error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 音声データのリサンプリング処理
    /// 48kHzから16kHzへの簡易リサンプリング
    /// </summary>
    /// <param name="audioData">変換元の音声データ</param>
    /// <param name="fromSampleRate">変換元サンプルレート</param>
    /// <param name="toSampleRate">変換先サンプルレート</param>
    /// <returns>リサンプリングされたfloat音声データ</returns>
    private float[] ResampleAudioData(short[] audioData, int fromSampleRate, int toSampleRate) {
        if (fromSampleRate == DiscordConstants.SAMPLE_RATE_48K && toSampleRate == DiscordConstants.SAMPLE_RATE_16K) {
            // 3:1の比率でリサンプリング（48kHz→16kHz）
            float[] resampledData = new float[audioData.Length / 3];
            for (int i = 0; i < resampledData.Length; i++) {
                resampledData[i] = audioData[i * 3] / DiscordConstants.PCM_SCALE_FACTOR;
            }
            return resampledData;
        } else {
            // その他のサンプルレート変換
            float[] floatData = new float[audioData.Length];
            for (int i = 0; i < audioData.Length; i++) {
                floatData[i] = audioData[i] / DiscordConstants.PCM_SCALE_FACTOR;
            }
            return floatData;
        }
    }

    /// <summary>
    /// 音声バッファへの安全な追加処理
    /// </summary>
    /// <param name="audioData">追加する音声データ</param>
    private void AddToAudioBuffer(float[] audioData) {
        if (audioData == null || audioData.Length == 0) return;
        
        lock (_audioBuffer) {
            _audioBuffer.AddRange(audioData);
        }
    }

    /// <summary>
    /// 統合された音声認識処理
    /// 非同期処理とエラーハンドリングを最適化
    /// </summary>
    /// <param name="audioData">処理対象の音声データ</param>
    private IEnumerator ProcessAudioCoroutine(float[] audioData) {
        if (audioData == null || audioData.Length == 0) {
            yield break;
        }

        var recognitionTask = new TaskCompletionSource<(string text, Exception error)>();
        
        // 音声認識を非同期で実行
        Task.Run(async () => {
            try {
                if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
                    recognitionTask.SetResult(("", null));
                    return;
                }
                
                string recognizedText = await TranscribeWithWitAI(audioData);
                recognitionTask.SetResult((recognizedText, null));
                
            } catch (OperationCanceledException) {
                recognitionTask.SetResult(("", null));
            } catch (Exception ex) {
                recognitionTask.SetResult(("", ex));
            }
        });

        // 結果を待機
        while (!recognitionTask.Task.IsCompleted) {
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
        }

        // 結果を処理（awaitを使用しない方法に変更）
        if (recognitionTask.Task.IsCompletedSuccessfully) {
            var result = recognitionTask.Task.Result;
            var (recognizedText, error) = result;
            
            if (_cancellationTokenSource?.Token.IsCancellationRequested == true) {
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
    }

    /// <summary>
    /// ステレオPCMデータをモノラルに変換します。
    /// </summary>
    /// <param name="stereoData">ステレオPCMデータ</param>
    /// <param name="totalSamples">合計サンプル数</param>
    /// <returns>モノラルに変換されたPCMデータ</returns>
    private short[] ConvertStereoToMono(short[] stereoData, int totalSamples) {
        short[] monoData = new short[totalSamples / 2];
        for (int i = 0; i < monoData.Length; i++) {
            monoData[i] = stereoData[i * 2];
        }
        return monoData;
    }

    /// <summary>
    /// 統合されたOpusエラーログ処理
    /// </summary>
    /// <param name="message">エラーメッセージ</param>
    /// <param name="value">関連する値（オプション）</param>
    private void LogOpusError(string message, object value = null) {
        _opusErrors++;
        if (_opusErrors <= 3 || _opusErrors % 10 == 0) {
            string logMessage = value != null ? $"{message}: {value}" : message;
            LogMessage($"❌ {logMessage} ({_opusErrors} total errors)");
        }
    }

    /// <summary>
    /// Opusデコーダーのリセット処理
    /// </summary>
    /// <param name="ex">発生した例外</param>
    private void HandleOpusDecoderReset(Exception ex) {
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

    /// <summary>
    /// WebSocket接続を確立する共通処理
    /// </summary>
    /// <param name="url">接続先URL</param>
    /// <param name="isVoice">Voice Gatewayかどうか</param>
    /// <param name="connectionName">接続名</param>
    private async Task<ClientWebSocket> CreateWebSocketConnection(string url, bool isVoice, string connectionName) {
        var webSocket = new ClientWebSocket();
        await webSocket.ConnectAsync(new Uri(url), _cancellationTokenSource.Token);
        
        if (isVoice) {
            _voiceConnected = true;
        } else {
            _isConnected = true;
        }
        
        LogMessage($"✅ {connectionName} connected successfully");
        return webSocket;
    }

    /// <summary>
    /// DiscordのメインGatewayにWebSocketで接続します。
    /// </summary>
    private async Task ConnectToDiscord() {
        await ErrorHandler.SafeExecuteAsync<bool>(async () => {
            _webSocket = await CreateWebSocketConnection(
                "wss://gateway.discord.gg/?v=10&encoding=json",
                false,
                "Discord Gateway"
            );
            
            _ = Task.Run(ReceiveMessages, _cancellationTokenSource.Token);
            return true;
        }, "Discord connection", LogMessage);
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
        ResetBotState();
        LogMessage("✅ Bot shutdown completed - all resources cleaned up");
    }

    /// <summary>
    /// ボットの状態をリセット
    /// </summary>
    private void ResetBotState() {
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
                case 10: await HandleMainHello(payload); break;
                case 11: HandleMainHeartbeatAck(); break;
                case 0: await HandleDispatchEvent(payload.t, payload.d.ToString()); break;
            }
        } catch (Exception ex) {
            LogMessage($"❌ Message processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// メインGatewayのHelloメッセージを処理
    /// </summary>
    private async Task HandleMainHello(DiscordGatewayPayload payload) {
        var helloData = JsonConvert.DeserializeObject<HelloData>(payload.d.ToString());
        await StartHeartbeat(helloData.heartbeat_interval);
        await SendIdentify();
    }

    /// <summary>
    /// メインGatewayのHeartbeat ACKを処理
    /// </summary>
    private void HandleMainHeartbeatAck() {
        _heartbeatAcknowledged = true;
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
}
