namespace BiliVoxLive;

using System.Net.WebSockets;
using System.Net.Http;  // 添加 HttpClient 引用
using System.Text.Json;
using System.Text;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.RegularExpressions;  // 添加这一行

public interface IDanmakuService : IDisposable
{
    event EventHandler<DanmakuEventArgs>? OnDanmakuReceived;
    Task ConnectToRoomAsync(long roomId);
    Task DisconnectFromRoomAsync(long roomId);
    Task SendDanmakuAsync(long roomId, string content);
}

public class DanmakuEventArgs : EventArgs
{
    public string UserName { get; }
    public string Content { get; }
    public DateTime Timestamp { get; }
    public bool IsGift { get; }
    public bool IsSuperChat { get; }
    public bool IsGuardBuy { get; }  // 新增上舰标识
    public bool IsWarning { get; }    // 新增警告标识
    public int? SuperChatPrice { get; } // 新增SC价格
    public string? Color { get; }      // 新增消息颜色

    public DanmakuEventArgs(
        string userName, 
        string content, 
        bool isGift = false, 
        bool isSuperChat = false,
        bool isGuardBuy = false,
        bool isWarning = false,
        int? superChatPrice = null,
        string? color = null)
    {
        UserName = userName;
        Content = content;
        Timestamp = DateTime.Now;
        IsGift = isGift;
        IsSuperChat = isSuperChat;
        IsGuardBuy = isGuardBuy;
        IsWarning = isWarning;
        SuperChatPrice = superChatPrice;
        Color = color;
    }
}

public class OnlineRankCountEventArgs : DanmakuEventArgs
{
    public int Count { get; }

    public OnlineRankCountEventArgs(int count)
        : base("系统", $"当前观看人数: {count}")
    {
        Count = count;
    }
}

public class WatchedChangeEventArgs : DanmakuEventArgs
{
    public int Count { get; }

    public WatchedChangeEventArgs(int count)
        : base("系统", $"观看过的人数: {count}")
    {
        Count = count;
    }
}

public class DanmakuService : IDanmakuService
{
    private readonly LogService _logService;
    private readonly BiliApiService _biliApiService;
    private readonly CookieService _cookieService;  // 添加 CookieService
    private readonly Dictionary<long, ClientWebSocket> _roomClients = new();
    private readonly Dictionary<long, CancellationTokenSource> _heartbeatTokens = new();
    private readonly Dictionary<long, CancellationTokenSource> _receiveTokens = new();  // 新增，用于管理接收任务的取消令牌
    private bool _isConnected;
    private const int MAX_RECONNECT_ATTEMPTS = 5;  // 增加最大重连次数
    private readonly Dictionary<long, int> _reconnectAttempts = new();  // 跟踪重连次数
    private const int HEARTBEAT_INTERVAL = 20000;  // 降低心跳间隔到20秒
    private readonly SemaphoreSlim _connectionLock = new(1, 1);  // 添加连接锁
    private readonly SemaphoreSlim _disconnectLock = new(1, 1);  // 添加一个新的锁用于断开连接
    private long _currentRoomId; // 添加当前房间ID跟踪
    
    public event EventHandler<DanmakuEventArgs>? OnDanmakuReceived;

    public DanmakuService(LogService logService, BiliApiService biliApiService, CookieService cookieService)
    {
        _logService = logService;
        _biliApiService = biliApiService;
        _cookieService = cookieService;
    }

    private async Task<(string host, string token)> GetDanmuInfoAsync(long roomId)
    {
        try
        {
            var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={roomId}";
            using var client = new HttpClient();
            
            // 添加完整的请求头
            var cookie = _cookieService.GetCookie();
            if (!string.IsNullOrEmpty(cookie))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                client.DefaultRequestHeaders.Add("Referer", "https://live.bilibili.com");
                client.DefaultRequestHeaders.Add("Origin", "https://live.bilibili.com");
            }
            
            var response = await client.GetStringAsync(url);
            
            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(response);
            if (jsonResponse.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception($"获取弹幕服务器信息失败: {jsonResponse.GetProperty("message").GetString()}");
            }

            var data = jsonResponse.GetProperty("data");
            var token = data.GetProperty("token").GetString() ?? string.Empty;
            var hostList = data.GetProperty("host_list");
            var host = hostList.EnumerateArray().First();
            var hostUrl = $"wss://{host.GetProperty("host").GetString()}/sub";

            _logService.Info($"获取到弹幕服务器地址: {hostUrl}");
            return (hostUrl, token);
        }
        catch (Exception ex)
        {
            _logService.Error("获取弹幕服务器信息失败", ex);
            throw;
        }
    }

    public async Task ConnectToRoomAsync(long roomId)
    {
        await _connectionLock.WaitAsync();
        try
        {
            _logService.Info($"准备连接到房间 {roomId}");
            
            // 断开所有现有连接
            foreach (var existingRoomId in _roomClients.Keys.ToList())
            {
                _logService.Info($"断开旧房间 {existingRoomId} 的连接");
                await DisconnectFromRoomAsync(existingRoomId);
            }
            _roomClients.Clear();  // 确保完全清空
            
            await Task.Delay(100);  // 等待旧连接完全清理
            
            // 更新当前房间ID
            _currentRoomId = roomId;

            // 创建新连接
            var (host, token) = await GetDanmuInfoAsync(roomId);
            var newClient = new ClientWebSocket();
            newClient.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            
            await newClient.ConnectAsync(new Uri(host), CancellationToken.None);
            _logService.Info($"WebSocket连接成功: {host}");

            // 发送认证包
            var authPacket = CreateAuthenticationPacket(roomId, token);
            await newClient.SendAsync(authPacket, WebSocketMessageType.Binary, true, CancellationToken.None);
            _logService.Info($"已发送认证包到房间 {roomId}");
            
            // 等待服务器响应
            var buffer = new byte[1024];
            var result = await newClient.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Exception($"房间 {roomId} 认证失败");
            }

            // 存储新连接
            _roomClients[roomId] = newClient;
            
            // 先等待一下确保连接稳定
            await Task.Delay(100);

            // 启动心跳和消息接收
            var cts = new CancellationTokenSource();
            _heartbeatTokens[roomId] = cts;
            _ = HeartbeatLoopAsync(newClient, roomId, cts.Token);

            var receiveCts = new CancellationTokenSource();
            _receiveTokens[roomId] = receiveCts;
            _ = ReceiveLoopAsync(newClient, roomId, receiveCts.Token);

            _reconnectAttempts[roomId] = 0;
            _isConnected = true;
            
            _logService.Info($"成功连接到房间 {roomId}");
        }
        catch (Exception ex)
        {
            _logService.Error($"连接房间 {roomId} 失败: {ex.Message}");
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private byte[] CreateAuthenticationPacket(long roomId, string token)
    {
        var cookie = _cookieService.GetCookie();
        string uid = "0";
        
        // 从cookie中提取更多信息
        if (!string.IsNullOrEmpty(cookie))
        {
            var uidMatch = Regex.Match(cookie, @"DedeUserID=(\d+)");
            if (uidMatch.Success)
            {
                uid = uidMatch.Groups[1].Value;
                _logService.Debug($"从Cookie中提取到用户ID: {uid}");
            }
            else
            {
                _logService.Warning("无法从Cookie中提取用户ID");
            }
        }

        // 构建更完整的认证数据
        var authData = JsonSerializer.Serialize(new
        {
            uid = long.Parse(uid),
            roomid = roomId,
            protover = 3,
            platform = "web",
            type = 2,
            key = token,
            buvid = GetBuvidFromCookie(cookie),  // 添加buvid
            client_timestamp = DateTimeOffset.Now.ToUnixTimeSeconds()  // 添加时间戳
        });

        var authBytes = Encoding.UTF8.GetBytes(authData);
        var packet = new byte[16 + authBytes.Length];

        // 写入包头
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), 7);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);

        // 写入包体
        Buffer.BlockCopy(authBytes, 0, packet, 16, authBytes.Length);

        return packet;
    }

    private string GetBuvidFromCookie(string cookie)
    {
        var match = Regex.Match(cookie, @"buvid3=([^;]+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private byte[] CreateHeartbeatPacket()
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 16);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
        return packet;
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket client, long roomId, CancellationToken token)
    {
        try
        {
            var heartbeatPacket = CreateHeartbeatPacket();
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(heartbeatPacket, WebSocketMessageType.Binary, true, token);
                    _logService.Debug("已发送心跳包");
                    
                    // 等待心跳回应
                    var buffer = new byte[1024];
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var operation = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(8, 4));
                        if (operation == 3)  // 心跳回应
                        {
                            _logService.Debug("收到心跳回应");
                        }
                    }
                    
                    await Task.Delay(HEARTBEAT_INTERVAL, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logService.Error($"心跳异常: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"心跳循环异常: {ex.Message}");
        }
        finally
        {
            if (_isConnected && client.State != WebSocketState.Open)
            {
                _logService.Info("心跳中断，触发重连");
                await ReconnectAsync(roomId);
            }
        }
    }

    private async Task SendJoinRoomMessageAsync(ClientWebSocket client, long roomId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            roomid = roomId,
            protover = 2,
            platform = "web",
            clientver = "1.8.2",
            type = 2
        });

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var headerLength = 16;
        var totalLength = headerLength + payloadBytes.Length;

        var buffer = new byte[totalLength];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), totalLength);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), 7);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(12, 4), 1);

        payloadBytes.CopyTo(buffer, headerLength);

        await client.SendAsync(buffer, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket client, long roomId, CancellationToken token)
    {
        var buffer = WebSocket.CreateClientBuffer(16384, 16384);  // 增大缓冲区
        using var ms = new MemoryStream();
        
        try
        {
            _logService.Debug($"开始接收房间 {roomId} 的消息");
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                
                try
                {
                    do
                    {
                        result = await client.ReceiveAsync(buffer, token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logService.Debug($"房间 {roomId} 收到关闭消息");
                            return;
                        }
                        
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        ProcessMessage(ms.ToArray(), roomId);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _logService.Debug($"房间 {roomId} 接收循环被取消");
                    break;
                }
                catch (WebSocketException ex)
                {
                    _logService.Error($"房间 {roomId} WebSocket错误: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"房间 {roomId} 接收循环异常: {ex.Message}");
        }
        finally
        {
            _logService.Debug($"结束接收房间 {roomId} 的消息");
        }
    }

    private async Task ReceiveMessagesAsync(ClientWebSocket client, long roomId)
    {
        var buffer = new byte[16384];
        try
        {
            while (client.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    _logService.Error($"WebSocket接收错误: {ex.Message}", ex);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    _logService.Warning("WebSocket已被释放");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logService.Warning($"房间 {roomId} WebSocket连接被关闭");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    ProcessMessage(buffer.AsSpan(0, result.Count), roomId);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"消息接收循环错误: {ex.Message}", ex);
        }
        finally
        {
            // 只有在非主动关闭的情况下才重连
            if (_isConnected)
            {
                _logService.Info($"正在重新连接房间 {roomId}...");
                await ReconnectAsync(roomId);
            }
        }
    }

    private async Task<bool> ReconnectAsync(long roomId)
    {
        if (!_reconnectAttempts.ContainsKey(roomId))
        {
            _reconnectAttempts[roomId] = 0;
        }

        while (_reconnectAttempts[roomId] < MAX_RECONNECT_ATTEMPTS)
        {
            try
            {
                _reconnectAttempts[roomId]++;
                _logService.Info($"尝试重新连接房间 {roomId}，第 {_reconnectAttempts[roomId]} 次重试");
                
                // 确保旧的连接已经完全清理
                await CleanupConnectionAsync(roomId);
                
                // 等待一段时间再重连
                await Task.Delay(3000 * _reconnectAttempts[roomId]);
                
                // 创建新的连接
                await ConnectToRoomAsync(roomId);
                
                // 重连成功，重置重试次数
                _reconnectAttempts[roomId] = 0;
                return true;
            }
            catch (Exception ex)
            {
                _logService.Error($"第 {_reconnectAttempts[roomId]} 次重连失败: {ex.Message}");
                
                if (_reconnectAttempts[roomId] >= MAX_RECONNECT_ATTEMPTS)
                {
                    _logService.Error($"房间 {roomId} 重试次数超过上限 ({MAX_RECONNECT_ATTEMPTS})，停止重试");
                    return false;
                }
            }
        }

        return false;
    }

    private async Task CleanupConnectionAsync(long roomId)
    {
        try
        {
            // 取消心跳任务
            if (_heartbeatTokens.TryGetValue(roomId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _heartbeatTokens.Remove(roomId);
            }

            // 清理WebSocket连接
            if (_roomClients.TryGetValue(roomId, out var client))
            {
                if (client.State == WebSocketState.Open)
                {
                    try
                    {
                        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
                    }
                    catch { /* 忽略关闭时的错误 */ }
                }
                
                client.Dispose();
                _roomClients.Remove(roomId);
            }

            // 等待一小段时间确保资源完全释放
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            _logService.Warning($"清理连接时出现错误: {ex.Message}");
            // 继续执行，不抛出异常
        }
    }

    private string CleanMessageContent(ReadOnlySpan<byte> data)
    {
        // 首先进行基本的字符清理
        var cleanedData = new byte[data.Length];
        int cleanedLength = 0;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (b >= 32 && b <= 126 || b >= 128)
            {
                cleanedData[cleanedLength++] = b;
            }
        }

        // 获取清理后的字符串
        var cleanedString = Encoding.UTF8.GetString(cleanedData, 0, cleanedLength)
            .Trim()
            .Replace("\0", "");

        // 找到第一个 '{' 和最后一个 '}'
        int jsonStart = cleanedString.IndexOf('{');
        int jsonEnd = cleanedString.LastIndexOf('}');
        
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            return cleanedString[jsonStart..(jsonEnd + 1)];
        }

        _logService.Warning($"无效的JSON格式: {cleanedString}");
        return string.Empty;
    }

    private void ProcessMessage(ReadOnlySpan<byte> data, long roomId)
    {
        // 提前检查房间ID
        if (roomId != _currentRoomId)
        {
            _logService.Debug($"忽略非当前房间({roomId})的消息，当前房间: {_currentRoomId}");
            return;
        }

        if (data.Length < 16) // 包长度至少16字节
        {
            _logService.Warning($"收到的消息长度过短: {data.Length} bytes");
            return;
        }

        try
        {
            var packageLength = BinaryPrimitives.ReadInt32BigEndian(data.Slice(0, 4));
            var headerLength = BinaryPrimitives.ReadInt16BigEndian(data.Slice(4, 2));
            var version = BinaryPrimitives.ReadInt16BigEndian(data.Slice(6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(data.Slice(8, 4));

            _logService.Debug($"收到消息: 长度={packageLength}, 操作类型={operation}, 版本={version}");

            switch (operation)
            {
                case 3: // 心跳回应
                    var online = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16));
                    _logService.Debug($"房间 {roomId} 在线人数: {online}");
                    break;

                case 5: // 通知消息
                    var body = data.Slice(headerLength);
                    if (version == 2 || version == 3) // 增加对版本3的支持
                    {
                        body = DecompressBody(body.ToArray());
                        // 处理多条打包消息的情况
                        int offset = 0;
                        while (offset < body.Length)
                        {
                            if (offset + 4 > body.Length) break;
                            
                            var subPackageLength = BinaryPrimitives.ReadInt32BigEndian(body.Slice(offset, 4));
                            if (subPackageLength <= 0 || offset + subPackageLength > body.Length) break;
                            
                            var subBody = body.Slice(offset + headerLength, subPackageLength - headerLength);
                            var jsonData = CleanMessageContent(subBody);
                            
                            if (!string.IsNullOrEmpty(jsonData))
                            {
                                try
                                {
                                    var messageObj = JsonSerializer.Deserialize<JsonElement>(jsonData);
                                    HandleDanmakuMessage(messageObj, roomId);
                                }
                                catch (Exception ex)
                                {
                                    _logService.Error($"解析子消息失败: {ex.Message}");
                                }
                            }
                            
                            offset += subPackageLength;
                        }
                    }
                    else
                    {
                        var jsonData = CleanMessageContent(body);
                        _logService.Debug($"收到通知消息: {jsonData}");
                        
                        try 
                        {
                            var messageObj = JsonSerializer.Deserialize<JsonElement>(jsonData);
                            HandleDanmakuMessage(messageObj, roomId);
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"解析消息失败: {ex.Message}");
                        }
                    }
                    break;

                case 8: // 进房回应
                    _logService.Info($"成功进入房间 {roomId}");
                    break;

                default:
                    _logService.Debug($"未处理的操作类型: {operation}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logService.Error("处理消息失败", ex);
        }
    }

    private ReadOnlySpan<byte> DecompressBody(byte[] compressedData)
    {
        try
        {
            using var compressedStream = new MemoryStream(compressedData);
            using var decompressStream = new MemoryStream();
            using var deflateStream = new BrotliStream(compressedStream, CompressionMode.Decompress);
            deflateStream.CopyTo(decompressStream);
            return new ReadOnlySpan<byte>(decompressStream.ToArray());
        }
        catch
        {
            _logService.Warning("消息解压失败，尝试不解压处理");
            return compressedData;
        }
    }

    private string ParseMessageContent(byte[] data)
    {
        try
        {
            if (data.Length == 0) return string.Empty; // 修复: empty -> Empty

            // 尝试解压
            using var ms = new MemoryStream(data);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate);
            return reader.ReadToEnd();
        }
        catch
        {
            // 如果解压失败，假设是未压缩的JSON
            return Encoding.UTF8.GetString(data);
        }
    }

    private void HandleDanmakuMessage(JsonElement message, long roomId)
    {
        try
        {
            if (roomId != _currentRoomId)
            {
                _logService.Debug($"忽略非当前房间的消息: {roomId} != {_currentRoomId}");
                return;
            }

            if (message.TryGetProperty("cmd", out var cmd))
            {
                var cmdStr = cmd.GetString();
                
                if (!cmdStr?.Contains("HEARTBEAT") ?? false)
                {
                    _logService.Debug($"收到命令: {cmdStr}");
                }

                switch (cmdStr)
                {
                    case "DANMU_MSG":
                        try
                        {
                            var info = message.GetProperty("info");
                            var content = info[1].GetString() ?? string.Empty;
                            var userName = info[2][1].GetString() ?? "未知用户";

                            // 修复身份判断逻辑，使用 GetRawText() 处理数字类型
                            var isAdmin = info[2][2].GetRawText() == "1";
                            var isVip = info[2][3].GetRawText() == "1";
                            
                            // 添加更多用户身份标识
                            var displayName = userName;
                            
                            // 检查是否有粉丝牌
                            if (info[3].GetArrayLength() > 0)
                            {
                                try
                                {
                                    var medalName = info[3][1].GetString();
                                    var medalLevel = info[3][0].GetInt32();
                                    if (!string.IsNullOrEmpty(medalName))
                                    {
                                        displayName = $"[{medalName}{medalLevel}] {displayName}";
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logService.Debug($"解析粉丝牌失败: {ex.Message}");
                                }
                            }

                            // 添加身份标识
                            if (isAdmin) displayName = "【管理员】" + displayName;
                            if (isVip) displayName = "【VIP】" + displayName;

                            var args = new DanmakuEventArgs(displayName, content);
                            OnDanmakuReceived?.Invoke(this, args);
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理弹幕消息失败: {ex.Message}", ex);
                            // 添加更详细的错误信息
                            _logService.Debug($"弹幕消息内容: {message}");
                        }
                        break;

                    case "SEND_GIFT":
                        try
                        {
                            var data = message.GetProperty("data");
                            var userName = data.GetProperty("uname").GetString() ?? "未知用户";
                            var giftName = data.GetProperty("giftName").GetString() ?? "未知礼物";
                            var num = data.GetProperty("num").GetInt32();
                            
                            var content = $"赠送 {giftName} x{num}";
                            
                            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs(userName, content, true));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理礼物消息失败: {ex.Message}", ex);
                        }
                        break;

                    case "SUPER_CHAT_MESSAGE":
                        try
                        {
                            var data = message.GetProperty("data");
                            var userName = data.GetProperty("user_info").GetProperty("uname").GetString() ?? "未知用户";
                            var content = data.GetProperty("message").GetString() ?? "";
                            var price = data.GetProperty("price").GetInt32();
                            var color = data.GetProperty("message_font_color").GetString();
                            
                            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs(
                                userName, content, 
                                isGift: false, 
                                isSuperChat: true,
                                superChatPrice: price,
                                color: color));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理SC消息失败: {ex.Message}");
                        }
                        break;

                    case "GUARD_BUY":
                        try 
                        {
                            var data = message.GetProperty("data");
                            var userName = data.GetProperty("username").GetString() ?? "未知用户";
                            var guardLevel = data.GetProperty("guard_level").GetInt32();
                            var guardName = guardLevel switch {
                                3 => "舰长",
                                2 => "提督",
                                1 => "总督",
                                _ => "船员"
                            };
                            var num = data.GetProperty("num").GetInt32();
                            var content = $"购买了 {num} 个月的{guardName}";
                            
                            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs(
                                userName, content,
                                isGuardBuy: true,
                                color: "#FFB03C"));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理上舰消息失败: {ex.Message}");
                        }
                        break;

                    case "WARNING":
                        try
                        {
                            var msg = message.GetProperty("msg").GetString() ?? "直播警告";
                            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs(
                                "系统通知", msg,
                                isWarning: true,
                                color: "#FF4444"));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理警告消息失败: {ex.Message}");
                        }
                        break;

                    case "CUT_OFF":
                        try
                        {
                            var msg = message.GetProperty("msg").GetString() ?? "直播被切断";
                            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs(
                                "系统通知", msg,
                                isWarning: true,
                                color: "#FF0000"));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理切断消息失败: {ex.Message}");
                        }
                        break;

                    case "ONLINE_RANK_COUNT":
                        try
                        {
                            var count = message.GetProperty("data")
                                .GetProperty("count").GetInt32();
                            OnDanmakuReceived?.Invoke(this, 
                                new OnlineRankCountEventArgs(count));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理在线人数消息失败: {ex.Message}");
                        }
                        break;

                    case "WATCHED_CHANGE":
                        try
                        {
                            var num = message.GetProperty("data")
                                .GetProperty("num").GetInt32();
                            OnDanmakuReceived?.Invoke(this, 
                                new WatchedChangeEventArgs(num));
                        }
                        catch (Exception ex)
                        {
                            _logService.Error($"处理观看过人数消息失败: {ex.Message}");
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"处理房间 {roomId} 的消息失败: {ex.Message}", ex);
        }
    }

    private IEnumerable<string> SplitJsonMessages(string jsonData)
    {
        int currentIndex = 0;
        while (currentIndex < jsonData.Length)
        {
            // 找到下一个JSON对象的开始
            int start = jsonData.IndexOf('{', currentIndex);
            if (start == -1) break;

            // 跟踪大括号的嵌套
            int braceCount = 1;
            int end = start + 1;
            bool inString = false;

            // 寻找匹配的结束大括号
            while (end < jsonData.Length && braceCount > 0)
            {
                char c = jsonData[end];
                if (c == '"' && (end == 0 || jsonData[end - 1] != '\\'))
                {
                    inString = !inString;
                }
                else if (!inString)
                {
                    if (c == '{') braceCount++;
                    else if (c == '}') braceCount--;
                }
                end++;
            }

            if (braceCount == 0)
            {
                // 提取完整的JSON对象
                string json = jsonData[start..end];
                yield return json;
            }

            currentIndex = end;
        }
    }

    public async Task DisconnectFromRoomAsync(long roomId)
    {
        // 使用专门的断开连接锁
        await _disconnectLock.WaitAsync();
        try
        {
            _logService.Info($"正在断开房间 {roomId} 的连接...");
            
            // 取消接收循环的CancellationToken
            if (_receiveTokens.TryGetValue(roomId, out var receiveCts))
            {
                receiveCts.Cancel();
                _receiveTokens.Remove(roomId);
                receiveCts.Dispose();
            }

            // 取消心跳任务
            if (_heartbeatTokens.TryGetValue(roomId, out var heartbeatCts))
            {
                heartbeatCts.Cancel();
                _heartbeatTokens.Remove(roomId);
                heartbeatCts.Dispose();
            }

            // 关闭WebSocket连接
            if (_roomClients.TryGetValue(roomId, out var client))
            {   
                if (client.State == WebSocketState.Open)
                {
                    try
                    {
                        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logService.Warning($"关闭WebSocket时出错: {ex.Message}");
                    }
                }
                client.Dispose();
                _roomClients.Remove(roomId);
            }

            // 如果断开的是当前房间，更新状态
            if (roomId == _currentRoomId)
            {
                _currentRoomId = 0;
                _isConnected = false;
            }

            _logService.Info($"已断开房间 {roomId} 的连接");
        }
        finally
        {
            _disconnectLock.Release();
        }
    }

    public async Task SendDanmakuAsync(long roomId, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            _logService.Warning("弹幕内容不能为空");
            return;
        }

        try
        {
            await _biliApiService.SendDanmakuAsync(roomId, content);
            _logService.Info($"发送弹幕到房间 {roomId}: {content}");
            
            // 发送成功后触发本地弹幕事件
            OnDanmakuReceived?.Invoke(this, new DanmakuEventArgs("我", content));
        }
        catch (Exception ex)
        {
            _logService.Error($"发送弹幕失败: {ex.Message}", ex);
            throw;
        }
    }

    public void Dispose()
    {
        _connectionLock.Dispose();
        _disconnectLock.Dispose();
        foreach (var roomId in _roomClients.Keys.ToList())
        {
            CleanupConnectionAsync(roomId).Wait();
        }
        
        _heartbeatTokens.Clear();
        _reconnectAttempts.Clear();
        _roomClients.Clear();
    }
}