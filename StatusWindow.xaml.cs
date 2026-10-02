using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommentatorApp.Models;
using CommentatorApp.Services;

namespace CommentatorApp;

/// <summary>
/// 状态窗口。播送标题在 <see cref="TitleWindow"/>，这里只管版本号、连接状态、
/// 内部消息和版本横幅——都是「不用盯着看，但出问题时要看」的东西。
/// </summary>
public partial class StatusWindow : Window
{
    /// <summary>日志行的上限。超出就丢最旧的，不然长时间运行会一直吃内存。</summary>
    private const int MaxLogLines = 500;

    private bool _versionBannerDismissed;

    public StatusWindow(AppConfig config)
    {
        InitializeComponent();

        // 标题栏常驻显示本端版本，方便现场对着服务端确认。
        VersionSelfText.Text = $"v{AppVersion.Current}";
    }

    /// <summary>接上共享会话。所有回调都在 UI 线程上，不需要再 marshal。</summary>
    public void Attach(BroadcastSession session)
    {
        session.ConnectionChanged += connected =>
        {
            StatusDot.Fill = connected ? Brushes.Green : Brushes.Red;
            StatusText.Text = connected ? "已连接" : "连接中...";
            Log(connected ? "已连接到服务端" : "连接断开，正在重试");
        };

        session.ShotStateChanged += state =>
        {
            // 切台状态也记一笔：现场复盘「导播到底什么时候切的」时，
            // 只有这里留得下时间线，标题窗口是一闪而过的。
            Log(state.HasPending ? $"即将播送：{state.Next}" : $"正在播送：{state.Current}");
            LastUpdateText.Text = state.HasPending ? "导播已预切，等待确认" : "已确认切台";
        };

        session.SystemMessage += Log;
        session.VersionChecked += ApplyVersionCheck;
    }

    /// <summary>
    /// 周期性检查的结论。
    ///
    /// 只提示不阻断：这是解说端，播送中出问题比提示更重要。
    /// </summary>
    private void ApplyVersionCheck(AppVersion.VersionCheck check)
    {
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

    /// <summary>
    /// 标题窗口的鼠标穿透开关在这里也能改。
    ///
    /// 标题窗口铺满屏幕时会自动打开穿透（不然桌面点不动），而穿透之后它自己
    /// 就接收不到右键了，菜单再也唤不出来。状态窗口是那时唯一还能点到的窗口，
    /// 所以这条逃生口必须留在这儿，而不是只写在标题窗口的菜单里。
    /// </summary>
    private void OnToggleTitleClickThrough(object sender, RoutedEventArgs e)
    {
        if (WindowHelper.FindWindow<TitleWindow>() is not { } title) return;

        var enabled = sender is MenuItem item && item.IsChecked;
        WindowHelper.SetClickThrough(title, enabled);
        Log(enabled ? "已关闭播送标题的鼠标穿透" : "已打开播送标题的鼠标穿透");
    }

    /// <summary>
    /// 往消息流水里追加一行。
    ///
    /// 带时间戳前缀而不是只留一条状态：副屏那边看不出发生了什么，导播跑过来
    /// 问「刚才那下到底切没切」时，这扇窗口是唯一的证据。
    /// </summary>
    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > MaxLogLines)
        {
            LogList.Items.RemoveAt(0);
        }

        // 新行自动滚到底，否则最后一条永远停在可视区外面。
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
