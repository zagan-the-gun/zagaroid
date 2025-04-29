using UnityEngine;
using System.IO;
using Newtonsoft.Json;


public class CentralManager : MonoBehaviour
{
    // シングルトンインスタンス
    public static CentralManager Instance { get; private set; }


    // 設定ファイルの読み込み処理
    private Config config;

    [System.Serializable]
    public class Config {
        public string DeepLApiClientKey; // APIキーを格納するプロパティ
        public string ObsWebSocketsPassword;
    }

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
    // ... 他のグローバル設定

    void Awake() {
        // シングルトンパターンの実装
        if (Instance == null) {
            Instance = this;
            // シーンを跨いでも破棄しないようにする場合
            // DontDestroyOnLoad(gameObject);
            LoadConfig(); // CentralManager の初期化時に設定を読み込む
            Debug.Log("CentralManager initialized and config loaded.");

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

    // 必要に応じて他のグローバルな機能を追加
}

// Twitch        コメント受信 イベント セントラルマネージャーがイベントを受け取り各処理
// Twitch        コメント送信 イベント セントラルマネージャーがイベントを送信
// キャンバス       ニコニコ風  イベント セントラルマネージャーがイベントを送信
// OBS           字幕表示   イベント セントラルマネージャーがイベントを送信
// YukariWhisper 自動字幕   イベント　セントラルマネージャーがイベントを受け取り各処理
// 翻訳API　       翻訳       メソッド セントラルマネージャが翻訳APIのメソッドを直接参照する
