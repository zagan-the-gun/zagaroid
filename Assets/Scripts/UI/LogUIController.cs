using UnityEngine;
using UnityEngine.UIElements;
using System;
using System.Collections.Generic;

public class LogUIController : MonoBehaviour {
    [SerializeField] private UIDocument uiDocument;

    private TextField logTextField;
    private ScrollView logScrollView;

    private List<string> logMessages = new List<string>();
    private const int MaxLogLines = 100;

    // スクロールが一番下に固定されているかどうか
    private bool isScrolledToBottom = true; 

    // スクロールバーが動いたかどうかを追跡するための過去のスクロールオフセット
    // private Vector2 lastScrollOffset = Vector2.zero; // これは不要になるので削除

    void OnEnable() {
        // Unityのシステムログイベントを購読
        Application.logMessageReceived += HandleLog;

        if (uiDocument == null || uiDocument.rootVisualElement == null) {
            Debug.LogError("LogUIController: UIDocument またはそのルート要素が設定されていません。");
            return;
        }

        // UXML内のTextField要素を名前で取得
        logTextField = uiDocument.rootVisualElement.Q<TextField>("logOutput");
        if (logTextField == null) {
            Debug.LogError("LogUIController: UXML内で 'logOutput' という名前のTextFieldが見つかりません。");
            return;
        }

        logTextField.isReadOnly = true;

        // TextFieldの親要素であるScrollViewを取得
        logScrollView = logTextField.parent as ScrollView; 
        if (logScrollView == null) {
            Debug.LogWarning("LogUIController: 'logOutput' TextFieldの親としてScrollViewが見つかりませんでした。スクロール機能が正しく動作しない可能性があります。");
        } else {
            // スクロール速度を高速化（デフォルトの3倍）
            logScrollView.mouseWheelScrollSize = 60.0f; // デフォルトは約18
            
            // スクロールオフセットの変更イベントを購読
            logScrollView.RegisterCallback<ChangeEvent<Vector2>>(OnScrollViewScrollChanged); 
            
            // 初回のスクロール位置を一番下にするためのスケジューリング
            logScrollView.schedule.Execute(ScrollToBottomImmediately).StartingIn(100); 
        }

        logTextField.value = "";
    }

    void OnDisable() {
        Application.logMessageReceived -= HandleLog;
        if (logScrollView != null) {
            // イベントの購読解除
            logScrollView.UnregisterCallback<ChangeEvent<Vector2>>(OnScrollViewScrollChanged);
        }
    }

    private void HandleLog(string logString, string stackTrace, LogType type) {
        if (logTextField == null) return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedLog = $"[{timestamp}] {logString}";

        logMessages.Add(formattedLog);

        if (logMessages.Count > MaxLogLines) {
            logMessages.RemoveAt(0);
        }

        logTextField.value = string.Join("\n", logMessages);

        // isScrolledToBottom が true の場合のみ自動スクロールをスケジュール
        if (logScrollView != null && isScrolledToBottom) {
            // ログ追加後、レイアウト更新を待ってから一番下へスクロール
            logScrollView.schedule.Execute(ScrollToBottomImmediately).StartingIn(1); 
        }
    }

    // ScrollViewのscrollOffsetが変更されたときに呼び出されるメソッド
    private void OnScrollViewScrollChanged(ChangeEvent<Vector2> evt)
    {
        // 性能改善：スクロール変更の呼び出し頻度を制限
        logScrollView.schedule.Execute(UpdateScrolledToBottomStatus).StartingIn(50); // 50ms遅延で実行
    }

    // isScrolledToBottom の状態を更新するヘルパーメソッド
    private void UpdateScrolledToBottomStatus()
    {
        if (logScrollView == null) return;

        // 現在のスクロールオフセットY
        float currentScrollY = logScrollView.scrollOffset.y;

        // コンテンツの実際の高さ
        float contentHeight = logScrollView.contentContainer.resolvedStyle.height;
        // ScrollView自体の表示領域の高さ
        float viewportHeight = logScrollView.resolvedStyle.height;
        
        // スクロール可能な最大のY座標 (一番下の位置)
        float maxScrollY = contentHeight - viewportHeight;

        // コンテンツがビューポートより短い場合、スクロールバーは表示されないので常に一番下とみなす
        if (maxScrollY <= 0) { 
            isScrolledToBottom = true;
            return;
        }

        // 浮動小数点数の比較のため、許容誤差を設定
        const float epsilon = 5.0f; // 許容誤差を少し広げる（性能改善）

        // 現在のスクロール位置が一番下の許容範囲内にあるか判定
        isScrolledToBottom = (Mathf.Abs(currentScrollY - maxScrollY) < epsilon);

        // Debug.Log($"Scroll Y: {currentScrollY}, Max Scroll Y: {maxScrollY}, IsBottom: {isScrolledToBottom}"); // デバッグ用
    }

    // 強制的に一番下までスクロールするメソッド
    private void ScrollToBottomImmediately()
    {
        if (logScrollView != null)
        {
            float contentHeight = logScrollView.contentContainer.resolvedStyle.height;
            float viewportHeight = logScrollView.resolvedStyle.height;
            float maxScrollY = contentHeight - viewportHeight;
            
            // コンテンツがビューポートより短い場合はスクロール不要
            if (maxScrollY <= 0) { 
                maxScrollY = 0; // 負の値にならないように
            }
            logScrollView.scrollOffset = new Vector2(0, maxScrollY);
            isScrolledToBottom = true; // 強制的に一番下へスクロールしたので、フラグをtrueに
        }
    }
}