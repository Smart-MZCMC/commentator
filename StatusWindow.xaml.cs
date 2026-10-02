using System.IO;
using System.Windows;
using System.Windows.Media;
using CommentatorApp.Models;
using CommentatorApp.Services;
using Newtonsoft.Json;

namespace CommentatorApp;

public partial class MainWindow : Window
{
    /// <summary>「正在播送」配色：绿色，表示画面就是这个机位。</summary>
    private static readonly Brush OnAirBrush = Freeze("#4ecca3");

    /// <summary>「即将播送」配色：琥珀红，表示画面还没切过去。</summary>
    private static readonly Brush PendingBrush = Freeze("#e94560");

    /// <summary>版本提示条配色：低于最低适配版本用红（真的有功能异常），落后用琥珀。</summary>
    private static readonly Brush VersionUrgentBrush = Freeze("#e94560");

    private static readonly Brush VersionWarnBrush = Freeze("#d9a441");
    private static readonly Brush VersionBannerBackground = Freeze("#2b2038");

    private static readonly Brush PanelOnAirBrush = Freeze("#0f3460");
    private static readonly Brush PanelPendingBrush = Freeze("#3a1f2b");
    private static readonly Brush IdleBrush = Freeze("#8a8a8a");

    private WebSocketClient? _wsClient;
    private AppConfig _config = new();
    private VersionService? _versionService;
    private bool _versionBannerDismissed;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        // 标题栏常驻显示本端版本，方便现场对着服务端确认。
        VersionSelfText.Text = $"v{AppVersion.Current}";
        RenderShotState(hasPending: false, program: "", label: "等待导播指令");
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        // 冻结后可以跨线程安全共用，省掉每次切台都新建画刷。
        brush.Freeze();
        return brush;
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = ConnectWebSocket();
        StartVersionWatch();
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
                await RefreshVersionBannerAsync();
            }
            finally
            {
                // 无论成功失败都重新开始，避免一次异常后永远不再检查。
                timer.Start();
            }
        };
        timer.Start();

        // 立刻查一次：现场部署完新版本，解说员不该等 5 分钟才看到提示。
        _ = RefreshVersionBannerAsync();
    }

    private async Task RefreshVersionBannerAsync()
    {
        var service = _versionService;
        if (service is null) return;

        var check = await service.FetchAsync().ConfigureAwait(true);
        if (_versionBannerDismissed) return;

        var text = DescribeVersionCheck(check);
        if (string.IsNullOrEmpty(text))
        {
            VersionBanner.Visibility = Visibility.Collapsed;
            return;
        }

        VersionBannerText.Text = text;
        VersionSelfVersionText.Text = $"本端 v{AppVersion.Current}";
        VersionBanner.Visibility = Visibility.Visible;
    }

    private static string DescribeVersionCheck(AppVersion.VersionCheck check)
    {
        return check.Status switch
        {
            AppVersion.VersionStatus.Unsupported =>
                $"解说端版本 {check.Client} 已低于服务端要求的最低适配版本 {check.Minimum}，"
                + "部分功能可能异常，请尽快更新。",
            AppVersion.VersionStatus.ClientBehind =>
                $"解说端版本 {check.Client} 落后于服务端 {check.Server}，建议更新后再使用。",
            AppVersion.VersionStatus.ClientAhead =>
                $"解说端版本 {check.Client} 新于服务端 {check.Server}，"
                + "服务端可能缺少接口，请升级服务端。",
            _ => string.Empty,
        };
    }

    private void OnVersionBannerClose(object sender, RoutedEventArgs e)
    {
        // 只隐藏本次，不停止轮询：版本再次变化时还会提示。
        _versionBannerDismissed = true;
        VersionBanner.Visibility = Visibility.Collapsed;
    }

    private async Task ConnectWebSocket()
    {
        _wsClient?.Dispose();
        _wsClient = new WebSocketClient(_config);

        _wsClient.ConnectionChanged += connected =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = connected ? Brushes.Green : Brushes.Red;
                StatusText.Text = connected ? "已连接" : "连接中...";
            });
        };

        // 切台状态：后端每次都同时给出「当前播送」和「即将切台」，
        // 这里直接照着渲染，不做任何本地推断。
        _wsClient.ShotStateReceived += (hasPending, program, label) =>
        {
            Dispatcher.Invoke(() => RenderShotState(hasPending, program, label));
        };

        _wsClient.ChatReceived += msg =>
        {
            Dispatcher.Invoke(() => LastUpdateText.Text = $"[内部消息] {msg}");
        };

        _wsClient.SystemMessage += msg =>
        {
            Dispatcher.Invoke(() => LastUpdateText.Text = $"[系统] {msg}");
        };

        await _wsClient.ConnectAsync();
    }

    /// <summary>
    /// 渲染唯一的播送状态：有待切机位显示「即将播送」，否则显示「正在播送」。
    /// </summary>
    private void RenderShotState(bool hasPending, string program, string label)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            StateLabel.Text = "等待导播指令";
            StateLabel.Foreground = IdleBrush;
            ProgramName.Text = "—";
            ProgramName.Foreground = IdleBrush;
            ProgramPanel.Background = PanelOnAirBrush;
            return;
        }

        StateLabel.Text = label;
        ProgramName.Text = program;
        StateLabel.Foreground = hasPending ? PendingBrush : IdleBrush;
        ProgramName.Foreground = hasPending ? PendingBrush : OnAirBrush;
        ProgramPanel.Background = hasPending ? PanelPendingBrush : PanelOnAirBrush;
    }

    protected override void OnClosed(EventArgs e)
    {
        _wsClient?.Dispose();
        base.OnClosed(e);
    }
}
