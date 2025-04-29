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

    [Header("Video Settings")]
    [SerializeField] private VideoPlayerController videoPlayerController;
    [SerializeField] private VideoTriggerSetting[] videoSettings;

    private Canvas canvas; // Canvasの参照を保持

    // セントラルマネージャへ情報を送信するイベント
    public delegate void TwitchCommentReceivedDelegate(string user, string chatMessage);
    public static event TwitchCommentReceivedDelegate OnTwitchMessageReceived;
    public void SendCentralManager(string user, string chatMessage) {
        OnTwitchMessageReceived?.Invoke(user, chatMessage);
    }

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

        // セントラルマネージャーから設定情報を取得
        if (CentralManager.Instance != null) {
            // string apiKey = CentralManager.Instance.GetDeepLApiKey();
            // float volume = CentralManager.Instance.GetMasterVolume();
            // TranslationManager translator = CentralManager.Instance.TranslationManager;
            // ...
        } else {
            Debug.LogError("CentralManager instance not found!");
        }
    }

    void OnEnable() {
        // セントラルマネージャからコメントを受信するイベントを登録
        CentralManager.OnTwitchMessageSend += HandleTwitchMessageSend;
    }

    void OnDisable() {
        CentralManager.OnTwitchMessageSend -= HandleTwitchMessageSend;
    }

    // セントラルマネージャーから情報を受け取るイベント
    void HandleTwitchMessageSend(string text) {
        Debug.Log("Global Message Received: " + text);
        // messageをTwitchコメントに送信
        twitch.Client.SendPrivMessage(text);
    }

    private void Update() {
        if (twitch == null && twitch.Client == null) {
            Debug.Log("Twitchに再接続します");
            try {
                twitch = TwitchController.Create(authToken, channelToJoin);
                twitch.Client.onMessageReceived += OnMessageReceived;
                Debug.Log($"Twitchに再接続しました。チャンネル: {channelToJoin}");
            } catch (System.Exception e) {
                Debug.LogError($"Twitch再接続エラー: {e.Message}");
            }
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

            // https://fatwednesday.co.uk/assets/Assets/Twitcher/ReadMe.pdf
            string user = message.Info.displayName; // ユーザー名を取得
            Debug.Log($"DEAD BEEF user: {user}");
            // チャットメッセージを取得
            string chatMessage = message.ChatMessage;

            // セントラルマネージャーへ送信
            SendCentralManager(user, chatMessage);

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