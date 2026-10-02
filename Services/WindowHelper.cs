using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CommentatorApp.Services;

/// <summary>
/// Overlay 窗口要用的 Win32 扩展能力：鼠标穿透、置顶兜底、多显示器定位。
///
/// 只封装「透明浮层才需要」的那几件事，不要把普通窗口工具也塞进来。
/// </summary>
public static class WindowHelper
{
    private const int GwlExStyle = -20;

    /// <summary>鼠标事件直接穿到下层窗口，Overlay 自己不接收点击。</summary>
    private const int WsExTransparent = 0x00000020;

    /// <summary>分层窗口。AllowsTransparency 打开后 WPF 自己会设，这里只读不写。</summary>
    private const int WsExLayered = 0x00080000;

    /// <summary>取「离这个窗口最近」的那台显示器：跨屏时按面积最大的那块算。</summary>
    private const uint MonitorDefaultToNearest = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public int DwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 开启或关闭鼠标穿透。
    ///
    /// 穿透打开后，点击会直接落到 Overlay 下面的窗口上——投到直播画面时，
    /// 导播在下面操作不会被这块标题条挡住。
    ///
    /// 32/64 位要走不同的入口：SetWindowLong 在 64 位进程里会截断指针，
    /// 表现是设置偶发不生效、GetLastError 报参数错误。
    /// </summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var ex = GetExStyle(hwnd);
        ex = enabled ? ex | WsExTransparent : ex & ~WsExTransparent;
        SetExStyle(hwnd, ex);
    }

    /// <summary>当前是否已经处于鼠标穿透状态。</summary>
    public static bool IsClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        return (GetExStyle(hwnd) & WsExTransparent) != 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 取窗口**当前所在那块**显示器的工作区，单位是物理像素。
    ///
    /// 为什么不能用 <see cref="SystemParameters.WorkArea"/>：它返回的是
    /// **主显示器**的工作区，而且单位是「系统 DPI 下的 DIP」。解说端的窗口
    /// 会被拖到副屏上，机器上混着不同缩放比例的显示器时（1080p 主屏 +
    /// 4K 副屏是很常见的现场配置），拿主屏的坐标去摆副屏上的窗口，
    /// 算出来的位置会横跨两块屏幕的边界——现场看到的就是「标题条只剩
    /// 一半，另一半在屏外」。
    ///
    /// 物理像素是唯一不会算错的单位：不需要知道任何一块屏的缩放比例。
    /// </summary>
    public static Rect GetWorkAreaPx(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        // 句柄还没建出来时（构造函数里就会调 Layout）拿不到显示器信息，
        // 让调用方跳过摆位而不是拿主屏凑合——那正是上面那个 bug。
        if (hwnd == IntPtr.Zero) return Rect.Empty;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return Rect.Empty;

        var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return Rect.Empty;

        var work = info.RcWork;
        return new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>
    /// 在已打开的窗口里找某一类。
    ///
    /// 两个窗口要互相找到对方：标题窗口铺满屏幕并开了鼠标穿透之后，就再也点不到了，
    /// 「关掉穿透」这个入口只能放在状态窗口上。
    /// </summary>
    public static T? FindWindow<T>() where T : Window
    {
        foreach (var window in Application.Current.Windows)
        {
            if (window is T hit) return hit;
        }
        return null;
    }

    /// <summary>
    /// 只挪位置，不改大小。
    ///
    /// 位置用物理像素直接调 SetWindowPos，而不是赋 Window.Left/Top：
    /// 后者的单位跟着窗口所在显示器的缩放走，跨屏时算出来的值和实际
    /// 落点对不上。带上 NOSIZE 是必须的，否则会触发 SizeChanged，
    /// 而 SizeChanged 里又会调 Layout —— 无限循环。
    ///
    /// 调用方要保证 x/y 传的是虚拟桌面绝对坐标（可能为负）。
    /// </summary>
    public static bool MoveToPx(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static IntPtr GetExStyle(IntPtr hwnd)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GwlExStyle) : (IntPtr)GetWindowLong32(hwnd, GwlExStyle);
    }

    private static void SetExStyle(IntPtr hwnd, IntPtr ex)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, ex);
        else SetWindowLong32(hwnd, GwlExStyle, ex.ToInt32());
    }
}
