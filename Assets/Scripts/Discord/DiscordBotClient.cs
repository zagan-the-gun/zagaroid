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
    // ネットワーク関連（共通使用）
    public const int WEBSOCKET_BUFFER_SIZE = 4096;
    // タイムアウト関連（共通使用）
    public const int RECONNECT_DELAY = 5000;
    // 音声処理関連
    public const int SAMPLE_RATE_48K = 48000;
    public const int SAMPLE_RATE_16K = 16000;
    public const int CHANNELS_STEREO = 2;
    public const float PCM_SCALE_FACTOR = 32768.0f;
    // 音声認識関連
    public const int WITA_API_SAMPLE_RATE = 16000;
    public const int WITA_API_CHANNELS = 1;
    // Discord Gateway関連
    public const int DISCORD_INTENTS = 32509;
    // 無音検出関連
    public const float SILENCE_THRESHOLD = 0.0005f; // 無音判定の閾値（音量レベル）- 発話冒頭欠けを防ぐため更に下げた
    public const int SILENCE_DURATION_MS = 1000; // 無音継続時間（ミリ秒）- より長く設定
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
    public bool enableDebugLogging = false; // ログ削減のためデフォルトを無効に
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
    // Voice Gateway関連
    private string _voiceToken;
    private string _voiceEndpoint;
    private string _voiceSessionId;
    private IPEndPoint _voiceServerEndpoint;
    private uint _ourSSRC;
    private byte[] _secretKey;
    // Discord.js準拠の接続データ
    private string _encryptionMode;
    private string[] _availableModes;
    // Discord.js VoiceUDPSocket.ts準拠のKeep Alive は UDP マネージャー側で実装
    // 音声処理統計
    private static int _opusErrors = 0;
    // 音声処理関連
    private IOpusDecoder _opusDecoder;
    private readonly object _opusDecodeLock = new object();
    private HttpClient _httpClient;
    // 無音検出によるバッファリング
    private DiscordVoiceNetworkManager _audioBuffer;
    private bool _targetUserSpeaking = false;
    
    // 役割集約により、プレロールはUDP側に移譲（このクラスでは保持しない）
    
    // メインスレッドで実行するためのキュー
    private readonly Queue<Action> _mainThreadActions = new Queue<Action>();
    private readonly object _mainThreadActionsLock = new object();
    // 音声認識状態管理
    
    // PCMデバッグ機能
    [Header("PCM Debug Settings")]
    public bool enablePcmDebug = false; // PCMデバッグの有効/無効
    
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
            // UDP層でuserId付与済み。ここでは対象ユーザのみ処理
            if (!string.IsNullOrEmpty(userId) && userId == targetUserId) {
                _ = Task.Run(async () => {
                    try {
                        var pcmData = DecodeOpusToPcm(opusData);
                        if (pcmData != null) {
                            _audioBuffer?.AddAudioData(pcmData);
                        }
                    } catch (Exception ex) {
                        LogMessage($"Opus data processing error: {ex.Message}", LogLevel.Error);
                    }
                });
            }
        } catch (Exception ex) {
            LogMessage($"Audio packet processing error: {ex.Message}", LogLevel.Error);
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
                DiscordConstants.WITA_API_SAMPLE_RATE, // 16kHz
                DiscordConstants.WITA_API_CHANNELS,    // モノラル
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
        _networkManager.OnHelloReceived += async (interval) => {
            // Hello受信時は Identify を送信（HB開始は NetworkManager 内で実行済み）
            await SendIdentify();
        };
        _networkManager.OnDispatchReceived += async (eventType, dataJson) => {
            await HandleDispatchEvent(eventType, dataJson);
        };
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
        
        // PCMデバッグ：複合されたPCMデータを再生
        PlayPcmForDebug(audioData, "Combined Audio");
        
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
        bool isValid = audioLevel > DiscordConstants.SILENCE_THRESHOLD;
        
        // 🔧 デバッグ: 音量レベルをログ出力（発話冒頭欠けの調査用）
        LogMessage($"VOICE_VOLUME: Audio level={audioLevel:F6}, threshold={DiscordConstants.SILENCE_THRESHOLD:F6}, valid={isValid}", LogLevel.Debug);
        
        return isValid;
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
    /// Voice Gatewayにメッセージを送信します。
    /// </summary>
    /// <param name="message">送信するJSON文字列。</param>
    private async Task SendVoiceMessage(string message) {
        await _voiceGatewayManager.SendMessage(message);
    }

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
        
        // SSRCマッピングはUDP層で一元管理
        _voiceUdpManager?.SetSSRCMapping(ssrc, userId);
        
        if (userId == targetUserId) {
            LogMessage($"DEAD BEEF 2 HandleVoiceSpeaking", LogLevel.Debug);
            if (speaking) {
                LogMessage($"DEAD BEEF 3 HandleVoiceSpeaking", LogLevel.Debug);
                _targetUserSpeaking = true; // ターゲットユーザーの発話開始

                // プレロールのフラッシュはUDP層で実施済み
            } else {
                LogMessage($"DEAD BEEF 4 HandleVoiceSpeaking", LogLevel.Debug);
                _targetUserSpeaking = false; // ターゲットユーザーの発話終了
                // 発話終了時にバッファされた音声データを処理
                _audioBuffer?.ProcessBufferedAudio();
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
        
        // UDPマネージャーにSSRCを設定
        _voiceUdpManager.SetOurSSRC(_ourSSRC);
        
        LogMessage($"🔐 Available encryption modes: [{string.Join(", ", _availableModes)}]");
        // DiscordVoiceUdpManagerに委譲
        await _voiceUdpManager.SetupUdpClient(_voiceServerEndpoint, false);
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
            // 最低限の品質チェック（長さ・音量・クライアント準備）
            if (audioData == null) return "";
            int minSamples = DiscordConstants.WITA_API_SAMPLE_RATE / 2;
            if (audioData.Length < minSamples) return "";
            float audioLevel = CalculateAudioLevel(audioData);
            if (audioLevel <= DiscordConstants.SILENCE_THRESHOLD) return "";
            if (_httpClient == null || string.IsNullOrEmpty(witaiToken)) return "";

            // 送信前のPCMデバッグ（任意）
            PlayPcmForDebug(audioData, $"Pre-Translation (Wit.AI) - Level: {audioLevel:F4}");

            // 16kHz/mono の raw PCM に変換して送信
            byte[] rawPcmData = ConvertToRawPcm(audioData, DiscordConstants.WITA_API_SAMPLE_RATE, DiscordConstants.WITA_API_CHANNELS);
            using (var content = new ByteArrayContent(rawPcmData)) {
                content.Headers.Add("Content-Type", "audio/raw;encoding=signed-integer;bits=16;rate=16k;endian=little");
                var response = await _httpClient.PostAsync("https://api.wit.ai/speech", content, CancellationToken.None);
                if (!response.IsSuccessStatusCode) {
                    LogMessage($"Wit.AI HTTP error: {response.StatusCode} - {response.ReasonPhrase}");
                    return "";
                }

                string payload = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(payload)) return "";

                string text = ParseWitTextFromPayload(payload);
                return text ?? "";
            }
        } catch (OperationCanceledException) {
            return "";
        } catch (Exception ex) {
            LogMessage($"Wit.AI error: {ex.Message}");
            return "";
        }
    }

    private string ParseWitTextFromPayload(string payload) {
        var responses = new List<WitAIResponse>();
        foreach (var part in EnumerateWitResponseParts(payload)) {
            try {
                var item = JsonConvert.DeserializeObject<WitAIResponse>(part);
                if (item != null) responses.Add(item);
            } catch { /* ignore */ }
        }
        var final = responses.FirstOrDefault(r => r.type == "FINAL_UNDERSTANDING" && !string.IsNullOrEmpty(r.text));
        if (!string.IsNullOrEmpty(final?.text)) return final.text;
        var first = responses.FirstOrDefault(r => !string.IsNullOrEmpty(r.text));
        return first?.text;
    }

    private IEnumerable<string> EnumerateWitResponseParts(string payload) {
        if (string.IsNullOrWhiteSpace(payload)) yield break;
        string trimmed = payload.Trim();

        // 1) 連結JSONを '}{' や改行で分割
        var splitByBraces = System.Text.RegularExpressions.Regex.Split(trimmed, "\\}\\s*\\{");
        if (splitByBraces.Length > 1) {
            for (int i = 0; i < splitByBraces.Length; i++) {
                string part = splitByBraces[i];
                if (!part.StartsWith("{")) part = "{" + part;
                if (!part.EndsWith("}")) part = part + "}";
                yield return part;
            }
            yield break;
        }

        // 2) 単純に改行で区切られているケース
        var lines = trimmed.Split(new[] {"\r\n", "\n"}, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1) {
            foreach (var line in lines) yield return line.Trim();
            yield break;
        }

        // 3) 単一JSON
        yield return trimmed;
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
        // DiscordVoiceUdpManagerに委譲
        await _voiceUdpManager.SetupUdpClient(_voiceServerEndpoint, true);
    }

    /// <summary>
    /// OpusデータをPCMデータにデコード（オリジナルBOT準拠の簡素化版）
    /// </summary>
    /// <param name="opusData">Opusデータ</param>
    /// <returns>デコードされたPCMデータ（float配列）</returns>
    private float[] DecodeOpusToPcm(byte[] opusData) {
        try {
            // 基本検証
            if (opusData == null || opusData.Length < 1) {
                return null; // 静かにスキップ
            }
            
            // オリジナルBOT準拠: シンプルなデコード
            // 固定バッファサイズ（最大120ms at 48kHz）
            int maxFrameSize = 5760; // 120ms at 48kHz (安全側)
            int safeBufferSize = maxFrameSize * DiscordConstants.CHANNELS_STEREO;
            short[] pcmData = new short[safeBufferSize];
            
            // シンプルなデコード（フレームサイズは自動検出に任せる）
            // RTP拡張プレアンブル(0xBE,0xDE)が先頭に残っている場合は確定的に除去（12B）
            byte[] inputOpus = opusData;
            if (opusData != null && opusData.Length >= 12 && opusData[0] == 0xBE && opusData[1] == 0xDE) {
                var trimmed = new byte[opusData.Length - 12];
                Array.Copy(opusData, 12, trimmed, 0, trimmed.Length);
                inputOpus = trimmed;
                // Debug: 無効化済み
            }

            // デバッグログ出力は無効化
            int decodedSamples;
            try {
                lock (_opusDecodeLock) {
                    decodedSamples = _opusDecoder.Decode(inputOpus, pcmData, maxFrameSize, false);
                }
            } catch (Exception) {
                // 例外時フォールバック: 先頭に余分なヘッダが含まれている可能性を考慮
                if (inputOpus != null && inputOpus.Length > 12) {
                    var alt = new byte[inputOpus.Length - 12];
                    Array.Copy(inputOpus, 12, alt, 0, alt.Length);
                    short[] pcmAlt = new short[safeBufferSize];
                    lock (_opusDecodeLock) {
                        decodedSamples = _opusDecoder.Decode(alt, pcmAlt, maxFrameSize, false);
                    }
                    if (decodedSamples > 0) {
                        short[] monoAlt = ConvertStereoToMono(pcmAlt, decodedSamples * DiscordConstants.CHANNELS_STEREO);
                        var resultAlt = ResampleAudioData(monoAlt, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);
                        return resultAlt;
                    }
                }
                throw;
            }
            if (decodedSamples <= 0) {
                _opusErrors++;
                
                // エラーが続く場合はデコーダーをリセット
                if (_opusErrors % 10 == 0) {
                    HandleOpusDecoderReset(new Exception($"Decode failed: {decodedSamples}"));
                }
                // フォールバック: 復号結果にDiscord独自ヘッダー相当が含まれている可能性
                if (opusData.Length > 12) {
                    try {
                        var alt = new byte[opusData.Length - 12];
                        Array.Copy(opusData, 12, alt, 0, alt.Length);
                        short[] pcmAlt = new short[safeBufferSize];
                        int decodedAlt;
                        lock (_opusDecodeLock) {
                            decodedAlt = _opusDecoder.Decode(alt, pcmAlt, maxFrameSize, false);
                        }
                        if (decodedAlt > 0) {
                            short[] monoAlt = ConvertStereoToMono(pcmAlt, decodedAlt * DiscordConstants.CHANNELS_STEREO);
                            var resultAlt = ResampleAudioData(monoAlt, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);
                            return resultAlt;
                        }
                    } catch (Exception) {
                        // 何もしない（下で例外を出力）
                    }
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
        
        
        _httpClient?.Dispose();
        _httpClient = null;
        
        // SSRCマッピングはUDP層で管理
        
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
    /// Voice GatewayにIdentifyペイロードを送信し、音声セッションを確立します。
    /// </summary>
    private async Task SendVoiceIdentify() {
        LogMessage($"🔌 Voice Gateway sending Identify at {DateTime.Now:HH:mm:ss.fff}");
        var identify = DiscordVoiceGatewayManager.VoicePayloadHelper.CreateVoiceIdentifyPayload(guildId, botUserId, _voiceSessionId, _voiceToken);
        await SendVoiceMessage(JsonConvert.SerializeObject(identify));
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
        await _networkManager.SendIdentify(discordToken);
    }

    /// <summary>
    /// 指定されたボイスチャンネルに参加するためのリクエストを送信します。
    /// </summary>
    private async Task JoinVoiceChannel() {
        await _networkManager.SendJoinVoiceChannel(guildId, voiceChannelId);
    }

    /// <summary>
    /// ボイスチャンネルからログオフするためのリクエストを送信します。
    /// </summary>
    private async Task LeaveVoiceChannel() {
        await _networkManager.SendLeaveVoiceChannel(guildId);
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
    
    /// <summary>
    /// PCMデバッグ用：音声データを直接再生
    /// </summary>
    private void PlayPcmForDebug(float[] pcmData, string label) {
        if (!enablePcmDebug || pcmData == null || pcmData.Length == 0) return;
        
        // AudioClipを作成
        AudioClip clip = AudioClip.Create($"DebugPCM_{label}", pcmData.Length, 1, 16000, false);
        clip.SetData(pcmData, 0);
        
        // AudioSourceを探して再生
        AudioSource audioSource = FindObjectOfType<AudioSource>();
        if (audioSource == null) {
            // AudioSourceが見つからない場合は新しく作成
            GameObject audioObj = new GameObject("PCM_Debug_AudioSource");
            audioSource = audioObj.AddComponent<AudioSource>();
            audioSource.volume = 1.0f;
            audioSource.spatialBlend = 0.0f; // 2D音声
        }
        
        audioSource.clip = clip;
        audioSource.volume = 1.0f;
        audioSource.Play();
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
        if (pcmData == null || pcmData.Length == 0) return;
        
        // 音声レベルを計算
        float audioLevel = CalculateAudioLevel(pcmData);
        bool isSilent = audioLevel < silenceThreshold;
        
        // 🔧 デバッグ: バッファ追加時の音量レベルをログ出力
        UnityEngine.Debug.Log($"VOICE_BUFFER: Adding audio chunk - level={audioLevel:F6}, threshold={silenceThreshold:F6}, silent={isSilent}, samples={pcmData.Length}");
        
        // 音声データをバッファに追加
        audioChunks.Add(pcmData);
        
        // PCMデータの実際の時間を計算
        int pcmDurationMs = (int)((float)pcmData.Length / sampleRate * 1000);
        
        // 無音状態の更新
        if (!isSilent) {
            // 音声が検出された - 無音時間をリセット
            silenceDurationMs = 0;
        } else {
            // 無音が検出された - 無音時間を加算
            silenceDurationMs += pcmDurationMs;
            
            // 無音が1000ms以上続いたら処理
            if (silenceDurationMs >= 1000) {
                ProcessBufferedAudio(); // 無言になるとすぐにパケットが送信されなくなるので、PushToTalkで以外はここに処理が及ぶことはない
                silenceDurationMs = 0; // リセット
            }
        }
    }

    /// <summary>
    /// バッファされた音声データを処理
    /// </summary>
    public void ProcessBufferedAudio() {
        if (audioChunks.Count == 0) return;
        
        // 全チャンクの合計サンプル数を計算
        int totalSamples = audioChunks.Sum(chunk => chunk.Length);
        // 最小バッファサイズチェック（0.２秒分）
        int minSamples = sampleRate / 5; // 0.2秒分
        if (totalSamples < minSamples) {
            // 小さすぎるバッファは処理しない
            return;
        }
        
        // 結合された音声データを作成
        float[] combinedAudio = new float[totalSamples];
        int currentIndex = 0;
        foreach (var chunk in audioChunks) {
            Array.Copy(chunk, 0, combinedAudio, currentIndex, chunk.Length);
            currentIndex += chunk.Length;
        }
        
        // イベントを発火（メインスレッドで実行）
        if (OnAudioBufferReady != null) {
            _enqueueMainThreadAction(() => {
                OnAudioBufferReady.Invoke(combinedAudio, sampleRate, channels);
            });
        }
        // バッファをクリア
        audioChunks.Clear();
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