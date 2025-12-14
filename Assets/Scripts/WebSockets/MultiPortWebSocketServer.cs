using UnityEngine;
using System;
using System.Net;
using WebSocketSharp;
using WebSocketSharp.Server;
using System.Collections.Generic;
using System.Collections;
using Newtonsoft.Json.Linq;
using System.Linq;
using Newtonsoft.Json;

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
                // MCPフォーマットはサーバ側でmethod/idを判定して適切にルーティング
                if (IsMcpFormat(message)) {
                    RouteMcpMessage(message);
                } else {
                    // レガシー形式（ゆかりネット/ゆかコネNEO）は STT なので常に Wipe へ送る
                    // ① 字幕表示タスク
                    OnMessageReceivedFromPort50001?.Invoke(user, message);
                    
                    // ② Wipe へ送信タスク（旧形式は常に人間の音声なので送る）
                    // 字幕ソース名 user から speaker 名に変換
                    string speaker = ResolveSubtitleSourceToSpeaker(user) ?? user;
                    if (Instance != null) {
                        Instance.BroadcastToWipeAIClients(message, speaker, "subtitle");
                    }
                    
                    Debug.Log($"[MCP] レガシー形式の字幕を受信: user={user}, speaker={speaker}");
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
                        // 字幕通知: paramsからtext/speakerを抽出し、タスク判定してイベント投げる
                        try {
                            var paramsObj = obj["params"] as JObject;
                            string text = paramsObj?["text"]?.ToString();
                            string speaker = paramsObj?["speaker"]?.ToString();
                            string messageType = paramsObj?["type"]?.ToString();
                            string subtitleName = ResolveSubtitleFromSpeaker(speaker);
                            if (string.IsNullOrEmpty(subtitleName)) subtitleName = mySubtitle;

                            // Actor設定から翻訳可否を判定
                            var actor = CentralManager.Instance?.GetActorByName(speaker);
                            bool enableTranslation = actor?.translationEnabled ?? true;

                            Debug.Log($"[MCP] subtitle route: speaker={speaker}, subtitle={subtitleName}, actorName={actor?.actorName}, actorType={actor?.type}, enableTranslation={enableTranslation}");
                            
                            // ① 字幕の表示・翻訳処理
                            CentralManager.Instance?.HandleWebSocketMessageFromPort50001(
                                subtitleName, 
                                text ?? string.Empty, 
                                enableTranslation
                            );

                            // ② Wipe 由来でなければ、Wipe AI（MenZ-GeminiCLI）へも転送してコメント生成
                            if (actor?.type != "wipe") {
                                if (Instance != null) {
                                    Instance.BroadcastToWipeAIClients(text ?? string.Empty, speaker, "subtitle");
                                }
                            }
                            
                            // ③ Wipe AI のコメント（type="comment"）で TTS が有効なら VoiceVox で音声化
                            if (actor?.type == "wipe" && messageType == "comment" && actor?.ttsEnabled == true) {
                                if (Instance != null) {
                                    CentralManager.Instance?.RequestVoiceVoxTTS(text ?? string.Empty, actor.actorName);
                                }
                            }

                            Debug.Log($"[MCP] 字幕通知を受信: {method}, speaker={speaker}, type={messageType}, actorType={actor?.type}, enableTranslation={enableTranslation}, ttsEnabled={actor?.ttsEnabled}");
                        } catch (Exception ex) {
                            Debug.LogError($"[MCP] 字幕通知の解析に失敗: {ex.Message}");
                        }
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

    /// <summary>
    /// speaker 名から対応する字幕フィールド名を取得
    /// ActorConfig 配列から検索、見つからない場合は従来の個別設定にフォールバック
    /// </summary>
    private string ResolveSubtitleFromSpeaker(string speaker) {
        try {
            if (string.IsNullOrEmpty(speaker)) return mySubtitle;
            
            // Actor配列から検索（新方式）
            // speaker 名で ActorConfig を検索し、対応する字幕フィールド名を返す
            var config = CentralManager.Instance?.GetActorByName(speaker);
            if (config != null) {
                return config.actorName + "_subtitle";  // 例: "zagan" → "zagan_subtitle"
            }
            
            // フォールバック：自分の字幕に統一
            string myName = CentralManager.Instance != null ? CentralManager.Instance.GetMyName() : null;
            if (!string.IsNullOrEmpty(myName) && string.Equals(speaker.Trim(), myName.Trim(), StringComparison.OrdinalIgnoreCase)) {
                return mySubtitle;
            }
            return mySubtitle;
        } catch {
            return mySubtitle;
        }
    }

    /// <summary>
    /// 字幕ソース名を話者名に変換
    /// actor化で不要になる
    /// </summary>
    private string ResolveSubtitleSourceToSpeaker(string sourceName) {
        try {
            if (string.IsNullOrEmpty(sourceName)) return null;

            // 字幕ソース名から "_subtitle" を削除して speaker 名を取得
            // 例: "zagan_subtitle" → "zagan"
            if (sourceName.EndsWith("_subtitle", StringComparison.OrdinalIgnoreCase)) {
                string speaker = sourceName.Substring(0, sourceName.Length - 9);  // "_subtitle" = 9文字
                Debug.Log($"[MCP] Converted sourceName '{sourceName}' to speaker '{speaker}'");
                return speaker;
            }

            // "_subtitle" がない場合はそのまま返す
            Debug.Log($"[MCP] sourceName '{sourceName}' has no '_subtitle' suffix, returning as-is");
            return sourceName;
        } catch (Exception ex) {
            Debug.LogError($"[MCP] ResolveSubtitleSourceToSpeaker error: {ex.Message}");
            return sourceName;
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
    /// "/" パスの全クライアント（WipeAI、ReazonSpeech等）にメッセージをブロードキャスト
    /// MCP フォーマットの字幕・コメント通知を送信
    /// </summary>
    /// <param name="text">テキスト内容</param>
    /// <param name="speaker">話者名（MyName/FriendName/WipeAIName等）</param>
    /// <param name="messageType">メッセージタイプ（"subtitle"/"comment"等）</param>
    public void BroadcastToWipeAIClients(string text, string speaker, string messageType = "subtitle") {
        try {
            // MCP JSON-RPC 2.0 フォーマットでメッセージ構築
            var messageObj = new {
                jsonrpc = "2.0",
                method = "notifications/subtitle",
                @params = new {
                    text = text,
                    speaker = speaker ?? "unknown",
                    type = messageType,
                    language = "ja"
                }
            };

            string jsonMessage = JsonConvert.SerializeObject(messageObj, Formatting.None);
            var host = wss1.WebSocketServices["/"];
            host.Sessions.Broadcast(jsonMessage);
            
            Debug.Log($"[MCP] WipeAI クライアントにブロードキャスト: speaker={speaker}, type={messageType}, text_length={text.Length}");
        } catch (Exception ex) {
            Debug.LogError($"[MCP] WipeAI クライアントへのブロードキャストエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 音声認識リクエストを送信（ReazonSpeech向け）
    /// </summary>
    /// <param name="audioData">音声データ（float配列）</param>
    /// <param name="speaker">話者名</param>
    /// <param name="sampleRate">サンプリングレート（デフォルト: 16000）</param>
    public void SendAudioRecognitionRequest(float[] audioData, string speaker, int sampleRate = 16000) {
        try {
            if (audioData == null || audioData.Length == 0) {
                Debug.LogWarning("[MCP] 音声データが空です");
                return;
            }

            // float[] → PCM16LE → Base64
            byte[] pcmBytes = ConvertFloatToPcm16Le(audioData);
            string audioDataB64 = System.Convert.ToBase64String(pcmBytes);

            // MCP JSON-RPC 2.0 リクエスト構築
            var requestObj = new {
                jsonrpc = "2.0",
                method = "recognize_audio",
                @params = new {
                    speaker = speaker ?? "unknown",
                    audio_data = audioDataB64,
                    sample_rate = sampleRate,
                    channels = 1,
                    format = "pcm16le"
                }
            };

            string jsonMessage = JsonConvert.SerializeObject(requestObj, Formatting.None);
            var host = wss1.WebSocketServices["/"];
            host.Sessions.Broadcast(jsonMessage);
            
            Debug.Log($"[MCP] 音声認識リクエスト送信: speaker={speaker}, samples={audioData.Length}, duration={audioData.Length / (float)sampleRate:F2}s");
        } catch (Exception ex) {
            Debug.LogError($"[MCP] 音声認識リクエスト送信エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// float配列をPCM16LEバイト配列に変換
    /// </summary>
    private byte[] ConvertFloatToPcm16Le(float[] audioData) {
        byte[] pcmBytes = new byte[audioData.Length * 2];
        for (int i = 0; i < audioData.Length; i++) {
            float sample = Mathf.Clamp(audioData[i], -1f, 1f);
            short pcmValue = (short)(sample * 32767f);
            pcmBytes[i * 2] = (byte)(pcmValue & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((pcmValue >> 8) & 0xFF);
        }
        return pcmBytes;
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
            Instance?.HandleMessage(50002, "subtitles", e.Data);
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