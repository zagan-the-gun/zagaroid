using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

public class ActorUIController : MonoBehaviour
{
    [Header("Fonts")]
    [SerializeField] private Font japaneseFont; // 再描画時に見出しへ直適用
    [Header("UI Templates")]
    [SerializeField] private VisualTreeAsset actorDeleteDialogTemplate;
    [SerializeField] private StyleSheet actorDeleteDialogStyle;
    [SerializeField] private VisualTreeAsset avatarSettingDialogTemplate;
    [SerializeField] private StyleSheet avatarSettingDialogStyle;

    private readonly List<ActorConfig> actors = new List<ActorConfig>();

    // ルート参照
    private VisualElement uiRoot;
    private VisualElement actorContentRoot;
    private Button addButton;

    // 動的にアクターパネルを積むコンテナ（#3 をグリッドルートとして使用）
    private VisualElement actorGridRoot;           // name="3" をグリッドルートとして使用

    // Addタイル（UXML定義済み）
    private VisualElement panelActorAddTile;      // name="PanelActorAdd"

    // 削除確認用の簡易オーバーレイ
    private VisualElement confirmOverlay;
    private Label confirmMessage;
    private Button confirmOkButton;
    private Button confirmCancelButton;
    private Action pendingConfirmAction;

    private const string LogPrefix = "[ZAGARO][ActorUI]";

    private void OnEnable()
    {
        // UIDocument の自動検出（Scene内に必ず1つ存在）
        UIDocument uiDocument = GetComponent<UIDocument>();
        if (uiDocument == null) {
            uiDocument = FindObjectOfType<UIDocument>(true);
        }
        if (uiDocument == null || uiDocument.rootVisualElement == null) {
            Debug.LogError($"{LogPrefix} UIDocument が見つかりません。");
            return;
        }

        uiRoot = uiDocument.rootVisualElement;
        actorContentRoot = uiRoot.Q<VisualElement>("actorContent");
        if (actorContentRoot == null) {
            Debug.LogError($"{LogPrefix} actorContent が見つかりません。");
            return;
        }

        // #3 をグリッドルートとして使用
        actorGridRoot = actorContentRoot.Q<VisualElement>("3");
        if (actorGridRoot == null)
        {
            Debug.LogError($"{LogPrefix} name='3' のコンテナが見つかりません。");
            return;
        }
        // 3列レイアウト: USS クラスで管理
        actorGridRoot.AddToClassList("actors-grid");

        // 追加ボタンのある既存行からタイル参照を取得
        panelActorAddTile = actorContentRoot.Q<VisualElement>("PanelActorAdd");

        // 追加ボタンを取得（中身のButton）
        addButton = actorContentRoot.Q<Button>("ActorAddButton");
        if (addButton != null) {
            addButton.clicked -= OnAddActorClicked;
            addButton.clicked += OnAddActorClicked;
            Debug.Log($"{LogPrefix} Add ボタンをバインドしました: name={addButton.name}, enabled={addButton.enabledSelf}, display={addButton.resolvedStyle.display}");
        } else {
            Debug.LogWarning($"{LogPrefix} Add ボタンが見つかりません。");
        }

		EnsureConfirmOverlay();

        // 共通Saveボタンにフック（全タブ一括保存）
        uiRoot.Query<Button>("SaveSettingsButton").ForEach(b => {
            b.clicked -= OnSaveAllClicked;
            b.clicked += OnSaveAllClicked;
        });

        // CentralManager からロード。無ければ UXML から初期化
        LoadActorsFromCentral();
        if (actors.Count == 0) {
            BootstrapActorsFromExisting();
        }
        RebuildGrid();
    }

    private void OnDisable()
    {
        if (addButton != null)
        {
            addButton.clicked -= OnAddActorClicked;
        }
        // 共通Saveボタンのフック解除
        if (uiRoot != null)
        {
            uiRoot.Query<Button>("SaveSettingsButton").ForEach(b =>
            {
                b.clicked -= OnSaveAllClicked;
            });
        }
        TeardownConfirmOverlay();
    }

    // 既存 UXML の PanelActor から actors を初期化
    private void BootstrapActorsFromExisting() {
        if (actorGridRoot == null) return;
        if (actors.Count > 0) return;
        foreach (var child in actorGridRoot.Children()) {
            if (child == null) continue;
            if (child.name == "PanelActorAdd") continue;
            var nameField = child.Q<TextField>("ActorNameInput");
            var idField = child.Q<TextField>("DiscordTargetUserIdInput");
            var toggle = child.Q<Toggle>("EnableActorToggle");
            if (nameField == null && idField == null && toggle == null) continue;
            var cfg = new ActorConfig {
                actorName = nameField != null ? (nameField.value ?? string.Empty) : string.Empty,
                discordUserId = idField != null ? (idField.value ?? string.Empty) : string.Empty,
                enabled = toggle != null ? toggle.value : true
            };
            actors.Add(cfg);
        }
    }

    // ============ Add / Delete ============
    private void OnAddActorClicked()
    {
        Debug.Log($"{LogPrefix} ActorAddButton clicked (clicked)");
        var config = new ActorConfig();
        config.actorName = "actor name";
        actors.Add(config);
        RebuildGrid();
        FocusLastActorNameField();
        Debug.Log($"{LogPrefix} Actor 追加。現在件数: {actors.Count}");
    }

    private void RequestDelete(VisualElement panel, ActorConfig config)
    {
        ShowConfirm(
            message: $"DELETE this Actor?",
            onOk: () =>
            {
                actors.Remove(config);
                RebuildGrid();
                Debug.Log($"{LogPrefix} Actor 削除。現在件数: {actors.Count}");
            }
        );
    }

    // ============ UI Building ============
    private VisualElement CreateActorPanel(ActorConfig config) {
        var panel = new VisualElement();
        panel.AddToClassList("panel");
        panel.AddToClassList("actors-cell");

        var title = new Label(config.actorName + "設定" ?? string.Empty);
        title.AddToClassList("panel__title");
        panel.Add(title);

        var toggle = new Toggle("Enable") { name = "EnableActorToggle" };
        toggle.value = config.enabled;
        toggle.RegisterValueChangedCallback(evt => {
            config.enabled = evt.newValue;
        });
        toggle.style.marginBottom = 5;
        panel.Add(toggle);

        // translation（全タイプで使用可能）
        var translationToggle = new Toggle("Translation") { name = "ActorTranslationToggle" };
        translationToggle.value = config.translationEnabled;
        translationToggle.RegisterValueChangedCallback(evt => {
            config.translationEnabled = evt.newValue;
        });
        translationToggle.style.marginBottom = 5;
        panel.Add(translationToggle);

        var nameField = new TextField("actor name") { name = "ActorNameInput" };
        nameField.value = config.actorName;
        nameField.RegisterValueChangedCallback(evt => {
            string sanitized = SanitizeActorName(evt.newValue);
            if (sanitized != evt.newValue) {
                nameField.SetValueWithoutNotify(sanitized);
            }
            config.actorName = sanitized;
            title.text = sanitized + "設定";
        });
        panel.Add(nameField);

        // display name（表示名）: actor nameの直下に配置
        var displayField = new TextField("display name") { name = "ActorDisplayNameInput" };
        displayField.value = config.displayName;
        displayField.RegisterValueChangedCallback(evt => {
            config.displayName = evt.newValue ?? string.Empty;
        });
        panel.Add(displayField);

        // Avatar 関連（すべてのタイプで表示）
        var avatarContainer = new VisualElement();
        avatarContainer.style.marginBottom = 5;
        avatarContainer.style.display = DisplayStyle.Flex;

        // 顔画像サムネイル（Image で表示）
        var avatarThumbnail = new Image();
        avatarThumbnail.name = "AvatarThumbnail";
        avatarThumbnail.style.height = 100;
        avatarThumbnail.style.paddingTop = 5;
        avatarThumbnail.style.paddingBottom = 5;
        avatarThumbnail.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        
        // 初期表示：最初のアニメパスから読み込む
        if (config.avatarAnimePaths.Count > 0) {
            var texture = LoadTextureFromPath(config.avatarAnimePaths[0]);
            if (texture != null) {
                avatarThumbnail.image = texture;
            }
        }
        
        avatarContainer.Add(avatarThumbnail);

        // Avatar Setting ボタン
        var avatarSettingBtn = new Button() { text = "Avatar Setting" };
        avatarSettingBtn.AddToClassList("panel__button");
        avatarSettingBtn.clicked += () => ShowAvatarSettingPanel(config, avatarThumbnail);
        avatarContainer.Add(avatarSettingBtn);

        panel.Add(avatarContainer);

        // type ドロップダウン（local / friend / wipe）- display name の直下に配置
        var idField = new TextField("Discord User ID") { name = "DiscordTargetUserIdInput" };
        var typeDropdown = new DropdownField("type") { name = "ActorTypeDropdown" };
        var typeChoices = new List<string> { "local", "friend", "wipe" };
        typeDropdown.choices = typeChoices;
        if (string.IsNullOrEmpty(config.type) || !typeChoices.Contains(config.type)) {
            config.type = "local";
        }
        typeDropdown.value = config.type;

        // TTS トグル（wipeタイプのときのみ表示）
        var ttsToggle = new Toggle("TTS") { name = "ActorTTSToggle" };
        ttsToggle.value = config.ttsEnabled;
        ttsToggle.RegisterValueChangedCallback(evt => {
            config.ttsEnabled = evt.newValue;
        });
        ttsToggle.style.marginBottom = 5;
        ttsToggle.style.display = (config.type == "wipe") ? DisplayStyle.Flex : DisplayStyle.None;

        typeDropdown.RegisterValueChangedCallback(evt => {
            config.type = evt.newValue;
            idField.style.display = (config.type == "friend") ? DisplayStyle.Flex : DisplayStyle.None;
            // Type 変更時に TTS の表示/非表示を制御
            ttsToggle.style.display = (config.type == "wipe") ? DisplayStyle.Flex : DisplayStyle.None;
        });
        typeDropdown.style.marginBottom = 5;
        panel.Add(typeDropdown);

        // TTS（テキスト読み上げ、wipeタイプのときのみ有効）
        panel.Add(ttsToggle);

        // Discord User ID（friend のときだけ表示）
        idField.value = config.discordUserId;
        idField.style.marginBottom = 5;
        idField.RegisterValueChangedCallback(evt => {
            config.discordUserId = evt.newValue;
        });
        idField.style.display = (config.type == "friend") ? DisplayStyle.Flex : DisplayStyle.None;
        panel.Add(idField);

        var deleteBtn = new Button() { text = "Delete", name = "ActorDeleteButton" };
        deleteBtn.AddToClassList("panel__button");
        deleteBtn.clicked += () => RequestDelete(panel, config);
        panel.Add(deleteBtn);

        return panel;
    }

    // ============ Grid Rebuild ============
    private void RebuildGrid() {
        if (actorGridRoot == null) return;

        // ルートをクリア
        actorGridRoot.Clear();

        // USSに完全委譲: 行ラッパーを使わずフラットに追加
        foreach (var cfg in actors) {
            actorGridRoot.Add(CreateActorPanel(cfg));
        }
        // Add タイルを末尾へ
        panelActorAddTile?.RemoveFromHierarchy();
        if (panelActorAddTile != null) {
            actorGridRoot.Add(panelActorAddTile);
        }

        // 再配置後の再取得・再バインド（Buttonは移動で参照が変わる可能性）
        var latestAddBtn = panelActorAddTile?.Q<Button>("ActorAddButton") ?? actorContentRoot.Q<Button>("ActorAddButton");
        if (latestAddBtn != null) {
            addButton.clicked -= OnAddActorClicked;
            addButton = latestAddBtn;
            addButton.clicked -= OnAddActorClicked;
            addButton.clicked += OnAddActorClicked;
            Debug.Log($"{LogPrefix} Add ボタンを再バインド: name={addButton.name}, enabled={addButton.enabledSelf}, display={addButton.resolvedStyle.display}");
        }

        // 再描画後に見出しラベルへ日本語フォントを再適用（次フレームで安全に）
        if (japaneseFont != null && actorContentRoot != null) {
            actorContentRoot.schedule.Execute(() => {
                var titleQuery = actorContentRoot.Query<Label>(null, "panel__title");
                titleQuery.ForEach(lbl => {
                    lbl.style.unityFontDefinition = FontDefinition.FromFont(japaneseFont);
                });
            }).StartingIn(0);
        }

    }

    private void FocusLastActorNameField() {
        if (actorGridRoot == null) return;
        if (actorGridRoot.childCount == 0) return;
        for (int idx = actorGridRoot.childCount - 1; idx >= 0; idx--)
        {
            var ve = actorGridRoot[idx];
            var tf = ve?.Q<TextField>("ActorNameInput");
            if (tf != null) { tf.Focus(); break; }
        }
    }

    // CentralManager連携: 保存/読込
    private void LoadActorsFromCentral() {
        actors.Clear();
        var list = CentralManager.Instance?.GetActors();
        if (list == null) return;
        actors.AddRange(list);
    }

    private void SaveActorsToCentral() {
        CentralManager.Instance?.SetActors(actors);
    }

    // 共通Saveボタン押下時に呼ばれる
    private void OnSaveAllClicked() {
        SaveActorsToCentral();
        CentralManager.Instance?.SaveAllPlayerPrefs();
    }

    // 半角英数のみ許容（A-Za-z0-9）。それ以外は除去。
    private static string SanitizeActorName(string input) {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return Regex.Replace(input, "[^A-Za-z0-9]", "");
    }

    // ============ Confirm Overlay ============
    private void EnsureConfirmOverlay() {
        if (confirmOverlay != null) return;
        if (uiRoot == null) return;

        // UXMLテンプレートから生成（Inspector参照）
        var template = actorDeleteDialogTemplate;
        if (template == null) {
            Debug.LogError($"{LogPrefix} ActorDeleteConfirmDialog の VisualTreeAsset が未設定です。Inspectorで割り当ててください。");
            return;
        }

        var container = template.Instantiate();
        var overlay = container.Q<VisualElement>("ActorDeleteConfirmOverlay");
        confirmOverlay = overlay ?? container;
        confirmOverlay.style.display = DisplayStyle.None;
        confirmOverlay.pickingMode = PickingMode.Ignore;
        confirmOverlay.BringToFront();

        // USSを適用（Inspector参照）
        var ss = actorDeleteDialogStyle;
        if (ss != null) {
            confirmOverlay.styleSheets.Add(ss);
        }

        // 部品取得
        confirmMessage = container.Q<Label>("ConfirmMessageLabel");
        confirmCancelButton = container.Q<Button>("ConfirmCancelButton");
        confirmOkButton = container.Q<Button>("ConfirmOkButton");

        // クリック動作
        if (confirmCancelButton != null) {
            confirmCancelButton.clicked += HideConfirm;
        }
        if (confirmOkButton != null) {
            confirmOkButton.clicked += () =>
            {
                var action = pendingConfirmAction;
                pendingConfirmAction = null;
                HideConfirm();
                action?.Invoke();
            };
        }

        // 日本語フォントを明示適用
        if (japaneseFont != null) {
            var jp = FontDefinition.FromFont(japaneseFont);
            if (confirmMessage != null) confirmMessage.style.unityFontDefinition = jp;
            if (confirmOkButton != null) confirmOkButton.style.unityFontDefinition = jp;
            if (confirmCancelButton != null) confirmCancelButton.style.unityFontDefinition = jp;
        }

        uiRoot.Add(confirmOverlay);
    }

    private void TeardownConfirmOverlay() {
        if (confirmOverlay != null) {
            confirmOverlay.RemoveFromHierarchy();
            confirmOverlay = null;
            confirmMessage = null;
            confirmOkButton = null;
            confirmCancelButton = null;
            pendingConfirmAction = null;
        }
    }

    private void ShowConfirm(string message, Action onOk) {
        if (confirmOverlay == null) {
            EnsureConfirmOverlay();
        }
        if (confirmOverlay == null) {
            Debug.LogError($"{LogPrefix} 確認ダイアログの生成に失敗しました。UXML/USSのInspector割り当てを確認してください。");
            return;
        }
        if (confirmMessage != null) confirmMessage.text = message;
        pendingConfirmAction = onOk;
        confirmOverlay.style.display = DisplayStyle.Flex;
        confirmOverlay.pickingMode = PickingMode.Position; // 背景操作ブロック
        confirmOverlay.BringToFront(); // 最前面に表示
    }

    private void HideConfirm() {
        if (confirmOverlay == null) return;
        confirmOverlay.style.display = DisplayStyle.None;
        confirmOverlay.pickingMode = PickingMode.Ignore;
    }

    // ============ Avatar Setting Panel ============
    private void ShowAvatarSettingPanel(ActorConfig config, Image avatarThumbnail) {
        Debug.Log($"[ActorUI] Avatar Setting Panel opened for {config.actorName}");
        
        // パネルが既に存在する場合は削除
        if (uiRoot == null) return;
        var existingPanel = uiRoot.Q<VisualElement>("AvatarSettingOverlay");
        if (existingPanel != null) {
            existingPanel.RemoveFromHierarchy();
        }

        // UXML テンプレートから生成
        var template = avatarSettingDialogTemplate;
        if (template == null) {
            Debug.LogError($"{LogPrefix} AvatarSettingDialog の VisualTreeAsset が未設定です。Inspectorで割り当ててください。");
            return;
        }

        var container = template.Instantiate();
        var overlay = container.Q<VisualElement>("AvatarSettingOverlay");
        if (overlay == null) {
            Debug.LogError($"{LogPrefix} AvatarSettingOverlay が見つかりません。");
            return;
        }

        // USS を適用
        if (avatarSettingDialogStyle != null) {
            overlay.styleSheets.Add(avatarSettingDialogStyle);
        }

        // タイトルを設定
        var titleLabel = overlay.Q<Label>("DialogTitle");
        if (titleLabel != null) {
            titleLabel.text = $"{config.actorName} - Avatar Setting";
            if (japaneseFont != null) {
                titleLabel.style.unityFontDefinition = FontDefinition.FromFont(japaneseFont);
            }
        }

        // パスコンテナを取得
        var animePathsContainer = overlay.Q<VisualElement>("AnimePathsContainer");
        var lipSyncPathsContainer = overlay.Q<VisualElement>("LipSyncPathsContainer");

        if (animePathsContainer != null) {
            RefreshAvatarPathsList(
                config.avatarAnimePaths,
                animePathsContainer,
                () => RefreshAvatarPathsList(config.avatarAnimePaths, animePathsContainer, () => { }, () => { }),
                () => RefreshAvatarPathsList(config.avatarAnimePaths, animePathsContainer, () => { }, () => { })
            );
        }

        if (lipSyncPathsContainer != null) {
            RefreshAvatarPathsList(
                config.avatarLipSyncPaths,
                lipSyncPathsContainer,
                () => RefreshAvatarPathsList(config.avatarLipSyncPaths, lipSyncPathsContainer, () => { }, () => { }),
                () => RefreshAvatarPathsList(config.avatarLipSyncPaths, lipSyncPathsContainer, () => { }, () => { })
            );
        }

        // Avatar Scale フィールドを設定
        var scaleField = overlay.Q<FloatField>("AvatarScaleInput");
        if (scaleField != null) {
            scaleField.value = config.avatarDisplayScale;
            scaleField.RegisterValueChangedCallback(evt => {
                config.avatarDisplayScale = Mathf.Clamp(evt.newValue, 0.1f, 5.0f); // 0.1～5.0 に制限
                scaleField.SetValueWithoutNotify(config.avatarDisplayScale);
            });
        }

        // ボタンイベント設定
        var saveBtn = overlay.Q<Button>("SaveButton");
        var cancelBtn = overlay.Q<Button>("CancelButton");

        if (saveBtn != null) {
            saveBtn.clicked += () => {
                // Avatar Scaleの値を保存（既にRegisterValueChangedCallbackで更新されているが、念のため）
                if (scaleField != null) {
                    config.avatarDisplayScale = Mathf.Clamp(scaleField.value, 0.1f, 5.0f);
                }
                
                SaveActorsToCentral();
                
                // Canvas の RawImage に即座に反映
                CentralManager.Instance?.SetActors(actors);
                
                // サムネイル更新
                int animeCount = config.avatarAnimePaths.Count;
                int lipSyncCount = config.avatarLipSyncPaths.Count;
                if (animeCount > 0 || lipSyncCount > 0) {
                    avatarThumbnail.image = LoadTextureFromPath(config.avatarAnimePaths.Count > 0 ? config.avatarAnimePaths[0] : null);
                    if (avatarThumbnail.image == null) {
                        avatarThumbnail.image = LoadTextureFromPath(config.avatarLipSyncPaths.Count > 0 ? config.avatarLipSyncPaths[0] : null);
                    }
                    if (avatarThumbnail.image == null) {
                        avatarThumbnail.image = LoadTextureFromPath(null); // デフォルト画像
                    }
                } else {
                    avatarThumbnail.image = null; // 画像がない場合は null にする
                }
                
                Debug.Log($"[ActorUI] Avatar Setting saved for {config.actorName}");
                overlay.RemoveFromHierarchy();
            };
        }

        if (cancelBtn != null) {
            cancelBtn.clicked += () => {
                overlay.RemoveFromHierarchy();
            };
        }

        // 背景クリックで閉じる
        overlay.RegisterCallback<MouseDownEvent>(evt => {
            if (evt.target == overlay) {
                overlay.RemoveFromHierarchy();
            }
        });

        uiRoot.Add(overlay);
    }

    private void RefreshAvatarPathsList(
        List<string> pathsList,
        VisualElement container,
        Action onPathAdded,
        Action onPathRemoved
    ) {
        container.Clear();

        // 既存パスのサムネイルボタン表示
        for (int i = 0; i < pathsList.Count; i++) {
            int indexCopy = i;
            var thumbnailBtn = CreateThumbnailButton(pathsList[i], indexCopy, () => {
                ShowConfirm(
                    "Delete this path?",
                    () => {
                        pathsList.RemoveAt(indexCopy);
                        onPathRemoved?.Invoke();
                    }
                );
            });
            container.Add(thumbnailBtn);
        }

        // パス追加用ボタン（"+" ボタン）
        var addPathBtn = new Button();
        addPathBtn.text = "+";
        addPathBtn.AddToClassList("avatar-thumbnail-add-btn");
        addPathBtn.clicked += () => {
            OpenFileDialog((selectedPath) => {
                if (!string.IsNullOrEmpty(selectedPath) && !pathsList.Contains(selectedPath)) {
                    pathsList.Add(selectedPath);
                    onPathAdded?.Invoke();
                }
            });
        };
        container.Add(addPathBtn);
    }

    private Button CreateThumbnailButton(string path, int index, Action onDelete) {
        var btn = new Button();
        btn.text = "";  // テキストを空にしてサムネイル画像を表示
        btn.AddToClassList("avatar-thumbnail-btn");

        var imageContainer = new VisualElement();
        imageContainer.AddToClassList("avatar-thumbnail-btn__image");
        imageContainer.pickingMode = PickingMode.Ignore;  // クリックを親に透す

        // パスから画像を読み込んで表示
        var texture = LoadTextureFromPath(path);
        if (texture != null) {
            var image = new Image();
            image.image = texture;
            image.style.width = Length.Percent(100);
            image.style.height = Length.Percent(100);
            image.pickingMode = PickingMode.Ignore;  // クリックを親に透す
            imageContainer.Add(image);
        } else {
            // 読み込み失敗時はパスの一部を表示
            var fallbackLabel = new Label(System.IO.Path.GetFileName(path));
            fallbackLabel.style.fontSize = 10;
            fallbackLabel.style.color = new Color(0.8f, 0.8f, 0.8f, 1);
            fallbackLabel.style.whiteSpace = WhiteSpace.Normal;
            fallbackLabel.style.overflow = Overflow.Hidden;
            fallbackLabel.pickingMode = PickingMode.Ignore;  // クリックを親に透す
            imageContainer.Add(fallbackLabel);
        }

        btn.Add(imageContainer);
        btn.clicked += onDelete;

        return btn;
    }

    private void OpenFileDialog(Action<string> onPathSelected) {
        #if UNITY_EDITOR
        string selectedPath = UnityEditor.EditorUtility.OpenFilePanel(
            "Select Image File",
            System.IO.Path.GetDirectoryName(Application.persistentDataPath),
            "png,jpg,jpeg,gif,bmp"
        );
        if (!string.IsNullOrEmpty(selectedPath)) {
            onPathSelected(selectedPath);
        }
        #else
        // ランタイムではファイルダイアログが使用できないため、パス入力で対応
        Debug.Log($"[ActorUI] File dialog not available in runtime. Please manually enter the file path.");
        #endif
    }

    private Texture2D LoadTextureFromPath(string path) {
        if (string.IsNullOrEmpty(path)) return null;
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        Texture2D texture = new Texture2D(2, 2); // テクスチャを作成
        if (texture.LoadImage(bytes)) {
            return texture;
        }
        Debug.LogError($"{LogPrefix} Failed to load texture from path: {path}");
        return null;
    }
}
