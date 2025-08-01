using UnityEngine;
using UnityEngine.UIElements;
using SFB; // ★ 追加: StandaloneFileBrowserの名前空間
using System.IO; // ★ 追加: パス操作のため
using System.Collections.Generic; // ★ 追加: List<T>のため

public class SettingUIController : MonoBehaviour {
    // InspectorからUIDocumentをアタッチできるよう、[SerializeField] を付ける
    // このUIDocumentは、メインのRoot VisualElement（LogUIControllerなどが使っているもの）と同じものをアタッチします。
    [SerializeField] private UIDocument uiDocument;

    // 設定画面のルート要素への参照
    private VisualElement settingContentRoot;

    // 各設定UI要素への参照
    private Toggle autoStartSubtitleAIToggle;
    private TextField subtitleAIExecutionPathInput;
    private Button browseSubtitleAIPathButton; // 参照ボタン
    private Button startSubtitleAIButton; // 手動起動ボタン

    private Toggle autoStartVoiceVoxToggle;
    private TextField voiceVoxExecutionPathInput;
    private Button browseVoiceVoxPathButton; // 参照ボタン
    private Button startVoiceVoxButton; // 手動起動ボタン

    private Toggle autoStartMenzTranslationToggle;
    private TextField menzTranslationExecutionPathInput;
    private Button browseMenzTranslationPathButton; // 参照ボタン
    private Button startMenzTranslationButton; // 手動起動ボタン

    private IntegerField charactersPerSecondInput;
    private FloatField minDisplayTimeInput;
    private FloatField maxDisplayTimeInput;

    private TextField obsWebSocketsPasswordInput;
    private TextField mySubtitleInput;
    private TextField myEnglishSubtitleInput;
    private TextField friendSubtitleInput;

    private TextField deepLApiClientKeyInput;
    private DropdownField translationModeDropdown;
    private TextField menZTranslationServerUrlInput;
    
    private Toggle autoStartDiscordBotToggle;
    private TextField discordTokenInput;
    private TextField discordGuildIdInput;
    private TextField discordVoiceChannelIdInput;
    private TextField discordTextChannelIdInput;
    private TextField discordTargetUserIdInput;
    private TextField discordInputNameInput;
    private DropdownField discordSubtitleMethodDropdown;
    private TextField discordWitaiTokenInput;

    private Button startDiscordBotButton; // Discord BOT手動起動ボタン
    private Button stopDiscordBotButton; // Discord BOT停止ボタン
    
    private Button saveSettingsButton;


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
        autoStartSubtitleAIToggle = settingContentRoot.Q<Toggle>("AutoStartSubtitleAIToggle");
        subtitleAIExecutionPathInput = settingContentRoot.Q<TextField>("SubtitleAIExecutionPathInput");
        browseSubtitleAIPathButton = settingContentRoot.Q<Button>("BrowseSubtitleAIPathButton"); // 参照ボタン
        startSubtitleAIButton = settingContentRoot.Q<Button>("StartSubtitleAIButton"); // 手動起動ボタン

        autoStartVoiceVoxToggle = settingContentRoot.Q<Toggle>("AutoStartVoiceVoxToggle");
        voiceVoxExecutionPathInput = settingContentRoot.Q<TextField>("VoiceVoxExecutionPathInput");
        browseVoiceVoxPathButton = settingContentRoot.Q<Button>("BrowseVoiceVoxPathButton"); // 参照ボタン
        startVoiceVoxButton = settingContentRoot.Q<Button>("StartVoiceVoxButton"); // 手動起動ボタン

        autoStartMenzTranslationToggle = settingContentRoot.Q<Toggle>("AutoStartMenzTranslationToggle");
        menzTranslationExecutionPathInput = settingContentRoot.Q<TextField>("MenzTranslationExecutionPathInput");
        browseMenzTranslationPathButton = settingContentRoot.Q<Button>("BrowseMenzTranslationPathButton"); // 参照ボタン
        startMenzTranslationButton = settingContentRoot.Q<Button>("StartMenzTranslationButton"); // 手動起動ボタン

        charactersPerSecondInput = settingContentRoot.Q<IntegerField>("CharactersPerSecondInput");
        minDisplayTimeInput = settingContentRoot.Q<FloatField>("MinDisplayTimeInput");
        maxDisplayTimeInput = settingContentRoot.Q<FloatField>("MaxDisplayTimeInput");

        obsWebSocketsPasswordInput = settingContentRoot.Q<TextField>("ObsWebSocketsPasswordInput");
        mySubtitleInput = settingContentRoot.Q<TextField>("MySubtitleInput");
        myEnglishSubtitleInput = settingContentRoot.Q<TextField>("MyEnglishSubtitleInput");
        friendSubtitleInput = settingContentRoot.Q<TextField>("FriendSubtitleInput");
        deepLApiClientKeyInput = settingContentRoot.Q<TextField>("DeepLApiClientKeyInput"); // DeepL APIキー
        translationModeDropdown = settingContentRoot.Q<DropdownField>("TranslationModeDropdown"); // 翻訳方式選択
        menZTranslationServerUrlInput = settingContentRoot.Q<TextField>("MenZTranslationServerUrlInput"); // MenZ翻訳サーバーURL
        autoStartDiscordBotToggle = settingContentRoot.Q<Toggle>("AutoStartDiscordBotToggle");
        discordTokenInput = settingContentRoot.Q<TextField>("DiscordTokenInput");
        discordGuildIdInput = settingContentRoot.Q<TextField>("DiscordGuildIdInput");
        discordVoiceChannelIdInput = settingContentRoot.Q<TextField>("DiscordVoiceChannelIdInput");
        discordTextChannelIdInput = settingContentRoot.Q<TextField>("DiscordTextChannelIdInput");
        discordTargetUserIdInput = settingContentRoot.Q<TextField>("DiscordTargetUserIdInput");
        discordInputNameInput = settingContentRoot.Q<TextField>("DiscordInputNameInput");
        discordSubtitleMethodDropdown = settingContentRoot.Q<DropdownField>("DiscordSubtitleMethodDropdown");
        discordWitaiTokenInput = settingContentRoot.Q<TextField>("DiscordWitaiTokenInput");
        
        startDiscordBotButton = settingContentRoot.Q<Button>("StartDiscordBotButton"); // Discord BOT手動起動ボタン
        stopDiscordBotButton = settingContentRoot.Q<Button>("StopDiscordBotButton"); // Discord BOT停止ボタン
        saveSettingsButton = settingContentRoot.Q<Button>("SaveSettingsButton"); // 設定を保存ボタン

        // UI要素が見つからない場合のエラーチェック
        // if (deepLApiClientKeyInput == null) Debug.LogError("SettingUIController: 'DeepLApiClientKeyInput' TextFieldが見つかりません。");
        // if (saveSettingsButton == null) Debug.LogError("SettingUIController: 'SaveSettingsButton' Buttonが見つかりません。");
        // 他の要素についても同様にチェックを入れるとより堅牢になります。

        // --- 初期値の読み込みとUIへの反映 ---
        LoadSettingsToUI();
        Debug.LogWarning("設定UI LoadSettingsToUI処理終了");

        // --- イベントリスナーの登録 ---
        if (browseSubtitleAIPathButton != null)
        {
            browseSubtitleAIPathButton.clicked += OnBrowseSubtitleAIPathClicked;
        }

        if (startSubtitleAIButton != null) {
            startSubtitleAIButton.clicked += OnStartSubtitleAIClicked;
        }

        if (browseVoiceVoxPathButton != null)
        {
            browseVoiceVoxPathButton.clicked += OnBrowseVoiceVoxPathClicked;
        }

        if (startVoiceVoxButton != null) {
            startVoiceVoxButton.clicked += OnStartVoiceVoxClicked;
        }

        if (browseMenzTranslationPathButton != null)
        {
            browseMenzTranslationPathButton.clicked += OnBrowseMenzTranslationPathClicked;
        }

        if (startMenzTranslationButton != null) {
            startMenzTranslationButton.clicked += OnStartMenzTranslationClicked;
        }

        if (startDiscordBotButton != null) {
            startDiscordBotButton.clicked += OnStartDiscordBotClicked;
        }

        if (stopDiscordBotButton != null) {
            stopDiscordBotButton.clicked += OnStopDiscordBotClicked;
        }

        if (saveSettingsButton != null) {
            saveSettingsButton.clicked += SaveSettingsFromUI;
        }

        // DiscordBotの状態変更イベントを登録
        DiscordBotClient.OnDiscordBotStateChanged += OnDiscordBotStateChanged;
        
        // 初期状態でボタンの有効/無効を設定
        UpdateDiscordBotButtons();
    }

    void OnDisable() {
        // イベントリスナーの解除 (オブジェクトが無効になったときにメモリリークを防ぐ)
        if (browseSubtitleAIPathButton != null) {
            browseSubtitleAIPathButton.clicked -= OnBrowseSubtitleAIPathClicked;
        }

        if (startSubtitleAIButton != null) {
            startSubtitleAIButton.clicked -= OnStartSubtitleAIClicked;
        }

        if (browseVoiceVoxPathButton != null) {
            browseVoiceVoxPathButton.clicked -= OnBrowseVoiceVoxPathClicked;
        }

        if (startVoiceVoxButton != null) {
            startVoiceVoxButton.clicked -= OnStartVoiceVoxClicked;
        }

        if (browseMenzTranslationPathButton != null) {
            browseMenzTranslationPathButton.clicked -= OnBrowseMenzTranslationPathClicked;
        }

        if (startMenzTranslationButton != null) {
            startMenzTranslationButton.clicked -= OnStartMenzTranslationClicked;
        }

        if (startDiscordBotButton != null) {
            startDiscordBotButton.clicked -= OnStartDiscordBotClicked;
        }

        if (stopDiscordBotButton != null) {
            stopDiscordBotButton.clicked -= OnStopDiscordBotClicked;
        }

        if (saveSettingsButton != null) {
            saveSettingsButton.clicked -= SaveSettingsFromUI;
        }

        // DiscordBotの状態変更イベントを解除
        DiscordBotClient.OnDiscordBotStateChanged -= OnDiscordBotStateChanged;
    }

    // 設定値をUIに読み込むメソッド
    private void LoadSettingsToUI() {
        // PlayerPrefsから読み込み、UIに設定
        if (autoStartSubtitleAIToggle != null) {
            autoStartSubtitleAIToggle.value = CentralManager.Instance.GetAutoStartSubtitleAI();
        }

        if (subtitleAIExecutionPathInput != null) {
            subtitleAIExecutionPathInput.value = CentralManager.Instance.GetSubtitleAIExecutionPath();
        }

        if (autoStartVoiceVoxToggle != null) {
            autoStartVoiceVoxToggle.value = CentralManager.Instance.GetAutoStartVoiceVox();
        }

        if (voiceVoxExecutionPathInput != null) {
            voiceVoxExecutionPathInput.value = CentralManager.Instance.GetVoiceVoxExecutionPath();
        }

        if (autoStartMenzTranslationToggle != null) {
            autoStartMenzTranslationToggle.value = CentralManager.Instance.GetAutoStartMenzTranslation();
        }

        if (menzTranslationExecutionPathInput != null) {
            menzTranslationExecutionPathInput.value = CentralManager.Instance.GetMenzTranslationExecutionPath();
        }

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
        if (friendSubtitleInput != null) {
            friendSubtitleInput.value = CentralManager.Instance.GetFriendSubtitle();
        }

        if (deepLApiClientKeyInput != null) {
            deepLApiClientKeyInput.value = CentralManager.Instance.GetDeepLApiClientKey();
        }

        if (translationModeDropdown != null) {
            Debug.Log("翻訳方式ドロップダウンの初期化を開始");
            
            // CentralManagerインスタンスの確認
            if (CentralManager.Instance == null) {
                Debug.LogError("CentralManager.Instanceがnullです！");
                return;
            }
            
            // ドロップダウンの選択肢を設定
            var choices = new List<string> { "deepl", "menz" };
            translationModeDropdown.choices = choices;
            Debug.Log($"ドロップダウンの選択肢を設定しました: {string.Join(", ", choices)}");
            
            // 現在の設定値を取得
            string currentMode = CentralManager.Instance.GetTranslationMode();
            Debug.Log($"現在の翻訳方式: '{currentMode}'");
            
            // 値を設定
            translationModeDropdown.value = currentMode;
            Debug.Log($"ドロップダウンの値を設定しました: '{translationModeDropdown.value}'");
            
            // 選択肢に現在の値が含まれているかチェック
            if (!choices.Contains(currentMode)) {
                Debug.LogWarning($"現在の設定値 '{currentMode}' が選択肢に含まれていません。デフォルト値を設定します。");
                translationModeDropdown.value = "deepl";
                CentralManager.Instance.SetTranslationMode("deepl");
            }
        } else {
            Debug.LogError("translationModeDropdownがnullです！");
        }

        if (menZTranslationServerUrlInput != null) {
            menZTranslationServerUrlInput.value = CentralManager.Instance.GetMenZTranslationServerUrl();
        }

        if (autoStartDiscordBotToggle != null) {
            autoStartDiscordBotToggle.value = CentralManager.Instance.GetAutoStartDiscordBot();
        }

        // Discord設定の読み込み
        if (discordTokenInput != null) {
            discordTokenInput.value = CentralManager.Instance.GetDiscordToken();
        }
        if (discordGuildIdInput != null) {
            discordGuildIdInput.value = CentralManager.Instance.GetDiscordGuildId();
        }
        if (discordVoiceChannelIdInput != null) {
            discordVoiceChannelIdInput.value = CentralManager.Instance.GetDiscordVoiceChannelId();
        }
        if (discordTextChannelIdInput != null) {
            discordTextChannelIdInput.value = CentralManager.Instance.GetDiscordTextChannelId();
        }
        if (discordTargetUserIdInput != null) {
            discordTargetUserIdInput.value = CentralManager.Instance.GetDiscordTargetUserId();
        }
        if (discordInputNameInput != null) {
            discordInputNameInput.value = CentralManager.Instance.GetDiscordInputName();
        }
        if (discordSubtitleMethodDropdown != null) {
            Debug.Log("Discord字幕方式ドロップダウンの初期化を開始");
            
            // ドロップダウンの選択肢を設定（DiscordBotClient.SubtitleMethodに基づく）
            var discordChoices = new List<string> { "WitAI", "FasterWhisper" };
            discordSubtitleMethodDropdown.choices = discordChoices;
            Debug.Log($"Discord字幕方式ドロップダウンの選択肢を設定しました: {string.Join(", ", discordChoices)}");
            
            // 現在の設定値を取得（整数値を文字列に変換）
            int currentMethodIndex = CentralManager.Instance.GetDiscordSubtitleMethod();
            string currentMethodName = discordChoices[Mathf.Clamp(currentMethodIndex, 0, discordChoices.Count - 1)];
            Debug.Log($"現在のDiscord字幕方式: {currentMethodIndex} ({currentMethodName})");
            
            // 値を設定
            discordSubtitleMethodDropdown.value = currentMethodName;
            Debug.Log($"Discord字幕方式ドロップダウンの値を設定しました: '{discordSubtitleMethodDropdown.value}'");
        }
        if (discordWitaiTokenInput != null) {
            discordWitaiTokenInput.value = CentralManager.Instance.GetDiscordWitaiToken();
        }
        

        Debug.Log("設定UIに値をロードしました。");
    }

    // UIから設定値を読み込み、保存するメソッド
    private void SaveSettingsFromUI() {
        if (autoStartSubtitleAIToggle != null) {
            CentralManager.Instance.SetAutoStartSubtitleAI(autoStartSubtitleAIToggle.value);
        }

        if (subtitleAIExecutionPathInput != null) {
            CentralManager.Instance.SetSubtitleAIExecutionPath(subtitleAIExecutionPathInput.value);
        }

        if (autoStartVoiceVoxToggle != null) {
            CentralManager.Instance.SetAutoStartVoiceVox(autoStartVoiceVoxToggle.value);
        }

        if (voiceVoxExecutionPathInput != null) {
            CentralManager.Instance.SetVoiceVoxExecutionPath(voiceVoxExecutionPathInput.value);
        }

        if (autoStartMenzTranslationToggle != null) {
            CentralManager.Instance.SetAutoStartMenzTranslation(autoStartMenzTranslationToggle.value);
        }

        if (menzTranslationExecutionPathInput != null) {
            CentralManager.Instance.SetMenzTranslationExecutionPath(menzTranslationExecutionPathInput.value);
        }

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
        if (friendSubtitleInput != null) {
            CentralManager.Instance.SetFriendSubtitle(friendSubtitleInput.value);
        }

        if (deepLApiClientKeyInput != null) {
            CentralManager.Instance.SetDeepLApiClientKey(deepLApiClientKeyInput.value);
        }

        if (translationModeDropdown != null) {
            CentralManager.Instance.SetTranslationMode(translationModeDropdown.value);
        }

        if (menZTranslationServerUrlInput != null) {
            CentralManager.Instance.SetMenZTranslationServerUrl(menZTranslationServerUrlInput.value);
        }

        if (autoStartDiscordBotToggle != null) {
            CentralManager.Instance.SetAutoStartDiscordBot(autoStartDiscordBotToggle.value);
        }

        // Discord設定の保存
        if (discordTokenInput != null) {
            CentralManager.Instance.SetDiscordToken(discordTokenInput.value);
        }
        if (discordGuildIdInput != null) {
            CentralManager.Instance.SetDiscordGuildId(discordGuildIdInput.value);
        }
        if (discordVoiceChannelIdInput != null) {
            CentralManager.Instance.SetDiscordVoiceChannelId(discordVoiceChannelIdInput.value);
        }
        if (discordTextChannelIdInput != null) {
            CentralManager.Instance.SetDiscordTextChannelId(discordTextChannelIdInput.value);
        }
        if (discordTargetUserIdInput != null) {
            CentralManager.Instance.SetDiscordTargetUserId(discordTargetUserIdInput.value);
        }
        if (discordInputNameInput != null) {
            CentralManager.Instance.SetDiscordInputName(discordInputNameInput.value);
        }
        if (discordSubtitleMethodDropdown != null) {
            // 文字列を整数値に変換して保存
            var choices = new List<string> { "WitAI", "FasterWhisper" };
            int methodIndex = choices.IndexOf(discordSubtitleMethodDropdown.value);
            if (methodIndex >= 0) {
                CentralManager.Instance.SetDiscordSubtitleMethod(methodIndex);
            }
        }
        if (discordWitaiTokenInput != null) {
            CentralManager.Instance.SetDiscordWitaiToken(discordWitaiTokenInput.value);
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

    // --- 共通の実行ファイル選択メソッド ---
    private void BrowseExecutableFile(string title, string currentPath, System.Action<string> onPathSelected) {
        // OS別の実行ファイル拡張子を設定
        var extensions = GetExecutableExtensions();

        // デフォルトパスを設定
        string defaultPath = GetDefaultBrowsePath(currentPath);

        // ファイル選択ダイアログを開く
        string[] paths = StandaloneFileBrowser.OpenFilePanel(title, defaultPath, extensions, false);

        if (paths.Length > 0 && !string.IsNullOrEmpty(paths[0])) {
            onPathSelected?.Invoke(paths[0]);
        }
    }

    // --- OS別の実行ファイル拡張子を取得 ---
    private ExtensionFilter[] GetExecutableExtensions() {
        return new ExtensionFilter[] {
            #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            new ExtensionFilter("Execute Files", "exe", "bat", "cmd"),
            #elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            new ExtensionFilter("Execute Files", "sh", "command", "app"),
            #elif UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
            new ExtensionFilter("Execute Files", "sh"),
            #else
            new ExtensionFilter("Execute Files", "exe", "sh", "bat", "cmd"),
            #endif
            new ExtensionFilter("All Files", "*"),
        };
    }

    // --- デフォルトの参照パスを取得 ---
    private string GetDefaultBrowsePath(string currentPath) {
        if (string.IsNullOrEmpty(currentPath)) {
            #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            #else
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
            #endif
        } else {
            try {
                return System.IO.Path.GetDirectoryName(currentPath);
            } catch {
                #if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
                #else
                return System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                #endif
            }
        }
    }

    // --- 参照ボタンのクリックハンドラ ---
    private void OnBrowseSubtitleAIPathClicked() {
        Debug.Log("字幕AIパス参照ボタンがクリックされました。");
        
        BrowseExecutableFile(
            "字幕AI実行ファイルを選択",
            CentralManager.Instance.GetSubtitleAIExecutionPath(),
            (selectedPath) => {
                // 選択されたパスをTextFieldに設定
                subtitleAIExecutionPathInput.value = selectedPath;
                
                // CentralManagerに保存
                CentralManager.Instance.SetSubtitleAIExecutionPath(selectedPath);
                
                Debug.Log($"字幕AIパスが設定されました: {selectedPath}");
            }
        );
    }

    // --- 手動起動ボタンのクリックハンドラ ---
    private void OnStartSubtitleAIClicked() {
        Debug.Log("字幕AI手動起動ボタンがクリックされました");
        CentralManager.Instance.StartSubtitleAI();
    }

    // --- VoiceVox参照ボタンのクリックハンドラ ---
    private void OnBrowseVoiceVoxPathClicked() {
        Debug.Log("VoiceVoxパス参照ボタンがクリックされました。");
        
        BrowseExecutableFile(
            "VoiceVox実行ファイルを選択",
            CentralManager.Instance.GetVoiceVoxExecutionPath(),
            (selectedPath) => {
                // 選択されたパスをTextFieldに設定
                voiceVoxExecutionPathInput.value = selectedPath;
                
                // CentralManagerに保存
                CentralManager.Instance.SetVoiceVoxExecutionPath(selectedPath);
                
                Debug.Log($"VoiceVoxパスが設定されました: {selectedPath}");
            }
        );
    }

    // --- VoiceVox手動起動ボタンのクリックハンドラ ---
    private void OnStartVoiceVoxClicked() {
        Debug.Log("VoiceVox手動起動ボタンがクリックされました");
        CentralManager.Instance.StartVoiceVox();
    }

    // --- MenzTranslation参照ボタンのクリックハンドラ ---
    private void OnBrowseMenzTranslationPathClicked() {
        Debug.Log("MenzTranslationパス参照ボタンがクリックされました。");
        
        BrowseExecutableFile(
            "MenzTranslation実行ファイルを選択",
            CentralManager.Instance.GetMenzTranslationExecutionPath(),
            (selectedPath) => {
                // 選択されたパスをTextFieldに設定
                menzTranslationExecutionPathInput.value = selectedPath;
                
                // CentralManagerに保存
                CentralManager.Instance.SetMenzTranslationExecutionPath(selectedPath);
                
                Debug.Log($"MenzTranslationパスが設定されました: {selectedPath}");
            }
        );
    }

    // --- MenzTranslation手動起動ボタンのクリックハンドラ ---
    private void OnStartMenzTranslationClicked() {
        Debug.Log("MenzTranslation手動起動ボタンがクリックされました");
        CentralManager.Instance.StartMenzTranslation();
    }

    // --- Discord BOT手動起動ボタンのクリックハンドラ ---
    private void OnStartDiscordBotClicked() {
        Debug.Log("Discord BOT手動起動ボタンがクリックされました");
        CentralManager.Instance.StartDiscordBot();
    }

    // --- Discord BOT停止ボタンのクリックハンドラ ---
    private void OnStopDiscordBotClicked() {
        Debug.Log("Discord BOT停止ボタンがクリックされました");
        CentralManager.Instance.StopDiscordBot();
    }

    // --- DiscordBotの状態変更イベントハンドラ ---
    private void OnDiscordBotStateChanged(bool isRunning) {
        Debug.Log($"DiscordBot state changed event received - IsRunning: {isRunning}");
        UpdateDiscordBotButtons();
    }

    // --- DiscordBotボタンの有効/無効を更新 ---
    private void UpdateDiscordBotButtons() {
        bool isRunning = CentralManager.Instance.IsDiscordBotRunning();
        
        if (startDiscordBotButton != null) {
            startDiscordBotButton.SetEnabled(!isRunning);
        }
        if (stopDiscordBotButton != null) {
            stopDiscordBotButton.SetEnabled(isRunning);
        }
    }
}