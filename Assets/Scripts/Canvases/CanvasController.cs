using UnityEngine;
using System.Collections;
using TMPro;

public class CanvasController : MonoBehaviour {

    [Header("Comment template")]
    [SerializeField] private GameObject commentTemplate; // 流れるコメントのテンプレート
    
    [Header("Scroll settings")]
    [SerializeField] private float scrollSpeed = 120f; // デフォルトを抑えめに
    [SerializeField] private float smoothTime = 0.05f; // 少しマイルドに

    [Header("Canvas / Camera binding")]
    [SerializeField] private Canvas targetCanvas; // コメントを表示する対象Canvas（未指定なら親から自動取得）
    [SerializeField] private Camera uiRenderCamera; // UIを描画するカメラ（NDI送出カメラを割り当て推奨。未指定ならMainCamera）
    [SerializeField] private RectTransform commentsParent; // コメント生成先（未指定ならtargetCanvas直下 or 自身）

    void OnEnable() {
        // セントラルマネージャからコメントを受信するイベントを登録
        CentralManager.OnCanvasCommentSend += HandleCanvasCommentSend;
    }

    void OnDisable() {
        CentralManager.OnCanvasCommentSend -= HandleCanvasCommentSend;
    }

    void Start() {
        // Canvasの自動検出
        if (targetCanvas == null) {
            targetCanvas = GetComponentInParent<Canvas>();
        }

        if (targetCanvas != null) {
            // 画面に表示しつつ、カメラ出力（NDI）にも載るように、ScreenSpace-Cameraへ変更
            if (targetCanvas.renderMode != RenderMode.ScreenSpaceCamera) {
                targetCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            }

            // 平面距離は近めに（カメラのNear/Farに収まる値）
            if (targetCanvas.planeDistance < 0.1f) {
                targetCanvas.planeDistance = 1.0f;
            }

            // 必要に応じてソートオーダーも調整（最前面に出したい場合）
            // targetCanvas.sortingOrder = 1000;
        } else {
            Debug.LogWarning("CanvasController: 対象Canvasが見つかりません。親階層にCanvasを配置してください。");
        }
    }


    // セントラルマネージャーから情報を受け取るイベント
    void HandleCanvasCommentSend(string comment) {
        addComment(comment);
    }

    // スクロール前にTMPオブジェクトの生成
    private void addComment(string comment) {
        // デバッグ：設定確認
        Debug.Log($"[CanvasController] targetCanvas: {targetCanvas?.name}, commentTemplate: {commentTemplate?.name}, commentsParent: {commentsParent?.name}");
        
        if (targetCanvas == null) {
            Debug.LogError("[CanvasController] targetCanvas が null です！");
            return;
        }
        
        if (commentTemplate == null) {
            Debug.LogError("[CanvasController] commentTemplate が null です！");
            return;
        }
        
        // Main Canvas に直接生成
        Transform parentTransform = targetCanvas.transform;
        GameObject newComment = Instantiate(commentTemplate, parentTransform);

        newComment.SetActive(true); // 新しいコメントを表示

        // テキストを設定（UGUI/3D両対応）
        TMP_Text textComponent = newComment.GetComponent<TMP_Text>();
        if (textComponent != null) {
            textComponent.text = comment;
            // 折り返しを無効にする
            textComponent.enableWordWrapping = false;
            textComponent.color = new Color(1f, 1f, 1f, 1f); // 白色で不透明
            // 文字に黒いアウトラインを付ける
            textComponent.outlineWidth = 0.1f; // アウトラインの太さ
            textComponent.outlineColor = Color.black; // アウトラインの色
            // textComponent.fontSharedMaterial = textComponent.fontSharedMaterial; // アウトラインを有効にするための設定 (不要かも)
            // newCommentの幅をテキストの長さに合わせて調整
            RectTransform rectTransform = newComment.GetComponent<RectTransform>();
            float textWidth = textComponent.preferredWidth; // テキストの幅を取得
            rectTransform.sizeDelta = new Vector2(textWidth, rectTransform.sizeDelta.y); // 幅を設定
            
            // フォントを上下に伸ばす
            rectTransform.localScale = new Vector3(1f, 1f, 1f); // Y軸を1.2倍に伸ばす
        } else {
            Debug.LogError("コメントテンプレートが生成されていません！");
        }

        // スクロールを開始
        StartCoroutine(scrollComment(newComment));
    }

    private IEnumerator scrollComment(GameObject comment) {
        RectTransform rectTransform = comment.GetComponent<RectTransform>();

        // 表示親(RectTransform)の実サイズを使用
        RectTransform parentRect = rectTransform.parent as RectTransform;
        float canvasHeight = parentRect.rect.height;
        float canvasWidth = parentRect.rect.width;

        // テキストオブジェクトの高さを取得（フォントサイズ反映のため実サイズを優先）
        float textHeight = rectTransform.rect.height > 0 ? rectTransform.rect.height : rectTransform.sizeDelta.y;

		// スクロール開始位置をランダムに設定（アンカー/ピボット対応）
		float yMin = -rectTransform.anchorMin.y * canvasHeight + rectTransform.pivot.y * textHeight;
		float yMax = (1f - rectTransform.anchorMin.y) * canvasHeight - (1f - rectTransform.pivot.y) * textHeight;
		float randomYPosition = UnityEngine.Random.Range(yMin, yMax);

        // 右端の画面外から開始（アンカー/ピボット対応の一般式）
		float textWidth = rectTransform.rect.width > 0 ? rectTransform.rect.width : rectTransform.sizeDelta.x;
		float startX = (1f - rectTransform.anchorMin.x) * canvasWidth + rectTransform.pivot.x * textWidth; // 左端がちょうど右端に接する位置
        rectTransform.anchoredPosition = new Vector2(startX, randomYPosition);

        // 監視用ログ（初期配置・サイズ・アンカー/ピボット・Y可視範囲）
        Debug.Log($"[TICKER] initPos=({rectTransform.anchoredPosition.x:F1},{rectTransform.anchoredPosition.y:F1}) textRect=({rectTransform.rect.width:F1}x{rectTransform.rect.height:F1}) canvasSize=({canvasWidth:F1}x{canvasHeight:F1}) anchors(x={rectTransform.anchorMin.x:F2},y={rectTransform.anchorMin.y:F2}) pivot(x={rectTransform.pivot.x:F2},y={rectTransform.pivot.y:F2}) yRange=({yMin:F1}..{yMax:F1}) parent={parentRect.name}");

        // より滑らかなアニメーションのための設定
        Vector2 velocity = Vector2.zero;

        // スクロール処理
        // 左端の画面外まで流れたら終了（アンカー/ピボット対応）
		float endX = -((1f - rectTransform.pivot.x) * textWidth) - (rectTransform.anchorMin.x * canvasWidth);
        while (rectTransform.anchoredPosition.x > endX) {
            // より滑らかな移動のためのLerp使用
            Vector2 targetPosition = rectTransform.anchoredPosition + Vector2.left * scrollSpeed * Time.deltaTime;
            rectTransform.anchoredPosition = Vector2.SmoothDamp(
                rectTransform.anchoredPosition, 
                targetPosition, 
                ref velocity, 
                smoothTime
            );

            yield return null;
        }

        // スクロールが終わったらオブジェクトを破棄
        Destroy(comment);
    }

}