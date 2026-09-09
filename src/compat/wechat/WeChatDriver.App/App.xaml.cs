using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using WeChatDriver.Automation;
using WeChatDriver.Bench;
using WeChatDriver.Capture;
using WeChatDriver.Core;
using WeChatDriver.Mcp;
using WeChatDriver.Windows;

namespace WeChatDriver.App;

public partial class App : System.Windows.Application
{
    private DriverHost? _host;
    private NotifyIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatDriver");

        // 探测专用模式：导出 UIA 探测报告后退出。
        if (args.Contains("--probe-only"))
        {
            RunProbeOnly(baseDir, args);
            Shutdown();
            return;
        }

        // 捕获/OCR 诊断模式：截图并输出 OCR 识别结果。
        if (args.Contains("--capture"))
        {
            RunCaptureOnly(args);
            Shutdown();
            return;
        }

        // 草稿写入验证：剪贴板 + Ctrl+V 真实写入微信输入框。
        if (args.Contains("--write-test"))
        {
            RunWriteTest(args);
            Shutdown();
            return;
        }

        // 真实基准：写→读→清空循环 N 次。
        if (args.Contains("--bench"))
        {
            RunBench(args);
            Shutdown();
            return;
        }

        // P0-B 实际发送：写草稿 → 探测发送按钮 → 点击发送 → 验证清空。
        if (args.Contains("--send-test"))
        {
            RunSendTest(args);
            Shutdown();
            return;
        }

        // 读取聊天记录：滚动 + OCR，输出到文件。
        if (args.Contains("--read-history"))
        {
            RunReadHistory(args);
            Shutdown();
            return;
        }

        // 从文件读草稿并发送（供 Agent 把总结发回）。
        if (args.Contains("--send-file"))
        {
            RunSendFile(args);
            Shutdown();
            return;
        }

        // MCP stdio：无头模式，由外部 Agent（WorkBuddy/Codex）作为子进程拉起，走 stdin/stdout。
        if (args.Contains("--mcp-stdio"))
        {
            RunMcpStdio();
            Shutdown();
            return;
        }

        // MCP HTTP：无头模式，监听 127.0.0.1，供 Agent 通过 URL 接入。
        if (args.Contains("--mcp-http"))
        {
            RunMcpHttp(args);
            return; // 不 Shutdown：HTTP 服务常驻，直到进程被终止。
        }

        _host = new DriverHost(baseDir);
        var init = _host.Initialize();
        if (init.Succeeded)
        {
            _host.StartPolling(TimeSpan.FromMilliseconds(1500));
        }

        _window = new MainWindow(_host!);
        _window.Show();

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "微信 Driver",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.ContextMenuStrip = BuildTrayMenu();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示面板", null, (_, _) => ShowWindow());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _host?.Dispose();
            _tray?.Dispose();
            Shutdown();
        });
        return menu;
    }

    private void ShowWindow()
    {
        _window?.Show();
        _window?.Activate();
    }

    private static IOcrEngine TryCreateOcr(out bool owns)
    {
        var script = PaddleOcrLocator.Find();
        if (script is not null)
        {
            var paddle = new PaddleOcr(script);
            if (paddle.IsAvailable)
            {
                owns = true;
                return paddle;
            }
            paddle.Dispose();
        }
        owns = false;
        return new WindowsOcr();
    }

    private void RunCaptureOnly(string[] args)
    {
        var idx = Array.IndexOf(args, "--capture");
        var outPath = "artifacts/wechat/capture.png";
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
            outPath = args[idx + 1];

        var sessionResult = WindowDiscovery.Discover();
        if (!sessionResult.Succeeded || sessionResult.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {sessionResult.Failure?.Code} {sessionResult.Failure?.Message}");
            return;
        }

        var session = sessionResult.Value;
        using var bmp = WindowCapture.CaptureWindow(session.WindowHandle);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"captured: {outPath} {bmp.Width}x{bmp.Height}");

        IOcrEngine ocr = TryCreateOcr(out var owns);
        try
        {
            Console.WriteLine($"OCR available: {ocr.IsAvailable} · lang: {ocr.Language}");
            if (!ocr.IsAvailable)
            {
                Console.Error.WriteLine("WARN: OCR 不可用");
                return;
            }

            var png = WindowCapture.ToPng(bmp);
            foreach (var line in ocr.RecognizePng(png))
                Console.WriteLine($"  [{line.Confidence:F2}] {line.Text}");
        }
        finally
        {
            if (owns && ocr is IDisposable d) d.Dispose();
        }
    }

    private void RunWriteTest(string[] args)
    {
        var draft = "【驱动测试】草稿写入验证 123";
        var idx = Array.IndexOf(args, "--text");
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
            draft = args[idx + 1];

        var backend = new OcrInputBackend();
        Console.WriteLine($"OCR: {backend.OcrAvailable} · {backend.OcrLanguage}");

        var session = backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {session.Failure?.Code} {session.Failure?.Message}");
            return;
        }
        Console.WriteLine($"会话: {session.Value.ClientVersion} · DPI 由窗口决定");

        Console.WriteLine($"写入草稿: {draft}");
        var write = backend.WriteComposer(new ConversationKey("", "", null), draft);
        Console.WriteLine($"写入结果: Succeeded={write.Succeeded} · Confidence={write.Confidence}");
        if (!write.Succeeded)
        {
            Console.Error.WriteLine($"  失败: {write.Failure?.Code} {write.Failure?.Message}");
            return;
        }

        Thread.Sleep(400); // 等待渲染完成。
        try
        {
            var read = backend.ReadComposerText();
            Console.WriteLine($"读回结果: Succeeded={read.Succeeded} · Value={read.Value}");
            Console.WriteLine($"匹配: {read.Succeeded && read.Value?.Contains(draft) == true}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读回异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 清空输入框，避免残留测试文本。
        var clear = backend.ClearComposer();
        Console.WriteLine($"清空结果: Succeeded={clear.Succeeded}");
        backend.DisposeOcr();
    }

    private void RunBench(string[] args)
    {
        var n = 10;
        var idx = Array.IndexOf(args, "--n");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
            n = Math.Clamp(parsed, 1, 100);

        try
        {
            RunBenchCore(n);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"基准异常: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
        }
    }

    private void RunBenchCore(int n)
    {
        var backend = new OcrInputBackend();
        Console.WriteLine($"OCR: {backend.OcrAvailable} · {backend.OcrLanguage}");

        var session = backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {session.Failure?.Code} {session.Failure?.Message}");
            return;
        }

        var caps = backend.ProbeCapabilities();
        var bounds = WindowDiscovery.GetWindowBounds(session.Value.WindowHandle);
        var dpi = WindowDiscovery.GetDpi(session.Value.WindowHandle);

        // 清掉残留草稿，确保干净起点。
        backend.ClearComposer();
        Thread.Sleep(200);

        var writeMs = new List<double>();
        var readMs = new List<double>();
        var clearMs = new List<double>();
        var failureCodes = new List<FailureCode>();

        for (var i = 0; i < n; i++)
        {
            var text = $"基准草稿 #{i + 1}";
            Console.WriteLine($"iter {i + 1}: write...");

            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var w = backend.WriteComposer(new ConversationKey("", "", null), text);
            var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            writeMs.Add((t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (!w.Succeeded) failureCodes.Add(w.Failure?.Code ?? FailureCode.WriteFailed);
            Console.WriteLine($"iter {i + 1}: write done, read...");

            Thread.Sleep(250);

            var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            var r = backend.ReadComposerText();
            var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            readMs.Add((t3 - t2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (!r.Succeeded) failureCodes.Add(r.Failure?.Code ?? FailureCode.MessageTextUnavailable);
            Console.WriteLine($"iter {i + 1}: read done, clear...");

            var c = backend.ClearComposer();
            var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            clearMs.Add((t4 - t3) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (!c.Succeeded) failureCodes.Add(c.Failure?.Code ?? FailureCode.WriteFailed);
            Console.WriteLine($"iter {i + 1}: clear done");
        }

        backend.DisposeOcr();

        // 生成结构化基准报告（JSON 原始数据 + Markdown 汇总），落盘 artifacts/wechat/benchmarks/。
        var report = new BenchReport(
            DateTimeOffset.Now,
            $"Windows {Environment.OSVersion.Version}",
            "OcrInputBackend（剪贴板+Ctrl+V 写入 / OCR 读回 / Ctrl+A+Delete 清空）",
            session.Value.ClientVersion,
            session.Value.LocatorProfileId,
            dpi,
            $"{bounds.Width}x{bounds.Height}",
            n,
            new[]
            {
                BenchReport.Summary("写入", writeMs),
                BenchReport.Summary("读回", readMs),
                BenchReport.Summary("清空", clearMs)
            },
            failureCodes.GroupBy(f => f).Select(g => new FailureCount(g.Key.ToString(), g.Count())).ToList(),
            1.0, // 微信 4.x UIA 不可用，OCR 是主通道 → 触发率 100%
            caps.Value?.Entries ?? Array.Empty<CapabilityEntry>(),
            Array.Empty<string>());

        var (jsonPath, mdPath) = BenchReport.WriteToDisk(Path.Combine("artifacts", "wechat", "benchmarks"), "bench", report);

        Console.WriteLine($"\n=== 基准结果（N={n}，失败 {failureCodes.Count}）===");
        Print("写入(ms)", writeMs);
        Print("读回(ms)", readMs);
        Print("清空(ms)", clearMs);
        Console.WriteLine($"已写报告: {mdPath} / {jsonPath}");
    }

    private static void Print(string name, List<double> samples)
    {
        samples.Sort();
        var p50 = samples.Count == 0 ? 0 : samples[(int)(samples.Count * 0.5)];
        var p95 = samples.Count == 0 ? 0 : samples[(int)(samples.Count * 0.95)];
        Console.WriteLine($"  {name}  P50={p50:F0}  P95={p95:F0}  Max={samples.LastOrDefault():F0}");
    }

    /// <summary>若指定 --conversation 则先切换到该会话再发送；否则提示发送到当前会话的风险。返回是否可继续。</summary>
    private static bool TrySwitchToConversation(OcrInputBackend backend, string[] args)
    {
        var idx = Array.IndexOf(args, "--conversation");
        if (idx < 0 || idx + 1 >= args.Length || args[idx + 1].StartsWith("--"))
        {
            Console.WriteLine("WARN: 未指定 --conversation，将发送到「当前打开的会话」，请确认是测试通道。");
            return true; // 保持旧行为，但显式提示风险。
        }

        var target = args[idx + 1];
        var ok = backend.SwitchConversation(target);
        if (ok) Console.WriteLine($"已切换到会话: {target}");
        else Console.Error.WriteLine($"目标会话未找到，中止发送: {target}");
        return ok;
    }

    private void RunSendTest(string[] args)
    {
        var n = 3;
        var idx = Array.IndexOf(args, "--n");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
            n = Math.Clamp(parsed, 1, 20);

        var backend = new OcrInputBackend();
        Console.WriteLine($"OCR: {backend.OcrAvailable} · {backend.OcrLanguage}");

        var session = backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {session.Failure?.Code} {session.Failure?.Message}");
            return;
        }
        Console.WriteLine($"会话: {session.Value.ClientVersion}");

        var caps = backend.ProbeCapabilities();
        var bounds = WindowDiscovery.GetWindowBounds(session.Value.WindowHandle);
        var dpi = WindowDiscovery.GetDpi(session.Value.WindowHandle);

        if (!TrySwitchToConversation(backend, args))
        {
            backend.DisposeOcr();
            return;
        }
        Thread.Sleep(300);

        // 清掉残留草稿，确保第一条发送内容干净。
        backend.ClearComposer();
        Thread.Sleep(300);

        var writeMs = new List<double>();
        var sendMs = new List<double>();
        var verifyMs = new List<double>();
        var totalMs = new List<double>();
        var failureCodes = new List<FailureCode>();

        for (var i = 0; i < n; i++)
        {
            var text = $"【驱动测试】P0-B 发送验证 #{i + 1} {DateTime.Now:HH:mm:ss}";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var t0 = sw.ElapsedMilliseconds;
            var w = backend.WriteComposer(new ConversationKey("", "", null), text);
            var t1 = sw.ElapsedMilliseconds;

            if (!w.Succeeded)
            {
                failureCodes.Add(w.Failure?.Code ?? FailureCode.WriteFailed);
                Console.WriteLine($"[{i + 1}/{n}] 写入失败: {w.Failure?.Code}");
                continue;
            }

            Thread.Sleep(250); // 等待渲染 + 发送按钮亮起。

            var s = backend.SendMessage(new ConversationKey("", "", null));
            var t2 = sw.ElapsedMilliseconds;

            if (!s.Succeeded)
            {
                failureCodes.Add(s.Failure?.Code ?? FailureCode.SendFailed);
                Console.WriteLine($"[{i + 1}/{n}] 发送失败: {s.Failure?.Code}");
                continue;
            }

            Thread.Sleep(350); // 等待输入框清空 + 按钮变灰。

            var v = backend.VerifySent(new ConversationKey("", "", null), "");
            var t3 = sw.ElapsedMilliseconds;
            sw.Stop();
            if (!v.Succeeded) failureCodes.Add(v.Failure?.Code ?? FailureCode.SendUnverified);

            var write = t1 - t0;
            var send = t2 - t1 - 250;
            var verify = t3 - t2 - 350;
            var total = t3 - t0;
            writeMs.Add(write);
            sendMs.Add(send);
            verifyMs.Add(verify);
            totalMs.Add(total);

            Console.WriteLine($"[{i + 1}/{n}] 写{write}ms 发{send}ms 验{verify}ms 端到端{total}ms | 发送={(v.Succeeded ? "✅" : "❌")}");
        }

        backend.DisposeOcr();

        var report = new BenchReport(
            DateTimeOffset.Now,
            $"Windows {Environment.OSVersion.Version}",
            "OcrInputBackend（剪贴板+Ctrl+V 写入 / Enter 发送 / 发送按钮变灰验证）",
            session.Value.ClientVersion,
            session.Value.LocatorProfileId,
            dpi,
            $"{bounds.Width}x{bounds.Height}",
            writeMs.Count,
            new[]
            {
                BenchReport.Summary("写入", writeMs),
                BenchReport.Summary("发送", sendMs),
                BenchReport.Summary("验证", verifyMs),
                BenchReport.Summary("端到端", totalMs)
            },
            failureCodes.GroupBy(f => f).Select(g => new FailureCount(g.Key.ToString(), g.Count())).ToList(),
            1.0,
            caps.Value?.Entries ?? Array.Empty<CapabilityEntry>(),
            new[] { $"目标发送 {n} 条，实际完成 {writeMs.Count} 条，失败 {failureCodes.Count} 次" });

        var (jsonPath, mdPath) = BenchReport.WriteToDisk(Path.Combine("artifacts", "wechat", "benchmarks"), "send", report);

        Console.WriteLine($"\n=== 汇总（N={writeMs.Count}）===");
        Print("写入(ms)", writeMs);
        Print("发送(ms)", sendMs);
        Print("验证(ms)", verifyMs);
        Print("端到端(ms)", totalMs);
        Console.WriteLine($"已写报告: {mdPath} / {jsonPath}");
    }

    private void RunReadHistory(string[] args)
    {
        var n = 100;
        var idx = Array.IndexOf(args, "--n");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
            n = Math.Clamp(parsed, 1, 500);

        var outPath = "artifacts/wechat/history.txt";
        idx = Array.IndexOf(args, "--out");
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
            outPath = args[idx + 1];

        var backend = new OcrInputBackend();
        Console.WriteLine($"OCR: {backend.OcrAvailable} · {backend.OcrLanguage}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var session = backend.EstablishSession();
        sw.Stop();
        Console.WriteLine($"会话建立: {sw.ElapsedMilliseconds}ms");
        if (!session.Succeeded || session.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {session.Failure?.Code} {session.Failure?.Message}");
            return;
        }

        var caps = backend.ProbeCapabilities();
        var bounds = WindowDiscovery.GetWindowBounds(session.Value.WindowHandle);
        var dpi = WindowDiscovery.GetDpi(session.Value.WindowHandle);

        // 逐屏截图保存目录（默认自动生成，可用 --screenshots 覆盖）。
        var shotsDir = Path.Combine("artifacts", "wechat", "screens", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var si = Array.IndexOf(args, "--screenshots");
        if (si >= 0 && si + 1 < args.Length && !args[si + 1].StartsWith("--"))
            shotsDir = args[si + 1];
        backend.EnableScreenshotCapture(shotsDir);
        Console.WriteLine($"逐屏截图目录: {shotsDir}");

        Console.WriteLine($"开始滚动读取（上限 {n} 条）...");
        // 先切到指定会话（默认 Wencharm）。
        var idx2 = Array.IndexOf(args, "--conversation");
        var conversation = idx2 >= 0 && idx2 + 1 < args.Length && !args[idx2 + 1].StartsWith("--")
            ? args[idx2 + 1] : "Wencharm";

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var switched = backend.SwitchConversation(conversation);
        sw2.Stop();
        Console.WriteLine($"切换会话 {conversation}: {(switched ? "ok" : "未找到")} · {sw2.ElapsedMilliseconds}ms");
        Thread.Sleep(500);

        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var lines = backend.ReadChatHistory(out var readStats, n);
        sw3.Stop();
        Console.WriteLine($"滚动读取: {lines.Count} 行 · 滚动 {readStats.Scrolls} 屏 · OCR {readStats.OcrCalls} 次 · {sw3.ElapsedMilliseconds}ms（平均 {(readStats.OcrCalls > 0 ? sw3.ElapsedMilliseconds / (double)readStats.OcrCalls : 0):F0}ms/OCR）");

        // 消息之间空行分隔，避免长消息换行后与下一条粘在一起。
        var text = string.Join("\n\n", lines);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, text);
        Console.WriteLine($"已写入 {outPath}");

        var report = new BenchReport(
            DateTimeOffset.Now,
            $"Windows {Environment.OSVersion.Version}",
            "OcrInputBackend（滚动 + OCR 读取聊天历史）",
            session.Value.ClientVersion,
            session.Value.LocatorProfileId,
            dpi,
            $"{bounds.Width}x{bounds.Height}",
            1,
            new[]
            {
                BenchReport.Summary("会话建立", new[] { (double)sw.ElapsedMilliseconds }),
                BenchReport.Summary("切换会话", new[] { (double)sw2.ElapsedMilliseconds }),
                BenchReport.Summary("滚动读取", new[] { (double)sw3.ElapsedMilliseconds })
            },
            Array.Empty<FailureCount>(),
            1.0,
            caps.Value?.Entries ?? Array.Empty<CapabilityEntry>(),
            new[]
            {
                $"目标会话: {conversation}（切换 {(switched ? "成功" : "未找到")}）",
                $"读取 {lines.Count} 行 · 滚动 {readStats.Scrolls} 屏 · OCR {readStats.OcrCalls} 次",
                $"平均 {(readStats.OcrCalls > 0 ? sw3.ElapsedMilliseconds / (double)readStats.OcrCalls : 0):F0} ms/OCR",
                $"逐屏截图: {shotsDir}"
            });

        var (jsonPath, mdPath) = BenchReport.WriteToDisk(Path.Combine("artifacts", "wechat", "benchmarks"), "read", report);
        Console.WriteLine($"已写报告: {mdPath} / {jsonPath}");
        Console.WriteLine($"逐屏截图: {shotsDir}（每屏 screen_*.png + screens.txt 重叠日志）");
        Console.WriteLine("---- 内容预览 ----");
        foreach (var line in lines.TakeLast(40))
            Console.WriteLine(line);
        backend.DisposeOcr();
    }

    private void RunSendFile(string[] args)
    {
        var idx = Array.IndexOf(args, "--send-file");
        var path = "artifacts/wechat/summary.txt";
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
            path = args[idx + 1];

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在: {path}");
            return;
        }
        var text = File.ReadAllText(path).Trim();
        if (string.IsNullOrEmpty(text))
        {
            Console.Error.WriteLine("文件为空");
            return;
        }

        var backend = new OcrInputBackend();
        var session = backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
        {
            Console.Error.WriteLine($"FAILED: {session.Failure?.Code} {session.Failure?.Message}");
            return;
        }

        if (!TrySwitchToConversation(backend, args))
        {
            backend.DisposeOcr();
            return;
        }
        Thread.Sleep(300);

        backend.ClearComposer();
        Thread.Sleep(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var w = backend.WriteComposer(new ConversationKey("", "", null), text);
        sw.Stop();
        Console.WriteLine($"写入: {w.Succeeded} · {sw.ElapsedMilliseconds}ms");
        if (!w.Succeeded) return;

        Thread.Sleep(250);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var s = backend.SendMessage(new ConversationKey("", "", null));
        sw2.Stop();
        Console.WriteLine($"发送: {s.Succeeded} · {sw2.ElapsedMilliseconds}ms");
        if (!s.Succeeded) return;

        Thread.Sleep(350);
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var v = backend.VerifySent(new ConversationKey("", "", null), "");
        sw3.Stop();
        Console.WriteLine($"验证: {v.Succeeded} · {sw3.ElapsedMilliseconds}ms");
        backend.DisposeOcr();
    }

    private void RunMcpStdio()
    {
        // 后端惰性创建：保证 MCP 握手即时响应，首次工具调用才加载 OCR 模型。
        var api = new WeChatToolsAdapter(() => new OcrInputBackend());
        var server = new McpServer(api, "wechat-driver", "1.0.0");
        var transport = new McpStdioTransport(server);
        var exit = transport.Run();
        api.Dispose();
        Environment.ExitCode = exit;
    }

    private void RunMcpHttp(string[] args)
    {
        var port = 0;
        var idx = Array.IndexOf(args, "--mcp-port");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
            port = Math.Clamp(parsed, 1, 65535);

        string? token = null;
        idx = Array.IndexOf(args, "--mcp-token");
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
            token = args[idx + 1];

        var api = new WeChatToolsAdapter(() => new OcrInputBackend());
        var server = new McpServer(api, "wechat-driver", "1.0.0");
        var transport = new McpHttpTransport(server, port, token);
        transport.Start();

        Console.Error.WriteLine($"MCP HTTP 已启动: http://127.0.0.1:{transport.Port}/mcp"
            + (token is null ? "" : $" · Bearer {token}"));
    }

    private void RunProbeOnly(string baseDir, string[] args)
    {
        var host = new DriverHost(baseDir);
        var probe = host.ProbeToJson();
        var outPath = "artifacts/wechat/probe-report.json";
        var idx = Array.IndexOf(args, "--out");
        if (idx >= 0 && idx + 1 < args.Length) outPath = args[idx + 1];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (probe.Succeeded)
        {
            File.WriteAllText(outPath, probe.Value);
            Console.WriteLine($"OK: {outPath}");
        }
        else
        {
            File.WriteAllText(outPath, $"{{\"error\":\"{probe.Failure?.Message}\",\"code\":{(int)(probe.Failure?.Code ?? FailureCode.InternalError)}}}");
            Console.Error.WriteLine($"FAILED: {probe.Failure?.Code} {probe.Failure?.Message}");
        }
        host.Dispose();
    }
}
