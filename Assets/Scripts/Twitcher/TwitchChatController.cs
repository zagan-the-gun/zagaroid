using UnityEngine;
using Twitcher;
using UnityEngine.Video;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;


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
    // private HashSet<string> usersProfile = new HashSet<string>(); // コメントしたユーザーを追跡
    private Dictionary<string, int> usersProfile = new Dictionary<string, int>(); // ユーザー名とスタイルIDを保持する辞書
    private VoiceVoxApiClient client;
    private DeepLApiClient deepLApiClient;

    private Canvas canvas; // Canvasの参照を保持

    void Start() {

        // Canvasを直接取得
        // Todo: この死最初に見つかったキャンバスを取得しているのでこのやり方はマズい
        canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("Canvasが見つかりません！");
            return; // Canvasが見つからない場合は処理を中断
        }

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

        // DeepLApiClient のインスタンスを作成
        deepLApiClient = new DeepLApiClient();

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

    private void Update() {
        if (twitch == null && twitch.Client == null) {
            try {
                twitch = TwitchController.Create(authToken, channelToJoin);
                twitch.Client.onMessageReceived += OnMessageReceived;
                Debug.Log($"Twitchに再接続しました。チャンネル: {channelToJoin}");
            } catch (System.Exception e) {
                Debug.LogError($"Twitch再接続エラー: {e.Message}");
            }

        }
    }

    // メッセージが日本語を含まないか確認するメソッド
    private bool IsJapaneseFree(string message)
    {
        // 文字列内の各文字をチェック
        foreach (char c in message)
        {
            // 日本語の範囲に含まれる文字があるか確認
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter)
            {
                // 日本語の文字が含まれている場合
                return false; // 日本語が含まれている
            }
        }
        return true; // 日本語が含まれていない
    }

    private void OnMessageReceived(Message message) {
        if (message == null) {
            Debug.LogWarning("受信したメッセージがnullです");
            return;
        }

        Debug.Log($"Message received: {message.Command} - {(message.Parameters != null && message.Parameters.Length > 1 ? message.Parameters[1] : "no content")}");

        // チャットメッセージの処理
        if (message.Command == TwitchClient.Commands.PRIVMSG && message.Parameters != null && message.Parameters.Length > 1) {
            // string chatMessage = message.Parameters[1].ToLower();
            // チャットスクロールは生メッセージ
            // StartCoroutine(AddComment(message.ChatMessage));


            // https://fatwednesday.co.uk/assets/Assets/Twitcher/ReadMe.pdf
            string user = message.Info.displayName; // ユーザー名を取得
            Debug.Log($"DEAD BEEF user: {user}");

            // その配信で初めてのコメントかどうかをチェック
            if (!usersProfile.ContainsKey(user)) {
                usersProfile.Add(user, -1); // ユーザーを追加
                audioSource.PlayOneShot(entranceSound); // 音を鳴らす
            }

            // チャットメッセージを取得
            string chatMessage = message.ChatMessage;

            // 全文日本語が含まれていなければ翻訳処理に移行
            if (IsJapaneseFree(chatMessage))
            {
                // 日本語が含まれていない場合の処理
                Debug.Log("翻訳するよ！任せて！");
                // 翻訳結果を格納する変数
                string translatedText = null;

                // PostTranslate メソッドをコルーチンとして呼び出し、翻訳結果を取得
                StartCoroutine(HandlePostTranslate(chatMessage, user));
            }
            else
            {
                // コメント読み上げを開始
                StartCoroutine(SpeakComment(chatMessage, user));

                // コメントをニコニコ風に表示
                AddComment(chatMessage);
            }

            Debug.Log($"[{DateTime.Now:HH:mm:ss}] Chat message: {chatMessage}");

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

    private IEnumerator HandlePostTranslate(string chatMessage, string user) {
        // 翻訳処理を行う
        yield return StartCoroutine(deepLApiClient.PostTranslate(chatMessage, "JA", (result) => {
            // 翻訳結果を処理
            chatMessage = result;
        }));

        // 翻訳後の処理を続ける
        StartCoroutine(SpeakComment(chatMessage, user)); // コメント読み上げを開始
        AddComment(chatMessage); // コメントをニコニコ風に表示
    }

    private IEnumerator SpeakComment(string comment, string user) {
        // Todo: 複数同時に喋っても対応できるようにしたい、audioSourceを都度生成すれば良い？


        if (usersProfile[user] <= -1) {
            // コルーチンの結果を取得するための変数を用意
            int styleId = 3; // デフォルト値を設定
            // コルーチンを実行し、結果を取得
            yield return StartCoroutine(client.GetSpeakerRnd((result) => styleId = result));
            usersProfile[user] = styleId;
        }

        // 新しいAudioSourceを生成
        AudioSource spakAudioSource = gameObject.AddComponent<AudioSource>();

        // テキストからAudioClipを生成
        yield return client.TextToAudioClip(usersProfile[user], comment);

        if (client.AudioClip != null)
        {
            // AudioClipを取得し、AudioSourceにアタッチ
            spakAudioSource.clip = client.AudioClip;
            // AudioSourceで再生
            spakAudioSource.Play();
            // 再生が終わったらAudioSourceを破棄
            yield return new WaitForSeconds(spakAudioSource.clip.length);
            Destroy(spakAudioSource);
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
        StartCoroutine(ScrollComment(newComment));
    }

    private IEnumerator ScrollComment(GameObject comment) {
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
        // float canvasHeight = canvasRectTransform.sizeDelta.y;
        // float canvasWidth = canvasRectTransform.sizeDelta.x;
        float canvasHeight = 960;
        float canvasWidth = 1920;

        // テキストオブジェクトの高さを取得
        float textHeight = rectTransform.sizeDelta.y;
        Debug.Log($"DEAD BEEF textHeight: {textHeight}");

        // スクロール開始位置をランダムに設定
        // float randomYPosition = Random.Range(-canvas.GetComponent<RectTransform>().sizeDelta.y / 2, canvas.GetComponent<RectTransform>().sizeDelta.y / 2);
        // float randomYPosition = Random.Range(-canvasHeight / 2 + rectTransform.sizeDelta.y / 2, canvasHeight / 2 - rectTransform.sizeDelta.y / 2);
        float randomYPosition = UnityEngine.Random.Range(-textHeight, -canvasHeight + textHeight);
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