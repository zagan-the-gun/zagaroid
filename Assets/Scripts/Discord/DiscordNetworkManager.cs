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
    private static class DiscordPayloadHelper {
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
                    LogMessage($"{connectionName} connection closed", LogLevel.Info);
                    break;
                }
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