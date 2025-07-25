using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Discord Voice Gateway専用のWebSocket通信管理クラス
/// </summary>
public class DiscordVoiceGatewayManager : IDisposable {
    // イベント
    public delegate void DiscordLogDelegate(string logMessage);
    public event DiscordLogDelegate OnDiscordLog;
    
    public delegate void MessageReceivedDelegate(string message);
    public event MessageReceivedDelegate OnMessageReceived;
    
    public delegate void ConnectionStateChangedDelegate(bool isConnected);
    public event ConnectionStateChangedDelegate OnConnectionStateChanged;
    
    // 接続関連
    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cancellationTokenSource;
    private bool _isConnected = false;
    
    // ハートビート管理
    private System.Timers.Timer _heartbeatTimer;
    private long _lastHeartbeatAck = 0;
    private long _lastHeartbeatSend = 0;
    private int _missedHeartbeats = 0;
    private int? _ping = null;
    
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;

    /// <summary>
    /// Voice Gateway用のJSONオブジェクト作成ヘルパー
    /// </summary>
    public static class VoicePayloadHelper {
        /// <summary>
        /// Voice Gateway用ハートビートペイロードを作成
        /// </summary>
        public static object CreateHeartbeatPayload(long nonce) => new {
            op = 3,
            d = nonce
        };
        
        /// <summary>
        /// Voice Gateway用Identifyペイロードを作成
        /// </summary>
        public static object CreateVoiceIdentifyPayload(string guildId, string userId, string sessionId, string token) => new {
            op = 0,
            d = new {
                server_id = guildId,
                user_id = userId,
                session_id = sessionId,
                token = token
            }
        };
        
        /// <summary>
        /// プロトコル選択ペイロードを作成
        /// </summary>
        public static object CreateSelectProtocolPayload(string ip, int port, string mode) => new {
            op = 1,
            d = new {
                protocol = "udp",
                data = new {
                    address = ip,
                    port = port,
                    mode = mode
                }
            }
        };
    }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public DiscordVoiceGatewayManager(bool enableDebugLogging = true) {
        _enableDebugLogging = enableDebugLogging;
        _cancellationTokenSource = new CancellationTokenSource();
    }
    
    /// <summary>
    /// Voice Gatewayに接続
    /// </summary>
    public async Task<bool> Connect(string endpoint)
    {
        try
        {
            LogMessage($"🔌 Connecting to Voice Gateway: {endpoint}...", LogLevel.Info);
            
            _webSocket = new ClientWebSocket();
            
            // 接続タイムアウトを設定（30秒）
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeoutCts.Token))
            {
                await _webSocket.ConnectAsync(new Uri($"wss://{endpoint}/?v=4"), combinedCts.Token);
            }
            
            _isConnected = true;
            OnConnectionStateChanged?.Invoke(true);
            
            LogMessage("✅ Voice Gateway connected successfully", LogLevel.Info);
            
            // メッセージ受信ループを開始
            _ = Task.Run(ReceiveMessages, _cancellationTokenSource.Token);
            
            return true;
        }
        catch (OperationCanceledException ex)
        {
            LogMessage($"❌ Voice Gateway connection timeout: {ex.Message}", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
        catch (WebSocketException ex)
        {
            LogMessage($"❌ Voice Gateway WebSocket error: {ex.Message} (ErrorCode: {ex.WebSocketErrorCode})", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway connection failed: {ex.Message}", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
    }
    
    /// <summary>
    /// メッセージを送信
    /// </summary>
    public async Task SendMessage(string message)
    {
        try
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                LogMessage($"📤 Voice Gateway message sent: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
            }
            else
            {
                LogMessage("❌ Voice Gateway is not connected", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Send voice gateway message error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// メッセージを受信
    /// </summary>
    private async Task ReceiveMessages()
    {
        var buffer = new byte[DiscordConstants.WEBSOCKET_BUFFER_SIZE];
        var messageBuffer = new List<byte>();
        
        while (_webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.Clear();
                        
                        LogMessage($"📥 Voice Gateway message received: {message.Substring(0, Math.Min(100, message.Length))}...", LogLevel.Debug);
                        OnMessageReceived?.Invoke(message);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    LogMessage("Voice Gateway connection closed", LogLevel.Info);
                    break;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Voice Gateway receive error: {ex.Message}", LogLevel.Error);
                break;
            }
        }
        
        _isConnected = false;
        OnConnectionStateChanged?.Invoke(false);
    }
    
    /// <summary>
    /// ハートビートを開始
    /// </summary>
    public void StartHeartbeat(double interval)
    {
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        
        int intervalMs = (int)interval;
        _heartbeatTimer = new System.Timers.Timer(intervalMs);
        _heartbeatTimer.Elapsed += async (sender, e) => {
            if (_isConnected && _webSocket?.State == WebSocketState.Open)
            {
                await SendHeartbeat();
            }
            else
            {
                _heartbeatTimer?.Stop();
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
            }
        };
        _heartbeatTimer.Start();
        LogMessage($"💓 Voice Gateway heartbeat started (interval: {intervalMs}ms)", LogLevel.Info);
    }
    
    /// <summary>
    /// ハートビートを送信
    /// </summary>
    private async Task SendHeartbeat()
    {
        try
        {
            // ACKタイムアウト検出（15秒）
            if (_lastHeartbeatSend != 0)
            {
                var timeSinceLastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastHeartbeatSend;
                if (timeSinceLastHeartbeat > 15000 && _missedHeartbeats >= 1)
                {
                    LogMessage($"❌ Voice Gateway heartbeat ACK timeout ({timeSinceLastHeartbeat}ms > 15000ms)", LogLevel.Error);
                    await _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Heartbeat ACK timeout", CancellationToken.None);
                    return;
                }
            }
            
            // ミスしたハートビート数チェック
            if (_lastHeartbeatSend != 0 && _missedHeartbeats >= 3)
            {
                LogMessage($"❌ Voice Gateway missed too many heartbeats ({_missedHeartbeats}/3)", LogLevel.Error);
                await _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Too many missed heartbeats", CancellationToken.None);
                return;
            }
            
            _lastHeartbeatSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _missedHeartbeats++;
            
            // Voice Gateway準拠：nonceのみでハートビート送信
            var nonce = _lastHeartbeatSend;
            var heartbeat = VoicePayloadHelper.CreateHeartbeatPayload(nonce);
            var heartbeatJson = JsonConvert.SerializeObject(heartbeat);
            await SendMessage(heartbeatJson);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway heartbeat error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// ハートビートACKを処理
    /// </summary>
    public void HandleHeartbeatAck()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // 前回のACKから短時間で重複ACKが来た場合は無視
        if (_lastHeartbeatAck != 0 && (currentTime - _lastHeartbeatAck) < 100)
        {
            return;
        }
        
        _lastHeartbeatAck = currentTime;
        _missedHeartbeats = 0;
        
        if (_lastHeartbeatSend != 0)
        {
            _ping = (int)(_lastHeartbeatAck - _lastHeartbeatSend);
        }
    }
    
    /// <summary>
    /// 再接続
    /// </summary>
    public async Task Reconnect(string endpoint)
    {
        LogMessage("🔄 Attempting to reconnect to Voice Gateway...", LogLevel.Info);
        
        try
        {
            _webSocket?.Dispose();
            _webSocket = null;
            _isConnected = false;
            
            await Task.Delay(DiscordConstants.RECONNECT_DELAY);
            await Connect(endpoint);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Voice Gateway reconnection failed: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 接続状態を取得
    /// </summary>
    public bool IsConnected => _isConnected;
    
    /// <summary>
    /// Pingを取得
    /// </summary>
    public int? Ping => _ping;
    
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
        
        string logMessage = $"[DiscordVoiceGateway] {DateTime.Now:HH:mm:ss} {prefix} {message}";
        OnDiscordLog?.Invoke(logMessage);
    }
    
    /// <summary>
    /// リソースをクリーンアップ
    /// </summary>
    public void Dispose()
    {
        LogMessage("🗑️ DiscordVoiceGatewayManager disposing - performing cleanup", LogLevel.Info);
        
        _cancellationTokenSource?.Cancel();
        
        _heartbeatTimer?.Stop();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        
        _webSocket?.Dispose();
        _webSocket = null;
        
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        
        LogMessage("✅ DiscordVoiceGatewayManager cleanup completed", LogLevel.Info);
    }
} 