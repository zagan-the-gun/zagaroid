using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using TMPro;


public class CanvasController : MonoBehaviour {
    [SerializeField] private GameObject mainCanvas; // Main Canvasを指定
    // [SerializeField] private GameObject menuCanvas; // Menu Canvasを指定
    [SerializeField] private GameObject logCanvas; // Log Canvasを指定
    [SerializeField] private GameObject canvas; // 対象のCanvasを指定

    [Header("Comment template")]
    [SerializeField] private GameObject commentTemplate; // 流れるコメントのテンプレート

    void Start() {
    }

    void OnEnable() {
        // セントラルマネージャからコメントを受信するイベントを登録
        CentralManager.OnCanvasCommentSend += HandleCanvasCommentSend;
    }

    void OnDisable() {
        CentralManager.OnCanvasCommentSend -= HandleCanvasCommentSend;
    }

    // セントラルマネージャーから情報を受け取るイベント
    void HandleCanvasCommentSend(string comment) {
        Debug.Log("Global Message Received: " + comment);
        // messageをTwitchコメントに送信
        addComment(comment);
    }

    // スクロール前にTMPオブジェクトの生成
    private void addComment(string comment) {
        // テンプレートから新しいコメントオブジェクトを生成
        // CanvasのTransformを取得
        Transform canvasTransform = GameObject.Find("Canvas").transform; // "Canvas"はCanvasオブジェクトの名前
        // Canvasの配下に新しいTMPオブジェクトを生成
        GameObject newComment = Instantiate(commentTemplate, canvasTransform);

        newComment.SetActive(true); // 新しいコメントを表示

        // テキストを設定
        TextMeshPro textComponent = newComment.GetComponent<TextMeshPro>();
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
            rectTransform.localScale = new Vector3(1f, 1.6f, 1f); // Y軸を1.2倍に伸ばす
        } else {
            Debug.LogError("コメントテンプレートが生成されていません！");
        }

        // スクロールを開始
        StartCoroutine(scrollComment(newComment));
    }

    private IEnumerator scrollComment(GameObject comment) {
        RectTransform rectTransform = comment.GetComponent<RectTransform>();

        // キャンバスのサイズを取得
        RectTransform canvasRectTransform = canvas.GetComponent<RectTransform>();
        float canvasHeight = 960;
        float canvasWidth = 1920;

        // テキストオブジェクトの高さを取得
        float textHeight = rectTransform.sizeDelta.y;

        // スクロール開始位置をランダムに設定
        float randomYPosition = UnityEngine.Random.Range(-textHeight, -canvasHeight + textHeight);
        rectTransform.anchoredPosition = new Vector2(canvasWidth, randomYPosition);

        // より滑らかなアニメーションのための設定
        float scrollSpeed = 400f; // 超高速スクロール
        float smoothTime = 0.01f; // 最小限のスムージング時間
        Vector2 velocity = Vector2.zero;

        // スクロール処理
        while (rectTransform.anchoredPosition.x > -rectTransform.sizeDelta.x) {
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