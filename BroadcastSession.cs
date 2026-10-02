using System.Windows;
using System.Windows.Threading;
using CommentatorApp.Models;
using CommentatorApp.Services;

namespace CommentatorApp;

/// <summary>
/// 一次解说会话：连接、切台状态、赛事名、版本检查的持有者。
///
/// 为什么要有这一层：解说端现在有两个窗口（标题窗口 + 状态窗口），它们看的是
/// 同一份连接。让两个窗口各开一条 WebSocket 会导致两边状态可能不一致——
/// 标题窗口显示「即将播送」而状态窗口显示「正在播送」，解说员无法判断该信
/// 哪个，而这种矛盾在直播里是致命的。
///
/// 顺带解决了关闭顺序：标题窗口关掉时状态窗口还在跑，断线重连会一直重试，
/// 所以连接归 App 管，两个窗口只是订阅者。
/// </summary>
public sealed class BroadcastSession : IDisposable
{
    private readonly AppConfig _config;
    private readonly Dispatcher _dispatcher;
    private WebSocketClient? _wsClient;
    private VersionService? _versionService;

    /// <summary>连接状态变化，参数为是否已连上。</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>切台状态变化。整个 <see cref="ShotState"/> 一起给，而不是拆成几个字符串——调用点自己挑要显示的字，免得解约与动画的判断散在两处。</summary>
    public event Action<ShotState>? ShotStateChanged;

    /// <summary>系统/内部消息，透到状态窗口的日志行。</summary>
    public event Action<string>? SystemMessage;

    /// <summary>赛事名。查出来前是 null，两个窗口都先按「没有顶行」渲染。</summary>
    public string? EventName { get; private set; }

    /// <summary>
    /// 赛事名查到后触发一次。
    ///
    /// 这一层不能省：<see cref="EventName"/> 是在 HTTP 请求回来之后才被赋值的，
    /// 而窗口早在 <c>App.OnStartup</c> 里就 attach 完并 Show 了，那会儿值还是
    /// null。窗口若只在 Loaded 时读一次属性，服务端查到的项目名就永远上不了屏，
    /// 顶行一直显示配置里的兜底值——现场看着「有名字、也能用」，实际上从来没
    /// 真的用上服务端那份，改了项目名也不会跟着变。
    /// </summary>
    public event Action<string>? EventNameChanged;

    /// <summary>版本检查结果，交给状态窗口决定要不要亮横幅。</summary>
    public event Action<AppVersion.VersionCheck>? VersionChecked;

    public BroadcastSession(AppConfig config)
    {
        _config = config;
        _dispatcher = Application.Current.Dispatcher;
    }

    /// <summary>
    /// 在 UI 线程上触发事件。
    ///
    /// 这一层是必须的：切台状态来自 WebSocket 的接收循环，那是后台线程。窗口的
    /// 订阅者直接改控件会抛「跨线程访问」，而且因为它发生在工作线程上、外面又
    /// 没有 try，**整个解说端会直接崩掉**——现场表现就是切一次台程序就没了。
    ///
    /// 放在会话这一层而不是让每个窗口自己 marshal，是因为两个窗口都要做一遍，
    /// 漏掉一个就是同样的崩溃在另一个窗口复现。
    /// </summary>
    private void Raise(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public async Task StartAsync()
    {
        await ConnectAsync();
        StartVersionWatch();
        await LoadEventNameAsync();
    }

    private async Task ConnectAsync()
    {
        _wsClient?.Dispose();
        _wsClient = new WebSocketClient(_config);

        _wsClient.ConnectionChanged += connected => Raise(() => ConnectionChanged?.Invoke(connected));

        // 切台状态：后端每次都同时给出「当前播送」和「即将切台」，
        // 这里直接照着转发，不做任何本地推断。
        _wsClient.ShotStateReceived += state => Raise(() => ShotStateChanged?.Invoke(state));

        _wsClient.ChatReceived += msg => Raise(() => SystemMessage?.Invoke($"[内部消息] {msg}"));
        _wsClient.SystemMessage += msg => Raise(() => SystemMessage?.Invoke($"[系统] {msg}"));

        await _wsClient.ConnectAsync();
    }

    /// <summary>
    /// 周期性检查与服务端的版本是否匹配。
    ///
    /// 只做提示，不阻断任何功能：这是解说端，播送中出问题比提示更重要。
    /// </summary>
    private void StartVersionWatch()
    {
        try
        {
            _versionService = new VersionService(_config.ServerUrl);
        }
        catch
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = VersionService.PollInterval,
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                var service = _versionService;
                if (service is null) return;
                VersionChecked?.Invoke(await service.FetchAsync().ConfigureAwait(true));
            }
            finally
            {
                // 无论成功失败都重新开始，避免一次异常后永远不再检查。
                timer.Start();
            }
        };
        timer.Start();

        // 立刻查一次：现场部署完新版本，解说员不该等 5 分钟才看到提示。
        _ = RefreshVersionAsync();
    }

    private async Task RefreshVersionAsync()
    {
        var service = _versionService;
        if (service is null) return;
        VersionChecked?.Invoke(await service.FetchAsync().ConfigureAwait(true));
    }

    /// <summary>
    /// 查一次项目名填标题顶行。
    ///
    /// 失败不重试：赛事名只是锦上添花，机位名才是解说员真正要读的东西，
    /// 为它反复请求只会占用现场本就不宽裕的带宽。
    ///
    /// 查到后必须通知订阅者，理由见 <see cref="EventNameChanged"/>。
    /// </summary>
    private async Task LoadEventNameAsync()
    {
        var service = new ProjectInfoService(_config);

        // 错误提示要 marshal 回 UI 线程：取名内部刻意 ConfigureAwait(false)，
        // 不然一次 HTTP 请求会把 UI 线程按在原地，正在播的动画会顿住。
        var name = await service.FetchProjectNameAsync(msg => SystemMessage?.Invoke(msg))
            .ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(name))
        {
            EventName = name.Trim();
            // ConfigureAwait(true) 保证了这里已经在 UI 线程上，订阅者可以直接改控件。
            EventNameChanged?.Invoke(EventName);
        }
    }

    public void Dispose()
    {
        _wsClient?.Dispose();
    }
}
