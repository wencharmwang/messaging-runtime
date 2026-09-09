using WeChatDriver.Core;

namespace WeChatDriver.Windows;

/// <summary>
/// 微信主窗口健康诊断与自愈。
/// 解决「窗口尺寸/DPI 变化、最小化到托盘（窗口被移到屏幕外）」导致的静默读空：
/// 最小化到托盘时 PrintWindow 截空 → OCR 空 → MCP list/search/read 全部空返回。
/// 本类提供：状态诊断（最小化/屏幕外/尺寸/DPI/偏离标定）、自动恢复（还原 + 置前）。
/// </summary>
public static class WindowDiagnostics
{
    /// <summary>标定基准：与 Locator Profile（wechat-win-4.1.13.12-dpi120-layout1）一致。</summary>
    public const int ReferenceDpi = 120;
    public const int ReferenceWidth = 1118;
    public const int ReferenceHeight = 809;

    // 尺寸偏差容差（与标定窗口相比），超过视为布局可能变化 → Degraded。
    private const double WidthTolerance = 0.35;
    private const double HeightTolerance = 0.35;

    /// <summary>诊断窗口当前状态，返回结构化报告（不修改窗口）。</summary>
    public static WindowHealthReport Diagnose(nint hwnd)
    {
        var issues = new List<string>();
        var processRunning = WindowDiscovery.FindWeChatProcessIds().Count > 0;

        if (hwnd == nint.Zero || !NativeMethods.IsWindowVisible(hwnd))
        {
            return new WindowHealthReport(
                processRunning, false, false, false, 0, 0, 0, 0, 0,
                "Unusable", "未找到可见的微信主窗口", new[] { "主窗口不可见或不存在" });
        }

        NativeMethods.GetWindowRect(hwnd, out var r);
        var w = r.Width;
        var h = r.Height;
        var dpi = (int)NativeMethods.GetDpiForWindow(hwnd);
        var minimized = NativeMethods.IsIconic(hwnd);
        var offscreen = IsOffscreen(r);

        var status = "Healthy";

        if (minimized)
        {
            status = "Minimized";
            issues.Add($"窗口已最小化（iconic），rect=({r.Left},{r.Top})");
        }
        else if (offscreen)
        {
            status = "Offscreen";
            issues.Add($"窗口在屏幕外（rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) 与虚拟屏幕无交集）");
        }
        else if (w <= 0 || h <= 0)
        {
            status = "Unusable";
            issues.Add($"窗口尺寸异常 w={w} h={h}");
        }

        // DPI 偏离：像素锚会按 DPI 缩放，但标记 Degraded 提示。
        if (dpi > 0 && dpi != ReferenceDpi)
        {
            if (status == "Healthy") status = "Degraded";
            issues.Add($"DPI={dpi}（标定 {ReferenceDpi}），像素锚将按 DPI 缩放");
        }

        // 尺寸偏离标定。
        if (w > 0 && h > 0)
        {
            var wDev = Math.Abs(w - ReferenceWidth) / (double)ReferenceWidth;
            var hDev = Math.Abs(h - ReferenceHeight) / (double)ReferenceHeight;
            if (wDev > WidthTolerance || hDev > HeightTolerance)
            {
                if (status == "Healthy") status = "Degraded";
                issues.Add($"窗口尺寸 {w}x{h} 偏离标定 {ReferenceWidth}x{ReferenceHeight}（宽 {wDev:P0} / 高 {hDev:P0}）");
            }
        }

        var detail = issues.Count == 0 ? $"窗口 {w}x{h} @ {dpi} DPI" : string.Join("；", issues);
        return new WindowHealthReport(processRunning, true, minimized, offscreen, w, h, r.Left, r.Top, dpi, status, detail, issues);
    }

    /// <summary>判断窗口矩形是否完全落在虚拟屏幕之外（最小化/拖出屏幕）。</summary>
    private static bool IsOffscreen(NativeMethods.RECT r)
    {
        var vl = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var vt = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var vr = vl + NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var vb = vt + NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
        return r.Right <= vl || r.Left >= vr || r.Bottom <= vt || r.Top >= vb;
    }

    /// <summary>恢复窗口（还原最小化 + 置前），返回恢复后的诊断。</summary>
    public static WindowHealthReport Recover(nint hwnd)
    {
        if (hwnd == nint.Zero) return Diagnose(hwnd);

        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(hwnd);
        Thread.Sleep(600);
        return Diagnose(hwnd);
    }

    /// <summary>诊断；若最小化/屏幕外则自动恢复并复诊（最多恢复一次）。</summary>
    public static WindowHealthReport DiagnoseAndRecover(nint hwnd)
    {
        var report = Diagnose(hwnd);
        if (report.Status is "Minimized" or "Offscreen")
            report = Recover(hwnd);
        return report;
    }
}
