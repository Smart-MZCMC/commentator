using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommentatorApp.Models;
using CommentatorApp.Services;

namespace CommentatorApp;

/// <summary>
/// 播送标题 Overlay。几何与配色严格照 tools/render_title_preview.py 反推。
///
/// 关键约束：
/// - 蓝条与竖条颜色**固定**，不随「正在/即将」变色。
/// - 只有左下角 Chip 随状态变色：正在=绿、即将=红。
/// - 所有元素直接画在 OverlayCanvas 上，与 Python 的绘制顺序一致。
/// </summary>
public partial class TitleWindow : Window
{
    // ── 包装卡片 ──────────────────────────────────────────────────────
    //
    // 窗口就是这张卡片本身：采集端把它当信号源抓走，再摆到输出画面的
    // 位置和大小上，所以窗口不透明、不做成整屏浮层（理由见 TitleWindow.xaml）。
    //
    // ⚠️ 下面每一个几何常数都是 tools/measure_preview.py 量 tools/title-preview.png
    // 换算出来的实测值，量自图里第一张卡片（正在播送态）；量完的结论固化在
    // tools/verify_geometry.py 里，跑一遍就知道还对不对。
    //
    // **那张图才是本项目的样式参照物。**同目录下的 tools/reference.png（央视原始
    // 截图）**不是**：它的卡片是 4.92:1，这张是 3.563:1，比例根本对不上。
    // 此前有人按 reference.png 改过一次，把竖条改成「上下都比蓝条高出一截的凸出
    // 标签」，代码从此编译不过；量它的 tools/measure_edges.py 也一并删掉了，
    // 免得下一个人拿它去「修正」一个本来就对的实现。
    //
    // 改任何一个常数之前：先 python tools/measure_preview.py 重量，
    // 再改 tools/verify_geometry.py 里的实测值，最后 python tools/verify_geometry.py。
    private const double DefaultCardWidthRatio = 0.21;
    private const double DefaultMarginBottomRatio = 0.06;
    private const double DefaultMarginRightRatio = 0.04;

    // 输出画面尺寸兜底值。走 config（CanvasWidth / CanvasHeight）。
    private const double DefaultFrameWidth = 1920.0;
    private const double DefaultFrameHeight = 1080.0;

    // 设计网格 310 × 87，卡片比例 3.563:1。
    //
    // 310 这个宽**包含右侧竖条**（竖条 x 288.5..310），不是主体宽度——主体到 288.5。
    // 换算前提：预览图里那张卡片正好是 620 × 174 像素，即 1 设计单位 = 2 像素
    // （那张卡片本身就是 2 倍超采样后没缩回来的产物）。
    private const double DesignW = 310.0;
    private const double DesignH = 87.0;

    // 描边宽 3 个设计单位（预览图上 6 像素），用 Border 的 BorderThickness
    // **往内**画，理由见 Layout() 里的注释。
    private const double BorderU = 3.0 / DesignW;

    // 灰底：外框 x 0..288.5，y 45.5..87（高 41.5）
    //
    // 量到的「0..285.5」是**灰底填充**的右沿——它往右那 3 个单位是浅色描边
    // #F2F5F7。所以灰底元素的外框和蓝条一样到 288.5，两条右端是同一条边。
    //
    // 顶边不描：它紧贴蓝条的下沿，蓝条自己那圈描边的下边就是这条缝，再画一道
    // 就会出现一条图里根本没有的白线。
    private const double GrayW = 288.5 / DesignW;
    private const double GrayY = 45.5 / DesignH;

    // 蓝条：x 0..288.5，y 0..45.5。蓝色固定，不随「正在/即将」变。
    // 288.5 是外沿；描边往内占 3，蓝色填充的右沿落在 285.5，竖条从 288.5 起。
    private const double BarW = 288.5 / DesignW;
    private const double BarH = 45.5 / DesignH;

    // 竖条：x 288.5..310，y 0..45.5（宽 21.5），自己没有描边。
    //
    // ⚠️ 与蓝条**齐平**（同一条 EDGE 描边收口），中间那道 3 个单位的浅色分隔
    // 就是蓝条 BorderThickness 的右边，不是额外留的缝。
    //
    // 此前误按「上下都比蓝条高出一截的凸出标签」实现，那组值是从
    // tools/reference.png 量来的——而那份图不是本项目的参照物，见上面那段说明。
    // 不要再去动它；要动之前先用 tools/measure_preview.py 重量 title-preview.png。
    private const double AccentX = 288.5 / DesignW;
    private const double AccentW = 21.5 / DesignW;
    private const double AccentH = 45.5 / DesignH;

    // 色块（Chip）：左沿 x 3.5，高 38（灰底高 41.5），左右内边距 5.0，
    // 与机位名的间距 6.0。状态色块是解说端自己加的，预览图里就有两态对照。
    //
    // 内边距量的是色块内白字的左右留白。实测左 5.5 / 右 3.5，两侧不等是因为
    // render_title_preview.py 把文字按**墨迹**居中而不是按字宽居中——墨迹包围盒对
    // 「正在播送」这类字是有偏的，量到的 1 个单位差是偏心量不是内边距不对称。
    // 取左侧那个值：5.5 / 5.5。
    private const double ChipX = 3.5 / DesignW;
    private const double ChipPad = 5.5 / DesignW;
    private const double ChipGap = 6.0 / DesignW;

    // 底行行高占灰底高的比例：38 / 41.5。
    //
    // 行高对得上，但图里色块是 y 46.0..84.0，并不严格垂直居中（居中应是 47.25）。
    // 差的 1.25 个设计单位来自预览图脚本里那个 `- 2`（2 倍超采样，即 1 个设计单位）
    // 的手工微调。这里仍然居中：在 1920 输出、卡片 403 像素宽时这 1.25 单位只有
    // 1.6 像素，为了它引入一个常量不值得。看到色块略高于居中位置是正常的。
    private const double RowH = 38.0 / 41.5;

    // 字号上限（设计单位）
    //
    // 由墨迹高反推：黑体汉字的实际墨迹约占 em 的 0.86。
    // 赛事名实测墨迹 22.0 -> 25.6；机位名 18.0 -> 20.9；色块内 14.5 -> 16.9。
    // 墨迹高只算覆盖度 >= 0.85 的笔画像素（measure_preview.py 的 ink_box）；
    // 阈值放宽到 0.80 赛事名会量成 22.5、收到 0.60 之前是 23.0，多出来的
    // 全是笔画边缘的半覆盖像素，算进字号会把字撑大一号。
    private const double EventFontPx = 22.0 / 0.86;
    private const double ProgFontPx = 18.0 / 0.86;
    // 色块字号直接按行高给：16.9 / 38 ≈ 0.445
    private const double ChipFontRatio = 16.9 / 38.0;

    // WPF 行盒高相对字号的经验倍数，只拿来当高度上限；实测字号远低于它，
    // 正常情况下不会真的卡住。
    private const double LineBoxRatio = 1.5;

    private readonly AppConfig _config;

    private string _transitionStyle = TitleTransition.Slide;
    private TimeSpan _transitionDuration = TimeSpan.FromMilliseconds(300);

    private (bool HasPending, string Program) _lastState = (false, "");

    // ── 固定颜色（蓝条 / 竖条不随状态变） ────────────────────────────
    private static readonly Brush BarBrush = Frozen("#0054B7");
    private static readonly Brush AccentBrush = Frozen("#0046A5");

    // ── Chip 颜色（唯一随状态变的元素） ──────────────────────────────
    //
    // 在播是蓝不是绿，有两个原因：
    //   1. 键色已经是纯绿 #00FF00，卡片里再留一块绿，键控范围一放宽就分不清哪块
    //      是键、哪块是状态。改成蓝之后**整张卡片没有任何绿色**，绿键再没有风险。
    //   2. #0F5588 是参考图自己在播态用的原色，回到它而不是自造一个色相。
    //
    // 不取蓝条那个 #0054B7：色块和蓝条同色时它就不再像一块状态标记，只剩下色块
    // 的位置在说明「这里是状态」。#0F5588 比它暗、比它少一点饱和，两者能分辨。
    private static readonly Brush ChipOnAir = Frozen("#0F5588");
    private static readonly Brush ChipPending = Frozen("#DC3737");
    private static readonly Brush ChipIdle = Frozen("#8A9099");

    private Brush _chipBrush = ChipIdle;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 解析配置里的颜色，解析不了就用默认色。
    ///
    /// 配置是手写的 JSON，一个手滑的 #GG0000 会让 ColorConverter 抛异常，而异常
    /// 发生在构造函数里——窗口根本开不出来，现场只能干看着。这类值不值得为它崩掉
    /// 整个程序。
    /// </summary>
    private static Brush FrozenOrDefault(string hex, string fallback)
    {
        try
        {
            return Frozen(string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim());
        }
        catch
        {
            return Frozen(fallback);
        }
    }

    public TitleWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        // 窗口底色就是键色：卡片画不满的地方（竖条下方 x 288.5..310、y 45.5..87
        // 那一块）会自然露出底色，也就是交给采集端抠掉的键色。
        // 不需要额外画任何色块——阶梯状轮廓本身就是由「哪些地方不画」构成的。
        //
        // 兜底值与 AppConfig.KeyColor / config.json 保持同一个纯绿。理由见
        // AppConfig.cs 那段注释：EBU 绿离卡片自己的青绿状态块只有 20.9° 色相。
        Background = FrozenOrDefault(config.KeyColor, "#00FF00");

        _transitionStyle = TitleTransition.Normalize(config.Transition);
        _transitionDuration = TitleTransition.Duration(config.TransitionMs);

        SizeChanged += (_, _) => Layout();
        Activated += (_, _) => Layout();
        Loaded += (_, _) => Layout();

        ApplyEventName(null);
        RenderShotState(new ShotState());
    }

    public void Attach(BroadcastSession session)
    {
        session.ShotStateChanged += state => RenderShotState(state);

        // 订阅而不是只在 Loaded 时读一次属性：项目名是 HTTP 请求回来之后才有的，
        // 那时窗口早就 Show 完了，只读一次的话顶行永远停在配置里的兜底值。
        session.EventNameChanged += name => ApplyEventName(name);
        ApplyEventName(session.EventName);
    }

    /// <summary>
    /// 按设计网格坐标摆元素。所有坐标相对 OverlayCanvas。
    ///
    /// 设计网格就是 tools/title-preview.png 那张卡片本身（310 × 87），因此
    /// 「元素该放哪」不需要靠眼睛估，去 tools/measure_preview.py 重量就有。
    ///
    /// 尺寸以「输出画面宽度 × CardWidthRatio」为准，而不是当前显示器的宽度：
    /// 这块卡片是给信号链用的，按输出画面定尺寸，换一台分辨率不同的机器做
    /// 同一场直播时卡片不会跟着变样。
    ///
    /// 单位：函数内除了 workPx 与 MoveToPx 的参数，全部是 DIP。位置只认窗口
    /// 当前所在那块显示器的工作区——早期版本用的是 SystemParameters.WorkArea
    /// （主显示器 + 系统 DPI 的 DIP），解说员把窗口拖到副屏后再一改大小，窗口
    /// 就会被重新摆回主屏坐标系的某个位置，横跨屏幕边界只剩一半可见。
    /// </summary>

    private void Layout()
    {
        var workPx = WindowHelper.GetWorkAreaPx(this);
        // 句柄还没建出来就没有工作区可依据。XAML 里的初值就是卡片尺寸，
        // 等 Loaded / SizeChanged 带着真实工作区再算一次即可。
        if (workPx.IsEmpty || workPx.Width <= 0 || workPx.Height <= 0) return;

        var scale = DpiScaleX;
        var (frameWpx, _) = FrameSizeForConfig();

        // ── 单位只在这里换一次：往下全部是 DIP ──────────────────────────
        //
        // WPF 的窗口尺寸与元素几何都是 DIP，只有 MoveToPx 要物理像素。曾经把
        // cardW / cardH 留在物理像素上、只给 Width/Height 除了一次 scale，于是
        // 桌面缩放 150% 时窗口是 403/1.5 DIP、卡片却按 403 DIP 摆——卡片比窗口
        // 大 1.5 倍，右端竖条整个落在窗口外、底行被切掉一半。100% 缩放下两者
        // 数值相同，所以这个错在开发机上完全看不出来。
        var cardW = frameWpx * CardWidthRatioForConfig() / scale;
        var u = cardW / DesignW;
        var cardH = DesignH * u;
        if (cardW <= 0 || cardH <= 0) return;

        Width = cardW;
        Height = cardH;

        // 摆在所在显示器的右下角：解说员本地看它在画面下三分之一的位置，
        // 免得压在导播正在看的机位上。抓成信号源时这个位置不影响播出结果，
        // 采集端会按自己设置的画面位置摆。
        // MoveToPx 收物理像素，所以这里要把 DIP 乘回 scale。
        var x = workPx.Right - workPx.Width * MarginRightRatioForConfig() - cardW * scale;
        var y = workPx.Bottom - workPx.Height * MarginBottomRatioForConfig() - cardH * scale;
        WindowHelper.MoveToPx(this, (int)Math.Round(x), (int)Math.Round(y));

        // 描边一律**往内**画：给 Border 设 BorderThickness，而不是给 Rectangle 设
        // StrokeThickness。后者以路径为中心，有一半落在卡片外面被窗口裁掉，实际
        // 露出来只有 1.5 个设计单位，描边比预期细一半——这不是「看起来略粗」的问题，
        // 而是键色会从卡片边缘漏进来 1.5 个单位，抠像后卡片轮廓上会镶一道键色的边。
        var edge = cardW * BorderU;

        // 灰底：顶边不描（左上为 0），它紧贴蓝条下沿，蓝条自己那圈描边的下边就是
        // 这条缝，再描一道就会出现一条图里没有的白线。左右与下沿描边和蓝条对齐，
        // 于是蓝条与灰底合成同一个矩形轮廓，只有右下角缺一块。
        Card.BorderThickness = new Thickness(edge, 0, edge, edge);

        var grayTop = cardH * GrayY;
        var grayH = cardH - grayTop;
        Place(Card, 0, grayTop, cardW * GrayW, grayH);

        // 蓝条：四边都描。描边内沿的蓝色填充右沿落在 285.5，竖条从 288.5 起。
        EventBar.BorderThickness = new Thickness(edge);
        Place(EventBar, 0, 0, cardW * BarW, cardH * BarH);

        // 竖条：不描边，纯一块深蓝；上下与蓝条齐平（BarH == AccentH 实测同值）。
        // 它和灰底都不画到的右下角，会露出窗口底色，也就是交给采集端抠掉的键色。
        Place(AccentBar, cardW * AccentX, 0, cardW * AccentW, cardH * AccentH);

        // 底行落在灰底里，垂直居中
        var rowH = grayH * RowH;
        var rowY = grayTop + (grayH - rowH) / 2;

        Place(ProgramRow, cardW * ChipX, rowY, cardW * GrayW - cardW * ChipX, rowH);

        // Chip：左右内边距 5，与机位名间距 6（实测值见常量区）
        var chipPad = cardW * ChipPad;
        StateChip.Margin = new Thickness(0, 0, cardW * ChipGap, 0);
        StateChip.Padding = new Thickness(chipPad, 0, chipPad, 0);
        StateLabel.FontSize = Math.Max(8, rowH * ChipFontRatio);
        StateChip.Background = _chipBrush;

        // 机位名字号 + 水平偏移
        var progOffset = _config.ProgOffsetX * u;
        var progAreaWidth = cardW * GrayW - cardW * ChipX - chipPad * 2 - cardW * ChipGap;
        var progFont = FitFontSize(ProgramName.Text, progAreaWidth, ProgFontPx * u, rowH / LineBoxRatio);
        ProgramName.FontSize = progFont;
        ProgramName.RenderTransform = new TranslateTransform(progOffset, 0);

        // 赛事名字号
        // 蓝条左右各留一点给描边，文字可用宽度再收 0.92
        var barInner = cardW * BarW * 0.92;
        EventNameText.FontSize = FitFontSize(
            EventNameText.Text, barInner, EventFontPx * u, cardH * BarH / LineBoxRatio);

        EventBar.Background = BarBrush;
        AccentBar.Background = AccentBrush;
    }

    private static double FitFontSize(string text, double availableWidth, double maxSize, double heightCap)
    {
        if (string.IsNullOrWhiteSpace(text)) return Math.Min(maxSize, heightCap);
        var visualLength = 0.0;
        foreach (var ch in text)
        {
            visualLength += ch > 0x2E80 ? 1.0 : 0.55;
        }
        if (visualLength <= 0) return Math.Min(maxSize, heightCap);
        var fitted = availableWidth / visualLength * 0.92;
        return Math.Clamp(fitted, 8, Math.Min(maxSize, heightCap));
    }

    private static void Place(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }

    private double CardWidthRatioForConfig()
    {
        var ratio = _config.CardWidthRatio;
        return double.IsNaN(ratio) || ratio <= 0 ? DefaultCardWidthRatio : Math.Clamp(ratio, 0.05, 0.60);
    }

    /// <summary>
    /// 输出画面尺寸（物理像素），也就是卡片尺寸的设计基准。
    ///
    /// 只取宽度参与计算：卡片宽 = 画面宽 × CardWidthRatio，与画面高、与 16:9
    /// 都无关。仍然校验比例是因为把宽高写反会得到一张明显偏小的卡片，而这种错
    /// 在播出前很难一眼看出来。
    /// </summary>
    private (double Width, double Height) FrameSizeForConfig()
    {
        var w = _config.CanvasWidth;
        var h = _config.CanvasHeight;
        if (w <= 0 || h <= 0) return (DefaultFrameWidth, DefaultFrameHeight);
        if (Math.Abs(w / (double)h - 16.0 / 9.0) > 0.01) return (DefaultFrameWidth, DefaultFrameHeight);
        return (w, h);
    }

    private double MarginBottomRatioForConfig()
    {
        var ratio = _config.MarginBottomRatio;
        return double.IsNaN(ratio) || ratio < 0 ? DefaultMarginBottomRatio : Math.Clamp(ratio, 0, 0.4);
    }

    private double MarginRightRatioForConfig()
    {
        var ratio = _config.MarginRightRatio;
        return double.IsNaN(ratio) || ratio < 0 ? DefaultMarginRightRatio : Math.Clamp(ratio, 0, 0.4);
    }

    private void ApplyEventName(string? name)
    {
        var text = (name ?? _config.EventName).Trim();
        EventNameText.Text = text;
        var visible = !string.IsNullOrEmpty(text);
        EventBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AccentBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Layout();
    }

    private void RenderShotState(ShotState state)
    {
        var program = state.Program;
        if (string.IsNullOrWhiteSpace(program))
        {
            _lastState = (false, "");
            ProgramName.Text = "—";
            StateLabel.Text = "等待";
            _chipBrush = ChipIdle;
            Layout();
            return;
        }

        ProgramName.Text = program;

        if (state.HasPending)
        {
            StateLabel.Text = "即将播送";
            _chipBrush = ChipPending;
        }
        else
        {
            StateLabel.Text = "正在播送";
            _chipBrush = ChipOnAir;
        }

        Layout();

        if (_lastState == (state.HasPending, program)) return;
        _lastState = (state.HasPending, program);
        TitleTransition.Play(ProgramRow, _transitionStyle, _transitionDuration);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
    }

    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        Topmost = sender is MenuItem item ? item.IsChecked : !Topmost;
    }

    private void OnToggleClickThrough(object sender, RoutedEventArgs e)
    {
        var enabled = sender is MenuItem item && item.IsChecked;
        WindowHelper.SetClickThrough(this, enabled);
    }

    private void OnShowStatusWindow(object sender, RoutedEventArgs e)
    {
        if (WindowHelper.FindWindow<StatusWindow>() is not { } status) return;
        status.Show();
        status.Activate();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private double DpiScaleX
    {
        get
        {
            try
            {
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return scale > 0 ? scale : 1.0;
            }
            catch { return 1.0; }
        }
    }
}
