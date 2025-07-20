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
    // イベント
    public delegate void DiscordLogDelegate(string logMessage);
    public event DiscordLogDelegate OnDiscordLog;
    
    public delegate void MessageReceivedDelegate(string message);
    public event MessageReceivedDelegate OnMainGatewayMessageReceived;
    public event MessageReceivedDelegate OnVoiceGatewayMessageReceived;
    
    public delegate void ConnectionStateChangedDelegate(bool isConnected, string connectionType);
    public event ConnectionStateChangedDelegate OnConnectionStateChanged;
    
    // 接続関連
    private ClientWebSocket _mainWebSocket;
    private ClientWebSocket _voiceWebSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isMainConnected = false;
    private bool _isVoiceConnected = false;
    
    // ハートビート管理
    private System.Timers.Timer _mainHeartbeatTimer;
    private System.Timers.Timer _voiceHeartbeatTimer;
    private bool _mainHeartbeatAcknowledged = true;
    private int _mainSequence = 0;
    
    // Voice Gateway関連
    private long _lastVoiceHeartbeatAck = 0;
    private long _lastVoiceHeartbeatSend = 0;
    private int _missedVoiceHeartbeats = 0;
    private int? _voicePing = null;
    
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;
    
    /// <summary>
    /// コンストラクタ
    /// </summary>
    public DiscordNetworkManager(bool enableDebugLogging = true)
    {
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
            await _mainWebSocket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=9&encoding=json"), _cancellationTokenSource.Token);
            
            _isMainConnected = true;
            OnConnectionStateChanged?.Invoke(true, "Main Gateway");
            
            LogMessage("✅ Discord Gateway connected successfully", LogLevel.Info);
            
            // メッセージ受信ループを開始
            _ = Task.Run(ReceiveMainMessages, _cancellationTokenSource.Token);
            
            return true;
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
    /// Voice Gatewayに接続
    /// </summary>
    public async Task<bool> ConnectToVoiceGateway(string endpoint)
    {
        try
        {
            LogMessage($"🔌 Connecting to Voice Gateway: {endpoint}...", LogLevel.Info);
            
            _voiceWebSocket = new ClientWebSocket();
            await _voiceWebSocket.ConnectAsync(new Uri($"wss://{endpoint}/?v=4"), _cancellationTokenSource.Token);
            
            _isVoiceConnected = true;
            OnConnectionStateChanged?.Invoke(true, "Voice Gateway");
            
            LogMessage("✅ Voice Gateway connected successfully", LogLevel.Info);
            
            // メッセージ受信ループを開始
            _ = Task.Run(ReceiveVoiceMessages, _cancellationTokenSource.Token);
            
            return true;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway connection failed: {ex.Message}", LogLevel.Error);
            _isVoiceConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Voice Gateway");
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
    /// Voice Gatewayにメッセージを送信
    /// </summary>
    public async Task SendVoiceMessage(string message)
    {
        await SendWebSocketMessage(_voiceWebSocket, message, "Voice Gateway");
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
    /// Voice Gatewayからのメッセージを受信
    /// </summary>
    private async Task ReceiveVoiceMessages()
    {
        await ReceiveWebSocketMessages(_voiceWebSocket, OnVoiceGatewayMessageReceived, "Voice Gateway");
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
        else if (connectionName == "Voice Gateway")
        {
            _isVoiceConnected = false;
            OnConnectionStateChanged?.Invoke(false, "Voice Gateway");
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
    /// Voice Gatewayのハートビートを開始
    /// </summary>
    public void StartVoiceHeartbeat(double interval)
    {
        _voiceHeartbeatTimer?.Stop();
        _voiceHeartbeatTimer?.Dispose();
        
        int intervalMs = (int)interval;
        _voiceHeartbeatTimer = new System.Timers.Timer(intervalMs);
        _voiceHeartbeatTimer.Elapsed += async (sender, e) => {
            if (_isVoiceConnected && _voiceWebSocket?.State == WebSocketState.Open)
            {
                await SendVoiceHeartbeat();
            }
            else
            {
                _voiceHeartbeatTimer?.Stop();
                _voiceHeartbeatTimer?.Dispose();
                _voiceHeartbeatTimer = null;
            }
        };
        _voiceHeartbeatTimer.Start();
        LogMessage($"💓 Voice Gateway heartbeat started (interval: {intervalMs}ms)", LogLevel.Info);
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
    /// Voice Gatewayにハートビートを送信
    /// </summary>
    private async Task SendVoiceHeartbeat()
    {
        try
        {
            // ACKタイムアウト検出（15秒）
            if (_lastVoiceHeartbeatSend != 0)
            {
                var timeSinceLastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastVoiceHeartbeatSend;
                if (timeSinceLastHeartbeat > 15000 && _missedVoiceHeartbeats >= 1)
                {
                    LogMessage($"❌ Voice Gateway heartbeat ACK timeout ({timeSinceLastHeartbeat}ms > 15000ms)", LogLevel.Error);
                    await _voiceWebSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Heartbeat ACK timeout", CancellationToken.None);
                    return;
                }
            }
            
            // ミスしたハートビート数チェック
            if (_lastVoiceHeartbeatSend != 0 && _missedVoiceHeartbeats >= 3)
            {
                LogMessage($"❌ Voice Gateway missed too many heartbeats ({_missedVoiceHeartbeats}/3)", LogLevel.Error);
                await _voiceWebSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Too many missed heartbeats", CancellationToken.None);
                return;
            }
            
            _lastVoiceHeartbeatSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _missedVoiceHeartbeats++;
            
            // Voice Gateway準拠：nonceのみでハートビート送信
            var nonce = _lastVoiceHeartbeatSend;
            var heartbeat = DiscordPayloadHelper.CreateVoiceHeartbeatPayload(nonce, null);
            var heartbeatJson = JsonConvert.SerializeObject(heartbeat);
            await SendVoiceMessage(heartbeatJson);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway heartbeat error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// メインGatewayのハートビートACKを処理
    /// </summary>
    public void HandleMainHeartbeatAck()
    {
        _mainHeartbeatAcknowledged = true;
    }
    
    /// <summary>
    /// Voice GatewayのハートビートACKを処理
    /// </summary>
    public void HandleVoiceHeartbeatAck()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // 前回のACKから短時間で重複ACKが来た場合は無視
        if (_lastVoiceHeartbeatAck != 0 && (currentTime - _lastVoiceHeartbeatAck) < 100)
        {
            return;
        }
        
        _lastVoiceHeartbeatAck = currentTime;
        _missedVoiceHeartbeats = 0;
        
        if (_lastVoiceHeartbeatSend != 0)
        {
            _voicePing = (int)(_lastVoiceHeartbeatAck - _lastVoiceHeartbeatSend);
        }
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
    public bool IsVoiceConnected => _isVoiceConnected;
    
    /// <summary>
    /// Voice Gatewayのping値を取得
    /// </summary>
    public int? GetVoicePing() => _voicePing;
    
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
        
        _voiceHeartbeatTimer?.Stop();
        _voiceHeartbeatTimer?.Dispose();
        _voiceHeartbeatTimer = null;
        
        _mainWebSocket?.Dispose();
        _mainWebSocket = null;
        
        _voiceWebSocket?.Dispose();
        _voiceWebSocket = null;
        
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        LogMessage("✅ DiscordNetworkManager cleanup completed", LogLevel.Info);
    }
} 