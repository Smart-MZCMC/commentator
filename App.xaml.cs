using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommentatorApp.Models;
using Newtonsoft.Json;

namespace CommentatorApp;

public partial class App : Application
{
    private BroadcastSession? _session;

    /// <summary>崩溃日志。解说端是无人值守跑的，崩了没人看得见，必须自己记。</summary>
    private static readonly string CrashLogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashHandlers();

        var config = LoadConfig();
        _session = new BroadcastSession(config);

        var title = new TitleWindow(config);
        var status = new StatusWindow(config);
        title.Attach(_session);
        status.Attach(_session);

        // 标题窗口关掉只是不想看了，不该把整个应用带走——状态窗口还在接消息，
        // 而且现场常见「先把提示屏撤下来，状态窗口留着查日志」的做法。
        title.Closed += (_, _) => status.Show();

        // 状态窗口才是主窗口：它 Maximized 且带标题栏，现场第一眼看到的是它，
        // 标题窗口随后才被挪到副屏。
        MainWindow = status;
        status.Show();
        title.Show();

        _ = _session.StartAsync();
    }

    /// <summary>
    /// 把未处理异常记到文件。
    ///
    /// 之前这端崩过一次而现场完全看不出原因：切台状态是从 WebSocket 接收循环
    /// （后台线程）抛上来的，工作线程上的未处理异常会直接终结进程，界面上什么都
    /// 不会留下。这层就是防止下一次再抓瞎。
    /// </summary>
    private static void InstallCrashHandlers()
    {
        Current.DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI", args.Exception);
            // 不 Handle：让进程正常退出并留下非零退出码，比带着半个坏状态继续跑好。
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain", args.ExceptionObject as Exception
                ?? new Exception(args.ExceptionObject?.ToString() ?? "未知"));
        };
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n{new string('-', 60)}\r\n");
        }
        catch
        {
            // 记日志失败就没什么可做的了，别在这里再抛一次。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _session?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 读配置。读不到就用默认值，不因为一个坏掉的 config.json 就起不来——
    /// 现场没法连上开发机排查，起不来就是彻底停播。
    /// </summary>
    private static AppConfig LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (!File.Exists(path)) return new AppConfig();

            return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}
