
namespace BiliVoxLive.Controls;

using System.Windows.Controls;

public partial class LogViewer : UserControl
{
    private readonly ILogService _logService;

    public LogViewer()
    {
        InitializeComponent();
    }

    public void AppendLog(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            LogText.Clear();
            return;
        }

        LogText.AppendText(message + "\n");
        LogText.ScrollToEnd();
    }
}