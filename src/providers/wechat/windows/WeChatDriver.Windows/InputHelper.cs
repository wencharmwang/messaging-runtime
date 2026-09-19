using System.Runtime.InteropServices;
using System.Text;

namespace WeChatDriver.Windows;

/// <summary>
/// 正常桌面输入能力：系统剪贴板 + SendInput 键鼠注入。
/// 仅用于已授权窗口的草稿写入与发送，禁止无人值守自动发送。
/// </summary>
public static class InputHelper
{
    private const int CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    private const ushort VkControl = 0x11;
    private const ushort VkKeyV = 0x56;
    private const ushort VkReturn = 0x0D;
    private const ushort VkKeyA = 0x41;
    private const ushort VkDelete = 0x2E;
    private const ushort VkKeyF = 0x46;
    private const ushort VkEscape = 0x1B;

    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    // ---- 剪贴板 ----

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public static bool IsForegroundWindow(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>把文本写入系统剪贴板（CF_UNICODETEXT）。</summary>
    public static void SetClipboardText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("OpenClipboard 失败（剪贴板被占用）");

        try
        {
            EmptyClipboard();
            var hMem = GlobalAlloc(GmemMoveable | GmemZeroInit, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero)
                throw new InvalidOperationException("GlobalAlloc 失败");

            var p = GlobalLock(hMem);
            if (p == IntPtr.Zero)
                throw new InvalidOperationException("GlobalLock 失败");

            Marshal.Copy(bytes, 0, p, bytes.Length);
            GlobalUnlock(hMem);

            if (SetClipboardData(CfUnicodeText, hMem) == IntPtr.Zero)
                throw new InvalidOperationException("SetClipboardData 失败");
            // 成功后系统接管 hMem，不释放。
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>前台化窗口。</summary>
    public static bool BringToForeground(IntPtr hwnd)
    {
        NoteSelfActivation();
        return SetForegroundWindow(hwnd);
    }

    /// <summary>目标窗口当前是否为前台窗口。</summary>
    public static bool IsForeground(nint hwnd) => GetForegroundWindow() == hwnd;

    // 把微信拉到前台的是我们自己（读取、搜索、发送都会这么做）。前台是微信这件事因此不能
    // 直接当成"用户在用微信"——实测：整理流程自己激活了微信，紧接着的读取就被判成
    // wechat_in_use 而拒绝，助手于是把用户点名要读的会话一个个跳过。
    private static int _selfActivationTick;
    private static bool _hasSelfActivated;

    /// <summary>记下"这是我们自己抢的前台"，供 <see cref="SelfActivatedMoreRecentlyThanInput"/> 判断。</summary>
    public static void NoteSelfActivation()
    {
        _selfActivationTick = Environment.TickCount;
        _hasSelfActivated = true;
    }

    /// <summary>
    /// 前台是微信，但把它拉到前台的是我们自己，而且用户在之后没有碰过键鼠 —— 这不是
    /// "用户正在用微信"，是我们在自问自答。
    /// </summary>
    public static bool SelfActivatedMoreRecentlyThanInput(double windowSeconds)
    {
        if (!_hasSelfActivated) return false;
        var sinceSelf = unchecked((uint)Environment.TickCount - _selfActivationTick) / 1000.0;
        if (sinceSelf > windowSeconds) return false;
        // 用户在我们激活之后又动过键鼠 → 那才是真的在操作，让开。
        return IdleSeconds() >= sinceSelf;
    }

    /// <summary>
    /// 系统范围的空闲秒数（距最后一次键鼠输入）。用来区分「用户真的在操作微信」和
    /// 「只是把微信开着摆在最前面」——后者不该无限期拦住自动读取。
    /// GetLastInputInfo 是全系统计数，调用方必须先确认目标窗口确实在前台再使用它。
    /// </summary>
    public static double IdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;   // 取不到就当作"正在操作"，保持保守
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return elapsed / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>当前前台窗口句柄，供调用方在临时抢前台后还原。</summary>
    public static nint CurrentForeground() => GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>当前光标屏幕坐标，供调用方在真实点击后还原。</summary>
    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>把光标移回原位。</summary>
    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    /// <summary>
    /// 尽力把窗口切到前台。Windows 只允许「前台进程」或「刚收到过输入事件」的进程调用
    /// SetForegroundWindow，后台服务进程会间歇性被拒（实测约半数失败），故逐级兜底；
    /// 每级之后都实际校验前台窗口，全部失败返回 false（调用方不得继续发真实输入）。
    ///
    /// <paramref name="allowAggressiveActivation"/> 只对**用户主动下令**的路径开放：最后一级
    /// 会把目标窗口最小化再还原，这是明确的抢前台动作，屏幕上一定看得见。后台自动回复若走到
    /// 这一步，就等于在用户打字时把微信甩到最前面 —— 所以后台路径保持 false，宁可直接失败。
    /// </summary>
    public static bool TryActivate(nint hwnd, bool allowAggressiveActivation = false)
    {
        if (IsForeground(hwnd)) return true;

        // 从这里开始，把微信拉到前台的是我们自己。
        NoteSelfActivation();

        // 1) 直接请求。
        SetForegroundWindow(hwnd);
        if (Settle(hwnd)) return true;

        // 2) 轻敲 ALT：让本进程被记为「最近一次输入事件」的接收者，随后请求通常被接受。
        TapKey(NativeMethods.VkMenu);
        NativeMethods.SetForegroundWindow(hwnd);
        if (Settle(hwnd)) return true;

        // 3) 与当前前台线程共享输入队列后再请求。
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 &&
                       NativeMethods.AttachThreadInput(NativeMethods.GetCurrentThreadId(), foregroundThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(NativeMethods.GetCurrentThreadId(), foregroundThread, false);
        }
        if (Settle(hwnd)) return true;

        // 4) 先置顶（不激活）再请求。
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTop, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        NativeMethods.SetForegroundWindow(hwnd);
        if (Settle(hwnd)) return true;

        // 5) 最小化再还原，绕开前台锁。这一级在屏幕上完全可见，因此只限用户主动下令的路径。
        if (!allowAggressiveActivation) return false;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
        Thread.Sleep(200);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        Thread.Sleep(300);
        NativeMethods.SetForegroundWindow(hwnd);
        return Settle(hwnd);
    }

    /// <summary>
    /// 把前台还给调用前的窗口（best effort，失败不影响已完成的读写）。
    ///
    /// 只调一次 SetForegroundWindow 是不够的：前台锁会让后台进程的请求静默失败（见
    /// <see cref="TryActivate"/> 的实测记录），而这一次失败就把微信留在了用户原本正在打字的
    /// 应用前面 —— 也就是用户感觉到的「抢焦点」。轻敲 ALT 让本进程被记为「刚收到输入事件」，
    /// 仍不行就与当前前台线程共享输入队列后重试；这两条是 Windows 允许的合法绕行，不需要
    /// 把窗口最小化再还原。
    /// </summary>
    public static void RestoreForeground(nint previous)
    {
        if (previous == IntPtr.Zero || IsForeground(previous)) return;
        if (NativeMethods.SetForegroundWindow(previous) && SettleForeground(previous)) return;

        TapKey(NativeMethods.VkMenu);
        if (NativeMethods.SetForegroundWindow(previous) && SettleForeground(previous)) return;

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 &&
                       NativeMethods.AttachThreadInput(NativeMethods.GetCurrentThreadId(), foregroundThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(previous);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(NativeMethods.GetCurrentThreadId(), foregroundThread, false);
        }
    }

    /// <summary>还原路径上的轻量确认：比 <see cref="Settle"/> 短，还原不该再拖慢一次读取。</summary>
    private static bool SettleForeground(nint hwnd)
    {
        Thread.Sleep(120);
        return IsForeground(hwnd);
    }

    private static bool Settle(nint hwnd)
    {
        Thread.Sleep(350);
        return IsForeground(hwnd);
    }

    private static void TapKey(byte vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, 0);
        Thread.Sleep(30);
        NativeMethods.keybd_event(vk, 0, NativeMethods.KeyEventKeyUp, 0);
        Thread.Sleep(120);
    }

    /// <summary>在屏幕绝对坐标单击。</summary>
    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        var down = MouseInput(0x0002); // MOUSEEVENTF_LEFTDOWN
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(50);
        var up = MouseInput(0x0004);   // MOUSEEVENTF_LEFTUP
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
    }

    /// <summary>发送 Ctrl+V。</summary>
    public static void SendCtrlV()
    {
        SendKey(VkControl, false);
        Thread.Sleep(30);
        SendKey(VkKeyV, false);
        SendKey(VkKeyV, true);
        SendKey(VkControl, true);
        Thread.Sleep(80);
    }

    /// <summary>发送 Ctrl+A（全选，用于清空输入框）。</summary>
    public static void SendCtrlA()
    {
        SendKey(VkControl, false);
        Thread.Sleep(30);
        SendKey(VkKeyA, false);
        SendKey(VkKeyA, true);
        SendKey(VkControl, true);
        Thread.Sleep(50);
    }

    /// <summary>发送 Enter（仅在发送快捷键确认为 Enter 时使用）。</summary>
    public static void SendEnter()
    {
        SendKey(VkReturn, false);
        SendKey(VkReturn, true);
        Thread.Sleep(80);
    }

    /// <summary>发送 Delete（配合 Ctrl+A 清空输入框）。</summary>
    public static void SendDelete()
    {
        SendKey(VkDelete, false);
        SendKey(VkDelete, true);
        Thread.Sleep(50);
    }

    /// <summary>发送 Ctrl+F（微信全局搜索）。</summary>
    public static void SendCtrlF()
    {
        SendKey(VkControl, false);
        Thread.Sleep(30);
        SendKey(VkKeyF, false);
        SendKey(VkKeyF, true);
        SendKey(VkControl, true);
        Thread.Sleep(120);
    }

    /// <summary>发送 Esc（关闭搜索/弹层）。</summary>
    public static void SendEscape()
    {
        SendKey(VkEscape, false);
        SendKey(VkEscape, true);
        Thread.Sleep(80);
    }

    /// <summary>发送 Ctrl+Enter（微信 4.x 的发送快捷键）。</summary>
    public static void SendCtrlEnter()
    {
        SendKey(VkControl, false);
        Thread.Sleep(20);
        SendKey(VkReturn, false);
        Thread.Sleep(20);
        SendKey(VkReturn, true);
        SendKey(VkControl, true);
        Thread.Sleep(120);
    }

    /// <summary>在指定屏幕坐标滚动鼠标滚轮。delta 正值向上滚（看更早内容），负值向下。</summary>
    public static void SendMouseWheel(int delta, int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = x,
                    dy = y,
                    mouseData = (uint)delta,
                    dwFlags = 0x0800, // MOUSEEVENTF_WHEEL
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
    }

    /// <summary>
    /// 在指定屏幕坐标连续滚动 N 次滚轮（每次 1-notch）。用于「滚到底/滚到顶」这类需要大量滚动的场景。
    /// 只定位一次光标、用短间隔连续发事件（微信 Qt 对单次大 delta 不线性累加，必须拆多次 1-notch）。
    /// </summary>
    public static void SendMouseWheelRepeated(int delta, int x, int y, int count, int intervalMs = 12)
    {
        if (count <= 0) return;
        SetCursorPos(x, y);
        Thread.Sleep(30);
        var input = new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = x,
                    dy = y,
                    mouseData = (uint)delta,
                    dwFlags = 0x0800, // MOUSEEVENTF_WHEEL
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        for (var i = 0; i < count; i++)
        {
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
            if (i < count - 1) Thread.Sleep(intervalMs);
        }
    }

    private static void SendKey(ushort vk, bool up)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KeyeventfKeyup : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MouseInput(uint flags)
    {
        return new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>窗口内相对坐标（比例 0~1）→ 屏幕绝对坐标。</summary>
    public static (int x, int y) ToScreenPoint(IntPtr hwnd, double rx, double ry)
    {
        GetWindowRect(hwnd, out var rect);
        var x = rect.Left + (int)((rect.Right - rect.Left) * rx);
        var y = rect.Top + (int)((rect.Bottom - rect.Top) * ry);
        return (x, y);
    }

    /// <summary>
    /// 窗口内坐标：rx 从左比例，yFromTopPx 距窗口顶边的像素。用于消息区滚动中心等
    /// 「顶部锚定」的坐标（顶边像素不随窗口高度变化，而「从顶部比例」会下移）。
    /// </summary>
    public static (int x, int y) ToScreenPointFromTop(IntPtr hwnd, double rx, int yFromTopPx)
    {
        GetWindowRect(hwnd, out var rect);
        var x = rect.Left + (int)((rect.Right - rect.Left) * rx);
        var y = rect.Top + yFromTopPx;
        return (x, y);
    }

    /// <summary>
    /// 从窗口底部算的屏幕坐标：rx 从左比例，yFromBottomPx 距窗口底边的像素。
    /// 用于输入框/发送按钮等「固定在窗口底部」的控件——其距底部像素不随窗口高度变化，
    /// 而「从顶部比例」会随窗口高度变化而错位（微信窗口高度 809→961 时曾导致点击/截图错位）。
    /// </summary>
    public static (int x, int y) ToScreenPointFromBottom(IntPtr hwnd, double rx, int yFromBottomPx)
    {
        GetWindowRect(hwnd, out var rect);
        var x = rect.Left + (int)((rect.Right - rect.Left) * rx);
        var y = rect.Bottom - yFromBottomPx;
        return (x, y);
    }

    private static void RequireForeground(nint hwnd)
    {
        if (hwnd == 0 || GetForegroundWindow() != hwnd)
            throw new WindowAutomationException("foreground_lost", "WeChat lost foreground. No keyboard or global mouse event was sent.");
    }

    private static void RequireForegroundProcess(nint hwnd)
    {
        var foreground=GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(hwnd,out var expected);
        NativeMethods.GetWindowThreadProcessId(foreground,out var actual);
        // WeChat's search editor is a separate top-level Qt popup. Focusing it necessarily
        // changes the foreground HWND, but not the owning process. Continue only across that
        // same-process transition; another application taking focus still stops all input.
        if(hwnd==0||foreground==0||expected==0||actual!=expected)
            throw new WindowAutomationException("foreground_lost", "WeChat lost foreground. No keyboard or global mouse event was sent.");
    }

    /// <summary>One chord in one SendInput call; never hold modifiers across sleeps or other actions.</summary>
    public static void SendChordGuarded(nint hwnd, params ushort[] keys)
    {
        RequireForeground(hwnd);
        var inputs = keys.Select(key => new INPUT { type=InputKeyboard, U=new InputUnion { ki=new KEYBDINPUT { wVk=key } } })
            .Concat(keys.Reverse().Select(key => new INPUT { type=InputKeyboard, U=new InputUnion { ki=new KEYBDINPUT { wVk=key,dwFlags=KeyeventfKeyup } } })).ToArray();
        RequireForeground(hwnd);
        if (SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new WindowAutomationException("input_failed", "Windows rejected part of the keyboard chord.");
    }

    /// <summary>
    /// Send a real shortcut only while foreground still belongs to the same process. WeChat
    /// changes foreground HWND while constructing its Qt search popup; exact-HWND guarding is
    /// too strict there, while process guarding still prevents delivery to every other app.
    /// </summary>
    public static void SendChordForProcessGuarded(nint hwnd,params ushort[] keys)
    {
        RequireForegroundProcess(hwnd);
        var inputs=keys.Select(key=>new INPUT { type=InputKeyboard,U=new InputUnion { ki=new KEYBDINPUT { wVk=key } } })
            .Concat(keys.Reverse().Select(key=>new INPUT { type=InputKeyboard,U=new InputUnion { ki=new KEYBDINPUT { wVk=key,dwFlags=KeyeventfKeyup } } })).ToArray();
        RequireForegroundProcess(hwnd);
        if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<INPUT>())!=inputs.Length)
            throw new WindowAutomationException("input_failed","Windows rejected part of the keyboard chord.");
    }

    /// <summary>
    /// Type literal UTF-16 text through SendInput while the verified target stays foreground.
    /// This is used by the user-commanded WeChat search box: WM_CHAR posted to the main Qt
    /// window is not routed to the search editor on recent builds, while foreground Unicode
    /// keyboard input follows the editor's real focus without borrowing the user's clipboard.
    /// </summary>
    public static void SendUnicodeTextGuarded(nint hwnd,string text)
    {
        if (text.Any(char.IsControl))
            throw new WindowAutomationException("input_failed", "Control characters are not valid search text.");
        foreach (var value in text)
        {
            RequireForegroundProcess(hwnd);
            var inputs=new[]
            {
                new INPUT { type=InputKeyboard,U=new InputUnion { ki=new KEYBDINPUT { wScan=value,dwFlags=KeyeventfUnicode } } },
                new INPUT { type=InputKeyboard,U=new InputUnion { ki=new KEYBDINPUT { wScan=value,dwFlags=KeyeventfUnicode|KeyeventfKeyup } } }
            };
            if (SendInput(2,inputs,Marshal.SizeOf<INPUT>())!=2)
                throw new WindowAutomationException("input_failed", "Windows rejected part of the Unicode search text.");
        }
        RequireForegroundProcess(hwnd);
    }

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X,Y; }

    private static void RequirePoint(nint hwnd,int x,int y)
    {
        RequireForeground(hwnd);
        if (GetAncestor(WindowFromPoint(new POINT { X=x,Y=y }),2) != hwnd)
            throw new WindowAutomationException("window_occluded", "Another window covers the input point.");
    }

    public static void ClickAtGuarded(nint hwnd,int x,int y)
    {
        using var dpi=new DpiAwarenessScope();
        RequirePoint(hwnd,x,y);
        if (!SetCursorPos(x,y)) throw new WindowAutomationException("input_failed","Cannot position the pointer.");
        RequirePoint(hwnd,x,y);
        var inputs=new[] { MouseInput(0x0002),MouseInput(0x0004) };
        if(SendInput(2,inputs,Marshal.SizeOf<INPUT>())!=2)
            throw new WindowAutomationException("input_failed","Windows rejected the click.");
    }

    public static void SendMouseWheelGuarded(nint hwnd,int delta,int x,int y)
    {
        using var dpi=new DpiAwarenessScope();
        RequirePoint(hwnd,x,y);
        if(!SetCursorPos(x,y)) throw new WindowAutomationException("input_failed","Cannot position the pointer.");
        RequirePoint(hwnd,x,y);
        var input=new INPUT { type=InputMouse,U=new InputUnion { mi=new MOUSEINPUT { mouseData=unchecked((uint)delta),dwFlags=0x0800 } } };
        if(SendInput(1,new[] { input },Marshal.SizeOf<INPUT>())!=1)
            throw new WindowAutomationException("input_failed","Windows rejected the wheel event.");
    }

}
