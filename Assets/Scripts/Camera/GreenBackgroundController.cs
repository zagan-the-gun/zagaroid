using UnityEngine;

public class GreenBackgroundController : MonoBehaviour 
{
    private Camera mainCamera;
    [SerializeField] private Canvas canvas; // 対象のCanvasを指定

    void Start()
    {
        // Canvasのサイズを1920x1200に設定
        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(1920, 1200);

        // 実際のウィンドウサイズを1024x768に設定
        Screen.SetResolution(1024, 768, false);

        // Canvasのスケールを調整して縮小表示
        float scaleX = 1024f / 1920f;
        float scaleY = 768f / 1200f;
        canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    void Awake()
    {
        // シーン内のメインカメラを自動的に取得
        mainCamera = Camera.main;
        
        if (mainCamera == null)
        {
            Debug.LogError("メインカメラが見つかりません！");
            return;
        }

        // カメラの初期背景色を緑に設定
        mainCamera.clearFlags = CameraClearFlags.SolidColor;
        mainCamera.backgroundColor = Color.green;
        // カメラの初期背景色を透明に設定
        // mainCamera.clearFlags = CameraClearFlags.SolidColor;
        // mainCamera.backgroundColor = new Color(0, 0, 0, 0); // 透明な色を設定
        // mainCamera.clearFlags = CameraClearFlags.Depth;
    }
}