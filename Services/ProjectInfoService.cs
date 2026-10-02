using System.Net.Http;
using System.Net.Http.Headers;
using CommentatorApp.Models;
using Newtonsoft.Json.Linq;

namespace CommentatorApp.Services;

/// <summary>
/// 拉取项目名，供标题条顶行显示赛事名。
///
/// 为什么不走 WebSocket：shot_state 里只有机位名（见 <see cref="ShotState"/>），
/// 项目名是一次连接期间基本不变的静态信息，为了它改后端的推送协议不划算。
/// 这里沿用 <see cref="VersionService"/> 的做法——启动时查一次 HTTP。
/// </summary>
public sealed class ProjectInfoService
{
    private readonly AppConfig _config;

    public ProjectInfoService(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// 查当前配置项目的名字。失败时返回 null，调用方退回配置里的兜底值。
    ///
    /// <paramref name="onError"/> 可能从后台线程回调——方法内部一律
    /// ConfigureAwait(false)，不让一次 HTTP 请求把 UI 线程按在原地，
    /// 那会让正在播的切换动画卡住。要动界面的话调用方自己 marshal。
    /// </summary>
    public async Task<string?> FetchProjectNameAsync(Action<string>? onError = null)
    {
        if (_config.ProjectId <= 0) return null;

        try
        {
            var (token, loginError) = await AuthClient.LoginAsync(_config).ConfigureAwait(false);
            if (loginError is not null)
            {
                // 登录失败不当作致命错误：没开成员校验的部署上项目列表本来就能匿名读，
                // 仍然值得一试。只把提示透出去，不放弃这次请求。
                onError?.Invoke(loginError);
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }

            var url = $"{_config.ServerUrl.TrimEnd('/')}/api/projects";
            using var resp = await http.GetAsync(url).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                onError?.Invoke($"查询项目失败 HTTP {(int)resp.StatusCode}：{body}");
                return null;
            }

            // 接口返回的是数组，没有单查端点，所以整份拉下来自己按 id 找。
            foreach (var item in JArray.Parse(body))
            {
                if (item["id"]?.ToObject<int>() != _config.ProjectId) continue;
                var name = item["name"]?.ToString();
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }

            onError?.Invoke($"项目 {_config.ProjectId} 不在服务端列表里");
            return null;
        }
        catch (Exception ex)
        {
            onError?.Invoke($"查询项目异常：{ex.Message}");
            return null;
        }
    }
}
