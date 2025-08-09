using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Discord Voice UDP通信専用のマネージャークラス
/// Discord.js VoiceUDPSocket.ts準拠の実装
/// </summary>
public class DiscordVoiceUdpManager : IDisposable
{
    // UDP関連の定数（このクラス専用）
    private const int UDP_BUFFER_SIZE = 65536;           // 64KB - UDP送受信バッファサイズ
    private const int UDP_SEND_TIMEOUT = 5000;           // 5秒 - UDP送信タイムアウト
    private const int UDP_DISCOVERY_TIMEOUT = 3000;      // 3秒 - IP Discovery応答待機時間
    private const int UDP_DISCOVERY_PACKET_SIZE = 74;    // 74バイト - IP Discoveryパケットサイズ
    
    // 音声パケット処理関連の定数（このクラス専用）
    private const int RTP_HEADER_SIZE = 12;              // RTPヘッダーサイズ
    private const int MIN_ENCRYPTED_DATA_SIZE = 40;      // 暗号化データ最小サイズ
    private const int MIN_AUDIO_PACKET_SIZE = 60;        // 音声パケット最小サイズ  
    private const int DISCORD_HEADER_SIZE = 12;          // Discordヘッダーサイズ
    
    // 暗号化関連の定数（このクラス専用）
    private static readonly string[] SUPPORTED_ENCRYPTION_MODES = { 
        "xsalsa20_poly1305", 
        "xsalsa20_poly1305_suffix"
        // "aead_xchacha20_poly1305_rtpsize", // 未実装のため除外
        // "aead_aes256_gcm_rtpsize" // 未実装のため除外
    };
    private const string DEFAULT_ENCRYPTION_MODE = "xsalsa20_poly1305";
    
    // イベント
    public delegate void DiscordLogDelegate(string logMessage);
    public event DiscordLogDelegate OnDiscordLog;
    
    public delegate void AudioPacketReceivedDelegate(byte[] opusData, uint ssrc, string userId);
    public event AudioPacketReceivedDelegate OnAudioPacketReceived;
    
    public delegate void ConnectionStateChangedDelegate(bool isConnected);
    public event ConnectionStateChangedDelegate OnConnectionStateChanged;
    
    // 発話終了検出イベント
    public delegate void SpeechEndDetectedDelegate();
    public event SpeechEndDetectedDelegate OnSpeechEndDetected;
    
    // UDP関連
    private UdpClient _udpClient;
    private IPEndPoint _voiceServerEndpoint;
    private bool _isConnected = false;
    
    // Keep-Alive関連
    private System.Timers.Timer _keepAliveTimer;
    private uint _keepAliveCounter = 0;
    private const int KEEP_ALIVE_INTERVAL = 5000; // 5秒
    private const uint MAX_COUNTER_VALUE = 2_147_483_647; // 2^31 - 1
    
    // 音声関連
    private byte[] _secretKey;
    private string _encryptionMode;
    private Dictionary<uint, string> _ssrcToUserMap = new Dictionary<uint, string>();
    private uint _ourSSRC;
    
    // SSRC判定レース対策: SSRCごとのOpusプレロールバッファ（時間基準）
    private class PrerollFrame { public byte[] opusData; public DateTime enqueuedAtUtc; }
    private readonly Dictionary<uint, Queue<PrerollFrame>> _preRollOpusBySsrc = new Dictionary<uint, Queue<PrerollFrame>>();
    private readonly object _preRollLock = new object();
    private const int PREROLL_MAX_FRAMES = 32; // 安全上限
    private const int PREROLL_MAX_DURATION_MS = 300; // 最大保持時間（ms）
    
    // ログレベル管理
    private enum LogLevel { Debug, Info, Warning, Error }
    private bool _enableDebugLogging = true;

    /// <summary>
    /// Discord.js VoiceUDPSocket.ts準拠のSocketConfig構造体
    /// </summary>
    public struct SocketConfig {
        public string ip;
        public int port;
    }

    /// <summary>
    /// コンストラクタ
    /// </summary>
    public DiscordVoiceUdpManager(bool enableDebugLogging = true) {
        _enableDebugLogging = enableDebugLogging;
    }
    
    /// <summary>
    /// UDP接続をセットアップ
    /// </summary>
    public async Task<bool> SetupUdpClient(IPEndPoint voiceServerEndpoint, bool forAudio = false) {
        try {
            _voiceServerEndpoint = voiceServerEndpoint;
            
            LogMessage($"🔌 Setting up UDP client (forAudio: {forAudio})...", LogLevel.Info);
            
            // 音声用の場合は既存クライアントを再利用
            if (forAudio && _udpClient != null) {
                LogMessage($"🔄 Reusing existing UDP client for audio reception", LogLevel.Info);
                _isConnected = true;
                OnConnectionStateChanged?.Invoke(true);
                LogMessage($"✅ UDP client setup completed (forAudio: {forAudio})", LogLevel.Info);
                return true;
            }
            
            // Discovery用の場合のみ、既存クライアントがあればクローズ
            if (!forAudio && _udpClient != null) {
                _udpClient?.Close();
                _udpClient?.Dispose();
            }
            
            _udpClient = new UdpClient();
            _udpClient.Client.ReceiveBufferSize = UDP_BUFFER_SIZE;
            _udpClient.Client.SendBufferSize = UDP_BUFFER_SIZE;
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.ReceiveTimeout = 0;
            _udpClient.Client.SendTimeout = UDP_SEND_TIMEOUT;
            
            // UDPクライアントをバインド
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var boundEndpoint = (IPEndPoint)_udpClient.Client.LocalEndPoint;
            LogMessage($"📍 UDP client bound to {boundEndpoint.Address}:{boundEndpoint.Port}");
            
            _isConnected = true;
            OnConnectionStateChanged?.Invoke(true);
            
            LogMessage($"✅ UDP client setup completed (forAudio: {forAudio})", LogLevel.Info);
            return true;
        } catch (Exception ex) {
            LogMessage($"❌ UDP client setup failed: {ex.Message}", LogLevel.Error);
            _isConnected = false;
            OnConnectionStateChanged?.Invoke(false);
            return false;
        }
    }
    
    /// <summary>
    /// UDP IP Discoveryを実行
    /// 元の動作していたDiscordBotClientコードから移植
    /// </summary>
    public async Task<SocketConfig?> PerformIpDiscovery(uint ssrc) {
        try
        {
            LogMessage("🔍 Performing UDP IP discovery...", LogLevel.Info);
            
            // Discovery パケットを作成（元のCreateDiscoveryPacketから移植）
            var discoveryBuffer = CreateDiscoveryPacket(ssrc);
            
            // パケットを送信（元のSendDiscoveryPacketから移植）
            await _udpClient.SendAsync(discoveryBuffer, discoveryBuffer.Length, _voiceServerEndpoint);
            LogMessage("📤 Discovery packet sent", LogLevel.Debug);
            
            // 発見応答を待機（元のWaitForDiscoveryResponseから移植）
            var receiveTask = _udpClient.ReceiveAsync();
            var timeoutTask = Task.Delay(UDP_DISCOVERY_TIMEOUT);
            var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
            if (completedTask == receiveTask) {
                var result = await receiveTask;
                return ProcessDiscoveryResponse(result);
            } else {
                LogMessage($"❌ Discovery timeout after {UDP_DISCOVERY_TIMEOUT}ms", LogLevel.Error);
                return null;
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ UDP discovery error: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
    
    /// <summary>
    /// 発見パケットを作成（元のDiscordBotClientから移植）
    /// </summary>
    private byte[] CreateDiscoveryPacket(uint ssrc) {
                    var discoveryBuffer = new byte[UDP_DISCOVERY_PACKET_SIZE];
        // Type: 1
        discoveryBuffer[0] = 0x00;
        discoveryBuffer[1] = 0x01;
        // Length: 70
        discoveryBuffer[2] = 0x00;
        discoveryBuffer[3] = 0x46;
        // SSRC (Big Endian)
        var ssrcBytes = BitConverter.GetBytes(ssrc);
        if (BitConverter.IsLittleEndian) {
            Array.Reverse(ssrcBytes);
        }
        Array.Copy(ssrcBytes, 0, discoveryBuffer, 4, 4);
        return discoveryBuffer;
    }
    
    /// <summary>
    /// Discovery レスポンスを処理
    /// </summary>
    private SocketConfig? ProcessDiscoveryResponse(UdpReceiveResult result)
    {
        try
        {
            var message = result.Buffer;
            if (message.Length >= UDP_DISCOVERY_PACKET_SIZE) {
                var localConfig = ParseLocalPacket(message);
                if (localConfig.HasValue) {
                    LogMessage($"📍 Discovered local config: {localConfig.Value.ip}:{localConfig.Value.port}", LogLevel.Info);
                    return localConfig;
                } else {
                    LogMessage($"❌ Failed to parse discovery response", LogLevel.Error);
                    return null;
                }
            }
            else {
                LogMessage($"❌ Invalid discovery response length: {message.Length}", LogLevel.Error);
                return null;
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Discovery response processing error: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
    
    /// <summary>
    /// DiscordのIP Discovery応答パケットを解析し、IPアドレスとポートを抽出します。
    /// 元のDiscordBotClientのParseLocalPacketから移植
    /// </summary>
    /// <param name="message">サーバーからの74バイトの応答パケット。</param>
    /// <returns>IPとポートを含むSocketConfigオブジェクト。解析に失敗した場合はnull。</returns>
    private SocketConfig? ParseLocalPacket(byte[] message)
    {
        try
        {
            var packet = message;
            // Discord.js VoiceUDPSocket.ts準拠の応答検証
            if (packet.Length < UDP_DISCOVERY_PACKET_SIZE) {
                LogMessage($"❌ Invalid packet length: {packet.Length} (expected {UDP_DISCOVERY_PACKET_SIZE})", LogLevel.Error);
                return null;
        }
            // Discord.js実装: if (message.readUInt16BE(0) !== 2) return;
            var responseType = (packet[0] << 8) | packet[1];
            if (responseType != 2) {
                LogMessage($"❌ Invalid response type: {responseType} (expected 2)", LogLevel.Error);
                return null;
            }
            // Discord.js実装: packet.slice(8, packet.indexOf(0, 8)).toString('utf8')
            var ipEndIndex = Array.IndexOf(packet, (byte)0, 8);
            if (ipEndIndex == -1) ipEndIndex = packet.Length;
            var ipLength = ipEndIndex - 8;
            var ipBytes = new byte[ipLength];
            Array.Copy(packet, 8, ipBytes, 0, ipLength);
            var ip = Encoding.UTF8.GetString(ipBytes);
            // Discord.js実装: packet.readUInt16BE(packet.length - 2)
            var port = (packet[packet.Length - 2] << 8) | packet[packet.Length - 1];
            if (string.IsNullOrEmpty(ip) || port <= 0) {
                LogMessage("❌ Invalid IP or port from parseLocalPacket", LogLevel.Error);
                return null;
            }
            return new SocketConfig { ip = ip, port = port };
        }
        catch (Exception ex)
        {
            LogMessage($"❌ parseLocalPacket error: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
    
    /// <summary>
    /// Keep-Aliveパケット送信を開始
    /// Discord.js VoiceUDPSocket.ts準拠の実装
    /// </summary>
    public void StartKeepAlive()
    {
        try
        {
            LogMessage("💓 Starting UDP Keep-Alive...", LogLevel.Info);
            
            // 即座に最初のKeep-Aliveを送信
            _ = Task.Run(SendKeepAlive);
            
            // 定期的なKeep-Alive送信を開始
            _keepAliveTimer?.Stop();
            _keepAliveTimer?.Dispose();
            
            _keepAliveTimer = new System.Timers.Timer(KEEP_ALIVE_INTERVAL);
            _keepAliveTimer.Elapsed += async (sender, e) => await SendKeepAlive();
            _keepAliveTimer.Start();
            
            LogMessage($"💓 UDP Keep-Alive started (interval: {KEEP_ALIVE_INTERVAL}ms)", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Keep-Alive start error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// Keep-Aliveパケットを送信
    /// Discord.js VoiceUDPSocket.ts準拠の実装
    /// </summary>
    public async Task SendKeepAlive()
    {
        try
        {
            if (_udpClient == null || _voiceServerEndpoint == null) {
                LogMessage("❌ Cannot send Keep-Alive: UDP client or endpoint not set", LogLevel.Warning);
                return;
            }
            
            // Discord.js VoiceUDPSocket.ts準拠：8バイトKeep-Aliveバッファ
            var keepAliveBuffer = new byte[8];
            var counterBytes = BitConverter.GetBytes(_keepAliveCounter);
            Array.Copy(counterBytes, keepAliveBuffer, Math.Min(counterBytes.Length, 8));
            
            await _udpClient.SendAsync(keepAliveBuffer, keepAliveBuffer.Length, _voiceServerEndpoint);
            
            // Discord.js VoiceUDPSocket.ts準拠：カウンター増加とオーバーフロー処理
            _keepAliveCounter = (_keepAliveCounter >= MAX_COUNTER_VALUE) ? 0 : _keepAliveCounter + 1;
            
            LogMessage($"📤 Keep-Alive sent (counter: {_keepAliveCounter - 1})", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Keep-Alive send error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// UDP音声データ受信を開始
    /// </summary>
    public void StartReceiveAudio() {
        try {
            LogMessage("🎧 Starting UDP audio reception...", LogLevel.Info);
            _ = Task.Run(ReceiveAudioLoop);
        } catch (Exception ex) {
            LogMessage($"❌ Audio reception start error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// UDP音声データ受信ループ
    /// </summary>
    private async Task ReceiveAudioLoop() {
        LogMessage($"🎧 Starting UDP audio reception loop. UDP Client: {_udpClient != null}, Connected: {_isConnected}", LogLevel.Info);
        LogMessage($"🎧 Voice Server Endpoint: {_voiceServerEndpoint}", LogLevel.Info);
        LogMessage($"🎧 Local Endpoint: {GetLocalEndpoint()}", LogLevel.Info);
        bool _timeout = false;
        
        while (_isConnected && _udpClient != null) {
            try {
                var receiveTask = _udpClient.ReceiveAsync();
                var timeoutTask = Task.Delay(100); // 100ms timeout
                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                if (completedTask == receiveTask) {
                    var result = await receiveTask;
                    ProcessAudioPacket(result.Buffer);
                    _timeout = false;
                } else if (!_timeout) {
                    // タイムアウト時に発話終了を検出
                    OnSpeechEndDetected?.Invoke();
                    _timeout = true;
                }
            } catch (Exception ex) {
                if (_isConnected) {
                    LogMessage($"UDP receive error: {ex.Message}", LogLevel.Error);
                }
                await Task.Delay(1000);
            }
        }
        LogMessage("🎧 UDP audio reception stopped", LogLevel.Info);
    }
    
    /// <summary>
    /// 音声パケットを処理
    /// </summary>
    public void ProcessAudioPacket(byte[] packet) {
        try {
            // 最小パケットサイズチェック
            if (packet.Length < MIN_AUDIO_PACKET_SIZE) {
                return;
            }
            
            // RTPヘッダーからSSRCを抽出
            if (packet.Length >= DISCORD_HEADER_SIZE) {
                var ssrc = BitConverter.ToUInt32(packet, 8);
                if (BitConverter.IsLittleEndian) {
                    ssrc = ((ssrc & 0xFF) << 24) | (((ssrc >> 8) & 0xFF) << 16) | 
                          (((ssrc >> 16) & 0xFF) << 8) | ((ssrc >> 24) & 0xFF);
                }

                // ユーザーIDを取得（未マッピングの可能性あり）
                string userId = null;
                _ssrcToUserMap.TryGetValue(ssrc, out userId);
                
                // 音声データの前処理を実行
                byte[] processedOpusData = ProcessAudioData(packet);
                
                if (processedOpusData != null) {
                    if (!string.IsNullOrEmpty(userId)) {
                        // マッピング済みなら即時発行
                        OnAudioPacketReceived?.Invoke(processedOpusData, ssrc, userId);
                    } else {
                        // 未マッピングならプレロールに積む（時間基準で古いものは捨てる）
                        lock (_preRollLock) {
                            if (!_preRollOpusBySsrc.TryGetValue(ssrc, out var queue)) {
                                queue = new Queue<PrerollFrame>();
                                _preRollOpusBySsrc[ssrc] = queue;
                            }
                            var frame = new PrerollFrame { opusData = processedOpusData, enqueuedAtUtc = DateTime.UtcNow };
                            queue.Enqueue(frame);
                            while (queue.Count > 0) {
                                var head = queue.Peek();
                                var ageMs = (int)(DateTime.UtcNow - head.enqueuedAtUtc).TotalMilliseconds;
                                if (ageMs > PREROLL_MAX_DURATION_MS || queue.Count > PREROLL_MAX_FRAMES) {
                                    queue.Dequeue();
                                } else {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        } catch (Exception ex) {
            LogMessage($"❌ Audio packet processing error: {ex.Message}", LogLevel.Error);
        }
    }
    
    /// <summary>
    /// 音声データの前処理（RTPヘッダー抽出、復号化、Discordヘッダー除去）
    /// </summary>
    private byte[] ProcessAudioData(byte[] packet) {
        try {
            // RTPヘッダーを抽出
            var rtpHeader = ExtractRtpHeader(packet);
            int headerLen = IsRtpsizeMode(_encryptionMode) ? GetUnencryptedHeaderLength(packet) : RTP_HEADER_SIZE;
            
            // 暗号化された音声データを抽出
            var encryptedData = ExtractEncryptedData(packet);
            
            // 暗号化データの有効性チェック
            if (!IsValidEncryptedData(encryptedData)) {
                return null;
            }
            
            // パケットの復号化
            byte[] decryptedOpusData = DiscordCrypto.DecryptVoicePacket(encryptedData, rtpHeader, _secretKey, _encryptionMode);
            
            // Discord独自のヘッダーを取り除く
            byte[] actualOpusData = ExtractOpusFromDiscordPacket(decryptedOpusData);
            
            return actualOpusData;
            
        } catch (Exception ex) {
            LogMessage($"❌ Audio data processing error: {ex.Message}", LogLevel.Error);
            return null;
        }
    }
    
    /// <summary>
    /// RTPヘッダーを抽出
    /// </summary>
    private byte[] ExtractRtpHeader(byte[] packet) {
        int headerLen = IsRtpsizeMode(_encryptionMode) ? GetUnencryptedHeaderLength(packet) : RTP_HEADER_SIZE;
        if (headerLen < RTP_HEADER_SIZE || headerLen > packet.Length) headerLen = RTP_HEADER_SIZE;
        var rtpHeader = new byte[headerLen];
        Array.Copy(packet, 0, rtpHeader, 0, headerLen);
        return rtpHeader;
    }
    
    /// <summary>
    /// 暗号化されたデータを抽出
    /// </summary>
    private byte[] ExtractEncryptedData(byte[] packet) {
        int headerLen = IsRtpsizeMode(_encryptionMode) ? GetUnencryptedHeaderLength(packet) : RTP_HEADER_SIZE;
        if (headerLen < RTP_HEADER_SIZE || headerLen > packet.Length) headerLen = RTP_HEADER_SIZE;
        var encryptedData = new byte[packet.Length - headerLen];
        Array.Copy(packet, headerLen, encryptedData, 0, encryptedData.Length);
        return encryptedData;
    }

    // rtpsize系（aead_*_rtpsize / xsalsa20_poly1305_lite_rtpsize）かどうか
    private bool IsRtpsizeMode(string mode)
    {
        if (string.IsNullOrEmpty(mode)) return false;
        return mode.Contains("rtpsize");
    }

    // 未暗号化RTPヘッダー長（12 + 4*CC + (X?4:0)）
    private int GetUnencryptedHeaderLength(byte[] packet)
    {
        if (packet == null || packet.Length < RTP_HEADER_SIZE) return RTP_HEADER_SIZE;
        byte b0 = packet[0];
        int cc = b0 & 0x0F;               // CC: CSRC count
        bool x = (b0 & 0x10) != 0;        // X: extension flag
        int headerLen = RTP_HEADER_SIZE + (cc * 4) + (x ? 4 : 0);
        // 上限ガード
        if (headerLen > packet.Length) headerLen = RTP_HEADER_SIZE;
        return headerLen;
    }
    
    /// <summary>
    /// 暗号化されたデータが有効かチェック
    /// </summary>
    private bool IsValidEncryptedData(byte[] encryptedData) {
        return encryptedData.Length >= MIN_ENCRYPTED_DATA_SIZE && _secretKey != null;
    }
    
    /// <summary>
    /// 復号済みペイロードからOpusデータを抽出（RTPヘッダーは既に取り除かれている想定）
    /// </summary>
    private byte[] ExtractOpusFromDiscordPacket(byte[] decryptedPayload) {
        if (decryptedPayload == null || decryptedPayload.Length == 0) {
            return null;
        }
        return decryptedPayload;
    }
    
    /// <summary>
    /// 暗号化キーを設定
    /// </summary>
    public void SetSecretKey(byte[] secretKey) {
        _secretKey = secretKey;
        LogMessage($"🔐 Secret key set (length: {secretKey?.Length ?? 0} bytes)", LogLevel.Info);
    }
    
    /// <summary>
    /// 暗号化モードを設定
    /// </summary>
    public void SetEncryptionMode(string encryptionMode) {
        _encryptionMode = encryptionMode;
        LogMessage($"🔐 Encryption mode set: {encryptionMode}", LogLevel.Info);
    }
    
    /// <summary>
    /// 利用可能な暗号化モードの中から、サポートされているものを選択します。
    /// </summary>
    /// <param name="availableModes">サーバーから提供された利用可能なモードの配列。</param>
    /// <returns>選択された暗号化モードの文字列。</returns>
    public string ChooseEncryptionMode(string[] availableModes) {
        if (availableModes == null || availableModes.Length == 0) {
            LogMessage("⚠️ Available encryption modes not provided; defaulting to xsalsa20_poly1305", LogLevel.Warning);
            return DEFAULT_ENCRYPTION_MODE;
        }

        // サポート済みモードを優先的に選択
        foreach (var supportedMode in SUPPORTED_ENCRYPTION_MODES) {
            if (availableModes.Contains(supportedMode)) {
                LogMessage($"🔐 Selected encryption mode: {supportedMode}", LogLevel.Info);
                return supportedMode;
            }
        }

        // サポート外のみが提示された場合は安全に拒否する（未対応モードを選ばない）
        LogMessage($"❌ No supported encryption modes available. Server offered: [{string.Join(", ", availableModes)}]", LogLevel.Error);
        // どうしても選ぶ必要がある場合はここで return availableModes[0] するが、復号不能となる。
        // 現状は既知モードが無い場合は既定値を返し、上位で再試行/失敗処理を行う。
        return DEFAULT_ENCRYPTION_MODE;
    }
    
    /// <summary>
    /// UDP Discovery処理を実行（IP Discovery + フォールバック）
    /// </summary>
    /// <param name="ssrc">自分のSSRC</param>
    /// <param name="voiceServerEndpoint">Voice Serverのエンドポイント</param>
    /// <param name="availableModes">利用可能な暗号化モード</param>
    /// <param name="onDiscoveryComplete">Discovery完了時のコールバック</param>
    /// <returns>Discoveryが成功した場合はtrue、それ以外はfalse</returns>
    public async Task<bool> PerformUdpDiscovery(uint ssrc, IPEndPoint voiceServerEndpoint, string[] availableModes, Func<string, int, string, Task<bool>> onDiscoveryComplete) {
        try {
            LogMessage("🔍 Starting UDP Discovery process...", LogLevel.Info);
            
            // UDPクライアントをセットアップ
            await SetupUdpClient(voiceServerEndpoint, false);
            
            // IP Discoveryを実行
            var discoveryResult = await PerformIpDiscovery(ssrc);
            if (discoveryResult.HasValue) {
                LogMessage($"📍 IP Discovery successful: {discoveryResult.Value.ip}:{discoveryResult.Value.port}", LogLevel.Info);
                string selectedMode = ChooseEncryptionMode(availableModes);
                return await onDiscoveryComplete(discoveryResult.Value.ip, discoveryResult.Value.port, selectedMode);
            }
            
            // IP Discoveryが失敗した場合のフォールバック
            LogMessage("⚠️ IP Discovery failed, trying fallback...", LogLevel.Warning);
            return await PerformUdpFallback(ssrc, availableModes, onDiscoveryComplete);
            
        } catch (Exception ex) {
            LogMessage($"❌ UDP Discovery error: {ex.Message}", LogLevel.Error);
            return await PerformUdpFallback(ssrc, availableModes, onDiscoveryComplete);
        }
    }
    
    /// <summary>
    /// UDP Discoveryのフォールバック処理
    /// </summary>
    private async Task<bool> PerformUdpFallback(uint ssrc, string[] availableModes, Func<string, int, string, Task<bool>> onDiscoveryComplete) {
        try {
            LogMessage("🔄 Performing UDP Discovery fallback...", LogLevel.Info);
            
            var localEndpoint = GetLocalEndpoint();
            if (localEndpoint == null) {
                LogMessage("❌ Cannot get local endpoint for fallback", LogLevel.Error);
                return false;
            }
            
            string fallbackIP = GetLocalIPAddress();
            string selectedMode = ChooseEncryptionMode(availableModes);
            
            LogMessage($"🔄 Using fallback config: {fallbackIP}:{localEndpoint.Port}", LogLevel.Info);
            return await onDiscoveryComplete(fallbackIP, localEndpoint.Port, selectedMode);
            
        } catch (Exception ex) {
            LogMessage($"❌ UDP Discovery fallback error: {ex.Message}", LogLevel.Error);
            return false;
        }
    }
    
    /// <summary>
    /// ローカルのIPアドレスを取得
    /// </summary>
    private string GetLocalIPAddress() {
        try {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0)) {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint?.Address.ToString() ?? "192.168.1.1";
            }
        } catch (Exception ex) {
            LogMessage($"❌ Local IP detection error: {ex.Message}", LogLevel.Warning);
            return "192.168.1.1";
        }
    }
    
    /// <summary>
    /// SSRC とユーザーIDのマッピングを設定
    /// </summary>
    public void SetSSRCMapping(uint ssrc, string userId) {
        _ssrcToUserMap[ssrc] = userId;
        LogMessage($"👤 SSRC mapping set: {ssrc} -> {userId}", LogLevel.Debug);

        // プレロールをフラッシュ
        Queue<PrerollFrame> preRoll = null;
        lock (_preRollLock) {
            if (_preRollOpusBySsrc.TryGetValue(ssrc, out var queue) && queue.Count > 0) {
                preRoll = new Queue<PrerollFrame>(queue);
                _preRollOpusBySsrc[ssrc] = new Queue<PrerollFrame>();
            }
        }
        if (preRoll != null) {
            while (preRoll.Count > 0) {
                var frame = preRoll.Dequeue();
                try {
                    OnAudioPacketReceived?.Invoke(frame.opusData, ssrc, userId);
                } catch (Exception ex) {
                    LogMessage($"⚠️ PreRoll dispatch error: {ex.Message}", LogLevel.Warning);
                }
            }
        }
    }
    
    /// <summary>
    /// 自分のSSRCを設定
    /// </summary>
    public void SetOurSSRC(uint ssrc) {
        _ourSSRC = ssrc;
        LogMessage($"🎤 Our SSRC set: {ssrc}", LogLevel.Info);
    }
    
    /// <summary>
    /// 接続状態を取得
    /// </summary>
    public bool IsConnected => _isConnected;
    
    /// <summary>
    /// ローカルエンドポイントを取得
    /// </summary>
    public IPEndPoint GetLocalEndpoint() {
        return _udpClient?.Client?.LocalEndPoint as IPEndPoint;
    }
    
    /// <summary>
    /// ログメッセージを生成し、イベントを発行
    /// </summary>
    private void LogMessage(string message, LogLevel level = LogLevel.Info) {
        if (!_enableDebugLogging && level == LogLevel.Debug) return;
        
        string prefix;
        switch (level) {
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
        
        string logMessage = $"[DiscordVoiceUdp] {DateTime.Now:HH:mm:ss} {prefix} {message}";
        OnDiscordLog?.Invoke(logMessage);
    }
    
    /// <summary>
    /// リソースをクリーンアップ
    /// </summary>
    public void Dispose() {
        LogMessage("🗑️ DiscordVoiceUdpManager disposing - performing cleanup", LogLevel.Info);
        
        _isConnected = false;
        
        _keepAliveTimer?.Stop();
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        
        _ssrcToUserMap.Clear();
        
        LogMessage("✅ DiscordVoiceUdpManager cleanup completed", LogLevel.Info);
    }
} 