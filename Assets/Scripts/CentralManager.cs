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
    // ... 他のグローバル設定

    // private const int CHARS_PER_SECOND = 4; // 1秒あたりの文字数 (例: 4文字で1秒)
    // private const float MIN_DISPLAY_TIME = 4.0f; // 最低表示時間 (例: 2秒)
    // private const float MAX_DISPLAY_TIME = 8.0f; // 最大表示時間 (例: 30秒)

    private Queue<string> _japaneseSubtitleQueue = new Queue<string>(); // 日本語字幕のキュー
    private CurrentDisplaySubtitle _currentJapaneseDisplay; // 現在表示中の日本語字幕とその情報

    // 現在の字幕が結合された状態かどうかのフラグ
    private bool _isCombiningJapaneseSubtitles = false;

    private void Awake() {
        // シングルトンパターンの実装
        if (Instance == null) {
            Instance = this;



            _voiceVoxApiClient = new VoiceVoxApiClient();
            _deepLApiClient = new DeepLApiClient();
            _webSocketServer = MultiPortWebSocketServer.Instance;

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
        // MultiPortWebSocketServerの情報を受信するイベントを登録
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 += HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 += HandleWebSocketMessageFromPort50002;
        } else {
            Debug.LogError("MultiPortWebSocketServer のインスタンスが見つかりません。");
        }

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

    private void Update() {
        // 字幕の表示時間処理
        if (_currentJapaneseDisplay != null) {
            _currentJapaneseDisplay.remainingDuration -= Time.deltaTime;

            if (_currentJapaneseDisplay.remainingDuration <= 0) {
                Debug.Log("日本語字幕の表示時間が終了しました。");
                // OBS側で日本語字幕をクリアする (テキストを空にする)
                SendObsSubtitles(_currentJapaneseDisplay.japaneseSubtitle, "");
                // 日本語字幕が消えたら、英語字幕も消す
                if (!string.IsNullOrEmpty(_currentJapaneseDisplay.englishSubtitle)) {
                     SendObsSubtitles(_currentJapaneseDisplay.englishSubtitle, "");
                }
               
                _currentJapaneseDisplay = null;
                _isCombiningJapaneseSubtitles = false; // 結合フラグをリセット

                // キューに次の日本語字幕があれば表示を開始
                if (_japaneseSubtitleQueue.Count > 0) {
                    Debug.Log("キューから次の日本語字幕を表示します。");
                    string nextJapaneseText = _japaneseSubtitleQueue.Dequeue();
                    // 新しいCurrentDisplaySubtitleオブジェクトを作成
                    // 英語字幕は別途翻訳されるため、ここでは日本語の情報のみで十分
                    float nextDuration = calculateDisplayDuration(nextJapaneseText.Length);
                    CurrentDisplaySubtitle nextJpEntry = new CurrentDisplaySubtitle(
                        nextJapaneseText,
                        _currentJapaneseDisplay?.japaneseSubtitle ?? "zagan_subtitle", // 前のチャンネル情報を引き継ぐかデフォルト
                        _currentJapaneseDisplay?.englishSubtitle ?? "zagan_subtitle_en", // 前のチャンネル情報を引き継ぐかデフォルト
                        nextDuration
                    );
                    startDisplayingJapaneseSubtitle(nextJpEntry);

                    // キューから取り出した日本語字幕に対応する英語字幕も非同期で翻訳・送信
                    // ※ ここでチャンネル名が適切に渡されるように注意
                    StartCoroutine(translateSubtitle(nextJpEntry.englishSubtitle, nextJapaneseText));
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

    void OnDisable() {
        UnityTwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
        }
        // アプリケーションが終了する際や、CentralManagerが無効になる際に保存
        SaveAllPlayerPrefs(); 
    }

    void OnDestroy() {
        UnityTwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
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
        Debug.Log($"字幕を受信しました！ Subtitle: {subtitle}, Message: {subtitleText}");
        // 字幕をOBSに送信
        // SendObsSubtitles(subtitle, subtitleText);

        // 英語字幕用テキストソース名取得
        string myEnglishSubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetMyEnglishSubtitle() : null;

        // 文字数に応じて日本語字幕表示時間を調整する
        float calculatedDuration = calculateDisplayDuration(subtitleText.Length);

        // 新しい日本語字幕エントリを作成
        CurrentDisplaySubtitle newJpEntry = new CurrentDisplaySubtitle(
            subtitleText,
            subtitle,
            myEnglishSubtitle, // 英語チャンネルも情報として保持
            calculatedDuration
        );

        // 日本語字幕の表示ロジックを管理
        manageJapaneseSubtitleDisplay(newJpEntry);

        // 翻訳字幕の送信
        StartCoroutine(translateSubtitle(myEnglishSubtitle, subtitleText));
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
        if (_currentJapaneseDisplay == null) {
            // 現在表示中の日本語字幕がない場合、すぐに表示
            startDisplayingJapaneseSubtitle(newJpEntry);
            Debug.Log("字幕：新しい日本語字幕をすぐに表示します。");
        } else if (!_isCombiningJapaneseSubtitles) {
            // 表示中の日本語字幕があり、まだ結合中でない場合
            // 2. 先の字幕+次の字幕を同時に表示する
            // 2. 先の字幕の残表示時間+次の字幕の表示時間の間表示する
            Debug.Log("字幕：既存の日本語字幕と新しい日本語字幕を結合して表示します。");
            combineAndDisplayJapaneseSubtitles(_currentJapaneseDisplay, newJpEntry);
            _isCombiningJapaneseSubtitles = true; // 結合中フラグを立てる
        } else {
            // 3. 既に結合表示中にさらに新しい日本語字幕が来た場合、キューに追加して待機
            Debug.Log("字幕：既に結合表示中またはキューに他の字幕があるため、新しい日本語字幕をキューに追加します。");
            _japaneseSubtitleQueue.Enqueue(newJpEntry.japaneseText); // テキストのみキューに入れる
        }
    }

    // 実際に日本語字幕の表示を開始するメソッド
    private void startDisplayingJapaneseSubtitle(CurrentDisplaySubtitle jpEntry) {
        _currentJapaneseDisplay = jpEntry;
        _isCombiningJapaneseSubtitles = false; // 新しい日本語字幕なので結合フラグはリセット

        // 日本語字幕をOBSに送信
        SendObsSubtitles(_currentJapaneseDisplay.japaneseSubtitle, _currentJapaneseDisplay.japaneseText);

        Debug.Log($"日本語字幕表示開始: 『{_currentJapaneseDisplay.japaneseText}』, 残り時間: {_currentJapaneseDisplay.remainingDuration:F2}秒");
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
        _currentJapaneseDisplay = new CurrentDisplaySubtitle(
            combinedJapaneseText, 
            existingJp.japaneseSubtitle, // チャンネルは既存のものを引き継ぐ
            existingJp.englishSubtitle,   // 英語チャンネルも引き継ぐ
            newDuration
        );
        _currentJapaneseDisplay.remainingDuration = newDuration; // 残り時間も更新

        // OBSに送信
        SendObsSubtitles(_currentJapaneseDisplay.japaneseSubtitle, _currentJapaneseDisplay.japaneseText);

        Debug.Log($"日本語字幕結合表示開始: 『{_currentJapaneseDisplay.japaneseText}』, 残り時間: {_currentJapaneseDisplay.remainingDuration:F2}秒");
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

    public CurrentDisplaySubtitle(string jpText, string jpSubtitle, string enSubtitle, float duration) {
        japaneseText = jpText;
        japaneseSubtitle = jpSubtitle;
        englishSubtitle = enSubtitle; // 英語チャンネルもここで設定
        displayDuration = duration;
        remainingDuration = duration;
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
