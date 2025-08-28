using System;
using System.Collections.Generic;
using System.Collections;
using System.Threading;
using UnityEngine;
using WebSocketSharp;
using Newtonsoft.Json.Linq;

/// <summary>
/// Realtime 音声送信用 WebSocket クライアント
/// - サブプロトコル: pcm16.v1
/// - 仕様: 1バイナリ=1チャンク (PCM16LE / 16kHz / mono)
/// - 制御: テキスト JSON {"type":"hello", ...} → {"type":"ok", ...}
/// </summary>
public class RealtimeWebSocketClient : MonoBehaviour {
    private const string LOG_PREFIX = "RealtimeWebSocketClient:";
    [Header("Debug")] public bool enableDebugLogging = false;
    [Header("WebSocket Settings")]
    public string serverUrl = "ws://127.0.0.1:60001";
    public string subprotocol = "pcm16.v1";
    public int sampleRate = 16000;
    public int channels = 1;
    public int chunkSamples = 480; // 30ms @16kHz

    [SerializeField] private bool isReady; // ok 受信後に true

    private WebSocket ws;
    private readonly Queue<byte[]> outgoingFrames = new Queue<byte[]>();
    private readonly object queueLock = new object();
    private Thread sendThread;
    private AutoResetEvent queueEvent = new AutoResetEvent(false);
    private volatile bool running;
    private bool hasLoggedFirstSend;
    private readonly List<byte> _accumulator = new List<byte>(4096);
    // フラッシュ用の送信マーカー（長さ0のフレームを特別扱い）
    private static readonly byte[] FLUSH_MARKER = new byte[0];
    public static RealtimeWebSocketClient Instance { get; private set; }
    public bool CanSend { get { return isReady && IsWsActive; } }

    private bool IsWsActive {
        get {
            try {
                return ws != null && (ws.ReadyState == WebSocketState.Connecting || ws.ReadyState == WebSocketState.Open);
            } catch { return false; }
        }
    }
    private Coroutine reconnectCoroutine;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private int ExpectedChunkBytes => Math.Max(1, chunkSamples * channels * 2);

    private void Start() {
        Connect();
    }

    public void Connect() {
        // 既に接続中 or 接続済みなら二重接続を避ける
        if (IsWsActive) {
            Debug.Log($"{LOG_PREFIX} Connect skipped: ReadyState={ws.ReadyState}");
            return;
        }
        CleanupInternal();
        // CentralManagerからURLを上書き（存在すれば）
        try {
            if (CentralManager.Instance != null) {
                var url = CentralManager.Instance.GetRealtimeAudioWsUrl();
                if (!string.IsNullOrEmpty(url)) serverUrl = url;
            }
        } catch { }
        try {
            ws = new WebSocket(serverUrl, subprotocol);
            ws.Compression = CompressionMethod.None;

            ws.OnOpen += (s, e) => {
                Debug.Log($"{LOG_PREFIX} Connected: {serverUrl} (subprotocol={subprotocol})");
                SendHello();
            };

            ws.OnMessage += (s, e) => {
                if (e.IsText) HandleTextMessage(e.Data);
            };

            ws.OnError += (s, e) => {
                Debug.LogWarning($"{LOG_PREFIX} Error - {e.Message}");
                isReady = false;
                ScheduleReconnect();
            };

            ws.OnClose += (s, e) => {
                isReady = false;
                StopSenderLoop();
                Debug.Log($"{LOG_PREFIX} Closed (code={e.Code}, reason={e.Reason})");
                ScheduleReconnect();
            };

            Debug.Log($"{LOG_PREFIX} Connecting to {serverUrl} (async)...");
            ws.ConnectAsync();

            StartSenderLoop();
        } catch (Exception ex) {
            Debug.LogError($"{LOG_PREFIX} Connect failed - {ex.Message}");
            isReady = false;
        }
    }

    public void TryEnsureConnecting() {
        if (!IsWsActive) {
            Connect();
        }
    }

    public void Disconnect() {
        try {
            if (ws != null) ws.Close(CloseStatusCode.Normal);
        } catch (Exception ex) {
            Debug.LogWarning($"RealtimeWebSocketClient: Close error - {ex.Message}");
        }
        finally {
            CleanupInternal();
        }
    }

    private void OnDestroy() {
        Disconnect();
    }

    private void OnApplicationQuit() {
        Disconnect();
    }

    private void SendHello() {
        var hello = $"{{\"type\":\"hello\",\"format\":\"pcm16\",\"sample_rate\":{sampleRate},\"channels\":{channels},\"chunk_samples\":{chunkSamples}}}";
        try {
            ws.Send(hello);
            Debug.Log($"{LOG_PREFIX} Sent json = {hello}");
        } catch (Exception ex) {
            Debug.LogWarning($"{LOG_PREFIX} hello send failed - {ex.Message}");
        }
    }

    private void HandleTextMessage(string text) {
        try {
            var obj = JObject.Parse(text);
            var typeVal = (string)obj["type"];
            if (string.Equals(typeVal, "ok", StringComparison.OrdinalIgnoreCase)) {
                isReady = true;
                Debug.Log($"{LOG_PREFIX} READY (ok) received");
            } else if (string.Equals(typeVal, "error", StringComparison.OrdinalIgnoreCase)) {
                Debug.LogWarning($"{LOG_PREFIX} server error - {text}");
            }
        } catch (Exception ex) {
            Debug.LogWarning($"{LOG_PREFIX} invalid JSON text message: {ex.Message}");
        }
    }

    public void EnqueuePcmFrame(byte[] pcmFrame) {
        if (pcmFrame == null || pcmFrame.Length == 0) return;
        // 接続準備前でも受け取り、READY後に送るために蓄積
        AppendAndSliceToQueue(pcmFrame);
    }

    private void StartSenderLoop() {
        running = true;
        sendThread = new Thread(SenderLoop) { IsBackground = true, Name = "RealtimeWsSender" };
        sendThread.Start();
    }

    private void StopSenderLoop() {
        running = false;
        queueEvent.Set();
        try {
            if (sendThread != null && sendThread.IsAlive) sendThread.Join(200);
        } catch { }
        sendThread = null;
        lock (queueLock) { outgoingFrames.Clear(); }
    }

    private void SenderLoop() {
        while (running) {
            queueEvent.WaitOne(50);
            if (!running) break;

            if (!isReady || ws == null) continue;

            // 送信用キューから1件取得

            byte[] frame = null;
            lock (queueLock) {
                if (outgoingFrames.Count > 0) frame = outgoingFrames.Dequeue();
            }
            if (frame == null) continue;

            // フラッシュマーカーの場合は flush を送信
            if (frame.Length == 0) {
                try {
                    ws.Send("{\"type\":\"flush\"}");
                    Debug.Log($"{LOG_PREFIX} 発話終了: flushを送信しました");
                    hasLoggedFirstSend = false;
                } catch (Exception ex) {
                    Debug.LogWarning($"{LOG_PREFIX} flush send failed - {ex.Message}");
                }
                continue;
            }

            try {
                ws.Send(frame);
                if (!hasLoggedFirstSend) {
                    hasLoggedFirstSend = true;
                    Debug.Log($"{LOG_PREFIX} 発話開始: PCM送信を開始します (first_frame={frame.Length} bytes)");
                }

                // フラッシュはキューマーカーで制御するため、ここでの判定は不要
            } catch (Exception ex) {
                Debug.LogWarning($"{LOG_PREFIX} send failed - {ex.Message}");
            }
        }
    }

    private void CleanupInternal() {
        StopSenderLoop();
        if (ws != null) {
            try { ws.Close(); } catch { }
            ws = null;
        }
        isReady = false;
        hasLoggedFirstSend = false;
    }

    private bool IsSocketOpenOrConnecting() {
        try {
            if (ws == null) return false;
            return ws.ReadyState == WebSocketState.Open || ws.ReadyState == WebSocketState.Connecting;
        } catch { return false; }
    }

    private void ScheduleReconnect() {
        if (!gameObject.activeInHierarchy) return;
        if (reconnectCoroutine != null) return;
        reconnectCoroutine = StartCoroutine(ReconnectCoroutine());
    }

    private IEnumerator ReconnectCoroutine() {
        // 簡易バックオフ
        float[] delays = new float[] { 1f, 2f, 5f, 5f };
        int attempt = 0;
        while (!isReady) {
            float wait = delays[Mathf.Min(attempt, delays.Length - 1)];
            if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} Reconnecting in {wait:0.#}s (attempt {attempt + 1})");
            yield return new WaitForSeconds(wait);
            // 既にOpen/Connectingならスキップ
            if (IsSocketOpenOrConnecting()) {
                // ok待ち
                yield return new WaitForSeconds(0.5f);
                if (isReady) break;
                attempt++;
                continue;
            }
            Connect();
            // 接続試行後、少し様子を見る
            yield return new WaitForSeconds(0.5f);
            if (isReady) break;
            attempt++;
            // ループ継続
        }
        if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} Reconnect loop finished (ready={isReady})");
        reconnectCoroutine = null;
        yield break;
    }

    // 1発話セグメントを投入し、送信枯渇後に flush を送る
    public void EnqueueFloatSegment(float[] monoFloats) {
        if (monoFloats == null) return;
        // 未接続/非接続状態ではセグメントを破棄してスパムやメモリ増を防止
        if (!IsSocketOpenOrConnecting()) {
            if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} Drop segment (socket not open/connecting). samples={monoFloats.Length}");
            return;
        }
        var pcm = ConvertFloatToPcm16Le(monoFloats);
        if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} EnqueueFloatSegment samples={monoFloats.Length} bytes={pcm.Length} ready={isReady}");
        EnqueuePcmFrame(pcm);
        // セグメント末尾の端数は破棄して境界を明確化
        if (_accumulator.Count > 0) {
            _accumulator.Clear();
        }
        // セグメント終端のフラッシュをキューに積む（順序保証）
        EnqueueFlushMarker();
    }

    public void SendFlush() {
        // 送信用キューにフラッシュマーカーを投入して順序を保証
        EnqueueFlushMarker();
    }

    private void EnqueueFlushMarker() {
        lock (queueLock) {
            outgoingFrames.Enqueue(FLUSH_MARKER);
        }
        queueEvent.Set();
    }

    public static byte[] ConvertFloatToPcm16Le(float[] monoFloats) {
        int len = monoFloats.Length;
        var bytes = new byte[len * 2];
        int bi = 0;
        for (int i = 0; i < len; i++) {
            float f = Mathf.Clamp(monoFloats[i], -1f, 1f);
            short s = (short)Mathf.RoundToInt(f * 32767f);
            bytes[bi++] = (byte)(s & 0xFF);
            bytes[bi++] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    private void AppendAndSliceToQueue(byte[] bytes) {
        // 蓄積
        _accumulator.AddRange(bytes);
        int chunkBytes = ExpectedChunkBytes;
        // 固定長にスライスしてキュー投入
        while (_accumulator.Count >= chunkBytes) {
            var frame = new byte[chunkBytes];
            _accumulator.CopyTo(0, frame, 0, chunkBytes);
            _accumulator.RemoveRange(0, chunkBytes);
            lock (queueLock) {
                outgoingFrames.Enqueue(frame);
            }
            queueEvent.Set();
            if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} Queued frame chunkBytes={chunkBytes} acc_remain={_accumulator.Count}");
        }
    }

}

