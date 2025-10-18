using UnityEngine;
using System;
using System.Net;
using WebSocketSharp;
using WebSocketSharp.Server;
using System.Collections.Generic;
using System.Collections;
using Newtonsoft.Json.Linq;

public class MultiPortWebSocketServer : MonoBehaviour {
    private WebSocketServer wss1; // ポート50001
    private WebSocketServer wss2; // ポート50002
    private readonly Dictionary<int, Action<string, string>> messageHandlers = new Dictionary<int, Action<string, string>>();
    private string _wipeServicePath = "/wipe_subtitle"; // 動的に切替可能

    public static MultiPortWebSocketServer Instance { get; private set; }

    // ポートごとのイベント定義
    public static event Action<string, string> OnMessageReceivedFromPort50001;
    public static event Action<string, string> OnMessageReceivedFromPort50002;
    // Wipe用の受信イベント（messageのみ）
    public static event Action<string> OnWipeMessageReceived;

    private readonly Queue<Action> _executionQueue = new Queue<Action>();

    public string mySubtitle;

    // 翻訳クライアント（NMT）が接続されているかのフラグ
    private static bool isTranslationClientConnected = false;

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
                // 設定からWipeパスを取得
                string configured = CentralManager.Instance != null ? CentralManager.Instance.GetWipeAISubtitle() : "wipe_subtitle";
                _wipeServicePath = "/" + (string.IsNullOrEmpty(configured) ? "wipe_subtitle" : configured.Trim('/'));
                server.AddWebSocketService<WipeService>(_wipeServicePath);
                // 翻訳AI専用パス
                server.AddWebSocketService<TranslationService>("/translate_text");
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

    // 各 WebSocketBehavior からメッセージを受け取り、MCPフォーマットに応じてルーティング
    public void HandleMessage(int port, string user, string message) {
        Enqueue(() => {
            if (port == 50001) {
                // MCPフォーマットの判定とルーティング
                if (IsMcpFormat(message)) {
                    RouteMcpMessage(message);
                } else {
                    // レガシー形式は既存処理
                    OnMessageReceivedFromPort50001?.Invoke(user, message);
                }
            } else if (port == 50002) {
                OnMessageReceivedFromPort50002?.Invoke(user, message);
            }
        });
    }

    /// <summary>
    /// MCPフォーマットかどうかを判定
    /// </summary>
    private bool IsMcpFormat(string message) {
        try {
            if (string.IsNullOrEmpty(message)) return false;
            string trimmed = message.TrimStart();
            if (!trimmed.StartsWith("{")) return false;
            
            var obj = JObject.Parse(message);
            return obj["jsonrpc"]?.ToString() == "2.0";
        } catch {
            return false;
        }
    }

    /// <summary>
    /// MCPメッセージをmethodに応じてルーティング
    /// </summary>
    private void RouteMcpMessage(string message) {
        try {
            var obj = JObject.Parse(message);
            string method = obj["method"]?.ToString();
            string id = obj["id"]?.ToString();
            
            // methodが存在する場合（リクエストまたは通知）
            if (!string.IsNullOrEmpty(method)) {
                switch (method) {
                    case "notifications/subtitle":
                    case "notifications_subtitle":
                        // 字幕通知 → 既存の処理
                        OnMessageReceivedFromPort50001?.Invoke(mySubtitle, message);
                        Debug.Log($"[MCP] 字幕通知を受信: {method}");
                        break;
                        
                    case "translate_text":
                        // 翻訳リクエスト（通常は発生しない、zagaroidがクライアント側なので）
                        Debug.LogWarning($"[MCP] 予期しない翻訳リクエストを受信: {method}");
                        break;
                        
                    default:
                        Debug.LogWarning($"[MCP] 未対応のmethod: {method}");
                        break;
                }
            }
            // idだけ存在する場合（レスポンス）
            else if (!string.IsNullOrEmpty(id)) {
                bool hasResult = obj["result"] != null;
                bool hasError = obj["error"] != null;
                
                if (hasResult || hasError) {
                    // 翻訳レスポンス → TranslationController
                    if (TranslationController.Instance != null) {
                        TranslationController.Instance.OnWebSocketMessage(message);
                        Debug.Log($"[MCP] 翻訳レスポンスをTranslationControllerへ転送");
                    } else {
                        Debug.LogWarning("[MCP] TranslationControllerが見つかりません");
                    }
                } else {
                    Debug.LogWarning("[MCP] resultもerrorもないレスポンス");
                }
            } else {
                Debug.LogWarning("[MCP] methodもidもないメッセージ");
            }
        } catch (Exception ex) {
            Debug.LogError($"[MCP] ルーティングエラー: {ex.Message}");
        }
    }

    // Wipe用メッセージのメインスレッドディスパッチ
    private void HandleWipeMessageOnMainThread(string message) {
        Enqueue(() => {
            OnWipeMessageReceived?.Invoke(message);
        });
    }

    // Wipe: 全クライアントへブロードキャスト
    public void BroadcastToWipeClients(string message) {
        try {
            if (wss1 == null) return;
            var host = wss1.WebSocketServices[_wipeServicePath]; 
            host?.Sessions.Broadcast(message);
        } catch (Exception ex) {
            Debug.LogError($"[WS][WIPE] ブロードキャストエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 翻訳クライアント（NMT）にメッセージを送信（ブロードキャスト）
    /// </summary>
    public void SendToTranslationClient(string message) {
        if (!isTranslationClientConnected) {
            throw new Exception("翻訳クライアントが接続されていません");
        }
        
        try {
            if (wss1 == null) {
                throw new Exception("WebSocketサーバーが初期化されていません");
            }
            
            var host = wss1.WebSocketServices["/translate_text"];
            if (host == null) {
                throw new Exception("翻訳サービスが見つかりません");
            }
            
            host.Sessions.Broadcast(message);
            Debug.Log("[MCP] 翻訳クライアントにメッセージを送信");
        } catch (Exception ex) {
            Debug.LogError($"[MCP] 翻訳クライアントへの送信エラー: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 翻訳クライアントの接続状態を設定（TranslationServiceから呼ばれる）
    /// </summary>
    public static void SetTranslationClientConnectionState(bool connected) {
        isTranslationClientConnected = connected;
        
        // TranslationControllerに通知
        if (Instance != null) {
            Instance.Enqueue(() => {
                if (TranslationController.Instance != null) {
                    TranslationController.Instance.SetNmtConnectionState(connected);
                }
            });
        }
        Debug.Log($"[MCP] 翻訳クライアント接続状態: {(connected ? "接続" : "切断")}");
    }

    // 設定変更に応じて /wipe_subtitle サービスのパスを更新
    public void ReloadWipeServicePath() {
        try {
            if (wss1 == null) return;
            string configured = CentralManager.Instance != null ? CentralManager.Instance.GetWipeAISubtitle() : "wipe_subtitle";
            string newPath = "/" + (string.IsNullOrEmpty(configured) ? "wipe_subtitle" : configured.Trim('/'));
            if (newPath == _wipeServicePath) return;

            // 旧サービスを削除して新規に追加
            try { wss1.RemoveWebSocketService(_wipeServicePath); } catch {}
            wss1.AddWebSocketService<WipeService>(newPath);
            _wipeServicePath = newPath;
            Debug.Log($"[WS][WIPE] サービスパスを更新: {_wipeServicePath}");
        } catch (Exception ex) {
            Debug.LogError($"[WS][WIPE] パス更新エラー: {ex.Message}");
        }
    }

    // private IEnumerator InvokeOnMainThread(Action action) {
    //     yield return null;
    //     action?.Invoke();
    // }

    // ポート50001用のエコーサービス (内部クラス) - 字幕AI用
    private class EchoService1 : WebSocketBehavior {
        protected override void OnMessage(MessageEventArgs e) {
            Instance?.HandleMessage(50001, MultiPortWebSocketServer.Instance.mySubtitle, e.Data);
        }

        protected override void OnOpen() {
            Debug.Log("[MCP] 字幕クライアント接続");
        }

        protected override void OnClose(CloseEventArgs e) {
            Debug.Log("[MCP] 字幕クライアント切断");
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"EchoService1: エラー (ポート 50001 /) - {e.Message}");
        }
    }

    // /wipe_subtitle 用のサービス (内部クラス)
    private class WipeService : WebSocketBehavior {
        protected override void OnOpen() {
            // ブロードキャスト運用のため、個別登録処理は不要
        }

        protected override void OnMessage(MessageEventArgs e) {
            try {
                // 受信をメインスレッドでハンドリング（messageのみ）
                Instance?.HandleWipeMessageOnMainThread(e.Data);
            } catch (Exception ex) {
                Debug.LogError($"[WS][WIPE] OnMessage エラー: {ex.Message}");
            }
        }

        protected override void OnClose(CloseEventArgs e) {
            // ブロードキャスト運用のため、個別登録解除処理は不要
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"[WS][WIPE] エラー: {e.Message}");
        }
    }

    // /translate_text 用のサービス (内部クラス) - 翻訳AI専用
    private class TranslationService : WebSocketBehavior {
        protected override void OnOpen() {
            Debug.Log("[MCP] 翻訳クライアント接続");
            
            // 翻訳クライアントの接続を通知
            SetTranslationClientConnectionState(true);
        }

        protected override void OnMessage(MessageEventArgs e) {
            try {
                // 翻訳レスポンスをメインスレッドで処理
                Instance?.HandleMessage(50001, "translation", e.Data);
            } catch (Exception ex) {
                Debug.LogError($"[MCP][Translation] OnMessage エラー: {ex.Message}");
            }
        }

        protected override void OnClose(CloseEventArgs e) {
            Debug.Log("[MCP] 翻訳クライアント切断");
            
            // 翻訳クライアントの接続解除を通知
            SetTranslationClientConnectionState(false);
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"[MCP][Translation] エラー: {e.Message}");
        }
    }

    // ポート50002用のエコーサービス (内部クラス)
    private class EchoService2 : WebSocketBehavior {
        protected override void OnMessage(MessageEventArgs e) {
            // ログを削減: EchoService2の受信ログを削除
            Instance?.HandleMessage(50002, "ksk_subtitles", e.Data);
            Send($"Echo from port 50002: {e.Data}");
        }

        protected override void OnOpen() {
            // ログを削減: EchoService2の接続ログを削除
        }

        protected override void OnClose(CloseEventArgs e) {
            // ログを削減: EchoService2の切断ログを削除
        }

        protected override void OnError(ErrorEventArgs e) {
            Debug.LogError($"EchoService2: エラー (ポート 50002) - {e.Message}");
        }
    }
}