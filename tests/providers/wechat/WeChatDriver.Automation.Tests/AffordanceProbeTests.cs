using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WeChatDriver.Automation;
using WeChatDriver.Capture;
using WeChatDriver.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WeChatDriver.Automation.Tests;

/// <summary>
/// 群聊「@ 某人」与「引用某条消息」的**可行性探针**（opt-in，默认不跑）。
///
/// 为什么先要这个：这两个功能都要靠界面自动化做，而可行与否取决于**微信 4.x 真实长什么样** ——
/// 右键菜单里到底有没有「引用」、敲 @ 之后成员选择器是什么形态。这些猜不得，先看。
///
/// 分级开关（**默认什么都不做**，全部要显式打开）：
///   WECHAT_AFFORDANCE_PROBE=1              跑这个探针
///   WECHAT_AFFORDANCE_PROBE_TITLE=JARVIS   目标会话标题关键词（默认 JARVIS，容忍 OCR 混淆）
///   WECHAT_AFFORDANCE_PROBE_LIVE=1         允许**真实鼠标/键盘**（右键、点输入框、敲 @）
///
/// 它绝不做的事：**不按回车、不点发送按钮、不发任何消息**。敲进输入框的内容最后清掉。
/// 截图与 report.txt 落盘到 artifacts/affordance-probe/。
///
/// 三个已实测到的事实（别再踩）：
///   1. 微信的浮层（右键菜单、@ 成员选择器）是**同进程的独立顶层窗口，class 含
///      QWindowToolSaveBits**，每次重建、句柄不同 → 每轮都要重新枚举，不能缓存。
///      （WindowDiscovery.FindSearchPopup 的注释里也记着这件事。）
///   2. `InputHelper.ToScreenPoint(hwnd, rx, ry)` 收的是**比例 0~1**，不是像素。
///      把像素当比例传，右键会落到窗口外 —— 菜单不弹，还会被误判成"没有这个功能"。
///   3. 想往输入框敲字，**必须先用真实左键点一下输入框**。背景点击（贴消息）不移动 Qt 的
///      键盘焦点，之后无论是贴 WM_CHAR 还是发真实按键都落不到输入控件上。
/// </summary>
public sealed class AffordanceProbeTests(ITestOutputHelper output)
{
    // ---- 只在这里声明的 P/Invoke：探针不该为实验往生产代码里加原语 ----
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_CHAR = 0x0102;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_2 = 0x32;
    private const ushort VK_A = 0x41;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_ESCAPE = 0x1B;
    private const string PopupClassMarker = "QWindowToolSaveBits";

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private readonly ITestOutputHelper _log = output;
    private readonly List<string> _report = [];

    [Fact]
    public void ProbeGroupMentionAndQuoteAffordances()
    {
        if (Environment.GetEnvironmentVariable("WECHAT_AFFORDANCE_PROBE") != "1")
        {
            _log.WriteLine("跳过：设 WECHAT_AFFORDANCE_PROBE=1 才跑。");
            return;
        }

        var live = Environment.GetEnvironmentVariable("WECHAT_AFFORDANCE_PROBE_LIVE") == "1";
        var keyword = Environment.GetEnvironmentVariable("WECHAT_AFFORDANCE_PROBE_TITLE") ?? "JARVIS";
        using var dpi = new DpiAwarenessScope();
        _log.WriteLine($"模式：{(live ? "LIVE（会动真实鼠标/键盘）" : "只读（不动物理输入）")}  目标关键词：{keyword}");

        var hwnd = DiscoverMainWindow();
        if (hwnd == nint.Zero) { Say(false, "没找到可用的微信主窗口"); Flush(); return; }

        var pid = WindowDiscovery.GetWindowProcessId(hwnd);
        var windowDpi = WindowDiscovery.GetDpi(hwnd);
        var bounds = WindowDiscovery.GetWindowBounds(hwnd);
        var ocr = new WindowsOcr();
        Say(true, $"主窗口 0x{hwnd:X} pid={pid} {bounds.Width}×{bounds.Height} @({bounds.X},{bounds.Y}) dpi={windowDpi}");
        Say(ocr.IsAvailable, $"OCR={ocr.IsAvailable}（{ocr.Language}）");

        using var frame = WindowCapture.CaptureWindow(hwnd);
        Save(frame, "00-window");
        var layout = WeChatLayout.Detect(frame, windowDpi);
        var rects = ocr.RecognizePngWithRects(WindowCapture.ToPng(frame));
        Say(layout.HasOpenConversation, $"布局：页头={layout.Header} 消息区={layout.Messages} 输入框={layout.Composer}");

        var headerText = string.Join(" ", rects.Where(r => InRegion(r, layout.Header)).Select(r => r.Text));
        if (!LooseContains(headerText, keyword))
        {
            Say(false, $"页头是 \"{headerText.Trim()}\"，不含 \"{keyword}\" → 先把微信切到目标群再跑。");
            Flush();
            return;
        }
        Say(true, $"页头 \"{headerText.Trim()}\" ✓");

        var messageLines = rects.Where(r => InRegion(r, layout.Messages))
                                .Where(r => r.Text.Trim().Length >= 2).OrderBy(r => r.Y).ToList();
        for (var i = 0; i < Math.Min(messageLines.Count, 8); i++)
            _log.WriteLine($"      [{i}] ({messageLines[i].CenterX},{messageLines[i].CenterY}) {Trim(messageLines[i].Text)}");

        // 目标要是**正文气泡**：昵称标签（"柳伟杰@深圳集思科技"）和时间戳都短，正文长 ——
        // 按长度挑最像正文的一行（第一版按"倒数第二行"取到了昵称，右键未必有引用）。
        var target = messageLines
            .Where(r => !r.Text.Contains("http", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Text.Trim().Length).FirstOrDefault();
        if (target is null) { Say(false, "消息区没有可用目标"); Flush(); return; }
        Say(true, $"右键目标：\"{Trim(target.Text)}\"（{target.Text.Trim().Length} 字）窗口内({target.CenterX},{target.CenterY})");

        // ================= 阶段 A：右键 → 菜单里有没有「引用」 =================
        var (mx, my) = InputHelper.ToScreenPointFromTop(hwnd, (double)target.CenterX / layout.Width, target.CenterY);
        Say(true, $"屏幕坐标 ({mx},{my})");
        if (live)
        {
            if (!InputHelper.IsForeground(hwnd) && !InputHelper.BringToForeground(hwnd))
                Say(false, "⚠️ 抢不到前台，右键大概率无效");
            Thread.Sleep(150);
            SetCursorPos(mx, my);
            Thread.Sleep(150);
            RightClick();
        }
        else
        {
            PostMessage(hwnd, WM_CONTEXTMENU, hwnd, (nint)((my << 16) | (mx & 0xFFFF)));
        }
        Thread.Sleep(1000);
        using (var afterMenu = WindowCapture.CaptureWindow(hwnd)) Save(afterMenu, "01-after-rightclick");
        var menuTexts = ReportPopups(ocr, pid, "右键菜单");
        var quoteHits = menuTexts.Where(t => t.Contains('引') || t.Contains("回复") || t.Contains("转发")
                                          || t.Contains("复制") || t.Contains("收藏") || t.Contains("撤回")).ToList();
        Say(quoteHits.Count > 0, quoteHits.Count > 0
            ? $"菜单动作：{string.Join(" | ", quoteHits.Select(Trim))}"
            : "没读到菜单项 —— 右键可能没生效");

        PostKey(hwnd, VK_ESCAPE);
        Thread.Sleep(400);

        // ================= 阶段 B：敲 @ → 成员选择器 =================
        if (!live)
        {
            Say(false, "跳过敲 @：需要 WECHAT_AFFORDANCE_PROBE_LIVE=1（会往输入框真敲字符）。");
            Flush();
            return;
        }

        // 关键：**必须照抄生产代码的写入准备序列**。
        // OcrInputBackend 的前台写入路径是：
        //     Input.ActivateForWriting();            ← 前台/遮挡校验，漏了就敲不进字
        //     Input.Click(cx, cy, background: false);← 真实点击
        //     SetClipboardText + Chord(Ctrl,V)       ← 文字走剪贴板
        // 第一版只做了 `SetCursorPos + mouse_event`，看着等价，实际缺了 ActivateForWriting，
        // 结果输入框始终拿不到键盘焦点、怎么敲都是空的。
        var composerCenterX = layout.Composer.Left + layout.Composer.Width / 2;
        var composerCenterY = layout.Composer.Top + layout.Composer.Height / 2;
        var composerX = composerCenterX;
        var composerY = composerCenterY;
        var (cx, cy) = InputHelper.ToScreenPointFromBottom(
            hwnd, (double)composerCenterX / layout.Width, layout.Height - composerCenterY);
        {
            var input = new TargetWindowInput(hwnd, pid);
            var state = input.Validate();
            input.ExpectGeometry(state.Width, state.Height, state.Dpi);

            // 安全闸门：输入框里已经有草稿就**绝不往下走**（别覆盖用户正在打的字）
            using (var existing = WindowCapture.CaptureRegionFromBottom(hwnd, 0.88, 0.11, 10, 90))
            {
                var ink = existing.GetPixel(existing.Width / 2, existing.Height / 2);
                _log.WriteLine($"      发送键区域取样像素 RGB({ink.R},{ink.G},{ink.B})（绿=有草稿）");
            }

            input.ActivateForWriting();
            input.Click(composerX, composerY, background: false);
            Thread.Sleep(400);
            Say(true, $"已按生产序列激活并点击输入框 (窗口内 {composerX},{composerY} / 屏幕 {cx},{cy})，" +
                      $"当前内容：\"{ComposerText(ocr, hwnd, layout).Trim()}\"");
        }
        var cursor = InputHelper.CursorPosition();
        _log.WriteLine($"      光标现在在 ({cursor.X},{cursor.Y})，目标点 ({cx},{cy})");

        // ================= 阶段 B0：差分实验 =================
        // 已知事实：**合成键盘事件到不了输入框，但鼠标事件能**（右键菜单稳定弹出）。
        // 最可能的解释是**中文输入法**把合成的按键吃进了自己的候选缓冲 —— 这也正好解释了
        // 生产代码为什么用「剪贴板 + Ctrl+V」写文字（那是唯一绕开输入法的路）。
        //
        // 所以真正要问的问题变成：**粘贴一个 @ 能不能唤起成员选择器？**
        // 能的话，实现就完全不用碰键盘输入，和现有输入路径一致。
        //   A) 粘贴 "a"     —— 对照组：验证"粘贴进输入框"这条路本身是通的
        //   B) 粘贴 "@"     —— 关键：唤不唤起成员选择器
        //   C) 真实 Shift+2 —— 预期失败（被输入法吃掉），作为反证
        var textBefore = ComposerText(ocr, hwnd, layout);
        Say(true, $"敲字前输入框：\"{textBefore.Trim()}\"");

        void TryPaste(string label, string payload, string screenshot)
        {
            // 组合：真实鼠标点击（右键能弹出菜单，说明这条路是通的）+ 生产代码自己的 SendCtrlV
            SetCursorPos(cx, cy);
            Thread.Sleep(200);
            LeftClick();
            Thread.Sleep(300);
            InputHelper.SetClipboardText(payload);
            Thread.Sleep(150);
            try { InputHelper.SendCtrlV(); }
            catch (Exception error) { Say(false, $"{label} Ctrl+V 失败：{error.Message}"); return; }
            Thread.Sleep(1100);
            using var after = WindowCapture.CaptureWindow(hwnd);
            Save(after, screenshot);
            var text = ComposerText(ocr, hwnd, layout);
            Say(text.Contains(payload) || text.Length > textBefore.Trim().Length,
                $"{label} → 输入框：\"{text.Trim()}\"");
            ReportPopups(ocr, pid, $"{label}的浮层");
        }

        TryPaste("A 粘贴 \"a\"（对照）", "a", "02a-paste-a");
        TryPaste("B 粘贴 \"@\"（关键）", "@", "02b-paste-at");

        try
        {
            var input = new TargetWindowInput(hwnd, pid);
            input.Chord(VK_SHIFT, VK_2);
            Thread.Sleep(900);
            using var after = WindowCapture.CaptureWindow(hwnd);
            Save(after, "03-real-shift2");
            Say(true, $"C 真实 Shift+2 → 输入框：\"{ComposerText(ocr, hwnd, layout).Trim()}\"");
            ReportPopups(ocr, pid, "C 的浮层");
        }
        catch (Exception error) { Say(false, $"C 真实 Shift+2 发不出去：{error.Message}"); }

        // ================= 阶段 C：清场（**绝不回车**） =================
        PostKey(hwnd, VK_ESCAPE);
        Thread.Sleep(250);
        PostMessage(hwnd, 0x0100, VK_CONTROL, 0x00010001);
        PostKey(hwnd, VK_A);
        PostMessage(hwnd, 0x0101, VK_CONTROL, unchecked((nint)0xC0010001UL));
        Thread.Sleep(200);
        PostKey(hwnd, VK_DELETE);
        Thread.Sleep(400);
        using (var cleaned = WindowCapture.CaptureWindow(hwnd)) Save(cleaned, "04-after-cleanup");
        Say(true, $"已清空输入框：现在 \"{ComposerText(ocr, hwnd, layout).Trim()}\"");
        Say(true, "全程没有按过回车、没有点发送按钮。");

        Flush();
    }

    // ------------------------------------------------------------------
    // 浮层（右键菜单 / @ 成员选择器）
    // ------------------------------------------------------------------

    /// <summary>枚举同进程的浮层窗口。它们每次重建、句柄不同，所以**每轮都要重新枚举**。</summary>
    private static List<nint> PopupHandles(int pid)
    {
        var found = new List<nint>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out var owner);
            if ((int)owner != pid) return true;
            var name = new StringBuilder(256);
            if (GetClassName(hWnd, name, name.Capacity) <= 0) return true;
            if (name.ToString().Contains(PopupClassMarker, StringComparison.Ordinal)) found.Add(hWnd);
            return true;
        }, nint.Zero);
        return found;
    }

    private List<string> ReportPopups(IOcrEngine ocr, int pid, string label)
    {
        var texts = new List<string>();
        foreach (var handle in PopupHandles(pid))
        {
            GetWindowRect(handle, out var rect);
            _log.WriteLine($"      浮层 0x{handle:X} {rect.Right - rect.Left}×{rect.Bottom - rect.Top} " +
                           $"@({rect.Left},{rect.Top}) [{label}]");
            try
            {
                using var bitmap = WindowCapture.CaptureWindow(handle);
                Save(bitmap, $"popup-{label}-0x{handle:X}");
                foreach (var text in ocr.RecognizePngWithRects(WindowCapture.ToPng(bitmap)).Select(r => r.Text))
                {
                    texts.Add(text);
                    _log.WriteLine($"        · {Trim(text)}");
                }
            }
            catch (Exception error) { _log.WriteLine($"        （截不到：{error.Message}）"); }
        }
        if (texts.Count == 0) _log.WriteLine($"      （{label}：没有浮层）");
        return texts;
    }

    // ------------------------------------------------------------------
    // 帮手
    // ------------------------------------------------------------------

    private nint DiscoverMainWindow()
    {
        var session = WindowDiscovery.Discover();
        if (session.Succeeded) return WindowDiscovery.FindMainWindowLoose(session.Value!.ProcessId);
        // 窗口可能被收进托盘（实测踩到：上一轮跑完它自己藏了）。生产代码本来就有恢复这一步。
        foreach (var pid in WindowDiscovery.FindWeChatProcessIds())
        {
            var hwnd = WindowDiscovery.FindMainWindowLoose(pid);
            if (hwnd == nint.Zero) continue;
            _log.WriteLine($"      主窗口不可用，尝试恢复 0x{hwnd:X}…");
            WindowDiscovery.TryRestoreWindow(hwnd);
            Thread.Sleep(700);
            session = WindowDiscovery.Discover();
            if (session.Succeeded) return WindowDiscovery.FindMainWindowLoose(session.Value!.ProcessId);
        }
        return nint.Zero;
    }

    private string ComposerText(IOcrEngine ocr, nint hwnd, WeChatLayout layout)
    {
        try
        {
            using var region = WindowCapture.CaptureRegion(hwnd, layout.Composer.Left, layout.Composer.Top,
                                                           layout.Composer.Width, layout.Composer.Height);
            return string.Join(" ", ocr.RecognizePngWithRects(WindowCapture.ToPng(region)).Select(r => r.Text));
        }
        catch (Exception error) { return $"<读不到:{error.Message}>"; }
    }

    private static void RightClick()
    {
        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
    }

    private static void LeftClick()
    {
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    private static bool InRegion(OcrTextRect r, Rectangle region) =>
        r.CenterX >= region.Left && r.CenterX <= region.Right
        && r.CenterY >= region.Top && r.CenterY <= region.Bottom;

    /// <summary>容忍 OCR 混淆的包含判断。

    /// 实测：`jev-chat-JARVIS 15 群` 被读成 `Jev-chat-JARVlS 15 群`（大写 I → 小写 l）。
    /// 项目里也有同类记录（Wencharm 被读成 Wencham）。所以只留字母数字、统一大写，
    /// 再把 l/1 归一成 I、0 归一成 O。
    /// </summary>
    private static bool LooseContains(string haystack, string needle)
    {
        static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var ch in text.ToUpperInvariant())
            {
                if (!char.IsLetterOrDigit(ch)) continue;
                builder.Append(ch switch { 'L' or '1' => 'I', '0' => 'O', _ => ch });
            }
            return builder.ToString();
        }
        return Normalize(haystack).Contains(Normalize(needle), StringComparison.Ordinal);
    }

    private void PostKey(nint hwnd, ushort vk)
    {
        PostMessage(hwnd, 0x0100, vk, 0x00010001);
        PostMessage(hwnd, 0x0101, vk, unchecked((nint)0xC0010001UL));
        Thread.Sleep(60);
    }

    private static string Trim(string text) =>
        text.Length <= 44 ? text.Replace('\n', ' ').Trim() : text[..44].Replace('\n', ' ').Trim() + "…";

    private static string OutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MessagingRuntime.sln"))) dir = dir.Parent;
        var target = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "artifacts", "affordance-probe");
        Directory.CreateDirectory(target);
        return target;
    }

    private void Save(Bitmap bitmap, string name)
    {
        try
        {
            var path = Path.Combine(OutputDirectory(), name + ".png");
            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception error) { _log.WriteLine($"      （截图失败 {name}：{error.Message}）"); }
    }

    private void Say(bool ok, string text)
    {
        var line = $"{(ok ? "  ✓" : "  ✗")} {text}";
        _log.WriteLine(line);
        _report.Add(line);
    }

    private void Flush()
    {
        try { File.WriteAllLines(Path.Combine(OutputDirectory(), "report.txt"), _report, Encoding.UTF8); }
        catch { /* 报告写不出去不影响结论 */ }
    }
}
