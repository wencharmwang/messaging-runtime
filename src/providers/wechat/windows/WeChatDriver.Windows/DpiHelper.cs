namespace WeChatDriver.Windows;

/// <summary>DPI 与交互会话检查辅助。</summary>
public static class DpiHelper
{
    public static int GetDpi(nint hWnd) => (int)NativeMethods.GetDpiForWindow(hWnd);

    public static double ScaleFactor(nint hWnd) => GetDpi(hWnd) / 96.0;

    public static bool IsMinimized(nint hWnd) => NativeMethods.IsIconic(hWnd);

    public static bool IsForeground(nint hWnd) => NativeMethods.GetForegroundWindow() == hWnd;

    public static bool SetForeground(nint hWnd) => NativeMethods.SetForegroundWindow(hWnd);
}
