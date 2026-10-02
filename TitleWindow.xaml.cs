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
    // ── 信号链里的包装层 ──────────────────────────────────────────────
    //
    // 窗口现在就是一张 16:9 画面：芯象把它当窗口源抓走、叠进直播画面，
    // 卡片摆在画面里的下三分之一。窗口等于画面尺寸还有个附带好处——抓到的
    // 源不需要再跟采集区域对齐，窗口在屏幕上摆哪儿都不影响播出。
    //
    // 画布尺寸走 config（CanvasWidth / CanvasHeight），这里只是兜底值。
    private const double DefaultCanvasWidth = 1920.0;
    private const double DefaultCanvasHeight = 1080.0;
    private const double DefaultMarginBottomRatio = 0.06;
    private const double DefaultMarginRightRatio = 0.04;
    private const double DefaultCardWidthRatio = 0.21;

    // ── 卡片内部几何 ──────────────────────────────────────────────────
    //
    // 基数照 tools/render_title_preview.py 的 CARD_W=300 / CARD_H=87 反推，
    // 竖条算进画布所以设计宽是 310。下面所有比例都以 DesignW / DesignH 为分母，
    // 实际像素 = 设计值 × u，u = 卡片宽 / DesignW。
    private const double DesignW = 310.0;
    private const double DesignH = 87.0;

    // 灰底填充：0..290
    private const double GrayW = 290.0 / 310.0;

    // 外框描边矩形：0..288，stroke=3
    private const double OutlineW = 288.0 / 310.0;
    private const double BorderPx = 3.0 / 310.0;

    // 蓝条：0..288 × 0..45
    private const double BarW = 288.0 / 310.0;
    private const double BarH = 45.0 / 87.0;

    // 竖条：288.5..309.5 × 0..45
    private const double AccentX = 288.5 / 310.0;
    private const double AccentW = 21.0 / 310.0;
    private const double AccentH = 45.0 / 87.0;

    // Chip：x=3.5（7px @s=2），内边距 7，间距 6
    private const double ChipX = 3.5 / 310.0;
    private const double ChipPad = 7.0 / 310.0;
    private const double ChipGap = 6.0 / 310.0;

    // 底行：内容区高 = 87-45 = 42，行高 = 42 × 0.9
    private const double RowH = 0.9;

    // 字号上限（设计单位）
    private const double EventFontPx = 23.0;
    private const double ProgFontPx = 19.0;
    private const double ChipFontRatio = 0.4;
    private const double LineBoxRatio = 1.5;

    private readonly AppConfig _config;

    private string _transitionStyle = TitleTransition.Slide;
    private TimeSpan _transitionDuration = TimeSpan.FromMilliseconds(300);

    private (bool HasPending, string Program) _lastState = (false, "");

    /// <summary>鼠标穿透默认值是否已经落到窗口上，避免每次 Layout 都重复设。</summary>
    private bool? _clickThroughApplied;

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

    public TitleWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

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
    /// 尺寸与位置都只认「窗口当前所在那块显示器」的工作区，并且一律先在
    /// 物理像素里算清楚再换算。早期版本用的是 SystemParameters.WorkArea
    /// （主显示器 + 系统 DPI 的 DIP），解说员把窗口拖到副屏后再一改大小，
    /// 窗口就会被重新摆回主屏坐标系的某个位置，横跨屏幕边界只剩一半可见。
    /// </summary>
    private void Layout()
    {
        var workPx = WindowHelper.GetWorkAreaPx(this);
        // 句柄还没建出来就没有工作区可依据。XAML 里的初值就是 1920×1080，
        // 等 Loaded / SizeChanged 带着真实工作区再算一次即可。
        if (workPx.IsEmpty || workPx.Width <= 0 || workPx.Height <= 0) return;

        var scale = DpiScaleX;
        var (canvasWpx, canvasHpx) = CanvasSizeForConfig();

        // 窗口尺寸用 DIP 赋值、窗口内部几何也全在 DIP 里；只有摆位和边距用物理像素。
        // 除以 scale 是为了让物理尺寸正好等于配置的像素数——桌面缩放 150% 时
        // 1920px 的包装层仍然是 1920 个物理像素，不会变成 2880 再被芯象缩回去。
        var w = canvasWpx / scale;
        var h = canvasHpx / scale;
        if (w <= 0 || h <= 0) return;

        Width = w;
        Height = h;

        // 卡片按画面宽度定尺寸，内部所有尺寸 = 设计值 × u。
        var cardW = workPx.Width * CardWidthRatioForConfig();
        var u = cardW / DesignW;
        var cardH = DesignH * u;

        // 下三分之一、靠右。压边留出电视自己的字幕栏位置。
        var cardX = workPx.Right - workPx.Width * MarginRightRatioForConfig() - cardW;
        var cardY = workPx.Bottom - workPx.Height * MarginBottomRatioForConfig() - cardH;

        // 整块画布摆到所在显示器的左上角：窗口等于一张画面，位置只影响
        // 解说员本地看到的浮层，抓成信号源时不影响播出结果。
        var canvasX = workPx.Left + (workPx.Width - canvasWpx) / 2;
        var canvasY = workPx.Top + (workPx.Height - canvasHpx) / 2;
        WindowHelper.MoveToPx(this, (int)Math.Round(canvasX), (int)Math.Round(canvasY));

        var stroke = Math.Max(1, cardW * BorderPx);

        // 灰底：0..290 × 0..87
        Place(Card, cardX, cardY, cardW * GrayW, cardH);

        // 外框描边：0..288 × 0..87
        Place(CardOutline, cardX, cardY, cardW * OutlineW, cardH);
        CardOutline.StrokeThickness = stroke;

        // 蓝条：0..288 × 0..45
        Place(EventBar, cardX, cardY, cardW * BarW, cardH * BarH);
        EventBar.BorderThickness = new Thickness(stroke);

        // 竖条：288.5..309.5 × 0..45
        Place(AccentBar, cardX + cardW * AccentX, cardY, cardW * AccentW, cardH * AccentH);

        // 底行
        var bodyTop = cardY + cardH * BarH;
        var bodyH = cardH - cardH * BarH;
        var rowH = bodyH * RowH;
        var rowY = bodyTop + (bodyH - rowH) / 2;

        Place(ProgramRow, cardX, rowY, cardW, rowH);

        // Chip：x=3.5，内边距 7，间距 6
        var chipPad = cardW * ChipPad;
        StateChip.Margin = new Thickness(cardW * ChipX, 0, cardW * ChipGap, 0);
        StateChip.Padding = new Thickness(chipPad, 0, chipPad, 0);
        StateLabel.FontSize = Math.Max(8, rowH * ChipFontRatio);
        StateChip.Background = _chipBrush;

        // 机位名字号 + 水平偏移
        var progOffset = _config.ProgOffsetX * u;
        var progAreaWidth = cardW * (1.0 - ChipX) - chipPad * 2 - cardW * ChipGap;
        var progFont = FitFontSize(ProgramName.Text, progAreaWidth, ProgFontPx * u, rowH / LineBoxRatio);
        ProgramName.FontSize = progFont;
        ProgramName.RenderTransform = new TranslateTransform(progOffset, 0);

        // 赛事名字号
        var barInner = cardW * BarW * 0.82;
        EventNameText.FontSize = FitFontSize(
            EventNameText.Text, barInner, EventFontPx * u, cardH * BarH / LineBoxRatio);

        EventBar.Background = BarBrush;
        AccentBar.Background = AccentBrush;

        ApplyClickThroughDefault(workPx, canvasWpx, canvasHpx);
    }

    /// <summary>
    /// 画布铺满显示器时默认打开鼠标穿透。
    ///
    /// 1920×1080 的包装层在 1080p 桌面上会盖住整块屏，不穿透的话操作员
    /// 连桌面都点不进去。面积不到九成时保持可点，否则小窗口上想挪一下位置
    /// 都得先取消穿透。
    /// </summary>
    private void ApplyClickThroughDefault(Rect workPx, double canvasWpx, double canvasHpx)
    {
        var covers = canvasWpx >= workPx.Width * 0.9 && canvasHpx >= workPx.Height * 0.9;
        if (_clickThroughApplied == covers) return;
        _clickThroughApplied = covers;
        WindowHelper.SetClickThrough(this, covers);
        if (ClickThroughItem is not null) ClickThroughItem.IsChecked = covers;
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
    /// 画布尺寸（物理像素）。
    ///
    /// 非 16:9 一律退回 1920×1080：窗口源是按采集端的输出比例缩放的，
    /// 比例对不上就是变形或者裁边，而这种错在播出前很难一眼看出来。
    /// 宁可忽略配置也不能放一个会悄悄坏掉的比例进信号链。
    /// </summary>
    private (double Width, double Height) CanvasSizeForConfig()
    {
        var w = _config.CanvasWidth;
        var h = _config.CanvasHeight;
        if (w <= 0 || h <= 0) return (DefaultCanvasWidth, DefaultCanvasHeight);
        if (Math.Abs(w / (double)h - 16.0 / 9.0) > 0.01) return (DefaultCanvasWidth, DefaultCanvasHeight);
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
