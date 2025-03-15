using NAudio.Wave;
using System.Net.Http;
using System.IO;
using LibVLCSharp.Shared;  // 添加 LibVLC 引用

namespace BiliVoxLive;

public interface ILiveStreamService
{
    event EventHandler<AudioDataEventArgs>? OnAudioDataReceived;
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }
    Task HandleRoomAsync(long roomId, bool isActive);  // 添加新方法
}

public class LiveStreamService : ILiveStreamService
{
    private readonly Dictionary<long, WaveOutEvent> _players = new();
    private readonly Dictionary<long, IWaveProvider> _waveProviders = new();
    private readonly BiliApiService _biliApiService;
    private readonly ILogService _logService;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<long, CancellationTokenSource> _cancellationTokens = new();
    private readonly Dictionary<long, float> _roomVolumes = new();
    private readonly Dictionary<long, bool> _roomMuteStates = new();
    private readonly Dictionary<long, MediaPlayer> _mediaPlayers = new();
    private LibVLC _libVLC;
    private MediaPlayer? _currentPlayer;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);  // 确保连接操作的线程安全
    private readonly Dictionary<long, TaskCompletionSource<bool>> _connectionTasks = new();
    private volatile bool _isDisposing = false;
    private long _currentRoomId;  // 添加当前房间ID跟踪
    private CancellationTokenSource? _currentConnectionCts;
    private readonly Dictionary<long, DateTime> _lastFrameTimestamps = new();
    private readonly TimeSpan _freezeThreshold = TimeSpan.FromSeconds(10); // 10秒没有新帧认为是冻结
    private int _currentCdnIndex = 0;
    private readonly string[] _cdnOptions = new[] { "ws", "tct", "ali", "hw", "cos", "ks", "bd" };

    private const string HttpHeaderAccept = "*/*";
    private const string HttpHeaderOrigin = "https://live.bilibili.com";
    private const string HttpHeaderReferer = "https://live.bilibili.com/";
    private const string HttpHeaderUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";
    private readonly int NetworkTimeout = 20000; // 增加到20秒超时

    public bool IsRunning { get; private set; }
    public event EventHandler<AudioDataEventArgs>? OnAudioDataReceived;

    public LiveStreamService(
        BiliApiService biliApiService, 
        ILogService logService,
        LibVLC libVLC)  // 注入 LibVLC 实例
    {
        _biliApiService = biliApiService;
        _logService = logService;
        _libVLC = libVLC;
        _httpClient = new HttpClient();
        
        _logService.Info("LiveStreamService 初始化完成");
    }

    public MediaPlayer? GetCurrentPlayer() => _currentPlayer;

    public async Task HandleRoomAsync(long roomId, bool isActive)
    {
        if (_isDisposing) return;

        try
        {
            await _connectionLock.WaitAsync();  // 获取连接锁
            try
            {
                // 取消当前正在进行的连接
                if (_currentConnectionCts != null)
                {
                    await DisconnectAllAsync();  // 确保先断开所有连接
                    _currentConnectionCts.Cancel();
                    _currentConnectionCts.Dispose();
                    _currentConnectionCts = null;
                }

                if (isActive)
                {
                    // 创建新的取消令牌
                    _currentConnectionCts = new CancellationTokenSource();
                    _currentRoomId = roomId;

                    // 创建连接任务
                    var connectionTask = new TaskCompletionSource<bool>();
                    _connectionTasks[roomId] = connectionTask;

                    try
                    {
                        await ConnectToRoomAsync(roomId);
                        connectionTask.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        connectionTask.TrySetException(ex);
                        throw;
                    }
                }
                else
                {
                    if (_currentRoomId == roomId)
                    {
                        _currentRoomId = 0;
                    }
                    // 确保清理指定房间的连接
                    await DisconnectFromRoomAsync(roomId);
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _logService.Info($"房间 {roomId} 的连接被取消");
            await CleanupRoomResourcesAsync(roomId);
        }
        catch (Exception ex)
        {
            _logService.Error($"处理房间 {roomId} 时出错: {ex.Message}");
            await CleanupRoomResourcesAsync(roomId);
            throw;
        }
    }

    private async Task DisconnectAllAsync()
    {
        var roomIds = _mediaPlayers.Keys.ToList();
        foreach (var roomId in roomIds)
        {
            await DisconnectFromRoomAsync(roomId);
        }
    }

    private async Task DisconnectFromRoomAsync(long roomId)
    {
        try
        {
            if (_mediaPlayers.TryGetValue(roomId, out var player))
            {
                player.Stop();
                player.Dispose();
                _mediaPlayers.Remove(roomId);

                if (_currentPlayer == player)
                {
                    _currentPlayer = null;
                }
            }

            // 清理连接任务
            if (_connectionTasks.TryGetValue(roomId, out var task))
            {
                task.TrySetCanceled();
                _connectionTasks.Remove(roomId);
            }

            await CleanupRoomResourcesAsync(roomId);
        }
        catch (Exception ex)
        {
            _logService.Warning($"断开房间 {roomId} 时出错: {ex.Message}");
        }
    }

    private async Task CleanupRoomResourcesAsync(long roomId)
    {
        // 清理房间相关的所有资源
        _roomVolumes.Remove(roomId);
        _roomMuteStates.Remove(roomId);
        _cancellationTokens.Remove(roomId);
        _lastFrameTimestamps.Remove(roomId); // 清理帧时间戳
        
        if (_currentRoomId == roomId)
        {
            _currentRoomId = 0;
        }

        await Task.CompletedTask;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler);
        var headers = client.DefaultRequestHeaders;
        headers.Add("Accept", HttpHeaderAccept);
        headers.Add("Origin", HttpHeaderOrigin);
        headers.Add("Referer", HttpHeaderReferer);
        headers.Add("User-Agent", HttpHeaderUserAgent);
        client.Timeout = TimeSpan.FromMilliseconds(NetworkTimeout);
        
        return client;
    }

    private Task<bool> TryInitializePlayerAsync(long roomId, string streamUrl)
    {
        var player = null as MediaPlayer;
        try
        {
            // 移除对当前播放状态的检查，允许重新连接
            var cancellationToken = _currentConnectionCts?.Token ?? CancellationToken.None;
            
            _logService.Debug($"正在创建Media实例: {streamUrl}");
            var media = new Media(_libVLC, new Uri(streamUrl),
                ":network-caching=3000",    // 增加网络缓存到3秒，减少卡顿
                ":live-caching=3000",       // 增加直播缓存到3秒
                ":file-caching=3000",       // 增加文件缓存
                ":sout-mux-caching=3000",   // 增加复用缓存
                ":http-reconnect",          // 自动重连
                ":http-continuous",         // 连续播放
                ":aout=mmdevice",          // 音频输出设备
                ":audio-time-stretch",      // 音频时间拉伸
                ":clock-synchro=0",        // 时钟同步
                ":network-synchronisation", // 网络同步
                ":codec=any",              // 使用任何可用解码器
                ":avcodec-hw=any",         // 允许任何硬件解码
                ":avcodec-threads=0",      // 自动设置解码线程数
                ":avcodec-skip-frame=0",   // 不跳帧
                ":avcodec-skip-idct=0",    // 不跳过IDCT
                $":http-referrer={HttpHeaderReferer}",
                $":http-user-agent={HttpHeaderUserAgent}",
                ":quiet",
                ":no-video-title-show",    // 不显示视频标题
                ":adaptive-maxwidth=1920",  // 最大宽度限制
                ":adaptive-maxheight=1080", // 最大高度限制
                ":video-filter=postproc",   // 视频后处理滤镜
                ":postproc-q=6",
                ":aspect-ratio=16:9"
            );

            player = new MediaPlayer(media) 
            { 
                EnableHardwareDecoding = true,  // 启用硬件解码
                EnableMouseInput = false,       // 禁用鼠标输入
                EnableKeyInput = false,         // 禁用键盘输入
                Scale = 0                       // 自动缩放
            };

            // 初始化帧时间戳
            _lastFrameTimestamps[roomId] = DateTime.Now;

            // 添加更全面的事件处理
            player.EncounteredError += (s, e) => 
            {
                _logService.Error($"播放器错误发生，尝试重新连接...");
                // 尝试自动重连
                Task.Run(async () => {
                    try {
                        await Task.Delay(3000); // 等待3秒后重连
                        if (!_isDisposing && _currentRoomId == roomId)
                        {
                            _logService.Info("尝试重新连接...");
                            await ConnectToRoomAsync(roomId);
                        }
                    }
                    catch (Exception ex) {
                        _logService.Error($"重连失败: {ex.Message}");
                    }
                });
            };

            // 增加帧接收事件监听
            player.TimeChanged += (s, e) => {
                // 更新最后一帧时间戳
                _lastFrameTimestamps[roomId] = DateTime.Now;
            };

            // 添加特殊视频事件处理
            player.Vout += (s, e) => {
                // 视频输出创建事件，更新时间戳
                _lastFrameTimestamps[roomId] = DateTime.Now;
                _logService.Debug($"视频输出已创建，轨道数: {e.Count}");
            };

            player.PositionChanged += (s, e) => {
                // 播放位置变化事件，表示有进度
                _lastFrameTimestamps[roomId] = DateTime.Now;
            };

            // 增加更多的播放器事件监听
            player.EndReached += (s, e) => {
                _logService.Debug("播放结束，尝试重新开始");
                player.Stop();
                player.Play();
            };

            player.Opening += (s, e) => {
                _logService.Debug("正在打开媒体...");
            };

            player.Playing += (s, e) => {
                _logService.Debug("开始播放");
            };

            player.Stopped += (s, e) => {
                _logService.Debug("播放停止");
            };

            player.Paused += (s, e) => {
                _logService.Debug("播放暂停");
            };

            player.Buffering += (s, e) => 
            {
                _logService.Debug($"缓冲中: {e.Cache}%");
                // 如果缓冲超过90%但播放还未开始，尝试手动开始播放
                if (e.Cache > 90 && !player.IsPlaying)
                {
                    player.Play();
                }
            };
            
            // 确保新播放器被记录
            _mediaPlayers[roomId] = player;
            _currentPlayer = player;
            
            _logService.Info("播放器已创建，等待视频组件准备就绪");
            return Task.FromResult(true);
        }
        catch (OperationCanceledException)
        {
            _logService.Info($"房间 {roomId} 的播放器初始化被取消");
            
            if (player != null)
            {
                try 
                {
                    player.Stop();
                    player.Dispose();
                }
                catch { }
                
                _mediaPlayers.Remove(roomId);
                if (_currentPlayer == player)
                {
                    _currentPlayer = null;
                }
            }
            
            throw;
        }
        catch (Exception ex)
        {
            _logService.Error($"初始化播放器出错: {ex.Message}");
            
            if (player != null)
            {
                try 
                {
                    player.Stop();
                    player.Dispose();
                }
                catch { }
                
                _mediaPlayers.Remove(roomId);
                if (_currentPlayer == player)
                {
                    _currentPlayer = null;
                }
            }
            
            return Task.FromResult(false);
        }
    }

    public async Task ConnectToRoomAsync(long roomId)
    {
        int retryCount = 0;
        const int maxRetries = 3;
        
        while (retryCount < maxRetries)
        {
            try
            {
                // 每次重试时切换CDN
                string preferredCdn = _cdnOptions[_currentCdnIndex % _cdnOptions.Length];
                _currentCdnIndex++;
                
                _logService.Info($"正在获取房间 {roomId} 的直播流地址 (使用CDN: {preferredCdn})...");
                var streamUrl = await _biliApiService.GetLiveStreamUrlAsync(roomId, preferredCdn);
                _logService.Info($"获取到直播流地址: {streamUrl}");

                if (await TryInitializePlayerAsync(roomId, streamUrl))
                {
                    _logService.Info($"成功连接到房间 {roomId} (使用CDN: {preferredCdn})");
                    // 成功后设置当前房间ID
                    _currentRoomId = roomId;
                    
                    // 启动网络监控
                    var cts = new CancellationTokenSource();
                    _cancellationTokens[roomId] = cts;
                    
                    // 在后台启动网络监控
                    _ = Task.Run(() => MonitorNetworkAndReconnectAsync(roomId, cts.Token));
                    
                    return;
                }

                // 如果初始化播放器失败，但没有抛出异常，那么增加重试次数
                retryCount++;
                _logService.Warning($"初始化播放器失败，尝试重试 ({retryCount}/{maxRetries})");
                
                // 添加延迟，避免立即重试
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                retryCount++;
                _logService.Error($"连接房间失败 ({retryCount}/{maxRetries}): {ex.Message}");
                
                // 如果是最后一次尝试，则抛出异常
                if (retryCount >= maxRetries)
                {
                    throw;
                }
                
                // 添加延迟，避免立即重试
                await Task.Delay(2000);
            }
        }

        throw new Exception($"无法连接到房间 {roomId}，已重试 {maxRetries} 次");
    }

    public async Task StartAsync()
    {
        IsRunning = true;
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        
        // 取消当前连接
        if (_currentConnectionCts != null)
        {
            _currentConnectionCts.Cancel();
            _currentConnectionCts.Dispose();
            _currentConnectionCts = null;
        }
        
        // 停止所有房间的播放
        await DisconnectAllAsync();

        // 清理各种状态
        _currentRoomId = 0;
        _currentPlayer = null;
        _roomVolumes.Clear();
        _roomMuteStates.Clear();

        // 清理 LibVLC 资源（修改这部分）
        if (_libVLC != null)
        {
            _libVLC.Dispose();
            _libVLC = null!;
        }
    }

    public async Task SetVolumeAsync(long roomId, float volume)
    {
        try
        {
            if (_mediaPlayers.TryGetValue(roomId, out var player))
            {
                _roomVolumes[roomId] = volume;
                if (!_roomMuteStates.GetValueOrDefault(roomId))
                {
                    var newVolume = (int)(volume * 100);
                    await Task.Run(() => player.Volume = newVolume);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"设置音量失败: {ex.Message}");
        }
    }

    public async Task SetMuteAsync(long roomId, bool isMuted)
    {
        try
        {
            if (_mediaPlayers.TryGetValue(roomId, out var player))
            {
                _roomMuteStates[roomId] = isMuted;
                player.Volume = isMuted ? 0 : (int)(_roomVolumes.GetValueOrDefault(roomId, 1.0f) * 100);
                _logService.Debug($"设置房间 {roomId} {(isMuted ? "静音" : "取消静音")}");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"设置静音状态失败: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposing = true;
            
            // 取消所有正在进行的连接
            _currentConnectionCts?.Cancel();
            _currentConnectionCts?.Dispose();
            
            // 清理所有播放器
            foreach (var player in _mediaPlayers.Values)
            {
                try
                {
                    player.Stop();
                    player.Dispose();
                }
                catch (Exception ex)
                {
                    _logService.Error($"清理MediaPlayer时出错: {ex.Message}");
                }
            }
            
            _mediaPlayers.Clear();
            _currentPlayer = null;
            
            // 清理其他资源
            _connectionLock.Dispose();
            _libVLC?.Dispose();
            
            // 清理所有连接任务
            foreach (var task in _connectionTasks.Values)
            {
                task.TrySetCanceled();
            }
            _connectionTasks.Clear();
            _lastFrameTimestamps.Clear(); // 清理所有帧时间戳
            
            StopAsync().Wait();
        }
    }

    private async Task ProcessAudioStreamAsync(long roomId, Stream stream, CancellationToken ct)
    {
        try
        {
            var reader = new FlvStreamReader(stream, _logService);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var audioData = await reader.ReadAudioDataAsync();
                    if (audioData.Length > 0)
                    {
                        // 使用类的事件，确保参数顺序和类型正确
                        OnAudioDataReceived?.Invoke(this, new AudioDataEventArgs(audioData, roomId));
                        
                        // 这里添加处理音频数据的代码
                        _logService.Debug($"接收到音频数据: {audioData.Length} 字节");
                    }
                }
                catch (Exception ex)
                {
                    _logService.Error($"处理音频数据失败: {ex.Message}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"音频流处理错误: {ex.Message}", ex);
        }
    }

    // 添加网络连接状态检查方法
    private async Task<bool> IsNetworkAvailableAsync()
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync("https://api.bilibili.com/");
                return response.IsSuccessStatusCode;
            }
        }
        catch
        {
            return false;
        }
    }
    
    // 添加网络状态监控方法
    private async Task MonitorNetworkAndReconnectAsync(long roomId, CancellationToken cancellationToken)
    {
        // 网络监控间隔（秒）
        const int monitorInterval = 10; // 减少到10秒，更快发现问题
        const int maxConsecutiveRecoveries = 3; // 连续恢复尝试的最大次数
        int consecutiveRecoveries = 0;
        
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(monitorInterval * 1000, cancellationToken);
            
            // 如果当前没有活动的播放器或者当前房间不是监控的房间，则不执行任何操作
            if (_currentPlayer == null || _currentRoomId != roomId || !_lastFrameTimestamps.ContainsKey(roomId))
            {
                continue;
            }
            
            var now = DateTime.Now;
            var lastFrameTime = _lastFrameTimestamps[roomId];
            var timeSinceLastFrame = now - lastFrameTime;
            
            // 检查是否冻结 - 通过比较上次收到帧的时间
            bool isPlayerFrozen = timeSinceLastFrame > _freezeThreshold;
            bool isNetworkAvailable = await IsNetworkAvailableAsync();
            
            _logService.Debug($"播放状态检查: 播放中={_currentPlayer.IsPlaying}, 距上一帧={timeSinceLastFrame.TotalSeconds:F1}秒, 网络可用={isNetworkAvailable}");
            
            // 如果播放器冻结或不在播放状态，且网络可用，则尝试恢复
            if ((isPlayerFrozen || !_currentPlayer.IsPlaying) && isNetworkAvailable)
            {
                consecutiveRecoveries++;
                
                if (consecutiveRecoveries > maxConsecutiveRecoveries)
                {
                    _logService.Warning($"已连续尝试恢复 {consecutiveRecoveries} 次，等待下一个周期");
                    await Task.Delay(60000, cancellationToken); // 等待1分钟再尝试
                    consecutiveRecoveries = 0;
                    continue;
                }
                
                // 播放器冻结的情况，记录警告
                if (isPlayerFrozen)
                {
                    _logService.Warning($"检测到播放器冻结，已 {timeSinceLastFrame.TotalSeconds:F1} 秒没有接收到新帧");
                }
                
                _logService.Info($"尝试恢复播放 (第 {consecutiveRecoveries} 次)...");
                
                try
                {
                    // 先尝试简单重启播放
                    if (consecutiveRecoveries == 1)
                    {
                        _logService.Info("尝试简单重启播放...");
                        _currentPlayer.Stop();
                        await Task.Delay(1000, cancellationToken);
                        _currentPlayer.Play();
                        await Task.Delay(3000, cancellationToken); // 等待3秒看是否恢复
                        
                        // 更新时间戳，避免立即再次触发恢复
                        _lastFrameTimestamps[roomId] = DateTime.Now;
                    }
                    // 如果简单重启不成功，切换CDN并重新获取流
                    else
                    {
                        _logService.Info("简单重启失败，尝试切换CDN并重新连接...");
                        // 重置CDN索引使切换到不同的CDN
                        _currentCdnIndex++; 
                        await ConnectToRoomAsync(roomId);
                        consecutiveRecoveries = 0; // 重置计数器
                    }
                }
                catch (Exception ex)
                {
                    _logService.Error($"恢复播放失败: {ex.Message}");
                }
            }
            else
            {
                // 如果一切正常，重置连续恢复计数器
                consecutiveRecoveries = 0;
            }
        }
    }
}

// 添加 FLV 流解析器
public class FlvStreamReader
{
    private readonly Stream _stream;
    private bool _headerRead = false;
    private readonly ILogService _logService;

    public FlvStreamReader(Stream stream, ILogService logService)
    {
        _stream = stream;
        _logService = logService;
    }

    public async Task<byte[]> ReadAudioDataAsync()
    {
        try
        {
            if (!_headerRead)
            {
                await ReadFlvHeaderAsync();
                _headerRead = true;
            }

            // 读取 Tag Header
            var tagHeader = new byte[11];
            var read = await _stream.ReadAsync(tagHeader, 0, 11);
            if (read != 11) return Array.Empty<byte>();

            var tagType = tagHeader[0];
            var dataSize = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
            
            // 读取 Tag Data
            var data = new byte[dataSize];
            read = await _stream.ReadAsync(data, 0, dataSize);
            if (read != dataSize)
            {
                _logService.Warning($"FLV数据读取不完整: {read}/{dataSize}字节");
                return Array.Empty<byte>();
            }

            // 跳过 Previous Tag Size
            await _stream.ReadAsync(new byte[4], 0, 4);

            // 只处理音频数据 (0x08)
            if (tagType == 0x08)
            {
                var audioHeader = data[0];
                var soundFormat = (audioHeader >> 4) & 0x0F;  // 获取音频格式
                var soundRate = (audioHeader >> 2) & 0x03;    // 采样率
                var soundSize = (audioHeader >> 1) & 0x01;    // 采样大小
                var soundType = audioHeader & 0x01;           // 声道数

                _logService.Debug($"音频格式: {soundFormat}, 采样率: {soundRate}, " +
                                $"采样大小: {soundSize}, 声道数: {soundType}");

                // 返回音频负载数据（跳过音频Tag头）
                return data.Skip(1).ToArray();
            }

            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            _logService.Error($"FLV数据读取错误: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    private async Task ReadFlvHeaderAsync()
    {
        try
        {
            var header = new byte[9];
            var read = await _stream.ReadAsync(header, 0, 9);
            if (read != 9) throw new Exception("FLV头部读取失败");

            // 验证FLV头部
            if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
            {
                throw new Exception("无效的FLV格式");
            }

            _logService.Info("FLV头部验证成功");
        }
        catch (Exception ex)
        {
            _logService.Error($"FLV头部读取失败: {ex.Message}");
            throw;
        }
    }
}