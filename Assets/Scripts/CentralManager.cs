using UnityEngine;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections;


public class CentralManager : MonoBehaviour {
    // シングルトンインスタンス
    public static CentralManager Instance { get; private set; }


    // 設定ファイルの読み込み処理
    private Config config;

    [System.Serializable]
    public class Config {
        public string DeepLApiClientKey; // APIキーを格納するプロパティ
        public string ObsWebSocketsPassword;
    }

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

    private void Awake() {
        // シングルトンパターンの実装
        if (Instance == null) {
            Instance = this;
            // シーンを跨いでも破棄しないようにする場合
            // DontDestroyOnLoad(gameObject);
            LoadConfig(); // CentralManager の初期化時に設定を読み込む
            Debug.Log("CentralManager initialized and config loaded.");

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

    public void LoadConfig() {
        Debug.Log("Loading config...");
        string path = Path.Combine(Application.streamingAssetsPath, "config.json");

        if (File.Exists(path)) {
            string json = File.ReadAllText(path);
            config = JsonConvert.DeserializeObject<Config>(json);
            Debug.Log("Config loaded: " + json);
        } else {
            Debug.LogError("Config file not found: " + path);
        }
    }

    public string GetDeepLApiClientKey() {
        return config?.DeepLApiClientKey; // APIキーを返す
    }

    public string GetObsWebSocketsPassword() {
        return config?.ObsWebSocketsPassword; // APIキーを返す
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


    // アプリケーション全体で利用する設定を取得するメソッド
    // public string GetDeepLApiKey() {
    //     return DeepLApiKey;
    // }

    // public string GetTwitchOAuthToken() {
    //     return TwitchOAuthToken;
    // }

    // public float GetMasterVolume() {
    //     return MasterVolume;
    // }

    // public string GetDefaultLanguage() {
    //     return DefaultLanguage;
    // }
    //public な変数として API キーやグローバル設定を定義しており、Unity エディターのインスペクターから値を設定できます。
    // これらの設定値を取得するためのメソッド (GetDeepLApiKey(), GetMasterVolume() など) を提供しています。


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

    void OnEnable() {
        // Twitchからコメントを受信するイベントを登録
        TwitchChatController.OnTwitchMessageReceived += HandleTwitchMessageReceived;
        // MultiPortWebSocketServerの情報を受信するイベントを登録
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 += HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 += HandleWebSocketMessageFromPort50002;
        } else {
            Debug.LogError("MultiPortWebSocketServer のインスタンスが見つかりません。");
        }
    }

    void OnDisable() {
        TwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
        }
    }
    void OnDestroy() {
        TwitchChatController.OnTwitchMessageReceived -= HandleTwitchMessageReceived;
        if (MultiPortWebSocketServer.Instance != null) {
            MultiPortWebSocketServer.OnMessageReceivedFromPort50001 -= HandleWebSocketMessageFromPort50001;
            MultiPortWebSocketServer.OnMessageReceivedFromPort50002 -= HandleWebSocketMessageFromPort50002;
        }
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
            // コメントスクロールを開始
            SendCanvasMessage(chatMessage);

            // コメント読み上げを開始
            StartCoroutine(speakComment(user, chatMessage));
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
        // 翻訳処理を行う
        yield return StartCoroutine(_deepLApiClient.PostTranslate(chatMessage, "JA", (result) => {
            // 翻訳結果を処理
            chatMessage = result;
        }));

        // 翻訳文をTwitchコメントに送信
        SendTwitchMessage($"[{user}]: {chatMessage}");

        // コメントスクロールを開始
        SendCanvasMessage(chatMessage);

        // コメント読み上げを開始
        StartCoroutine(speakComment(user, chatMessage));
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
        SendObsSubtitles(subtitle, subtitleText);

        // 翻訳字幕の送信
        StartCoroutine(translateSubtitle(subtitle, subtitleText));
    }

    private void HandleWebSocketMessageFromPort50002(string subtitle, string subtitleText) {
        Debug.Log($"[Port 50002] Subtitle: {subtitle}, Message: {subtitleText}");
        // ポート50002からのメッセージに対する処理
    }

    private IEnumerator translateSubtitle(string subtitle, string subtitleText) {
        Debug.Log("字幕の翻訳開始");
        yield return StartCoroutine(_deepLApiClient.PostTranslate(subtitleText, "EN", (result) => {
            // 翻訳結果を取得
            Debug.Log($"コールバック実行: 翻訳結果受信: {result}");
            subtitleText = result;
        }));
        Debug.Log($"翻訳字幕: {subtitleText}");

        // 字幕をOBSに送信
        SendObsSubtitles($"{subtitle}_en", subtitleText);
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
