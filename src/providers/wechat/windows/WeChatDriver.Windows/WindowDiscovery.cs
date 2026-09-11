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

    /// <summary>
    /// 找到微信主窗口，**不要求它当前可见**。
    /// 用户把微信最小化到托盘时窗口是 hidden 的，普通 <see cref="Discover"/> 会判定不可用；
    /// 用户主动下令的发送允许把窗口拉回来，所以需要先能定位到它。
    /// 主窗口的 class 是 Qt…QWindowIcon，与搜索浮层（QWindowToolSaveBits）和托盘窗口都不冲突。
    /// </summary>
    public static nint FindMainWindowLoose(int pid)
    {
        using var dpi = new DpiAwarenessScope();
        nint best = nint.Zero;
        long bestArea = 0;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var owner);
            if ((int)owner != pid) return true;
            var sb = new StringBuilder(256);
            if (NativeMethods.GetClassName(hWnd, sb, sb.Capacity) <= 0) return true;
            if (!sb.ToString().Contains("QWindowIcon", StringComparison.Ordinal)) return true;
            NativeMethods.GetWindowRect(hWnd, out var rect);
            var area = (long)rect.Width * rect.Height;
            if (area <= bestArea) return true;
            bestArea = area;
            best = hWnd;
            return true;
        }, nint.Zero);
        return best;
    }

    /// <summary>窗口是否既可见又未最小化（后台输入的前提）。</summary>
    public static bool IsWindowUsable(nint hwnd) =>
        hwnd != nint.Zero && NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd);

    /// <summary>
    /// 依次尝试把窗口恢复成可见且非最小化。隐藏到托盘（IsWindowVisible=false）需要用
    /// SW_SHOWNOACTIVATE/SW_SHOW，最小化到任务栏则需要 SW_RESTORE；每级之后都实际校验。
    /// </summary>
    public static bool TryRestoreWindow(nint hwnd)
    {
        foreach (var command in new[]
                 {
                     NativeMethods.SwShowNoActivate, NativeMethods.SwShowNormal,
                     NativeMethods.SwShow, NativeMethods.SwRestore
                 })
        {
            NativeMethods.ShowWindow(hwnd, command);
            Thread.Sleep(400);
            if (IsWindowUsable(hwnd)) return true;
        }
        return false;
    }

    /// <summary>
    /// 定位微信全局搜索的建议/结果浮层。
    /// 它不是主窗口的子窗口，而是同进程的独立顶层窗口（class 含 QWindowToolSaveBits），
    /// 且**每次都会被重建、句柄每次都不同**，所以调用方必须在每轮操作前重新发现，不能缓存。
    /// </summary>
    public static nint FindSearchPopup(int pid)
    {
        using var dpi = new DpiAwarenessScope();
        nint best = nint.Zero;
        long bestArea = 0;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var owner);
            if ((int)owner != pid) return true;
            var sb = new StringBuilder(256);
            if (NativeMethods.GetClassName(hWnd, sb, sb.Capacity) <= 0) return true;
            if (!sb.ToString().Contains("QWindowToolSaveBits", StringComparison.Ordinal)) return true;
            NativeMethods.GetWindowRect(hWnd, out var rect);
            var area = (long)rect.Width * rect.Height;
            if (area <= bestArea) return true;
            bestArea = area;
            best = hWnd;
            return true;
        }, nint.Zero);
        return best;
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
