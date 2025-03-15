namespace BiliVoxLive;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BiliVoxLive.Controls;
using BiliVoxLive.Models; 
using BiliVoxLive.Windows;
using LibVLCSharp.Shared;  // 添加 LibVLCSharp.Shared 的引用

// 添加新的房间选项数据模型（在类外部）
public class RoomOption
{
    public string Status { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public long RoomId { get; set; }
    public bool IsLiving { get; set; }
    public string DisplayText { get; set; } = string.Empty;  // 添加此属性
}

public partial class MainWindow : Window
{
    private readonly BiliApiService _biliApiService;
    private readonly LiveStreamService _liveStreamService;
    private readonly DanmakuService _danmakuService;
    private readonly LogService _logService;  // 添加日志服务
    private readonly CookieService _cookieService;  // 添加这行
    private long _currentRoomId;
    private bool _isMuted = false;
    private List<EmoticonPackage>? _emoticonPackages;
    private bool _shouldAutoScroll = true;  // 添加这个字段
    private EmoticonWindow? _emoticonWindow;
    private string _currentUsername = "未登录";
    private Queue<long> _searchAddedRoomIds = new Queue<long>();
    private const int MaxSearchAddedRooms = 3;
    private CancellationTokenSource? _currentConnectionCts;
    private bool _isEmoticonButtonProcessing = false;
    private bool _isMedalButtonProcessing = false;
    private GridLength _savedSearchColumnWidth = new GridLength(300); // 保存搜索栏宽度
    private bool _isVideoEnabled = false;
    private GridLength _savedVideoColumnWidth = new GridLength(300);

    public MainWindow(
        BiliApiService biliApiService,
        LiveStreamService liveStreamService,
        DanmakuService danmakuService,
        LogService logService,
        CookieService cookieService)  // 添加这个参数
    {
        InitializeComponent();
        
        _biliApiService = biliApiService ?? throw new ArgumentNullException(nameof(biliApiService));
        _liveStreamService = liveStreamService ?? throw new ArgumentNullException(nameof(liveStreamService));
        _danmakuService = danmakuService ?? throw new ArgumentNullException(nameof(danmakuService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _cookieService = cookieService ?? throw new ArgumentNullException(nameof(cookieService));  // 添加这行

        // 订阅日志事件
        _logService.OnLogReceived += OnLogReceived;
        
        // 窗口加载完成后初始化
        this.Loaded += MainWindow_Loaded;
        
        // 窗口关闭时清理
        this.Closed += MainWindow_Closed;

        InitializeSearchSideBar(); // 在构造函数中添加初始化

        // 设置 DataContext
        DataContext = new { LogService = logService };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logService.Error("初始化失败", ex);
            this.Close();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)  // 添加?标记
    {
        _logService.OnLogReceived -= OnLogReceived;
    }

    // 修改日志处理方法，移除UI更新
    private void OnLogReceived(object? sender, string message)
    {
        // 不再更新UI，只保留日志服务的处理
    }

    private void InitializeEvents()
    {
        _logService.Info("开始初始化事件处理程序...");

        // 取消所有事件订阅（确保只订阅一次）
        UnsubscribeEvents();
        
        // 重新订阅事件
        _danmakuService.OnDanmakuReceived += OnDanmakuReceived;
        SendButton.Click += OnSendDanmakuClick;
        DanmakuInput.KeyDown += DanmakuInput_KeyDown;
        
        _logService.Info("事件处理程序初始化完成");
    }

    private void UnsubscribeEvents()
    {
        _danmakuService.OnDanmakuReceived -= OnDanmakuReceived;
        SendButton.Click -= OnSendDanmakuClick;
        DanmakuInput.KeyDown -= DanmakuInput_KeyDown;  // 修正这里，之前错误地使用了 OnSendDanmakuClick
    }

    private void DanmakuInput_KeyDown(object sender, KeyEventArgs e)  // 新增
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(DanmakuInput.Text))
        {
            OnSendDanmakuClick(sender!, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async Task InitializeAsync()
    {
        try 
        {
            _logService.Info("正在初始化窗口...");
            
            // 确保订阅事件
            InitializeEvents();
            
            // 初始化弹幕悬浮层状态
            if (DanmakuOverlay != null)
            {
                DanmakuOverlay.Visibility = Visibility.Collapsed;
                DanmakuOverlay.IsEnabled = false;
            }

            // 检查必要的服务是否注入
            if (_biliApiService == null || _logService == null)
            {
                throw new InvalidOperationException("服务注入失败");
            }

            // 尝试自动登录
            var cookieFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bilibili_cookies.txt");
            if (System.IO.File.Exists(cookieFilePath))
            {
                try
                {
                    _logService.Info("尝试自动登录...");
                    var cookieContent = await System.IO.File.ReadAllTextAsync(cookieFilePath);
                    await _biliApiService.LoginAsync(cookieContent);
                    // 登录成功后立即更新用户信息
                    await UpdateUserInfoAsync();
                    await LoadRooms();
                    return;
                }
                catch (Exception ex)
                {
                    _logService.Error("自动登录失败", ex);
                    // 不再立即关闭窗口，而是提供重试选项
                    if (MessageBox.Show("自动登录失败，是否尝试手动登录？", "登录失败",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        await ShowLoginDialogAndInitialize();
                    }
                }
            }
            else
            {
                _logService.Info("未找到Cookie文件，需要手动登录");
                await ShowLoginDialogAndInitialize();
            }

            // 获取用户信息
            await UpdateUserInfoAsync();
        }
        catch (Exception ex)
        {
            _logService.Error("初始化失败", ex);
            MessageBox.Show($"初始化失败: {ex.Message}\n\n可以查看日志了解详细信息", 
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ShowLoginDialogAndInitialize()
    {
        try
        {
            _logService.Info("显示登录对话框...");
            var loginResult = await ShowLoginDialog();
            
            if (!loginResult)
            {
                _logService.Info("用户取消登录");
                this.Close();  // 如果用户取消登录，直接关闭主窗口
                return;
            }
            
            await LoadRooms();
            _logService.Info("初始化完成");
        }
        catch (Exception ex)
        {
            _logService.Error("登录失败", ex);
            MessageBox.Show("登录失败，请查看日志了解详细信息", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            this.Close();  // 登录失败时关闭窗口
        }
    }

    private async Task<bool> ShowLoginDialog()
    {
        var loginWindow = new LoginWindow(_logService, _biliApiService) 
        { 
            Owner = this 
        };
        
        loginWindow.ShowDialog();

        // 如果登录成功，更新用户信息并返回 true
        if (loginWindow.LoginSuccess)
        {
            _logService.Info("登录成功");
            await UpdateUserInfoAsync();
            return true;
        }

        return false;
    }

    private async Task LoadRooms()
    {
        try 
        {
            var rooms = await _biliApiService.GetFollowedLiveRoomsAsync();
            
            if (!rooms.Any())
            {
                MessageBox.Show(
                    "当前关注列表中没有正在直播的主播\n可以点击搜索按钮🔍查找其他直播间",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }
            
            // 保存搜索添加的房间
            var searchAddedRooms = RoomSelector.Items.OfType<RoomOption>()
                .Where(r => _searchAddedRoomIds.Contains(r.RoomId))
                .ToList();
            
            RoomSelector.Items.Clear();
            
            // 先添加搜索的房间
            foreach (var room in searchAddedRooms)
            {
                RoomSelector.Items.Add(room);
            }
            
            // 将直播中的房间排在前面
            var sortedRooms = rooms.OrderByDescending(r => r.IsLiving)
                                 .ThenBy(r => r.Title);
            
            // 添加关注的房间（排除已添加的搜索房间）
            foreach (var room in sortedRooms)
            {
                if (!_searchAddedRoomIds.Contains(room.RoomId))
                {
                    var option = new RoomOption
                    {
                        Status = room.IsLiving ? "🔴" : "⭕",
                        Title = room.Title,
                        HostName = room.HostName,
                        RoomId = room.RoomId,
                        IsLiving = room.IsLiving
                    };
                    RoomSelector.Items.Add(option);
                }
            }

            if (RoomSelector.Items.Count > 0)
            {
                _logService.Info("选择第一个房间...");
                RoomSelector.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"加载房间列表失败: {ex.Message}");
            MessageBox.Show(
                "获取直播间列表失败，请检查网络连接后重试。",
                "错误", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error
            );
        }
    }

    private async Task ConnectToRoom(long roomId, CancellationToken cancellationToken)
    {
        var progress = new ProgressWindow("正在连接", $"正在连接到房间 {roomId}...") { Owner = this };
        try
        {
            progress.Show();
            
            // 立即断开所有其他房间的连接
            var disconnectTasks = RoomSelector.Items
                .OfType<RoomOption>()
                .Where(r => r.RoomId != roomId)
                .Select(r => CleanupRoomConnection(r.RoomId));
            
            await Task.WhenAll(disconnectTasks);

            // 使用合并的取消令牌，同时响应超时和手动取消
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, 
                new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            var connectionTasks = new List<Task>
            {
                _danmakuService.ConnectToRoomAsync(roomId),
                _liveStreamService.HandleRoomAsync(roomId, true)
            };

            await Task.WhenAll(connectionTasks);

            // 如果视频已启用，设置视频播放器
            if (_liveStreamService.GetCurrentPlayer() is LibVLCSharp.Shared.MediaPlayer player)
            {
                // 始终绑定播放器到 VideoView，避免 LibVLC 创建新窗口
                VideoView.MediaPlayer = player;
                // 如果播放器未启动，则手动启动播放
                if (!player.IsPlaying)
                {
                    player.Play();
                }
                // 根据当前是否开启视频，设置 VideoView 的可见性
                VideoView.Visibility = _isVideoEnabled ? Visibility.Visible : Visibility.Hidden;
            }

            _logService.Info($"已成功连接到房间 {roomId}");
        }
        catch (OperationCanceledException)
        {
            _logService.Info($"房间 {roomId} 的连接被取消");
            // 确保清理被取消的连接
            await CleanupRoomConnection(roomId);
            throw;
        }
        catch (Exception ex)
        {
            _logService.Error($"连接房间 {roomId} 失败: {ex.Message}");
            throw;
        }
        finally
        {
            progress?.Close();
        }
    }

    // 添加新的清理方法
    private async Task CleanupRoomConnection(long roomId)
    {
        try
        {
            _logService.Info($"正在清理房间 {roomId} 的连接...");
            
            var cleanupTasks = new List<Task>();
            
            // 清理弹幕连接
            cleanupTasks.Add(_danmakuService.DisconnectFromRoomAsync(roomId));
            
            // 清理直播流
            cleanupTasks.Add(_liveStreamService.HandleRoomAsync(roomId, false));
            
            // 清理UI
            DanmakuList.Items.Clear();
            DanmakuOverlay?.ClearDanmaku();
            VideoView.MediaPlayer = null;
            
            await Task.WhenAll(cleanupTasks);
            _logService.Info($"已清理房间 {roomId} 的连接");
        }
        catch (Exception ex)
        {
            _logService.Error($"清理房间 {roomId} 连接时出错: {ex.Message}");
        }
    }

    // 修改SelectionChanged事件处理
    private async void RoomSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoomSelector.SelectedItem is not RoomOption selected)
        {
            return;
        }

        try
        {
            // 取消当前正在进行的连接
            if (_currentConnectionCts != null)
            {
                _currentConnectionCts.Cancel();
                _currentConnectionCts.Dispose();
            }
            _currentConnectionCts = new CancellationTokenSource();
            
            var newRoomId = selected.RoomId;
            var oldRoomId = _currentRoomId;
            
            if (newRoomId == oldRoomId) return;
            
            _logService.Info($"准备从房间 {oldRoomId} 切换到 {newRoomId}");

            // 立即清理UI和状态
            DanmakuList.Items.Clear();
            DanmakuOverlay?.ClearDanmaku();
            _emoticonPackages = null;
            _currentRoomId = newRoomId;

            // 强制断开所有连接并连接到新房间
            await _liveStreamService.HandleRoomAsync(oldRoomId, false);
            await _danmakuService.DisconnectFromRoomAsync(oldRoomId);
            
            // 连接新房间
            await ConnectToRoom(newRoomId, _currentConnectionCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logService.Info("房间切换被取消");
        }
        catch (Exception ex)
        {
            _logService.Error($"切换房间失败: {ex.Message}");
            MessageBox.Show($"切换房间失败: {ex.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 添加开关事件处理
    private void DanmakuOverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DanmakuOverlay != null)
        {
            if (DanmakuOverlayToggle.IsChecked == true)
            {
                DanmakuOverlay.Visibility = Visibility.Visible;
                DanmakuOverlay.IsEnabled = true;
                _logService.Debug("已启用弹幕悬浮层");
            }
            else
            {
                DanmakuOverlay.Visibility = Visibility.Collapsed;
                DanmakuOverlay.IsEnabled = false;
                DanmakuOverlay.ClearDanmaku();  // 清除现有弹幕
                _logService.Debug("已禁用弹幕悬浮层");
            }
        }
    }

    private void ShowError(string title, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowError(title, message));
            return;
        }

        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void OnSendDanmakuClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DanmakuInput.Text))
        {
            return;
        }

        try
        {
            if (RoomSelector.SelectedItem is RoomOption selected)  // 修改这里
            {
                var roomId = selected.RoomId;
                var content = DanmakuInput.Text.Trim();
                await _biliApiService.SendDanmakuAsync(roomId, content);
                DanmakuInput.Clear();
            }
        }
        catch (Exception ex)
        {
            _logService.Error("发送弹幕失败", ex);
            MessageBox.Show("发送弹幕失败: " + ex.Message, "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        try
        {
            _logService.Info("正在关闭应用程序...");

            // 取消所有事件订阅
            _logService.OnLogReceived -= OnLogReceived;
            _danmakuService.OnDanmakuReceived -= OnDanmakuReceived;
            _liveStreamService.OnAudioDataReceived -= OnAudioDataReceived;

            // 断开所有房间连接
            var disconnectTasks = RoomSelector.Items  // 修改这里
                .OfType<RoomOption>()
                .Select(room => _danmakuService.DisconnectFromRoomAsync(room.RoomId));

            await Task.WhenAll(disconnectTasks);
            _logService.Info("已断开所有连接");
        }
        catch (Exception ex)
        {
            _logService.Error("关闭过程中发生错误", ex);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _logService.OnLogReceived -= OnLogReceived;
            _danmakuService.OnDanmakuReceived -= OnDanmakuReceived;
            _liveStreamService.OnAudioDataReceived -= OnAudioDataReceived;
            
            // 断开所有房间连接
            foreach (RoomOption room in RoomSelector.Items)  // 修改这里
            {
                _danmakuService.DisconnectFromRoomAsync(room.RoomId).Wait();
            }
        }
        catch (Exception ex)
        {
            _logService.Error("窗口关闭时清理资源失败", ex);
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private void OnAudioDataReceived(object? sender, AudioDataEventArgs e)
    {
        // 移除音频可视化相关代码，因为我们暂时不实现它
    }

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        // 停止所有直播流
        await _liveStreamService.StopAsync();
    }

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_currentRoomId == 0) return;

        var volume = (float)(e.NewValue / 100.0); // 将百分比转换为0-1的值
        VolumeText.Text = $"{(int)e.NewValue}%";
        
        await _liveStreamService.SetVolumeAsync(_currentRoomId, volume);
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRoomId == 0) return;

        _isMuted = !_isMuted;
        MuteIcon.Text = _isMuted ? "🔇" : "🔊";
        VolumeSlider.IsEnabled = !_isMuted;
                await _liveStreamService.SetMuteAsync(_currentRoomId, _isMuted);    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && button.ContextMenu == null)
        {
            ContextMenu menu = new ContextMenu();
            var searchItem = new MenuItem { Header = "搜索直播间" };
            searchItem.Click += (s, args) => 
            {
                // 使用 SearchSideBarHost
                SearchSideBarHost.Visibility = 
                    SearchSideBarHost.Visibility == Visibility.Visible ? 
                    Visibility.Collapsed : Visibility.Hidden;
            };
            menu.Items.Add(searchItem);
            button.ContextMenu = menu;
            menu.IsOpen = true;
        }
        else
        {
            try
            {
                RefreshButton.IsEnabled = false;
                _logService.Info("正在刷新直播间列表...");
                
                var currentRoomId = _currentRoomId;
                await LoadRooms();

                // 如果有之前的房间，尝试重新选中
                if (currentRoomId != 0)
                {
                    var previousRoom = RoomSelector.Items.OfType<RoomOption>()
                        .FirstOrDefault(r => r.RoomId == currentRoomId);
                    if (previousRoom != null)
                    {
                        RoomSelector.SelectedItem = previousRoom;
                    }
                }

                _logService.Info($"已刷新直播间列表，共 {RoomSelector.Items.Count} 个房间");
            }
            catch (Exception ex)
            {
                _logService.Error("刷新直播间列表失败", ex);
                MessageBox.Show($"刷新失败: {ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    public class DanmakuMessage
    {
        public string Content { get; set; } = "";
        public bool IsSuperChat { get; set; }
        public bool IsGuardBuy { get; set; }
        public bool IsWarning { get; set; }
        public Brush TextColor { get; set; } = Brushes.Black;
        public Brush Background { get; set; } = Brushes.Transparent;
        public double FontSize { get; set; } = 14;
    }

    private void OnDanmakuReceived(object? sender, DanmakuEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnDanmakuReceived(sender, e)));
            return;
        }

        try
        {
            // 处理不同类型的消息
            if (e is OnlineRankCountEventArgs rankCount)
            {
                // 更新当前观看人数
                CurrentViewersText.Text = rankCount.Count.ToString("N0");
            }
            else if (e is WatchedChangeEventArgs watchedChange)
            {
                // 更新看过的人数
                TotalViewersText.Text = watchedChange.Count.ToString("N0");
            }
            else
            {
                // 构建显示消息
                var message = new DanmakuMessage
                {
                    Content = $"[{e.Timestamp:HH:mm:ss}] {e.UserName}: {e.Content}",
                    IsSuperChat = e.IsSuperChat,
                    IsGuardBuy = e.IsGuardBuy,
                    IsWarning = e.IsWarning
                };

                if (e.IsSuperChat)
                {
                    message.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3"));
                    message.TextColor = Brushes.White;
                    message.FontSize = 16;
                    if (e.SuperChatPrice.HasValue)
                    {
                        message.Content = $"[{e.Timestamp:HH:mm:ss}] 💰 {e.UserName} ({e.SuperChatPrice}元):\n{e.Content}";
                    }
                }
                else if (!string.IsNullOrEmpty(e.Color))
                {
                    message.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(e.Color));
                    message.TextColor = e.IsWarning ? Brushes.White : Brushes.Black;
                }

                DanmakuList.Items.Add(message);
                
                // 只有当应该自动滚动时才滚动到底部
                if (_shouldAutoScroll)
                {
                    var scrollViewer = GetScrollViewer(DanmakuList);
                    scrollViewer?.ScrollToBottom();
                }

                // 限制列表项数量
                while (DanmakuList.Items.Count > 200)
                {
                    DanmakuList.Items.RemoveAt(0);
                }

                // 只有在弹幕悬浮层启用时才显示悬浮弹幕
                if (DanmakuOverlay != null && 
                    DanmakuOverlay.IsEnabled && 
                    DanmakuOverlay.Visibility == Visibility.Visible)
                {
                    DanmakuOverlay.ShowDanmaku(message.Content, e.IsGift, e.IsSuperChat);
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"显示弹幕失败: {ex.Message}", ex);
        }
    }

    private async void EmoticonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEmoticonButtonProcessing) return;
        try
        {
            _isEmoticonButtonProcessing = true;
            
            if (_currentRoomId == 0)
            {
                MessageBox.Show("请先选择一个直播间", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 获取表情包
            _emoticonPackages = await _biliApiService.GetEmoticons(_currentRoomId);
            
            // 每次都创建新的表情窗口实例
            _emoticonWindow?.Dispose();  // 如果之前有实例，先释放
            _emoticonWindow = new EmoticonWindow(_emoticonPackages, _currentRoomId);
            _emoticonWindow.EmoticonSelected += (s, emoticon) =>
            {
                DanmakuInput.Text = emoticon.Text;
                OnSendDanmakuClick(this, new RoutedEventArgs());
                _emoticonWindow.IsOpen = false;
            };

            // 设置弹出窗口的定位目标和方式
            _emoticonWindow.PlacementTarget = sender as Button;
            _emoticonWindow.Placement = PlacementMode.Top;
            
            _emoticonWindow.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logService.Error($"显示表情窗口失败: {ex.Message}");
            MessageBox.Show("获取表情失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isEmoticonButtonProcessing = false;
        }
    }

    private async void MedalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMedalButtonProcessing) return;
        try
        {
            _isMedalButtonProcessing = true;
            
            var medals = await _biliApiService.GetFanMedalsAsync();
            if (!medals.Any())
            {
                MessageBox.Show("你还没有粉丝勋章哦", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var menu = new ContextMenu();
            var currentItem = new MenuItem { Header = "当前佩戴：" };
            currentItem.IsEnabled = false;
            menu.Items.Add(currentItem);
            
            var noneItem = new MenuItem { Header = "不佩戴" };
            noneItem.Click += async (s, args) => await WearMedal(0);
            menu.Items.Add(noneItem);
            
            menu.Items.Add(new Separator());

            foreach (var medal in medals)
            {
                var item = new MenuItem { Header = medal.ToString() };
                if (medal.IsWearing)
                {
                    currentItem.Header = $"当前佩戴：{medal}";
                }
                item.Click += async (s, args) => await WearMedal(medal.MedalId);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = sender as Button;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logService.Error("显示粉丝勋章列表失败", ex);
            MessageBox.Show("获取粉丝勋章失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isMedalButtonProcessing = false;
        }
    }

    private async Task WearMedal(int medalId)
    {
        try
        {
            await _biliApiService.WearMedalAsync(medalId);
            if (medalId == 0)
            {
                MessageBox.Show("已取消佩戴粉丝勋章", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("已切换粉丝勋章", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _logService.Error("切换粉丝勋章失败", ex);
            MessageBox.Show($"切换失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DanmakuList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ListView listView)
        {
            var scrollViewer = GetScrollViewer(listView);
            if (scrollViewer != null)
            {
                // 检查是否滚动到底部（允许1像素的误差）
                _shouldAutoScroll = Math.Abs(scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset) < 1;
            }
        }
    }

    private ScrollViewer? GetScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scrollViewer)
            return scrollViewer;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }
        
        return null;
    }

    private async Task UpdateUserInfoAsync()
    {
        try
        {
            var userInfo = await _biliApiService.GetUserInfoAsync();
            if (userInfo != null && userInfo.isLogin)
            {
                _currentUsername = userInfo.uname ?? "未登录";
                _logService.Info($"当前登录用户: {_currentUsername}");
            }
            else
            {
                _currentUsername = "未登录";
                _logService.Warning("用户未登录或登录状态无效");
            }
        }
        catch (Exception ex)
        {
            _logService.Warning($"获取用户信息失败: {ex.Message}");
            _currentUsername = "未登录";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        
        // 用户名项（不可点击）
        var usernameItem = new MenuItem 
        { 
            Header = _currentUsername,
            IsEnabled = false,
            Background = Brushes.LightGray
        };
        menu.Items.Add(usernameItem);
        
        menu.Items.Add(new Separator());

        // 退出登录项（总是在最后）
        var logoutItem = new MenuItem 
        { 
            Header = "退出登录",
            Foreground = Brushes.Red
        };
        logoutItem.Click += (s, args) => _ = Task.Run(async () => await LogoutAsync());
        menu.Items.Add(logoutItem);

        // 显示菜单
        menu.PlacementTarget = sender as Button;
        menu.IsOpen = true;
    }

    private async Task LogoutAsync()
    {
        var result = MessageBox.Show(
            "确定要退出登录吗？",
            "确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                _logService.Info("正在退出登录...");

                // 停止当前直播
                if (_currentRoomId != 0)
                {
                    await CleanupRoomConnection(_currentRoomId);
                }

                // 清空房间列表
                RoomSelector.Items.Clear();
                _currentRoomId = 0;

                // 清除Cookie文件
                await _cookieService.ClearCookiesAsync();

                // 重置用户名
                _currentUsername = "未登录";

                // 提示用户重新登录
                MessageBox.Show(
                    "已退出登录，请重新登录",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                // 显示登录窗口
                await ShowLoginDialogAndInitialize();
            }
            catch (Exception ex)
            {
                _logService.Error("退出登录失败", ex);
                MessageBox.Show(
                    $"退出登录失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        var column = this.FindName("SearchColumn") as ColumnDefinition;
        if (column != null)
        {
            if (SearchSideBarHost.Visibility == Visibility.Visible)
            {
                // 隐藏搜索栏和分隔线
                SearchSideBarHost.Visibility = Visibility.Collapsed;
                SearchSplitter.Visibility = Visibility.Collapsed;
                column.Width = new GridLength(0);
            }
            else
            {
                // 显示搜索栏和分隔线
                column.Width = new GridLength(300);  // 设置固定的初始宽度
                SearchSideBarHost.Visibility = Visibility.Visible;
                SearchSplitter.Visibility = Visibility.Visible;
            }
        }
    }

    private void InitializeSearchSideBar()
    {
        var searchSideBar = new SearchSideBar();
        searchSideBar.Initialize(_biliApiService, _logService);
        SearchSideBarHost.Content = searchSideBar;
        
        searchSideBar.RoomSelected += async (sender, room) =>
        {
            if (room != null)
            {
                await AddRoomToList(room, true);
                
                // 隐藏搜索栏和分隔线
                SearchSideBarHost.Visibility = Visibility.Collapsed;
                SearchSplitter.Visibility = Visibility.Collapsed;
                var column = this.FindName("SearchColumn") as ColumnDefinition;
                if (column != null)
                {
                    column.Width = new GridLength(0);
                }
            }
        };
    }

    // 新增：添加房间到列表的方法
    private Task AddRoomToList(RoomOption room, bool fromSearch = false)
    {
        // 检查是否已存在相同房间
        var existingRoom = RoomSelector.Items.OfType<RoomOption>()
            .FirstOrDefault(r => r.RoomId == room.RoomId);

        if (existingRoom != null)
        {
            // 如果房间已存在，将其移到顶部
            RoomSelector.Items.Remove(existingRoom);
            RoomSelector.Items.Insert(0, existingRoom);
            RoomSelector.SelectedItem = existingRoom;
            return Task.CompletedTask;
        }

        // 如果是搜索添加的房间
        if (fromSearch)
        {
            // 如果已经达到最大数量，移除最早添加的搜索房间
            while (_searchAddedRoomIds.Count >= MaxSearchAddedRooms)
            {
                var oldRoomId = _searchAddedRoomIds.Dequeue();
                var oldRoom = RoomSelector.Items.OfType<RoomOption>()
                    .FirstOrDefault(r => r.RoomId == oldRoomId);
                if (oldRoom != null)
                {
                    RoomSelector.Items.Remove(oldRoom);
                }
            }
            _searchAddedRoomIds.Enqueue(room.RoomId);
        }

        // 添加新房间到顶部
        RoomSelector.Items.Insert(0, room);
        RoomSelector.SelectedItem = room;
        
        return Task.CompletedTask;
    }

    private void VideoToggle_Click(object sender, RoutedEventArgs e)
    {
        var column = this.FindName("VideoColumn") as ColumnDefinition;
        if (column != null)
        {
            if (VideoToggle.IsChecked == true)
            {
                // 计算所需的视频区域宽度（16:9比例）
                double availableHeight = this.ActualHeight - 20;
                double desiredVideoWidth = availableHeight * 16.0 / 9.0;
                
                // 保存当前窗口位置和大小
                double currentLeft = this.Left;
                double currentWidth = this.Width;
                
                // 计算新的窗口宽度（当前宽度 + 视频区域宽度）
                double newWidth = currentWidth + desiredVideoWidth;
                
                // 如果新窗口会超出屏幕右边界，则向左扩展
                double screenWidth = SystemParameters.WorkArea.Width;
                if (currentLeft + newWidth > screenWidth)
                {
                    this.Left = Math.Max(0, screenWidth - newWidth);
                }
                
                // 设置新的窗口宽度
                this.Width = newWidth;
                
                // 设置视频区域宽度
                column.Width = new GridLength(desiredVideoWidth, GridUnitType.Pixel);
                
                // 获取当前播放器
                var player = _liveStreamService.GetCurrentPlayer();
                if (player != null)
                {
                    // 设置播放器参数
                    player.EnableMouseInput = true;
                    player.EnableKeyInput = true;
                    player.Scale = 0; // 自动缩放以适应窗口
                    player.AspectRatio = "16:9";
                    
                    // 显示视频区域
                    VideoView.Visibility = Visibility.Visible;
                }
                
                _isVideoEnabled = true;
                VideoSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                // 保存视频区域宽度
                double videoWidth = column.Width.Value;
                
                // 获取当前播放器
                var player = _liveStreamService.GetCurrentPlayer();
                if (player != null)
                {
                    // 隐藏视频区域，但保持播放器运行
                    VideoView.Visibility = Visibility.Hidden;
                }
                
                // 收起视频区域
                _savedVideoColumnWidth = column.Width;
                column.Width = new GridLength(0);
                VideoSplitter.Visibility = Visibility.Collapsed;
                
                // 恢复窗口原来的宽度
                this.Width = Math.Max(700, this.Width - videoWidth); // 确保不小于最小宽度
                
                _isVideoEnabled = false;
            }
        }
    }
}