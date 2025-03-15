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

    private const string HttpHeaderAccept = "*/*";
    private const string HttpHeaderOrigin = "https://live.bilibili.com";
    private const string HttpHeaderReferer = "https://live.bilibili.com/";
    private const string HttpHeaderUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36";
    private readonly int NetworkTimeout = 10000; // 10秒超时

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
                ":network-caching=1000",    // 网络缓存，增加到1秒以减少卡顿
                ":live-caching=1000",       // 直播缓存
                ":file-caching=1000",
                ":sout-mux-caching=1000",
                ":http-reconnect",          // 自动重连
                ":http-continuous",         // 连续播放
                ":aout=mmdevice",          // 音频输出设备
                ":audio-time-stretch",      // 音频时间拉伸
                ":clock-synchro=0",        // 时钟同步
                ":network-synchronisation", // 网络同步
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

            // 添加事件处理
            player.EncounteredError += (s, e) => 
            {
                _logService.Error($"播放器错误发生");
            };

            player.Buffering += (s, e) => 
            {
                _logService.Debug($"缓冲中: {e.Cache}%");
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
        try
        {
            _logService.Info($"正在获取房间 {roomId} 的直播流地址...");
            var streamUrl = await _biliApiService.GetLiveStreamUrlAsync(roomId);
            _logService.Info($"获取到直播流地址: {streamUrl}");

            if (await TryInitializePlayerAsync(roomId, streamUrl))
            {
                _logService.Info($"成功连接到房间 {roomId}");
                return;
            }

            throw new Exception("无法初始化播放器");
        }
        catch (Exception ex)
        {
            _logService.Error($"连接房间失败: {ex.Message}");
            throw;
        }
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