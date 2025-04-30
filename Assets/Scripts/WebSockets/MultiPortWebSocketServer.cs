using System;
using System.Net;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;
using System.Threading.Tasks;


public class MultiPortWebSocketServer : MonoBehaviour {
    private WebSocketServer wss1;
    private WebSocketServer wss2;

    // セントラルマネージャへ情報を送信するイベント
    public delegate void WebSocketMessageReceivedDelegate(string user, string chatMessage);
    public static event WebSocketMessageReceivedDelegate OnWebSocketMessageReceived;
    public void SendCentralManager(string subtitle, string subtitleText) {
        OnWebSocketMessageReceived?.Invoke(subtitle, subtitleText);
    }

    private void Start() {
        // WebSocketサーバーをポート50001で初期化
        wss1 = new WebSocketServer(IPAddress.Any, 50001);
        wss1.AddWebSocketService<EchoService1>("/");
        // wss1.AddWebSocketService<EchoService1>("/", () => new EchoService1(obsClient)); // インスタンスを渡す
        wss1.Start();
        Debug.Log("起動したよ！WebSocket server started on ws://localhost:50001/");

        // WebSocketサーバーをポート50002で初期化
        wss2 = new WebSocketServer(IPAddress.Any, 50002);
        wss2.AddWebSocketService<EchoService2>("/");
        wss2.Start();
        Debug.Log("起動したよ！WebSocket server started on ws://localhost:50002/");
    }

    private void OnDestroy() {
        // サーバーを停止
        if (wss1 != null) {
            wss1.Stop();
            Debug.Log("WebSocket server on port 50001 stopped.");
        } if (wss2 != null) {
            wss2.Stop();
            Debug.Log("WebSocket server on port 50002 stopped.");
        }
    }
    // イベントハンドラーを登録するメソッド (CentralManager から呼び出す)
    public void RegisterWebSocketMessageHandler(int port, Action<string, string> handler) {
        switch (port) {
            case 50001:
                EchoService1.OnWebSocketMessageReceived += handler;
                break;
            case 50002:
                EchoService2.OnWebSocketMessageReceived += handler;
                break;
            default:
                Debug.LogError($"指定されたポート ({port}) のイベントハンドラー登録はサポートされていません。");
                break;
        }
    }

}

// ポート50001用のエコーサービス
public class EchoService1 : WebSocketBehavior {

    public static event Action<string, string> OnWebSocketMessageReceived;

    protected override void OnMessage(MessageEventArgs e) {
        // 受信したメッセージをそのまま返す
        // Send("Echo from port 50001: " + e.Data);
        Debug.Log("EchoService1 Received on port 50001: " + e.Data);
        // if (UnityMainThreadDispatcher.Instance == null) {
        //     Debug.Log("ディスパッチャーはヌルです！");
        // }

        // メインスレッドでイベントを処理
        Debug.Log($"EchoService1 スレッドID: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
        OnWebSocketMessageReceived?.Invoke("ksk_subtitles", e.Data);
        // try {
        //     UnityMainThreadDispatcher.Instance.Enqueue(() => {
        //         Debug.Log("ディスパッチャー！");
        //         // 字幕をCentralManagerに送信
        //         OnWebSocketMessageReceived?.Invoke("ksk_subtitles", e.Data);
        //     });
        //     Debug.Log("メソッド終了");
        // } catch (Exception ex) {
        //     Debug.LogError($"EchoService1.OnMessage で例外発生: {ex}");
        // }
    }
}

// ポート50002用のエコーサービス
public class EchoService2 : WebSocketBehavior {

    public static event Action<string, string> OnWebSocketMessageReceived;

    protected override void OnMessage(MessageEventArgs e) {
        // 受信したメッセージをそのまま返す
        Send("Echo from port 50002: " + e.Data);
        Debug.Log("Received on port 50002: " + e.Data);
        OnWebSocketMessageReceived?.Invoke("ksk_subtitles", e.Data);
    }
}