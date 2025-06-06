using UnityEngine;
using UnityEngine.UIElements;

public class TabController : MonoBehaviour{
    // public VisualTreeAsset uiDocument; // UXMLをInspectorからアタッチ
    [SerializeField] private UIDocument uiDocument; 

    private VisualElement root;
    private Button logButton;
    private Button settingButton;
    private VisualElement logContent;
    private VisualElement settingContent;

    private void OnEnable() {
        // SerializedFieldでアタッチされたUIDocumentのルート要素を使用する
        VisualElement uiRoot = uiDocument.rootVisualElement;

        // 各要素を名前で取得
        logButton = uiRoot.Q<Button>("LogButton");
        settingButton = uiRoot.Q<Button>("SettingButton");
        logContent = uiRoot.Q<VisualElement>("logContent");
        settingContent = uiRoot.Q<VisualElement>("settingContent");

        // ボタンにイベントリスナーを追加
        logButton.clicked += () => ShowContent(logContent, settingContent);
        settingButton.clicked += () => ShowContent(settingContent, logContent);

        // 初期表示はLogにする
        ShowContent(logContent, settingContent);
    }

    private void ShowContent(VisualElement contentToShow, VisualElement contentToHide) {
        contentToShow.style.display = DisplayStyle.Flex; // または DisplayStyle.Gridなど適切なもの
        contentToHide.style.display = DisplayStyle.None;
    }
}