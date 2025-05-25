using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Lexone.UnityTwitchChat;

public class UnityTwitchChatController : MonoBehaviour {
    public Chatter chatterObject; // Unity-Twitch-Chat のクライアント

    private float pingInterval = 30f;
    private float lastPingTime;
    private float pingTimeout = 10f; // PING 送信後、この時間内に PONG がなければ切断とみなす
    private float pongTimeoutThreshold = 45f; // PING 間隔 + 猶予
    private DateTime lastPongReceivedTime;
    private bool isReconnecting = false; // 再接続処理中フラグ
    private bool isTwitchConnected = false;

    // セントラルマネージャへ情報を送信するイベント
    public delegate void TwitchCommentReceivedDelegate(string user, string chatMessage);
    public static event TwitchCommentReceivedDelegate OnTwitchMessageReceived;
    public void SendCentralManager(string user, string chatMessage) {
        OnTwitchMessageReceived?.Invoke(user, chatMessage);
    }

    void Start() {
        // メッセージ受信イベントの登録 (ライブラリのイベント名に合わせて修正が必要)
        IRC.Instance.OnChatMessage += OnChatMessage;
        IRC.Instance.OnConnectionAlert += OnConnectionAlert;
    }

    void OnEnable() {
        // セントラルマネージャからコメントを受信するイベントを登録
        CentralManager.OnTwitchMessageSend += HandleTwitchMessageSend;
    }

    void OnDisable() {
        CentralManager.OnTwitchMessageSend -= HandleTwitchMessageSend;
        IRC.Instance.OnChatMessage -= OnChatMessage;
        IRC.Instance.OnConnectionAlert -= OnConnectionAlert;
    }

    void OnDestroy() {
        CentralManager.OnTwitchMessageSend -= HandleTwitchMessageSend;
        IRC.Instance.OnChatMessage -= OnChatMessage;
        IRC.Instance.OnConnectionAlert -= OnConnectionAlert;
    }

    // void Update() {
    //     if (Time.time - lastPingTime > pingInterval) {
    //     IRC.Instance.Ping();
    //     lastPingTime = Time.time;
    //     }
    // }


    void Update() {
        if (IRC.Instance != null && isTwitchConnected && !isReconnecting) {
            if ((DateTime.Now - lastPongReceivedTime).TotalSeconds > pongTimeoutThreshold) {
                Debug.LogWarning($" PONG タイムアウト。再接続を試みます...");
                isReconnecting = true;
                IRC.Instance.Disconnect();
                // 少し遅延を入れてから再接続を試みる
                Invoke(nameof(AttemptReconnect), 5f);
                // SendCentralManager("ZAGAROID", "Twitchチャットのポーリングタイムアウトを検知");
                SendCentralManager("ZAGAROID", "チャットのポーリングタイムアウトを検知");
            } else if (Time.time - lastPingTime > pingInterval) {
                IRC.Instance.Ping();
                lastPingTime = Time.time;
            }
        } else if (IRC.Instance != null && isTwitchConnected) { // PING タイマーのみ動作させる
            if (Time.time - lastPingTime > pingInterval) {
                IRC.Instance.Ping();
                lastPingTime = Time.time;
            }
        }
    }

    void AttemptReconnect() {
        IRC.Instance.Connect();
        isReconnecting = false;
        lastPongReceivedTime = DateTime.Now; // 再接続後に初期化
    }

    // セントラルマネージャーから情報を受け取るイベント
    void HandleTwitchMessageSend(string text) {
        Debug.Log("Global Message Received: " + text);
        // messageをTwitchコメントに送信 (ライブラリの送信メソッドに合わせて修正が必要)
        IRC.Instance.SendChatMessage(text);
    }

    // メッセージ受信イベントハンドラ (ライブラリのイベント引数に合わせて修正が必要)
    private void OnChatMessage(Chatter chatter) {
        if (chatter == null) {
            Debug.LogWarning("受信したメッセージがnullです");
            return;
        }

        Debug.Log($" {chatter.tags.displayName}: {chatter.message}");

        // セントラルマネージャーへ送信
        SendCentralManager(chatter.tags.displayName, chatter.message);
    }

    private void OnConnectionAlert(IRCReply alert) {
        if (alert == null) {
            Debug.LogWarning("受信したアラートがnullです");
            return;
        }
        // Debug.Log($" Connection Alert: {alert}");
        switch (alert) {
            case IRCReply.PONG_RECEIVED:
                lastPongReceivedTime = DateTime.Now;
                // Debug.Log($" PONG を受信しました。詳細: {alert}");
                break;
            case IRCReply.NO_CONNECTION:
                Debug.LogError($"Twitch IRC への接続に失敗しました。 詳細: {alert}");
                // UI にエラーメッセージを表示するなどの処理
                break;
            case IRCReply.BAD_LOGIN:
                Debug.LogError($"Twitch IRC のログイン情報が無効です。 詳細: {alert}");
                // 設定画面へ誘導するなどの処理
                break;
            case IRCReply.MISSING_LOGIN_INFO:
                Debug.LogError($"Twitch IRC のログイン情報が不足しています。 詳細: {alert}");
                // 設定画面へ誘導するなどの処理
                break;
            case IRCReply.CONNECTED_TO_SERVER:
                Debug.Log($"Twitch IRC サーバーに接続しました。 詳細: {alert}");
                // 接続成功時の UI 更新などの処理
                break;
            case IRCReply.CONNECTION_INTERRUPTED:
                Debug.LogWarning($"Twitch IRC との接続が中断されました。再接続を試みます... 詳細: {alert}");
                // UI に警告を表示するなどの処理
                IRC.Instance.Disconnect();
                Invoke(nameof(AttemptReconnect), 5f);
                SendCentralManager("ZAGAROID", "IRCReply.CONNECTION_INTERRUPTEDイベントによって再接続しました");
                // IRC.Instance.Connect();
                break;
            case IRCReply.JOINED_CHANNEL:
                Debug.Log($"チャンネルに参加しました。 詳細: {alert}");
                // チャンネル参加成功時の UI 更新などの処理
                isTwitchConnected = true;
                break;
            default:
                Debug.Log($" その他の接続アラート 詳細: {alert}");
                break;
        }
    }
}