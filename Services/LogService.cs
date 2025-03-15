namespace BiliVoxLive;
using System.IO;

public interface ILogService
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
}

public class LogService : ILogService
{
    private readonly StreamWriter _logFile;
    private readonly string _logPath;
    private readonly long _maxLogSize;     // 最大日志文件大小（字节）
    private readonly int _maxLogDays;      // 日志保留天数
    private readonly object _cleanLock = new object();
    private DateTime _lastCleanTime;       // 上次清理时间

    public event EventHandler<string>? OnLogReceived;

    public LogService()
    {
        _maxLogSize = 10 * 1024 * 1024;   // 默认10MB
        _maxLogDays = 7;                   // 默认保留7天
        _lastCleanTime = DateTime.Now;
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");

        // 检查并清理旧日志
        CleanOldLogs();

        // 如果当前日志文件过大，进行备份
        if (File.Exists(_logPath) && new FileInfo(_logPath).Length > _maxLogSize)
        {
            ArchiveCurrentLog();
        }

        _logFile = new StreamWriter(_logPath, true) { AutoFlush = true };
        WriteLog("日志服务已初始化");
    }

    private void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logMessage = $"[{timestamp}] [{level}] {message}";
        WriteLog(logMessage);
    }

    // 使用普通方法实现而不是显式实现
    public void Debug(string message) => Log("DEBUG", message);
    public void Info(string message) => Log("INFO", message);
    public void Warning(string message) => Log("WARN", message);
    public void Error(string message, Exception? ex = null) => Log("ERROR", $"{message} {ex?.Message}");

    private void WriteLog(string message)
    {
        // 每次写入前检查是否需要清理
        CheckAndCleanLogs();

        // 添加自动在每个日志消息后添加换行符
        if (!message.EndsWith(Environment.NewLine))
        {
            message += Environment.NewLine;
        }

        try
        {
            // 确保同步写入
            lock (_logFile)
            {
                _logFile.Write(message);
                _logFile.Flush();
            }
            
            // 同步输出到控制台
            Console.Write(message);
            Console.Out.Flush();
            
            // 触发事件
            try { OnLogReceived?.Invoke(this, message); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写入日志失败: {ex.Message}");
        }
    }

    private void CheckAndCleanLogs()
    {
        // 每小时最多清理一次
        if ((DateTime.Now - _lastCleanTime).TotalHours < 1) return;

        lock (_cleanLock)
        {
            try
            {
                var currentFileSize = new FileInfo(_logPath).Length;
                if (currentFileSize > _maxLogSize)
                {
                    ArchiveCurrentLog();
                }

                CleanOldLogs();
                _lastCleanTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"检查和清理日志时出错: {ex.Message}");
            }
        }
    }

    private void ArchiveCurrentLog()
    {
        try
        {
            if (!File.Exists(_logPath)) return;

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var archivePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "logs",
                $"app_{timestamp}.log"
            );

            // 确保存档目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

            // 关闭当前日志文件
            _logFile?.Dispose();

            // 移动当前日志文件到存档
            File.Move(_logPath, archivePath);

            // 创建新的日志文件
            _logFile?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"存档日志文件时出错: {ex.Message}");
        }
    }

    private void CleanOldLogs()
    {
        try
        {
            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logsDir)) return;

            var cutoffDate = DateTime.Now.AddDays(-_maxLogDays);
            var oldLogs = Directory.GetFiles(logsDir, "app_*.log")
                .Select(f => new FileInfo(f))
                .Where(f => f.CreationTime < cutoffDate)
                .ToList();

            foreach (var log in oldLogs)
            {
                try
                {
                    log.Delete();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"删除旧日志文件 {log.Name} 时出错: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清理旧日志时出错: {ex.Message}");
        }
    }

    ~LogService()
    {
        _logFile.Dispose();
    }
}