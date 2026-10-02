namespace CommentatorApp.Models;

public class AppConfig
{
    public string ServerUrl { get; set; } = "http://192.168.1.100:3000";
    public string WsUrl { get; set; } = "ws://192.168.1.100:3000/ws";
    public int ProjectId { get; set; } = 1;
    public string Role { get; set; } = "commentator";

    /// <summary>
    /// 登录凭据。
    ///
    /// 解说端此前**连登录这个概念都没有**，在服务端是匿名的：任何人拿到那个
    /// 地址就能监听整个项目的实时消息。后端把 REQUIRE_PROJECT_MEMBERSHIP 打开
    /// 之后，WebSocket 会校验「这个账号是不是该项目的成员」，届时没有凭据的
    /// 解说端会直接连不上。
    ///
    /// 留空表示不登录，行为与改动前一致——所以可以先把账号发下去、确认现场
    /// 都能连上，再打开后端那个开关。
    /// </summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>
    /// 赛事名的兜底值。
    ///
    /// 标题条顶行显示的是项目名，正常由 <see cref="ProjectId"/> 去服务端查，
    /// 不需要人工干预。这里只在两种情况下顶上去：服务端查不到（网络受限的
    /// 内网现场、或项目刚建还没起服务），或者解说员确实想显示一个比项目名
    /// 更贴画面的说法（例如项目叫「秋季运动会」而画面上想写「射箭世界杯总决赛」）。
    /// </summary>
    public string EventName { get; set; } = "";

    /// <summary>
    /// 「正在播送 ↔ 即将播送」切换动画，取值见
    /// <see cref="CommentatorApp.Services.TitleTransition"/>。
    ///
    /// 留空按 slide 处理。不同解说员对动效的耐受度差别很大——有人觉得滑动
    /// 很专业，有人觉得播送中晃眼，所以做成现场可改的配置而不是写死。
    /// </summary>
    public string Transition { get; set; } = "slide";

    /// <summary>
    /// 切换动画时长（毫秒）。
    ///
    /// 上限压在 800：解说员读机位名靠的是余光扫一眼，超过这个量级旧内容还在
    /// 屏幕上就变成两行字并排，比不动画更难读。
    /// </summary>
    public int TransitionMs { get; set; } = 300;

    /// <summary>
    /// 包装画布尺寸，单位**物理像素**。默认 1920 × 1080。
    ///
    /// 这一版窗口不再是一块贴在屏幕上的小卡片，而是**要被芯象当窗口源抓走、
    /// 叠进直播画面的包装层**，所以窗口本身必须等于一张 16:9 画面：窄条窗口
    /// （310:87 ≈ 3.56:1）被塞进 16:9 输出时只能裁两侧，先被吃掉的就是右端竖条。
    ///
    /// 必须是 16:9。非 16:9 的值会被忽略、退回 1920 × 1080——采集端是按输出
    /// 画面比例缩放窗口源的，比例不对就是变形或者裁边。
    ///
    /// 写物理像素而不是 DIP：桌面缩放 150% 时，1920px 的包装层仍然是 1920 个
    /// 物理像素，不会变成 2880 再被缩回去。
    /// </summary>
    public int CanvasWidth { get; set; } = 1920;

    /// <summary>见 <see cref="CanvasWidth"/>。</summary>
    public int CanvasHeight { get; set; } = 1080;

    /// <summary>
    /// 卡片宽度占**画面**宽度的比例。
    ///
    /// 默认 0.21 是照央视奥运频道量的——它那个下三分之一字幕条就占画面的
    /// 这么宽。运行时会被夹在 0.05..0.60。
    ///
    /// 卡片摆在画面里的位置由 <see cref="MarginBottomRatio"/> /
    /// <see cref="MarginRightRatio"/> 决定，16:9 的下三分之一是包装的惯例位置。
    /// </summary>
    public double CardWidthRatio { get; set; } = 0.21;

    /// <summary>
    /// 卡片下边缘到画面下边缘的比例。
    ///
    /// 压得太贴边会被电视机自己的字幕栏压住，留 6%。
    /// </summary>
    public double MarginBottomRatio { get; set; } = 0.06;

    /// <summary>卡片右边缘到画面右边缘的比例。</summary>
    public double MarginRightRatio { get; set; } = 0.04;

    /// <summary>
    /// 机位名水平偏移（逻辑像素，最终图片尺寸下的像素）。
    ///
    /// 负数向左、正数向右。0 = 默认位置。
    /// 与 tools/render_title_preview.py 里的 PROG_OFFSET_X 对应。
    /// </summary>
    public double ProgOffsetX { get; set; } = -25;
}
