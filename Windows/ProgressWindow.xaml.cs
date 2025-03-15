
using System.Windows;

namespace BiliVoxLive.Windows
{
    public partial class ProgressWindow : Window
    {
        public ProgressWindow(string title, string message)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
        }

        public void SetMessage(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetMessage(message));
                return;
            }
            MessageText.Text = message;
        }
    }
}