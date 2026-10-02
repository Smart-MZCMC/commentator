using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CommentatorApp.Services;

/// <summary>
/// 「正在播送 ↔ 即将播送」的切换动画。
///
/// 做成配置项而不是写死，是因为解说员对动效的耐受度差别很大：播送时靠余光
/// 扫一眼机位名，位移太大反而看不清。所以给四种风格，现场在 config.json 里
/// 改 <c>Transition</c> 就能换，不用重新构建。
///
/// 四种风格都遵守同一条约束：**旧内容先被覆盖，新内容后到位**。让新旧两行
/// 字同时在屏上是切换动画最常见的翻车方式——参考图那种单行标题条一旦叠成
/// 两行，解说员会下意识把上面那行也念出去。
/// </summary>
public static class TitleTransition
{
    /// <summary>slide：从下往上滑入。默认，最接近电视下三分之一条的行为。</summary>
    public const string Slide = "slide";

    /// <summary>fade：纯淡入淡出，完全不位移。对动效敏感的解说员用这个。</summary>
    public const string Fade = "fade";

    /// <summary>roll：横向推移，新内容把旧内容顶出去，像滚动字幕。</summary>
    public const string Roll = "roll";

    /// <summary>scale：沿垂直中线向上下展开并淡入，动作感最强。</summary>
    public const string Scale = "scale";

    /// <summary>动画时长上限。超过这个量级旧内容还在屏上，读起来比不动画更累。</summary>
    private const int MaxDurationMs = 800;

    /// <summary>所有可选值，供界面提示与 README 使用。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Slide, Fade, Roll, Scale };

    /// <summary>
    /// 把配置里的取值规整成一个认识的样式。
    ///
    /// 认不出来时退回 slide 而不是报错或干脆不动：这是播送中的界面，一个手滑
    /// 多打的字母不该让标题条停住。
    /// </summary>
    public static string Normalize(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Slide;
        var key = configured.Trim().ToLowerInvariant();
        return All.Contains(key) ? key : Slide;
    }

    /// <summary>把配置里的毫秒数夹到可用区间，0 表示关闭动画。</summary>
    public static TimeSpan Duration(int? configuredMs)
    {
        // 0 与负数都当关闭：现场有人会把动画直接设成 0 试试，
        // 为此再发明一个 none 样式并不比夹一下更清楚。
        return TimeSpan.FromMilliseconds(Math.Clamp(configuredMs ?? 0, 0, MaxDurationMs));
    }

    /// <summary>
    /// 播一段新内容：把 <paramref name="element"/> 复位到动画起点，再播入场。
    ///
    /// 只做入场、不做退场，是因为退场发生在下一段开始之前，那时屏幕上还是旧
    /// 内容——先淡出等于让解说员对着已经过时的机位名多看半秒。入场自带
    /// 「从看不见开始」，旧内容被覆盖的过程本身就是退场。
    /// </summary>
    public static void Play(FrameworkElement element, string style, TimeSpan duration)
    {
        // 导播连点切台时上一段动画可能还停在半路。不先摘干净，新动画会和残留的
        // 偏移量叠加，字会飘到屏幕外面去。
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(TranslateTransform.XProperty, null);
        element.BeginAnimation(TranslateTransform.YProperty, null);
        element.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (duration <= TimeSpan.Zero)
        {
            element.Opacity = 1;
            element.RenderTransform = Identity();
            return;
        }

        var translate = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = new TransformGroup { Children = { translate, scale } };

        var board = new Storyboard();
        board.Children.Add(FadeIn(element, duration));

        var travel = TravelOf(element);
        switch (style)
        {
            case Fade:
                // 位移保持 0，只靠淡入。
                break;

            case Roll:
                board.Children.Add(SlideTo(translate, -travel.X, 0, duration));
                break;

            case Scale:
                board.Children.Add(ScaleFrom(scale, duration));
                break;

            default:
                board.Children.Add(SlideTo(translate, 0, travel.Y, duration));
                break;
        }

        board.Begin(element, HandoffBehavior.SnapshotAndReplace, true);
    }

    private static DoubleAnimation FadeIn(DependencyObject target, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = EaseOut(),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        return animation;
    }

    /// <summary>
    /// 位移量按元素自身尺寸取比例，而不是写死像素。
    ///
    /// 标题条的宽度跟着机位名字数走，写死 200px 在短名字上会滑出可视区，
    /// 在长名字上又显得几乎没动。
    /// </summary>
    private static (double X, double Y) TravelOf(FrameworkElement element)
    {
        return (
            Math.Max(element.ActualWidth, 240) * 0.12,
            Math.Max(element.ActualHeight, 60) * 0.12);
    }

    /// <summary>从 (x, y) 滑到原点。只动一个轴，交给调用点决定是哪个。</summary>
    private static DoubleAnimation SlideTo(
        TranslateTransform transform,
        double x,
        double y,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = x != 0 ? x : y,
            To = 0,
            Duration = duration,
            EasingFunction = EaseOut(),
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, new PropertyPath(
            x != 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty));
        return animation;
    }

    /// <summary>
    /// 垂直中线展开。
    ///
    /// 用 ScaleY 而不是 RotateY：真正的三维翻转得给外层挂 PerspectiveCamera，
    /// 而标题条的父容器还要承载两行文字，为一个装饰性效果把整棵视觉树改成
    /// 三维不值得——侧面看过去 RotateY 会把字挤到没法读，ScaleY 至少始终可读。
    /// </summary>
    private static DoubleAnimation ScaleFrom(ScaleTransform scale, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = 0.4,
            To = 1,
            Duration = duration,
            // 轻微回弹，让它读起来像「弹开」而不是「拉伸」。
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 },
        };
        Storyboard.SetTarget(animation, scale);
        Storyboard.SetTargetProperty(animation, new PropertyPath(ScaleTransform.ScaleYProperty));
        return animation;
    }

    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

    private static TransformGroup Identity()
    {
        return new TransformGroup
        {
            Children = { new TranslateTransform(), new ScaleTransform(1, 1) },
        };
    }
}
