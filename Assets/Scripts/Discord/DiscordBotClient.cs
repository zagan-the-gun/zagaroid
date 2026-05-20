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
    public const float SILENCE_THRESHOLD = 0.001f; // 無音判定の閾値（音量レベル）- 発話冒頭欠けを防ぐため更に下げた
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
    private string witaiToken;
    // Bot自身の情報
    private string botUserId;
    // イベント
    // 旧イベント（互換維持用）。speaker情報が無いので新規実装では使用しない想定。
    public delegate void VoiceRecognizedDelegate(string inputName, string recognizedText);
    public static event VoiceRecognizedDelegate OnVoiceRecognized;

    // 新イベント：複数話者対応（actorName付き）
    public delegate void VoiceRecognizedWithActorDelegate(string inputName, string actorName, string recognizedText);
    public static event VoiceRecognizedWithActorDelegate OnVoiceRecognizedWithActor;
    public delegate void DiscordLogDelegate(string logMessage);
    public static event DiscordLogDelegate OnDiscordLog;
    public delegate void DiscordBotStateChangedDelegate(bool isRunning);
    public static event DiscordBotStateChangedDelegate OnDiscordBotStateChanged;
    public delegate void DiscordLoggedInDelegate();
    public static event DiscordLoggedInDelegate OnDiscordLoggedIn;
    // 接続関連
    private DiscordNetworkManager _networkManager;
    private DiscordVoiceGatewayManager _voiceGatewayManager;
    private DiscordVoiceUdpManager _voiceUdpManager;
    // Voice Gateway関連
    private string _voiceToken;
    private string _voiceEndpoint;
    // STTモード（MenZ字幕AI）のキャッシュ（メインスレッドで更新し、他スレッドから参照）
    private static volatile bool s_isMenZMode = false;
    public static bool IsMenZMode() { return s_isMenZMode; } // 互換性のため名前は維持
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
    // 複数話者対応：actor nameごとに音声バッファを管理
    private Dictionary<string, DiscordVoiceNetworkManager> _audioBuffersByActorName = new Dictionary<string, DiscordVoiceNetworkManager>();
    private readonly object _audioBuffersLock = new object();
    // Discord User ID → Actor name マッピング（起動時にキャッシュ）
    private Dictionary<string, string> _discordUserIdToActorName = new Dictionary<string, string>();
    
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
    /// 
    /// 下位ロガー（DiscordNetworkManager / DiscordVoiceGatewayManager / DiscordVoiceUdpManager）が
    /// 既に "[Discord*] HH:mm:ss <prefix> ..." の形でプレフィックスを付けた文字列を
    /// このメソッドへ流し込むため、二重ヘッダー化を避けて素通しする。
    /// 自分自身のログだけ "[DiscordBot]" を被せる。
    /// </summary>
    private void LogMessage(string message, LogLevel level = LogLevel.Info) {
        if (!enableDebugLogging && level == LogLevel.Debug) return;

        if (message != null && message.StartsWith("[Discord")) {
            OnDiscordLog?.Invoke(message);
            return;
        }

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
    private int _audioPacketCount = 0;
    
    private void OnAudioPacketReceived(byte[] opusData, uint ssrc, string discordUserId) {
        try {
            if (string.IsNullOrEmpty(discordUserId)) return;
            
            // Discord User ID → Actor name マッピングから高速lookup
            if (!_discordUserIdToActorName.TryGetValue(discordUserId, out string actorName)) {
                // 登録されていないユーザー（対象外）
                return;
            }
            
            // 連発するパケット系ログは Debug。enableDebugLogging=true のときだけ出る
            _audioPacketCount++;
            if (_audioPacketCount % 100 == 0) {
                LogMessage($"🎧 Audio packet: discordUserId={discordUserId}, actorName={actorName}, ssrc={ssrc}, size={opusData?.Length ?? 0}", LogLevel.Debug);
            }
            
            // Opus → PCM 変換して音声バッファに追加
            _ = Task.Run(async () => {
                try {
                    var pcmData = DecodeOpusToPcm(opusData);
                    if (pcmData != null) {
                        var buffer = GetOrCreateAudioBuffer(actorName);
                        buffer?.AddAudioData(pcmData);
                    }
                } catch (Exception ex) {
                    LogMessage($"Opus data processing error (actor={actorName}): {ex.Message}", LogLevel.Error);
                }
            });
        } catch (Exception ex) {
            LogMessage($"Audio packet processing error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// actor nameに対応する音声バッファを取得または作成
    /// </summary>
    private DiscordVoiceNetworkManager GetOrCreateAudioBuffer(string actorName) {
        if (string.IsNullOrEmpty(actorName)) return null;
        
        lock (_audioBuffersLock) {
            if (!_audioBuffersByActorName.ContainsKey(actorName)) {
                // 新しいバッファを作成
                var buffer = new DiscordVoiceNetworkManager(
                    DiscordConstants.SILENCE_THRESHOLD,
                    DiscordConstants.SILENCE_DURATION_MS,
                    DiscordConstants.WITA_API_SAMPLE_RATE, // 16kHz
                    DiscordConstants.WITA_API_CHANNELS,    // モノラル
                    EnqueueMainThreadAction,
                    actorName // actor nameを渡す
                );
                
                // イベントハンドラーを設定
                buffer.OnAudioBufferReady += OnAudioBufferReady;
                
                _audioBuffersByActorName[actorName] = buffer;
                LogMessage($"🎤 新しい音声バッファを作成: actorName={actorName}", LogLevel.Info);
            }
            return _audioBuffersByActorName[actorName];
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
        
        // AudioBuffersのクリーンアップ（複数話者対応）
        lock (_audioBuffersLock) {
            foreach (var kvp in _audioBuffersByActorName) {
                if (kvp.Value != null) {
                    kvp.Value.OnAudioBufferReady -= OnAudioBufferReady;
                    kvp.Value.ClearBuffer();
                }
            }
            _audioBuffersByActorName.Clear();
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
            
            // AudioBufferは複数話者対応のため、GetOrCreateAudioBuffer()で動的に作成
            LogMessage($"AudioBuffer: 複数話者対応モード（動的作成）");
            return true;
        }, "Opus decoder initialization", LogError);
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
        _voiceGatewayManager.OnVoiceReadyReceived += async (ssrc, ip, port, modes) => await HandleVoiceReady(ssrc, ip, port, modes);
        _voiceGatewayManager.OnVoiceSessionDescriptionReceived += async (secretKey, mode) => await HandleVoiceSessionDescription(secretKey, mode);
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
    /// AudioBufferから音声データが準備完了した時の処理（複数話者対応）
    /// </summary>
    private void OnAudioBufferReady(float[] audioData, int sampleRate, int channels, string actorName) {
        bool isMenZMode = DiscordBotClient.IsMenZMode();
        // 字幕パイプラインの要点。発話単位で1回だけ出るので Info 維持
        LogMessage($"🎤 Audio buffer ready: actorName={actorName}, samples={audioData.Length}, isMenZMode={isMenZMode}", LogLevel.Info);

        if (isMenZMode) {
            // STTモードでもローカルでPCMデバッグ再生できるようにする
            PlayPcmForDebug(audioData, $"Audio ({actorName})");

            EnqueueMainThreadAction(() => {
                if (MultiPortWebSocketServer.Instance != null) {
                    MultiPortWebSocketServer.Instance.SendAudioRecognitionRequest(audioData, actorName, sampleRate);
                } else {
                    // 例外的な状況。WitAIへフォールバック
                    LogMessage("MultiPortWebSocketServerが見つかりません。WitAIにフォールバックします。", LogLevel.Warning);
                    StartCoroutine(ProcessAudioCoroutine(audioData, actorName));
                }
            });
            return;
        }

        LogMessage($"WitAIモードで音声認識を実行します (actor={actorName})", LogLevel.Info);
        PlayPcmForDebug(audioData, $"Audio ({actorName})");
        StartCoroutine(ProcessAudioCoroutine(audioData, actorName));
    }
    

    /// <summary>
    /// 音声認識処理（簡素化版）
    /// </summary>
    private IEnumerator ProcessAudioCoroutine(float[] audioData, string actorName) {
        var task = TranscribeWithWitAI(audioData);
        
        while (!task.IsCompleted) {
            yield return new WaitForSeconds(0.1f);
        }
        
        if (task.IsCompletedSuccessfully && !string.IsNullOrEmpty(task.Result)) {
            // WitAIでも actorName は分かるので、複数話者対応イベントを優先して投げる
            OnVoiceRecognizedWithActor?.Invoke("Discord", actorName, task.Result);

            // 互換維持：旧イベントも投げる（ただしspeaker不明）
            OnVoiceRecognized?.Invoke("Discord", task.Result);
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
        
        // Opusエラーカウンターをリセット（再起動時に新セッション開始）
        _opusErrors = 0;
        
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
            
            // 旧: STT（MenZ字幕AI）時の事前RWC接続は無効化

            // Discord Gatewayへの接続を試行
            bool connectionSuccess = await ConnectToDiscord();
            if (connectionSuccess) {
                EnqueueMainThreadAction(() => OnDiscordBotStateChanged?.Invoke(true));
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
        // VOICE_EVENT 共通プレフィックスで暗号化方式を明示
        LogMessage($"[VOICE_EVENT] encryption_mode={_encryptionMode} secret_key_len={_secretKey?.Length ?? 0}");
        await StartUdpAudioReceive();
    }

    /// <summary>
    /// Voice GatewayのSpeakingメッセージを処理（Discord.js準拠、複数話者対応）
    /// </summary>
    private void HandleVoiceSpeaking(bool speaking, uint ssrc, string userId) {
        if (userId == null) return;
        
        // Discord User ID → Actor name マッピング確認
        if (!_discordUserIdToActorName.TryGetValue(userId, out string actorName)) {
            // 対象外のユーザー
            return;
        }
        
        // 詳細は Debug。要点（VOICE_EVENT）は下で出す
        LogMessage($"🎤 Speaking event: user_id={userId}, actorName={actorName}, ssrc={ssrc}, speaking={speaking}", LogLevel.Debug);

        // SSRCマッピングはUDP層で一元管理（discordUserIdを使用）
        _voiceUdpManager?.SetSSRCMapping(ssrc, userId);

        if (speaking) {
            // 発話開始の要点。pipeline 切り分けに必要なので Info 維持
            LogMessage($"[VOICE_EVENT] actor={actorName} userId={userId} ssrc={ssrc} encryption_mode={_encryptionMode} secret_key_len={_secretKey?.Length ?? 0}");
            
            // 発話検出時に顔を表示
            EnqueueMainThreadAction(() => CentralManager.SetFaceVisible(true));
        } else {
            // 発話終了時にバッファされた音声データを処理
            var buffer = GetOrCreateAudioBuffer(actorName);
            buffer?.ProcessBufferedAudio();
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
            if (_httpClient == null || string.IsNullOrEmpty(witaiToken)) return "";

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

    /// <summary>
    /// Wit.AIのストリーミングレスポンスから最終または最初に得られたテキストを抽出します。
    /// </summary>
    /// <param name="payload">Wit.AIから返却された連結JSONまたは改行区切りJSON文字列。</param>
    /// <returns>抽出したテキスト。見つからない場合はnull。</returns>
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

    /// <summary>
    /// Wit.AIのレスポンス文字列を個々のJSON文字列に分割して列挙します。
    /// </summary>
    /// <param name="payload">連結JSON、改行区切り、または単一JSONの文字列。</param>
    /// <returns>各要素が完全なJSON文字列の列挙。</returns>
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
    /// 実処理はDiscordVoiceUdpManagerに委譲します。
    /// </summary>
    private async Task StartUdpAudioReceive() {
        try {
            await _voiceUdpManager.StartUdpAudioReceive(_voiceServerEndpoint);
        } catch (Exception ex) {
            LogMessage($"❌ UDP audio receive start error: {ex.Message}");
        }
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
                // 例外時: FEC (Forward Error Correction) でデコード試行
                // Opusは前のパケットから失われたフレームを復元できる
                if (inputOpus != null && inputOpus.Length > 0) {
                    try {
                        short[] pcmFec = new short[safeBufferSize];
                        int fecSamples;
                        lock (_opusDecodeLock) {
                            // FECモード: fec=true でデコード試行
                            fecSamples = _opusDecoder.Decode(null, pcmFec, maxFrameSize, true);
                        }
                        if (fecSamples > 0) {
                            short[] monoFec = ConvertStereoToMono(pcmFec, fecSamples * DiscordConstants.CHANNELS_STEREO);
                            var resultFec = ResampleAudioData(monoFec, DiscordConstants.SAMPLE_RATE_48K, DiscordConstants.SAMPLE_RATE_16K);
                            return resultFec;
                        }
                    } catch {
                        // FECも失敗したら無視
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
            _opusErrors++;
            // エラーログは最初の数回のみ
            if (_opusErrors <= 3)
            {
                LogMessage($"❌ Opus decode exception: {ex.Message}");
            }
            return null;
        }
    }

    // Keep-Aliveの開始はDiscordVoiceUdpManagerに集約したため本メソッドは不要

    /// <summary>
    /// マネージドリソースを解放します。
    /// </summary>
    public void Dispose() {
        DisposeResources();
    }

    /// <summary>
    /// Botの停止とデコーダの破棄など、内部リソースをまとめて解放します。
    /// </summary>
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
            witaiToken = centralManager.GetDiscordWitaiToken();
            
            // friend Actorの Discord User ID → Actor name マッピングを構築
            _discordUserIdToActorName.Clear();
            var friendActors = centralManager.GetActors()?.Where(a => a.type == "friend" && !string.IsNullOrEmpty(a.discordUserId)).ToList();
            if (friendActors != null) {
                foreach (var actor in friendActors) {
                    _discordUserIdToActorName[actor.discordUserId] = actor.actorName;
                    LogMessage($"🎯 Friend Actor登録: {actor.actorName} (discordUserId={actor.discordUserId})", LogLevel.Info);
                }
            }
            
            // デバッグ: 設定確認
            int friendCount = _discordUserIdToActorName.Count;
            LogMessage($"🔧 Discord設定: guildId={guildId}, voiceChannelId={voiceChannelId}, friend actors={friendCount}", LogLevel.Info);
            
            // STT（MenZ字幕AI）モードのキャッシュ
            try {
                try {
                    var mode = centralManager.GetDiscordSubtitleMethodString();
                    s_isMenZMode = (mode == "STT" || mode == "MenZ"); // 後方互換
                } catch {
                    s_isMenZMode = (centralManager.GetDiscordSubtitleMethod() == 1);
                }
                UnityEngine.Debug.Log($"[DiscordBot] STTモード: {s_isMenZMode}");
            } catch { s_isMenZMode = false; }
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
        EnqueueMainThreadAction(() => OnDiscordBotStateChanged?.Invoke(false));
        
        LogMessage("✅ Discord bot stopped");
    }

    /// <summary>
    /// ボットの状態をリセットします。
    /// </summary>
    private void ResetBotState() {
        _httpClient?.Dispose();
        _httpClient = null;
        
        // AudioBuffersのクリーンアップ（複数話者対応）
        lock (_audioBuffersLock) {
            foreach (var kvp in _audioBuffersByActorName) {
                kvp.Value?.ClearBuffer();
            }
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
        EnqueueMainThreadAction(() => OnDiscordLoggedIn?.Invoke());
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

        try {
            // 複数話者対応：friend Actorの誰かがVCに在席しているかチェック
            if (voiceStateData != null && !string.IsNullOrEmpty(voiceStateData.user_id)) {
                // このユーザーが監視対象か確認
                if (_discordUserIdToActorName.TryGetValue(voiceStateData.user_id, out string actorName)) {
                    // 指定のボイスチャンネルに居るか
                    if (!string.IsNullOrEmpty(voiceChannelId) && voiceStateData.channel_id == voiceChannelId) {
                        LogMessage($"Friend actor in watched VC: actor={actorName}, channel_id={voiceStateData.channel_id}", LogLevel.Info);
                        EnqueueMainThreadAction(() => CentralManager.SetFaceVisible(true));
                    } else {
                        // 退席 or 別チャンネルへ移動
                        string ch = string.IsNullOrEmpty(voiceStateData.channel_id) ? "(none)" : voiceStateData.channel_id;
                        LogMessage($"Friend actor not in watched VC: actor={actorName} (current={ch})", LogLevel.Info);
                        // 注：複数話者の場合、他のActorがいる可能性があるので顔を隠さない
                    }
                }
            }
        } catch (Exception ex) {
            LogMessage($"VoiceState presence check error: {ex.Message}", LogLevel.Warning);
        }
    }
    
    /// <summary>
    /// VOICE_SERVER_UPDATEイベントを処理
    /// </summary>
    private async Task HandleVoiceServerUpdateEvent(string data) {
        var voiceServerData = JsonConvert.DeserializeObject<VoiceServerData>(data);
        _voiceToken = voiceServerData.token;
        _voiceEndpoint = voiceServerData.endpoint;
        if (!string.IsNullOrEmpty(_voiceToken) && !string.IsNullOrEmpty(_voiceEndpoint) && !string.IsNullOrEmpty(_voiceSessionId)) {
            // Voice Gateway に Identify 情報を事前設定（Hello 直後に自動 Identify 送信される）
            _voiceGatewayManager.SetIdentity(guildId, botUserId, _voiceSessionId, _voiceToken);
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
    /// メインGatewayにIdentifyペイロードを送信します。
    /// </summary>
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
    /// UDP受信タイムアウトによる発話終了検出（話者特定版）
    /// </summary>
    private void OnSpeechEndDetected(uint ssrc, string discordUserId) {
        string actorName = null;
        if (!string.IsNullOrEmpty(discordUserId)) {
            _discordUserIdToActorName.TryGetValue(discordUserId, out actorName);
        }

        // 発話終了の要点。発話単位で1回。actor が解決できない場合は警告を兼ねる
        if (!string.IsNullOrEmpty(actorName)) {
            LogMessage($"🔇 Speech end detected via UDP timeout: ssrc={ssrc}, discordUserId={discordUserId}, actor={actorName}", LogLevel.Info);
            lock (_audioBuffersLock) {
                if (_audioBuffersByActorName.TryGetValue(actorName, out var buffer)) {
                    buffer?.ProcessBufferedAudio();
                } else {
                    LogMessage($"⚠️ Speech end: actorName='{actorName}' に対応する音声バッファが存在しません（未発話状態でtimeoutした可能性）", LogLevel.Warning);
                }
            }
        } else {
            // discordUserId が ActorConfig に未登録 = 監視対象外なので Debug 扱い
            LogMessage($"Speech end (unmapped): ssrc={ssrc}, discordUserId={discordUserId}", LogLevel.Debug);
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
/// 無音検出による音声バッファリングクラス（複数話者対応）
/// </summary>
public class DiscordVoiceNetworkManager {
    private List<float[]> audioChunks = new List<float[]>();
    private float silenceThreshold;
    private int silenceDurationMs = 0; // 無音継続時間（ミリ秒）
    private int sampleRate;
    private int channels;
    private string actorName; // 話者識別用
    public delegate void AudioBufferReadyDelegate(float[] audioData, int sampleRate, int channels, string actorName);
    public event AudioBufferReadyDelegate OnAudioBufferReady;
    private readonly Action<Action> _enqueueMainThreadAction;

    /// <summary>
    /// 無音検出に基づく音声バッファリングクラスを初期化します。
    /// </summary>
    /// <param name="silenceThreshold">無音と判定する音量レベルの閾値。</param>
    /// <param name="silenceDurationMs">無音継続時間のしきい値（ミリ秒）。</param>
    /// <param name="sampleRate">内部で扱うサンプルレート。</param>
    /// <param name="channels">チャンネル数。</param>
    /// <param name="enqueueMainThreadAction">メインスレッドでコールバックを実行するためのキュー関数。</param>
    /// <param name="actorName">話者名（Actor名）。</param>
    public DiscordVoiceNetworkManager(float silenceThreshold, int silenceDurationMs, int sampleRate, int channels, Action<Action> enqueueMainThreadAction, string actorName) {
        this.silenceThreshold = silenceThreshold;
        this.silenceDurationMs = silenceDurationMs;
        this.sampleRate = sampleRate;
        this.channels = channels;
        this._enqueueMainThreadAction = enqueueMainThreadAction;
        this.actorName = actorName;
    }
    
    /// <summary>
    /// 音声データをバッファに追加
    /// </summary>
    // リップシンク通知の定数
    private const float LevelScaleTo01 = 4.0f;   // RMS→[0,1]に引き上げる係数
    private const bool EnableVoiceBufferLog = false; // ログ多すぎ防止

    public void AddAudioData(float[] pcmData) {
        if (pcmData == null || pcmData.Length == 0) return;

        float audioLevel = DiscordVoiceNetworkManager.CalculateAudioLevel(pcmData);
        bool isSilent = audioLevel < silenceThreshold;

        float normalizedLevel = UnityEngine.Mathf.Clamp01(audioLevel * LevelScaleTo01);

        if (_enqueueMainThreadAction != null) {
            _enqueueMainThreadAction(() => CentralManager.SendLipSyncLevel(normalizedLevel, actorName));
        } else {
            CentralManager.SendLipSyncLevel(normalizedLevel, actorName);
        }

        // ノイズが極端に多くなるため、必要なときだけ EnableVoiceBufferLog=true にして調査する
        if (EnableVoiceBufferLog) {
            UnityEngine.Debug.Log($"VOICE_BUFFER: actor={actorName}, level={audioLevel:F6}, threshold={silenceThreshold:F6}, silent={isSilent}, samples={pcmData.Length}");
        }

        audioChunks.Add(pcmData);

        int pcmDurationMs = (int)((float)pcmData.Length / sampleRate * 1000);

        if (!isSilent) {
            silenceDurationMs = 0;
        } else {
            silenceDurationMs += pcmDurationMs;

            // 無音が1000ms以上続いたら処理。Push-to-Talk 以外では Discord が無音時にパケットを送らないため、
            // この経路はあまり通らない（メインの発火経路は Speaking=false / UDP timeout 経由、docs/integrations/discord.md § 7.1）
            if (silenceDurationMs >= 1000) {
                ProcessBufferedAudio();
                silenceDurationMs = 0;
            }
        }
    }

    /// <summary>
    /// バッファされた音声データを処理
    /// </summary>
    public void ProcessBufferedAudio() {
        if (audioChunks.Count == 0) return;

        int totalSamples = audioChunks.Sum(chunk => chunk.Length);
        // 最小バッファサイズチェック（0.2秒分）。docs/integrations/discord.md § 7.2 参照
        int minSamples = sampleRate / 5;

        if (totalSamples < minSamples) {
            // 短すぎる発話は STT に投げても認識されないためスキップ。
            // ここで Clear せずに return しているのは、次に音声が積まれてきた時に結合して再評価するため（意図）
            return;
        }

        float[] combinedAudio = new float[totalSamples];
        int currentIndex = 0;
        foreach (var chunk in audioChunks) {
            Array.Copy(chunk, 0, combinedAudio, currentIndex, chunk.Length);
            currentIndex += chunk.Length;
        }

        if (OnAudioBufferReady != null) {
            _enqueueMainThreadAction(() => {
                OnAudioBufferReady.Invoke(combinedAudio, sampleRate, channels, actorName);
            });
        } else {
            // 通常はあり得ない（DiscordBotClient.GetOrCreateAudioBuffer で必ず購読される）
            UnityEngine.Debug.LogWarning($"[DiscordBot] OnAudioBufferReady がバインドされていません: actor={actorName}");
        }
        audioChunks.Clear();
    }
    
    /// <summary>
    /// PCMデータの音量レベル（RMS）を計算します。
    /// </summary>
    /// <param name="pcmData">-1.0〜1.0に正規化されたPCMデータ。</param>
    /// <returns>RMS音量レベル（無効入力時は0）。</returns>
    public static float CalculateAudioLevel(float[] pcmData) {
        if (pcmData == null || pcmData.Length == 0) return 0f;
        float sumOfSquares = 0f;
        for (int i = 0; i < pcmData.Length; i++) {
            float sample = pcmData[i];
            sumOfSquares += sample * sample;
        }
        return (float)Math.Sqrt(sumOfSquares / pcmData.Length);
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
[Serializable] public class ReadyData { public string session_id; public DiscordUser user; }
[Serializable] public class VoiceServerData { public string endpoint; public string token; }
[Serializable] public class VoiceStateData { public string user_id; public string session_id; public string channel_id; }

// Voice Gateway Data Structures

// External API Data Structures
[Serializable] public class WitAIResponse { public string text; public string type; }