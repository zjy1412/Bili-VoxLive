using System;
using System.Collections.ObjectModel;
using System.ComponentModel;               // 添加这行 - INotifyPropertyChanged
using System.Runtime.CompilerServices;     // 添加这行 - CallerMemberName
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BiliVoxLive.Models;

namespace BiliVoxLive.Controls
{
    public partial class SearchSideBar : UserControl, INotifyPropertyChanged
    {
        public static readonly DependencyProperty LogServiceProperty =
            DependencyProperty.Register(
                nameof(LogService),
                typeof(LogService),
                typeof(SearchSideBar),
                new PropertyMetadata(null, OnLogServiceChanged)
            );

        public LogService LogService
        {
            get => (LogService)GetValue(LogServiceProperty);
            set => SetValue(LogServiceProperty, value);
        }

        private static void OnLogServiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SearchSideBar searchSideBar && e.NewValue is LogService logService)
            {
                searchSideBar._logService = logService;
                searchSideBar.InitializeSearchTimer();
            }
        }

        private LogService? _logService;
        private System.Timers.Timer? _searchTimer;
        
        public ObservableCollection<RoomOption> SearchResults { get; } = new();
        public ObservableCollection<string> SearchSuggestions { get; } = new();
        
        public event EventHandler<RoomOption>? RoomSelected;
        
        private bool _isProcessing = false;
        private int _currentPage = 1;
        private int _totalPages = 1;
        private ObservableCollection<PageNumberVM> _pageNumbersToShow = new();
        private string _currentKeyword = "";
        private BiliApiService? _biliApiService;
        
        public ObservableCollection<PageNumberVM> PageNumbersToShow 
        { 
            get => _pageNumbersToShow;
            private set
            {
                _pageNumbersToShow = value;
                OnPropertyChanged();
            }
        }

        public bool CanGoPrevious => _currentPage > 1 && !_isProcessing;
        public bool CanGoNext => _currentPage < _totalPages && !_isProcessing;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public SearchSideBar()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void Initialize(BiliApiService biliApiService, LogService logService)
        {
            _biliApiService = biliApiService;
            _logService = logService;
            InitializeSearchTimer();
        }

        private void InitializeSearchTimer()
        {
            _searchTimer = new System.Timers.Timer(500); // 500ms延迟
            _searchTimer.Elapsed += async (s, e) =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await SearchSuggestionsAsync();
                });
            };
            _searchTimer.AutoReset = false;
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchSuggestions.Any())
            {
                SuggestionsPopup.IsOpen = true;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 使用Dispatcher延迟关闭，以便让选择建议的事件先触发
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!SuggestionsList.IsKeyboardFocusWithin)
                {
                    SuggestionsPopup.IsOpen = false;
                }
            }), DispatcherPriority.Input);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer?.Stop();
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _searchTimer?.Start();
            }
            else
            {
                SearchSuggestions.Clear();
                SuggestionsPopup.IsOpen = false;
            }
        }

        private async Task SearchSuggestionsAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text)) return;
                
                if (_biliApiService == null) return;
                
                var suggestions = await _biliApiService.GetSearchSuggestionsAsync(SearchBox.Text);
                SearchSuggestions.Clear();
                foreach (var suggestion in suggestions)
                {
                    SearchSuggestions.Add(suggestion);
                }
                
                if (SearchSuggestions.Any() && SearchBox != null && SearchBox.IsFocused)
                {
                    SuggestionsPopup.IsOpen = true;
                }
            }
            catch (Exception ex)
            {
                _logService?.Error("获取搜索建议失败", ex);
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
            _currentKeyword = SearchBox.Text;
            await PerformSearch(1);
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = Task.Run(async () => await PerformSearch());
                e.Handled = true;
            }
        }

        private async Task PerformSearch(int page = 1)
        {
            if (_isProcessing || string.IsNullOrWhiteSpace(_currentKeyword)) return;
        
            try
            {
                _isProcessing = true;
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
                
                SearchResults.Clear();
                
                // 关闭建议、隐藏popup并取消搜索框焦点
                SearchSuggestions.Clear();
                SuggestionsPopup.IsOpen = false;
                
                // 添加对SearchBox的非空检查
                if (SearchBox != null)
                {
                    SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                
                // 确保_biliApiService不为空
                if (_biliApiService == null)
                {
                    _logService?.Error("BiliApiService为空，无法执行搜索");
                    _isProcessing = false;
                    OnPropertyChanged(nameof(CanGoPrevious));
                    OnPropertyChanged(nameof(CanGoNext));
                    return;
                }
                
                var (results, totalPages) = await _biliApiService.SearchLiveRoomsAsync(_currentKeyword, page);
                foreach (var room in results)
                {
                    SearchResults.Add(room);
                }
                
                _currentPage = page;
                _totalPages = totalPages; // 使用API返回的实际总页数
                UpdatePageNumbers();
            }
            catch (Exception ex)
            {
                _logService?.Error("搜索失败", ex);
                MessageBox.Show("搜索失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isProcessing = false;
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
            }
        }

        private void UpdatePageNumbers()
        {
            PageNumbersToShow.Clear();
            var pages = new List<PageNumberVM>();
            
            // 始终添加第一页
            pages.Add(new PageNumberVM 
            { 
                Number = "1",
                IsSelected = _currentPage == 1,
                IsEnabled = true
            });
            
            if (_currentPage > 4)
            {
                pages.Add(new PageNumberVM { Number = "...", IsEnabled = false });
            }
            
            // 添加当前页附近的页码
            for (int i = Math.Max(2, _currentPage - 2); 
                 i <= Math.Min(_totalPages - 1, _currentPage + 2); i++)
            {
                pages.Add(new PageNumberVM 
                { 
                    Number = i.ToString(),
                    IsSelected = i == _currentPage,
                    IsEnabled = true
                });
            }
            
            if (_currentPage < _totalPages - 3)
            {
                pages.Add(new PageNumberVM { Number = "...", IsEnabled = false });
            }
            
            // 添加最后一页
            if (_totalPages > 1)
            {
                pages.Add(new PageNumberVM 
                { 
                    Number = _totalPages.ToString(),
                    IsSelected = _currentPage == _totalPages,
                    IsEnabled = true
                });
            }
            
            foreach (var page in pages)
            {
                PageNumbersToShow.Add(page);
            }
            
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
        }

        private async void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _currentPage <= 1) return;
            await PerformSearch(_currentPage - 1);
        }

        private async void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing || _currentPage >= _totalPages) return;
            await PerformSearch(_currentPage + 1);
        }

        private async void PageNumber_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing) return;
        
            var button = sender as Button;
            var pageVM = button?.DataContext as PageNumberVM;
        
            if (pageVM != null && pageVM.IsEnabled && int.TryParse(pageVM.Number, out int pageNum))
            {
                await PerformSearch(pageNum);
            }
        }

        private async Task PerformSearch()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text)) return;
                
                _currentKeyword = SearchBox.Text;
                _currentPage = 1;
                SearchResults.Clear();
                
                // 关闭建议、隐藏popup并取消搜索框焦点
                SearchSuggestions.Clear();
                SuggestionsPopup.IsOpen = false;
                
                // 添加对SearchBox的非空检查
                if (SearchBox != null)
                {
                    SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
                
                // 确保_biliApiService不为空
                if (_biliApiService == null)
                {
                    _logService?.Error("BiliApiService为空，无法执行搜索");
                    _isProcessing = false;
                    OnPropertyChanged(nameof(CanGoPrevious));
                    OnPropertyChanged(nameof(CanGoNext));
                    return;
                }
                
                var (results, totalPages) = await _biliApiService.SearchLiveRoomsAsync(_currentKeyword, _currentPage);
                foreach (var room in results)
                {
                    SearchResults.Add(room);
                }
                _totalPages = totalPages;
                UpdatePageNumbers();
            }
            catch (Exception ex)
            {
                _logService?.Error("搜索失败", ex);
                MessageBox.Show("搜索失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchResult_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (SearchResultsList.SelectedItem is RoomOption room)
            {
                RoomSelected?.Invoke(this, room);
                SearchResultsList.SelectedItem = null; // 清除选中状态
            }
        }

        private void SearchSuggestion_Selected(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox?.SelectedItem is string suggestion)
            {
                SearchBox.Text = suggestion;
                // 清除选中状态
                listBox.SelectedItem = null;
                // 执行搜索，使用异步lambda
                _ = Task.Run(async () => await PerformSearch());
            }
        }
    }
}

public class PageNumberVM : INotifyPropertyChanged
{
    private string _number = "";
    private bool _isSelected;
    private bool _isEnabled;

    public string Number 
    {
        get => _number;
        set
        {
            _number = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
