using System.Drawing;
using WeChatDriver.Windows;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WeChatDriver.Capture;

/// <summary>基于 PrintWindow 的窗口/区域捕获。仅局部捕获，不做全屏/全窗常态截图。</summary>
public static class WindowCapture
{
    private const uint PwRenderFullContent = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>捕获整个窗口客户区为位图。</summary>
    public static Bitmap CaptureWindow(IntPtr hwnd)
    {
        using var dpi = new DpiAwarenessScope();
        // A destroyed handle is the ordinary state of WeChat's transient Qt windows: the client
        // destroys and recreates QWindowToolSaveBits while a search query settles, and a recreated
        // main window leaves the previous handle dead. That is a window identity change, not a
        // capture failure, so it must carry the code the self-healing callers already retry on.
        // Reporting it as capture_failed used to be indistinguishable from a broken screenshot
        // pipeline, and the read path never recovered.
        if (!WindowDiscovery.IsWindowAlive(hwnd))
            throw new WindowAutomationException("window_changed",
                "The target window no longer exists; re-discover it before capturing.");
        if (!GetWindowRect(hwnd, out var rect)) throw new WindowAutomationException("capture_failed", "Cannot read the target window bounds.");
        var w = Math.Max(1, rect.Right - rect.Left);
        var h = Math.Max(1, rect.Bottom - rect.Top);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            if (!PrintWindow(hwnd, hdc, PwRenderFullContent))
                throw new WindowAutomationException("capture_failed", "PrintWindow failed; an empty image is not a valid observation.");
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }

    /// <summary>捕获窗口内的一个相对区域（比例 0~1）。</summary>
    public static Bitmap CaptureRegion(IntPtr hwnd, double x, double y, double width, double height)
    {
        using var full = CaptureWindow(hwnd);
        var px = (int)(full.Width * x);
        var py = (int)(full.Height * y);
        var pw = Math.Max(1, (int)(full.Width * width));
        var ph = Math.Max(1, (int)(full.Height * height));
        px = Math.Clamp(px, 0, full.Width - 1);
        py = Math.Clamp(py, 0, full.Height - 1);
        pw = Math.Min(pw, full.Width - px);
        ph = Math.Min(ph, full.Height - py);
        return full.Clone(new Rectangle(px, py, pw, ph), full.PixelFormat);
    }

    public static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// 从窗口底部算的区域捕获：x/width 为左起比例，yFromBottomPx 为区域底边距窗口底边的像素。
    /// 用于发送按钮等「固定在窗口底部」的控件。
    /// </summary>
    public static Bitmap CaptureRegionFromBottom(IntPtr hwnd, double x, double width, int yFromBottomPx, int heightPx)
    {
        using var full = CaptureWindow(hwnd);
        var px = (int)(full.Width * x);
        var py = full.Height - yFromBottomPx - heightPx;
        var pw = Math.Max(1, (int)(full.Width * width));
        var ph = Math.Max(1, heightPx);
        px = Math.Clamp(px, 0, full.Width - 1);
        py = Math.Clamp(py, 0, full.Height - 1);
        pw = Math.Min(pw, full.Width - px);
        ph = Math.Min(ph, full.Height - py);
        return full.Clone(new Rectangle(px, py, pw, ph), full.PixelFormat);
    }

    /// <summary>
    /// 顶部+底部都锚定的区域捕获：x/width 为左起比例，topPx 为区域顶边距窗口顶边的像素，
    /// bottomPx 为区域底边距窗口底边的像素。区域高度 = 窗口高 - topPx - bottomPx（随窗口高度伸缩）。
    /// 用于消息区/会话列表等「顶部固定 + 底部固定」的可伸缩区域——其垂直边界不随窗口高度变化，
    /// 而「从顶部比例」在窗口高度变化（809→961）时会错位（顶边下移、底边上移，读不到完整消息）。
    /// </summary>
    public static Bitmap CaptureRegionAnchored(IntPtr hwnd, double x, double width, int topPx, int bottomPx)
    {
        using var full = CaptureWindow(hwnd);
        var px = (int)(full.Width * x);
        var py = Math.Clamp(topPx, 0, full.Height - 1);
        var pw = Math.Max(1, (int)(full.Width * width));
        var ph = Math.Max(1, full.Height - bottomPx - py);
        px = Math.Clamp(px, 0, full.Width - 1);
        pw = Math.Min(pw, full.Width - px);
        ph = Math.Min(ph, full.Height - py);
        return full.Clone(new Rectangle(px, py, pw, ph), full.PixelFormat);
    }
}
