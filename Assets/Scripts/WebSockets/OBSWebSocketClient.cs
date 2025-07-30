using System;
using System.Text;
using System.Security.Cryptography;
using UnityEngine;
using WebSocketSharp;
using System.Collections;

public class OBSWebSocketClient : MonoBehaviour {
    private WebSocket ws;
    public string obsUrl = "ws://localhost:4455"; // OBS WebSocketのURL
    public string OBS_WEBSOCKETS_PASSWORD; // OBSの接続パスワード
    public string textSourceName = "ksk_subtitles"; // OBSのテキストソース名

    private string challengeResponse;
    private bool isConnected = false;
    private Coroutine reconnectCoroutine;
    private float reconnectInterval = 10f; // 再接続間隔を10秒に延長

    private void Awake() {
    }

    private void Start() {
        // OBS WebSocket接続用パスワードの読み取り
        OBS_WEBSOCKETS_PASSWORD = CentralManager.Instance != null ? CentralManager.Instance.GetObsWebSocketsPassword() : null;
        if (string.IsNullOrEmpty(OBS_WEBSOCKETS_PASSWORD)) {
            Debug.LogError("[OBS] パスワード読み込みエラー");
        }

        ConnectToOBS();
    }

    private void ConnectToOBS() {
        if (ws != null) {
            ws.Close();
        }

        ws = new WebSocket(obsUrl);

        // メッセージ受信時のイベントハンドラ
        ws.OnMessage += (sender, e) => {
            HandleMessage(e.Data);
        };

        // 接続成功時のイベントハンドラ
        ws.OnOpen += (sender, e) => {
            isConnected = true;
            if (reconnectCoroutine != null) {
                StopCoroutine(reconnectCoroutine);
                reconnectCoroutine = null;
            }
        };

        // エラー発生時のイベントハンドラ
        ws.OnError += (sender, e) => {
            Debug.LogError("[OBS] 接続エラー: " + e.Message);
            isConnected = false;
        };

        // 切断時のイベントハンドラ
        ws.OnClose += (sender, e) => {
            isConnected = false;
            
            // 自動再接続を開始
            if (reconnectCoroutine == null) {
                reconnectCoroutine = StartCoroutine(ReconnectToOBS());
            }
        };

        try {
            ws.Connect();
        } catch (Exception ex) {
            Debug.LogError("[OBS] 接続エラー: " + ex.Message);
            isConnected = false;
            
            // 自動再接続を開始
            if (reconnectCoroutine == null) {
                reconnectCoroutine = StartCoroutine(ReconnectToOBS());
            }
        }
    }

    private IEnumerator ReconnectToOBS() {
        yield return new WaitForSeconds(reconnectInterval);
        
        // 接続中でない場合のみ再接続を試行
        if (!isConnected) {
            ConnectToOBS();
        }
        
        // 再接続が失敗した場合、新しい再接続コルーチンを開始
        if (!isConnected) {
            reconnectCoroutine = StartCoroutine(ReconnectToOBS());
        } else {
            reconnectCoroutine = null;
        }
    }

    private void HandleMessage(string message) {
        try {
            var response = JsonUtility.FromJson<HelloResponse>(message);
            if (response.op == 0) { // Helloメッセージ
                if (response.d.authentication != null) { // 認証が必要な場合
                    string challenge = response.d.authentication.challenge;
                    string salt = response.d.authentication.salt;
                    challengeResponse = CreateAuth(challenge, salt);

                    // 1. GetAuthRequiredリクエストを送信
                    var authenticateRequest = new AuthenticateRequest {
                        op = 1,
                        d = new AuthenticateRequest.AuthenticateData {
                            rpcVersion = 1,
                            authentication = challengeResponse
                        }
                    };
                    SendMessage(JsonUtility.ToJson(authenticateRequest));
                }
            } else if (response.op == 2) {// 識別済み
                // negotiatedRpcVersionを取得
                int negotiatedRpcVersion = response.d.negotiatedRpcVersion;
            } else if (response.op == 5) {// イベント取得
            } else if (response.op == 7) {// リクエストレスポンス
            }

        } catch (Exception ex) {
            Debug.LogError("[OBS] メッセージ処理エラー: " + ex.Message);
        }
    }

    private void SendMessage(string message) {
        if (ws != null && isConnected) {
            try {
                ws.Send(message);
            } catch (Exception ex) {
                Debug.LogError("[OBS] メッセージ送信エラー: " + ex.Message);
                isConnected = false;
            }
        } else {
            Debug.LogWarning("[OBS] WebSocketが接続されていません。メッセージを送信できません: " + message);
        }
    }

    private string CreateAuth(string challenge, string salt) {
        // saltを使ってSHA-256ハッシュを生成
        string base64Secret = CreateSHA256(OBS_WEBSOCKETS_PASSWORD + salt);
        string challengeResponse = CreateSHA256(base64Secret + challenge);
        return challengeResponse;
    }

    private string CreateSHA256(string input) {
        byte[] passConcat = Encoding.UTF8.GetBytes(input);
        using var sha256 = SHA256.Create();
        byte[] sha256Hash = sha256.ComputeHash(passConcat);
        return Convert.ToBase64String(sha256Hash);
    }

    void OnEnable() {
        // セントラルマネージャからコメントを受信するイベントを登録
        CentralManager.OnObsSubtitlesSend += HandleObsSubtitlesSend;
    }

    private void OnDisable() {
        CentralManager.OnObsSubtitlesSend -= HandleObsSubtitlesSend;
    }

    private void OnDestroy() {
        CentralManager.OnObsSubtitlesSend -= HandleObsSubtitlesSend;
        if (reconnectCoroutine != null) {
            StopCoroutine(reconnectCoroutine);
        }
        if (ws != null)
        {
            ws.Close();
        }
    }

    public void HandleObsSubtitlesSend(string textSourceName, string text) {
        // 接続状態をチェック
        if (!isConnected) {
            // 再接続を開始
            if (reconnectCoroutine == null) {
                reconnectCoroutine = StartCoroutine(ReconnectToOBS());
            }
            return;
        }

        // OBS WebSocket APIを使用してテキストソースを更新
        var updateTextSourceRequest = new UpdateTextSourceRequest {
            op = 6, // SetTextGDIPlusのオペレーションコード
            d = new UpdateTextSourceRequest.Request { // UpdateTextSourceRequest.Requestを使用
                requestType = "SetInputSettings", // リクエストタイプを設定
                requestId = Guid.NewGuid().ToString(), // 一意のリクエストIDを生成
                requestData = new UpdateTextSourceRequest.Request.RequestData { // 正しいプロパティを使用
                    inputName = textSourceName,
                    inputSettings = new UpdateTextSourceRequest.Request.RequestData.InputSettings {
                        text = text
                    }
                }
            }
        };
        SendMessage(JsonUtility.ToJson(updateTextSourceRequest));
    }

    // Helloメッセージのレスポンス用クラス
    [Serializable]
    public class HelloResponse {
        public int op;
        public HelloData d;

        [Serializable]
        public class HelloData
        {
            public Authentication authentication; // 認証情報を追加
            public string obsWebSocketVersion;
            public int rpcVersion;
            public int negotiatedRpcVersion;

            [Serializable]
            public class Authentication
            {
                public string challenge;
                public string salt;
            }
        }
    }

    [Serializable]
     public class AuthenticateRequest {
        public int op;
        public AuthenticateData d;

        [Serializable]
        public class AuthenticateData {
            public int rpcVersion;
            public string authentication;
       }
    }

    [Serializable]
    public class EventResponce {
        public int op;
        public Event d;

        [Serializable]
        public class Event {
           public string eventType;
           public int eventIntent;
           public EventData eventData;

           [Serializable]
           public class EventData {
               public bool studioModeEnabled;
           }
       }
   }

    [Serializable]
    public class UpdateTextSourceRequest {
        public int op;
        public Request d;

        [Serializable]
        public class Request {
            public string requestType;
            public string requestId;
            public RequestData requestData;

            [Serializable]
            public class RequestData {
                public string inputName;
                public InputSettings inputSettings;

                [Serializable]
                public class InputSettings {
                    public string text;
                }
            }

        }
   }

    [Serializable]
    public class RequestResponse {
        public int op;
        public Request d;

        [Serializable]
        public class Request {
            public string requestType;
            public string requestId;
            public RequestData requestData;

            [Serializable]
            public class RequestData {
                public string inputName;
                public InputSettings inputSettings;

                [Serializable]
                public class InputSettings {
                    public string text;
                }
            }

        }
   }

}