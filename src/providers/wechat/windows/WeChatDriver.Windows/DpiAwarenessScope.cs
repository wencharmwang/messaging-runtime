using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WeChatDriver.Windows;

/// <summary>HTTP worker threads and dotnet DLL launches must use the same physical pixels as PrintWindow.</summary>
public sealed class DpiAwarenessScope : IDisposable
{
    private readonly nint _previous;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    public DpiAwarenessScope()
    {
        _previous = SetThreadDpiAwarenessContext(new nint(-4)); // PER_MONITOR_AWARE_V2
        if (_previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot establish physical-pixel coordinates.");
    }

    public void Dispose() => SetThreadDpiAwarenessContext(_previous);
}
