using UnityEngine;
// using System.IO;
// using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Generic; // Queue<T> を使うため
using System.Linq; // 文字数計算にLinqを使う場合


public class CentralManager : MonoBehaviour {
    // シングルトンインスタンス
    public static CentralManager Instance { get; private set; }

    [Header("Entrance Sound Settings")]
    [SerializeField] private AudioClip entranceSound; // 初コメ入室音を指定

    private Dictionary<string, int> usersProfile = new Dictionary<string, int>(); // ユーザー名とスタイルIDを保持する辞書

    // 他のマネージャーへの参照 (必要に応じて public で公開)
    // private TranslationManager _translationManager;
    // private TwitchChatIOManager _twitchChatIOManager;
    // private OBSWebSocketIOManager _obsWebSocketIOManager;
    // private TTSWebSocketIOManager _ttsWebSocketIOManager;
    // private VoiceIOManager _voiceIOManager;
    // private SubtitleManager _subtitleManager;

    // アプリケーション全体の設定 (インスペクターから設定可能)
    // [Header("API Keys")]
    // public string DeepLApiKey = "";
    // public string TwitchOAuthToken = "";
    // ... 他のAPIキー

    [Header("Global Settings")]
    [Range(0f, 1f)]
    public float MasterVolume = 1f;
    public string DefaultLanguage = "ja";

    private VoiceVoxApiClient _voiceVoxApiClient;
    private DeepLApiClient _deepLApiClient;
    private MultiPortWebSocketServer _webSocketServer;
    private DiscordBotClient _discordBotClient;
    // ... 他のグローバル設定

    // private const int CHARS_PER_SECOND = 4; // 1秒あたりの文字数 (例: 4文字で1秒)
    // private const float MIN_DISPLAY_TIME = 4.0f; // 最低表示時間 (例: 2秒)
    // private const float MAX_DISPLAY_TIME = 8.0f; // 最大表示時間 (例: 30秒)

    private Dictionary<string, Queue<CurrentDisplaySubtitle>> _subtitleQueuesByChannel = new Dictionary<string, Queue<CurrentDisplaySubtitle>>(); // チャンネルごとの日本語字幕キュー
    private Dictionary<string, CurrentDisplaySubtitle> _currentDisplayByChannel = new Dictionary<string, CurrentDisplaySubtitle>(); // チャンネルごとの現在表示中の日本語字幕

    private void Awake() {
        // シングルトンパターンの実装
        if (Instance == null) {
            Instance = this;



            _voiceVoxApiClient = new VoiceVoxApiClient();
            _deepLApiClient = new DeepLApiClient();
            _webSocketServer = MultiPortWebSocketServer.Instance;
            _discordBotClient = FindObjectOfType<DiscordBotClient>();

            // 他のマネージャーのインスタンスを検索してキャッシュ
            // _translationManager = FindObjectOfType<TranslationManager>();
            // _twitchChatIOManager = FindObjectOfType<TwitchChatIOManager>();
            // _obsWebSocketIOManager = FindObjectOfType<OBSWebSocketIOManager>();
            // _ttsWebSocketIOManager = FindObjectOfType<TTSWebSocketIOManager>();
            // _voiceIOManager = FindObjectOfType<VoiceIOManager>();
            // _subtitleManager = FindObjectOfType<SubtitleManager>();

            Debug.Log("CentralManager initialized.");
        } else {
            Debug.LogWarning("Another CentralManager instance detected. Destroying the new one.");
            Destroy(gameObject);
        }
        // Awake() はスクリプトのインスタンスがロードされた直後に一度だけ呼び出されます。
        // Instance がまだ設定されていない場合（最初のインスタンスの場合）、自身を Instance に設定します。
        // シーンを跨いでも CentralManager のインスタンスを維持したい場合は、DontDestroyOnLoad(gameObject); を記述します。
        // 既に Instance が存在する場合（シーン内に複数の CentralManager が存在する場合）、新しいインスタンスを破棄して重複を防ぎます。
        // 他のマネージャーのインスタンスを FindObjectOfType<>() で検索し、メンバ変数にキャッシュしています。これにより、他のマネージャーへのアクセスが容易になります。
    }

    void Start() {
        // Twitchからコメントを受信するイベントを登録
        UnityTwitchChatController.OnTwitchMessageReceived += HandleTwitchMessageReceived;
        // DiscordBotからの音声認識結果を受信するイベントを登録
        DiscordBotClient.OnVoiceRecognized += HandleDiscordVoiceRecognized;
        DiscordBotClient.OnDiscordLog += HandleDiscordLog;
        DiscordBotClient.OnDiscordBotStateChanged += HandleDiscordBotStateChanged;
        // MultiPortWebSocketServerの情報を受信するイベントを登録
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 += HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 += HandleWebSocketMessageFromPort50002;
        } else {
            Debug.LogError("MultiPortWebSocketServer のインスタンスが見つかりません。");
        }

        // アプリケーション終了時のイベントを登録
        Application.quitting += OnApplicationQuitting;

        // 字幕AIの自動起動
        if (GetAutoStartSubtitleAI()) {
            // 少し遅延させてから起動（他のコンポーネントの初期化を待つ）
            StartCoroutine(DelayedSubtitleAIStart());
        }

        // VoiceVoxの自動起動
        if (GetAutoStartVoiceVox()) {
            // 少し遅延させてから起動（他のコンポーネントの初期化を待つ）
            StartCoroutine(DelayedVoiceVoxStart());
        }

        // MenzTranslationの自動起動
        if (GetAutoStartMenzTranslation()) {
            // 少し遅延させてから起動（他のコンポーネントの初期化を待つ）
            StartCoroutine(DelayedMenzTranslationStart());
        }

        // DiscordBotの自動起動
        if (GetAutoStartDiscordBot()) {
            // 少し遅延させてから起動（他のコンポーネントの初期化を待つ）
            StartCoroutine(DelayedDiscordBotStart());
        }
    }

    private void OnApplicationQuitting() {
        Debug.Log("Application quitting - stopping DiscordBot");
        if (_discordBotClient != null) {
            _discordBotClient.StopBot();
        }
    }

    private IEnumerator DelayedSubtitleAIStart() {
        yield return new WaitForSeconds(2f); // 2秒待機
        UnityEngine.Debug.Log("字幕AIの自動起動を開始します");
        StartSubtitleAI();
    }

    private IEnumerator DelayedVoiceVoxStart() {
        yield return new WaitForSeconds(3f); // 3秒待機（字幕AIと少しずらす）
        UnityEngine.Debug.Log("VoiceVoxの自動起動を開始します");
        StartVoiceVox();
    }

    private IEnumerator DelayedMenzTranslationStart() {
        yield return new WaitForSeconds(4f); // 4秒待機（他と少しずらす）
        UnityEngine.Debug.Log("MenzTranslationの自動起動を開始します");
        StartMenzTranslation();
    }

    private IEnumerator DelayedDiscordBotStart() {
        yield return new WaitForSeconds(5f); // 5秒待機（他と少しずらす）
        UnityEngine.Debug.Log("DiscordBotの自動起動を開始します");
        StartDiscordBot();
    }

    private void Update() {
        // 字幕の表示時間処理（チャンネル別）
        if (_currentDisplayByChannel.Count == 0) {
            return;
        }

        // ToList() でコピーして反復中のディクショナリ変更を回避
        foreach (var kvp in _currentDisplayByChannel.ToList()) {
            var channel = kvp.Key;
            var current = kvp.Value;
            if (current == null) {
                continue;
            }

            current.remainingDuration -= Time.deltaTime;

            if (current.remainingDuration <= 0) {
                Debug.Log("日本語字幕の表示時間が終了しました。");
                // OBS側で日本語字幕をクリアする (テキストを空にする)
                SendObsSubtitles(current.japaneseSubtitle, "");
                // 日本語字幕が消えたら、英語字幕も消す
                if (!string.IsNullOrEmpty(current.englishSubtitle)) {
                    SendObsSubtitles(current.englishSubtitle, "");
                }

                // 現在表示をクリア
                _currentDisplayByChannel.Remove(channel);

                // 同チャンネルのキューに次があれば表示を開始
                if (_subtitleQueuesByChannel.TryGetValue(channel, out var queue) && queue.Count > 0) {
                    Debug.Log("キューから次の日本語字幕を表示します。");
                    var nextEntry = queue.Dequeue();
                    startDisplayingJapaneseSubtitle(nextEntry);

                    // 英語字幕が設定されている場合のみ翻訳を実行
                    if (!string.IsNullOrEmpty(nextEntry.englishSubtitle)) {
                        StartCoroutine(translateSubtitle(nextEntry.englishSubtitle, nextEntry.japaneseText));
                    }
                }
            }
        }
    }

    // PlayerPrefs を使った設定の読み書きメソッド
    public string GetSubtitleAIExecutionPath() {
        // 存在しない場合はデフォルト値として空文字列を返す。
        return PlayerPrefs.GetString("SubtitleAIExecutionPath", "");
    }
    public void SetSubtitleAIExecutionPath(string value) {
        PlayerPrefs.SetString("SubtitleAIExecutionPath", value);
    }

    public int GetCharactersPerSecond() {
        // 存在しない場合はデフォルト値として 4 を返す。
        return PlayerPrefs.GetInt("CharactersPerSecond", 4);
    }
    public void SetCharactersPerSecond(int value) {
        PlayerPrefs.SetInt("CharactersPerSecond", value);
    }

    public float GetMinDisplayTime() {
        // 存在しない場合はデフォルト値として 4.0f を返す。
        return PlayerPrefs.GetFloat("MinDisplayTime", 4.0f);
    }
    public void SetMinDisplayTime(float value) {
        PlayerPrefs.SetFloat("MinDisplayTime", value);
    }

    public float GetMaxDisplayTime() {
        // 存在しない場合はデフォルト値として 8.0f を返す。
        return PlayerPrefs.GetFloat("MaxDisplayTime", 8.0f);
    }
    public void SetMaxDisplayTime(float value) {
        PlayerPrefs.SetFloat("MaxDisplayTime", value);
    }

    public string GetMySubtitle() {
        // "MySubtitle"というキーで保存された文字列を読み込む。存在しない場合は空文字列を返す。
        return PlayerPrefs.GetString("MySubtitle", "");
    }
    public void SetMySubtitle(string key) {
        PlayerPrefs.SetString("MySubtitle", key);
    }

    public string GetMyEnglishSubtitle() {
        // "MyEnglishSubtitle"というキーで保存された文字列を読み込む。存在しない場合は空文字列を返す。
        return PlayerPrefs.GetString("MyEnglishSubtitle", "");
    }
    public void SetMyEnglishSubtitle(string key) {
        PlayerPrefs.SetString("MyEnglishSubtitle", key);
    }

    public string GetFriendSubtitle() {
        // "FriendSubtitle"というキーで保存された文字列を読み込む。存在しない場合は空文字列を返す。
        return PlayerPrefs.GetString("FriendSubtitle", "");
    }
    public void SetFriendSubtitle(string key) {
        PlayerPrefs.SetString("FriendSubtitle", key);
    }

    public string GetDeepLApiClientKey() {
        // "DeepLApiClientKey"というキーで保存された文字列を読み込む。存在しない場合は空文字列を返す。
        return PlayerPrefs.GetString("DeepLApiClientKey", "");
    }
    public void SetDeepLApiClientKey(string key) {
        PlayerPrefs.SetString("DeepLApiClientKey", key);
    }

    public string GetObsWebSocketsPassword() {
        // "ObsWebSocketsPassword"というキーで保存された文字列を読み込む。存在しない場合は空文字列を返す。
        return PlayerPrefs.GetString("ObsWebSocketsPassword", "");
    }
    public void SetObsWebSocketsPassword(string password) {
        PlayerPrefs.SetString("ObsWebSocketsPassword", password);
    }

    public bool GetAutoStartSubtitleAI() {
        // "AutoStartSubtitleAI"というキーで保存された値を読み込む。存在しない場合はfalseを返す。
        return PlayerPrefs.GetInt("AutoStartSubtitleAI", 0) == 1;
    }
    public void SetAutoStartSubtitleAI(bool value) {
        PlayerPrefs.SetInt("AutoStartSubtitleAI", value ? 1 : 0);
    }

    public string GetMenZTranslationServerUrl() {
        // "MenZTranslationServerUrl"というキーで保存された文字列を読み込む。存在しない場合はデフォルト値を返す。
        return PlayerPrefs.GetString("MenZTranslationServerUrl", "ws://127.0.0.1:55001");
    }
    public void SetMenZTranslationServerUrl(string url) {
        PlayerPrefs.SetString("MenZTranslationServerUrl", url);
    }

    // Realtime Audio WebSocket (Unity -> SubtitleAI) URL
    public string GetRealtimeAudioWsUrl() {
        return PlayerPrefs.GetString("RealtimeAudioWsUrl", "ws://127.0.0.1:60001");
    }
    public void SetRealtimeAudioWsUrl(string url) {
        PlayerPrefs.SetString("RealtimeAudioWsUrl", url);
    }

    public string GetTranslationMode() {
        // "TranslationMode"というキーで保存された文字列を読み込む。存在しない場合はデフォルト値を返す。
        return PlayerPrefs.GetString("TranslationMode", "deepl");
    }
    public void SetTranslationMode(string mode) {
        PlayerPrefs.SetString("TranslationMode", mode);
    }

    // VoiceVox関連の設定メソッド
    public bool GetAutoStartVoiceVox() {
        // "AutoStartVoiceVox"というキーで保存された値を読み込む。存在しない場合はfalseを返す。
        return PlayerPrefs.GetInt("AutoStartVoiceVox", 0) == 1;
    }
    public void SetAutoStartVoiceVox(bool value) {
        PlayerPrefs.SetInt("AutoStartVoiceVox", value ? 1 : 0);
    }

    public string GetVoiceVoxExecutionPath() {
        // 存在しない場合はデフォルト値として空文字列を返す。
        return PlayerPrefs.GetString("VoiceVoxExecutionPath", "");
    }
    public void SetVoiceVoxExecutionPath(string value) {
        PlayerPrefs.SetString("VoiceVoxExecutionPath", value);
    }

    public bool GetAutoStartMenzTranslation() {
        // 存在しない場合はデフォルト値として false を返す。
        return PlayerPrefs.GetInt("AutoStartMenzTranslation", 0) == 1;
    }
    public void SetAutoStartMenzTranslation(bool value) {
        PlayerPrefs.SetInt("AutoStartMenzTranslation", value ? 1 : 0);
    }

    public string GetMenzTranslationExecutionPath() {
        // 存在しない場合はデフォルト値として空文字列を返す。
        return PlayerPrefs.GetString("MenzTranslationExecutionPath", "");
    }
    public void SetMenzTranslationExecutionPath(string value) {
        PlayerPrefs.SetString("MenzTranslationExecutionPath", value);
    }

    // Discord Bot 設定
    public bool GetAutoStartDiscordBot() {
        return PlayerPrefs.GetInt("AutoStartDiscordBot", 0) == 1;
    }
    public void SetAutoStartDiscordBot(bool value) {
        PlayerPrefs.SetInt("AutoStartDiscordBot", value ? 1 : 0);
    }

    public string GetDiscordToken() {
        return PlayerPrefs.GetString("DiscordToken", "");
    }
    public void SetDiscordToken(string value) {
        PlayerPrefs.SetString("DiscordToken", value);
    }

    public string GetDiscordGuildId() {
        return PlayerPrefs.GetString("DiscordGuildId", "");
    }
    public void SetDiscordGuildId(string value) {
        PlayerPrefs.SetString("DiscordGuildId", value);
    }

    public string GetDiscordVoiceChannelId() {
        return PlayerPrefs.GetString("DiscordVoiceChannelId", "");
    }
    public void SetDiscordVoiceChannelId(string value) {
        PlayerPrefs.SetString("DiscordVoiceChannelId", value);
    }

    public string GetDiscordTextChannelId() {
        return PlayerPrefs.GetString("DiscordTextChannelId", "");
    }
    public void SetDiscordTextChannelId(string value) {
        PlayerPrefs.SetString("DiscordTextChannelId", value);
    }

    public string GetDiscordTargetUserId() {
        return PlayerPrefs.GetString("DiscordTargetUserId", "");
    }
    public void SetDiscordTargetUserId(string value) {
        PlayerPrefs.SetString("DiscordTargetUserId", value);
    }

    public string GetDiscordInputName() {
        return PlayerPrefs.GetString("DiscordInputName", "Discord");
    }
    public void SetDiscordInputName(string value) {
        PlayerPrefs.SetString("DiscordInputName", value);
    }

    // 旧: int保存 (0: WitAI, 1: MenZ) → 新: string保存 ("WitAI" / "MenZ")
    private const string DiscordSubtitleMethodKey = "DiscordSubtitleMethod"; // legacy int
    private const string DiscordSubtitleMethodStrKey = "DiscordSubtitleMethodStr"; // new string

    public int GetDiscordSubtitleMethod() {
        // 互換維持: 既存コード用に残す（内部は新形式から変換）
        string mode = GetDiscordSubtitleMethodString();
        return mode == "MenZ" ? 1 : 0;
    }
    public void SetDiscordSubtitleMethod(int value) {
        // 互換維持: 新形式へ反映
        SetDiscordSubtitleMethodString(value == 1 ? "MenZ" : "WitAI");
    }

    public string GetDiscordSubtitleMethodString() {
        // 新キーがあればそれを返す
        if (PlayerPrefs.HasKey(DiscordSubtitleMethodStrKey)) {
            var v = PlayerPrefs.GetString(DiscordSubtitleMethodStrKey, "WitAI");
            return (v == "MenZ") ? "MenZ" : "WitAI";
        }
        // 旧キーからの移行
        int legacy = PlayerPrefs.GetInt(DiscordSubtitleMethodKey, 0);
        string mapped = legacy == 1 ? "MenZ" : "WitAI";
        PlayerPrefs.SetString(DiscordSubtitleMethodStrKey, mapped);
        return mapped;
    }
    public void SetDiscordSubtitleMethodString(string value) {
        string normalized = (value == "MenZ") ? "MenZ" : "WitAI";
        PlayerPrefs.SetString(DiscordSubtitleMethodStrKey, normalized);
    }

    public string GetDiscordWitaiToken() {
        return PlayerPrefs.GetString("DiscordWitaiToken", "");
    }
    public void SetDiscordWitaiToken(string value) {
        PlayerPrefs.SetString("DiscordWitaiToken", value);
    }



    // 汎用的な外部プログラム起動メソッド
    private void StartExternalProgram(string execPath, string programName) {
        if (string.IsNullOrEmpty(execPath)) {
            UnityEngine.Debug.LogWarning($"{programName}の実行パスが設定されていません");
            return;
        }

        if (!System.IO.File.Exists(execPath)) {
            UnityEngine.Debug.LogError($"{programName}の実行ファイルが見つかりません: {execPath}");
            return;
        }

        try {
            // スクリプトファイル（.sh, .bat）をターミナル/コマンドプロンプトで実行
            if (execPath.EndsWith(".sh") || execPath.EndsWith(".bat") || execPath.EndsWith(".cmd")) {
                var startInfo = new System.Diagnostics.ProcessStartInfo();
                
                #if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                // macOSの場合：ターミナルでスクリプトを実行
                if (execPath.EndsWith(".sh")) {
                    startInfo.FileName = "open";
                    startInfo.Arguments = $"-a Terminal.app \"{execPath}\"";
                } else {
                    // .batや.cmdファイルはmacOSでは直接実行
                    startInfo.FileName = execPath;
                    startInfo.UseShellExecute = true;
                }
                #elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
                // Linuxの場合：gnome-terminalでスクリプトを実行
                if (execPath.EndsWith(".sh")) {
                    startInfo.FileName = "gnome-terminal";
                    startInfo.Arguments = $"-- bash \"{execPath}\"";
                } else {
                    // .batや.cmdファイルはLinuxでは直接実行
                    startInfo.FileName = execPath;
                    startInfo.UseShellExecute = true;
                }
                #else
                // Windowsの場合：コマンドプロンプトで実行
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"{execPath}\"";
                #endif
                
                startInfo.UseShellExecute = true;
                System.Diagnostics.Process.Start(startInfo);
            } else {
                // 通常の実行ファイルの場合
                var startInfo = new System.Diagnostics.ProcessStartInfo();
                startInfo.FileName = execPath;
                startInfo.UseShellExecute = true;
                startInfo.CreateNoWindow = false; // ウィンドウを表示
                System.Diagnostics.Process.Start(startInfo);
            }
            
            UnityEngine.Debug.Log($"{programName}を起動しました: {execPath}");
        } catch (System.Exception e) {
            UnityEngine.Debug.LogError($"{programName}の起動に失敗しました: {e.Message}");
        }
    }

    // 字幕AIの簡単起動メソッド
    public void StartSubtitleAI() {
        StartExternalProgram(GetSubtitleAIExecutionPath(), "字幕AI");
    }

    // VoiceVoxの簡単起動メソッド
    public void StartVoiceVox() {
        StartExternalProgram(GetVoiceVoxExecutionPath(), "VoiceVox");
    }

    // MenzTranslationの簡単起動メソッド
    public void StartMenzTranslation() {
        StartExternalProgram(GetMenzTranslationExecutionPath(), "MenzTranslation");
    }

    // DiscordBotの起動メソッド
    public void StartDiscordBot() {
        if (_discordBotClient == null) {
            Debug.LogError("DiscordBotClient が見つかりません。シーンにDiscordBotClientコンポーネントを追加してください。");
            return;
        }

        Debug.Log("DiscordBot を開始します");
        _discordBotClient.StartBot();
    }

    // DiscordBotの停止メソッド
    public void StopDiscordBot() {
        if (_discordBotClient != null) {
            Debug.Log("DiscordBot を停止します");
            _discordBotClient.StopBot();
        }
    }

    // DiscordBotの実行状態を確認
    public bool IsDiscordBotRunning() {
        return _discordBotClient != null && _discordBotClient.IsBotRunning();
    }

    // すべての PlayerPrefs の変更をディスクに書き込む
    public void SaveAllPlayerPrefs() {
        PlayerPrefs.Save();
        Debug.Log("すべての PlayerPrefs 設定をセーブしました");
    }


    // 他のマネージャーへのアクセス用プロパティ (読み取り専用)
    // public TranslationManager TranslationManager => _translationManager;
    // public TwitchChatIOManager TwitchChatIOManager => _twitchChatIOManager;
    // public OBSWebSocketIOManager OBSWebSocketIOManager => _obsWebSocketIOManager;
    // public TTSWebSocketIOManager TTSWebSocketIOManager => _ttsWebSocketIOManager;
    // public VoiceIOManager VoiceIOManager => _voiceIOManager;
    // public SubtitleManager SubtitleManager => _subtitleManager;
    // 各マネージャーへの参照を読み取り専用のプロパティ (TranslationManager, TwitchChatIOManager など) として公開しています。
    // これにより、他のクラスから CentralManager.Instance.TranslationManager のようにアクセスできます。

    // アプリケーション全体で利用する可能性のある機能 (例: グローバルイベントの発行など)
    public delegate void GlobalMessageDelegate(string message);
    public static event GlobalMessageDelegate OnGlobalMessage;
    public static void SendGlobalMessage(string message) {
        OnGlobalMessage?.Invoke(message);
    }
    // OnGlobalMessage という静的なイベントの例を示しています。これにより、アプリケーションのどこからでもグローバルなメッセージを送信し、他のコンポーネントがそれを購読できます。

    // Twitchコメントへの送信イベントを登録
    public delegate void TwitchCommentSendDelegate(string text);
    public static event TwitchCommentSendDelegate OnTwitchMessageSend;
    public static void SendTwitchMessage(string text) {
        OnTwitchMessageSend?.Invoke(text);
    }
    // Twitchコメントへの送信イベントを登録
    // public delegate void TwitchCommentSendDelegate(string text);
    // public static event TwitchCommentSendDelegate OnTwitchMessageSend;
    // public static void SendTwitchMessage(string text) {
    //     OnTwitchMessageSend?.Invoke(text);
    // }

    // Canvasへの送信イベントを登録
    public delegate void CanvasCommentSendDelegate(string comment);
    public static event CanvasCommentSendDelegate OnCanvasCommentSend;
    public static void SendCanvasMessage(string text) {
        OnCanvasCommentSend?.Invoke(text);
    }

    // OBS字幕への送信イベントを登録
    public delegate void ObsSubtitlesSendDelegate(string subtitle, string subtitleText);
    public static event ObsSubtitlesSendDelegate OnObsSubtitlesSend;
    public static void SendObsSubtitles(string subtitle, string subtitleText) {
        OnObsSubtitlesSend?.Invoke(subtitle, subtitleText);
    }

    // リップシンク用イベント
    public delegate void LipSyncLevelDelegate(float level01);
    public static event LipSyncLevelDelegate OnLipSyncLevel;
    public static void SendLipSyncLevel(float level01) {
        OnLipSyncLevel?.Invoke(level01);
    }

    public delegate void SpeakingChangedDelegate(bool speaking);
    public static event SpeakingChangedDelegate OnSpeakingChanged;
    public static void SendSpeakingChanged(bool speaking) {
        OnSpeakingChanged?.Invoke(speaking);
    }

    void OnDisable() {
        UnityTwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        DiscordBotClient.OnVoiceRecognized -= HandleDiscordVoiceRecognized;
        DiscordBotClient.OnDiscordLog -= HandleDiscordLog;
        DiscordBotClient.OnDiscordBotStateChanged -= HandleDiscordBotStateChanged;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
        }
        
        // DiscordBotの停止処理を追加
        if (_discordBotClient != null) {
            Debug.Log("CentralManager being disabled - stopping DiscordBot");
            _discordBotClient.StopBot();
        }
        
        // アプリケーションが終了する際や、CentralManagerが無効になる際に保存
        SaveAllPlayerPrefs(); 
    }

    void OnDestroy() {
        UnityTwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        DiscordBotClient.OnVoiceRecognized -= HandleDiscordVoiceRecognized;
        DiscordBotClient.OnDiscordLog -= HandleDiscordLog;
        DiscordBotClient.OnDiscordBotStateChanged -= HandleDiscordBotStateChanged;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
        }
        
        // DiscordBotの停止処理を追加
        if (_discordBotClient != null) {
            Debug.Log("CentralManager being destroyed - stopping DiscordBot");
            _discordBotClient.StopBot();
        }
        
        // アプリケーションが終了する際や、CentralManagerが無効になる際に保存
        SaveAllPlayerPrefs(); 
    }

    // Twitchから情報を受け取り、それぞれの処理を実行する
    private void HandleTwitchMessageReceived(string user, string chatMessage) {
        Debug.Log("Twitchから受信しました！: " + chatMessage);

        // 翻訳処理
        // 全文日本語が含まれていなければ翻訳処理に移行
        if (isJapaneseFree(chatMessage)) {
            // 日本語が含まれていない場合の処理
            Debug.Log("翻訳するよ！任せて！");
            // 翻訳処理
            StartCoroutine(translate(user, chatMessage));
        } else {
            // コメント読み上げを開始
            StartCoroutine(speakComment(user, chatMessage));

            // コメントスクロールを開始
            SendCanvasMessage(chatMessage);
        }
    }

    // DiscordBotから音声認識結果を受け取る
    private void HandleDiscordVoiceRecognized(string inputName, string recognizedText) {

        // 字幕用チャンネル名を取得（友達の字幕チャンネルに変更）
        string subtitleChannel = GetFriendSubtitle();
        if (string.IsNullOrEmpty(subtitleChannel)) {
            return; // 設定されていない場合は処理をスキップ
        }

        // 字幕として送信（HandleWebSocketMessageFromPort50001と同じ処理）
        float calculatedDuration = calculateDisplayDuration(recognizedText.Length);
        // string myEnglishSubtitle = GetMyEnglishSubtitle();
        // if (string.IsNullOrEmpty(myEnglishSubtitle)) {
        //     myEnglishSubtitle = "zagan_subtitle_en"; // デフォルト値
        // }

        CurrentDisplaySubtitle newEntry = new CurrentDisplaySubtitle(
            recognizedText,
            subtitleChannel,
            "", // 英語字幕チャンネルを一時的に空文字列に
            calculatedDuration
        );

        manageJapaneseSubtitleDisplay(newEntry);

        // コメント読み上げを開始
        // StartCoroutine(speakComment(inputName, recognizedText));

        // コメントスクロールを開始
        // SendCanvasMessage(recognizedText);
    }

    // DiscordBotからログメッセージを受け取る
    private void HandleDiscordLog(string logMessage) {
        Debug.Log(logMessage);
        // 必要に応じてUIに表示したり、ファイルに保存したりする
        SendGlobalMessage($"[Discord] {logMessage}");
    }

    // DiscordBotの状態変更を受け取る
    private void HandleDiscordBotStateChanged(bool isRunning) {
        Debug.Log($"DiscordBot状態変更: {(isRunning ? "起動" : "停止")}");
        // 必要に応じてUIの更新やその他の処理を行う
    }

    // コメントをVoiceVoxで喋らせる
    private IEnumerator speakComment(string user, string chatMessage) {
        Debug.Log("speakComment");
        // 新しいAudioSourceを生成
        AudioSource speakAudioSource = gameObject.AddComponent<AudioSource>(); // AudioSourceを追加
        if (speakAudioSource == null) {
            Debug.LogError("speakAudioSourceがNULLです");
        }

        // その配信で初めてのコメントかどうかをチェック
        if (!usersProfile.ContainsKey(user)) {
            usersProfile.Add(user, -1); // リストにユーザーを追加
            speakAudioSource.PlayOneShot(entranceSound); // 音を鳴らす
        }

        // 話者が決定していなければここで設定
        if (usersProfile[user] <= -1) {
            // コルーチンの結果を取得するための変数を用意
            int styleId = 3; // デフォルト値を設定
            // コルーチンを実行し、結果を取得
            yield return StartCoroutine(_voiceVoxApiClient.GetSpeakerRnd((result) => styleId = result));
            usersProfile[user] = styleId;
        }

        // テキストからAudioClipを生成
        yield return _voiceVoxApiClient.TextToAudioClip(usersProfile[user], chatMessage);

        if (_voiceVoxApiClient.AudioClip != null) {
            // AudioClipを取得し、AudioSourceにアタッチ
            speakAudioSource.clip = _voiceVoxApiClient.AudioClip;
            // AudioSourceで再生
            speakAudioSource.Play();
            // 再生が終わったらAudioSourceを破棄
            yield return new WaitForSeconds(speakAudioSource.clip.length);
            Destroy(speakAudioSource);
        } else {
            Debug.LogError("読み上げ音声が生成されませんでした");
        }

    }

    // 翻訳を実行
    private IEnumerator translate(string user, string chatMessage) {
        string originalMessage = chatMessage; // 元のメッセージを保持
        string translationMode = GetTranslationMode();
        Debug.Log($"Twitchコメント翻訳方式: {translationMode}");

        bool firstTranslationSucceeded = false;

        if (translationMode == "menz") {
            // MenZ翻訳サーバーを使用
            MenZTranslationClient menZClient = FindObjectOfType<MenZTranslationClient>();
            if (menZClient != null) {
                yield return StartCoroutine(menZClient.PostTranslate(originalMessage, "JA", "", (result) => {
                    if (!string.IsNullOrEmpty(result)) {
                        Debug.Log($"MenZ翻訳結果: {result}");
                        chatMessage = result;
                        firstTranslationSucceeded = true;
                    }
                }));
            }

            // MenZ翻訳に失敗した場合はDeepLにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("MenZ翻訳に失敗しました。DeepLにフォールバックします。");
                yield return StartCoroutine(_deepLApiClient.PostTranslate(originalMessage, "JA", (deepLResult) => {
                    if (!string.IsNullOrEmpty(deepLResult)) {
                        Debug.Log($"DeepL翻訳結果（フォールバック）: {deepLResult}");
                        chatMessage = deepLResult;
                    } else {
                        Debug.LogWarning("DeepL翻訳（フォールバック）も失敗しました。元のメッセージを使用します。");
                        chatMessage = originalMessage;
                    }
                }));
            }
        } else {
            // DeepLを使用（デフォルト）
            yield return StartCoroutine(_deepLApiClient.PostTranslate(originalMessage, "JA", (result) => {
                if (!string.IsNullOrEmpty(result)) {
                    Debug.Log($"DeepL翻訳結果: {result}");
                    chatMessage = result;
                    firstTranslationSucceeded = true;
                }
            }));

            // DeepL翻訳に失敗した場合はMenZにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("DeepL翻訳に失敗しました。MenZ翻訳にフォールバックします。");
                MenZTranslationClient menZClient = FindObjectOfType<MenZTranslationClient>();
                if (menZClient != null) {
                    yield return StartCoroutine(menZClient.PostTranslate(originalMessage, "JA", "", (menZResult) => {
                        if (!string.IsNullOrEmpty(menZResult)) {
                            Debug.Log($"MenZ翻訳結果（フォールバック）: {menZResult}");
                            chatMessage = menZResult;
                        } else {
                            Debug.LogWarning("MenZ翻訳（フォールバック）も失敗しました。元のメッセージを使用します。");
                            chatMessage = originalMessage;
                        }
                    }));
                } else {
                    Debug.LogWarning("MenZTranslationClientが見つかりません。元のメッセージを使用します。");
                    chatMessage = originalMessage;
                }
            }
        }

        // コメント読み上げを開始
        StartCoroutine(speakComment(user, chatMessage));

        // 翻訳文をTwitchコメントに送信
        SendTwitchMessage($"[{user}]: {chatMessage}");

        // コメントスクロールを開始
        SendCanvasMessage(chatMessage);
    }

    // メッセージが日本語を含まないか確認
    private bool isJapaneseFree(string message) {
        // 文字列内の各文字をチェック
        foreach (char c in message) {
            // 日本語の範囲に含まれる文字があるか確認
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter) {
                // 日本語の文字が含まれている場合
                return false; // 日本語が含まれている
            }
        }
        return true; // 日本語が含まれていない
    }

    private void HandleWebSocketMessageFromPort50001(string subtitle, string subtitleText) {
        Debug.Log($"字幕を受信しました！ Subtitle: {subtitle}, Message(raw): {subtitleText}");
        // 字幕をOBSに送信
        // SendObsSubtitles(subtitle, subtitleText);

        // JSON互換: {"text":"..."} 形式ならtextを抽出。失敗時はそのまま使用
        string extractedText = subtitleText;
        string extractedSubtitleName = subtitle;
        // 英語字幕用デフォルト名（JSON無指定時のフォールバック）
        string defaultEnglishSubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetMyEnglishSubtitle() : null;
        string extractedEnglishSubtitleName = defaultEnglishSubtitle;
        try {
            if (!string.IsNullOrEmpty(subtitleText)) {
                string trimmed = subtitleText.TrimStart();
                if (trimmed.StartsWith("{")) {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(subtitleText);
                    var candidate = obj["text"]?.ToString();
                    if (!string.IsNullOrEmpty(candidate)) {
                        extractedText = candidate;
                    }
                    // 複数クライアント対応: JSON内にsubtitle名があれば優先（無ければchannelも許容）
                    var subtitleCandidate = obj["subtitle"]?.ToString();
                    if (string.IsNullOrEmpty(subtitleCandidate)) {
                        subtitleCandidate = obj["channel"]?.ToString();
                    }
                    if (!string.IsNullOrWhiteSpace(subtitleCandidate)) {
                        extractedSubtitleName = subtitleCandidate;
                        // 英語字幕はベース名 + "_en" を使用
                        extractedEnglishSubtitleName = subtitleCandidate + "_en";
                    }
                }
            }
        } catch (System.Exception ex) {
            Debug.LogWarning($"字幕JSON解析に失敗しました。プレーンテキストとして扱います。理由: {ex.Message}");
        }

        // 文字数に応じて日本語字幕表示時間を調整する
        float calculatedDuration = calculateDisplayDuration(extractedText.Length);

        // 新しい日本語字幕エントリを作成
        CurrentDisplaySubtitle newJpEntry = new CurrentDisplaySubtitle(
            extractedText,
            extractedSubtitleName,
            extractedEnglishSubtitleName, // 英語チャンネルも情報として保持
            calculatedDuration
        );

        // 日本語字幕の表示ロジックを管理
        manageJapaneseSubtitleDisplay(newJpEntry);

        // 翻訳字幕の送信
        StartCoroutine(translateSubtitle(extractedEnglishSubtitleName, extractedText));
    }

    // 字幕表示時間を計算するヘルパーメソッド
    private float calculateDisplayDuration(int charCount) {
        // 設定値を取得して利用
        float charsPerSecond = GetCharactersPerSecond();
        float minDisplayTime = GetMinDisplayTime();
        float maxDisplayTime = GetMaxDisplayTime();

        float duration = (float)charCount / charsPerSecond;
        duration = Mathf.Max(duration, minDisplayTime); // durationがminDisplayTimeより小さければminDisplayTimeなる
        return Mathf.Clamp(duration, minDisplayTime, maxDisplayTime); // 最終的にminDisplayTimeからmaxDisplayTimeの間にクランプ
    }

    // 日本語字幕の表示状態を管理するメインロジック
    private void manageJapaneseSubtitleDisplay(CurrentDisplaySubtitle newJpEntry) {
        var channel = newJpEntry.japaneseSubtitle;

        // チャンネル用のキューを確保
        if (!_subtitleQueuesByChannel.TryGetValue(channel, out var queue)) {
            queue = new Queue<CurrentDisplaySubtitle>();
            _subtitleQueuesByChannel[channel] = queue;
        }

        if (!_currentDisplayByChannel.TryGetValue(channel, out var current) || current == null) {
            // 現在表示中の日本語字幕がない場合、すぐに表示
            startDisplayingJapaneseSubtitle(newJpEntry);
            Debug.Log("字幕：新しい日本語字幕をすぐに表示します。");
        } else if (!current.IsCombined) {
            // 同チャンネル内で結合表示
            Debug.Log("字幕：既存の日本語字幕と新しい日本語字幕を結合して表示します。");
            combineAndDisplayJapaneseSubtitles(current, newJpEntry);
        } else {
            // 結合中にさらに新規が来た場合、同チャンネルのキューへ
            Debug.Log("字幕：既に結合表示中またはキューに他の字幕があるため、新しい日本語字幕をキューに追加します。");
            queue.Enqueue(newJpEntry);
        }
    }

    // 実際に日本語字幕の表示を開始するメソッド
    private void startDisplayingJapaneseSubtitle(CurrentDisplaySubtitle jpEntry) {
        // チャンネルごとの現在表示に設定
        _currentDisplayByChannel[jpEntry.japaneseSubtitle] = jpEntry;

        // 日本語字幕をOBSに送信
        SendObsSubtitles(jpEntry.japaneseSubtitle, jpEntry.japaneseText);

        Debug.Log($"日本語字幕表示開始: 『{jpEntry.japaneseText}』, 残り時間: {jpEntry.remainingDuration:F2}秒");
    }

    // 日本語字幕を結合して表示するメソッド
    private void combineAndDisplayJapaneseSubtitles(CurrentDisplaySubtitle existingJp, CurrentDisplaySubtitle newJp) {
        // 日本語字幕の結合 (既存 + 新規)
        string combinedJapaneseText = $"{existingJp.japaneseText}\n{newJp.japaneseText}";

        // 設定値を取得して利用
        float minDisplayTime = GetMinDisplayTime();
        float maxDisplayTime = GetMaxDisplayTime();

        // 新しい表示時間を計算: 既存字幕の残り時間 + 新規字幕の表示時間 (ただし最大時間を超えない)
        float newDuration = existingJp.remainingDuration + newJp.displayDuration;
        newDuration = Mathf.Clamp(newDuration, minDisplayTime, maxDisplayTime);

        // 新しい結合済みCurrentDisplaySubtitleとして設定
        var combined = new CurrentDisplaySubtitle(
            combinedJapaneseText,
            existingJp.japaneseSubtitle, // チャンネルは既存のものを引き継ぐ
            existingJp.englishSubtitle,   // 英語チャンネルも引き継ぐ
            newDuration
        );
        combined.remainingDuration = newDuration; // 残り時間も更新
        combined.SetCombined(true); // 結合状態を設定

        // チャンネルごとの現在表示に反映
        _currentDisplayByChannel[existingJp.japaneseSubtitle] = combined;

        // OBSに送信
        SendObsSubtitles(combined.japaneseSubtitle, combined.japaneseText);

        Debug.Log($"日本語字幕結合表示開始: 『{combined.japaneseText}』, 残り時間: {combined.remainingDuration:F2}秒");
    }

    private void HandleWebSocketMessageFromPort50002(string subtitle, string subtitleText) {
        Debug.Log($"[Port 50002] Subtitle: {subtitle}, Message: {subtitleText}");
        // ポート50002からのメッセージに対する処理
    }

    // 英語字幕の送信
    private IEnumerator translateSubtitle(string subtitle, string subtitleText) {
        Debug.Log("字幕の翻訳開始");
        
        string originalSubtitleText = subtitleText; // 元の字幕テキストを保持
        string translationMode = GetTranslationMode();
        Debug.Log($"翻訳方式: {translationMode}");

        bool firstTranslationSucceeded = false;

        if (translationMode == "menz") {
            // MenZ翻訳サーバーを使用
            MenZTranslationClient menZClient = FindObjectOfType<MenZTranslationClient>();
            if (menZClient != null) {
                yield return StartCoroutine(menZClient.PostTranslate(originalSubtitleText, "EN", "", (result) => {
                    if (!string.IsNullOrEmpty(result)) {
                        Debug.Log($"MenZ翻訳結果: {result}");
                        subtitleText = result;
                        firstTranslationSucceeded = true;
                    }
                }));
            }

            // MenZ翻訳に失敗した場合はDeepLにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("MenZ翻訳に失敗しました。DeepLにフォールバックします。");
                yield return StartCoroutine(_deepLApiClient.PostTranslate(originalSubtitleText, "EN", (deepLResult) => {
                    if (!string.IsNullOrEmpty(deepLResult)) {
                        Debug.Log($"DeepL翻訳結果（フォールバック）: {deepLResult}");
                        subtitleText = deepLResult;
                    } else {
                        Debug.LogWarning("DeepL翻訳（フォールバック）も失敗しました。英語字幕は空欄にします。");
                        subtitleText = "";
                    }
                }));
            }
        } else {
            // DeepLを使用（デフォルト）
            yield return StartCoroutine(_deepLApiClient.PostTranslate(originalSubtitleText, "EN", (result) => {
                if (!string.IsNullOrEmpty(result)) {
                    Debug.Log($"DeepL翻訳結果: {result}");
                    subtitleText = result;
                    firstTranslationSucceeded = true;
                }
            }));

            // DeepL翻訳に失敗した場合はMenZにフォールバック
            if (!firstTranslationSucceeded) {
                Debug.LogWarning("DeepL翻訳に失敗しました。MenZ翻訳にフォールバックします。");
                MenZTranslationClient menZClient = FindObjectOfType<MenZTranslationClient>();
                if (menZClient != null) {
                    yield return StartCoroutine(menZClient.PostTranslate(originalSubtitleText, "EN", "", (menZResult) => {
                        if (!string.IsNullOrEmpty(menZResult)) {
                            Debug.Log($"MenZ翻訳結果（フォールバック）: {menZResult}");
                            subtitleText = menZResult;
                        } else {
                            Debug.LogWarning("MenZ翻訳（フォールバック）も失敗しました。英語字幕は空欄にします。");
                            subtitleText = "";
                        }
                    }));
                } else {
                    Debug.LogWarning("MenZTranslationClientが見つかりません。英語字幕は空欄にします。");
                    subtitleText = "";
                }
            }
        }

        Debug.Log($"翻訳字幕: {subtitleText}");

        // 字幕をOBSに送信
        SendObsSubtitles($"{subtitle}", subtitleText);
    }
}

public class CurrentDisplaySubtitle {
    public string japaneseText; // 表示中の日本語字幕テキスト
    public string japaneseSubtitle; // 日本語字幕のOBSソース名 (例: "zagan_subtitle")
    public string englishSubtitle; // 英語字幕のOBSソース名 (例: "zagan_subtitle_en") // 英語字幕チャンネルも保持
    public float displayDuration; // この日本語字幕の表示時間（秒）
    public float remainingDuration; // この日本語字幕の残り表示時間（秒）
    public bool IsCombined { get; private set; } = false; // 結合状態を管理

    public CurrentDisplaySubtitle(string jpText, string jpSubtitle, string enSubtitle, float duration) {
        japaneseText = jpText;
        japaneseSubtitle = jpSubtitle;
        englishSubtitle = enSubtitle; // 英語チャンネルもここで設定
        displayDuration = duration;
        remainingDuration = duration;
        IsCombined = false; // 初期状態は非結合
    }
    
    /// <summary>
    /// 結合状態を設定
    /// </summary>
    public void SetCombined(bool combined) {
        IsCombined = combined;
    }
}

// Twitch        コメント受信   イベント セントラルマネージャーがイベントを受け取り各処理 OK
// Twitch        コメント送信   イベント セントラルマネージャーがイベントを送信 OK
// VoiceVox      音声生成    メソッド セントラルマネージャーがVoiceVoxのメソッドを直接実行する OK
// VoiceVox      話者取得    メソッド セントラルマネージャーがVoiceVoxのメソッドを直接実行する OK
// キャンバス       ニコニコ風   イベント セントラルマネージャーがイベントを送信 OK
// 翻訳API　       翻訳        メソッド セントラルマネージャが翻訳APIのメソッドを直接参照する OK
// YukariWhisper 自動字幕    イベント　セントラルマネージャーがイベントを受け取り各処理 OK
// OBS           字幕表示    イベント セントラルマネージャーがイベントを送信 OK
// DiscordBOT　　　　　　　音声ストリーム 
