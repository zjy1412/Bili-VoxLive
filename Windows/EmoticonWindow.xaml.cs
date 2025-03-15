namespace BiliVoxLive.Windows;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using BiliVoxLive.Models;
using System.Windows.Controls.Primitives;

public partial class EmoticonWindow : Popup, IDisposable
{
    private readonly List<EmoticonPackage> _packages;
    public event EventHandler<Emoticon>? EmoticonSelected;
    private static Dictionary<long, int> _lastSelectedIndexByRoom = new();
    private readonly long _roomId;

    public EmoticonWindow(List<EmoticonPackage> packages, long roomId)
    {
        InitializeComponent();
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _roomId = roomId;

        PackageSelector.ItemsSource = packages;

        // 优先使用上次选择的索引
        if (_lastSelectedIndexByRoom.TryGetValue(roomId, out int lastIndex))
        {
            if (lastIndex >= 0 && lastIndex < packages.Count)
            {
                PackageSelector.SelectedIndex = lastIndex;
                EmoticonList.ItemsSource = packages[lastIndex].Emoticons;
            }
        }
        else
        {
            // 如果没有保存的索引，则默认选择最后一个表情包
            if (packages.Count > 0)
            {
                PackageSelector.SelectedIndex = packages.Count - 1;
                EmoticonList.ItemsSource = packages[packages.Count - 1].Emoticons;
            }
        }

        // 如果没有保存的索引或索引无效，则选择第一项
        if (PackageSelector.SelectedIndex == -1 && packages.Count > 0)
        {
            PackageSelector.SelectedIndex = 0;
            EmoticonList.ItemsSource = packages[0].Emoticons;
        }

        // 点击表情时触发事件
        EmoticonList.SelectionChanged += (s, e) =>
        {
            if (EmoticonList.SelectedItem is Emoticon emoticon)
            {
                EmoticonSelected?.Invoke(this, emoticon);
                EmoticonList.SelectedItem = null;
            }
        };

        // 确保在选择表情包时保存索引
        PackageSelector.SelectionChanged += (s, e) => 
        {
            if (PackageSelector.SelectedIndex >= 0)
            {
                _lastSelectedIndexByRoom[roomId] = PackageSelector.SelectedIndex;
            }
            
            if (PackageSelector.SelectedItem is EmoticonPackage package)
            {
                EmoticonList.ItemsSource = package.Emoticons;
            }
        };

        // 点击外部时关闭
        this.MouseLeave += (s, e) => this.IsOpen = false;
    }

    private void PackageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackageSelector.SelectedItem is EmoticonPackage package)
        {
            EmoticonList.ItemsSource = package.Emoticons;
            _lastSelectedIndexByRoom[_roomId] = PackageSelector.SelectedIndex;
        }
    }

    public void Dispose()
    {
        this.IsOpen = false;
        // 清理事件订阅等资源
        EmoticonSelected = null;
        this.MouseLeave -= (s, e) => this.IsOpen = false;
    }
}