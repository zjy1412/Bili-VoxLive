namespace BiliVoxLive.Windows;

using System.Windows;
using System.Windows.Media.Imaging;

public partial class QrPopupWindow : PopupWindow
{
    public QrPopupWindow()
    {
        InitializeComponent();
    }

    public void SetQrCode(BitmapImage qrCode)
    {
        QrCodeImage.Source = qrCode;
    }

    public void SetStatus(string status)
    {
        QrStatusText.Text = status;
    }
}