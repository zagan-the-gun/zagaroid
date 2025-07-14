using UnityEngine;
using System;
using System.Net;
using WebSocketSharp;
using WebSocketSharp.Server;
using System.Collections.Generic;
using System.Collections;

public class MultiPortWebSocketServer : MonoBehaviour {
    private WebSocketServer wss1; // ポート50001
    private WebSocketServer wss2; // ポート50002
    private readonly Dictionary<int, Action<string, string>> messageHandlers = new Dictionary<int, Action<string, string>>();

    public static MultiPortWebSocketServer Instance { get; private set; }

    // ポートごとのイベント定義
    public static event Action<string, string> OnMessageReceivedFromPort50001;
    public static event Action<string, string> OnMessageReceivedFromPort50002;

    private readonly Queue<Action> _executionQueue = new Queue<Action>();

    public string mySubtitle;

    private void Awake() {
        if (Instance == null) {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("MultiPortWebSocketServer: シングルトンとして初期化");
        } else if (Instance != this) {
            Debug.LogWarning("MultiPortWebSocketServer: 別のインスタンスが存在するため破棄");
            Destroy(gameObject);
        }
    }

    private void Start() {
        InitializeServer(50001);
        // InitializeServer(50000);
        InitializeServer(50002);
        mySubtitle = CentralManager.Instance != null ? CentralManager.Instance.GetMySubtitle() : null;
    }

    private void InitializeServer(int port) {
        try {
            WebSocketServer server = new WebSocketServer(IPAddress.Any, port);
            if (port == 50001) {
            // if (port == 50000) {
                server.AddWebSocketService<EchoService1>("/");
                wss1 = server; // インスタンスを保存
            } else if (port == 50002) {
                server.AddWebSocketService<EchoService2>("/");
                wss2 = server; // インスタンスを保存
            }
            server.Start();
            Debug.Log($"MultiPortWebSocketServer: WebSocket サーバー開始 - ws://localhost:{port}/");
        } catch (Exception ex) {
            Debug.LogError($"MultiPortWebSocketServer: ポート {port} のサーバー開始に失敗 - {ex.Message}");
        }
    }

    private void OnDestroy() {
        Debug.Log("MultiPortWebSocketServer: OnDestroy が呼び出されました");
        StopAllServers();
    }

    private void OnApplicationQuit() {
        Debug.Log("MultiPortWebSocketServer: OnApplicationQuit が呼び出されました");
        StopAllServers();
    }

    private void StopAllServers() {
        if (wss1 != null) {
            try {
                wss1.Stop();
                wss1 = null;
                Debug.Log("MultiPortWebSocketServer: WebSocket サーバー停止 - ポート: 50001");
            } catch (Exception ex) {
                Debug.LogError($"MultiPortWebSocketServer: ポート 50001 のサーバー停止に失敗 - {ex.Message}");
            }
        }
        if (wss2 != null) {
            try {
                wss2.Stop();
                wss2 = null;
                Debug.Log("MultiPortWebSocketServer: WebSocket サーバー停止 - ポート: 50002");
            } catch (Exception ex) {
                Debug.LogError($"MultiPortWebSocketServer: ポート 50002 のサーバー停止に失敗 - {ex.Message}");
            }
        }
        messageHandlers.Clear();
    }

    // 手動でサーバーを停止するためのパブリックメソッド
    public void StopServers() {
        Debug.Log("MultiPortWebSocketServer: 手動でサーバーを停止します");
        StopAllServers();
    }

    public void RegisterMessageHandler(int port, Action<string, string> handler) {
        messageHandlers[port] = handler;
        Debug.Log($"MultiPortWebSocketServer: ポート {port} にメッセージハンドラを登録");
    }

    // WebSocket スレッドからメインスレッドへ処理をディスパッチ
    public void Enqueue(Action action) {
        lock (_executionQueue) {
            _executionQueue.Enqueue(action);
        }
    }

    private void Update() {
        lock (_executionQueue) {
            while (_executionQueue.Count > 0) {
                _executionQueue.Dequeue().Invoke();
            }
        }
    }

    // 各 WebSocketBehavior からメッセージを受け取り、対応するポートのイベントを発行する
    public void HandleMessage(int port, string user, string message) {
        Debug.Log($"MultiPortWebSocketServer: メッセージ受信 (スレッド: {System.Threading.Thread.CurrentThread.ManagedThreadId}) - ポート: {port}, ユーザー: {user}, メッセージ: {message}");
        Enqueue(() => {
            Debug.Log($"HandleMessage (メインスレッド): ポート: {port}, ユーザー: {user}, メッセージ: {message}");
            if (port == 50001) {
            // if (port == 50000) {
                OnMessageReceivedFromPort50001?.Invoke(user, message);
            } else if (port == 50002) {
                OnMessageReceivedFromPort50002?.Invoke(user, message);
            }

            // (既存のポート固有のハンドラも呼び出す場合)
            // if (messageHandlers.TryGetValue(port, out var handler)) {
            //     handler?.Invoke(user, message);
            // } else {
            //     Debug.LogWarning($"MultiPortWebSocketServer: ポート {port} にハンドラが登録されていません");
            // }
        });
    }

    // private IEnumerator InvokeOnMainThread(Action action) {
    //     yield return null;
    //     action?.Invoke();
    // }

    // ポート50001用のエコーサービス (内部クラス)
    private class EchoService1 : WebSocketBehavior {
        protected override void OnMessage(MessageEventArgs e) {
            Debug.Log($"EchoService1 Received on port 50001 (スレッド: {System.Threading.Thread.CurrentThread.ManagedThreadId}): {e.Data}");
            Instance?.HandleMessage(50001, MultiPortWebSocketServer.Instance.mySubtitle, e.Data);
            // Instance?.HandleMessage(50000, MultiPortWebSocketServer.Instance.mySubtitle, e.Data);
            // ポート50001ではエコーバックなし
        }

        protected override void OnOpen() {
            Debug.Log($"EchoService1: クライアント接続 (ポート 50001)");
        }

        protected override void OnClose(CloseEventArgs e) {
            Debug.Log($"EchoService1: クライアント切断 (ポート 50001) - コード: {e.Code}, 理由: {e.Reason}");
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"EchoService1: エラー (ポート 50001) - {e.Message}");
        }
    }

    // ポート50002用のエコーサービス (内部クラス)
    private class EchoService2 : WebSocketBehavior {
        protected override void OnMessage(MessageEventArgs e) {
            Debug.Log($"EchoService2 Received on port 50002 (スレッド: {System.Threading.Thread.CurrentThread.ManagedThreadId}): {e.Data}");
            Instance?.HandleMessage(50002, "ksk_subtitles", e.Data);
            Send($"Echo from port 50002: {e.Data}");
        }

        protected override void OnOpen() {
            Debug.Log($"EchoService2: クライアント接続 (ポート 50002)");
        }

        protected override void OnClose(CloseEventArgs e) {
            Debug.Log($"EchoService2: クライアント切断 (ポート 50002) - コード: {e.Code}, 理由: {e.Reason}");
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"EchoService2: エラー (ポート 50002) - {e.Message}");
        }
    }
}