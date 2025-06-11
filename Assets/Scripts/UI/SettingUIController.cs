using UnityEngine;
using UnityEngine.UIElements;

public class SettingUIController : MonoBehaviour {
    // InspectorからUIDocumentをアタッチできるよう、[SerializeField] を付ける
    // このUIDocumentは、メインのRoot VisualElement（LogUIControllerなどが使っているもの）と同じものをアタッチします。
    [SerializeField] private UIDocument uiDocument;

    // 設定画面のルート要素への参照
    private VisualElement settingContentRoot;

    // 各設定UI要素への参照
    private Toggle autoStartSubtitleAIToggle;
    private TextField subtitleAIPathInput;
    private Button browseSubtitleAIPathButton; // 参照ボタン

    private IntegerField charactersPerSecondInput;
    private FloatField minDisplayTimeInput;
    private FloatField maxDisplayTimeInput;

    private TextField obsWebSocketsPasswordInput;
    private TextField mySubtitleInput;
    private TextField myEnglishSubtitleInput;

    private TextField deepLApiClientKeyInput;
    private Button saveSettingsButton;

    // PlayerPrefsで使用するキー名 (定数として定義しておくとミスが減る)
    private const string KEY_AUTO_START_SUBTITLE_AI = "AutoStartSubtitleAI";
    private const string KEY_SUBTITLE_AI_PATH = "SubtitleAIPath";

    void OnEnable() {
        Debug.LogWarning("設定UI起動");
        if (uiDocument == null || uiDocument.rootVisualElement == null) {
            Debug.LogError("SettingUIController: UIDocument またはそのルート要素が設定されていません。");
            return;
        }

        // UIDocumentのルートVisualElementから、設定画面のコンテナを探す
        // TabControllerが切り替えている "settingContent" VisualElementを想定
        settingContentRoot = uiDocument.rootVisualElement.Q<VisualElement>("settingContent");

        if (settingContentRoot == null) {
            Debug.LogError("SettingUIController: UXML内で 'settingContent' という名前のVisualElementが見つかりません。");
            return;
        }

        // 各UI要素を取得
        autoStartSubtitleAIToggle = settingContentRoot.Q<Toggle>(KEY_AUTO_START_SUBTITLE_AI);
        subtitleAIPathInput = settingContentRoot.Q<TextField>(KEY_SUBTITLE_AI_PATH);
        browseSubtitleAIPathButton = settingContentRoot.Q<Button>("BrowseSubtitleAIPathButton"); // 参照ボタン

        charactersPerSecondInput = settingContentRoot.Q<IntegerField>("CharactersPerSecondInput");
        minDisplayTimeInput = settingContentRoot.Q<FloatField>("MinDisplayTimeInput");
        maxDisplayTimeInput = settingContentRoot.Q<FloatField>("MaxDisplayTimeInput");

        obsWebSocketsPasswordInput = settingContentRoot.Q<TextField>("ObsWebSocketsPasswordInput");
        mySubtitleInput = settingContentRoot.Q<TextField>("MySubtitleInput");
        myEnglishSubtitleInput = settingContentRoot.Q<TextField>("MyEnglishSubtitleInput");
        deepLApiClientKeyInput = settingContentRoot.Q<TextField>("DeepLApiClientKeyInput"); // DeepL APIキー
        saveSettingsButton = settingContentRoot.Q<Button>("SaveSettingsButton"); // 設定を保存ボタン

        // UI要素が見つからない場合のエラーチェック
        // if (deepLApiClientKeyInput == null) Debug.LogError("SettingUIController: 'DeepLApiClientKeyInput' TextFieldが見つかりません。");
        // if (saveSettingsButton == null) Debug.LogError("SettingUIController: 'SaveSettingsButton' Buttonが見つかりません。");
        // 他の要素についても同様にチェックを入れるとより堅牢になります。

        // --- 初期値の読み込みとUIへの反映 ---
        LoadSettingsToUI();
        Debug.LogWarning("設定UI LoadSettingsToUI処理終了");

        // --- イベントリスナーの登録 ---
        if (saveSettingsButton != null) {
            saveSettingsButton.clicked += SaveSettingsFromUI;
        }
        // 他のボタンやトグルのイベントもここに追加
        if (browseSubtitleAIPathButton != null) {
            browseSubtitleAIPathButton.clicked += OnBrowseSubtitleAIPathClicked;
        }
    }

    void OnDisable() {
        // イベントリスナーの解除 (オブジェクトが無効になったときにメモリリークを防ぐ)
        if (saveSettingsButton != null) {
            saveSettingsButton.clicked -= SaveSettingsFromUI;
        }
        if (browseSubtitleAIPathButton != null) {
            browseSubtitleAIPathButton.clicked -= OnBrowseSubtitleAIPathClicked;
        }
    }

    // 設定値をUIに読み込むメソッド
    private void LoadSettingsToUI() {
        // PlayerPrefsから読み込み、UIに設定
        if (charactersPerSecondInput != null) {
            charactersPerSecondInput.value = CentralManager.Instance.GetCharactersPerSecond();
        }
        if (minDisplayTimeInput != null) {
            minDisplayTimeInput.value = CentralManager.Instance.GetMinDisplayTime();
        }
        if (maxDisplayTimeInput != null) {
            maxDisplayTimeInput.value = CentralManager.Instance.GetMaxDisplayTime();
        }

        if (obsWebSocketsPasswordInput != null) {
            obsWebSocketsPasswordInput.value = CentralManager.Instance.GetObsWebSocketsPassword();
        }

        if (mySubtitleInput != null) {
            mySubtitleInput.value = CentralManager.Instance.GetMySubtitle();
        }
        if (myEnglishSubtitleInput != null) {
            myEnglishSubtitleInput.value = CentralManager.Instance.GetMyEnglishSubtitle();
        }

        if (deepLApiClientKeyInput != null) {
            deepLApiClientKeyInput.value = CentralManager.Instance.GetDeepLApiClientKey();
        }

        Debug.Log("設定UIに値をロードしました。");
    }

    // UIから設定値を読み込み、保存するメソッド
    private void SaveSettingsFromUI() {
        if (charactersPerSecondInput != null) {
            CentralManager.Instance.SetCharactersPerSecond(charactersPerSecondInput.value);
        }

        if (minDisplayTimeInput != null) {
            CentralManager.Instance.SetMinDisplayTime(minDisplayTimeInput.value);
        }

        if (maxDisplayTimeInput != null) {
            CentralManager.Instance.SetMaxDisplayTime(maxDisplayTimeInput.value);
        }

        if (obsWebSocketsPasswordInput != null) {
            CentralManager.Instance.SetObsWebSocketsPassword(obsWebSocketsPasswordInput.value);
        }

        if (mySubtitleInput != null) {
            CentralManager.Instance.SetMySubtitle(mySubtitleInput.value);
        }
        if (myEnglishSubtitleInput != null) {
            CentralManager.Instance.SetMyEnglishSubtitle(myEnglishSubtitleInput.value);
        }

        if (deepLApiClientKeyInput != null) {
            CentralManager.Instance.SetDeepLApiClientKey(deepLApiClientKeyInput.value);
        }

        // その他の設定もPlayerPrefsに直接保存
        if (autoStartSubtitleAIToggle != null) {
            PlayerPrefs.SetInt(KEY_AUTO_START_SUBTITLE_AI, autoStartSubtitleAIToggle.value ? 1 : 0);
        }
        if (subtitleAIPathInput != null) {
            PlayerPrefs.SetString(KEY_SUBTITLE_AI_PATH, subtitleAIPathInput.value);
        }

        // PlayerPrefsの変更をディスクに書き込む
        // CentralManager の OnDisable/OnDestroy で自動的に保存されるため、
        // ここで毎回 PlayerPrefs.Save(); を呼ぶ必要はありませんが、
        // 確実に保存したい場合は呼んでも構いません。
        CentralManager.Instance.SaveAllPlayerPrefs(); // CentralManagerにまとめて保存を指示
        // PlayerPrefs.Save(); // この行でもOKです。

        Debug.Log("設定を保存しました。");
        // ユーザーに保存完了のフィードバックを与えるUI表示などをここに追加
    }

    // --- 参照ボタンのクリックハンドラ (例) ---
    private void OnBrowseSubtitleAIPathClicked() {
        Debug.Log("字幕AIパス参照ボタンがクリックされました。ファイル選択ダイアログなどを実装します。");
        // ここにファイル選択ダイアログを表示するロジックを実装します。
        // Unityでは通常、UnityStandaloneFileBrowserなどの外部ライブラリを使うと便利です。
    }

    // 必要に応じて、他のカスタムイベントハンドラーやヘルパーメソッドを追加できます。
}