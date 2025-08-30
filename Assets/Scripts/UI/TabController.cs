using UnityEngine;
using UnityEngine.UIElements;

public class TabController : MonoBehaviour{
    // public VisualTreeAsset uiDocument; // UXMLをInspectorからアタッチ
    [SerializeField] private UIDocument uiDocument; 

    private VisualElement root;
    private VisualElement mainContainer;
    private Button logButton;
    private Button settingButton;
    private VisualElement logContent;
    private VisualElement settingContent;
    
    // 追加: トグル用要素
    private Button toggleButton;
    private VisualElement tabsContainer;
    private VisualElement contentDisplayArea;
    private bool isCollapsed = false; // 初期は展開状態（Contentを表示）

    private void OnEnable() {
        // SerializedFieldでアタッチされたUIDocumentのルート要素を使用する
        VisualElement uiRoot = uiDocument.rootVisualElement;

        // 各要素を名前で取得
        root = uiRoot.Q<VisualElement>("root");
        mainContainer = uiRoot.Q<VisualElement>("main");
        logButton = uiRoot.Q<Button>("LogButton");
        settingButton = uiRoot.Q<Button>("SettingButton");
        logContent = uiRoot.Q<VisualElement>("logContent");
        settingContent = uiRoot.Q<VisualElement>("settingContent");
        toggleButton = uiRoot.Q<Button>("Toggle");
        tabsContainer = uiRoot.Q<VisualElement>("tabs");
        contentDisplayArea = uiRoot.Q<VisualElement>("contentDisplayArea");

        // ボタンにイベントリスナーを追加
        logButton.clicked += () => ShowContent(logContent, settingContent);
        settingButton.clicked += () => ShowContent(settingContent, logContent);
        if (toggleButton != null) {
            toggleButton.clicked += ToggleDrawer;
        }

        // 初期表示はLogにする
        ShowContent(logContent, settingContent);

        // 初期は閉じた状態（Tabs を下、Content 非表示）
        SetExpanded(false);
    }

    private void ShowContent(VisualElement contentToShow, VisualElement contentToHide) {
        contentToShow.style.display = DisplayStyle.Flex; // または DisplayStyle.Gridなど適切なもの
        contentToHide.style.display = DisplayStyle.None;
    }

    // トグルボタン押下で開閉を切り替え
    private void ToggleDrawer() {
        SetExpanded(isCollapsed); // 現在が畳み状態なら展開、展開なら畳む
    }

    // 開閉状態の反映
    private void SetExpanded(bool expanded) {
        if (tabsContainer == null || contentDisplayArea == null) return;

        if (expanded) {
            // 表示: Content を表示、Tabs を上に、ボタンは「▼」
            contentDisplayArea.style.display = DisplayStyle.Flex;
            contentDisplayArea.style.flexGrow = 1;
            // UXML で max-height:150px が指定されているため、制限を解除
            contentDisplayArea.style.maxHeight = StyleKeyword.None;
            // Tabs を最上部へ
            MoveElementToIndex(root, tabsContainer, 0);
            if (mainContainer != null) mainContainer.style.display = DisplayStyle.None; // Content を最大化
            if (toggleButton != null) toggleButton.text = "▼";
            isCollapsed = false;
        } else {
            // 非表示: Content を閉じる、Tabs を一番下に、ボタンは「▲」
            contentDisplayArea.style.display = DisplayStyle.None;
            // Tabs を最下部へ（末尾に挿入）
            MoveElementToIndex(root, tabsContainer, int.MaxValue);
            if (mainContainer != null) {
                mainContainer.style.display = DisplayStyle.Flex;
            }
            if (toggleButton != null) toggleButton.text = "▲";
            isCollapsed = true;
        }
    }

    private void MoveElementToIndex(VisualElement parent, VisualElement child, int targetIndex) {
        if (parent == null || child == null) return;
        if (child.parent != parent) return;
        parent.Remove(child);
        int clampedIndex = Mathf.Clamp(targetIndex, 0, parent.childCount);
        parent.Insert(clampedIndex, child);
    }
}