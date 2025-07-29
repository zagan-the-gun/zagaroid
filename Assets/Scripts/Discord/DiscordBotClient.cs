using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;
using System.Net.Http;
using Concentus;
using Concentus.Structs;
using Newtonsoft.Json.Linq;
/// <summary>
/// Discord Bot関連の定数定義
/// </summary>
public static class DiscordConstants {
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
    public const int SAMPLE_RATE_48K = 48000;
    public const int SAMPLE_RATE_16K = 16000;
    public const int CHANNELS_STEREO = 2;
    public const float PCM_SCALE_FACTOR = 32768.0f;
    // タイムアウト関連
    public const int RECONNECT_DELAY = 5000;
    // 音声認識関連
    public const int WITA_API_SAMPLE_RATE = 16000;
    public const int WITA_API_CHANNELS = 1;
    // Discord Gateway関連
    public const int DISCORD_INTENTS = 32509;
    public const string DISCORD_OS = "unity";
    public const string DISCORD_BROWSER = "unity-bot";
    public const string DISCORD_DEVICE = "unity-bot";
    public const string DISCORD_PROTOCOL = "udp";
    // Discord.js準拠の暗号化モード（実装済みのもののみ）
    public static readonly string[] SUPPORTED_ENCRYPTION_MODES = { 
        "xsalsa20_poly1305", 
        "xsalsa20_poly1305_suffix"
        // "aead_xchacha20_poly1305_rtpsize", // 未実装のため除外
        // "aead_aes256_gcm_rtpsize" // 未実装のため除外
    };
    public const string DEFAULT_ENCRYPTION_MODE = "xsalsa20_poly1305";
    // 無音検出関連
    public const float SILENCE_THRESHOLD = 0.005f; // 無音判定の閾値（音量レベル）- より寛容に設定
    public const int SILENCE_DURATION_MS = 1000; // 無音継続時間（ミリ秒）- より長く設定
}

/// <summary>
/// Discord Gateway用のJSONオブジェクト作成ヘルパー
/// </summary>
public static class DiscordPayloadHelper {
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

}
/// <summary>
/// エラーハンドリング用のヘルパークラス
/// </summary>
public static class ErrorHandler {
    /// <summary>
    /// 非同期操作を安全に実行し、エラーをログに記録
    /// </summary>
    public static async Task<T> SafeExecuteAsync<T>(Func<Task<T>> operation, string context, Action<string> logCallback) {
        try {
            return await operation();
        } catch (Exception ex) {
            logCallback($"{context} error: {ex.Message}");
            return default(T);
        }
    }
    /// <summary>
    /// 同期操作を安全に実行し、エラーをログに記録
    /// </summary>
    public static T SafeExecute<T>(Func<T> operation, string context, Action<string> logCallback) {
        try {
            return operation();
        } catch (Exception ex) {
            logCallback($"{context} error: {ex.Message}");
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
    public delegate void DiscordBotStateChangedDelegate(bool isRunning);
    public static event DiscordBotStateChangedDelegate OnDiscordBotStateChanged;
    // 接続関連
    private DiscordNetworkManager _networkManager;
    private DiscordVoiceGatewayManager _voiceGatewayManager;
    private DiscordVoiceUdpManager _voiceUdpManager;
    private string _sessionId;
    // Voice Gateway関連
    private UdpClient _voiceUdpClient;
    private string _voiceToken;
    private string _voiceEndpoint;
    private string _voiceSessionId;
    private IPEndPoint _voiceServerEndpoint;
    private Dictionary<uint, string> _ssrcToUserMap = new Dictionary<uint, string>();
    private uint _ourSSRC;
    private byte[] _secretKey;
    // Discord.js準拠の接続データ
    private string _encryptionMode;
    private string[] _availableModes;
    // Discord.js VoiceUDPSocket.ts準拠のKeep Alive
    private System.Timers.Timer _keepAliveTimer;
    private uint _keepAliveCounter = 0;
    private const int KEEP_ALIVE_INTERVAL = DiscordConstants.UDP_SEND_TIMEOUT; // 5秒
    private const uint MAX_COUNTER_VALUE = uint.MaxValue;
    // 音声処理統計
    private static int _opusErrors = 0;
    // 音声処理関連
    private IOpusDecoder _opusDecoder;
    private HttpClient _httpClient;
    // 無音検出によるバッファリング
    private DiscordVoiceNetworkManager _audioBuffer;
    private bool _targetUserSpeaking = false;
    
    // メインスレッドで実行するためのキュー
    private readonly Queue<Action> _mainThreadActions = new Queue<Action>();
    private readonly object _mainThreadActionsLock = new object();
    // 音声認識状態管理
    private struct OpusPacket {
        public byte[] data;
        public string userId;
    }
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    
    /// <summary>
    /// ログメッセージを生成し、イベントを発行します。
    /// </summary>
    private void LogMessage(string message, LogLevel level = LogLevel.Info) {
        if (!enableDebugLogging && level == LogLevel.Debug) return;
        
        string prefix;
        switch (level) {
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
        
        string logMessage = $"[DiscordBot] {DateTime.Now:HH:mm:ss} {prefix} {message}";
        OnDiscordLog?.Invoke(logMessage);
    }

    /// <summary>
    /// エラーログ用のラッパーメソッド（ErrorHandlerとの互換性のため）
    /// </summary>
    private void LogError(string message) {
        LogMessage(message, LogLevel.Error);
    }
    
    /// <summary>
    /// メインスレッドで実行するアクションをキューに追加
    /// </summary>
    private void EnqueueMainThreadAction(Action action) {
        lock (_mainThreadActionsLock) {
            _mainThreadActions.Enqueue(action);
        }
    }
    /// <summary>
    /// 接続状態変更時の処理
    /// </summary>
    private void OnConnectionStateChanged(bool isConnected, string connectionType) {
        LogMessage($"{connectionType} connection state changed: {(isConnected ? "Connected" : "Disconnected")}");
    }
    
    /// <summary>
    /// 音声パケット受信イベントのハンドラー
    /// </summary>
    private void OnAudioPacketReceived(byte[] opusData, uint ssrc, string userId) {
        try {
            // ターゲットユーザーのSSRCかチェック
            if (_ssrcToUserMap.ContainsKey(ssrc)) {
                LogMessage($"DEAD BEEF 1 OnAudioPacketReceived", LogLevel.Debug);
                // 処理済みのOpusデータを直接デコード
                _ = Task.Run(() => ProcessOpusData(opusData, userId));
            }
        } catch (Exception ex) {
            LogMessage($"Audio packet processing error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 処理済みのOpusデータをPCMに変換してAudioBufferに追加
    /// </summary>
    private async Task ProcessOpusData(byte[] opusData, string userId) {
        try {
            // OpusデータをPCMデータに変換
            var pcmData = AdvancedDecodeOpusToPcm(opusData);
            
            if (pcmData != null) {
                LogMessage($"DEAD BEEF 1 ProcessOpusData", LogLevel.Debug);
                // AudioBufferに追加
                _audioBuffer?.AddAudioData(pcmData);
            }
        } catch (Exception ex) {
            LogMessage($"Opus data processing error: {ex.Message}", LogLevel.Error);
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
        
        // AudioBufferのクリーンアップ
        if (_audioBuffer != null) {
            _audioBuffer.OnAudioBufferReady -= OnAudioBufferReady;
            _audioBuffer.ClearBuffer();
            _audioBuffer = null;
        }
        
        StopBot();
    }
    /// <summary>
    /// OpusデコーダーとAudioBufferを初期化します。
    /// 48kHz、ステレオの音声をデコードするように設定されます。
    /// </summary>
    private void InitializeOpusDecoder() {
        ErrorHandler.SafeExecute<bool>(() => {
            _opusDecoder = OpusCodecFactory.CreateDecoder(DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.CHANNELS_STEREO);
            LogMessage("Opus decoder initialized");
            
            // AudioBufferを初期化
            _audioBuffer = new DiscordVoiceNetworkManager(
                DiscordConstants.SILENCE_THRESHOLD,
                DiscordConstants.SILENCE_DURATION_MS,
                DiscordConstants.SAMPLE_RATE_48K,
                DiscordConstants.CHANNELS_STEREO,
                EnqueueMainThreadAction // コールバック関数を渡す
            );
            
            // AudioBufferのイベントハンドラーを設定
            _audioBuffer.OnAudioBufferReady += OnAudioBufferReady;
            
            LogMessage($"AudioBuffer initialized with silence threshold: {DiscordConstants.SILENCE_THRESHOLD}, duration: {DiscordConstants.SILENCE_DURATION_MS}ms");
            return true;
        }, "Opus decoder and AudioBuffer initialization", LogError);
    }
    
    /// <summary>
    /// NetworkManagerを初期化します。
    /// </summary>
    private void InitializeNetworkManager() {
        // 既存のNetworkManagerがあればクリーンアップ
        if (_networkManager != null) {
            _networkManager.Dispose();
            _networkManager = null;
        }
        
        // 既存のVoiceGatewayManagerがあればクリーンアップ
        if (_voiceGatewayManager != null) {
            _voiceGatewayManager.Dispose();
            _voiceGatewayManager = null;
        }
        
        // 既存のVoiceUdpManagerがあればクリーンアップ
        if (_voiceUdpManager != null) {
            _voiceUdpManager.Dispose();
            _voiceUdpManager = null;
        }
        
        _networkManager = new DiscordNetworkManager(enableDebugLogging);
        _voiceGatewayManager = new DiscordVoiceGatewayManager(enableDebugLogging);
        _voiceUdpManager = new DiscordVoiceUdpManager(enableDebugLogging);
        
        // Main Gateway イベントハンドラーを設定
        _networkManager.OnDiscordLog += (message) => LogMessage(message);
        _networkManager.OnMainGatewayMessageReceived += (message) => _ = ProcessDiscordMessage(message);
        _networkManager.OnConnectionStateChanged += OnConnectionStateChanged;
        
        // Voice Gateway イベントハンドラーを設定
        _voiceGatewayManager.OnDiscordLog += (message) => LogMessage(message);
        _voiceGatewayManager.OnConnectionStateChanged += (isConnected) => OnConnectionStateChanged(isConnected, "Voice Gateway");
        
        // Voice Gateway メッセージ処理イベントハンドラーを設定
        _voiceGatewayManager.OnVoiceHelloReceived += async (heartbeatInterval) => await HandleVoiceHello(heartbeatInterval);
        _voiceGatewayManager.OnVoiceReadyReceived += async (ssrc, ip, port, modes) => await HandleVoiceReady(ssrc, ip, port, modes);
        _voiceGatewayManager.OnVoiceSessionDescriptionReceived += async (secretKey, mode) => await HandleVoiceSessionDescription(secretKey, mode);
        _voiceGatewayManager.OnVoiceHeartbeatAckReceived += HandleVoiceHeartbeatAck;
        _voiceGatewayManager.OnVoiceSpeakingReceived += HandleVoiceSpeaking;
        
        // Voice UDP イベントハンドラーを設定
        _voiceUdpManager.OnDiscordLog += (message) => LogMessage(message);
        _voiceUdpManager.OnAudioPacketReceived += OnAudioPacketReceived;
        _voiceUdpManager.OnConnectionStateChanged += (isConnected) => OnConnectionStateChanged(isConnected, "Voice UDP");
        _voiceUdpManager.OnSpeechEndDetected += OnSpeechEndDetected;
        
        LogMessage("NetworkManager, VoiceGatewayManager, and VoiceUdpManager initialized");
    }
    /// <summary>
    /// Unityのライフサイクルメソッド。
    /// フレームごとに呼び出され、Opusパケットキューを処理します。
    /// </summary>
    private void Update() {        
        // メインスレッドアクションの実行
        lock (_mainThreadActionsLock) {
            while (_mainThreadActions.Count > 0) {
                var action = _mainThreadActions.Dequeue();
                action?.Invoke();
            }
        }
    }
    
    /// <summary>
    /// AudioBufferから音声データが準備完了した時の処理
    /// </summary>
    private void OnAudioBufferReady(float[] audioData, int sampleRate, int channels) {
        if (!IsValidAudioData(audioData, out float audioLevel)) {
            LogMessage($"Audio data invalid: {audioData?.Length ?? 0} samples, level={audioLevel:F4}", LogLevel.Debug);
            return;
        }
        
        LogMessage($"Audio ready: {audioData.Length} samples, level={audioLevel:F4}", LogLevel.Debug);
        StartCoroutine(ProcessAudioCoroutine(audioData));
    }

    /// <summary>
    /// 音声データの品質チェック（統合版）
    /// </summary>
    private bool IsValidAudioData(float[] audioData, out float audioLevel) {
        audioLevel = 0f;
        if (audioData == null || audioData.Length == 0) return false;
        
        // 最小長チェック
        if (audioData.Length < DiscordConstants.WITA_API_SAMPLE_RATE / 2) return false;
        
        // 音量レベル計算
        audioLevel = CalculateAudioLevel(audioData);
        return audioLevel > DiscordConstants.SILENCE_THRESHOLD;
    }

    /// <summary>
    /// 音声認識処理（簡素化版）
    /// </summary>
    private IEnumerator ProcessAudioCoroutine(float[] audioData) {
        var task = TranscribeWithWitAI(audioData);
        
        while (!task.IsCompleted) {
            yield return new WaitForSeconds(0.1f);
        }
        
        if (task.IsCompletedSuccessfully && !string.IsNullOrEmpty(task.Result)) {
            OnVoiceRecognized?.Invoke(inputName, task.Result);
        } else if (task.IsFaulted) {
            LogMessage($"Speech recognition error: {task.Exception?.GetBaseException().Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// Discordボットを起動します。
    /// 設定を読み込み、Discord Gatewayへの接続を開始します。
    /// </summary>
    public async void StartBot() {
        if (_networkManager != null && _networkManager.IsMainConnected) {
            LogMessage("⚠️ Bot is already running");
            return;
        }
        
        await ErrorHandler.SafeExecuteAsync<bool>(async () => {
            LoadSettingsFromCentralManager();
            if (string.IsNullOrEmpty(discordToken)) {
                LogMessage("❌ Discord token is not set");
                return false;
            }
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {witaiToken}");
            InitializeOpusDecoder();
            InitializeNetworkManager();
            
            // Discord Gatewayへの接続を試行
            bool connectionSuccess = await ConnectToDiscord();
            if (connectionSuccess) {
                OnDiscordBotStateChanged?.Invoke(true);
                LogMessage("✅ Discord bot started successfully");
            } else {
                LogMessage("❌ Discord bot failed to start - connection failed");
                // 接続に失敗した場合はリソースをクリーンアップ
                _networkManager?.Dispose();
                _networkManager = null;
                _voiceGatewayManager?.Dispose();
                _voiceGatewayManager = null;
                _voiceUdpManager?.Dispose();
                _voiceUdpManager = null;
                _httpClient?.Dispose();
                _httpClient = null;
            }
            return connectionSuccess;
        }, "StartBot", LogError);
    }
    /// <summary>
    /// メインGatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendMessage(string message) {
        await _networkManager.SendMainMessage(message);
    }
    /// <summary>
    /// Voice Gatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendVoiceMessage(string message) {
        await _voiceGatewayManager.SendMessage(message);
    }
    /// <summary>
    /// Voice Gatewayから受信した単一のメッセージペイロードを処理します。
    /// オペレーションコードに基づいて処理を分岐します。
    /// </summary>
    /// <param name="message">受信したJSON形式のメッセージ文字列。</param>

    /// <summary>
    /// Voice GatewayのHelloメッセージを処理
    /// </summary>
    private async Task HandleVoiceHello(double heartbeatInterval) {
        LogMessage($"🔌 Voice Gateway Hello received at {DateTime.Now:HH:mm:ss.fff}");
        await StartVoiceHeartbeat(heartbeatInterval);
        await SendVoiceIdentify();
    }
    /// <summary>
    /// Voice GatewayのReadyメッセージを処理
    /// </summary>
    private async Task HandleVoiceReady(uint ssrc, string ip, int port, string[] modes) {
        LogMessage($"🔌 Voice Gateway Ready received at {DateTime.Now:HH:mm:ss.fff}");
        var readyData = new VoiceReadyData { ssrc = ssrc, ip = ip, port = port, modes = modes };
        await InitializeVoiceConnection(readyData);
        
        // DiscordVoiceUdpManagerでUDP Discoveryを実行
        bool discoverySuccess = await _voiceUdpManager.PerformUdpDiscovery(
            _ourSSRC, 
            _voiceServerEndpoint, 
            _availableModes, 
            async (detectedIP, detectedPort, selectedMode) => {
                return await CompleteUdpDiscovery(detectedIP, detectedPort);
            }
        );
        
        if (!discoverySuccess) {
            LogMessage("❌ WARNING: UDP Discovery failed. Voice may not work.", LogLevel.Warning);
        }
    }
    /// <summary>
    /// Voice GatewayのSession Descriptionメッセージを処理
    /// </summary>
    private async Task HandleVoiceSessionDescription(byte[] secretKey, string mode) {
        LogMessage($"🔌 Voice Gateway Session Description received at {DateTime.Now:HH:mm:ss.fff}");
        _secretKey = secretKey;
        _encryptionMode = mode;
        
        // UDPマネージャーに暗号化キーを設定
        _voiceUdpManager.SetSecretKey(_secretKey);
        _voiceUdpManager.SetEncryptionMode(_encryptionMode);
        
        LogMessage($"🔐 Encryption mode: {_encryptionMode}, Secret key length: {_secretKey?.Length ?? 0} bytes");
        await StartUdpAudioReceive();
    }
    /// <summary>
    /// Voice GatewayのHeartbeat ACKを処理
    /// </summary>
    private void HandleVoiceHeartbeatAck() {
        _voiceGatewayManager.HandleHeartbeatAck();
    }
    /// <summary>
    /// Voice GatewayのSpeakingメッセージを処理（Discord.js準拠）
    /// </summary>
    private void HandleVoiceSpeaking(bool speaking, uint ssrc, string userId) {
        LogMessage($"🎤 Speaking event: user_id={userId}, ssrc={ssrc}, speaking={speaking}, target_user_id={targetUserId}", LogLevel.Info);
        
        if (userId == null) return;
        
        // Discord.js準拠: SSRCマッピングを動的に管理
        _ssrcToUserMap[ssrc] = userId;
        
        if (userId == targetUserId) {
            LogMessage($"DEAD BEEF 2 HandleVoiceSpeaking", LogLevel.Debug);
            if (speaking) {
                LogMessage($"DEAD BEEF 3 HandleVoiceSpeaking", LogLevel.Debug);
                _targetUserSpeaking = true; // ターゲットユーザーの発話開始
                _audioBuffer?.ClearBuffer();
            } else {
                LogMessage($"DEAD BEEF 4 HandleVoiceSpeaking", LogLevel.Debug);
                _targetUserSpeaking = false; // ターゲットユーザーの発話終了
                // 発話終了時にバッファされた音声データを処理
                _audioBuffer?.ProcessBufferedAudio();
            }
        }
        // Discord.js準拠: speaking.endは無視 - 無音検出に任せる
    }

    /// <summary>
    /// 音声接続を初期化
    /// </summary>
    private async Task InitializeVoiceConnection(VoiceReadyData readyData) {
        _ourSSRC = readyData.ssrc;
        _voiceServerEndpoint = new IPEndPoint(IPAddress.Parse(readyData.ip), readyData.port);
        _availableModes = readyData.modes;
        
        // UDPマネージャーにSSRCを設定
        _voiceUdpManager.SetOurSSRC(_ourSSRC);
        
        LogMessage($"🔐 Available encryption modes: [{string.Join(", ", _availableModes)}]");
        await SetupUdpClient();
    }

    /// <summary>
    /// PCMデータの音量レベルを計算（RMS方式）
    /// </summary>
    private float CalculateAudioLevel(float[] pcmData) {
        if (pcmData?.Length == 0) return 0f;
        
        float sum = 0f;
        for (int i = 0; i < pcmData.Length; i++) {
            sum += pcmData[i] * pcmData[i];
        }
        return (float)Math.Sqrt(sum / pcmData.Length);
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
    /// Opusデコーダーのリセット処理（簡素化版）
    /// </summary>
    private void HandleOpusDecoderReset(Exception ex) {
        if (ex.Message.Contains("corrupted") && _opusErrors % 50 == 0) {
            LogMessage($"Resetting Opus decoder after {_opusErrors} errors", LogLevel.Warning);
            _opusDecoder?.Dispose();
            InitializeOpusDecoder();
        }
    }
    /// <summary>
    /// Wit.AI APIを使用して音声データを文字に変換します。
    /// </summary>
    /// <param name="audioData">文字起こしするfloat形式の音声データ。</param>
    /// <returns>認識されたテキスト文字列。</returns>
    private async Task<string> TranscribeWithWitAI(float[] audioData) {
        try {
            
            // 音声データの品質チェック
            if (audioData == null || audioData.Length == 0) {
                return "";
            }
            
            // 0.5秒以上の音声データかチェック（16kHzで8000サンプル）
            int minSamples = DiscordConstants.WITA_API_SAMPLE_RATE / 2; // 0.5秒分
            if (audioData.Length < minSamples) {
                // LogMessage($"🔇 Audio data too short for transcription ({audioData.Length} samples < {minSamples} samples)");
                return "";
            }
            
            // 音声データの音量チェック（無音データの除外）
            float audioLevel = CalculateAudioLevel(audioData);
            if (audioLevel <= DiscordConstants.SILENCE_THRESHOLD) {
                LogMessage($"🔇 Audio level too low for transcription ({audioLevel:F4} <= {DiscordConstants.SILENCE_THRESHOLD})");
                return "";
            }
            
            if (_httpClient == null || string.IsNullOrEmpty(witaiToken))
            {
                // LogMessage("❌ HttpClient is not initialized or witaiToken is missing.");
                return "";
            }
            
            // Node.js準拠: 生のPCMデータに変換（48kHz → 16kHz）
            byte[] rawPcmData = ConvertToRawPcm(audioData, DiscordConstants.WITA_API_SAMPLE_RATE, DiscordConstants.WITA_API_CHANNELS);
            using (var content = new ByteArrayContent(rawPcmData))
            {
                // Node.js準拠のContent-Type
                content.Headers.Add("Content-Type", "audio/raw;encoding=signed-integer;bits=16;rate=16k;endian=little");
                // HTTPリクエストを実行
                var response = await _httpClient.PostAsync("https://api.wit.ai/speech", content, CancellationToken.None);
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
                    // 空のレスポンスの場合はログを出力しない（無駄なログを削減）
                    if (!string.IsNullOrWhiteSpace(jsonResponse)) {
                        LogMessage($"Wit.AI no text found. Response: {jsonResponse}");
                    }
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
    /// UDPのIP Discoveryを完了し、選択した暗号化プロトコルをサーバーに通知します。
    /// </summary>
    /// <param name="detectedIP">検出されたIPアドレス。</param>
    /// <param name="detectedPort">検出されたポート番号。</param>
    /// <returns>成功した場合はtrue、それ以外はfalse。</returns>
    private async Task<bool> CompleteUdpDiscovery(string detectedIP, int detectedPort) {
        var result = await ErrorHandler.SafeExecuteAsync(async () => {
            // DiscordVoiceUdpManagerで暗号化モード選択
            string selectedMode = _voiceUdpManager.ChooseEncryptionMode(_availableModes);
            var selectProtocolData = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateSelectProtocolPayload(detectedIP, detectedPort, selectedMode);
            var jsonData = JsonConvert.SerializeObject(selectProtocolData);
            
            if (!_voiceGatewayManager.IsConnected) {
                LogMessage("❌ Voice Gateway is not connected!");
                return false;
            }
            
            await _voiceGatewayManager.SendMessage(jsonData);
            return true;
        }, "UDP discovery completion", LogError);
        return result;
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
            // 音声受信開始
            _voiceUdpManager.StartReceiveAudio();
        } catch (Exception ex) {
            LogMessage($"❌ UDP audio receive start error: {ex.Message}");
        }
    }

    /// <summary>
    /// 音声受信用にUDPクライアントをセットアップします。
    /// </summary>
    private async Task SetupUdpClientForAudio() {
        await SetupUdpClient(true);
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
        }, "UDP setup", LogError);
    }

    /// <summary>
    /// OpusデータをPCMデータにデコード（オリジナルBOT準拠の簡素化版）
    /// </summary>
    /// <param name="opusData">Opusデータ</param>
    /// <returns>デコードされたPCMデータ（float配列）</returns>
    private float[] AdvancedDecodeOpusToPcm(byte[] opusData) {
        try {
            // 基本検証
            if (opusData == null || opusData.Length < 1) {
                return null; // 静かにスキップ
            }
            
            // オリジナルBOT準拠: シンプルなデコード
            // 固定バッファサイズ（最大60ms at 48kHz）
            int maxFrameSize = 2880; // 60ms at 48kHz
            int safeBufferSize = maxFrameSize * DiscordConstants.CHANNELS_STEREO;
            short[] pcmData = new short[safeBufferSize];
            
            // シンプルなデコード（フレームサイズは自動検出に任せる）
            int decodedSamples = _opusDecoder.Decode(opusData, pcmData, maxFrameSize, false);
            if (decodedSamples <= 0) {
                _opusErrors++;
                
                // エラーが続く場合はデコーダーをリセット
                if (_opusErrors % 10 == 0) {
                    HandleOpusDecoderReset(new Exception($"Decode failed: {decodedSamples}"));
                }

                return null;
            }
            
            // ステレオ→モノラル変換
            short[] monoData = ConvertStereoToMono(pcmData, decodedSamples * DiscordConstants.CHANNELS_STEREO);
            
            // リサンプリング（48kHz→16kHz）
            var result = ResampleAudioData(monoData, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);

            return result;
            
        } catch (Exception ex) {
            LogMessage($"❌ Opus decode exception: {ex.Message}");
            _opusErrors++;
            return null;
        }
    }

    /// <summary>
    /// UDP接続を維持するためのKeep-Aliveパケット送信を定期的に開始します。
    /// </summary>
    private void StartKeepAlive() {
        _voiceUdpManager.StartKeepAlive();
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
    /// Voice Gatewayへのハートビート送信を定期的に開始します。
    /// </summary>
    /// <param name="interval">ハートビートの間隔（ミリ秒）。</param>
    private async Task StartVoiceHeartbeat(double interval) {
        _voiceGatewayManager.StartHeartbeat(interval);
    }

    public void Dispose() {
        DisposeResources();
    }
    private void DisposeResources() {
        StopBot();
        _opusDecoder?.Dispose();
        _opusDecoder = null;
    }

    /// <summary>
    /// Discordに接続
    /// </summary>
    private async Task<bool> ConnectToDiscord() {
        return await _networkManager.ConnectToMainGateway();
    }
    /// <summary>
    /// CentralManagerからDiscord関連の設定を読み込みます。
    /// </summary>
    private void LoadSettingsFromCentralManager() {
        var centralManager = FindObjectOfType<CentralManager>();
        if (centralManager != null) {
            discordToken = centralManager.GetDiscordToken();
            guildId = centralManager.GetDiscordGuildId();
            voiceChannelId = centralManager.GetDiscordVoiceChannelId();
            targetUserId = centralManager.GetDiscordTargetUserId();
            inputName = centralManager.GetDiscordInputName();
            witaiToken = centralManager.GetDiscordWitaiToken();
        }
    }
    /// <summary>
    /// ボットを停止し、すべての接続とリソースをクリーンアップします。
    /// </summary>
    public async void StopBot() {
        if (_networkManager == null) {
            LogMessage("⚠️ Bot is not running");
            return;
        }
        
        LogMessage("🛑 Stopping Discord bot...");
        
        // ボイスチャンネルからログオフ
        if (_networkManager.IsMainConnected) {
            try {
                await LeaveVoiceChannel();
            } catch (Exception ex) {
                LogMessage($"Voice channel leave error: {ex.Message}", LogLevel.Warning);
            }
        }
        
        // NetworkManagerをクリーンアップ
        _networkManager?.Dispose();
        _networkManager = null;
        
        // VoiceGatewayManagerをクリーンアップ
        _voiceGatewayManager?.Dispose();
        _voiceGatewayManager = null;
        
        // VoiceUdpManagerをクリーンアップ
        _voiceUdpManager?.Dispose();
        _voiceUdpManager = null;
        
        ResetBotState();
        OnDiscordBotStateChanged?.Invoke(false);
        
        LogMessage("✅ Discord bot stopped");
    }
    /// <summary>
    /// ボットの状態をリセットします。
    /// </summary>
    private void ResetBotState() {
        _keepAliveTimer?.Stop();
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        
        _voiceUdpClient?.Close();
        _voiceUdpClient?.Dispose();
        _voiceUdpClient = null;
        
        _httpClient?.Dispose();
        _httpClient = null;
        
        _ssrcToUserMap.Clear();
        
        // AudioBufferのクリーンアップ
        if (_audioBuffer != null) {
            _audioBuffer.ClearBuffer();
        }
    }
    /// <summary>
    /// ボットが実行中かどうかを確認します。
    /// </summary>
    /// <returns>ボットが実行中の場合はtrue、それ以外はfalse。</returns>
    public bool IsBotRunning() {
        return _networkManager != null && _networkManager.IsMainConnected;
    }

    /// <summary>
    /// Discordメッセージを処理
    /// </summary>
    private async Task ProcessDiscordMessage(string message) {
        try {
            var payload = JsonConvert.DeserializeObject<DiscordGatewayPayload>(message);
            if (payload.s.HasValue) {
                _networkManager.UpdateMainSequence(payload.s.Value);
            }
            
            switch (payload.op) {
                case 10: await HandleMainHello(payload); break;
                case 0: await HandleDispatchEvent(payload.t, payload.d.ToString()); break;
                case 11: _networkManager.HandleMainHeartbeatAck(); break;
            }
        } catch (Exception ex) {
            LogMessage($"Discord message processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// メインGatewayのHelloメッセージを処理
    /// </summary>
    private async Task HandleMainHello(DiscordGatewayPayload payload) {
        var helloData = JsonConvert.DeserializeObject<HelloData>(payload.d.ToString());
        _networkManager.StartMainHeartbeat(helloData.heartbeat_interval);
        await SendIdentify();
    }

    /// <summary>
    /// Voice GatewayにIdentifyペイロードを送信し、音声セッションを確立します。
    /// </summary>
    private async Task SendVoiceIdentify() {
        LogMessage($"🔌 Voice Gateway sending Identify at {DateTime.Now:HH:mm:ss.fff}");
        var identify = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateVoiceIdentifyPayload(guildId, botUserId, _voiceSessionId, _voiceToken);
        await SendVoiceMessage(JsonConvert.SerializeObject(identify));
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

                return ip;
            }
        }, "Primary IP detection", LogError) ?? ErrorHandler.SafeExecute(() => {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) {
                    return ip.ToString();
                }
            }
            return null;
        }, "Fallback IP detection", LogError) ?? "192.168.1.1";
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
            await _voiceGatewayManager.Connect(_voiceEndpoint);
            return true;
        }, "Voice connection", LogError);
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
    /// 指定されたボイスチャンネルに参加するためのリクエストを送信します。
    /// </summary>
    private async Task JoinVoiceChannel() {
        var voiceStateUpdate = DiscordPayloadHelper.CreateVoiceStateUpdatePayload(guildId, voiceChannelId);
        await SendMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }
    
    /// <summary>
    /// ボイスチャンネルからログオフするためのリクエストを送信します。
    /// </summary>
    private async Task LeaveVoiceChannel() {
        var voiceStateUpdate = DiscordPayloadHelper.CreateVoiceStateUpdatePayload(guildId, null);
        await SendMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }

    /// <summary>
    /// UDP受信タイムアウトによる発話終了検出
    /// </summary>
    private void OnSpeechEndDetected() {
        if (_targetUserSpeaking) {
            LogMessage($"🔇 Speech end detected via UDP timeout", LogLevel.Info);
            // _targetUserSpeaking = false;
            // 発話終了時にバッファされた音声データを処理
            _audioBuffer?.ProcessBufferedAudio();
        }
    }
}

/// <summary>
/// 無音検出による音声バッファリングクラス
/// </summary>
public class DiscordVoiceNetworkManager {
    private List<float[]> audioChunks = new List<float[]>();
    private float silenceThreshold;
    private int silenceDurationMs = 0; // 無音継続時間（ミリ秒）
    private int sampleRate;
    private int channels;
    
    public delegate void AudioBufferReadyDelegate(float[] audioData, int sampleRate, int channels);
    public event AudioBufferReadyDelegate OnAudioBufferReady;
    
    private readonly Action<Action> _enqueueMainThreadAction;

    public DiscordVoiceNetworkManager(float silenceThreshold, int silenceDurationMs, int sampleRate, int channels, Action<Action> enqueueMainThreadAction) {
        this.silenceThreshold = silenceThreshold;
        this.silenceDurationMs = silenceDurationMs;
        this.sampleRate = sampleRate;
        this.channels = channels;
        this._enqueueMainThreadAction = enqueueMainThreadAction;
    }
    
    /// <summary>
    /// 音声データをバッファに追加
    /// </summary>
    public void AddAudioData(float[] pcmData) {
        Debug.Log("DEAD BEEF AddAudioData: 1");
        if (pcmData == null || pcmData.Length == 0) return;
        
        // 音声レベルを計算
        float audioLevel = CalculateAudioLevel(pcmData);
        Debug.Log("DEAD BEEF AddAudioData: 2");
        bool isSilent = audioLevel < silenceThreshold;
        Debug.Log("DEAD BEEF AddAudioData: 3");
        
        // 音声データをバッファに追加
        audioChunks.Add(pcmData);
        Debug.Log("DEAD BEEF AddAudioData: 4");
        
        // PCMデータの実際の時間を計算
        int pcmDurationMs = (int)((float)pcmData.Length / sampleRate * 1000);
        Debug.Log($"DEAD BEEF AddAudioData: 4.5 - PCMデータ長: {pcmData.Length}サンプル, 時間: {pcmDurationMs}ms");
        
        // 無音状態の更新
        if (!isSilent) {
            // 音声が検出された - 無音時間をリセット
            silenceDurationMs = 0;
            Debug.Log("DEAD BEEF AddAudioData: 5 - 音声検出、無音時間リセット");
        } else {
            // 無音が検出された - 無音時間を加算
            silenceDurationMs += pcmDurationMs;
            Debug.Log($"DEAD BEEF AddAudioData: 6 - 無音継続中: {silenceDurationMs}ms");
            
            // 無音が1000ms以上続いたら処理
            if (silenceDurationMs >= 1000) {
                Debug.Log("DEAD BEEF AddAudioData: 7 - 無音1000ms達成、処理開始");
                ProcessBufferedAudio(); // 無言になるとすぐにパケットが送信されなくなるので、PushToTalkで以外はここに処理が及ぶことはない
                silenceDurationMs = 0; // リセット
            }
        }
    }
    /// <summary>
    /// バッファされた音声データを処理
    /// </summary>
    public void ProcessBufferedAudio() {
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 1");
        if (audioChunks.Count == 0) return;
        
        // 全チャンクの合計サンプル数を計算
        int totalSamples = audioChunks.Sum(chunk => chunk.Length);
        // 最小バッファサイズチェック（0.5秒分）
        int minSamples = sampleRate / 2; // 0.5秒分
        if (totalSamples < minSamples) {
            // 小さすぎるバッファは処理しない
            return;
        }
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 2");

        
        // 結合された音声データを作成
        float[] combinedAudio = new float[totalSamples];
        int currentIndex = 0;
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 3");
        foreach (var chunk in audioChunks) {
            Array.Copy(chunk, 0, combinedAudio, currentIndex, chunk.Length);
            currentIndex += chunk.Length;
        }
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 4");
        // イベントを発火（メインスレッドで実行）
        if (OnAudioBufferReady != null) {
            _enqueueMainThreadAction(() => {
                OnAudioBufferReady.Invoke(combinedAudio, sampleRate, channels);
            });
        }
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 5");
        // バッファをクリア
        audioChunks.Clear();
        Debug.Log("DEAD BEEF ProcessBufferedAudio: 6");
    }
    
    /// <summary>
    /// 音声レベルを計算
    /// </summary>
    private float CalculateAudioLevel(float[] pcmData) {
        if (pcmData == null || pcmData.Length == 0) return 0f;
        
        float sum = 0f;
        for (int i = 0; i < pcmData.Length; i++) {
            sum += pcmData[i] * pcmData[i];  // RMS方式（二乗平均平方根）
        }
        
        return (float)Math.Sqrt(sum / pcmData.Length);
    }
    
    /// <summary>
    /// バッファをクリア
    /// </summary>
    public void ClearBuffer() {
        audioChunks.Clear();
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