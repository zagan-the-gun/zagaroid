using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;

public class TabController : MonoBehaviour{
    [SerializeField] private UIDocument uiDocument; 

    private VisualElement root;
    private VisualElement uiRootVE; // UIDocument のルート（パネル直下）
    private VisualElement mainContainer;
    private UnityEngine.UIElements.Button logButton;
    private UnityEngine.UIElements.Button settingButton;
    private UnityEngine.UIElements.Button objectButton;
    private VisualElement logContent;
    private VisualElement settingContent;
    private VisualElement objectContent;
    
    // 追加: トグル用要素
    private UnityEngine.UIElements.Button toggleButton;
    private VisualElement tabsContainer;
    private VisualElement contentDisplayArea;
    private bool isCollapsed = false; // 初期は展開状態（Contentを表示）

    // 追加: UGUI / UITK の操作モード切替（直接管理）
    [Header("UGUI Toggle Targets")]
    [SerializeField] private GraphicRaycaster[] raycastersToToggle;  // 最大化: 無効, 最小化: 有効
    [SerializeField] private UIDragMove[] dragMovesToToggle;         // 最大化: 無効, 最小化: 有効
    [Header("Options")]
    [SerializeField] private bool autoDiscover = true;               // シーンから不足分を自動検出
    private const string LogPrefix = "[ZAGARO][Tab]";

    /// <summary>
    /// UIDocument から必要な要素を取得し、ボタンのイベント配線と初期表示を設定します。
    /// 必要に応じて UGUI 側の対象も自動検出します。
    /// </summary>
    private void OnEnable() {
        // SerializedFieldでアタッチされたUIDocumentのルート要素を使用する
        VisualElement uiRoot = uiDocument.rootVisualElement;
        uiRootVE = uiRoot;

        // 各要素を名前で取得
        root = uiRoot.Q<VisualElement>("root");
        mainContainer = uiRoot.Q<VisualElement>("main");
        logButton = uiRoot.Q<UnityEngine.UIElements.Button>("LogButton");
        settingButton = uiRoot.Q<UnityEngine.UIElements.Button>("SettingButton");
        objectButton = uiRoot.Q<UnityEngine.UIElements.Button>("ObjectButton");
        logContent = uiRoot.Q<VisualElement>("logContent");
        settingContent = uiRoot.Q<VisualElement>("settingContent");
        objectContent = uiRoot.Q<VisualElement>("objectContent");
        toggleButton = uiRoot.Q<UnityEngine.UIElements.Button>("Toggle");
        tabsContainer = uiRoot.Q<VisualElement>("tabs");
        contentDisplayArea = uiRoot.Q<VisualElement>("contentDisplayArea");

        // ボタンにイベントリスナーを追加
        logButton.clicked += () => {
            ShowOnly(logContent);
        };
        settingButton.clicked += () => {
            ShowOnly(settingContent);
            SetExpanded(true); // 設定タブを開いたら展開して操作可能にする
        };
        objectButton.clicked += () => {
            ShowOnly(objectContent);
            SetExpanded(true); // 設定タブを開いたら展開して操作可能にする
        };

        if (toggleButton != null) {
            toggleButton.clicked += ToggleDrawer;
        }

        // 初期表示はLogにする
        ShowOnly(logContent);

        // 初期は閉じた状態（Tabs を下、Content 非表示）
        AutoDiscoverRefs();
        SetExpanded(false);
    }

    /// <summary>
    /// 指定の片方を表示し、もう片方を非表示にします。
    /// </summary>
    /// <param name="contentToShow">表示する要素</param>
    /// <param name="contentToHide">非表示にする要素</param>
    private void ShowContent(VisualElement contentToShow, VisualElement contentToHide) {
        contentToShow.style.display = DisplayStyle.Flex; // または DisplayStyle.Gridなど適切なもの
        contentToHide.style.display = DisplayStyle.None;
    }

    /// <summary>
    /// 渡された要素のみ表示し、同階の他コンテンツは全て非表示にします。
    /// </summary>
    private void ShowOnly(VisualElement target) {
        if (target == null) return;
        // 同列の候補: logContent / settingContent / objectContent
        VisualElement[] all = new VisualElement[] { logContent, settingContent, objectContent };
        foreach (var ve in all) {
            if (ve == null) continue;
            ve.style.display = (ve == target) ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    // トグルボタン押下で開閉を切り替え
    /// <summary>
    /// 折りたたみ/展開の状態をトグルします。
    /// </summary>
    private void ToggleDrawer() {
        SetExpanded(isCollapsed); // 現在が畳み状態なら展開、展開なら畳む
    }

    // 開閉状態の反映
    /// <summary>
    /// 折りたたみ/展開状態を反映し、UGUI の有効/無効・ピッキング制御を切り替えます。
    /// </summary>
    /// <param name="expanded">true: 展開, false: 折りたたみ</param>
    private void SetExpanded(bool expanded) {
        if (tabsContainer == null || contentDisplayArea == null) return;

        if (expanded) {
            // 表示: Content を表示、Tabs を上に、ボタンは「▼」
            contentDisplayArea.style.display = DisplayStyle.Flex;
            contentDisplayArea.style.flexGrow = 1;
            // UXML で max-height:150px が指定されているため、制限を解除
            contentDisplayArea.style.maxHeight = StyleKeyword.None;
            // Tabs を描画順の末尾へ（重なり順で最前面にする）
            MoveElementToIndex(root, tabsContainer, int.MaxValue);
            // タブを絶対配置で上固定し、最前面化
            if (tabsContainer != null) {
                tabsContainer.style.position = Position.Absolute;
                tabsContainer.style.top = 0;
                tabsContainer.style.left = 0;
                tabsContainer.style.right = 0;
                tabsContainer.style.height = 30;
            }
            if (contentDisplayArea != null) {
                contentDisplayArea.style.marginTop = 30; // タブの高さ分下げる
            }
            if (mainContainer != null) mainContainer.style.display = DisplayStyle.None; // Content を最大化
            if (toggleButton != null) toggleButton.text = "▼";
            isCollapsed = false;
            // UITK 最大化モード: Canvas 側の操作を停止
            SetRaycastersEnabled(false);
            SetDragsEnabled(false);
            Debug.Log($"{LogPrefix} Expanded -> UITK Maximized");

            // ピッキングを通常状態へ（全体操作可）
            RestorePickingExpanded();
        } else {
            // 非表示: Content を閉じる、Tabs を一番下に、ボタンは「▲」
            contentDisplayArea.style.display = DisplayStyle.None;
            // 絶対配置解除し、相対配置へ戻す
            if (tabsContainer != null) {
                tabsContainer.style.position = Position.Relative;
                tabsContainer.style.top = new StyleLength(StyleKeyword.Null);
                tabsContainer.style.left = new StyleLength(StyleKeyword.Null);
                tabsContainer.style.right = new StyleLength(StyleKeyword.Null);
                tabsContainer.style.height = new StyleLength(StyleKeyword.Null);
            }
            if (contentDisplayArea != null) {
                contentDisplayArea.style.marginTop = 0;
            }
            // Tabs を最下部へ（末尾に挿入）
            MoveElementToIndex(root, tabsContainer, int.MaxValue);
            if (mainContainer != null) {
                mainContainer.style.display = DisplayStyle.Flex;
            }
            if (toggleButton != null) toggleButton.text = "▲";
            isCollapsed = true;
            // UITK 最小化モード: プレビュー上のドラッグ等を許可
            SetRaycastersEnabled(true);
            SetDragsEnabled(true);
            Debug.Log($"{LogPrefix} Collapsed -> UITK Minimized");

            // ピッキングをホワイトリスト方式へ（トグルバーのみ操作可）
            ApplyPickingCollapsedWhitelist();
        }
    }

    /// <summary>
    /// 子要素を親内の指定インデックスへ移動します。
    /// </summary>
    /// <param name="parent">親要素</param>
    /// <param name="child">移動する子</param>
    /// <param name="targetIndex">挿入する位置</param>
    private void MoveElementToIndex(VisualElement parent, VisualElement child, int targetIndex) {
        if (parent == null || child == null) return;
        if (child.parent != parent) return;
        parent.Remove(child);
        int clampedIndex = Mathf.Clamp(targetIndex, 0, parent.childCount);
        parent.Insert(clampedIndex, child);
    }

    /// <summary>
    /// GraphicRaycaster 群の有効/無効を一括で切り替えます。
    /// </summary>
    /// <param name="enabled">有効にするか</param>
    private void SetRaycastersEnabled(bool enabled) {
        if (raycastersToToggle == null) return;
        foreach (var r in raycastersToToggle) {
            if (r == null) continue;
            r.enabled = enabled;
        }
    }

    /// <summary>
    /// UIDragMove 群の有効/無効を一括で切り替えます。
    /// </summary>
    /// <param name="enabled">有効にするか</param>
    private void SetDragsEnabled(bool enabled) {
        if (dragMovesToToggle == null) return;
        foreach (var d in dragMovesToToggle) {
            if (d == null) continue;
            d.enabled = enabled;
        }
    }

    /// <summary>
    /// 設定が空の場合のみ、シーンから Raycaster/UIDragMove を自動収集します。
    /// </summary>
    private void AutoDiscoverRefs() {
        if (!autoDiscover) return;
        var allRays = FindObjectsOfType<GraphicRaycaster>(true);
        if ((raycastersToToggle == null || raycastersToToggle.Length == 0) && allRays != null && allRays.Length > 0) {
            raycastersToToggle = allRays;
        }
        var allDrags = FindObjectsOfType<UIDragMove>(true);
        if ((dragMovesToToggle == null || dragMovesToToggle.Length == 0) && allDrags != null && allDrags.Length > 0) {
            dragMovesToToggle = allDrags;
        }
    }

    // ==== Picking control for collapsed/expanded ====
    /// <summary>
    /// 折りたたみ時のピッキング制御を適用します。ルートは Ignore、タブ/トグルのみ Position。
    /// </summary>
    private void ApplyPickingCollapsedWhitelist() {
        if (uiRootVE == null) return;
        // まず全体を Ignore（子のヒットテストは継続される）
        SetTreePicking(uiRootVE, PickingMode.Ignore);
        // タブ領域とトグルボタンだけ操作可
        if (tabsContainer != null) tabsContainer.pickingMode = PickingMode.Position;
        if (toggleButton != null) toggleButton.pickingMode = PickingMode.Position;
        Debug.Log($"{LogPrefix} Picking collapsed: root=Ignore, tabs/toggle=Position");
    }

    /// <summary>
    /// 展開時のピッキング制御を復元します（全て Position）。
    /// </summary>
    private void RestorePickingExpanded() {
        if (uiRootVE == null) return;
        SetTreePicking(uiRootVE, PickingMode.Position);
        Debug.Log($"{LogPrefix} Picking expanded: all=Position");
    }

    /// <summary>
    /// 指定したルート配下のツリー全体に一括で pickingMode を適用します。
    /// </summary>
    /// <param name="rootEl">ルート要素</param>
    /// <param name="mode">適用するモード</param>
    private void SetTreePicking(VisualElement rootEl, PickingMode mode) {
        if (rootEl == null) return;
        rootEl.pickingMode = mode;
        int count = rootEl.childCount;
        for (int i = 0; i < count; i++) {
            var child = rootEl[i];
            SetTreePicking(child, mode);
        }
    }
}