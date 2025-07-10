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

public class DiscordBotClient : MonoBehaviour {
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
    private const int KEEP_ALIVE_INTERVAL = 5000; // 5秒
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
    
    // NetworkingState, SUPPORTED_ENCRYPTION_MODES, Awake, OnDestroy, InitializeOpusDecoder, Update, StartBot, LoadSettingsFromCentralManager, StopBot, ConnectToDiscord, ProcessDiscordMessage, IsValidJson, StartHeartbeat, SendIdentify, SendHeartbeat, SendMessage, HandleDispatchEvent, JoinVoiceChannel, ConnectToVoiceGateway, ProcessVoiceMessage, SendVoiceIdentify, SendVoiceMessage, SetupUdpClient, GetLocalIPAddress, PerformUdpIpDiscovery, ParseLocalPacket, SocketConfig, UseDiscordJsFallback, CompleteUdpDiscovery, ChooseEncryptionMode, StartUdpAudioReceive, StartKeepAlive, SendKeepAlive, SetupUdpClientForAudio, ReceiveUdpAudio, ProcessRtpPacket, ExtractOpusFromDiscordPacket, ProcessOpusData, ConvertStereoToMono, ConvertToFloatAndResample, ProcessAudioCoroutine, TranscribeWithWitAI, ConvertToRawPcm, ConvertToWav
    // ...（ここにリファクタ前の全実装を貼り付けてください）...

    private async Task ReconnectAsync() {
        LogMessage("Attempting to reconnect...");
        StopBot();
        await Task.Delay(5000);
        StartBot();
    }

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

    private void Awake() {
        InitializeOpusDecoder();
    }

    private void OnDestroy() {
        LogMessage("🗑️ DiscordBotClient being destroyed - performing cleanup");
        StopBot();
    }

    private void InitializeOpusDecoder() {
        try {
            _opusDecoder = OpusCodecFactory.CreateDecoder(48000, 2);
            LogMessage("Opus decoder initialized");
        } catch (Exception ex) {
            LogMessage($"Opus decoder initialization failed: {ex.Message}");
        }
    }

    private void Update() {
        lock (_opusPacketQueue) {
            while (_opusPacketQueue.Count > 0) {
                var packet = _opusPacketQueue.Dequeue();
                ProcessOpusData(packet.data, packet.userId);
            }
        }
    }

    public async void StartBot() {
        try {
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
        } catch (Exception ex) {
            LogMessage($"❌ StartBot error: {ex.Message}");
        }
    }

    private async Task ConnectToDiscord() {
        try {
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), _cancellationTokenSource.Token);
            _isConnected = true;
            
            LogMessage("✅ Connected to Discord Gateway");
            
            _ = Task.Run(ReceiveMessages, _cancellationTokenSource.Token);
        } catch (Exception ex) {
            LogMessage($"❌ Discord connection error: {ex.Message}");
        }
    }

    private async Task ReceiveVoiceMessages() {
        var buffer = new byte[4096];
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
                    LogMessage($"Voice message error: {ex.Message}");
                }
                break;
            }
        }
    }

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
                                // 音声が停止した時に診断を実行
                                DiagnoseAudioProcessing();
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

    private async Task ConnectToVoiceGateway() {
        try {
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
        } catch (Exception ex) {
            LogMessage($"❌ Voice connection error: {ex.Message}");
            _voiceConnected = false;
        }
    }

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

    public void StopBot() {
        LogMessage("🛑 Starting bot shutdown process...");
        
        // フラグを先に設定して、新しい処理を開始しないようにする
        _isConnected = false;
        _voiceConnected = false;

        // 音声バッファをクリア
        lock (_audioBuffer) {
            _audioBuffer.Clear();
        }
        
        // Opusパケットキューをクリア
        lock (_opusPacketQueue) {
            _opusPacketQueue.Clear();
        }

        // Discord.js VoiceWebSocket.ts準拠のクリーンアップ
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _voiceHeartbeatTimer?.Dispose();
        _voiceHeartbeatTimer = null;
        
        // Discord.js VoiceUDPSocket.ts準拠のクリーンアップ
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        // CancellationTokenSourceをキャンセル（これによりすべての非同期処理が停止する）
        if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested) {
            LogMessage("🔄 Cancelling all async operations...");
            _cancellationTokenSource.Cancel();
        }

        // WebSocket接続を閉じる（非同期だが待機しない）
        if (_webSocket != null && _webSocket.State == WebSocketState.Open) {
            LogMessage("🔄 Closing main WebSocket...");
            _ = _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
        }
        _webSocket?.Dispose();
        _webSocket = null;

        if (_voiceWebSocket != null && _voiceWebSocket.State == WebSocketState.Open) {
            LogMessage("🔄 Closing voice WebSocket...");
            _ = _voiceWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
        }
        _voiceWebSocket?.Dispose();
        _voiceWebSocket = null;

        // UDPクライアントを閉じる
        if (_voiceUdpClient != null) {
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
        
        // Discord.js準拠の状態リセット
        _networkingState = NetworkingState.Closed;
        _lastHeartbeatAck = 0;
        _lastHeartbeatSend = 0;
        _missedHeartbeats = 0;
        _voiceSequence = -1;
        _ping = null;
        _keepAliveCounter = 0;
        
        // 音声処理関連のカウンターをリセット
        _successfulDecryptions = 0;
        _failedDecryptions = 0;
        _opusSuccesses = 0;
        _opusErrors = 0;
        
        LogMessage("✅ Bot shutdown completed - all resources cleaned up");
    }

    private async Task ReceiveMessages() {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();
        
        LogMessage("📡 Starting main Discord message receive loop");
        
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
                    LogMessage($"Message receive error: {ex.Message}");
                }
                break;
            }
        }
        
        LogMessage("📡 Main Discord message receive loop ended");
    }

    private async Task ProcessDiscordMessage(string message) {
        try {
            if (!IsValidJson(message)) return;
            
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
            LogMessage($"Message processing error: {ex.Message}");
        }
    }

    private bool IsValidJson(string jsonString) {
        try {
            if (string.IsNullOrWhiteSpace(jsonString)) return false;
            jsonString = jsonString.Trim();
            if ((!jsonString.StartsWith("{") || !jsonString.EndsWith("}")) &&
                (!jsonString.StartsWith("[") || !jsonString.EndsWith("]"))) {
                return false;
            }
            JToken.Parse(jsonString);
            return true;
        } catch {
            return false;
        }
    }

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

    private async Task SendIdentify() {
        var identify = new {
            op = 2,
            d = new {
                token = discordToken,
                intents = 32509,
                properties = new {
                    os = "unity",
                    browser = "unity-bot",
                    device = "unity-bot"
                }
            }
        };

        await SendMessage(JsonConvert.SerializeObject(identify));
    }

    private async Task SendHeartbeat() {
        var heartbeat = new {
            op = 1,
            d = _mainSequence
        };
        await SendMessage(JsonConvert.SerializeObject(heartbeat));
    }

    private async Task JoinVoiceChannel() {
        var voiceStateUpdate = new {
            op = 4,
            d = new {
                guild_id = guildId,
                channel_id = voiceChannelId,
                self_mute = true,
                self_deaf = false
            }
        };
        await SendMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }

    private async Task SendVoiceIdentify() {
        var identify = new {
            op = 0,
            d = new {
                server_id = guildId,
                user_id = botUserId,
                session_id = _voiceSessionId,
                token = _voiceToken
            }
        };
        await SendVoiceMessage(JsonConvert.SerializeObject(identify));
    }

    private async Task SetupUdpClient() {
        try {
            _voiceUdpClient?.Close();
            _voiceUdpClient?.Dispose();
            
            // Discord.jsの実装を参考に、UDPクライアントを作成（バインドは後で行う）
            _voiceUdpClient = new UdpClient();
            _voiceUdpClient.Client.ReceiveBufferSize = 65536;
            _voiceUdpClient.Client.SendBufferSize = 65536;
            
            // UDPソケットの設定を最適化
            _voiceUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _voiceUdpClient.Client.ReceiveTimeout = 0; // ノンブロッキング
            _voiceUdpClient.Client.SendTimeout = 5000;
            
            LogMessage("UDP client set up successfully");
        } catch (Exception ex) {
            LogMessage($"UDP setup error: {ex.Message}");
        }
    }

    private string GetLocalIPAddress() {
        try {
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
        } catch (Exception ex) {
            LogMessage($"Primary IP detection failed: {ex.Message}");
            
            // フォールバック: ネットワークインターフェースから取得
            try {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList) {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) {
                        LogMessage($"Fallback IP detected: {ip}");
                        return ip.ToString();
                    }
                }
            } catch (Exception ex2) {
                LogMessage($"Fallback IP detection failed: {ex2.Message}");
            }
            
            // 最後のフォールバック
            LogMessage("Using default fallback IP: 192.168.1.1");
            return "192.168.1.1";
        }
    }

    private async Task<bool> PerformUdpIpDiscovery() {
        try {
            _networkingState = NetworkingState.UdpHandshaking; // 状態遷移を記録
            
            // UDPクライアントを任意のポートにバインド
            _voiceUdpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var boundEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
            
            // Discord.js VoiceUDPSocket.ts完全準拠の74バイトパケット
            var discoveryBuffer = new byte[74];
            
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
            var timeoutTask = Task.Delay(3000);
            
            var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
            
            if (completedTask == receiveTask) {
                var result = await receiveTask;
                var message = result.Buffer;
                
                if (message.Length >= 74) {
                    // Discord.js VoiceUDPSocket.ts準拠の応答解析
                    var localConfig = ParseLocalPacket(message);
                    
                    if (localConfig != null) {
                        return await CompleteUdpDiscovery(localConfig.ip, localConfig.port);
                    }
                } else {
                    LogMessage($"❌ Discovery response too short: {message.Length} bytes (expected 74)");
                }
            } else {
                LogMessage("❌ Discovery timeout after 3000ms");
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
                var rtpHeader = new byte[12];
                Array.Copy(packet, 0, rtpHeader, 0, 12);
                
                var encryptedData = new byte[packet.Length - 12];
                Array.Copy(packet, 12, encryptedData, 0, encryptedData.Length);
                
                if (encryptedData.Length >= 40 && _secretKey != null) {
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

    private byte[] ExtractOpusFromDiscordPacket(byte[] discordPacket) {
        try {
            if (discordPacket == null || discordPacket.Length < 12) {
                return null;
            }
            
            // Discord音声パケットの構造解析
            // BE-DE で始まるDiscord独自ヘッダーをスキップ
            if (discordPacket.Length >= 2 && discordPacket[0] == 0xBE && discordPacket[1] == 0xDE) {
                // Discord拡張ヘッダーは12バイト固定
                const int DISCORD_HEADER_SIZE = 12;
                
                if (discordPacket.Length <= DISCORD_HEADER_SIZE) {
                    LogMessage($"⚠️ Discord packet too small: {discordPacket.Length} bytes");
                    return null;
                }
                
                // Opusデータ部分を抽出（12バイト後から）
                int opusDataSize = discordPacket.Length - DISCORD_HEADER_SIZE;
                byte[] opusData = new byte[opusDataSize];
                Array.Copy(discordPacket, DISCORD_HEADER_SIZE, opusData, 0, opusDataSize);
                
                return opusData;
            }
            
            // BE-DEヘッダーがない場合、そのままOpusデータとして扱う
            return discordPacket;
            
        } catch (Exception ex) {
            LogMessage($"❌ Discord packet extraction error: {ex.Message}");
            return null;
        }
    }
    
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
            
            short[] pcmData = new short[960 * 2];
            int decodedSamples = _opusDecoder.Decode(opusData, pcmData, 960, false);
            
            if (decodedSamples > 0) {
                _opusSuccesses++;
                
                short[] actualPcmData = new short[decodedSamples * 2];
                Array.Copy(pcmData, actualPcmData, decodedSamples * 2);
                
                short[] monoData = ConvertStereoToMono(actualPcmData, decodedSamples * 2);
                float[] resampledData = ConvertToFloatAndResample(monoData, 48000, 16000);
                
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

    private short[] ConvertStereoToMono(short[] stereoData, int totalSamples) {
        short[] monoData = new short[totalSamples / 2];
        for (int i = 0; i < monoData.Length; i++) {
            monoData[i] = stereoData[i * 2];
        }
        return monoData;
    }

    private float[] ConvertToFloatAndResample(short[] shortData, int fromSampleRate, int toSampleRate) {
        if (fromSampleRate == 48000 && toSampleRate == 16000) {
            float[] resampledData = new float[shortData.Length / 3];
            for (int i = 0; i < resampledData.Length; i++) {
                resampledData[i] = shortData[i * 3] / 32768.0f;
            }
            return resampledData;
        } else {
            float[] floatData = new float[shortData.Length];
            for (int i = 0; i < shortData.Length; i++) {
                floatData[i] = shortData[i] / 32768.0f;
            }
            return floatData;
        }
    }

    private IEnumerator ProcessAudioCoroutine(float[] audioData) {
        LogMessage($"🎤 Starting audio processing coroutine with {audioData.Length} samples");
        
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
                LogMessage("🎤 Starting Wit.AI transcription...");
                recognizedText = await TranscribeWithWitAI(audioData);
                LogMessage("🎤 Wit.AI transcription completed");
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
        
        LogMessage("🎤 Audio processing coroutine completed");
    }

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
            byte[] rawPcmData = ConvertToRawPcm(audioData, 16000, 1);
            
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

    // Node.js準拠: 生のPCMデータに変換（WAVヘッダーなし）
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

    private void ProcessAudioBuffer(bool force)
    {
        lock (_audioBuffer)
        {
            // 2秒以上のデータがある場合、または強制的に処理する場合（かつデータが少しでもある場合）
            if (_audioBuffer.Count >= 16000 * 2 || (force && _audioBuffer.Count > 1600)) // 0.1秒以上
            {
                float[] audioData = _audioBuffer.ToArray();
                _audioBuffer.Clear();
                
                LogMessage($"🎯 Processing audio: {audioData.Length} samples (force: {force})");
                StartCoroutine(ProcessAudioCoroutine(audioData));
            }
            else if (force && _audioBuffer.Count > 0)
            {
                // 強制処理の場合、少量のデータでも処理
                float[] audioData = _audioBuffer.ToArray();
                _audioBuffer.Clear();
                
                LogMessage($"🎯 Force processing: {audioData.Length} samples");
                StartCoroutine(ProcessAudioCoroutine(audioData));
            }
        }
    }

    // 音声処理の状態を診断するメソッド
    private void DiagnoseAudioProcessing() {
        lock (_audioBuffer) {
            LogMessage($"🔍 Audio Diagnosis:");
            LogMessage($"  - Audio buffer samples: {_audioBuffer.Count}");
            LogMessage($"  - Opus packet queue: {_opusPacketQueue.Count}");
            LogMessage($"  - Successful decryptions: {_successfulDecryptions}");
            LogMessage($"  - Failed decryptions: {_failedDecryptions}");
            LogMessage($"  - Opus successes: {_opusSuccesses}");
            LogMessage($"  - Opus errors: {_opusErrors}");
            LogMessage($"  - Target user speaking: {_isTargetUserSpeaking}");
            LogMessage($"  - Voice connected: {_voiceConnected}");
        }
    }

    // Data structures
    [Serializable]
    public class DiscordGatewayPayload {
        public int op;
        public object d;
        public int? s;
        public string t;
    }

    [Serializable]
    public class HelloData {
        public int heartbeat_interval;
    }

    [Serializable]
    public class ReadyData {
        public string session_id;
        public DiscordUser user;
    }

    [Serializable]
    public class DiscordUser {
        public string id;
        public string username;
        public string discriminator;
    }

    [Serializable]
    public class VoiceServerData {
        public string endpoint;
        public string token;
    }

    [Serializable]
    public class VoiceStateData {
        public string user_id;
        public string session_id;
    }

    [Serializable]
    public class VoiceGatewayPayload {
        public int op;
        public object d;
    }

    [Serializable]
    public class VoiceReadyData {
        public uint ssrc;
        public string ip;
        public int port;
        public string[] modes;
    }

    [Serializable]
    public class VoiceSpeakingData {
        public bool speaking;
        public uint ssrc;
        public string user_id;
    }

    [Serializable]
    public class VoiceHelloData {
        public double heartbeat_interval;
    }

    [Serializable]
    public class VoiceSessionDescriptionData {
        public byte[] secret_key;
        public string mode;
    }

    [Serializable]
    public class WitAIResponse {
        public string text;
        public string type; // Node.js準拠: FINAL_UNDERSTANDINGフィルタリング用
    }

    // Discord.js準拠の暗号化モード（XSalsa20対応のため古いモードを優先）
    private readonly string[] SUPPORTED_ENCRYPTION_MODES = { "xsalsa20_poly1305", "xsalsa20_poly1305_suffix", "aead_xchacha20_poly1305_rtpsize", "aead_aes256_gcm_rtpsize" };

    private async Task<bool> UseDiscordJsFallback() {
        try {
            LogMessage("📋 Using Discord.js fallback approach...");
            
            // Discord.js フォールバック: ローカルエンドポイントを使用
            var localEndpoint = (IPEndPoint)_voiceUdpClient.Client.LocalEndPoint;
            string fallbackIP = GetLocalIPAddress();
            
            LogMessage($"📋 Fallback IP: {fallbackIP}");
            LogMessage($"📋 Fallback Port: {localEndpoint.Port}");
            LogMessage($"⚠️ Using bound endpoint: {fallbackIP}:{localEndpoint.Port}");
            LogMessage($"⚠️ Voice reception may be limited due to NAT restrictions");
            
            return await CompleteUdpDiscovery(fallbackIP, localEndpoint.Port);
        } catch (Exception ex) {
            LogMessage($"Discord.js fallback error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CompleteUdpDiscovery(string detectedIP, int detectedPort) {
        try {
            LogMessage($"🔄 Completing UDP discovery with IP: {detectedIP}, Port: {detectedPort}");
            
            // Discord.js Networking.ts準拠の状態遷移
            _networkingState = NetworkingState.SelectingProtocol;
            
            // Discord.js実装通りの暗号化モード選択
            string selectedMode = ChooseEncryptionMode(_availableModes);
            
            var selectProtocolData = new {
                op = 1,
                d = new {
                    protocol = "udp",
                    data = new {
                        address = detectedIP,
                        port = detectedPort,
                        mode = selectedMode
                    }
                }
            };
                
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
        } catch (Exception ex) {
            LogMessage($"❌ UDP discovery completion error: {ex.Message}");
            LogMessage($"Error details: {ex.StackTrace}");
            return false;
        }
    }
    
    // Discord.js Networking.ts準拠の暗号化モード選択
    private string ChooseEncryptionMode(string[] availableModes) {
        if (availableModes == null) {
            LogMessage("⚠️ No encryption modes available, using default");
            return "xsalsa20_poly1305";
        }
        
        foreach (var supportedMode in SUPPORTED_ENCRYPTION_MODES) {
            if (availableModes.Contains(supportedMode)) {
                LogMessage($"🔐 Selected encryption mode: {supportedMode} (Discord.js preferred)");
                return supportedMode;
            }
        }
        
        // フォールバック：利用可能なモードの最初のもの
        var fallbackMode = availableModes.Length > 0 ? availableModes[0] : "xsalsa20_poly1305";
        LogMessage($"⚠️ Using fallback encryption mode: {fallbackMode}");
        return fallbackMode;
    }

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
    
    // Discord.js VoiceUDPSocket.ts準拠のKeep Alive実装
    private void StartKeepAlive() {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = new System.Timers.Timer(KEEP_ALIVE_INTERVAL);
        _keepAliveTimer.Elapsed += async (sender, e) => await SendKeepAlive();
        _keepAliveTimer.Start();
        
        // Discord.js VoiceUDPSocket.ts準拠：即座に最初のKeep Aliveを送信
        _ = Task.Run(SendKeepAlive);
    }
    
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

    private async Task SetupUdpClientForAudio() {
        try {
            // 既存のUDPクライアントがある場合は適切に処理
            if (_voiceUdpClient != null) {
                return;
            }
            
            // 新しいUDPクライアントを作成（Discord.jsパターンを参考）
            _voiceUdpClient = new UdpClient();
            _voiceUdpClient.Client.ReceiveBufferSize = 65536;
            _voiceUdpClient.Client.SendBufferSize = 65536;
            
            // Discord.jsの推奨設定を適用
            _voiceUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _voiceUdpClient.Client.ReceiveTimeout = 0; // ノンブロッキング
            _voiceUdpClient.Client.SendTimeout = 5000;
        } catch (Exception ex) {
            LogMessage($"UDP audio client setup error: {ex.Message}");
        }
    }

    private async Task ReceiveUdpAudio() {
        int packetCount = 0;
        int timeoutCount = 0;
        
        LogMessage("🎧 Starting UDP audio receive loop");
        
        while (_voiceConnected && _voiceUdpClient != null && !_cancellationTokenSource.Token.IsCancellationRequested) {
            try {
                var receiveTask = _voiceUdpClient.ReceiveAsync();
                var timeoutTask = Task.Delay(1000);
                
                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                
                if (completedTask == receiveTask) {
                    var result = await receiveTask;
                    var packet = result.Buffer;
                    packetCount++;
                    timeoutCount = 0; // リセット
                    
                    if (packet.Length >= 12) {
                        // 音声パケットは通常60バイト以上
                        if (packet.Length >= 60) {
                            await ProcessRtpPacket(packet);
                        }
                    } else {
                        LogMessage($"⚠️ Packet too small #{packetCount}: {packet.Length} bytes");
                    }
                } else {
                    timeoutCount++;
                    
                    // 30秒経過してもパケットが受信されない場合、再接続を試行
                    if (packetCount == 0 && timeoutCount >= 30) {
                        LogMessage("⚠️ No packets received for 30 seconds, attempting reconnection...");
                        break;
                    }
                    
                    // 長時間アイドル状態でも接続を維持
                    if (packetCount > 0 && timeoutCount >= 60) {
                        timeoutCount = 0; // リセットして継続
                    }
                }
            } catch (Exception ex) {
                if (_voiceConnected) {
                    LogMessage($"UDP receive error: {ex.Message}");
                }
                await Task.Delay(1000);
            }
        }
        
        LogMessage("🎧 UDP audio receive loop ended");
        
        // UDPループが終了した理由を診断
        LogMessage($"🔍 UDP loop exit diagnosis:");
        LogMessage($"  - Voice connected: {_voiceConnected}");
        LogMessage($"  - UDP client null: {_voiceUdpClient == null}");
        LogMessage($"  - Cancellation requested: {_cancellationTokenSource?.Token.IsCancellationRequested}");
        LogMessage($"  - Total packets received: {packetCount}");
        LogMessage($"  - Timeout count: {timeoutCount}");
        
        // 音声処理の状態も診断
        DiagnoseAudioProcessing();
    }

    // Discord.js VoiceWebSocket.ts準拠のハートビート管理
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
            
            var heartbeat = new {
                op = 3, // VoiceOpcodes.Heartbeat
                d = new {
                    t = nonce,
                    seq_ack = _voiceSequence
                }
            };
            
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
    /// Parses the 74-byte response packet returned by the voice server during IP
    /// discovery and extracts the detected IP とポート番号.
    /// Discord.js の `parseLocalPacket` 互換メソッドです。
    /// </summary>
    private SocketConfig ParseLocalPacket(byte[] message) {
        try {
            var packet = message;
            
            // Discord.js VoiceUDPSocket.ts準拠の応答検証
            if (packet.Length < 74) {
                LogMessage($"❌ Invalid packet length: {packet.Length} (expected 74)");
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
}
