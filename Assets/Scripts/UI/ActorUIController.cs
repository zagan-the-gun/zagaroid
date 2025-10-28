using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

public class ActorUIController : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;
    [Header("Fonts")]
    [SerializeField] private Font japaneseFont; // 再描画時に見出しへ直適用
    [Header("UI Templates")]
    [SerializeField] private VisualTreeAsset actorDeleteDialogTemplate;
    [SerializeField] private StyleSheet actorDeleteDialogStyle;
    [Header("Defaults")]
    [SerializeField] private int defaultActorCount = 0; // 起動時に既存が無ければ生成する数

    // ランタイムの最小モデル
    // ActorConfig は Models/ActorConfig.cs に分離

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

#if UNITY_EDITOR
	private void AssignActorDeleteDialogAssetsIfMissingEditorOnly()
	{
		if (actorDeleteDialogTemplate == null)
		{
			actorDeleteDialogTemplate = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI Toolkit/ActorDeleteConfirmDialog.uxml");
		}
		if (actorDeleteDialogStyle == null)
		{
			actorDeleteDialogStyle = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI Toolkit/ActorDeleteConfirmDialog.uss");
		}
	}

	private void OnValidate()
	{
		AssignActorDeleteDialogAssetsIfMissingEditorOnly();
	}
#endif

    private void OnEnable()
    {
        // UIDocument の自動検出（未設定でも動くように）
        if (uiDocument == null)
        {
            uiDocument = GetComponent<UIDocument>();
            if (uiDocument == null)
            {
                uiDocument = FindObjectOfType<UIDocument>(true);
            }
        }
        if (uiDocument == null || uiDocument.rootVisualElement == null)
        {
            Debug.LogError($"{LogPrefix} UIDocument が見つかりません。Scene へアタッチし参照を設定してください。");
            return;
        }

        uiRoot = uiDocument.rootVisualElement;
        actorContentRoot = uiRoot.Q<VisualElement>("actorContent");
        if (actorContentRoot == null)
        {
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
        // 2列レイアウト: USS クラスで管理
        actorGridRoot.AddToClassList("actors-grid");

        // 追加ボタンのある既存行からタイル参照を取得
        panelActorAddTile = actorContentRoot.Q<VisualElement>("PanelActorAdd");
        if (panelActorAddTile != null) panelActorAddTile.AddToClassList("actors-cell");

        // 追加ボタンを取得（中身のButton）
        addButton = actorContentRoot.Q<Button>("ActorAddButton");
        if (addButton != null)
        {
            addButton.clicked -= OnAddActorClicked;
            addButton.clicked += OnAddActorClicked;
            addButton.UnregisterCallback<ClickEvent>(OnAddActorClickedEvent);
            addButton.RegisterCallback<ClickEvent>(OnAddActorClickedEvent);
            Debug.Log($"{LogPrefix} Add ボタンをバインドしました: name={addButton.name}, enabled={addButton.enabledSelf}, display={addButton.resolvedStyle.display}");
        }
        else
        {
            Debug.LogWarning($"{LogPrefix} Add ボタンが見つかりません。");
        }

		#if UNITY_EDITOR
		AssignActorDeleteDialogAssetsIfMissingEditorOnly();
		#endif
		EnsureConfirmOverlay();

        // 共通Saveボタンにフック（全タブ一括保存）
        uiRoot.Query<Button>("SaveSettingsButton").ForEach(b =>
        {
            b.clicked -= OnSaveAllClicked;
            b.clicked += OnSaveAllClicked;
        });

        // CentralManager からロード。無ければ UXML から初期化
        LoadActorsFromCentral();
        if (actors.Count == 0)
        {
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
    private void BootstrapActorsFromExisting()
    {
        if (actorGridRoot == null) return;
        if (actors.Count > 0) return;
        foreach (var child in actorGridRoot.Children())
        {
            if (child == null) continue;
            if (child.name == "PanelActorAdd") continue;
            var nameField = child.Q<TextField>("ActorNameInput");
            var idField = child.Q<TextField>("DiscordTargetUserIdInput");
            var toggle = child.Q<Toggle>("EnableActorToggle");
            if (nameField == null && idField == null && toggle == null) continue;
            var cfg = new ActorConfig
            {
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

    private void OnAddActorClickedEvent(ClickEvent evt)
    {
        Debug.Log($"{LogPrefix} ActorAddButton clicked (ClickEvent)");
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
    private VisualElement CreateActorPanel(ActorConfig config)
    {
        var panel = new VisualElement();
        panel.AddToClassList("panel");
        panel.AddToClassList("actors-cell");

        var title = new Label(config.actorName + "設定" ?? string.Empty);
        title.AddToClassList("panel__title");
        panel.Add(title);

        var toggle = new Toggle("Enable") { name = "EnableActorToggle" };
        toggle.value = config.enabled;
        toggle.RegisterValueChangedCallback(evt =>
        {
            config.enabled = evt.newValue;
        });
        toggle.style.marginBottom = 5;
        panel.Add(toggle);

        var nameField = new TextField("actor name") { name = "ActorNameInput" };
        nameField.value = config.actorName;
        nameField.RegisterValueChangedCallback(evt =>
        {
            string sanitized = SanitizeActorName(evt.newValue);
            if (sanitized != evt.newValue)
            {
                nameField.SetValueWithoutNotify(sanitized);
            }
            config.actorName = sanitized; // 正規化なし、入力通り（非ASCII除去のみ）
            title.text = sanitized + "設定";
        });
        panel.Add(nameField);

        var idField = new TextField("Discord User ID") { name = "DiscordTargetUserIdInput" };
        idField.value = config.discordUserId;
        idField.style.marginBottom = 5;
        idField.RegisterValueChangedCallback(evt =>
        {
            config.discordUserId = evt.newValue;
        });
        panel.Add(idField);

        var deleteBtn = new Button() { text = "Delete", name = "ActorDeleteButton" };
        deleteBtn.AddToClassList("panel__button");
        deleteBtn.clicked += () => RequestDelete(panel, config);
        panel.Add(deleteBtn);

        return panel;
    }

    // ============ Grid Rebuild ============
    private void RebuildGrid()
    {
        if (actorGridRoot == null) return;

        // ルートをクリア
        actorGridRoot.Clear();

        // USSに完全委譲: 行ラッパーを使わずフラットに追加
        foreach (var cfg in actors)
        {
            actorGridRoot.Add(CreateActorPanel(cfg));
        }
        // Add タイルを末尾へ
        panelActorAddTile?.RemoveFromHierarchy();
        if (panelActorAddTile != null)
        {
            actorGridRoot.Add(panelActorAddTile);
        }

        // 再配置後の再取得・再バインド（Buttonは移動で参照が変わる可能性）
        var latestAddBtn = panelActorAddTile?.Q<Button>("ActorAddButton") ?? actorContentRoot.Q<Button>("ActorAddButton");
        if (latestAddBtn != null)
        {
            addButton.clicked -= OnAddActorClicked;
            addButton = latestAddBtn;
            addButton.clicked -= OnAddActorClicked;
            addButton.clicked += OnAddActorClicked;
            addButton.UnregisterCallback<ClickEvent>(OnAddActorClickedEvent);
            addButton.RegisterCallback<ClickEvent>(OnAddActorClickedEvent);
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

    private void FocusLastActorNameField()
    {
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
    private void LoadActorsFromCentral()
    {
        actors.Clear();
        var list = CentralManager.Instance?.GetActors();
        if (list == null) return;
        actors.AddRange(list);
    }

    private void SaveActorsToCentral()
    {
        CentralManager.Instance?.SetActors(actors);
    }

    // 共通Saveボタン押下時に呼ばれる
    private void OnSaveAllClicked()
    {
        SaveActorsToCentral();
        CentralManager.Instance?.SaveAllPlayerPrefs();
    }

    // 半角英数のみ許容（A-Za-z0-9）。それ以外は除去。
    private static string SanitizeActorName(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return Regex.Replace(input, "[^A-Za-z0-9]", "");
    }

    // ============ Confirm Overlay ============
    private void EnsureConfirmOverlay()
    {
        if (confirmOverlay != null) return;
        if (uiRoot == null) return;

        // UXMLテンプレートから生成（Inspector参照）
        var template = actorDeleteDialogTemplate;
        if (template == null)
        {
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
        if (ss != null)
        {
            confirmOverlay.styleSheets.Add(ss);
        }

        // 部品取得
        confirmMessage = container.Q<Label>("ConfirmMessageLabel");
        confirmCancelButton = container.Q<Button>("ConfirmCancelButton");
        confirmOkButton = container.Q<Button>("ConfirmOkButton");

        // クリック動作
        if (confirmCancelButton != null)
        {
            confirmCancelButton.clicked += HideConfirm;
        }
        if (confirmOkButton != null)
        {
            confirmOkButton.clicked += () =>
            {
                var action = pendingConfirmAction;
                pendingConfirmAction = null;
                HideConfirm();
                action?.Invoke();
            };
        }

        // 日本語フォントを明示適用
        if (japaneseFont != null)
        {
            var jp = FontDefinition.FromFont(japaneseFont);
            if (confirmMessage != null) confirmMessage.style.unityFontDefinition = jp;
            if (confirmOkButton != null) confirmOkButton.style.unityFontDefinition = jp;
            if (confirmCancelButton != null) confirmCancelButton.style.unityFontDefinition = jp;
        }

        uiRoot.Add(confirmOverlay);
    }

    private void TeardownConfirmOverlay()
    {
        if (confirmOverlay != null)
        {
            confirmOverlay.RemoveFromHierarchy();
            confirmOverlay = null;
            confirmMessage = null;
            confirmOkButton = null;
            confirmCancelButton = null;
            pendingConfirmAction = null;
        }
    }

    private void ShowConfirm(string message, Action onOk)
    {
        if (confirmOverlay == null)
        {
            EnsureConfirmOverlay();
        }
        if (confirmOverlay == null)
        {
            Debug.LogError($"{LogPrefix} 確認ダイアログの生成に失敗しました。UXML/USSのInspector割り当てを確認してください。");
            return;
        }
        if (confirmMessage != null) confirmMessage.text = message;
        pendingConfirmAction = onOk;
        confirmOverlay.style.display = DisplayStyle.Flex;
        confirmOverlay.pickingMode = PickingMode.Position; // 背景操作ブロック
    }

    private void HideConfirm()
    {
        if (confirmOverlay == null) return;
        confirmOverlay.style.display = DisplayStyle.None;
        confirmOverlay.pickingMode = PickingMode.Ignore;
    }
}

// （フォント再適用ヘルパーは削除）


