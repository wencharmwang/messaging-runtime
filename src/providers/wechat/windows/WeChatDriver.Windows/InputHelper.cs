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
    public static bool BringToForeground(IntPtr hwnd) => SetForegroundWindow(hwnd);

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
