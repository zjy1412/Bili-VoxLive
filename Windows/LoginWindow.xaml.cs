namespace BiliVoxLive.Windows;

using System.Windows;
using QRCoder;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security;
using System.IO;
using System.Windows.Media.Imaging;

public partial class LoginWindow : Window
{
    private readonly ILogService _logService;
    private readonly BiliApiService _biliApiService;
    private bool _loginSuccess;
    private CancellationTokenSource? _qrCheckCts;
    private QrPopupWindow? _qrPopup;

    public LoginWindow(ILogService logService, BiliApiService biliApiService)
    {
        InitializeComponent();
        _logService = logService;
        _biliApiService = biliApiService;
        _loginSuccess = false;
    }

    public bool LoginSuccess => _loginSuccess;

    private void ShowQrPopup()
    {
        _qrPopup = new QrPopupWindow { Owner = this };
        _qrPopup.Show();
    }

    private void HideQrPopup()
    {
        if (_qrPopup != null)
        {
            _qrPopup.Close();
            _qrPopup = null;
        }
    }

    private async void QrCodeLoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _qrCheckCts?.Cancel();
            _qrCheckCts = new CancellationTokenSource();
            
            ShowQrPopup();
            
            if (_qrPopup == null) throw new Exception("Failed to create QR popup window");
            
            _qrPopup.SetStatus("正在加载二维码...");
            
            var qrResponse = await _biliApiService.GetQrCodeAsync();
            if (qrResponse == null) throw new Exception("获取二维码失败");
            
            var qrBitmap = await GenerateQrCodeImage(qrResponse.Url);
            _qrPopup.SetQrCode(qrBitmap);
            _qrPopup.SetStatus("请使用哔哩哔哩手机客户端扫描");
            
            await CheckQrCodeStatus(qrResponse.QrKey, _qrCheckCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logService.Info("二维码登录已取消");
        }
        catch (Exception ex)
        {
            _logService.Error("二维码登录失败", ex);
            MessageBox.Show($"二维码登录失败: {ex.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            HideQrPopup();
            _qrCheckCts?.Dispose();
            _qrCheckCts = null;
        }
    }

    private void SmsLoginButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("该功能正在开发中，敬请期待！", "提示", 
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CookieLoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cookieGuide = "请粘贴Cookie文件内容：\n\n" +
                             "1. 请使用浏览器Cookie导出扩展\n" +
                             "2. 确保已登录 bilibili.com\n" +
                             "3. 导出Netscape格式的Cookie文件\n" +
                             "4. 将文件内容完整复制到此处";

            var dialog = new InputDialog("Cookie登录", cookieGuide) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _ = Task.Run(async () =>
                {
                    await _biliApiService.LoginAsync(dialog.Answer);
                    Dispatcher.Invoke(() =>
                    {
                        _loginSuccess = true;
                        Close();
                    });
                });
            }
        }
        catch (Exception ex)
        {
            _logService.Error("Cookie登录失败", ex);
            MessageBox.Show($"登录失败: {ex.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task<BitmapImage> GenerateQrCodeImage(string url)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new QRCode(qrData);
        var bitmap = qrCode.GetGraphic(20); // 20是像素大小
        
        var bitmapImage = new BitmapImage();
        using var memory = new MemoryStream();
        
        bitmap.Save(memory, ImageFormat.Png);
        memory.Position = 0;
        
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = memory;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        
        // 将所有资源释放
        qrCode.Dispose();
        qrData.Dispose();
        qrGenerator.Dispose();
        bitmap.Dispose();

        return Task.FromResult(bitmapImage);
    }

    private async Task CheckQrCodeStatus(string qrKey, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _biliApiService.CheckQrCodeStatusAsync(qrKey);
                _logService.Debug($"二维码状态检查: {status.Code} - {status.Message}");
                
                _qrPopup?.SetStatus(status.Message);
                
                if (status.Code == 0) // 登录成功
                {
                    _loginSuccess = true;
                    _logService.Info("扫码登录成功");
                    // 添加一个短暂的延迟，确保Cookie已经完全保存
                    await Task.Delay(500, ct);
                    Close();
                    return;
                }
                
                if (status.Code == 86090)
                {
                    MessageBox.Show("二维码已失效，请重新获取", "提示");
                    return;
                }
                
                await Task.Delay(1000, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.Error("检查二维码状态失败", ex);
                MessageBox.Show($"检查二维码状态失败: {ex.Message}", "错误");
                throw;
            }
        }
    }
}