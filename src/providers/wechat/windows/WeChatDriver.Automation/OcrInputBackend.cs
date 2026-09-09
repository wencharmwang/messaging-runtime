using WeChatDriver.Capture;
using WeChatDriver.Core;
using WeChatDriver.Windows;

namespace WeChatDriver.Automation;

/// <summary>
/// 面向 Qt 自绘微信 4.x 的主后端：OCR 读取 + 剪贴板/SendInput 写入。
/// UIA 对微信 4.x 几乎无用（仅窗口标题栏），故本后端接管会话/读取/写入/发送全部职责。
/// </summary>
public sealed class OcrInputBackend : IWindowAutomationBackend
{
    public sealed record DirectSendAttempt(bool SubmissionStarted, OperationResult Result);
    private readonly IOcrEngine _ocr;
    private readonly bool _ownsOcr;
    private WeChatWindowSession? _session;
    private nint _hwnd;
    private TargetWindowInput? _input;
    private WeChatLayout? _layout;
    private string? _verifiedConversation;
    private DateTime _verifiedConversationAtUtc = DateTime.MinValue;
    private ConversationHeaderReader? _headerReader;
    private ReadHistoryStats? _lastRead;
    private int _scrollChanges;
    private readonly WeChatDriver.UIA.NativeComposerReader _nativeComposer = new();
    private string _composerReadSource = "not_read";
    private string? _composerFallback;
    public bool MatchesComposerDraft(string expected, string? observed) => _composerReadSource.StartsWith("uia_", StringComparison.Ordinal)
        ? observed is not null && MessageFingerprint.Normalize(expected) == MessageFingerprint.Normalize(observed)
        : ComposerTextReader.MatchesDraft(expected, observed);
    public string NavigationMode => "visible_list_click";
    public string ReadInputMode => "background_window_messages";
    public bool BackgroundWriting { get; }
    public string WriteInputMode => BackgroundWriting ? "background_window_messages" : "foreground_guarded_input";
    private TargetWindowInput Input => _input ?? throw new WindowAutomationException("provider_unavailable","Connect WeChat first.");
    private WeChatLayout Layout => _layout ?? throw new WindowAutomationException("layout_unverified","Observe the window layout first.");

    public void ObserveLayout() => ObserveLayout(requireOpenConversation: true);

    public void ObserveListLayout() => ObserveLayout(requireOpenConversation: false);

    private void ObserveLayout(bool requireOpenConversation)
    {
        WindowAutomationException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Thread.Sleep(300);
                    EstablishSession();
                }
                using var dpi = new DpiAwarenessScope();
                Input.BeginObservation();
                _dpi = WindowDiscovery.GetDpi(_hwnd);
                using var frame = WindowCapture.CaptureWindow(_hwnd);
                _layout = requireOpenConversation
                    ? WeChatLayout.Detect(frame,_dpi)
                    : WeChatLayout.DetectList(frame,_dpi);
                Input.ExpectGeometry(frame.Width,frame.Height,_dpi);
                return;
            }
            catch (WindowAutomationException ex) when (ex.Code is "layout_unverified" or "window_changed" or "window_unavailable")
            {
                last = ex;
                _layout = null;
            }
        }
        throw last ?? new WindowAutomationException("layout_unverified", "微信窗口布局暂不可用，系统将在下一轮自动重试。");
    }

    public IReadOnlyDictionary<string,object?> Diagnostics()
    {
        ObserveListLayout();
        return new Dictionary<string,object?>
        {
            ["process_id"]=_session!.ProcessId, ["window_handle"]=_hwnd.ToInt64(),
            ["dpi"]=_dpi, ["window_width"]=Layout.Width, ["window_height"]=Layout.Height,
            ["window_health"]=DiagnoseWindow(),
            ["foreground"]=Input.Validate().Foreground, ["read_input_mode"]=ReadInputMode,
            ["write_input_mode"]=WriteInputMode,
            ["composer_read_source"]=_composerReadSource, ["composer_read_fallback"]=_composerFallback,
            ["navigation"]=NavigationMode, ["ocr_available"]=_ocr.IsAvailable, ["ocr_engine"]=_ocr.Language,
            ["conversation_open"]=Layout.HasOpenConversation,
            ["message_region"]=Layout.HasOpenConversation ? new { Layout.Messages.X,Layout.Messages.Y,Layout.Messages.Width,Layout.Messages.Height } : null,
            ["list_region"]=new { Layout.List.X,Layout.List.Y,Layout.List.Width,Layout.List.Height },
            ["header_region"]=Layout.HasOpenConversation ? new { Layout.Header.X,Layout.Header.Y,Layout.Header.Width,Layout.Header.Height } : null,
            ["current_header"]=Layout.HasOpenConversation ? ReadHeaderLines() : Array.Empty<string>(), ["header_fallback"]=_headerReader?.FallbackStatus, ["last_read"]=_lastRead, ["capture_source"]="runtime_printwindow_physical_pixels"
        };
    }

    private System.Drawing.Bitmap Capture(System.Drawing.Rectangle region)
    {
        Input.Validate();
        using var full=WindowCapture.CaptureWindow(_hwnd);
        if(full.Width!=Layout.Width || full.Height!=Layout.Height)
            throw new WindowAutomationException("layout_changed","WeChat changed size during capture.");
        return full.Clone(region,full.PixelFormat);
    }

    private string[] ReadHeaderLines()
    {
        // Exclude call/menu icons: they otherwise merge into the title as punctuation.
        var area=Layout.Header;
        area.Width=Math.Max(Px(160),area.Width-Px(180));
        using var header=Capture(area);
        _headerReader ??= new ConversationHeaderReader(_ocr,()=>
        {
            var english=new WindowsOcr("en-US");
            return english.IsAvailable ? english : new WindowsOcr();
        });
        return _headerReader.Read(header).ToArray();
    }

    private void VerifyReadTarget()
    {
        if(_verifiedConversation is null || !VerifyConversationTitle(_verifiedConversation))
            throw new WindowAutomationException("conversation_changed","The active chat changed during reading. No further scrolling was performed.");
    }


    // 当前窗口 DPI（EstablishSession 时读取）。像素锚常量按 120 DPI 标定，运行时按实际 DPI 等比缩放，
    // 这样即使窗口被拖到不同 DPI 的显示器、或系统缩放变化，锚点也不会失真。
    private int _dpi = WindowDiagnostics.ReferenceDpi;

    // 逐屏截图（审核读取过程/滚动重叠）。
    private string? _screenshotDir;
    private readonly List<string> _screenLog = new();

    // Pane boundaries are detected in the captured bitmap; no proportional sidebar crop is trusted.
    private const double SendX=0.88,SendW=0.11;
    private const int SendCenterFromBottomPx=57,SendButtonHeightPx=40;

    // 滚动标定：基准窗口 1118×809（消息区高 542px）下，4 notch 滚约 60% 屏、保留 2~3 条重叠锚点（实测防跳过下限）。
    // 微信 Qt 对单次滚轮事件的大 delta 不线性累加，故每次滚动都拆成多个 1-notch 事件（每次独立触发一次滚动）。
    // notch 数按消息区高度等比缩放，保持「约 60% 屏」滚动比例不随窗口高度漂移：
    // 窗口变高时固定 notch 会重叠更多、浪费 OCR；变矮时会滚过头、跳过短消息。
    private const int ReferenceNotches = 4;
    private const int ReferenceRegionHeightPx = 542; // = 809 - 65(顶) - 202(底)

    /// <summary>把标定于 120 DPI 的像素值按当前窗口 DPI 等比缩放（四舍五入）。</summary>
    private int Px(int v) => (int)Math.Round(v * _dpi / (double)WindowDiagnostics.ReferenceDpi);

    /// <summary>创建后端。OCR 优先用 RapidOCR(PaddleOCR)，不可用时回退 Windows OCR。</summary>
    public OcrInputBackend(Version? expectedVersion = null, IOcrEngine? ocr = null, bool backgroundWriting = true)
    {
        BackgroundWriting = backgroundWriting;
        if (ocr is not null)
        {
            _ocr = ocr;
            _ownsOcr = false;
        }
        else
        {
            var script = PaddleOcrLocator.Find();
            if (script is not null)
            {
                var paddle = new PaddleOcr(script);
                if (paddle.IsAvailable)
                {
                    _ocr = paddle;
                    _ownsOcr = true;
                    return;
                }
                paddle.Dispose();
            }
            _ocr = new WindowsOcr();
            _ownsOcr = false;
        }
    }

    /// <summary>释放自建 OCR 资源（如 PaddleOCR 子进程）。</summary>
    public void DisposeOcr()
    {
        if (_ownsOcr && _ocr is IDisposable d) d.Dispose();
    }

    /// <summary>启用读取过程的逐屏截图：每读一屏就保存消息区截图 + 记录该屏文本（标注新增/重叠）。</summary>
    public void EnableScreenshotCapture(string directory)
    {
        _screenshotDir = directory;
        _screenLog.Clear();
        Directory.CreateDirectory(directory);
    }

    /// <summary>保存当前消息区截图并记录该屏消息（标注重叠 + 截断）。截断由 ReadScreenMessages 提供。</summary>
    private void RecordScreen(int index, IReadOnlyList<string> lines, List<string> seen, bool topClipped, bool bottomClipped)
    {
        if (_screenshotDir is null) return;

        using var bmp = CaptureMessageRegion();
        bmp.Save(Path.Combine(_screenshotDir, $"screen_{index:00}.png"), System.Drawing.Imaging.ImageFormat.Png);

        var newCount = 0;
        var dupCount = 0;
        _screenLog.Add($"### 屏 {index:00}（OCR {lines.Count} 条）[顶截断={topClipped} 底截断={bottomClipped}]");
        foreach (var line in lines)
        {
            var norm = MessageFingerprint.Normalize(line);
            if (norm.Length < 1) continue;
            if (IsSeen(seen, norm))
            {
                dupCount++;
                _screenLog.Add($"  [重叠] {line}");
            }
            else
            {
                newCount++;
                _screenLog.Add($"  [新增] {line}");
            }
        }
        _screenLog.Add($"  → 新增 {newCount} 条，重叠 {dupCount} 条");
    }

    /// <summary>把逐屏日志写到截图目录下的 screens.txt。</summary>
    private void FlushScreenLog()
    {
        if (_screenshotDir is null) return;
        File.WriteAllText(Path.Combine(_screenshotDir, "screens.txt"), string.Join('\n', _screenLog));
    }

    public WeChatWindowSession? Session => _session;
    public bool OcrAvailable => _ocr.IsAvailable;
    public string OcrLanguage => _ocr.Language;

    public OperationResult<WeChatWindowSession> EstablishSession()
    {
        var result = WindowDiscovery.Discover(expectedVersion: null);
        if (result.Succeeded && result.Value is not null)
        {
            _session = result.Value;
            _hwnd = result.Value.WindowHandle;
            _input = new TargetWindowInput(_hwnd,result.Value.ProcessId);
            _dpi = WindowDiscovery.GetDpi(_hwnd);
        }
        return result;
    }

    /// <summary>诊断并自愈微信窗口（最小化/屏幕外自动还原+置前）。返回诊断报告。</summary>
    public WindowHealthReport DiagnoseWindow()
    {
        if (_session is null)
        {
            var r = EstablishSession();
            if (!r.Succeeded)
            {
                var msg = r.Failure?.Message ?? "无法建立微信会话";
                return new WindowHealthReport(false, false, false, false, 0, 0, 0, 0, 0, "Unusable", msg, new[] { msg });
            }
        }
        return WindowDiagnostics.Diagnose(_hwnd);
    }

    public OperationResult<CapabilityMatrix> ProbeCapabilities()
    {
        if (_session is null)
            return OperationResult<CapabilityMatrix>.Fail(FailureCode.SessionDisposed, "会话未建立");

        var entries = new List<CapabilityEntry>
        {
            new("uia_conversation_list", false, "Qt 自绘，UIA 不暴露"),
            new("uia_composer", false, "Qt 自绘，UIA 不暴露"),
            new("uia_send_control", false, "Qt 自绘，UIA 不暴露"),
            new("ocr_read", _ocr.IsAvailable, $"OCR {_ocr.Language}"),
            new("clipboard_write", true, "剪贴板 + Ctrl+V"),
            new("background_read", true, "HWND/PID-bound window messages; no global mouse or keyboard fallback"),
            new("list_navigation",true,"Fresh conversation-list OCR coordinates with exact header verification"),
            new("foreground_write",true,"Explicit foreground, window ownership and occlusion checks before input")
        };
        return OperationResult<CapabilityMatrix>.Ok(new CapabilityMatrix(_session.ClientVersion, _session.LocatorProfileId, entries));
    }

    public OperationResult<IReadOnlyList<ConversationState>> EnumerateConversations()
    {
        if (_session is null)
            return OperationResult<IReadOnlyList<ConversationState>>.Fail(FailureCode.SessionDisposed, "会话未建立");

        ObserveListLayout();
        var rows=ReadConversationRows();
        var result=rows.Select((r,index)=>new ConversationState(
            new ConversationKey($"pos:{index}",r.Name,$"window:{_hwnd};x:{r.X};y:{r.Y}"),
            null,string.IsNullOrEmpty(r.Preview)?null:MessageFingerprint.ActivityHash(r.Preview),
            // Opening a conversation clears its red unread badge. The badge is
            // UI state, not a new message; including it caused one incoming
            // message to trigger a second open after the first read.
            MessageFingerprint.ActivityHash($"{r.Preview}\n{r.DisplayTime}"),DateTimeOffset.Now,true,false)).ToArray();
        return OperationResult<IReadOnlyList<ConversationState>>.Ok(result);
    }

    public OperationResult<string?> ReadLatestMessageText(ConversationKey conversation)
    {
        if (_session is null)
            return OperationResult<string?>.Fail(FailureCode.SessionDisposed, "会话未建立");

        var lines = OcrMessageRegion();
        var latest = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l.Text));
        if (latest is null)
            return OperationResult<string?>.Fail(FailureCode.MessageTextUnavailable, "消息区 OCR 无可读文本", confidence: 0.0);

        return OperationResult<string?>.Ok(latest.Text.Trim(), latest.Confidence);
    }

    public OperationResult WriteComposer(ConversationKey conversation, string text)
    {
        if (_session is null)
            return OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立");
        if (string.IsNullOrWhiteSpace(text))
            return OperationResult.Fail(FailureCode.WriteFailed, "草稿文本为空");

        try
        {
            VerifyReadTarget();
            if (BackgroundWriting)
            {
                var before = ReadComposerText();
                if (!before.Succeeded || !string.IsNullOrEmpty(before.Value))
                    return OperationResult.Fail(FailureCode.WriteFailed, "Composer must be verifiably empty before background writing.");
                var bx = Layout.Composer.Left + Layout.Composer.Width / 2;
                var by = Layout.Composer.Top + Layout.Composer.Height / 2;
                Input.Click(bx, by);
                Input.WriteText(bx, by, text, VerifyReadTarget);
                return OperationResult.Ok(0.85);
            }
            Input.ActivateForWriting();
            Thread.Sleep(250);

            // 点击输入框中部，建立真实焦点。
            var cx=Layout.Composer.Left+Layout.Composer.Width/2;
            var cy=Layout.Composer.Top+Layout.Composer.Height/2;
            Input.Click(cx,cy,background:false);

            // 剪贴板 + Ctrl+V。
            InputHelper.SetClipboardText(text);
            VerifyReadTarget();
            Input.Chord(0x11,0x56);
            return OperationResult.Ok(0.85);
        }
        catch (WindowAutomationException) { throw; }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.ClipboardFailed, ex.Message);
        }
    }

    /// <summary>清空输入框：点击 + Ctrl+A + Delete。</summary>
    public OperationResult ClearComposer()
    {
        if (_session is null)
            return OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立");

        try
        {
            VerifyReadTarget();
            if (BackgroundWriting)
            {
                var existing = ReadComposerText();
                return existing.Succeeded && string.IsNullOrEmpty(existing.Value)
                    ? OperationResult.Ok()
                    : OperationResult.Fail(FailureCode.WriteFailed, "Background mode preserves existing or unreadable drafts.");
            }
            Input.ActivateForWriting();
            Thread.Sleep(200);
            var cx=Layout.Composer.Left+Layout.Composer.Width/2;
            var cy=Layout.Composer.Top+Layout.Composer.Height/2;
            Input.Click(cx,cy,background:false);
            VerifyReadTarget();
            Input.Chord(0x11,0x41);
            VerifyReadTarget();
            Input.Chord(0x2E);
            return OperationResult.Ok(0.8);
        }
        catch (WindowAutomationException) { throw; }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.WriteFailed, ex.Message);
        }
    }

    public OperationResult<string?> ReadComposerText()
    {
        if (_session is null)
            return OperationResult<string?>.Fail(FailureCode.SessionDisposed, "会话未建立");

        Input.Validate();
        using var button = WindowCapture.CaptureRegionFromBottom(_hwnd, SendX, SendW, Px(SendCenterFromBottomPx), Px(SendButtonHeightPx));
        var sendEnabled = FindGreenButtonCenter(button).found;
        // The empty editor can be absent from Qt's UIA tree and then appear after
        // text is written. Bypass the negative cache when Send is enabled so the
        // post-write verification gets one fresh native read before using OCR.
        var direct = _nativeComposer.Read(_hwnd, _session.ProcessId, Layout.Composer, retryUnavailable: sendEnabled);
        Input.Validate();
        if (direct.Available && (!string.IsNullOrWhiteSpace(direct.Text) || !sendEnabled))
        {
            _composerReadSource = direct.Source;
            _composerFallback = null;
            return OperationResult<string?>.Ok(direct.Text, 1.0);
        }
        _composerReadSource = "ocr";
        _composerFallback = direct.Available ? "uia_empty_with_send_enabled" : direct.Source;
        using var composer = CaptureComposerRegion();
        return ComposerTextReader.Read(composer, _ocr, sendEnabled);
    }

    public OperationResult SendMessage(ConversationKey conversation)
    {
        if (_session is null)
            return OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立");

        // 先确认发送按钮存在；提交前再次验证目标会话和输入焦点。
        using var bmp = WindowCapture.CaptureRegionFromBottom(_hwnd, SendX, SendW, Px(SendCenterFromBottomPx), Px(SendButtonHeightPx));
        var (buttonX, buttonY, found) = FindGreenButtonCenter(bmp);
        if (!found)
            return OperationResult.Fail(FailureCode.SendControlUnknown, "未探测到绿色发送按钮（输入框可能为空）");

        try
        {
            VerifyReadTarget();
            if (BackgroundWriting)
            {
                // Click the observed Send button; never inject Enter into the foreground app.
                var x = (int)(Layout.Width * SendX) + buttonX;
                var y = Layout.Height - Px(SendCenterFromBottomPx) - Px(SendButtonHeightPx) + buttonY;
                Input.Click(x, y);
                return OperationResult.Ok(0.85);
            }
            Input.ActivateForWriting();
            Thread.Sleep(150);
            // 再次点击输入框确保焦点。
            var cx=Layout.Composer.Left+Layout.Composer.Width/2;
            var cy=Layout.Composer.Top+Layout.Composer.Height/2;
            Input.Click(cx,cy,background:false);
            Thread.Sleep(100);
            // 使用 Enter 提交；不尝试其他快捷键或自动重试。
            VerifyReadTarget();
            Input.Chord(0x0D);
            return OperationResult.Ok(0.85);
        }
        catch (WindowAutomationException) { throw; }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.SendFailed, ex.Message);
        }
    }

    /// <summary>在位图中找绿色按钮像素，返回相对区域内的中心坐标。</summary>
    private static (int x, int y, bool found) FindGreenButtonCenter(System.Drawing.Bitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                // 微信「发送」按钮启用态绿色：G 显著高于 R/B。
                if (p.G > 140 && p.G > p.R + 30 && p.G > p.B + 30 && p.A > 200)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                    count++;
                }
            }
        }
        // 至少需要一定数量的绿色像素才算按钮（避免噪点）。
        if (count < 20 || maxX < 0)
            return (0, 0, false);
        return ((minX + maxX) / 2, (minY + maxY) / 2, true);
    }

    public OperationResult<bool> VerifySent(ConversationKey conversation, string expectedFingerprint)
    {
        if (_session is null)
            return OperationResult<bool>.Fail(FailureCode.SessionDisposed, "会话未建立");

        // 验证发送：输入框空则按钮变灰。这是比 OCR 输入框文本更可靠的信号
        // （OCR 区域会受刚上屏的消息气泡/工具栏干扰，误判）。
        using var bmp = WindowCapture.CaptureRegionFromBottom(_hwnd, SendX, SendW, Px(SendCenterFromBottomPx), Px(SendButtonHeightPx));
        var (_, _, greenFound) = FindGreenButtonCenter(bmp);
        if (!greenFound)
            return OperationResult<bool>.Ok(true, 0.9);

        return OperationResult<bool>.Fail(FailureCode.SendUnverified, "发送后按钮仍为绿色（输入框未清空）");
    }

    private System.Drawing.Bitmap CaptureMessageRegion() => Capture(Layout.Messages);
    private System.Drawing.Bitmap CaptureListRegion() => Capture(Layout.List);
    private System.Drawing.Bitmap CaptureComposerRegion() => Capture(Layout.Composer);
    private int MessageRegionHeight() => Layout.Messages.Height;
    private int MessageRegionWidth() => Layout.Messages.Width;

    private IReadOnlyList<OcrLine> OcrMessageRegion()
    {
        using var bmp = CaptureMessageRegion();
        return _ocr.RecognizePng(WindowCapture.ToPng(bmp));
    }

    private IReadOnlyList<OcrTextRect> OcrMessageRegionWithRects()
    {
        using var bmp = CaptureMessageRegion();
        return _ocr.RecognizePngWithRects(WindowCapture.ToPng(bmp));
    }

    private IReadOnlyList<OcrTextRect> OcrListRegionWithRects()
    {
        using var bmp = CaptureListRegion();
        return _ocr.RecognizePngWithRects(WindowCapture.ToPng(bmp));
    }

    /// <summary>Select a unique visible list row for the configured automatic reply path.</summary>
    public bool SelectConversationForDirectSend(string displayName)
    {
        if (_session is null) return false;
        // The worker re-observes the same conversation immediately before send.
        // Reuse that fresh identity proof so sending does not visibly click the
        // selected row for a third time. ObserveLayout still refreshes geometry.
        if (_verifiedConversation == displayName &&
            DateTime.UtcNow - _verifiedConversationAtUtc <= TimeSpan.FromSeconds(2))
        {
            try
            {
                ObserveLayout();
                return true;
            }
            catch (WindowAutomationException ex) when (ex.Code is "layout_unverified" or "layout_changed" or "window_changed" or "window_unavailable")
            {
                // Fall through to a fresh visible-row selection.
            }
        }
        _verifiedConversation = null;
        _verifiedConversationAtUtc = DateTime.MinValue;
        _lastRead = null;
        for (var clickAttempt = 0; clickAttempt < 3; clickAttempt++)
        {
            ObserveListLayout();
            var rows = ReadConversationRows();
            var row = ConversationListLocator.Unique(rows, displayName)
                ?? throw new WindowAutomationException("conversation_not_visible",
                    "The requested conversation is not in the visible list. Scroll the conversation list manually and retry.");
            Input.Click(Layout.List.Left + row.X, Layout.List.Top + row.Y);
            if (WaitForOpenConversation())
            {
                _verifiedConversation = displayName;
                _verifiedConversationAtUtc = DateTime.UtcNow;
                return true;
            }
        }
        throw new WindowAutomationException("layout_unverified",
            "The requested conversation did not open after three fresh visible-row clicks.");
    }

    private bool WaitForOpenConversation(string? expectedTitle = null, int maxWaitMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        do
        {
            Thread.Sleep(150);
            try
            {
                ObserveLayout();
                if (expectedTitle is null || VerifyConversationTitle(expectedTitle)) return true;
            }
            catch (WindowAutomationException ex) when (ex.Code is "layout_unverified" or "layout_changed" or "window_changed" or "window_unavailable")
            {
                // Qt can drop or defer a background row click while repainting.
                // The caller refreshes OCR coordinates and clicks the row again.
            }
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>Write an automatic reply without OCR/title readback, then submit it.</summary>
    public DirectSendAttempt WriteAndSendDirect(string text)
    {
        if (_session is null)
            return new(false, OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立"));
        if (string.IsNullOrWhiteSpace(text))
            return new(false, OperationResult.Fail(FailureCode.WriteFailed, "草稿文本为空"));
        var submissionStarted = false;
        try
        {
            var cx = Layout.Composer.Left + Layout.Composer.Width / 2;
            var cy = Layout.Composer.Top + Layout.Composer.Height / 2;
            if (BackgroundWriting)
            {
                Input.Click(cx, cy);
                Input.WriteText(cx, cy, text, static () => { });
            }
            else
            {
                Input.ActivateForWriting();
                Input.Click(cx, cy, background: false);
                InputHelper.SetClipboardText(text);
                Input.Chord(0x11, 0x56);
            }
            Thread.Sleep(150);
            using var button = WindowCapture.CaptureRegionFromBottom(
                _hwnd, SendX, SendW, Px(SendCenterFromBottomPx), Px(SendButtonHeightPx));
            var (buttonX, buttonY, found) = FindGreenButtonCenter(button);
            if (!found)
                return new(false, OperationResult.Fail(FailureCode.SendControlUnknown, "输入后未探测到绿色发送按钮"));
            submissionStarted = true;
            if (BackgroundWriting)
            {
                var x = (int)(Layout.Width * SendX) + buttonX;
                var y = Layout.Height - Px(SendCenterFromBottomPx) - Px(SendButtonHeightPx) + buttonY;
                Input.Click(x, y);
            }
            else
            {
                Input.Chord(0x0D);
            }
            return new(true, OperationResult.Ok(0.8));
        }
        catch (WindowAutomationException ex)
        {
            return new(submissionStarted, OperationResult.Fail(FailureCode.SendFailed, ex.Message));
        }
        catch (Exception ex)
        {
            return new(submissionStarted, OperationResult.Fail(FailureCode.SendFailed, ex.Message));
        }
    }

    private IReadOnlyList<ConversationRow> ReadConversationRows()
    {
        using var bitmap=CaptureListRegion();
        var rectangles=_ocr.RecognizePngWithRects(WindowCapture.ToPng(bitmap));
        return ConversationListLocator.Parse(rectangles,_dpi,bitmap);
    }

    private IReadOnlyList<OcrLine> OcrComposerRegion()
    {
        using var bmp = CaptureComposerRegion();
        return _ocr.RecognizePng(WindowCapture.ToPng(bmp));
    }

    private (int x,int y) MessageCenterWindow() =>
        (Layout.Messages.Left+Layout.Messages.Width/2,Layout.Messages.Top+Layout.Messages.Height/2);

    /// <summary>Locate only a freshly observed, unique conversation-list row. Never open global search.</summary>
    public bool SwitchConversation(string displayName)
    {
        if(_session is null) return false;
        // A generation-time freshness check usually targets the conversation
        // opened for the initial observation. Verify the still-open header in
        // place; only navigate again if the user or WeChat changed the chat.
        if (_verifiedConversation == displayName)
        {
            try
            {
                ObserveLayout();
                if (VerifyConversationTitle(displayName))
                {
                    _verifiedConversationAtUtc = DateTime.UtcNow;
                    return true;
                }
            }
            catch (WindowAutomationException ex) when (ex.Code is "layout_unverified" or "layout_changed" or "window_changed" or "window_unavailable")
            {
                // Fall through to a fresh visible-row selection.
            }
        }
        _verifiedConversation=null;
        _verifiedConversationAtUtc=DateTime.MinValue;
        _lastRead=null;
        for (var clickAttempt = 0; clickAttempt < 3; clickAttempt++)
        {
            ObserveListLayout();
            var rows=ReadConversationRows();
            var row=ConversationListLocator.Unique(rows,displayName)
                ?? throw new WindowAutomationException("conversation_not_visible","The requested conversation is not in the visible list. Scroll the conversation list manually and retry.");
            Input.Click(Layout.List.Left+row.X,Layout.List.Top+row.Y);
            if (!WaitForOpenConversation(displayName)) continue;
            _verifiedConversation=displayName;
            _verifiedConversationAtUtc=DateTime.UtcNow;
            return true;
        }
        throw new WindowAutomationException("conversation_unverified","The requested list row was clicked three times, but OCR could not verify the exact chat header. Inspect diagnose_provider.current_header; no message was submitted by navigation.");
    }

    public bool VerifyConversationTitle(string displayName) =>
        _session is not null && _layout is not null &&
        ReadHeaderLines().Contains(MessageFingerprint.Normalize(displayName),StringComparer.Ordinal);

    public bool IsForeground => _session is not null && Input.Validate().Foreground;

    // An empty chat is a valid state: readiness is established by the exact header, not by nonempty OCR.
    private bool WaitForChatContent(int maxWaitMs=8000)
    {
        VerifyReadTarget();
        Thread.Sleep(180);
        return true;
    }

    public sealed record ReadHistoryStats(int Scrolls, int OcrCalls, int ObservedScrollChanges=0, int MessageCount=0);

    /// <summary>Bounded tail observation for monitoring. Preserve repeated identical bubbles and never walk history.</summary>
    public IReadOnlyList<string> ReadLatestMessages(int maxMessages = 50)
    {
        _scrollChanges = 0;
        VerifyReadTarget();
        // Moving down is needed when the user has inspected older history. Already at
        // the tail, this performs two unchanged rounds, with no visible movement.
        ScrollToBottom();
        WaitForChatContent();
        var screen = ReadScreenMessages();
        var messages = MessageCleaner.FilterUiNoise(screen.Messages).ToList();
        if (screen.TopClipped && messages.Count > 0) messages.RemoveAt(0);
        // BottomClipped is a 25px proximity heuristic for stitching history pages.
        // At the confirmed tail, a complete final bubble can occupy that margin.
        // It is not evidence that the newest text is actually truncated.
        _lastRead = new ReadHistoryStats(0, 1, _scrollChanges, messages.Count);
        var result = messages.TakeLast(maxMessages).ToArray();
        if (_verifiedConversation is not null) _verifiedConversationAtUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>
    /// 滚动读取聊天记录：从当前可见消息区开始，向上滚动逐屏 OCR，去重合并。
    /// 返回按「最早 → 最新」大致顺序的文本行。仅限文字，不处理图片/文件。
    /// </summary>
    public IReadOnlyList<string> ReadChatHistory(out ReadHistoryStats stats, int maxMessages = 100, int maxScrolls = 25)
    {
        stats = new ReadHistoryStats(0, 0);
        _lastRead=null;
        _scrollChanges=0;
        if (_session is null) return Array.Empty<string>();

        var seen = new List<string>();
        var result = new List<string>();
        var scrolls = 0;
        var ocrCalls = 0;
        var noNewStreak = 0; // 连续无新内容屏数（用于到底后停止）
        string? carry = null; // 跨屏截断的消息下半（等待与更早屏的上半拼接）

        // 先把当前消息区滚到最底部（看最新消息）。
        ScrollToBottom();
        // 就绪轮询：GPU OCR 很快，若不等渲染完成，首次 OCR 会读到空/半屏消息区（竞态）。
        WaitForChatContent();

        for (var scroll = 0; scroll < maxScrolls && result.Count < maxMessages; scroll++)
        {
            var screen = ReadScreenMessages();
            ocrCalls++;
            var messages = screen.Messages.ToList();
            if(messages.Count==0) break; // 早→晚，每条消息一个气泡
            var topClipped = screen.TopClipped;
            var bottomClipped = screen.BottomClipped;
            RecordScreen(scroll, messages, seen, topClipped, bottomClipped);

            var anyNew = false;

            // 1) 拼接 carry：上屏顶部截断的消息下半 + 本屏底部截断的消息上半 → 完整消息
            if (carry is not null)
            {
                if (bottomClipped && messages.Count > 0)
                {
                    carry = messages[^1] + MessageCleaner.StripDirectionPrefix(carry); // 上半 + 下半（剥掉下半重复的前缀）
                    messages.RemoveAt(messages.Count - 1);
                }
                anyNew |= TryAdd(carry, seen, result);
                carry = null;
            }

            // 2) 本屏顶部截断 → 最早一条消息是"消息下半"，暂存 carry
            string? nextCarry = null;
            if (topClipped && messages.Count > 0)
            {
                nextCarry = messages[0];
                messages.RemoveAt(0);
            }

            // 3) 剩余消息去重加入。
            // 关键时序：messages 是「早→晚」（旧→新），但屏处理顺序是「新屏→旧屏」（屏0最新先处理）。
            // result 按「新→旧」累积，最后 result.Reverse() 一次性翻成「旧→新」。
            // 所以每屏必须【倒序】追加（新→旧），否则 reverse 会把每屏内部顺序也反掉 → 消息穿插乱序。
            for (var i = messages.Count - 1; i >= 0; i--)
                anyNew |= TryAdd(messages[i], seen, result);

            carry = nextCarry;

            if (result.Count >= maxMessages) break;
            if (!anyNew)
            {
                // 连续 2 屏无新内容 → 已到顶（消息区滚不动了），停止。
                if (++noNewStreak >= 2) break;
                ScrollUp();
                scrolls++;
                continue;
            }
            noNewStreak = 0;
            ScrollUp();
            scrolls++;
        }

        // flush 残留 carry（最顶端一条消息顶部截断、无更早屏可拼）
        if (carry is not null)
            TryAdd(carry, seen, result);

        FlushScreenLog();
        result.Reverse();
        // 输出前清洗：合并超长消息跨屏产生的截断段/重复段，让「复制消息」拿到干净文本。
        var cleaned = MessageCleaner.Clean(result);
        stats = new ReadHistoryStats(scrolls, ocrCalls, _scrollChanges, cleaned.Count);
        _lastRead=stats;
        return cleaned.TakeLast(maxMessages).ToArray();
    }

    /// <summary>去重后加入结果；返回是否新增。</summary>
    private static bool TryAdd(string text, List<string> seen, List<string> result)
    {
        var norm = MessageFingerprint.Normalize(text);
        if (norm.Length < 1) return false;
        if (IsSeen(seen, norm)) return false;
        seen.Add(norm);
        result.Add(text);
        return true;
    }

    private static bool IsSeen(List<string> seen, string norm)
    {
        foreach (var s in seen)
            if (TextSimilarity.IsDuplicate(s, norm)) return true;
        return false;
    }

    /// <summary>
    /// 消息类型标注：识别图片/音频消息，转换为可读标注。
    /// 图片消息 OCR 直接读图片内文字（无需特殊处理）；微信的"[图片]"标签转为"[图片消息]"。
    /// 音频消息 OCR 读出秒数（"3"或") 3"），转为"[音频 N秒]"。
    /// </summary>
    private static string AnnotateMessageType(string text)
    {
        var t = text.Trim();
        // 音频秒数：纯数字 1-3 位，可能带秒号"、"、")"、空白
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\)\(]?\s*\d{1,3}\s*""?\s*$"))
        {
            var digits = new string(t.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var sec) && sec >= 1 && sec <= 600)
                return $"[音频 {sec}秒]";
        }
        // 图片消息标识（微信列表预览或消息区）
        if (t == "[图片]") return "[图片消息]";
        return text;
    }

    /// <summary>音频秒数行的 OCR 模式（与 AnnotateMessageType 保持一致）。</summary>
    private static readonly System.Text.RegularExpressions.Regex AudioSecondsRegex =
        new(@"^[\)\(]?\s*\d{1,3}\s*""?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 从 OCR rects 构建「消息」列表（按气泡分组，早→晚）。
    /// 同一气泡内的多行文本（y-gap ≤ 30px）合并成一条消息；不同气泡间 y-gap > 30px 视为新消息。
    /// 方向判定：优先用气泡背景色——微信「我发的」气泡为浅绿（唯一信号），「对方发的」为近白。
    /// 仅靠气泡文本右边界判方向会在消息被底部截断（最后一行是短行）时误判，故用颜色兜底、位置回退。
    /// 音频消息：首块为秒数时标注 [音频 N秒]，有转写文字则标注 [音频转文字 N秒] 文字。
    /// </summary>
    private static IReadOnlyList<string> BuildMessagesFromRects(IReadOnlyList<OcrTextRect> rects, int regionWidth, System.Drawing.Bitmap bmp)
    {
        const int bubbleGap = 30;
        var sorted = rects.OrderBy(r => r.Y).ToList();
        var messages = new List<string>();
        List<string>? bubble = null;
        List<OcrTextRect>? bubbleRects = null;
        var bubbleMaxRight = int.MinValue;
        var prevBottom = int.MinValue;

        void Flush()
        {
            if (bubble is null || bubble.Count == 0) { bubble = null; bubbleRects = null; return; }
            // 方向：绿色气泡 → 我；否则回退到气泡文本右边界（我发的靠右、对方靠左）。
            var prefix = DetectDirection(bubbleRects!, bmp, regionWidth, bubbleMaxRight);
            var first = bubble[0];
            if (AudioSecondsRegex.IsMatch(first))
            {
                var sec = new string(first.Where(char.IsDigit).ToArray());
                var rest = bubble.Skip(1).ToList();
                messages.Add(prefix + (rest.Count > 0
                    ? $"[音频转文字 {sec}秒] " + string.Join(" ", rest)
                    : $"[音频 {sec}秒]"));
            }
            else
            {
                messages.Add(prefix + AnnotateMessageType(string.Join(" ", bubble)));
            }
            bubble = null;
            bubbleRects = null;
            bubbleMaxRight = int.MinValue;
        }

        foreach (var r in sorted)
        {
            var text = (r.Text ?? "").Trim();
            if (text.Length < 1) continue;

            if (bubble is not null && r.Y - prevBottom > bubbleGap) Flush();
            bubble ??= new List<string>();
            bubbleRects ??= new List<OcrTextRect>();
            bubble.Add(text);
            bubbleRects.Add(r);
            bubbleMaxRight = Math.Max(bubbleMaxRight, r.X + r.Width);
            prevBottom = r.Y + r.Height;
        }
        Flush();
        return messages;
    }

    /// <summary>
    /// 判定单个气泡方向：先采样气泡背景色（绿色=我），颜色不明确时回退到文本右边界。
    /// </summary>
    private static string DetectDirection(IReadOnlyList<OcrTextRect> bubble, System.Drawing.Bitmap bmp, int regionWidth, int bubbleMaxRight)
    {
        if (BubbleIsGreen(bmp, bubble)) return MessageCleaner.MinePrefix;
        return bubbleMaxRight > regionWidth * 0.85 ? MessageCleaner.MinePrefix : MessageCleaner.OtherPrefix;
    }

    /// <summary>
    /// 采样气泡像素，判断是否为「我」的绿色气泡。
    /// 两类采样点：
    /// 1) 文本左右 padding 区（距文本边缘 4px，气泡 padding 通常 ~12px，落在气泡内而非头像/背景）；
    /// 2) 相邻文本行之间的空隙（行距中间、水平居中）——文本行间一定是气泡色，适用于长消息文本几乎填满气泡、
    ///    左右 padding 很小导致左右采样落空的情况。
    /// 任一采样点为绿即判「我」。
    /// </summary>
    private static bool BubbleIsGreen(System.Drawing.Bitmap bmp, IReadOnlyList<OcrTextRect> bubble)
    {
        if (bubble.Count == 0) return false;
        var minX = bubble.Min(r => r.X);
        var maxRight = bubble.Max(r => r.X + r.Width);
        var minY = bubble.Min(r => r.Y);
        var maxY = bubble.Max(r => r.Y + r.Height);
        var midY = (minY + maxY) / 2;

        var probes = new List<(int x, int y)>
        {
            (minX - 4, midY), (minX - 4, minY + 4), (minX - 4, maxY - 4),
            (maxRight + 4, midY), (maxRight + 4, minY + 4), (maxRight + 4, maxY - 4),
        };

        // 行间隙采样：文本行之间空隙一定是气泡色，补足「长消息填满气泡、左右 padding 小」的情况。
        var byY = bubble.OrderBy(r => r.Y).ToList();
        for (var i = 1; i < byY.Count; i++)
        {
            var prevBottom = byY[i - 1].Y + byY[i - 1].Height;
            var gap = byY[i].Y - prevBottom;
            if (gap >= 4)
            {
                var cx = (minX + maxRight) / 2;
                probes.Add((cx, prevBottom + gap / 2));
            }
        }

        foreach (var (x, y) in probes)
        {
            if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) continue;
            var p = bmp.GetPixel(x, y);
            if (MessageCleaner.IsMineBubbleGreen(p.R, p.G, p.B)) return true;
        }
        return false;
    }

    /// <summary>一屏的读取结果：按气泡分组的消息 + 顶部/底部截断标志。</summary>
    private sealed record ScreenRead(IReadOnlyList<string> Messages, bool TopClipped, bool BottomClipped);

    /// <summary>
    /// 读取一屏：同一窗口位图内完成 OCR 和气泡分组；读取过程不点击消息正文。
    /// 截断用文本块 Y 坐标判断（比像素法可靠：贴顶/贴底即视为气泡被切）。
    /// </summary>
    private ScreenRead ReadScreenMessages()
    {
        VerifyReadTarget();
        using var bmp=CaptureMessageRegion();
        var rects=_ocr.RecognizePngWithRects(WindowCapture.ToPng(bmp));
        VerifyReadTarget();

        var sorted = rects.Where(r => !string.IsNullOrWhiteSpace(r.Text)).OrderBy(r => r.Y).ToList();
        var screenHeight = MessageRegionHeight();

        // 顶部截断：最早一条文本块贴顶（气泡被上切）；底部截断：最晚一条文本块贴底（气泡被下切）。
        var topClipped = sorted.Count > 0 && sorted[0].Y < 25;
        var bottomClipped = sorted.Count > 0 && sorted[^1].Y + sorted[^1].Height > screenHeight - 25;

        // 捕获消息区位图用于气泡颜色方向判定（与 OCR 同一静止画面，供绿色气泡识别「我」）。
        return new ScreenRead(BuildMessagesFromRects(sorted, MessageRegionWidth(), bmp), topClipped, bottomClipped);
    }

    private void ScrollUp()
    {
        VerifyReadTarget();
        var previous=MessageRegionHash();
        var (mx, my) = MessageCenterWindow();
        // 微信 Qt 对单次滚轮事件的大 delta 不线性累加（实测 120*12 与 120*4 滚动距离几乎一样），
        // 故拆成多次 1-notch 事件，每次独立触发一次滚动。
        // notch 数按消息区高度等比缩放（基准 542px→4 notch ≈ 60% 屏、40% 重叠），
        // 保持重叠比例恒定：窗口变高时增加滚动、减少重复截图；变矮时减少滚动、避免跳过消息。
        var regionHeight = MessageRegionHeight();
        var notches = Math.Clamp((int)Math.Round((double)ReferenceNotches * regionHeight / ReferenceRegionHeightPx), 1, 30);
        for (var i = 0; i < notches; i++)
        {
            Input.Wheel(mx,my,120);
            Thread.Sleep(20);
        }
        // 滚动后渲染窗口略增；400ms 保底避免竞态（旧内容已去重→早停）。
        Thread.Sleep(400);
        VerifyReadTarget();
        if(MessageRegionHash()!=previous) _scrollChanges++;
    }

    private void ScrollToBottom()
    {
        VerifyReadTarget();
        var (mx, my) = MessageCenterWindow();
        // 滚到底：向下滚动直到消息区内容不再变化（= 已到最底部）。不能用固定 notch 数——
        // 上次读取若把会话滚到很老的位置，固定 30 notch 只够滚约 4~5 屏，读会从中间开始。
        // 用截图哈希判断「内容是否还在变」，连续 2 轮不变即到底；每轮 8 notch、最多 30 轮（超长会话也够）。
        var prevHash = MessageRegionHash();
        var stableRounds = 0;
        for (var round = 0; round < 30; round++)
        {
            VerifyReadTarget();
            Input.Wheel(mx,my,-120,8);
            Thread.Sleep(180); // 等渲染
            var hash = MessageRegionHash();
            if (hash == prevHash)
            {
                if (++stableRounds >= 2) break;
            }
            else
            {
                stableRounds = 0;
                _scrollChanges++;
            }
            prevHash = hash;
        }
        // 到底后 WaitForChatContent 由 ReadChatHistory 兜底，保证首次 OCR 不读到空/半屏。
    }

    /// <summary>
    /// 消息区截图的降采样灰度哈希（FNV-1a），用于判断「滚动后内容是否还在变化」（是否已到底/到顶）。
    /// 只做像素采样，不做 OCR，足够判断内容是否稳定。
    /// </summary>
    private ulong MessageRegionHash()
    {
        using var bmp = CaptureMessageRegion();
        ulong hash = 1469598103934665603UL; // FNV-1a offset basis
        const int step = 16;
        for (var y = 0; y < bmp.Height; y += step)
        {
            for (var x = 0; x < bmp.Width; x += step)
            {
                var p = bmp.GetPixel(x, y);
                var g = (byte)((p.R + p.G + p.B) / 3);
                hash = (hash ^ g) * 1099511628211UL; // FNV-1a prime
            }
        }
        return hash;
    }
}
