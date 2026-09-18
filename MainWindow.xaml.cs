using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommentatorApp.Models;
using CommentatorApp.Services;
using Newtonsoft.Json;

namespace CommentatorApp;

public partial class MainWindow : Window
{
    private WebSocketClient? _wsClient;
    private AppConfig _config = new();
    private DispatcherTimer _nextShotClearTimer;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        _nextShotClearTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _nextShotClearTimer.Tick += (s, e) =>
        {
            _nextShotClearTimer.Stop();
            // 5秒后自动清空即将播送（如果导播没确认已切）
        };
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

        _wsClient.NextShotReceived += (type, content) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (type == "next_shot")
                {
                    // 更新"即将播送"
                    NextPreview.Text = content;
                    NextPreview.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e94560"));

                    // 5秒后自动清空
                    _nextShotClearTimer.Stop();
                    _nextShotClearTimer.Start();
                }
                else if (type == "confirm_switch")
                {
                    // 导播确认已切：将"即将播送"提升为"正在播送"
                    _nextShotClearTimer.Stop();
                    CurrentPlaying.Text = content;
                    CurrentPlaying.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ecca3"));
                    NextPreview.Text = "等待导播指令...";
                    NextPreview.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e94560"));
                }
                else if (type == "chat")
                {
                    // 内部消息显示在状态栏
                    LastUpdateText.Text = $"[内部消息] {content}";
                }
            });
        };

        _wsClient.SystemMessage += msg =>
        {
            Dispatcher.Invoke(() =>
            {
                LastUpdateText.Text = $"[系统] {msg}";
            });
        };

        await _wsClient.ConnectAsync();
    }

    /// <summary>
    /// 由导播端调用"确认已切"时，解说端上半部更新为"正在播送"
    /// 这个方法通过 WebSocket 消息触发，当 next_shot 被消费后调用
    /// </summary>
    public void ConfirmSwitch(string content)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentPlaying.Text = content;
            CurrentPlaying.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4ecca3"));
            NextPreview.Text = "等待导播指令...";
            NextPreview.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e94560"));
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _wsClient?.Dispose();
        base.OnClosed(e);
    }
}
