namespace BiliVoxLive;
using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LibVLCSharp.Shared;

public class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            var app = new Application();
            var currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var libvlcDirectory = Path.Combine(currentDirectory, "libvlc", "win-x64");

            // 检查目录是否存在
            if (!Directory.Exists(libvlcDirectory))
            {
                MessageBox.Show($"VLC库目录不存在: {libvlcDirectory}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 检查插件目录
            var pluginsDir = Path.Combine(libvlcDirectory, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                MessageBox.Show($"VLC插件目录不存在: {pluginsDir}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 检查必要的文件
            var requiredFiles = new[] { "libvlc.dll", "libvlccore.dll" };
            foreach (var file in requiredFiles)
            {
                var filePath = Path.Combine(libvlcDirectory, file);
                if (!File.Exists(filePath))
                {
                    MessageBox.Show($"缺少必要的文件: {filePath}", 
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // 必须先设置环境变量
            Environment.SetEnvironmentVariable("PATH", 
                $"{libvlcDirectory};{Environment.GetEnvironmentVariable("PATH")}");

            // 然后初始化 Core
            try
            {
                Core.Initialize(libvlcDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"VLC Core初始化失败: {ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 创建服务
            var services = new ServiceCollection();
            var serviceProvider = services.BuildServiceProvider();  // 先创建 ServiceProvider
            
            // 创建 LibVLC 实例
            LibVLC? libVLC = null;
            try
            {
                libVLC = new LibVLC(
                    enableDebugLogs: false,
                    "--quiet",  // 完全禁用VLC日志输出
                    "--drop-late-frames",
                    "--skip-frames",
                    "--network-caching=1000",
                    "--live-caching=1000",
                    "--clock-synchro=0", 
                    "--sout-mux-caching=1000",
                    "--audio-time-stretch",
                    "--aout=mmdevice",
                    "--no-stats",
                    "--no-osd",
                    "--no-snapshot-preview",
                    "--no-metadata-network-access",
                    "--verbose=-1",       // 设置为最低日志级别
                    "--no-file-logging",  // 禁用文件日志
                    "--no-sub-autodetect-file"
                );

                // 只记录真正的错误
                var ignoredMessages = new[]
                {
                    "buffer", "looking", "creating", "volume", 
                    "decoded", "found", "trying", "stream", "audio output",
                    "format", "params", "frame", "using", "TLS", "data",
                    "conversion", "removing", "resolving", "simple", "state",
                    "playback", "setting", "version", "HTTP"
                };

                libVLC.Log += (s, e) =>
                {
                    // 只记录严重错误
                    if (e.Level == LogLevel.Error)
                    {
                        // 忽略包含特定关键词的消息
                        if (!ignoredMessages.Any(msg => e.Message.Contains(msg, StringComparison.OrdinalIgnoreCase)))
                        {
                            var logService = serviceProvider.GetService<ILogService>();
                            logService?.Error($"VLC严重错误: {e.Message}");
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"VLC实例创建失败: {ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 配置服务
            services.AddSingleton<LogService>();
            services.AddSingleton<ILogService>(sp => sp.GetRequiredService<LogService>());
            services.AddSingleton<CookieService>();
            services.AddSingleton<ICookieService>(sp => sp.GetRequiredService<CookieService>());
            services.AddSingleton(libVLC);
            services.AddSingleton<BiliApiService>();
            services.AddSingleton<LiveStreamService>();
            services.AddSingleton<DanmakuService>();
            services.AddTransient<MainWindow>();

            // 重新构建 ServiceProvider，包含所有注册的服务
            serviceProvider = services.BuildServiceProvider();
            
            // 创建主窗口
            var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            app.MainWindow = mainWindow;

            // 改进全局异常处理
            app.DispatcherUnhandledException += (s, e) =>
            {
                var logService = serviceProvider.GetRequiredService<ILogService>();
                logService.Error($"未处理的异常: {e.Exception.Message}", e.Exception);
                
                MessageBox.Show(
                    $"发生错误: {e.Exception.Message}\n\n请检查日志了解详情。",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                
                e.Handled = true;
            };

            // 应用程序退出处理
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var logService = serviceProvider.GetRequiredService<ILogService>();
                logService.Error($"未处理的域异常: {e.ExceptionObject}", e.ExceptionObject as Exception);
            };

            app.Exit += async (s, e) =>
            {
                try
                {
                    var liveService = serviceProvider.GetRequiredService<LiveStreamService>();
                    await liveService.StopAsync();
                    libVLC.Dispose();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"清理资源时出错: {ex.Message}", "警告", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            // 运行应用程序
            mainWindow.Show();
            app.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", 
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}