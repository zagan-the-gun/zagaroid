using UnityEngine;

public class Canvas : MonoBehaviour
{
    [SerializeField] private GameObject mainCanvas; // Main Canvasを指定
    [SerializeField] private GameObject menuCanvas; // Menu Canvasを指定
    [SerializeField] private GameObject logCanvas; // Log Canvasを指定
    [SerializeField] private GameObject canvas; // 対象のCanvasを指定

    void Start()
    {
        if (logCanvas != null && mainCanvas != null && menuCanvas != null)
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

            RectTransform mainRectTransform = mainCanvas.GetComponent<RectTransform>();
            Debug.Log($"DEAD BEEF Main.x: {mainRectTransform.sizeDelta.x}, Main.y: {mainRectTransform.sizeDelta.y}");

            RectTransform menuRectTransform = menuCanvas.GetComponent<RectTransform>();
            Debug.Log($"DEAD BEEF Menu.x: {menuRectTransform.sizeDelta.x}, Menu.y: {menuRectTransform.sizeDelta.y}");

            RectTransform logRectTransform = logCanvas.GetComponent<RectTransform>();
            Debug.Log($"DEAD BEEF Log.x: {logRectTransform.sizeDelta.x}, Log.y: {logRectTransform.sizeDelta.y}");

            RectTransform canvasRectTransform = canvas.GetComponent<RectTransform>();
            float canvas_y = mainRectTransform.sizeDelta.y - logRectTransform.sizeDelta.y;
            float canvas_x = mainRectTransform.sizeDelta.x - menuRectTransform.sizeDelta.x;
            Debug.Log($"DEAD BEEF Canvas.x: {canvasRectTransform.sizeDelta.x}, Canvas.y: {canvasRectTransform.sizeDelta.y}");
            // // Canvasのサイズを1920x1200に設定
            // canvasRectTransform.sizeDelta = new Vector2(1920, 1200);
            // // 実際のウィンドウサイズを1024x768に設定
            // Screen.SetResolution((int)canvas_x, (int)canvas_y, false);
            // // Canvasのスケールを調整して縮小表示
            // float scaleX = (mainRectTransform.sizeDelta.x - menuRectTransform.sizeDelta.x) / 1920f;
            // float scaleY = (mainRectTransform.sizeDelta.y - logRectTransform.sizeDelta.y) / 1200f;
            // canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);
            // Debug.Log($"DEAD BEEF new Canvas.x: {canvasRectTransform.sizeDelta.x}, new Canvas.y: {canvasRectTransform.sizeDelta.y}");
        }
        else
        {
            Debug.LogError("Menu CanvasまたはMain Canvasが設定されていません！");
        }
    }
}