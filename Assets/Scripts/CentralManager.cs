using UnityEngine;
// using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Generic; // Queue<T> を使うため
using System.Linq; // 文字数計算にLinqを使う場合


public class CentralManager : MonoBehaviour {
    // --- 顔表示制御（他クラスはCentralManager経由で購読） ---
    public static event System.Action<bool> FaceVisibilityChanged; // true=表示, false=非表示
    public static void SetFaceVisible(bool visible) {
        FaceVisibilityChanged?.Invoke(visible);
    }
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

    // 旧：字幕表示ロジックは SubtitleController に移行

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
        // 顔表示制御に必要な最小通知のみBOTから受けてCentralManagerでハンドル
        DiscordBotClient.OnDiscordBotStateChanged += HandleDiscordBotStateChanged;
        DiscordBotClient.OnDiscordLoggedIn += HandleDiscordLoggedIn;
        // MultiPortWebSocketServerの情報を受信するイベントを登録
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 += HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 += HandleWebSocketMessageFromPort50002;
            MultiPortWebSocketServer.OnWipeMessageReceived += HandleWipeMessageReceived;
        } else {
            Debug.LogError("MultiPortWebSocketServer のインスタンスが見つかりません。");
        }

        // SubtitleController の自動生成は行わない（TranslationController と同様、シーン配置前提）

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

    private void Update() {}

    // PlayerPrefs を使った設定の読み書きメソッド
    private const string ActorsKey = "Actors";

    public List<ActorConfig> GetActors() {
        try {
            var json = PlayerPrefs.GetString(ActorsKey, "");
            if (string.IsNullOrEmpty(json)) return new List<ActorConfig>();
            var list = JsonConvert.DeserializeObject<List<ActorConfig>>(json);
            return list ?? new List<ActorConfig>();
        } catch {
            return new List<ActorConfig>();
        }
    }

    public void SetActors(List<ActorConfig> actors) {
        try {
            var json = JsonConvert.SerializeObject(actors ?? new List<ActorConfig>());
            PlayerPrefs.SetString(ActorsKey, json);
        } catch (System.Exception ex) {
            Debug.LogError($"[CentralManager] SetActors error: {ex.Message}");
        }
    }
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

    // Realtime Audio WebSocket (Unity -> SubtitleAI) URL
    public string GetRealtimeAudioWsUrl() {
        return PlayerPrefs.GetString("RealtimeAudioWsUrl", "ws://127.0.0.1:60001");
    }
    public void SetRealtimeAudioWsUrl(string url) {
        PlayerPrefs.SetString("RealtimeAudioWsUrl", url);
    }

    // 表示名（私 / 友人 / WipeAI）
    public string GetMyName() {
        return PlayerPrefs.GetString("MyName", "");
    }
    public void SetMyName(string value) {
        PlayerPrefs.SetString("MyName", value);
    }
    public string GetFriendName() {
        return PlayerPrefs.GetString("FriendName", "");
    }
    public void SetFriendName(string value) {
        PlayerPrefs.SetString("FriendName", value);
    }
    public string GetWipeAIName() {
        return PlayerPrefs.GetString("WipeAIName", "");
    }
    public void SetWipeAIName(string value) {
        PlayerPrefs.SetString("WipeAIName", value);
    }

    // Wipe AI: subtitle チャンネル名
    public string GetWipeAISubtitle() {
        return PlayerPrefs.GetString("WipeAISubtitle", "wipe_subtitle");
    }
    public void SetWipeAISubtitle(string value) {
        PlayerPrefs.SetString("WipeAISubtitle", value);
    }

    // 翻訳方式の設定管理はTranslationControllerに移動しました
    // 互換性のため、ラッパーメソッドを残します
    public string GetTranslationMode() {
        return TranslationController.Instance != null 
            ? TranslationController.Instance.GetTranslationMode() 
            : "deepl";
    }
    
    public void SetTranslationMode(string mode) {
        if (TranslationController.Instance != null) {
            TranslationController.Instance.SetTranslationMode(mode);
        } else {
            // フォールバック（通常は発生しない）
            PlayerPrefs.SetString("TranslationMode", mode);
        }
    }

    /// <summary>
    /// 字幕用の翻訳をCentralManager経由で実行し、結果をコールバックで返します。
    /// </summary>
    /// <param name="sourceText">翻訳元のテキスト</param>
    /// <param name="targetLang">対象言語（例: "en"）</param>
    /// <param name="subtitleName">字幕/識別子（TranslationControllerにはspeakerとして渡す）</param>
    /// <param name="onCompleted">翻訳結果（null可）</param>
    public IEnumerator TranslateForSubtitle(string sourceText, string targetLang, string subtitleName, System.Action<string> onCompleted) {
        if (TranslationController.Instance == null) {
            Debug.LogWarning("[CentralManager] TranslationControllerが見つかりません。翻訳をスキップします。");
            onCompleted?.Invoke(null);
            yield break;
        }

        string result = null;
        yield return StartCoroutine(TranslationController.Instance.Translate(
            sourceText,
            targetLang,
            subtitleName,
            (translatedText) => { result = translatedText; }
        ));

        onCompleted?.Invoke(result);
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

    // 旧: int保存 (0: WitAI, 1: MenZ/STT) → 新: string保存 ("WitAI" / "STT")
    private const string DiscordSubtitleMethodKey = "DiscordSubtitleMethod"; // legacy int
    private const string DiscordSubtitleMethodStrKey = "DiscordSubtitleMethodStr"; // new string

    public int GetDiscordSubtitleMethod() {
        // 互換維持: 既存コード用に残す（内部は新形式から変換）
        string mode = GetDiscordSubtitleMethodString();
        return mode == "STT" ? 1 : 0;
    }
    public void SetDiscordSubtitleMethod(int value) {
        // 互換維持: 新形式へ反映
        SetDiscordSubtitleMethodString(value == 1 ? "STT" : "WitAI");
    }

    public string GetDiscordSubtitleMethodString() {
        // 新キーがあればそれを返す
        if (PlayerPrefs.HasKey(DiscordSubtitleMethodStrKey)) {
            var v = PlayerPrefs.GetString(DiscordSubtitleMethodStrKey, "WitAI");
            // 後方互換: "MenZ" → "STT" に変換
            if (v == "MenZ") v = "STT";
            return (v == "STT") ? "STT" : "WitAI";
        }
        // 旧キーからの移行
        int legacy = PlayerPrefs.GetInt(DiscordSubtitleMethodKey, 0);
        string mapped = legacy == 1 ? "STT" : "WitAI";
        PlayerPrefs.SetString(DiscordSubtitleMethodStrKey, mapped);
        return mapped;
    }
    public void SetDiscordSubtitleMethodString(string value) {
        // 後方互換: "MenZ" → "STT" に変換
        if (value == "MenZ") value = "STT";
        string normalized = (value == "STT") ? "STT" : "WitAI";
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

    // Wipe向け送信API
    public void SendWipeRequest(object payload) {
        try {
            string json = (payload is string s) ? s : JsonConvert.SerializeObject(payload);
            MultiPortWebSocketServer.Instance?.BroadcastToWipeClients(json);
        } catch (System.Exception ex) {
            Debug.LogError($"Wipe送信エラー: {ex.Message}");
        }
    }

    // Wipe向け：字幕送信用ヘルパー
    public void SendWipeSubtitle(string text, string speaker = "viewer") {
        if (string.IsNullOrEmpty(text)) return;
        var payload = new {
            type = "subtitle",
            text = text,
            speaker = speaker
        };
        SendWipeRequest(payload);
    }

    // Wipe向け：チャット送信用ヘルパー（type=comment、textキーは維持）
    public void SendWipeComment(string text, string speaker) {
        if (string.IsNullOrEmpty(text)) return;
        var payload = new {
            type = "comment",
            text = text,
            speaker = speaker
        };
        SendWipeRequest(payload);
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
        DiscordBotClient.OnDiscordLoggedIn -= HandleDiscordLoggedIn;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
            MultiPortWebSocketServer.OnWipeMessageReceived -= HandleWipeMessageReceived;
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
            MultiPortWebSocketServer.OnWipeMessageReceived -= HandleWipeMessageReceived;
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

            // Wipe AI へチャットとして送信（発言者ユーザ名）
            SendWipeComment(chatMessage, user);
        }
    }

    // DiscordBotから音声認識結果を受け取る
    private void HandleDiscordVoiceRecognized(string inputName, string recognizedText) {

        // 字幕用チャンネル名を取得（友達の字幕チャンネルに変更）
        string subtitleChannel = GetFriendSubtitle();
        if (string.IsNullOrEmpty(subtitleChannel)) {
            return; // 設定されていない場合は処理をスキップ
        }

        // 字幕表示はSubtitleControllerへ委譲
        SubtitleController.Instance?.EnqueueJapaneseSubtitle(recognizedText, subtitleChannel, "", false);

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
        // 要件: 起動時は顔OFF、停止時は顔ON
        if (isRunning) {
            SetFaceVisible(false);
        } else {
            SetFaceVisible(true);
        }
    }

    // DiscordBotのログイン完了（READY）を受け取る
    private void HandleDiscordLoggedIn() {
        Debug.Log("DiscordBot READY 受信: ログイン確認");
        // 顔表示はターゲット在席時のみ。READYでは何もしない。
    }

    // /wipe_subtitle からの受信を処理
    private void HandleWipeMessageReceived(string message) {
        Debug.Log($"[WIPE] 受信: {message}");
        try {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(message);
            string type = obj.Value<string>("type");

            if (type == "comment") {
                string comment = obj.Value<string>("comment");
                if (string.IsNullOrEmpty(comment)) return;

                // WipeAI専用のOBS字幕チャンネルへ表示
                string wipeSubtitleChannel = GetWipeAISubtitle();
                if (!string.IsNullOrEmpty(wipeSubtitleChannel)) {
                    // 字幕表示はSubtitleControllerへ委譲
                    SubtitleController.Instance?.EnqueueJapaneseSubtitle(comment, wipeSubtitleChannel, "", false);

                    // WipeAIの字幕もVoiceVoxで読み上げ
                    string wipeName = GetWipeAIName();
                    if (string.IsNullOrEmpty(wipeName)) wipeName = "WipeAI";
                    StartCoroutine(speakComment(wipeName, comment));
                }
            }
        } catch (System.Exception ex) {
            Debug.LogError($"[WIPE] 解析エラー: {ex.Message}");
        }
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
        // TranslationControllerに翻訳を委譲（フォールバック処理も含む）
        if (TranslationController.Instance != null) {
            yield return StartCoroutine(TranslationController.Instance.Translate(
                chatMessage, 
                "ja", 
                user, 
                (translatedText) => {
                    chatMessage = translatedText ?? chatMessage; // nullの場合は元のメッセージを使用
                }
            ));
        } else {
            Debug.LogWarning("[CentralManager] TranslationControllerが見つかりません。翻訳をスキップします。");
        }

        // コメント読み上げを開始
        StartCoroutine(speakComment(user, chatMessage));

        // 翻訳文をTwitchコメントに送信
        SendTwitchMessage($"[{user}]: {chatMessage}");

        // コメントスクロールを開始
        SendCanvasMessage(chatMessage);

        // Wipe AI へチャットとして送信（発言者ユーザ名）
        SendWipeComment(chatMessage, user);
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

    // MCP: speaker名からsubtitle名へのマッピング
    private string GetSubtitleFromSpeaker(string speaker) {
        if (string.IsNullOrEmpty(speaker)) return "";

        // 設定から各名前と字幕チャンネルを取得
        string myName = GetMyName();
        string friendName = GetFriendName();
        string wipeAIName = GetWipeAIName();

        // speaker名と一致するチャンネルを返す（大文字小文字を無視）
        if (!string.IsNullOrEmpty(myName) && 
            string.Equals(speaker.Trim(), myName.Trim(), System.StringComparison.OrdinalIgnoreCase)) {
            return GetMySubtitle();
        }
        
        if (!string.IsNullOrEmpty(friendName) && 
            string.Equals(speaker.Trim(), friendName.Trim(), System.StringComparison.OrdinalIgnoreCase)) {
            return GetFriendSubtitle();
        }
        
        if (!string.IsNullOrEmpty(wipeAIName) && 
            string.Equals(speaker.Trim(), wipeAIName.Trim(), System.StringComparison.OrdinalIgnoreCase)) {
            return GetWipeAISubtitle();
        }

        // 一致しない場合はMySubtitleをデフォルトとして返す
        Debug.LogWarning($"[MCP] 不明なspeaker名: {speaker}, デフォルトの字幕チャンネルを使用します");
        return GetMySubtitle();
    }

    private void HandleWebSocketMessageFromPort50001(string subtitle, string subtitleText) {
        // サーバ側でMCP解析済み: subtitle=字幕ソース名, subtitleText=テキスト
        if (string.IsNullOrEmpty(subtitle) || string.IsNullOrEmpty(subtitleText)) return;
        SubtitleController.Instance?.EnqueueJapaneseSubtitle(subtitleText, subtitle, subtitle + "_en", false);
    }

    // 字幕表示時間を計算するヘルパーメソッド
    private float calculateDisplayDuration(int charCount) { return 0f; }

    // 旧APIスタブ（残置呼び出し対策）

    private void HandleWebSocketMessageFromPort50002(string subtitle, string subtitleText) {
        Debug.Log($"[Port 50002] Subtitle: {subtitle}, Message: {subtitleText}");
        // ポート50002からのメッセージに対する処理
    }

    // 英語字幕の送信
    private IEnumerator translateSubtitle(string subtitle, string subtitleText) { yield break; }
}

// CurrentDisplaySubtitle は SubtitleController 側に移動

// Twitch        コメント受信   イベント セントラルマネージャーがイベントを受け取り各処理 OK
// Twitch        コメント送信   イベント セントラルマネージャーがイベントを送信 OK
// VoiceVox      音声生成    メソッド セントラルマネージャーがVoiceVoxのメソッドを直接実行する OK
// VoiceVox      話者取得    メソッド セントラルマネージャーがVoiceVoxのメソッドを直接実行する OK
// キャンバス       ニコニコ風   イベント セントラルマネージャーがイベントを送信 OK
// 翻訳API　       翻訳        メソッド セントラルマネージャが翻訳APIのメソッドを直接参照する OK
// YukariWhisper 自動字幕    イベント　セントラルマネージャーがイベントを受け取り各処理 OK
// OBS           字幕表示    イベント セントラルマネージャーがイベントを送信 OK
// DiscordBOT　　　　　　　音声ストリーム 
