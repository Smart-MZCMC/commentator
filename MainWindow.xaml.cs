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

    private static readonly Brush PanelOnAirBrush = Freeze("#0f3460");
    private static readonly Brush PanelPendingBrush = Freeze("#3a1f2b");
    private static readonly Brush IdleBrush = Freeze("#8a8a8a");

    private WebSocketClient? _wsClient;
    private AppConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
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
