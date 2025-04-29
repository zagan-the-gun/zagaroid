using System;
using System.Text;
using System.Security.Cryptography;
using UnityEngine;
using WebSocketSharp;

public class OBSWebSocketClient : MonoBehaviour {
    private WebSocket ws;
    public string obsUrl = "ws://localhost:4455"; // OBS WebSocketのURL
    public string OBS_WEBSOCKETS_PASSWORD = ""; // OBSの接続パスワード
    public string textSourceName = "ksk_subtitles"; // OBSのテキストソース名

    private string challengeResponse;

    private void Start() {
        // パスワードの読み取り
        OBS_WEBSOCKETS_PASSWORD = CentralManager.Instance != null ? CentralManager.Instance.GetObsWebSocketsPassword() : null;
        if (string.IsNullOrEmpty(OBS_WEBSOCKETS_PASSWORD)) {
            Debug.LogError("おーびーえすうぇぶそけっつぱすわーど！よみこみえらー！");
        } else {
            Debug.Log("おーびーえすうぇぶそけっつぱすわーどをよみこみました！: " + OBS_WEBSOCKETS_PASSWORD);
        }

        ws = new WebSocket(obsUrl);

        // メッセージ受信時のイベントハンドラ
        ws.OnMessage += (sender, e) => {
            Debug.Log("Received: " + e.Data);
            HandleMessage(e.Data);
        };

        // (認証前)接続成功時のイベントハンドラ
        ws.OnOpen += (sender, e) => {
            Debug.Log("Connected to OBS: " + sender);
        };

        // エラー発生時のイベントハンドラ
        ws.OnError += (sender, e) => {
            Debug.LogError("Error OBS: " + e.Message);
        };

        // 切断時のイベントハンドラ
        ws.OnClose += (sender, e) => {
            Debug.Log("Disconnected from OBS");
        };

        ws.Connect();

        // OnUpdateTextSource イベントに登録されたメソッドを実行
        EchoService1.OnUpdateTextSource += UpdateTextSource;

    }

    private void HandleMessage(string message) {
        try {
            var response = JsonUtility.FromJson<HelloResponse>(message);
            // Debug.Log("DEAD BEEF OBS 1: " + message);
            if (response.op == 0) { // Helloメッセージ
                // Debug.Log("DEAD BEEF OBS o1 1: " + message);
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
                    // Debug.Log("DEAD BEEF OBS o1 2 " + JsonUtility.ToJson(authenticateRequest));
                    ws.Send(JsonUtility.ToJson(authenticateRequest));
                }
            } else if (response.op == 2) {// 識別済み
                // negotiatedRpcVersionを取得
                int negotiatedRpcVersion = response.d.negotiatedRpcVersion;

                // デバッグログに表示
                Debug.Log("Negotiated RPC Version: " + negotiatedRpcVersion);

            } else if (response.op == 5) {// イベント取得
                // Debug.Log("DEAD BEEF OBS EventResponse" + message);
            } else if (response.op == 7) {// リクエストレスポンス
                // var requestResponse = JsonUtility.FromJson<RequestResponse>(message);
                // Debug.Log("DEAD BEEF OBS RequestResponse" + message);
            }

        } catch (Exception ex) {
            Debug.LogError("DEAD BEEF OBS Error processing message: " + ex.Message);
            Debug.LogError("DEAD BEEF OBS Received message: " + message);
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

    private void OnDestroy() {
        EchoService1.OnUpdateTextSource -= UpdateTextSource;
        if (ws != null)
        {
            ws.Close();
        }
    }

    public void UpdateTextSource(string textSourceName, string text) {
        // Debug.Log("DEAD BEEF OBS UpdateTextSource 発火！: " + text);
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
        // Debug.Log("DEAD BEEF OBS UpdateTextSource 1 request: " + updateTextSourceRequest);
        ws.Send(JsonUtility.ToJson(updateTextSourceRequest));
        // Debug.Log("DEAD BEEF OBS UpdateTextSource 2 request: " + JsonUtility.ToJson(updateTextSourceRequest));
        Debug.Log($"Updated text source '{textSourceName}' to: {text}");
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