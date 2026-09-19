using System.Runtime.InteropServices;

namespace WeChatDriver.Windows;

public sealed class WindowAutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record WindowInputState(int ProcessId, bool Visible, bool Minimized, int Width, int Height, bool Foreground, int Dpi=120);

public interface IWindowInputNative
{
    WindowInputState Inspect(nint hwnd);
    bool Activate(nint hwnd);
    bool OwnsPoint(nint hwnd, int x, int y);
    void Click(nint hwnd, int x, int y, bool background);
    void Wheel(nint hwnd, int x, int y, int delta, bool background);
    void Chord(nint hwnd, ushort[] keys);
    void BackgroundChord(nint hwnd, ushort[] keys) =>
        throw new WindowAutomationException("background_key_unsupported", "Background shortcut input is unavailable.");
    void Character(nint hwnd, int x, int y, char value) => throw new WindowAutomationException("background_text_unsupported", "Background text input is unavailable.");
    /// <summary>Post a single virtual key (down+up) to the window's own queue. No focus needed.</summary>
    void Key(nint hwnd, ushort virtualKey) => throw new WindowAutomationException("background_key_unsupported", "Background key input is unavailable.");
}

/// <summary>Coordinates are window-local physical pixels. Every event revalidates the bound HWND/PID and geometry.</summary>
public sealed class TargetWindowInput(nint hwnd, int processId, IWindowInputNative? native = null)
{
    private readonly IWindowInputNative _native = native ?? new Win32WindowInput(processId);
    private int _width, _height, _dpi;

    public WindowInputState Validate()
    {
        var state = _native.Inspect(hwnd);
        if (state.ProcessId != processId || processId <= 0)
            throw new WindowAutomationException("window_changed", "The target window no longer belongs to the connected WeChat process.");
        if (!state.Visible || state.Minimized)
            throw new WindowAutomationException("window_unavailable", "Restore WeChat in the current desktop session; background operations do not activate or restore windows.");
        if (_width > 0 && (state.Width != _width || state.Height != _height || (_dpi>0 && state.Dpi!=_dpi)))
            throw new WindowAutomationException("layout_changed", "WeChat geometry changed after observation. Observe again before acting.");
        return state;
    }

    public void BeginObservation() { _width=0; _height=0; _dpi=0; Validate(); }

    public void ExpectGeometry(int width, int height, int dpi=0)
    {
        _width = width; _height = height; _dpi=dpi;
        Validate();
    }

    /// <summary>
    /// Bring WeChat to the foreground for real (SendInput) keyboard work.
    /// <paramref name="allowAggressiveActivation"/> is passed straight through to
    /// <see cref="InputHelper.TryActivate"/>: the last rung of that ladder minimizes and restores
    /// the window, which is visible on screen and therefore reserved for paths a person asked for.
    /// </summary>
    public void ActivateForWriting(bool allowAggressiveActivation = false)
    {
        Validate();
        // SetForegroundWindow alone is refused for background processes often enough to
        // matter, so use the guarded ladder instead of the single call.
        if (!InputHelper.TryActivate(hwnd, allowAggressiveActivation))
            throw new WindowAutomationException("foreground_unavailable",
                "WeChat could not be brought to the foreground, so no real input was sent.");
        RequireForeground();
    }

    private void RequireForeground()
    {
        if (!Validate().Foreground)
            throw new WindowAutomationException("foreground_lost", "WeChat is not foreground. No keyboard input was sent.");
    }

    private void CheckPoint(int x, int y, bool background)
    {
        var state = Validate();
        if (x < 0 || y < 0 || x >= state.Width || y >= state.Height)
            throw new WindowAutomationException("invalid_target", "The observed point is outside the target window.");
        if (!background)
        {
            RequireForeground();
            if (!_native.OwnsPoint(hwnd, x, y))
                throw new WindowAutomationException("window_occluded", "Another window covers the target point. No global input was sent.");
        }
    }

    public void Click(int x, int y, bool background = true)
    {
        CheckPoint(x, y, background);
        _native.Click(hwnd, x, y, background);
    }

    public void Wheel(int x, int y, int delta, int count = 1, bool background = true)
    {
        for (var i = 0; i < count; i++)
        {
            CheckPoint(x, y, background);
            _native.Wheel(hwnd, x, y, delta, background);
            if (i + 1 < count) Thread.Sleep(12);
        }
    }

    public void Chord(params ushort[] keys)
    {
        RequireForeground();
        _native.Chord(hwnd, keys);
    }

    /// <summary>Post a shortcut only to the bound window's queue; never touches global focus.</summary>
    public void SendBackgroundChord(params ushort[] keys)
    {
        Validate();
        _native.BackgroundChord(hwnd, keys);
    }

    /// <summary>Post one virtual key to the window queue. Works without the foreground.</summary>
    public void SendVirtualKey(ushort virtualKey) => _native.Key(hwnd, virtualKey);

    public void WriteText(int x, int y, string text, Action verifyTarget)
    {
        // Never turn message text into Enter/Tab/shortcut events.
        if (text.Any(char.IsControl))
            throw new WindowAutomationException("background_text_unsupported", "Background character input requires text without control characters.");
        CheckPoint(x,y,background:true);
        verifyTarget();
        foreach (var value in text)
        {
            CheckPoint(x, y, background: true);
            verifyTarget();
            _native.Character(hwnd, x, y, value);
        }
    }
}

internal sealed class Win32WindowInput(int expectedProcessId) : IWindowInputNative
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; public Point(int x, int y) { X=x; Y=y; } }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint hwnd, Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    public WindowInputState Inspect(nint hwnd)
    {
        using var dpi = new DpiAwarenessScope();
        if (!IsWindow(hwnd)) return new(0, false, false, 0, 0, false);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        NativeMethods.GetWindowRect(hwnd, out var r);
        return new((int)pid, NativeMethods.IsWindowVisible(hwnd), NativeMethods.IsIconic(hwnd), r.Width, r.Height,
            NativeMethods.GetForegroundWindow() == hwnd, (int)NativeMethods.GetDpiForWindow(hwnd));
    }

    public bool Activate(nint hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    private static Point ScreenPoint(nint hwnd, int x, int y)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) throw new WindowAutomationException("window_changed", "Cannot locate target window.");
        return new Point(r.Left+x, r.Top+y);
    }

    public bool OwnsPoint(nint hwnd, int x, int y)
    {
        using var dpi = new DpiAwarenessScope();
        return GetAncestor(WindowFromPoint(ScreenPoint(hwnd,x,y)), 2) == hwnd;
    }

    private static (nint hwnd, Point point) Receiver(nint root, Point screen)
    {
        NativeMethods.GetWindowThreadProcessId(root, out var expectedPid);
        var receiver = root;
        for (var i=0;i<16;i++)
        {
            var local = screen;
            if (!ScreenToClient(receiver, ref local)) throw new WindowAutomationException("window_changed", "Cannot map input coordinates.");
            var child = ChildWindowFromPointEx(receiver,local,7); // skip invisible/disabled/transparent
            if (child == 0 || child == receiver) return (receiver,local);
            NativeMethods.GetWindowThreadProcessId(child,out var childPid);
            if (childPid != expectedPid || GetAncestor(child,2) != root)
                throw new WindowAutomationException("invalid_target", "Input receiver is outside the bound window.");
            receiver=child;
        }
        throw new WindowAutomationException("invalid_target", "Input receiver tree is too deep.");
    }

    private static nint Coordinates(Point p) => new(unchecked((int)((uint)(ushort)p.X | ((uint)(ushort)p.Y << 16))));

    private void Deliver(nint root, nint receiver, uint message, nuint wParam, nint lParam)
    {
        NativeMethods.GetWindowThreadProcessId(root,out var rootPid);
        NativeMethods.GetWindowThreadProcessId(receiver,out var pid);
        if (rootPid != expectedProcessId || pid != expectedProcessId || GetAncestor(receiver,2) != root)
            throw new WindowAutomationException("window_changed", "The input receiver changed.");
        if (SendMessageTimeout(receiver,message,wParam,lParam,3,1500,out _) == 0)
            throw new WindowAutomationException("background_unavailable", "The bound window did not accept a background event; no global input fallback was used.");
    }

    public void Click(nint hwnd, int x, int y, bool background)
    {
        using var dpi = new DpiAwarenessScope();
        if (!background)
        {
            var screen=ScreenPoint(hwnd,x,y);
            InputHelper.ClickAtGuarded(hwnd,screen.X,screen.Y);
            return;
        }
        var target=Receiver(hwnd,ScreenPoint(hwnd,x,y));
        var position=Coordinates(target.point);
        Deliver(hwnd,target.hwnd,0x0200,0,position); // WM_MOUSEMOVE, target queue only
        Deliver(hwnd,target.hwnd,0x0201,1,position);
        Deliver(hwnd,target.hwnd,0x0202,0,position);
    }

    public void Wheel(nint hwnd,int x,int y,int delta,bool background)
    {
        using var dpi = new DpiAwarenessScope();
        var screen=ScreenPoint(hwnd,x,y);
        if (!background) { InputHelper.SendMouseWheelGuarded(hwnd,delta,screen.X,screen.Y); return; }
        var target=Receiver(hwnd,screen);
        Deliver(hwnd,target.hwnd,0x0200,0,Coordinates(target.point));
        Deliver(hwnd,target.hwnd,0x020A,unchecked((nuint)((uint)(ushort)delta << 16)),Coordinates(screen));
    }

    public void Chord(nint hwnd,ushort[] keys) => InputHelper.SendChordGuarded(hwnd,keys);

    public void BackgroundChord(nint hwnd,ushort[] keys)
    {
        // Qt routes top-level key messages to the focused editor inside this exact popup.
        // Keep modifiers down across the inner keys, then release in reverse order.
        foreach(var key in keys)
            Deliver(hwnd,hwnd,0x0100,key,0x00010001); // WM_KEYDOWN
        foreach(var key in keys.Reverse())
            Deliver(hwnd,hwnd,0x0101,key,unchecked((nint)0xC0010001UL)); // WM_KEYUP
    }

    public void Character(nint hwnd,int x,int y,char value)
    {
        using var dpi = new DpiAwarenessScope();
        Deliver(hwnd,hwnd,0x0102,value,1); // Qt routes top-level keyboard events to its focused widget.
    }

    public void Key(nint hwnd,ushort virtualKey)
    {
        using var dpi = new DpiAwarenessScope();
        // Same routing as Character: Qt receives the posted key on its focused widget,
        // so this works without the window being foreground. Used for Escape cleanup.
        Deliver(hwnd,hwnd,0x0100,virtualKey,0x00010001); // WM_KEYDOWN
        Deliver(hwnd,hwnd,0x0101,virtualKey,unchecked((nint)0xC0010001UL)); // WM_KEYUP
    }

}
