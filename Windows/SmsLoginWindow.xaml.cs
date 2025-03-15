namespace BiliVoxLive.Windows;

using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

public class CaptchaResult
{
    public string Challenge { get; set; } = "";
    public string Validate { get; set; } = "";
    public string Seccode { get; set; } = "";
}

public partial class SmsLoginWindow : Window
{
    private readonly ILogService _logService;
    private readonly BiliApiService _biliApiService;
    private bool _loginSuccess;
    private string _captchaKey = "";
    private string _challenge = "";
    private string _validate = "";
    private string _seccode = "";

    public SmsLoginWindow(ILogService logService, BiliApiService biliApiService)
    {
        InitializeComponent();
        _logService = logService;
        _biliApiService = biliApiService;
        _loginSuccess = false;
    }

    private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SendCodeButton.IsEnabled = false;
            StatusText.Text = "发送中...";
            
            var phoneNumber = PhoneNumberInput.Text.Trim();
            if (!Regex.IsMatch(phoneNumber, @"^1[3-9]\d{9}$"))
            {
                MessageBox.Show("请输入正确的手机号");
                SendCodeButton.IsEnabled = true;
                return;
            }

            // 第一次尝试直接发送
            var deviceId = Guid.NewGuid().ToString("N");
            var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

            var result = await _biliApiService.SendSmsWithCaptchaAsync(deviceId, timestamp, phoneNumber);

            if (result.Success)
            {
                ShowSmsCodeInput(result.CaptchaKey);
            }
            else if (!string.IsNullOrEmpty(result.RecaptchaUrl))
            {
                // 如果需要验证码，显示验证码并等待完成
                _logService.Debug("需要滑动验证");
                var validateTask = new TaskCompletionSource<(string challenge, string validate, string seccode)>();
                
                await ShowCaptcha(result.RecaptchaUrl, validateTask);
                
                var (challenge, validate, seccode) = await validateTask.Task;
                
                // 验证完成后重新发送
                _logService.Debug("滑动验证完成，重新发送验证码...");
                result = await _biliApiService.SendSmsWithCaptchaAsync(
                    deviceId,
                    timestamp,
                    phoneNumber,
                    challenge,
                    validate,
                    seccode
                );

                if (result.Success)
                {
                    ShowSmsCodeInput(result.CaptchaKey);
                }
                else
                {
                    StatusText.Text = result.Message;
                    SendCodeButton.IsEnabled = true;
                }
            }
            else
            {
                StatusText.Text = result.Message;
                SendCodeButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _logService.Error("发送验证码失败", ex);
            StatusText.Text = "发送失败";
            SendCodeButton.IsEnabled = true;
        }
    }

    private async Task ShowCaptcha(string url, TaskCompletionSource<(string, string, string)> validateTask)
    {
        try
        {
            _logService.Debug("=================== 开始验证码流程 ===================");
            _logService.Debug($"验证码URL: {url}");
            
            StatusText.Text = "请完成滑动验证...";
            CaptchaWebView.Visibility = Visibility.Visible;

            await CaptchaWebView.EnsureCoreWebView2Async();

            // 注入日志脚本
            await CaptchaWebView.CoreWebView2.ExecuteScriptAsync(@"
                // 拦截所有console方法
                const methods = ['log', 'debug', 'info', 'warn', 'error'];
                methods.forEach(method => {
                    const original = console[method];
                    console[method] = function() {
                        original.apply(console, arguments);
                        const message = Array.from(arguments)
                            .map(arg => typeof arg === 'object' ? JSON.stringify(arg) : String(arg))
                            .join(' ');
                        window.chrome.webview.postMessage({
                            type: 'log',
                            level: method,
                            message: message
                        });
                    };
                });

                console.log('日志系统已初始化');
            ");

            // 添加验证码检测脚本
            await CaptchaWebView.CoreWebView2.ExecuteScriptAsync(@"
                console.log('初始化验证码监听...');
                let lastData = null;
                
                function checkValidation() {
                    try {
                        console.log('检查GEETEST_DATA...');
                        if (window.GEETEST_DATA) {
                            console.log('GEETEST_DATA:', JSON.stringify(window.GEETEST_DATA));
                            
                            const currentData = JSON.stringify(window.GEETEST_DATA);
                            if (currentData !== lastData && window.GEETEST_DATA.geetest_validate) {
                                console.log('检测到新的验证结果');
                                lastData = currentData;
                                
                                window.chrome.webview.postMessage({
                                    type: 'validate',
                                    data: {
                                        challenge: window.GEETEST_DATA.geetest_challenge,
                                        validate: window.GEETEST_DATA.geetest_validate,
                                        seccode: window.GEETEST_DATA.geetest_validate + '|jordan'
                                    }
                                });
                                return true;
                            }
                        }
                    } catch(err) {
                        console.error('验证检查错误:', err);
                    }
                    return false;
                }

                setInterval(checkValidation, 500);
                console.log('验证码监听已启动');
            ");

            // 统一消息处理
            CaptchaWebView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var message = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
                    var type = message.GetProperty("type").GetString();

                    switch (type)
                    {
                        case "log":
                            var level = message.GetProperty("level").GetString();
                            var logMessage = message.GetProperty("message").GetString();
                            _logService.Debug($"WebView2 {level}: {logMessage}");
                            break;

                        case "validate":
                            var data = message.GetProperty("data");
                            var result = new CaptchaResult
                            {
                                Challenge = data.GetProperty("challenge").GetString()!,
                                Validate = data.GetProperty("validate").GetString()!,
                                Seccode = data.GetProperty("seccode").GetString()!
                            };

                            _logService.Debug($"收到验证结果: {JsonSerializer.Serialize(result)}");
                            
                            Dispatcher.Invoke(() =>
                            {
                                StatusText.Text = "验证通过，正在发送验证码...";
                                CaptchaWebView.Visibility = Visibility.Collapsed;
                            });

                            validateTask.SetResult((result.Challenge, result.Validate, result.Seccode));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logService.Error($"处理WebView2消息失败: {ex.Message}");
                }
            };

            _logService.Debug($"正在加载验证页面: {url}");
            CaptchaWebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _logService.Error($"显示验证码失败: {ex.Message}");
            validateTask.SetException(ex);
        }
    }

    private async void OnCaptchaResult(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            _logService.Debug($"收到验证码结果: {e.WebMessageAsJson}");
            var result = JsonSerializer.Deserialize<CaptchaResult>(e.WebMessageAsJson);
            
            if (result != null && !string.IsNullOrEmpty(result.Validate))
            {
                await Dispatcher.InvokeAsync(async () => {
                    try {
                        // 立即更新UI状态
                        StatusText.Text = "验证通过，正在发送验证码...";
                        CaptchaWebView.Visibility = Visibility.Collapsed;

                        // 存储验证结果
                        _challenge = result.Challenge;
                        _validate = result.Validate;
                        _seccode = result.Seccode;

                        // 清理事件监听
                        if (CaptchaWebView.CoreWebView2 != null)
                        {
                            CaptchaWebView.CoreWebView2.WebMessageReceived -= OnCaptchaResult;
                        }

                        // 发送验证码
                        var deviceId = Guid.NewGuid().ToString("N");
                        var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
                        
                        var smsResult = await _biliApiService.SendSmsWithCaptchaAsync(
                            deviceId,
                            timestamp, 
                            PhoneNumberInput.Text.Trim(),
                            _challenge,
                            _validate,
                            _seccode
                        );

                        if (smsResult.Success)
                        {
                            ShowSmsCodeInput(smsResult.CaptchaKey);
                        }
                        else
                        {
                            StatusText.Text = smsResult.Message;
                            SendCodeButton.IsEnabled = true;
                        }
                    }
                    catch (Exception ex) {
                        _logService.Error("发送验证码失败", ex);
                        StatusText.Text = "发送失败";
                        SendCodeButton.IsEnabled = true;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logService.Error("处理验证码结果失败", ex);
            Dispatcher.Invoke(() => {
                StatusText.Text = "验证失败，请重试";
                SendCodeButton.IsEnabled = true;
            });
        }
    }

    private void ShowSmsCodeInput(string captchaKey)
    {
        _captchaKey = captchaKey;
        VerificationCodeInput.IsEnabled = true;
        LoginButton.IsEnabled = true;
        StatusText.Text = "验证码已发送";
        StartCountdown();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoginButton.IsEnabled = false;
            StatusText.Text = "登录中...";
            
            var code = VerificationCodeInput.Text.Trim();
            if (string.IsNullOrEmpty(code))
            {
                MessageBox.Show("请输入验证码");
                return;
            }

            var result = await _biliApiService.VerifySmsCodeAsync(code, _captchaKey);
            if (result.Success)
            {
                _loginSuccess = true;
                Close();
            }
            else
            {
                StatusText.Text = result.Message;
                LoginButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _logService.Error("登录失败", ex);
            StatusText.Text = "登录失败";
            LoginButton.IsEnabled = true;
        }
    }

    private async void StartCountdown()
    {
        for (int i = 60; i > 0; i--)
        {
            SendCodeButton.Content = $"{i}秒";
            await Task.Delay(1000);
        }
        SendCodeButton.Content = "获取验证码";
        SendCodeButton.IsEnabled = true;
    }

    public bool LoginSuccess => _loginSuccess;
}
