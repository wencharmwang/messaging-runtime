using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using WeChatDriver.Core;

namespace WeChatDriver.Windows;

/// <summary>微信窗口发现：进程、主窗口句柄、版本。</summary>
public sealed class WindowDiscovery
{
    private const string ProcessName = "Weixin";
    private const string AltProcessName = "WeChat";

    public static IReadOnlyList<int> FindWeChatProcessIds()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProcessName, AltProcessName };
        return Process.GetProcesses()
            .Where(p => names.Contains(p.ProcessName))
            .Select(p => p.Id)
            .ToList();
    }

    /// <summary>枚举可见顶层窗口；微信 4.x 的 Qt 主窗口有时不公开标题。</summary>
    public static IReadOnlyList<DiscoveredWindow> EnumerateTopLevelWindows()
    {
        using var dpi = new DpiAwarenessScope();
        var result = new List<DiscoveredWindow>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            var title = GetWindowTitle(hWnd);
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            NativeMethods.GetWindowRect(hWnd, out var rect);
            result.Add(new DiscoveredWindow(hWnd, (int)pid, title, rect.Left, rect.Top, rect.Width, rect.Height));
            return true;
        }, nint.Zero);
        return result;
    }

    /// <summary>定位微信主窗口。拒绝多个候选窗口。</summary>
    public static OperationResult<WeChatWindowSession> Discover(Version? expectedVersion = null)
    {
        var pids = FindWeChatProcessIds();
        if (pids.Count == 0)
            return OperationResult<WeChatWindowSession>.Fail(FailureCode.WeChatNotRunning, "微信未运行");

        var candidates = SelectCandidates(EnumerateTopLevelWindows(), pids).ToList();

        if (candidates.Count == 0)
            return OperationResult<WeChatWindowSession>.Fail(FailureCode.WindowNotFound, "未找到微信主窗口");

        var win = candidates[0];
        var version = GetProcessFileVersion((int)win.ProcessId);
        if (version is null)
            return OperationResult<WeChatWindowSession>.Fail(FailureCode.UnknownClientVersion, "无法读取微信版本");

        // 版本不匹配：进入 Degraded。
        var health = expectedVersion is not null && version != expectedVersion
            ? SessionHealth.Degraded
            : SessionHealth.Healthy;

        var session = new WeChatWindowSession(
            win.ProcessId,
            win.Handle,
            version.ToString(),
            LocatorProfileIdBuilder.Build(version, win),
            health,
            DateTimeOffset.Now);

        return OperationResult<WeChatWindowSession>.Ok(session);
    }

    public static IReadOnlyList<DiscoveredWindow> SelectCandidates(
        IReadOnlyList<DiscoveredWindow> windows, IReadOnlyList<int> processIds)
    {
        var candidates = windows.Where(w => processIds.Contains(w.ProcessId)
                && (string.IsNullOrWhiteSpace(w.Title) || w.Title is "微信" or "WeChat" or "Weixin"))
            .Where(w =>
            {
                var dpi = Math.Max(96, GetDpi(w.Handle));
                return w.Width >= 650 * dpi / 120 && w.Height >= 480 * dpi / 120;
            })
            .OrderByDescending(w => (long)w.Width * w.Height)
            .ToList();
        // Qt can expose companion/overlay windows owned by the same process.
        // The chat shell is the largest pane-capable top-level window.
        return candidates.Count == 0 ? [] : [candidates[0]];
    }

    public static Version? GetProcessFileVersion(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var path = p.MainModule?.FileName;
            if (path is null) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(info.FileVersion, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public static string GetWindowTitle(nint hWnd)
    {
        var len = NativeMethods.GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int GetDpi(nint hWnd) => (int)NativeMethods.GetDpiForWindow(hWnd);

    public static bool IsInteractiveSession()
    {
        var active = NativeMethods.WTSGetActiveConsoleSessionId();
        var current = Process.GetCurrentProcess().SessionId;
        return active == current;
    }

    /// <summary>取窗口的客户区矩形（屏幕坐标）。</summary>
    public static (int X, int Y, int Width, int Height) GetWindowBounds(nint hwnd)
    {
        using var dpi = new DpiAwarenessScope();
        NativeMethods.GetWindowRect(hwnd, out var r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

public sealed record DiscoveredWindow(
    [property: JsonIgnore] nint Handle,
    int ProcessId,
    string Title,
    int X,
    int Y,
    int Width,
    int Height);

internal static class LocatorProfileIdBuilder
{
    public static string Build(Version version, DiscoveredWindow win)
    {
        var dpi = WindowDiscovery.GetDpi(win.Handle);
        return $"wechat-win-{version}-dpi{dpi}-layout1";
    }
}
