using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    // 下面每一个数值都由 tools/measure_ref.py 量 tools/reference.png 换算而来，
    // 设计宽取 310（换算系数 310 / 2143 = 0.1447）。注释里的像素是参考图原值，
    // 改任何一个之前先拿脚本重量一遍。
    //
    // ⚠️ 不要照 tools/render_title_preview.py 的常量写：那份脚本并不忠实于参考图
    // （它把灰底画到 x=270 就收尾，卡片右下方留出一块空的深色底）。此前按它的
    // CARD_H=87 实现，整张卡片是 3.56:1，而参考图实测 4.93:1——实物明显比样式图
    // 更「高」，描边也粗了 3 倍。
    private const double DefaultCardWidthRatio = 0.21;
    private const double DefaultMarginBottomRatio = 0.06;
    private const double DefaultMarginRightRatio = 0.04;

    // 输出画面尺寸兜底值。走 config（CanvasWidth / CanvasHeight）。
    private const double DefaultFrameWidth = 1920.0;
    private const double DefaultFrameHeight = 1080.0;

    // 内容区 2143 × 436 像素 = 4.92:1
    private const double DesignW = 310.0;
    private const double DesignH = 63.0;

    // 灰底：x 0..298.1，y 33.5..63.0（参考图 x 14..2075，y 361..564）
    private const double GrayW = 298.1 / 310.0;
    private const double GrayY = 33.5 / 63.0;

    // 蓝条：x 1.0..294.1，y 1.5..31.8（参考图 x 21..2047，y 139..348）
    //
    // 左上各留 1 个多设计单位，那里在参考图里是透明的，因此会露出键色。
    private const double BarX = 1.0 / 310.0;
    private const double BarY = 1.5 / 63.0;
    private const double BarW = 293.1 / 310.0;
    private const double BarH = 30.4 / 63.0;

    // 竖条：x 298.6..309.9，y 0..33.3（参考图 x 2079..2156，y 129..358）
    //
    // 它是一块**上下都比蓝条高**的凸出标签（蓝条 y 1.5..31.8，竖条 0..33.3），
    // 不是齐平的一条。此前按齐平做，看着就是蓝条右端颜色略深的一块。
    //
    // 与蓝条之间有 4.5 个设计单位的缝，参考图里同样是透明的 → 露出键色。
    private const double AccentX = 298.6 / 310.0;
    private const double AccentY = 0.0;
    private const double AccentW = 11.3 / 310.0;
    private const double AccentH = 33.3 / 63.0;

    // Chip：x=3.5，内边距 7，间距 6。参考图里没有色块，是解说端自己加的。
    private const double ChipX = 3.5 / 310.0;
    private const double ChipPad = 7.0 / 310.0;
    private const double ChipGap = 6.0 / 310.0;

    // 底行行高占灰底高的比例。参考图黑字墨迹高 151 像素 / 灰底 204 ≈ 0.74，
    // 且上下居中（字心 463 对灰底中心 462.5），也就是字把灰底撑满。
    private const double RowH = 0.9;

    // 字号上限（设计单位）
    //
    // 参考图里赛事名与机位名的墨迹高度实测 150 / 151 像素——**同号**，此前写成
    // 23 / 19 是错的。墨迹高 ÷ 0.86（黑体汉字的实际墨迹约占 em 的 0.86）得字号。
    private const double EventFontPx = 25.2;
    private const double ProgFontPx = 25.2;
    private const double ChipFontRatio = 0.4;
    private const double LineBoxRatio = 1.5;

    private readonly AppConfig _config;

    private string _transitionStyle = TitleTransition.Slide;
    private TimeSpan _transitionDuration = TimeSpan.FromMilliseconds(300);

    private (bool HasPending, string Program) _lastState = (false, "");

    // ── 固定颜色（蓝条 / 竖条不随状态变） ────────────────────────────
    private static readonly Brush BarBrush = Frozen("#0054B7");
    private static readonly Brush AccentBrush = Frozen("#0046A5");

    // ── Chip 颜色（唯一随状态变的元素） ──────────────────────────────
    private static readonly Brush ChipOnAir = Frozen("#48C4A0");
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

        // 窗口底色就是键色：卡片画不满的地方（竖条与蓝条之间、蓝条与灰底之间、
        // 灰底右下方那一块）会自然露出底色，也就是交给采集端抠掉的键色。
        // 不需要额外画任何色块——阶梯状轮廓本身就是由「哪些地方不画」构成的。
        Background = FrozenOrDefault(config.KeyColor, "#FF00FF");

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
    /// 按 Python 的像素坐标摆元素。所有坐标相对 OverlayCanvas。
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

        // 灰底：y 33.4% 起铺到卡片底。灰底比蓝条宽（298.3 对 294.3），
        // 左右各多出来一点，是参考图里就有的。
        var grayTop = cardH * GrayY;
        var grayH = cardH - grayTop;
        Place(Card, 0, grayTop, cardW * GrayW, grayH);

        // 蓝条：左上各留一点，那圈在参考图里是透明的，会露出键色
        Place(EventBar, cardW * BarX, cardH * BarY, cardW * BarW, cardH * BarH);

        // 竖条：上下都比蓝条高，与蓝条之间留着一道缝，都是独立一块
        Place(AccentBar, cardW * AccentX, cardH * AccentY, cardW * AccentW, cardH * AccentH);

        // 底行落在灰底里，垂直居中
        var rowH = grayH * RowH;
        var rowY = grayTop + (grayH - rowH) / 2;

        Place(ProgramRow, cardW * ChipX, rowY, cardW * GrayW - cardW * ChipX, rowH);

        // Chip：内边距 7，间距 6
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
