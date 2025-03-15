namespace BiliVoxLive;
using System.IO;
using System.Text;  // 添加这行
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public interface ICookieService
{
    Task SaveCookiesAsync(string cookieContent);
    Task<string> LoadCookiesAsync();
    Task ClearCookiesAsync();
}

public class CookieService : ICookieService
{
    private readonly ILogService _logService;
    private readonly string _cookieFilePath;
    private string? _cachedCookie;  // 只保留一个定义

    public CookieService(ILogService logService)
    {
        _logService = logService;
        _cookieFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bilibili_cookies.txt");  // 修复大小写
    }

    private static readonly string[] RequiredCookies = { "SESSDATA", "bili_jct", "DedeUserID" };

    public string GetCookie()
    {
        if (!string.IsNullOrEmpty(_cachedCookie))
        {
            return _cachedCookie;
        }

        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logService.Warning($"Cookie文件不存在: {_cookieFilePath}");
                return string.Empty;
            }

            // 读取并解析文件
            var lines = File.ReadAllLines(_cookieFilePath);
            _logService.Debug($"读取到 {lines.Length} 行数据");
            
            var cookieDict = ParseNetscapeCookieFile(lines);
            
            // 检查是否所有必需的cookie都存在
            if (RequiredCookies.All(cookieDict.ContainsKey))
            {
                _cachedCookie = string.Join("; ", RequiredCookies.Select(name => $"{name}={cookieDict[name]}"));
                _logService.Info($"成功提取Cookie");
                return _cachedCookie;
            }

            var missing = RequiredCookies.Except(cookieDict.Keys);
            _logService.Warning($"缺少必需的Cookie: {string.Join(", ", missing)}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logService.Error("读取Cookie失败", ex);
            return string.Empty;
        }
    }

    private Dictionary<string, string> ParseNetscapeCookieFile(string[] lines)
    {
        var cookieDict = new Dictionary<string, string>();
        int lineNumber = 0;
        
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                continue;

            try
            {
                // 处理带空格的内容：将多个空格替换为一个，然后按空格分割
                var cleanedLine = Regex.Replace(line.Trim(), @"\s+", " ");
                var parts = cleanedLine.Split(' ');
                
                if (parts.Length >= 7)
                {
                    var domain = parts[0];
                    var name = parts[5];
                    var value = parts[6];
                    
                    // 对于超过7个部分的情况，将剩余部分合并为value
                    if (parts.Length > 7)
                    {
                        value = string.Join(" ", parts.Skip(6));
                    }

                    if (domain.Contains("bilibili.com") && RequiredCookies.Contains(name))
                    {
                        _logService.Debug($"找到必需的Cookie: {name}={value}");
                        cookieDict[name] = value;
                    }
                    else
                    {
                        _logService.Debug($"跳过Cookie: domain={domain}, name={name}");
                    }
                }
                else
                {
                    _logService.Debug($"跳过行 {lineNumber}: 格式不符合要求 (字段数: {parts.Length})");
                    _logService.Debug($"行内容: {line}");
                }
            }
            catch (Exception ex)
            {
                _logService.Warning($"解析第 {lineNumber} 行时出错: {ex.Message}");
            }
        }

        // 验证找到的Cookie
        var found = cookieDict.Keys.ToList();
        _logService.Info($"找到以下Cookie: {string.Join(", ", found)}");
        
        var missing = RequiredCookies.Except(found).ToList();
        if (missing.Any())
        {
            _logService.Warning($"缺少以下Cookie: {string.Join(", ", missing)}");
        }
        else
        {
            _logService.Info("已找到所有必需的Cookie");
        }

        return cookieDict;
    }

    public void SaveCookie(string cookieContent)
    {
        if (string.IsNullOrWhiteSpace(cookieContent))
        {
            throw new ArgumentException("Cookie内容不能为空");
        }

        cookieContent = cookieContent.Trim();
        if (!cookieContent.StartsWith("# Netscape HTTP Cookie File"))
        {
            throw new FormatException("Cookie格式错误：必须是Netscape格式的Cookie文件");
        }

        try
        {
            _cachedCookie = null;  // 清除缓存
            File.WriteAllText(_cookieFilePath, cookieContent);
            _logService.Debug($"Cookie已保存到: {_cookieFilePath}");

            // 立即验证保存的内容
            var cookie = GetCookie();
            if (string.IsNullOrEmpty(cookie))
            {
                throw new Exception("保存的Cookie无效，未包含所需信息");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"保存Cookie失败: {ex.Message}", ex);
        }
    }

    public async Task SaveCookiesAsync(string cookieContent)
    {
        if (string.IsNullOrWhiteSpace(cookieContent))
        {
            throw new ArgumentException("Cookie内容不能为空");
        }

        try
        {
            cookieContent = cookieContent.Trim();
            
            // 确保内容以Netscape格式开头，如果不是则添加
            if (!cookieContent.StartsWith("# Netscape HTTP Cookie File"))
            {
                var builder = new StringBuilder();
                builder.AppendLine("# Netscape HTTP Cookie File");
                builder.Append(cookieContent);
                cookieContent = builder.ToString();
            }

            _cachedCookie = null;  // 清除缓存
            await File.WriteAllTextAsync(_cookieFilePath, cookieContent);
            _logService.Debug($"Cookie已保存到: {_cookieFilePath}");

            // 验证保存的Cookie是否包含必要的字段
            var savedCookie = await LoadCookiesAsync();
            if (string.IsNullOrEmpty(savedCookie))
            {
                throw new Exception("保存的Cookie无效，未包含必需的字段");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"保存Cookie失败: {ex.Message}", ex);
        }
    }

    public async Task<string> LoadCookiesAsync()
    {
        if (!string.IsNullOrEmpty(_cachedCookie))
        {
            return _cachedCookie;
        }

        try
        {
            if (!File.Exists(_cookieFilePath))
            {
                _logService.Warning($"Cookie文件不存在: {_cookieFilePath}");
                return string.Empty;
            }

            // 读取并解析文件
            var lines = await File.ReadAllLinesAsync(_cookieFilePath);
            _logService.Debug($"读取到 {lines.Length} 行数据");
            
            var cookieDict = ParseNetscapeCookieFile(lines);
            
            // 检查是否所有必需的cookie都存在
            if (RequiredCookies.All(cookieDict.ContainsKey))
            {
                _cachedCookie = String.Join("; ", RequiredCookies.Select(name => $"{name}={cookieDict[name]}"));
                _logService.Info($"成功提取Cookie");
                return _cachedCookie;
            }

            var missing = RequiredCookies.Except(cookieDict.Keys);
            _logService.Warning($"缺少必需的Cookie: {string.Join(", ", missing)}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logService.Error("读取Cookie失败", ex);
            return string.Empty;
        }
    }

    public async Task ClearCookiesAsync()
    {
        try
        {
            if (File.Exists(_cookieFilePath))
            {
                File.Delete(_cookieFilePath);  // File.DeleteAsync 不存在，使用同步方法
                _cachedCookie = null;
                _logService.Debug($"Cookie文件已删除: {_cookieFilePath}");
            }
        }
        catch (Exception ex)
        {
            _logService.Error("删除Cookie文件失败", ex);
        }
    }
}