using UnityEngine;

public class GreenBackgroundController : MonoBehaviour 
{
    private Camera mainCamera;
    private int lastScreenWidth;
    private int lastScreenHeight;
    // [SerializeField] private Canvas canvas; // 対象のCanvasを指定

    // void Start()
    // {
    //     // Canvasのサイズを1920x1200に設定
    //     RectTransform canvasRect = canvas.GetComponent<RectTransform>();
    //     canvasRect.sizeDelta = new Vector2(1920, 1200);

    //     // 実際のウィンドウサイズを1024x768に設定
    //     Screen.SetResolution(1024, 768, false);

    //     // Canvasのスケールを調整して縮小表示
    //     float scaleX = 1024f / 1920f;
    //     float scaleY = 768f / 1200f;
    //     canvas.transform.localScale = new Vector3(scaleX, scaleY, 1f);
    // }

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
        
        // 初期画面サイズを記録
        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        
        // 初期ビューポートを設定
        UpdateViewport();
    }

    void Update()
    {
        if (mainCamera == null) return;
        
        // 画面サイズが変更された場合のみビューポートを更新
        if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
        {
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            UpdateViewport();
        }
    }
    
    private void UpdateViewport()
    {
        if (mainCamera == null) return;
        
        // NDI RenderTextureのアスペクト比（1920x960 = 2:1）に合わせてビューポートを調整
        // PC画面とNDI画面で左右の位置を一致させるため
        const float ndiAspectRatio = 1920f / 960f; // 2:1
        float currentAspectRatio = (float)Screen.width / Screen.height;
        
        // PC画面のアスペクト比をNDI RenderTextureのアスペクト比に合わせてビューポートを調整
        Rect viewportRect = new Rect();
        
        if (currentAspectRatio > ndiAspectRatio)
        {
            // PC画面の方が横長 → 左右をカット（上下いっぱいに表示）
            float scaleHeight = currentAspectRatio / ndiAspectRatio;
            float normalizedWidth = 1.0f / scaleHeight;
            float normalizedX = (1.0f - normalizedWidth) * 0.5f;
            
            viewportRect.x = normalizedX;
            viewportRect.y = 0;
            viewportRect.width = normalizedWidth;
            viewportRect.height = 1.0f;
        }
        else
        {
            // PC画面の方が縦長または同じ → 上下をカット（左右いっぱいに表示）
            float scaleWidth = ndiAspectRatio / currentAspectRatio;
            float normalizedHeight = 1.0f / scaleWidth;
            float normalizedY = (1.0f - normalizedHeight) * 0.5f;
            
            viewportRect.x = 0;
            viewportRect.y = normalizedY;
            viewportRect.width = 1.0f;
            viewportRect.height = normalizedHeight;
        }
        
        // ビューポートを設定（PC画面は左右いっぱいに表示、上下はカット）
        mainCamera.rect = viewportRect;
    }
}