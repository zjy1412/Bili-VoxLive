using System.Windows;

namespace BiliVoxLive.Windows
{
    public class PopupWindow : Window
    {
        public PopupWindow()
        {
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
        }
    }
}