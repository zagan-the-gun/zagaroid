using UnityEngine;
using Twitcher;
using UnityEngine.Video;
using TMPro;
using System.Collections;
using System.Collections.Generic;


[System.Serializable]
public class VideoTriggerSetting{
    public VideoClip videoClip;
    [Tooltip("Trigger words (word1, word2, word3, ...)")]
    public string[] triggerWords;
    [Tooltip("Position of the video (x, y)")]
    public Vector2 position;
    [Tooltip("Size of the video (width, height)")]
    public Vector2 size = new Vector2(400, 300);
}

public class TwitchChatController : MonoBehaviour {
    [Header("Twitch Settings")]
    [SerializeField, Tooltip("Name of channel to join")]
    private string channelToJoin;
    [SerializeField, Tooltip("OAuth token to login with (oauth:xxxxxxxxxxxxxx)")]
    private string authToken;
    private Camera mainCamera;
    private TwitchController twitch;

    [Header("Twitch Chat Settings")]
    [SerializeField] private GameObject commentTemplate;

    [Header("Video Settings")]
    [SerializeField] private VideoPlayerController videoPlayerController;
    [SerializeField] private VideoTriggerSetting[] videoSettings;

    [Header("Entrance Sound Settings")]
    [SerializeField] private AudioClip entranceSound; // 音声ファイルを指定
    private AudioSource audioSource; // AudioSourceコンポーネントを格納する変数
    private HashSet<string> usersWhoCommented = new HashSet<string>(); // コメントしたユーザーを追跡
    private VoiceVoxApiClient client;

    void Start() {
        // メインカメラの取得
        mainCamera = Camera.main;
        if (mainCamera == null) {
            Debug.LogError("メインカメラが見つかりません！");
            return;
        }

        if (string.IsNullOrEmpty(channelToJoin)) {
            Debug.LogError("チャンネル名が設定されていません！");
            return;
        }

        if (string.IsNullOrEmpty(authToken)) {
            Debug.LogError("OAuthトークンが設定されていません！");
            return;
        }

        // OAuthトークンの形式を確認
        if (!authToken.StartsWith("oauth:")) {
            Debug.LogError("OAuthトークンは'oauth:'で始まる必要があります");
            return;
        }

        if (videoPlayerController == null) {
            Debug.LogError("VideoPlayerControllerがアタッチされていません");
            return;
        }

        // Enable full verbose logging
        TwitcherUtil.logging = TwitcherUtil.LoggingMode.Verbose;

        audioSource = gameObject.AddComponent<AudioSource>(); // AudioSourceを追加

        client = new VoiceVoxApiClient();

        try {
            Debug.Log($"接続を試みています... チャンネル: {channelToJoin}");
            
            // Create a twitcher instance and connect
            twitch = TwitchController.Create(authToken, channelToJoin);
            
            // Subscribe to the message event
            if (twitch != null && twitch.Client != null) {
                twitch.Client.onMessageReceived += OnMessageReceived;
                Debug.Log($"Twitchに接続しました。チャンネル: {channelToJoin}");
            } else {
                Debug.LogError("Twitchクライアントの作成に失敗しました。");
            }
        } catch (System.Exception e) {
            Debug.LogError($"Twitch接続エラー: {e.Message}");
        }
    }

    private void OnMessageReceived(Message message) {
        if (message == null) {
            Debug.LogWarning("受信したメッセージがnullです");
            return;
        }

        Debug.Log($"Message received: {message.Command} - {(message.Parameters != null && message.Parameters.Length > 1 ? message.Parameters[1] : "no content")}");

        // チャットメッセージの処理
        if (message.Command == TwitchClient.Commands.PRIVMSG && message.Parameters != null && message.Parameters.Length > 1) {
            string chatMessage = message.Parameters[1].ToLower();
            // チャットスクロールは生メッセージ
            // StartCoroutine(AddComment(message.ChatMessage));

            Debug.Log($"DEAD BEEF message.Parameters[0]: {message.Parameters[0]}");
            string user = message.Parameters[0]; // ユーザー名を取得
            // string chatMessage = message.Parameters[1];

            // 初めてのコメントかどうかをチェック
            if (!usersWhoCommented.Contains(user)) {
                Debug.Log($"DEAD BEEF user: {user}");
                usersWhoCommented.Add(user); // ユーザーを追加
                Debug.Log($"DEAD BEEF user add: {user}");
                audioSource.PlayOneShot(entranceSound); // 音を鳴らす
                Debug.Log($"DEAD BEEF play sound: {entranceSound}");
            }

            // コメント読み上げを開始
            StartCoroutine(SpeakComment(message.ChatMessage));

            // コメントをニコニコ風に表示
            AddComment(message.ChatMessage);

            Debug.Log($"Chat message: {message.ChatMessage}");

            // 背景色の変更処理
            if (mainCamera != null) {
                if (chatMessage.Contains("green")) {
                    Debug.Log("Changing color to green");
                    mainCamera.backgroundColor = Color.green;
                } else if (chatMessage.Contains("blue")) {
                    Debug.Log("Changing color to blue");
                    mainCamera.backgroundColor = Color.blue;
                } else if (chatMessage.Contains("red")) {
                    Debug.Log("Changing color to red");
                    mainCamera.backgroundColor = Color.red;
                } else if (chatMessage.Contains("black")) {
                    Debug.Log("Changing color to black");
                    mainCamera.backgroundColor = Color.black;
                }
            }

            // 動画再生の処理
            for (int i = 0; i < videoSettings.Length; i++) {
                var setting = videoSettings[i];
                // string[] triggers = setting.triggerWords.Split(',');
                foreach (string trigger in setting.triggerWords) {
                    string[] words = trigger.ToLower().Split(',');
                    foreach (string word in words) {
                        if (chatMessage.Contains(word.Trim())) {
                            PlayVideo(i);
                            break;
                        }
                    }
                }
            }
        }
    }

    private IEnumerator SpeakComment(string comment) {

        // コルーチンの結果を取得するための変数を用意
        int styleId = 0; // デフォルト値を設定

        // コルーチンを実行し、結果を取得
        yield return StartCoroutine(client.GetSpeakerRnd((result) => styleId = result));
        Debug.Log($"styleId: {styleId})");

        // テキストからAudioClipを生成（話者は「8:春日部つむぎ」）
        yield return client.TextToAudioClip(styleId, comment);

        if (client.AudioClip != null)
        {
            // AudioClipを取得し、AudioSourceにアタッチ
            audioSource.clip = client.AudioClip;
            // AudioSourceで再生
            audioSource.Play();
        }

    }

    // private IEnumerator AddComment(string comment) {
    // スクロール前に表示テキストの処理
    private void AddComment(string comment) {
        // テンプレートから新しいコメントオブジェクトを生成
        // GameObject newComment = Instantiate(commentTemplate, transform);
        // GameObject newComment = Instantiate(commentTemplate.gameObject, transform);
        // CanvasのTransformを取得
        Transform canvasTransform = GameObject.Find("Canvas").transform; // "Canvas"はCanvasオブジェクトの名前
        // Canvasの下に新しいコメントを生成
        GameObject newComment = Instantiate(commentTemplate, canvasTransform);

        newComment.SetActive(true); // 新しいコメントを表示

        // テキストを設定
        TextMeshPro textComponent = newComment.GetComponent<TextMeshPro>();
        if (textComponent != null) {
            textComponent.text = comment;
            // 折り返しを無効にする
            textComponent.enableWordWrapping = false;
            // 文字に黒いアウトラインを付ける
            textComponent.outlineWidth = 0.2f; // アウトラインの太さ
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
        StartCoroutine(ScrollComment(newComment));
    }

    private IEnumerator ScrollComment(GameObject comment) {
        RectTransform rectTransform = comment.GetComponent<RectTransform>();
        float startPosition = rectTransform.anchoredPosition.x;

        // Canvasを直接取得
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null) {
            Debug.LogError("Canvasが見つかりません！");
            yield break; // Canvasが見つからない場合は処理を中断
        }

        // キャンバスのサイズを取得
        RectTransform canvasRectTransform = canvas.GetComponent<RectTransform>();
        float canvasHeight = canvasRectTransform.sizeDelta.y;
        float canvasWidth = canvasRectTransform.sizeDelta.x;

        // テキストオブジェクトの高さを取得
        float textHeight = rectTransform.sizeDelta.y;
        Debug.Log($"DEAD BEEF textHeight: {textHeight}");

        // スクロール開始位置をランダムに設定
        // float randomYPosition = Random.Range(-canvas.GetComponent<RectTransform>().sizeDelta.y / 2, canvas.GetComponent<RectTransform>().sizeDelta.y / 2);
        // float randomYPosition = Random.Range(-canvasHeight / 2 + rectTransform.sizeDelta.y / 2, canvasHeight / 2 - rectTransform.sizeDelta.y / 2);
        float randomYPosition = Random.Range(-textHeight, -canvasHeight + textHeight);
        rectTransform.anchoredPosition = new Vector2(canvasWidth, randomYPosition); // 初期位置をキャンバスの右端に設定し、Y位置をランダムに設定
        Debug.Log($"DEAD BEEF randomYPosition: {randomYPosition}");

        // スクロール処理
        while (rectTransform.anchoredPosition.x > -rectTransform.sizeDelta.x) {
            rectTransform.anchoredPosition += Vector2.left * 100f * Time.deltaTime; // スクロール速度を調整
            yield return null;
        }

        // スクロールが終わったらオブジェクトを破棄
        Destroy(comment);
    }

    private void PlayVideo(int index) {
        if (index >= 0 && index < videoSettings.Length) {
            var setting = videoSettings[index];
            videoPlayerController.PlayVideo(setting.videoClip, setting.position, setting.size);
            Debug.Log($"動画を再生: {setting.videoClip.name}");
        }
    }

    void OnDestroy() {
        // Clean up
        if (twitch != null && twitch.Client != null) {
            twitch.Client.onMessageReceived -= OnMessageReceived;
            Debug.Log("Twitchクライアントをクリーンアップしました");
        }
    }
}