using UnityEngine;
using UnityEngine.UIElements; // UI Toolkit の名前空間
using System; // DateTime のために必要
using System.Collections.Generic; // List のために必要

public class LogUIController : MonoBehaviour {
    // UXML から TextField を参照するためのUIDocumentsからの参照
    [SerializeField] private UIDocument uiDocument;

    private TextField logTextField; // UXML 内の TextField を参照する変数
    private ScrollView logScrollView; // TextField が配置されている可能性のあるScrollView

    private List<string> logMessages = new List<string>();
    private const int MaxLogLines = 100; // 表示する最大行数

    void OnEnable() {
        // ログメッセージを受信するイベントを登録
        Application.logMessageReceived += HandleLog;

        // UIDocumentが設定されていることを確認
        if (uiDocument == null || uiDocument.rootVisualElement == null) {
            Debug.LogError("LogUIController: UIDocument またはそのルート要素が設定されていません。");
            return;
        }

        // UXML内のTextField要素を名前で取得
        // UXMLでTextFieldにname="logOutput"のような名前を付けておくことを推奨
        logTextField = uiDocument.rootVisualElement.Q<TextField>("logOutput"); // 仮にlogOutputというnameを持つTextFieldを想定
        if (logTextField == null) {
            Debug.LogError("LogUIController: UXML内で 'logOutput' という名前のTextFieldが見つかりません。");
            return;
        }

        // TextFieldをリードオンリーにする（ユーザーが直接入力できないように）
        logTextField.isReadOnly = true;

        // TextFieldのスクロールビューを取得（もしあれば）
        // TextFieldはデフォルトでスクロール可能なので、通常は明示的にScrollViewは不要ですが
        // もしTextFieldをScrollViewに入れているなら参照
        // logScrollView = logTextField.Q<ScrollView>(); //不要かも

        // TextFieldの親要素であるScrollViewを取得する
        // UXMLでは <ui:ScrollView><ui:TextField .../></ui:ScrollView> の構造なので
        // TextFieldの親（parent）がScrollViewであるはずです。
        logScrollView = logTextField.parent as ScrollView; 
        if (logScrollView == null)
        {
            Debug.LogWarning("LogUIController: 'logOutput' TextFieldの親としてScrollViewが見つかりませんでした。スクロール機能が正しく動作しない可能性があります。");
        }

        // 初期表示をクリア
        logTextField.value = "";
    }

    void OnDisable() {
        // イベントの登録を解除
        Application.logMessageReceived -= HandleLog;
    }

    private void HandleLog(string logString, string stackTrace, LogType type) {
        if (logTextField == null) return; // TextFieldが初期化されていない場合は何もしない

        // 現在の時刻を取得し、ログメッセージに追加
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedLog = $"[{timestamp}] {logString}";

        logMessages.Add(formattedLog);

        // 最大行数を超えた場合、古い行を削除
        if (logMessages.Count > MaxLogLines) {
            logMessages.RemoveAt(0);
        }

        // 全ログメッセージをTextFieldのvalueに設定
        logTextField.value = string.Join("\n", logMessages);

        // ログメッセージが追加され、TextFieldのvalueが更新された後、
        // 次のフレームでスクロールを一番下まで移動させる
        if (logScrollView != null)
        {
            // スクロールコンテンツの一番下までスクロールします。
            // VisualElement.schedule.Execute() を使うと、UIのレイアウト更新後に実行されるため確実です。
            logScrollView.schedule.Execute(() => {
                // ScrollTo(VisualElement element) は、指定した要素が見える位置までスクロールします。
                // 今回はTextField全体を対象とするため、TextFieldを渡します。
                // その後、スクロールビューのコンテンツの高さを取得し、最大スクロール量に設定することで、
                // 強制的に一番下までスクロールさせます。
                logScrollView.ScrollTo(logTextField); 
                
                // より確実に一番下までスクロールさせるために、contentContainerの高さとviewableAreaの差を直接設定します。
                // Unity 2021.2 以降の新しい ScrollView の API を利用
                logScrollView.scrollOffset = new Vector2(0, logScrollView.contentContainer.resolvedStyle.height - logScrollView.resolvedStyle.height);

            }).Every(1); // 毎フレームではなく、ログが来た次のフレームで一度だけ実行させる
        }

    }
}