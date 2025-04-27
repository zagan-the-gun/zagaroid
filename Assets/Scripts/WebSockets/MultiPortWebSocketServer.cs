using System;
using System.Net;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;
using System.Threading.Tasks;


public class MultiPortWebSocketServer : MonoBehaviour {
    private WebSocketServer wss1;
    private WebSocketServer wss2;

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
}

// ポート50001用のエコーサービス
public class EchoService1 : WebSocketBehavior {

    public static event Action<string, string> OnUpdateTextSource;

    protected override async void OnMessage(MessageEventArgs e) {
        // 受信したメッセージをそのまま返す
        Send("Echo from port 50001: " + e.Data);
        Debug.Log("Received on port 50001: " + e.Data);

        // 字幕送信
        OnUpdateTextSource?.Invoke("ksk_subtitles", e.Data);
        // Todo:翻訳処理を入れる

    }
}

// ポート50002用のエコーサービス
public class EchoService2 : WebSocketBehavior {
    protected override void OnMessage(MessageEventArgs e) {
        // 受信したメッセージをそのまま返す
        Send("Echo from port 50002: " + e.Data);
        Debug.Log("Received on port 50002: " + e.Data);
    }
}