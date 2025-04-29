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
            textComponent.outlineWidth = 0.6f; // アウトラインの太さ
            textComponent.outlineColor = Color.black; // アウトラインの色
            // textComponent.fontSharedMaterial = textComponent.fontSharedMaterial; // アウトラインを有効にするための設定 (不要かも)
            // newCommentの幅をテキストの長さに合わせて調整
            RectTransform rectTransform = newComment.GetComponent<RectTransform>();
            float textWidth = textComponent.preferredWidth; // テキストの幅を取得
            rectTransform.sizeDelta = new Vector2(textWidth, rectTransform.sizeDelta.y); // 幅を設定
        } else {
            Debug.LogError("コメントテンプレートが生成されていません！");
        }

        // スクロールを開始
        StartCoroutine(scrollComment(newComment));
    }

    private IEnumerator scrollComment(GameObject comment) {
        RectTransform rectTransform = comment.GetComponent<RectTransform>();
        float startPosition = rectTransform.anchoredPosition.x;

        // Canvasを直接取得
        // Canvas canvas = FindObjectOfType<Canvas>();
        // if (canvas == null) {
        //     Debug.LogError("Canvasが見つかりません！");
        //     yield break; // Canvasが見つからない場合は処理を中断
        // }

        // キャンバスのサイズを取得
        RectTransform canvasRectTransform = canvas.GetComponent<RectTransform>();
        float canvasHeight = 960;
        float canvasWidth = 1920;

        // テキストオブジェクトの高さを取得
        float textHeight = rectTransform.sizeDelta.y;
        // Debug.Log($"DEAD BEEF textHeight: {textHeight}");

        // スクロール開始位置をランダムに設定
        float randomYPosition = UnityEngine.Random.Range(-textHeight, -canvasHeight + textHeight);
        rectTransform.anchoredPosition = new Vector2(canvasWidth, randomYPosition); // 初期位置をキャンバスの右端に設定し、Y位置をランダムに設定
        // Debug.Log($"DEAD BEEF randomYPosition: {randomYPosition}");

        // スクロール処理
        while (rectTransform.anchoredPosition.x > -rectTransform.sizeDelta.x) {
            rectTransform.anchoredPosition += Vector2.left * 100f * Time.deltaTime; // スクロール速度を調整
            yield return null;
        }

        // スクロールが終わったらオブジェクトを破棄
        Destroy(comment);
    }

}