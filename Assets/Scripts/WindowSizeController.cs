using UnityEngine;
// using System.Runtime.InteropServices;

public class WindowSizeController : MonoBehaviour
{
    private const string WidthKey = "WindowWidth";
    private const string HeightKey = "WindowHeight";

    private const int MinWidth = 800;  // 最小幅
    private const int MinHeight = 600; // 最小高さ

    [SerializeField] private GameObject mainCanvas; // Main Canvasを指定
    // [SerializeField] private GameObject menuCanvas; // Menu Canvasを指定
    [SerializeField] private GameObject logCanvas; // Log Canvasを指定
    [SerializeField] private GameObject canvas; // 対象のCanvasを指定
    // public GameObject CanvasReference => canvas; // キャンバスを公開するプロパティ

    private int previousWidth;  // 前回のウィンドウ幅
    private int previousHeight; // 前回のウィンドウ高さ

    void Start()
    {
        // 前回のウィンドウサイズを読み込む
        int width = PlayerPrefs.GetInt(WidthKey, 1600); // デフォルト値は1600
        int height = PlayerPrefs.GetInt(HeightKey, 800); // デフォルト値は800

        // // ウィンドウサイズの下限を適用
        width = Mathf.Max(width, MinWidth);
        height = Mathf.Max(height, MinHeight);

        // // ウィンドウサイズを設定
        Screen.SetResolution(width, height, false);


        if (logCanvas != null && mainCanvas != null)
        {

            // Canvasのサイズを1920x1200に設定
            // RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            // canvasRect.sizeDelta = new Vector2(1920, 1200);

            // 実際のウィンドウサイズを1024x768に設定
            // Screen.SetResolution(1024, 768, false);
            // Screen.SetResolution(600, 350, false);

            // // Canvasのスケールを調整して縮小表示
            // float scaleX = 1024f / 1920f;
            // float scaleY = 768f / 1200f;
            // canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);
            // float scaleX = 600f / 1920f;
            // float scaleY = 350f / 1200f;
            // canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);

            // メインキャンバスのサイズ
            RectTransform mainRectTransform = mainCanvas.GetComponent<RectTransform>();
            // デバッグログを削減: メインキャンバスサイズ

            // メニューキャンバスのサイズ
            // RectTransform menuRectTransform = menuCanvas.GetComponent<RectTransform>();
            // Debug.Log($"DEAD BEEF Menu.x: {menuRectTransform.sizeDelta.x}, Menu.y: {menuRectTransform.sizeDelta.y}");

            // ログキャンバスのサイズ
            RectTransform logRectTransform = logCanvas.GetComponent<RectTransform>();
            // デバッグログを削減: ログキャンバスサイズ

            // 表示キャンバスのサイズ
            RectTransform canvasRectTransform = canvas.GetComponent<RectTransform>();
            // デバッグログを削減: キャンバスサイズ（変更前）
            float canvas_y = mainRectTransform.sizeDelta.y - logRectTransform.sizeDelta.y;
            // float canvas_x = mainRectTransform.sizeDelta.x - menuRectTransform.sizeDelta.x;
            float canvas_x = mainRectTransform.sizeDelta.x;

            // // Canvasのサイズを1920x960に設定
            canvasRectTransform.sizeDelta = new Vector2(1920, 960);
            // // 実際のウィンドウサイズを600x300に設定
            // Screen.SetResolution((int)canvas_x, (int)canvas_y, false);
            // // Canvasのスケールを調整して縮小表示
            float scaleX = canvas_x / 1920f;
            float scaleY = canvas_y / 960f;
            canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);
            // デバッグログを削減: キャンバスサイズ（変更後）
            // Debug.Log($"DEAD BEEF new Canvas.x: {canvasRectTransform.sizeDelta.x}, new Canvas.y: {canvasRectTransform.sizeDelta.y}");
        }
        else
        {
            Debug.LogError("Menu CanvasまたはMain Canvasが設定されていません！");
        }

    }

    void Update()
    {
        // 現在のウィンドウサイズを取得
        int currentWidth = Screen.width;
        int currentHeight = Screen.height;

        if (currentWidth < MinWidth) {
            // Vector2 windowPosition = GetCurrentWindowPosition();
            Screen.SetResolution(MinWidth, currentHeight, false);
            // SetWindowPosition((int)windowPosition.x, (int)windowPosition.y);
        }

        if (currentHeight < MinHeight) {
            // Vector2 windowPosition = GetCurrentWindowPosition();
            Screen.SetResolution(currentWidth, MinHeight, false);
            // SetWindowPosition((int)windowPosition.x, (int)windowPosition.y);
        }
    }

    void OnApplicationQuit()
    {
        // 現在のウィンドウサイズを保存
        PlayerPrefs.SetInt(WidthKey, Screen.width);
        PlayerPrefs.SetInt(HeightKey, Screen.height);
        PlayerPrefs.Save(); // 変更を保存
    }
}