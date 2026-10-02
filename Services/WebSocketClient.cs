using System.IO;
using System.Net.WebSockets;
using System.Text;
using CommentatorApp.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CommentatorApp.Services;

public class WebSocketClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;
    private Timer? _reconnectTimer;
    private bool _intentionalClose;
    private int _reconnectAttempts;
    private AppConfig _config;

    /// <summary>
    /// 登录换来的 JWT。
    ///
    /// 解说端此前**连登录这个概念都没有**：它在服务端是匿名的，任何人拿到
    /// 那个地址就能监听整个项目的实时消息。后端启用 REQUIRE_PROJECT_MEMBERSHIP
    /// 之后，没有令牌的连接会被直接拒绝，所以凭据必须在那之前就位。
    /// </summary>
    private string? _token;

    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// 收到切台状态。
    ///
    /// 整个 <see cref="ShotState"/> 一起给，而不是拆成「是否待切 / 显示名 / 标签」
    /// 三个位置参数：两个窗口都要读它，拆开之后每个调用点都得自己拼回去，
    /// 而拼接规则（该显示 current 还是 next）一旦两处不一致，标题窗口和状态窗口
    /// 就会显示不同的字。
    /// </summary>
    public event Action<ShotState>? ShotStateReceived;

    /// <summary>收到内部消息（chat）。</summary>
    public event Action<string>? ChatReceived;

    public event Action<string>? SystemMessage;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public WebSocketClient(AppConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync()
    {
        _intentionalClose = false;
        await DoConnectAsync();
    }

    /// <summary>
    /// 用配置里的账号换一个令牌。
    ///
    /// 没配账号时返回 null 且不发请求：这样在没有启用成员校验的部署上，
    /// 解说端的行为与改动前完全一致——现场不会因为少填一个字段而起不来。
    ///
    /// 登录本身搬到 <see cref="AuthClient"/>：拉项目名的 HTTP 请求也要用令牌，
    /// 两处各留一份迟早会只改一处。
    /// </summary>
    private async Task<string?> LoginAsync()
    {
        var (token, error) = await AuthClient.LoginAsync(_config, SystemMessage);
        return error is null ? token : null;
    }

    private async Task DoConnectAsync()
    {
        try
        {
            _cts?.Cancel();
            _ws?.Dispose();

            _ws = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            // 每次重连都重新换令牌：JWT_TTL 默认 60 分钟，长时间运行的解说端
            // 用旧令牌重连会一直失败，而失败原因只表现为「连不上」。
            _token = await LoginAsync();

            var url = $"{_config.WsUrl}?project_id={_config.ProjectId}&role={_config.Role}";
            if (!string.IsNullOrEmpty(_token))
            {
                url += $"&token={Uri.EscapeDataString(_token)}";
            }

            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _reconnectAttempts = 0;
            ConnectionChanged?.Invoke(true);

            _ = ReceiveLoopAsync();
            StartHeartbeat();
        }
        catch
        {
            // 连不上时丢掉令牌，下一轮重新登录。绝大多数失败是令牌过期，
            // 拿着同一个过期令牌重试是白费。
            _token = null;
            ConnectionChanged?.Invoke(false);
            ScheduleReconnect();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts?.Token.IsCancellationRequested == true)
            {
                // 一条 WS 消息可能被拆成多帧，必须按 EndOfMessage 拼完再解析，
                // 否则 JSON 会被截断，shot_state 这类消息会被静默丢掉。
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts!.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConnectionChanged?.Invoke(false);
                        if (!_intentionalClose) ScheduleReconnect();
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        frame.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (frame.Length > 0)
                {
                    HandleMessage(Encoding.UTF8.GetString(frame.ToArray()));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            ConnectionChanged?.Invoke(false);
            if (!_intentionalClose) ScheduleReconnect();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString() ?? "";
            var payload = msg["payload"] as JObject;

            switch (type)
            {
                case "shot_state":
                    // 导播端每次切台都下发完整状态：current=当前播送，next=即将切台。
                    ShotStateReceived?.Invoke(payload?.ToObject<ShotState>() ?? new ShotState());
                    break;
                case "chat":
                    var chatMsg = payload?["message"]?.ToString() ?? "";
                    // 心跳只是保活信号，后端不会再转发，这里只是兜底跳过。
                    if (chatMsg == "heartbeat") break;
                    ChatReceived?.Invoke(chatMsg);
                    break;
                case "system":
                    // 连接与断线重连时，后端会把项目当前的切台状态放进欢迎消息。
                    //
                    // 没有这一步的话，中途连上来的解说端会一直停在「等待导播指令」，
                    // 直到下一次切台——而这中间的解说内容已经和画面脱节了。
                    ApplyWelcomeState(payload);
                    SystemMessage?.Invoke(payload?["message"]?.ToString()
                        ?? payload?["error"]?.ToString()
                        ?? "");
                    break;
            }
        }
        catch { }
    }

    /// <summary>
    /// 渲染欢迎消息里带回来的当前切台状态。
    /// </summary>
    private void ApplyWelcomeState(JObject? payload)
    {
        if (payload == null) return;
        if (payload["state_available"]?.Value<bool>() != true) return;

        var state = new ShotState
        {
            Current = payload["current_shot"]?.ToString() ?? "",
            Next = payload["next_shot"]?.ToString() ?? ""
        };

        // 没有当前机位时不要冒充成一次切台，交给界面继续显示「等待导播指令」。
        if (string.IsNullOrEmpty(state.Current) && string.IsNullOrEmpty(state.Next)) return;

        ShotStateReceived?.Invoke(state);
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(_ =>
        {
            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    var msg = JsonConvert.SerializeObject(new
                    {
                        type = "chat",
                        project_id = _config.ProjectId,
                        payload = new
                        {
                            message = "heartbeat",
                            // 时间戳供服务端刷新「最后一次见到这个客户端」的时刻，
                            // 掉线扫描判断的就是它。
                            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }
                    });
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    _ws?.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private void ScheduleReconnect()
    {
        _reconnectAttempts++;
        var delay = TimeSpan.FromSeconds(Math.Min(1 * (1 << (_reconnectAttempts - 1)), 10));
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(async _ => await DoConnectAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    public void Disconnect()
    {
        _intentionalClose = true;
        _heartbeatTimer?.Dispose();
        _reconnectTimer?.Dispose();
        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        ConnectionChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Disconnect();
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
