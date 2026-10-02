using System.Net.Http;
using System.Text;
using CommentatorApp.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CommentatorApp.Services;

/// <summary>
/// 用配置里的账号换一个 JWT。
///
/// 单独抽出来是因为解说端现在有两条路要用令牌：WebSocket 握手，以及拉项目名
/// 的 HTTP 请求。留着两份的话，哪天登录参数或错误提示改了，很容易只改一处。
/// </summary>
public static class AuthClient
{
    /// <summary>
    /// 登录。返回 (令牌, 失败原因)，失败原因非空时令牌一定为 null。
    ///
    /// 没配账号时直接返回 (null, null) 且不发请求：这样在没有启用成员校验的
    /// 部署上，解说端的行为与引入登录之前完全一致——现场不会因为少填一个
    /// 字段而起不来。
    /// </summary>
    public static async Task<(string? Token, string? Error)> LoginAsync(
        AppConfig config,
        Action<string>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrEmpty(config.Password))
        {
            return (null, null);
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = JsonConvert.SerializeObject(new
            {
                username = config.Username,
                password = config.Password
            });
            var url = $"{config.ServerUrl.TrimEnd('/')}/api/auth/login";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                // 密码错、账号被停用都会走到这里。一定要透出到界面：否则现场
                // 只看到一直「连接中...」，完全分不清是凭据问题还是网络问题。
                var detail = await resp.Content.ReadAsStringAsync();
                var message = $"登录失败 HTTP {(int)resp.StatusCode}：{detail}";
                onError?.Invoke(message);
                return (null, message);
            }

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return (json["token"]?.ToString(), null);
        }
        catch (Exception ex)
        {
            var message = $"登录异常：{ex.Message}";
            onError?.Invoke(message);
            return (null, message);
        }
    }
}
