using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Discord WebSocket通信を管理するクラス
/// </summary>
public class DiscordNetworkManager : IDisposable
{
    // メインゲートウェイ専用の定数
    private const string DISCORD_OS = "unity";
    private const string DISCORD_BROWSER = "unity-bot";
    private const string DISCORD_DEVICE = "unity-bot";
    
    // イベント
    public delegate void DiscordLogDelegate(string logMessage);
    public event DiscordLogDelegate OnDiscordLog;
    
    public delegate void MessageReceivedDelegate(string message);
    public event MessageReceivedDelegate OnMainGatewayMessageReceived;
        
        // メインGateway: Hello受信時（heartbeat interval を通知）
        public delegate void HelloReceivedDelegate(int heartbeatInterval);
        public event HelloReceivedDelegate OnHelloReceived;
        
        // メインGateway: Dispatch受信時（イベントタイプとデータJSONを通知）
        public delegate void DispatchReceivedDelegate(string eventType, string dataJson);
        public event DispatchReceivedDelegate OnDispatchReceived;
    
    public delegate void ConnectionStateChangedDelegate(bool isConnected, string connectionType);
    public event ConnectionStateChangedDelegate OnConnectionStateChanged;
    
    // 接続関連
    private ClientWebSocket _mainWebSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isMainConnected = false;
    
    // ハートビート管理
    private System.Timers.Timer _mainHeartbeatTimer;
    private bool _mainHeartbeatAcknowledged = true;
    private int _mainSequence = 0;
    
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;

    /// <summary>
    /// Discord Gateway用のJSONオブジェクト作成ヘルパー
    /// </summary>
    internal static class DiscordPayloadHelper {
        /// <summary>
        /// メインGateway用ハートビートペイロードを作成
        /// </summary>
        public static object CreateHeartbeatPayload(int? sequence) => new {
            op = 1,
            d = sequence
        };

        /// <summary>
        /// Identifyペイロードを作成
        /// </summary>
        public static object CreateIdentifyPayload(string token) => new {
            op = 2,
            d = new {
                token = token,
                intents = DiscordConstants.DISCORD_INTENTS,
                properties = new {
                    os = DISCORD_OS,
                    browser = DISCORD_BROWSER,
                    device = DISCORD_DEVICE
                }
            }
        };

        /// <summary>
        /// ボイスチャンネル参加ペイロードを作成
        /// </summary>
        public static object CreateVoiceStateUpdatePayload(string guildId, string channelId) => new {
            op = 4,
            d = new {
                guild_id = guildId,
                channel_id = channelId,
                self_mute = true,
                self_deaf = false
            }
        };
    }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public DiscordNetworkManager(bool enableDebugLogging = true) {
        _enableDebugLogging = enableDebugLogging;
        _cancellationTokenSource = new CancellationTokenSource();
        
        // 受信メッセージを内部処理へルーティング
        OnMainGatewayMessageReceived += ProcessMainGatewayMessage;
    }
    
    /// <summary>
    /// メインGatewayに接続
    /// </summary>
    public async Task<bool> ConnectToMainGateway()
    {
        try
        {
            LogMessage("🔌 Connecting to Discord Gateway...", LogLevel.Info);
            
            _mainWebSocket = new ClientWebSocket();
            
            // 接続タイムアウトを設定（30秒）
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token))
            {
                await _mainWebSocket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=9&encoding=json"), combinedCts.Token);
            }
            
            _isMainConnected = true;
            OnConnectionStateChanged?.Invoke(true, "Main Gateway");
            
            LogMessage("✅ Discord Gateway connected successfully", LogLevel.Info);
            
            // メッセージ受信ループを開始
            _ = Task.Run(ReceiveMainMessages, _cancellationTokenSource.Token);
            
            return true;
        }
        catch (OperationCanceledException ex)
        {
            LogMessage($"❌ Discord Gateway connection timeout: {ex.Message}", LogLevel.Error);
            _isMainConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Main Gateway");
            return false;
        }
        catch (WebSocketException ex)
        {
            LogMessage($"❌ Discord Gateway WebSocket error: {ex.Message} (ErrorCode: {ex.WebSocketErrorCode})", LogLevel.Error);
            _isMainConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Main Gateway");
            return false;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Discord Gateway connection failed: {ex.Message}", LogLevel.Error);
            _isMainConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Main Gateway");
            return false;
        }
    }
    

    
    /// <summary>
    /// メインGatewayにメッセージを送信
    /// </summary>
    public async Task SendMainMessage(string message)
    {
        await SendWebSocketMessage(_mainWebSocket, message, "Main Gateway");
    }

    /// <summary>
    /// Identifyメッセージを送信
    /// </summary>
    public async Task SendIdentify(string token)
    {
        var identify = DiscordPayloadHelper.CreateIdentifyPayload(token);
        await SendMainMessage(JsonConvert.SerializeObject(identify));
    }

    /// <summary>
    /// ボイスチャンネル参加メッセージを送信
    /// </summary>
    public async Task SendJoinVoiceChannel(string guildId, string channelId)
    {
        var voiceStateUpdate = DiscordPayloadHelper.CreateVoiceStateUpdatePayload(guildId, channelId);
        await SendMainMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }

    /// <summary>
    /// ボイスチャンネル離脱メッセージを送信
    /// </summary>
    public async Task SendLeaveVoiceChannel(string guildId)
    {
        var voiceStateUpdate = DiscordPayloadHelper.CreateVoiceStateUpdatePayload(guildId, null);
        await SendMainMessage(JsonConvert.SerializeObject(voiceStateUpdate));
    }
    

    
    /// <summary>
    /// WebSocketにメッセージを送信する共通メソッド
    /// </summary>
    private async Task SendWebSocketMessage(ClientWebSocket webSocket, string message, string socketName)
    {
        try
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                LogMessage($"📤 {socketName} message sent: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
            }
            else
            {
                LogMessage($"❌ {socketName} is not connected", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Send {socketName.ToLower()} message error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// メインGatewayからのメッセージを受信
    /// </summary>
    private async Task ReceiveMainMessages()
    {
        await ReceiveWebSocketMessages(_mainWebSocket, OnMainGatewayMessageReceived, "Main Gateway");
    }
    

    
    /// <summary>
    /// WebSocket受信処理の共通メソッド
    /// </summary>
    private async Task ReceiveWebSocketMessages(ClientWebSocket webSocket, MessageReceivedDelegate messageHandler, string connectionName)
    {
        var buffer = new byte[DiscordConstants.WEBSOCKET_BUFFER_SIZE];
        var messageBuffer = new List<byte>();
        
        while (webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        
                        LogMessage($"📥 {connectionName} message received: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
                        messageHandler?.Invoke(message);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Voice Gateway 側と同じく、Identify 拒否や強制切断の原因究明には
                    // close code / reason が必須。「connection closed」だけ残ると追跡不能になる。
                    // 詳細は docs/integrations/discord.md § 11.1。
                    int? code = result.CloseStatus.HasValue ? (int?)(int)result.CloseStatus.Value : null;
                    string reason = string.IsNullOrEmpty(result.CloseStatusDescription) ? "(none)" : result.CloseStatusDescription;
                    LogMessage(
                        $"⚠️ {connectionName} connection closed by server: code={(code?.ToString() ?? "null")} status={result.CloseStatus} reason='{reason}'",
                        LogLevel.Warning);
                    break;
                }
            }
            catch (WebSocketException wsex)
            {
                int? code = webSocket?.CloseStatus.HasValue == true ? (int?)(int)webSocket.CloseStatus.Value : null;
                LogMessage(
                    $"⚠️ {connectionName} receive WebSocketException: {wsex.Message} (WebSocketErrorCode={wsex.WebSocketErrorCode}, closeCode={(code?.ToString() ?? "null")})",
                    LogLevel.Warning);
                break;
            }
            catch (Exception ex)
            {
                LogMessage($"{connectionName} receive error: {ex.Message}", LogLevel.Error);
                break;
            }
        }

        // 接続状態を更新
        if (connectionName == "Main Gateway")
        {
            _isMainConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Main Gateway");
        }
    }
    
    /// <summary>
    /// メインGatewayの受信メッセージを処理（opコードで分岐し、内部状態更新やイベント発火を行う）
    /// </summary>
    /// <param name="message">受信したJSON文字列</param>
    private void ProcessMainGatewayMessage(string message)
    {
        try
        {
            var payload = JsonConvert.DeserializeObject<DiscordGatewayPayload>(message);
            if (payload == null) return;

            if (payload.s.HasValue)
            {
                UpdateMainSequence(payload.s.Value);
            }

            switch (payload.op)
            {
                case 10: // Hello
                {
                    var dJson = payload.d?.ToString();
                    if (!string.IsNullOrEmpty(dJson))
                    {
                        var obj = JObject.Parse(dJson);
                        var interval = obj.Value<int>("heartbeat_interval");
                        StartMainHeartbeat(interval);
                        OnHelloReceived?.Invoke(interval);
                    }
                    break;
                }
                case 0: // Dispatch
                {
                    OnDispatchReceived?.Invoke(payload.t, payload.d?.ToString());
                    break;
                }
                case 11: // Heartbeat ACK
                {
                    HandleMainHeartbeatAck();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Main Gateway message processing error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// メインGatewayのハートビートを開始
    /// </summary>
    public void StartMainHeartbeat(int interval)
    {
        _mainHeartbeatTimer?.Stop();
        _mainHeartbeatTimer?.Dispose();
        
        _mainHeartbeatTimer = new System.Timers.Timer(interval);
        _mainHeartbeatTimer.Elapsed += async (sender, e) => {
            if (!_mainHeartbeatAcknowledged)
            {
                LogMessage("❌ Main Gateway heartbeat not acknowledged - reconnecting", LogLevel.Warning);
                await ReconnectMainGateway();
            }
            else
            {
                _mainHeartbeatAcknowledged = false;
                await SendMainHeartbeat();
            }
        };
        _mainHeartbeatTimer.Start();
        LogMessage($"💓 Main Gateway heartbeat started (interval: {interval}ms)", LogLevel.Info);
    }
    

    
    /// <summary>
    /// メインGatewayにハートビートを送信
    /// </summary>
    private async Task SendMainHeartbeat()
    {
        var heartbeat = DiscordPayloadHelper.CreateHeartbeatPayload(_mainSequence);
        await SendMainMessage(JsonConvert.SerializeObject(heartbeat));
    }
    

    
    /// <summary>
    /// メインGatewayのハートビートACKを処理
    /// </summary>
    public void HandleMainHeartbeatAck()
    {
        _mainHeartbeatAcknowledged = true;
    }
    

    
    /// <summary>
    /// メインGatewayのシーケンス番号を更新
    /// </summary>
    public void UpdateMainSequence(int sequence)
    {
        _mainSequence = sequence;
    }
    
    /// <summary>
    /// メインGatewayに再接続
    /// </summary>
    private async Task ReconnectMainGateway()
    {
        LogMessage("🔄 Attempting to reconnect to Main Gateway...", LogLevel.Info);
        
        try
        {
            _mainWebSocket?.Dispose();
            _mainWebSocket = null;
            _isMainConnected = false;
            
            await Task.Delay(DiscordConstants.RECONNECT_DELAY);
            await ConnectToMainGateway();
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Main Gateway reconnection failed: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 接続状態を取得
    /// </summary>
    public bool IsMainConnected => _isMainConnected;
    
    /// <summary>
    /// ログメッセージを生成し、イベントを発行
    /// </summary>
    private void LogMessage(string message, LogLevel level = LogLevel.Info)
    {
        if (!_enableDebugLogging && level == LogLevel.Debug) return;
        
        string prefix;
        switch (level)
        {
            case LogLevel.Debug:
                prefix = "🔍";
                break;
            case LogLevel.Warning:
                prefix = "⚠️";
                break;
            case LogLevel.Error:
                prefix = "❌";
                break;
            default:
                prefix = "ℹ️";
                break;
        }
        
        string logMessage = $"[DiscordNetwork] {DateTime.Now:HH:mm:ss} {prefix} {message}";
        OnDiscordLog?.Invoke(logMessage);
    }
    
    /// <summary>
    /// リソースをクリーンアップ
    /// </summary>
    public void Dispose()
    {
        LogMessage("🗑️ DiscordNetworkManager disposing - performing cleanup", LogLevel.Info);
        
        _cancellationTokenSource?.Cancel();
        
        _mainHeartbeatTimer?.Stop();
        _mainHeartbeatTimer?.Dispose();
        _mainHeartbeatTimer = null;
        
        _mainWebSocket?.Dispose();
        _mainWebSocket = null;
        
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        LogMessage("✅ DiscordNetworkManager cleanup completed", LogLevel.Info);
    }
} 

[Serializable]
public class DiscordGatewayPayload {
    public int op;
    public object d;
    public int? s;
    public string t;
}