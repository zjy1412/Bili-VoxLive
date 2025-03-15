namespace BiliVoxLive;

using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Text;
using System.Security;  // 添加这行
using BiliVoxLive.Models;
using BiliVoxLive.Windows;
using System.Security.Cryptography;

public interface IBiliApiService
{
    Task<IEnumerable<LiveRoom>> GetFollowedLiveRoomsAsync();
    Task<QrCodeResponse> GetQrCodeAsync();  // 添加这行
    Task<QrCodeStatus> CheckQrCodeStatusAsync(string qrKey);  // 添加这行
    Task<UserInfo> GetUserInfoAsync();
    Task<List<string>> GetSearchSuggestionsAsync(string keyword);
    // 更新接口方法的返回类型
    Task<(List<RoomOption> Results, int TotalPages)> SearchLiveRoomsAsync(string keyword, int page = 1);
}

public class BiliApiService : IBiliApiService
{
    private const string LiveApiHost = "https://api.live.bilibili.com";
    public const string HttpHeaderAccept = "application/json, text/javascript, */*; q=0.01";
    public const string HttpHeaderAcceptLanguage = "zh-CN";
    public const string HttpHeaderReferer = "https://live.bilibili.com/";
    public const string HttpHeaderOrigin = "https://live.bilibili.com";
    public const string HttpHeaderUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36";

    private readonly CookieService _cookieService;
    private readonly LogService _logService;
    private HttpClient _httpClient;

    // 添加字段来缓存表情包
    private List<EmoticonPackage>? _emoticonPackages;

    // 添加手机号字段
    private string _phoneNumber = "";

    public BiliApiService(CookieService cookieService, LogService logService)
    {
        _cookieService = cookieService;
        _logService = logService;
        _httpClient = new HttpClient();
    }

    private void InitializeHttpClient()
    {
        // 先清理旧的 HttpClient
        _httpClient?.Dispose();

        var handler = new HttpClientHandler
        {
            UseCookies = false,
            UseDefaultCredentials = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true  // 添加此行
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var headers = _httpClient.DefaultRequestHeaders;
        headers.Clear();
        headers.Add("Accept", HttpHeaderAccept);
        headers.Add("Accept-Language", HttpHeaderAcceptLanguage);
        headers.Add("Origin", HttpHeaderOrigin);
        headers.Add("Referer", HttpHeaderReferer);
        headers.Add("User-Agent", HttpHeaderUserAgent);

        // 确保Cookie每次都重新获取
        var cookie = _cookieService.GetCookie();
        _logService.Debug($"当前使用的Cookie: {cookie}");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            if (_httpClient.DefaultRequestHeaders.Contains("Cookie"))
            {
                _httpClient.DefaultRequestHeaders.Remove("Cookie");
            }
            _httpClient.DefaultRequestHeaders.Add("Cookie", cookie);
        }
    }

    public async Task<bool> LoginAsync(string cookieStr)
    {
        try
        {
            _logService.Info("开始登录流程...");

            // 首先尝试保存Cookie
            await _cookieService.SaveCookiesAsync(cookieStr);
            _logService.Info("Cookie已保存");

            // 初始化新的HttpClient
            InitializeHttpClient();

            // 验证登录状态
            var response = await _httpClient.GetAsync("https://api.bilibili.com/x/web-interface/nav");
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);
            
            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString() ?? "未知错误");
            }

            var data = json.RootElement.GetProperty("data");
            var isLogin = data.GetProperty("isLogin").GetBoolean();
            if (!isLogin)
            {
                throw new Exception("Cookie无效或已过期");
            }

            var uname = data.GetProperty("uname").GetString();
            _logService.Info($"登录成功: {uname}");
            return true;
        }
        catch (Exception ex)
        {
            _logService.Error($"登录失败: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GetLiveStreamUrlAsync(long roomId)
    {
        try
        {
            // 确保使用最新的 Cookie
            InitializeHttpClient();

            var url = $"{LiveApiHost}/xlive/web-room/v2/index/getRoomPlayInfo?" +
                     $"room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&" +
                     $"platform=web&ptype=8";
            
            var response = await _httpClient.GetStringAsync(url);
            _logService.Debug($"直播流API返回: {response}");

            using var jsonDoc = JsonDocument.Parse(response);
            var root = jsonDoc.RootElement;

            if (root.GetProperty("code").GetInt32() == 0)
            {
                var streams = root.GetProperty("data")
                    .GetProperty("playurl_info")
                    .GetProperty("playurl")
                    .GetProperty("stream");

                // 只获取 FLV 格式的流
                var flvStream = streams.EnumerateArray()
                    .FirstOrDefault(s => s.GetProperty("protocol_name").GetString() == "http_stream");

                if (flvStream.ValueKind != JsonValueKind.Undefined)
                {
                    var formats = flvStream.GetProperty("format").EnumerateArray()
                        .FirstOrDefault(f => f.GetProperty("format_name").GetString() == "flv");

                    if (formats.ValueKind != JsonValueKind.Undefined)
                    {
                        var codec = formats.GetProperty("codec").EnumerateArray()
                            .FirstOrDefault(c => c.GetProperty("codec_name").GetString() == "avc");

                        if (codec.ValueKind != JsonValueKind.Undefined)
                        {
                            var urls = codec.GetProperty("url_info");
                            if (urls.GetArrayLength() > 0)
                            {
                                var baseUrl = codec.GetProperty("base_url").GetString();
                                var urlInfo = urls[0];
                                var host = urlInfo.GetProperty("host").GetString();
                                var extra = urlInfo.GetProperty("extra").GetString();
                                
                                var fullUrl = $"{host}{baseUrl}{extra}";
                                _logService.Info($"已选择FLV直播流: {fullUrl}");
                                return fullUrl;
                            }
                        }
                    }
                }
            }
            
            throw new Exception($"未找到可用的直播流：{root.GetProperty("message").GetString()}");
        }
        catch (Exception ex)
        {
            _logService.Error($"获取直播流地址失败: {ex.Message}");
            throw;
        }
    }

    private async Task<long> GetRealRoomIdAsync(long roomId)
    {
        var url = $"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}";
        var response = await _httpClient.GetAsync(url);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();

        if (json == null) throw new Exception("Failed to get real room id");

        return json.RootElement
            .GetProperty("data")
            .GetProperty("room_id")
            .GetInt64();
    }

    public async Task<IEnumerable<LiveRoom>> GetFollowedLiveRoomsAsync()
    {
        try
        {
            var url = "https://api.live.bilibili.com/xlive/web-ucenter/v1/xfetter/GetWebList?page=1&page_size=10";
            Console.WriteLine($"Requesting: {url}");
            
            var response = await _httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Response: {responseText}");
            
            var json = JsonDocument.Parse(responseText);
            var code = json.RootElement.GetProperty("code").GetInt32();
            
            if (code != 0)
            {
                var message = json.RootElement.GetProperty("message").GetString();
                throw new Exception($"API返回错误: {message}");
            }
            
            var rooms = new List<LiveRoom>();
            var list = json.RootElement.GetProperty("data").GetProperty("list");
            
            foreach (var item in list.EnumerateArray())
            {
                rooms.Add(new LiveRoom
                {
                    RoomId = item.GetProperty("room_id").GetInt64(),
                    Title = item.GetProperty("title").GetString() ?? "",
                    HostName = item.GetProperty("uname").GetString() ?? "",
                    IsLiving = item.GetProperty("live_status").GetInt32() == 1
                });
            }
            
            Console.WriteLine($"Found {rooms.Count} rooms");
            return rooms;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取直播间列表失败: {ex}");
            throw new Exception($"获取直播间列表失败: {ex.Message}", ex);
        }
    }

    public async Task<LiveRoom> GetLiveRoomInfoAsync(long roomId)
    {
        var url = $"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}";
        var response = await _httpClient.GetAsync(url);
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();

        if (json == null) throw new Exception("Failed to get room info");

        var data = json.RootElement.GetProperty("data");
        return new LiveRoom
        {
            RoomId = roomId,
            Title = data.GetProperty("title").GetString() ?? "",
            HostName = data.GetProperty("uname").GetString() ?? "",
            IsLiving = data.GetProperty("live_status").GetInt32() == 1
        };
    }

    public async Task SendDanmakuAsync(long roomId, string content)
    {
        try
        {
            var cookie = _cookieService.GetCookie();
            var csrf = Regex.Match(cookie, @"bili_jct=([^;]+)").Groups[1].Value;
            
            if (string.IsNullOrEmpty(csrf))
            {
                throw new Exception("Cookie中未找到bili_jct令牌");
            }

            // 检查是否是表情弹幕
            bool isEmoticon = content.StartsWith("[") && content.EndsWith("]");
            _logService.Debug($"发送的弹幕是否是表情: {isEmoticon}");

            // 准备发送内容
            string sendContent = content;
            if (isEmoticon && _emoticonPackages != null)
            {
                var emoteName = content.Trim('[', ']');
                var emoticon = _emoticonPackages
                    .SelectMany(p => p.Emoticons)
                    .FirstOrDefault(e => e.Text == content || e.Name == emoteName);

                if (emoticon != null && !string.IsNullOrEmpty(emoticon.emoticon_unique))
                {
                    sendContent = $"{emoticon.emoticon_unique}";
                    _logService.Debug($"替换表情标识符: {content} -> {sendContent}");
                }
                else
                {
                    _logService.Warning($"未找到表情或表情标识符: {content}");
                }
            }

            // 准备基础参数
            var formParams = new List<KeyValuePair<string, string>>
            {
                new("roomid", roomId.ToString()),
                new("msg", sendContent),
                new("color", "16777215"),
                new("mode", "1"),
                new("fontsize", "25"),
                new("rnd", DateTimeOffset.Now.ToUnixTimeSeconds().ToString()),
                new("csrf", csrf),
                new("csrf_token", csrf)
            };

            // 如果是表情弹幕，添加额外参数
            if (isEmoticon)
            {
                formParams.Add(new("bubble", "0"));
                formParams.Add(new("dm_type", "1"));
            }

            var url = "https://api.live.bilibili.com/msg/send";
            InitializeHttpClient();

            var data = new FormUrlEncodedContent(formParams);
            var response = await _httpClient.PostAsync(url, data);
            var responseContent = await response.Content.ReadAsStringAsync();
            _logService.Debug($"发送弹幕API返回: {responseContent}");
            
            var result = JsonDocument.Parse(responseContent);
            
            if (result.RootElement.GetProperty("code").GetInt32() != 0)
            {
                var msg = result.RootElement.GetProperty("message").GetString();
                throw new Exception($"发送弹幕失败: {msg}");
            }

            _logService.Info($"成功发送弹幕到房间 {roomId}: {content}");
        }
        catch (Exception ex)
        {
            _logService.Error($"发送弹幕失败: {ex.Message}");
            throw;
        }
    }

    public async Task<List<EmoticonPackage>> GetEmoticons(long roomId)
    {
        try
        {
            // 先初始化带Cookie的HttpClient
            InitializeHttpClient();
            
            // 获取真实房间ID
            var realRoomId = await GetRealRoomIdAsync(roomId);
            
            // 使用正确的API endpoint和参数
            var url = $"{LiveApiHost}/xlive/web-ucenter/v2/emoticon/GetEmoticons" +
                     $"?platform=pc&room_id={realRoomId}";
                     
            _logService.Debug($"请求表情包API: {url}");
            _logService.Debug($"使用Cookie: {_cookieService.GetCookie()}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            _logService.Debug($"表情API返回: {content}");
            
            var json = JsonDocument.Parse(content);
            
            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString());
            }

            var packages = new List<EmoticonPackage>();
            var data = json.RootElement.GetProperty("data").GetProperty("data");
            
            foreach (var package in data.EnumerateArray())
            {
                var emoticonPackage = new EmoticonPackage
                {
                    Id = package.GetProperty("pkg_id").GetInt32(),
                    Name = package.GetProperty("pkg_name").GetString() ?? string.Empty,
                    Type = package.GetProperty("pkg_type").GetInt32().ToString(),
                    Emoticons = new List<Emoticon>()
                };

                foreach (var emoticon in package.GetProperty("emoticons").EnumerateArray())
                {
                    var emoji = emoticon.GetProperty("emoji").GetString() ?? string.Empty;
                    var text = emoji.StartsWith("[") ? emoji : $"[{emoji}]";
                    
                    // 获取 emoticon_unique
                    var emoticonUnique = emoticon.GetProperty("emoticon_unique").GetString() ?? string.Empty;
                    _logService.Debug($"表情标识符: {text} -> {emoticonUnique}");

                    emoticonPackage.Emoticons.Add(new Emoticon
                    {
                        Id = emoticon.TryGetProperty("emoticon_id", out var id) ? id.GetInt32() : 0,
                        Name = emoji,
                        Text = text,
                        Description = emoticon.GetProperty("descript").GetString() ?? emoji,
                        Package = emoticonPackage.Name,
                        emoticon_unique = emoticonUnique  // 使用小写属性名
                    });
                }

                // 只添加有表情的包
                if (emoticonPackage.Emoticons.Any())
                {
                    packages.Add(emoticonPackage);
                }
            }

            // 移除skip(2)，因为我们现在要显示所有表情包
            _logService.Info($"成功获取 {packages.Count} 个表情包，共 {packages.Sum(p => p.Emoticons.Count)} 个表情");
            _emoticonPackages = packages; // 缓存表情包
            return packages;
        }
        catch (Exception ex)
        {
            _logService.Error($"获取表情包失败: {ex.Message}");
            throw;
        }
    }

    // 获取用户的粉丝勋章列表
    public async Task<List<FanMedal>> GetFanMedalsAsync()
    {
        try
        {
            InitializeHttpClient();
            var uid = await GetUidAsync();
            
            var url = $"{LiveApiHost}/xlive/web-ucenter/user/MedalWall" +
                     $"?target_id={uid}";
                     
            _logService.Debug($"请求粉丝勋章API: {url}");
            
            var response = await _httpClient.GetStringAsync(url);
            _logService.Debug($"粉丝勋章API返回: {response}");
            
            var json = JsonDocument.Parse(response);

            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString());
            }

            var medals = new List<FanMedal>();
            
            // 修正路径：data.list
            if (json.RootElement.TryGetProperty("data", out var data) && 
                data.TryGetProperty("list", out var list))
            {
                foreach (var item in list.EnumerateArray())
                {
                    try
                    {
                        // 获取 medal_info 对象
                        var medalInfo = item.GetProperty("medal_info");
                        
                        medals.Add(new FanMedal
                        {
                            MedalId = medalInfo.GetProperty("medal_id").GetInt32(),
                            Level = medalInfo.GetProperty("level").GetInt32(),
                            MedalName = medalInfo.GetProperty("medal_name").GetString() ?? "",
                            UpName = item.GetProperty("target_name").GetString() ?? "",
                            RoomId = medalInfo.GetProperty("target_id").GetInt64(),
                            IsWearing = medalInfo.GetProperty("wearing_status").GetInt32() == 1
                        });
                    }
                    catch (Exception ex)
                    {
                        _logService.Warning($"解析单个粉丝勋章数据失败: {ex.Message}");
                        continue;
                    }
                }
            }

            _logService.Info($"获取到 {medals.Count} 个粉丝勋章");
            return medals;
        }
        catch (Exception ex)
        {
            _logService.Error($"获取粉丝勋章失败: {ex.Message}");
            throw;
        }
    }

    // 切换佩戴的粉丝勋章
    public async Task WearMedalAsync(int medalId)
    {
        try
        {
            InitializeHttpClient();
            var cookie = _cookieService.GetCookie();
            var csrf = Regex.Match(cookie, @"bili_jct=([^;]+)").Groups[1].Value;

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("medal_id", medalId.ToString()),
                new KeyValuePair<string, string>("csrf", csrf),
                new KeyValuePair<string, string>("csrf_token", csrf)
            });

            var url = "https://api.live.bilibili.com/xlive/web-room/v1/fansMedal/wear";
            var response = await _httpClient.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(responseText);

            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString());
            }

            _logService.Info($"成功切换粉丝勋章 {medalId}");
        }
        catch (Exception ex)
        {
            _logService.Error($"切换粉丝勋章失败", ex);
            throw;
        }
    }

    private async Task<string> GetUidAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("https://api.bilibili.com/x/web-interface/nav");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
            
            if (json == null) throw new Exception("Failed to get user info");
            
            // 修改这里：将数字类型转换为字符串
            return json.RootElement
                .GetProperty("data")
                .GetProperty("mid")
                .GetInt64()  // 使用 GetInt64() 而不是 GetString()
                .ToString();
        }
        catch (Exception ex)
        {
            _logService.Error($"获取用户ID失败: {ex.Message}");
            throw;
        }
    }

    private string CalculateSign(IEnumerable<KeyValuePair<string, string>> parameters, string appSecret)
    {
        // 按照参数名升序排序
        var orderedParams = parameters.OrderBy(p => p.Key);
        var stringBuilder = new StringBuilder();
        
        // 构建待签名字符串
        foreach (var param in orderedParams)
        {
            if (stringBuilder.Length > 0) stringBuilder.Append('&');
            stringBuilder.Append($"{param.Key}={param.Value}");
        }
        
        // 添加 AppSecret
        stringBuilder.Append(appSecret);
        
        // 计算 MD5
        using var md5 = System.Security.Cryptography.MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
        var hashBytes = md5.ComputeHash(inputBytes);
        
        // 转换为小写的十六进制字符串
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    }

    // 添加一个重载方法，接受字典类型参数
    private string CalculateSign(Dictionary<string, string> parameters, string appSecret)
    {
        var orderedParams = parameters.OrderBy(p => p.Key);
        var stringBuilder = new StringBuilder();
        
        foreach (var param in orderedParams)
        {
            if (stringBuilder.Length > 0) stringBuilder.Append('&');
            stringBuilder.Append($"{param.Key}={param.Value}");
        }
        
        stringBuilder.Append(appSecret);
        
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(stringBuilder.ToString());
        var hashBytes = md5.ComputeHash(inputBytes);
        
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
    }

    public async Task<QrCodeResponse> GetQrCodeAsync()
    {
        const string appKey = "4409e2ce8ffd12b8";     // TV 端 appkey
        const string appSecret = "59b43e04ad6965f34319062b478f83dd";
        const string url = "http://passport.bilibili.com/x/passport-tv-login/qrcode/auth_code";
        
        var parameters = new Dictionary<string, string>
        {
            ["appkey"] = appKey,
            ["local_id"] = "0",
            ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        
        // 计算签名
        var sign = CalculateSign(parameters, appSecret);
        parameters.Add("sign", sign);
        
        var content = new FormUrlEncodedContent(parameters);
        
        try 
        {
            using var response = await _httpClient.PostAsync(url, content);
            var jsonContent = await response.Content.ReadAsStringAsync();
            _logService.Debug($"获取二维码返回: {jsonContent}");
            
            var json = JsonDocument.Parse(jsonContent);
            var code = json.RootElement.GetProperty("code").GetInt32();
            
            if (code != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString() ?? "获取二维码失败");
            }
            
            var data = json.RootElement.GetProperty("data");
            return new QrCodeResponse
            {
                Url = data.GetProperty("url").GetString() ?? "",
                QrKey = data.GetProperty("auth_code").GetString() ?? ""
            };
        }
        catch (Exception ex)
        {
            _logService.Error($"获取二维码失败: {ex.Message}");
            throw;
        }
    }

    public async Task<QrCodeStatus> CheckQrCodeStatusAsync(string qrKey)
    {
        const string appKey = "4409e2ce8ffd12b8";
        const string appSecret = "59b43e04ad6965f34319062b478f83dd";
        const string url = "http://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
        
        var parameters = new Dictionary<string, string>
        {
            ["appkey"] = appKey,
            ["auth_code"] = qrKey,
            ["local_id"] = "0",
            ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        
        var sign = CalculateSign(parameters, appSecret);
        parameters.Add("sign", sign);
        
        var content = new FormUrlEncodedContent(parameters);
        
        try
        {
            using var response = await _httpClient.PostAsync(url, content);
            var jsonContent = await response.Content.ReadAsStringAsync();
            _logService.Debug($"QR状态检查返回: {jsonContent}");
            
            var json = JsonDocument.Parse(jsonContent);
            var code = json.RootElement.GetProperty("code").GetInt32();

            string message = code switch
            {
                0 => "登录成功",
                86039 => "等待确认",
                86038 => "等待扫码",
                86090 => "二维码已失效",
                _ => json.RootElement.GetProperty("message").GetString() ?? "未知状态"
            };

            if (code == 0)
            {
                var data = json.RootElement.GetProperty("data");
                var cookies = data.GetProperty("cookie_info").GetProperty("cookies");
                
                var cookieBuilder = new StringBuilder();
                cookieBuilder.AppendLine("# Netscape HTTP Cookie File");
                var timestamp = DateTimeOffset.Now.AddDays(30).ToUnixTimeSeconds();

                foreach (var cookie in cookies.EnumerateArray())
                {
                    var name = cookie.GetProperty("name").GetString();
                    var value = cookie.GetProperty("value").GetString();
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))  // 修复拼写错误
                    {
                        cookieBuilder.AppendLine($".bilibili.com\tTRUE\t/\tFALSE\t{timestamp}\t{name}\t{value}");
                    }
                }

                var cookieContent = cookieBuilder.ToString();
                _logService.Debug($"生成的Cookie内容: {cookieContent}");
                await _cookieService.SaveCookiesAsync(cookieContent);
                
                // 重要：保存Cookie后立即重新初始化HttpClient
                InitializeHttpClient();
            }

            return new QrCodeStatus { Code = code, Message = message };
        }
        catch (Exception ex)
        {
            _logService.Error($"检查二维码状态失败: {ex.Message}");
            throw;
        }
    }

    public async Task<SmsResult> SendSmsWithCaptchaAsync(string deviceId, string timestamp, string phoneNumber, string challenge = "", string validate = "", string seccode = "")
    {
        try
        {
            _phoneNumber = phoneNumber;
            var parameters = new Dictionary<string, string>
            {
                ["actionKey"] = "appkey",
                ["appkey"] = "783bbb7264451d82",
                ["build"] = "6510400",
                ["channel"] = "bili",
                ["cid"] = "86",
                ["device"] = "phone",
                ["mobi_app"] = "android",
                ["platform"] = "android",
                ["tel"] = phoneNumber,
                ["ts"] = timestamp,
                ["buvid"] = deviceId
            };

            if (!string.IsNullOrEmpty(challenge))
            {
                _logService.Debug($"添加验证参数 - challenge: {challenge}, validate: {validate}");
                parameters.Add("geetest_challenge", challenge);
                parameters.Add("geetest_validate", validate);
                parameters.Add("geetest_seccode", $"{validate}|jordan");
            }

            var sign = CalculateSign(parameters, "2653583c8873dea268ab9386918b1d65");
            parameters.Add("sign", sign);

            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync(
                "https://passport.bilibili.com/x/passport-login/sms/send",
                content
            );

            var responseText = await response.Content.ReadAsStringAsync();
            _logService.Debug($"发送短信API返回: {responseText}");

            var json = JsonDocument.Parse(responseText);
            var code = json.RootElement.GetProperty("code").GetInt32();

            if (code == 0)
            {
                var data = json.RootElement.GetProperty("data");
                return new SmsResult
                {
                    Success = true,
                    Message = "验证码已发送",
                    CaptchaKey = data.GetProperty("captcha_key").GetString() ?? ""
                };
            }
            else if (code == 86207)
            {
                // 需要人机验证
                var recaptchaUrl = json.RootElement
                    .GetProperty("data")
                    .GetProperty("url")
                    .GetString() ?? "";

                return new SmsResult
                {
                    Success = false,
                    Message = "需要进行人机验证",
                    RecaptchaUrl = recaptchaUrl
                };
            }
            else
            {
                return new SmsResult
                {
                    Success = false,
                    Message = json.RootElement.GetProperty("message").GetString() ?? "发送失败"
                };
            }
        }
        catch (Exception ex)
        {
            _logService.Error("发送短信验证码失败", ex);
            return new SmsResult
            {
                Success = false,
                Message = $"发送失败: {ex.Message}"
            };
        }
    }

    public async Task<LoginResult> VerifySmsCodeAsync(string smsCode, string captchaKey)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["appkey"] = "783bbb7264451d82",
                ["build"] = "6510400",
                ["channel"] = "bili",
                ["cid"] = "86",
                ["code"] = smsCode,
                ["device"] = "phone",
                ["mobi_app"] = "android",
                ["platform"] = "android",
                ["tel"] = _phoneNumber,
                ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(),
                ["captcha_key"] = captchaKey
            };

            var sign = CalculateSign(parameters, "2653583c8873dea268ab9386918b1d65");
            parameters.Add("sign", sign);

            InitializeHttpClient();
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", HttpHeaderUserAgent);
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.bilibili.com");
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
            
            var response = await _httpClient.PostAsync(
                "https://passport.bilibili.com/x/passport-login/web/login/sms",
                new FormUrlEncodedContent(parameters)
            );

            var responseText = await response.Content.ReadAsStringAsync();
            _logService.Debug($"短信登录API返回: {responseText}");
            
            var json = JsonDocument.Parse(responseText);
            var code = json.RootElement.GetProperty("code").GetInt32();
            var message = json.RootElement.GetProperty("message").GetString() ?? "未知错误";

            if (code == 0)
            {
                var data = json.RootElement.GetProperty("data");
                if (data.TryGetProperty("cookie_info", out var cookieInfo))
                {
                    var cookies = cookieInfo.GetProperty("cookies");
                    var cookieBuilder = new StringBuilder();
                    cookieBuilder.AppendLine("# Netscape HTTP Cookie File");

                    foreach (var cookie in cookies.EnumerateArray())
                    {
                        var name = cookie.GetProperty("name").GetString();
                        var value = cookie.GetProperty("value").GetString();
                        var expires = cookie.GetProperty("expires").GetInt64();
                        
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                        {
                            cookieBuilder.AppendLine($".bilibili.com\tTRUE\t/\tFALSE\t{expires}\t{name}\t{value}");
                        }
                    }

                    var cookieContent = cookieBuilder.ToString();
                    await _cookieService.SaveCookiesAsync(cookieContent);
                    
                    return new LoginResult 
                    { 
                        Success = true, 
                        Message = "登录成功",
                        Cookie = cookieContent
                    };
                }
            }

            // API 请求成功但登录失败的情况
            return new LoginResult 
            { 
                Success = false, 
                Message = message 
            };
        }
        catch (Exception ex)
        {
            _logService.Error("验证短信验证码失败", ex);
            // 异常情况的返回值
            return new LoginResult 
            { 
                Success = false, 
                Message = ex.Message 
            };
        }
    }

    public async Task<UserInfo> GetUserInfoAsync()
    {
        try
        {
            InitializeHttpClient();
            var response = await _httpClient.GetAsync("https://api.bilibili.com/x/web-interface/nav");
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            _logService.Debug($"获取用户信息返回: {content}");
            
            var json = JsonDocument.Parse(content);
            
            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString());
            }

            var data = json.RootElement.GetProperty("data");
            var isLogin = data.GetProperty("isLogin").GetBoolean();
            var uname = data.GetProperty("uname").GetString();

            _logService.Info($"获取用户信息成功: isLogin={isLogin}, uname={uname}");
            
            return new UserInfo
            {
                isLogin = isLogin,
                uname = uname
            };
        }
        catch (Exception ex)
        {
            _logService.Error($"获取账户信息失败: {ex.Message}");
            throw;
        }
    }

    public async Task<List<string>> GetSearchSuggestionsAsync(string keyword)
    {
        try
        {
            var url = $"https://s.search.bilibili.com/main/suggest?term={Uri.EscapeDataString(keyword)}&main_ver=v1";
            var response = await _httpClient.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            var suggestions = new List<string>();
            var tag = json.RootElement.GetProperty("result").GetProperty("tag");

            foreach (var item in tag.EnumerateArray())
            {
                var value = item.GetProperty("value").GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    suggestions.Add(value);
                }
            }

            return suggestions;
        }
        catch (Exception ex)
        {
            _logService.Error("获取搜索建议失败", ex);
            return new List<string>();
        }
    }

    public async Task<(List<RoomOption> Results, int TotalPages)> SearchLiveRoomsAsync(string keyword, int page = 1)
    {
        try
        {
            InitializeHttpClient();
            
            var url = $"https://api.bilibili.com/x/web-interface/search/type?search_type=live_room&keyword={Uri.EscapeDataString(keyword)}&page={page}&order=online";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            if (json.RootElement.GetProperty("code").GetInt32() != 0)
            {
                throw new Exception(json.RootElement.GetProperty("message").GetString());
            }

            var results = new List<RoomOption>();
            var data = json.RootElement.GetProperty("data");
            var result = data.GetProperty("result");
            
            // 获取总页数
            int totalPages = data.GetProperty("numPages").GetInt32();

            // 处理搜索结果
            foreach (var item in result.EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? "";
                var uname = item.GetProperty("uname").GetString() ?? "";
                
                // 清理HTML标签
                title = System.Text.RegularExpressions.Regex.Replace(title, @"<[^>]+>", "");
                uname = System.Text.RegularExpressions.Regex.Replace(uname, @"<[^>]+>", "");
                
                results.Add(new RoomOption
                {
                    RoomId = item.GetProperty("roomid").GetInt64(),
                    Title = title,
                    HostName = uname,
                    Status = item.GetProperty("live_status").GetInt32() == 1 ? "🔴" : "⭕",
                    IsLiving = item.GetProperty("live_status").GetInt32() == 1,
                    DisplayText = $"{(item.GetProperty("live_status").GetInt32() == 1 ? "🔴" : "⭕")} {title} ({uname})"
                });
            }

            return (results, totalPages);
        }
        catch (Exception ex)
        {
            _logService.Error("搜索直播间失败", ex);
            throw;
        }
    }

}

public record QrCodeResponse  // 将记录类型移到更合适的位置
{
    public string Url { get; init; } = "";
    public string QrKey { get; init; } = "";
}

public record QrCodeStatus  // 将记录类型移到更合适的位置
{
    public int Code { get; init; }
    public string Message { get; init; } = "";
}

public record LiveRoom
{
    public long RoomId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public bool IsLiving { get; set; }
}

// API 响应数据模型
public class BiliApiResponse
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public LiveStreamData? Data { get; set; }
}

public class LiveStreamData
{
    public StreamUrl[] Durl { get; set; } = Array.Empty<StreamUrl>();
}

public class StreamUrl
{
    public string Url { get; set; } = string.Empty;
}

// 添加粉丝勋章模型类
public class FanMedal
{
    public int MedalId { get; set; }
    public int Level { get; set; }
    public string MedalName { get; set; } = "";
    public string UpName { get; set; } = "";
    public long RoomId { get; set; }
    public bool IsWearing { get; set; }

    public override string ToString()
    {
        return $"[{MedalName} {Level}] {UpName}";
    }
}

public class SmsResult 
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string CaptchaKey { get; set; } = "";
    public string RecaptchaUrl { get; set; } = "";  // 添加滑动验证URL
}

public class LoginResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Cookie { get; set; } = "";
}

public class UserInfo
{
    public bool isLogin { get; set; }
    public string? uname { get; set; }
}