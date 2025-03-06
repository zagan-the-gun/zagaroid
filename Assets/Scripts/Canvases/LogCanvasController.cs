using UnityEngine;
using TMPro;



public class LogCanvasController : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI logText; // Log CanvasのText要素を指定
    private string logMessages = ""; // ログメッセージを保持する変数

    void OnEnable()
    {
        // ログメッセージを受信するイベントを登録
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        // イベントの登録を解除
        Application.logMessageReceived -= HandleLog;
    }

    void Start()
    {
        // 初期化
        logText.text = ""; // 最初は空にする
    }

    private void HandleLog(string logString, string stackTrace, LogType type)
    {
        // ログメッセージを追加
        logMessages += logString + "\n"; // メッセージを追加
        logText.text = logMessages; // Text要素に表示
    }
}