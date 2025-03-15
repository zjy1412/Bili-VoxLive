using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BiliVoxLive.Controls
{
    public class DanmakuOverlay : Canvas
    {
        private readonly Random _random = new Random();
        private const int MAX_DANMAKU_COUNT = 20;
        private const int DANMAKU_HEIGHT = 30;
        private readonly Queue<Border> _danmakuPool = new Queue<Border>();
        private const int POOL_SIZE = 50;

        public DanmakuOverlay()
        {
            Background = Brushes.Transparent;
            ClipToBounds = true;
            IsHitTestVisible = false;  // 确保弹幕不会影响鼠标事件

            // 预创建弹幕对象池
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var danmaku = CreateDanmakuElement("", false, false);
                danmaku.Visibility = Visibility.Collapsed;
                Children.Add(danmaku);
                _danmakuPool.Enqueue(danmaku);
            }
        }

        public void ShowDanmaku(string message, bool isGift = false, bool isSuperChat = false)
        {
            try
            {
                if (string.IsNullOrEmpty(message)) return;

                Border danmaku;
                if (_danmakuPool.Count > 0)
                {
                    danmaku = _danmakuPool.Dequeue();
                    UpdateDanmaku(danmaku, message, isGift, isSuperChat);
                }
                else
                {
                    danmaku = CreateDanmakuElement(message, isGift, isSuperChat);
                    Children.Add(danmaku);
                }

                danmaku.Visibility = Visibility.Visible;

                // 随机选择一个垂直位置
                var yPosition = _random.Next(0, Math.Max(1, (int)(ActualHeight - DANMAKU_HEIGHT)));
                SetTop(danmaku, yPosition);
                SetLeft(danmaku, ActualWidth);

                var animation = CreateDanmakuAnimation(danmaku);
                animation.Completed += (s, e) =>
                {
                    danmaku.Visibility = Visibility.Collapsed;
                    _danmakuPool.Enqueue(danmaku);
                };
                danmaku.BeginAnimation(LeftProperty, animation);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示弹幕失败: {ex.Message}");
            }
        }

        public void ClearDanmaku()
        {
            foreach (var child in Children.OfType<Border>().ToList())
            {
                child.BeginAnimation(LeftProperty, null);  // 停止所有动画
                child.Visibility = Visibility.Collapsed;
                if (!_danmakuPool.Contains(child))
                {
                    _danmakuPool.Enqueue(child);
                }
            }
        }

        private void UpdateDanmaku(Border danmaku, string message, bool isGift, bool isSuperChat)
        {
            if (danmaku.Child is TextBlock textBlock)
            {
                textBlock.Text = message;
                textBlock.Foreground = isGift ? Brushes.Gold : (isSuperChat ? Brushes.Red : Brushes.White);
            }

            danmaku.Background = new SolidColorBrush(Color.FromArgb(
                180,
                isGift ? (byte)64 : (isSuperChat ? (byte)192 : (byte)32),
                isGift ? (byte)64 : (isSuperChat ? (byte)32 : (byte)32),
                isGift ? (byte)0 : (isSuperChat ? (byte)32 : (byte)32)
            ));
            danmaku.BorderBrush = isGift ? Brushes.Gold : (isSuperChat ? Brushes.Red : Brushes.DarkGray);
        }

        private static Border CreateDanmakuElement(string message, bool isGift, bool isSuperChat)
        {
            var textBlock = new TextBlock
            {
                Text = message,
                FontSize = 16,
                Foreground = isGift ? Brushes.Gold : (isSuperChat ? Brushes.Red : Brushes.Black),  // 修改默认颜色为黑色
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            return new Border
            {
                Child = textBlock,
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),  // 半透明白色背景
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(15),
                Effect = new System.Windows.Media.Effects.DropShadowEffect  // 添加阴影效果
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Opacity = 0.3
                }
            };
        }

        private DoubleAnimation CreateDanmakuAnimation(FrameworkElement danmaku)
        {
            var animation = new DoubleAnimation
            {
                From = ActualWidth,
                To = -danmaku.ActualWidth - 50,
                Duration = TimeSpan.FromSeconds(8),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            return animation;
        }
    }
}