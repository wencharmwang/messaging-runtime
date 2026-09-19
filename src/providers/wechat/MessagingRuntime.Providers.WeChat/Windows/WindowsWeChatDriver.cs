using WeChatDriver.Automation;
using WeChatDriver.Core;
using WeChatDriver.Windows;

namespace MessagingRuntime.Providers.WeChat.Windows;

/// <summary>All UI work is serialized, including the entire locate/write/send/verify transaction.</summary>
public sealed class WindowsWeChatDriver(Func<OcrInputBackend> factory) : IWeChatDriver
{
    private readonly object _gate = new();
    private OcrInputBackend? _backend;
    private bool _disposed;
    private OcrInputBackend Backend
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _backend ??= factory(); }
    }
    public bool Connect()
    {
        // Refresh on every operation so a recreated Qt main window replaces a
        // stale cached HWND without requiring a Runtime restart.
        // A failed refresh must not be reported as success. "A session object exists" is not
        // the same as "that session still points at a live window": returning true here is how
        // a dead handle used to reach the capture code and surface as capture_failed instead of
        // the honest window_unavailable. Keep the cached binding only while it is genuinely usable.
        return Run(() => Backend.EstablishSession().Succeeded || Backend.HasUsableSession());
    }
    public bool BusyWithForegroundSearch => !_disposed && _backend is not null && _backend.ForegroundSearchActive;
    private void Ready(bool writing = false)
    {
        if (!Connect()) throw new MessagingException("provider_unavailable", "WeChat is not available in this Windows session.");
        var health = Backend.DiagnoseWindow();
        if (health.Status is not ("Healthy" or "Degraded"))
            throw new MessagingException("provider_unavailable", "WeChat window health does not permit this operation.");
        // Legacy size/DPI warnings do not invalidate the dynamic pane detector.
        // Require a fresh verified layout before writing; every event still checks geometry and focus.
        if (writing) Backend.ObserveLayout();
    }
    public IReadOnlyList<WeChatConversation> ListConversations()
    {
        return Run(() =>
        {
            Ready();
            var result = Backend.EnumerateConversations();
            if (!result.Succeeded || result.Value is null) throw new MessagingException("read_failed", "Could not read the WeChat conversation list.");
            return result.Value.Select(c => new WeChatConversation(c.Key.DisplayName, c.Key.StableHint, c.UnreadCount,
                c.LastPreviewHash,c.LastMessageFingerprint)).ToArray();
        }, skipWhenSearching: true);
    }
    /// <summary>
    /// Restore only. Deliberately does not call <see cref="Ready"/> first: the window being
    /// minimized or hidden is exactly the state this exists to fix.
    /// </summary>
    public void RestoreWindow()
    {
        Run(() =>
        {
            if (!Backend.RestoreWindowSession())
                throw new MessagingException("window_unavailable", "WeChat is not running on this desktop, so its window cannot be restored.");
            return true;
        }, skipWhenSearching: true);
    }

    /// <summary>
    /// Scroll the conversation list back to the top. Same shape as <see cref="RestoreWindow"/>:
    /// an explicit recovery the caller opts into, never part of a read.
    /// </summary>
    public void ScrollConversationListToTop()
    {
        Run(() =>
        {
            if (!Backend.ScrollConversationListToTop())
                throw new MessagingException("conversation_list_unavailable",
                    "WeChat's conversation list is not available to scroll; the window may be closed or hidden.");
            return true;
        }, skipWhenSearching: true);
    }

    private void Locate(string name, bool allowGlobalSearch = false, bool background = false)
    {
        // 用户点名的目标（allowGlobalSearch）：**先试可见列表行点击，找不到再走全局搜索**。
        //
        // 以前 allowGlobalSearch 直接等于"走搜索"，于是每一次按名字读取都要把微信切到前台；
        // 而切不到前台时整条路都失败（实测：前台为 False 时，按名字读全部 contact_not_found，
        // 连列表里明明看得见的会话也一样）。行点击不抢前台也不慢，行内验证同样有标题校验兜底，
        // 所以先试它；只有它报"这条不在可见列表里 / 点完核不过"时才动用搜索。
        if (allowGlobalSearch)
        {
            try
            {
                if (Backend.SwitchConversation(name)) return;
            }
            catch (WindowAutomationException error)
                when (error.Code is "conversation_not_visible" or "conversation_unverified"
                      or "window_unavailable" or "layout_unverified")
            {
                // 落到下面的全局搜索。
            }
            if (Backend.SelectConversationByGlobalSearch(name)) return;
            throw new MessagingException("conversation_unavailable", "The conversation could not be opened for reading.");
        }
        try
        {
            if (Backend.SwitchConversation(name)) return;
            throw new MessagingException("conversation_unavailable", "The conversation could not be opened for reading.");
        }
        catch (WindowAutomationException error)
            when (!background && error.Code == "conversation_not_visible")
        {
            // 目标只是被滚到列表下面去了。以前这里直接把「看不到这条」还给用户，并让他自己去滚列表
            // 找 —— 他点的是"帮我读这条"，把活推回去就是没做完。而全局搜索本来就能定位不在可见
            // 列表里的会话（/api/read 的用户点名路径一直走的就是它）。
            // 只对用户发起的读取这么做：后台读取不能去抢用户的前台。
            if (Backend.SelectConversationByGlobalSearch(name)) return;
            throw;
        }
    }

    /// <summary>
    /// Reading a WeChat conversation means opening it, which moves whatever the user is doing.
    /// Background monitoring therefore yields while the user is working in WeChat — unless the
    /// conversation we want is already the open one, in which case nothing moves.
    ///
    /// Only the background automatic-reply agent yields. A read the person asked for — a button
    /// in the UI, or an interactive assistant answering on their behalf — must never be refused
    /// because of their own activity: pressing "organize" and being told "you are using WeChat"
    /// is the assistant blocking the very thing it was asked to do. Measured: the organizer's
    /// reads were refused five times this way, and the organizer itself had just brought WeChat
    /// to the foreground, so the busy signal was its own doing.
    ///
    /// Called after Ready(), so an unusable window is still reported as provider_unavailable
    /// instead of being mistaken for "the user is busy in WeChat".
    /// </summary>
    private void YieldToUserIfBusy(bool alreadyOpen, bool background)
    {
        if (!background || alreadyOpen) return;
        if (!Backend.UserIsInWeChat()) return;
        throw new MessagingException("wechat_in_use", "The user is working in WeChat; this read waits until they stop.");
    }
    /// <summary>
    /// Reading a conversation means opening it, so a background read moves whatever the user had
    /// open. Put their chat back afterwards: they left WeChat on their own conversation, and
    /// coming back to a different one is a surprise they cannot undo. Only background reads do
    /// this — a user-commanded open is meant to stay where it went.
    ///
    /// Only while WeChat is actually in front. When it is not, the person cannot see the chat that
    /// was swapped out, so swapping it back repairs nothing — it buys one more synthetic row click,
    /// one more chance for Qt to activate itself and pull WeChat in front of whatever they are
    /// doing. That invisible round trip is what "它一直在跳" was: every background read moved the
    /// selection twice, and the second move existed only to undo a change nobody could see.
    /// </summary>
    private void RestoreChatAfterRead(string? displaced, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displaced)) return;
        if (SameChatTitle(displaced, displayName)) return;
        try
        {
            if (!Backend.IsForeground) return;
            Backend.SwitchConversation(displaced!);
        }
        catch (Exception) { /* best effort: the next read re-establishes whatever it needs */ }
    }

    /// <summary>
    /// Background window messages are not supposed to activate WeChat. Keep the caller's
    /// foreground handle as a transaction invariant anyway: some Qt builds may activate
    /// themselves while processing a synthetic click. Restore only when WeChat is the window
    /// that unexpectedly came forward, never over a newer foreground choice made by the user.
    /// </summary>
    private void RestoreForegroundAfterBackgroundOperation(nint previousForeground)
    {
        try
        {
            if (previousForeground == nint.Zero) return;
            // Qt processes the synthetic row click and only then decides to activate itself, so a
            // single restore can be undone a moment later. Check twice instead of once: the repair
            // is cheap, and a missed one leaves WeChat in front of whatever the person is typing in.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var currentForeground = InputHelper.CurrentForeground();
                if (previousForeground == currentForeground) return;
                var session = Backend.Session;
                if (session is null || WindowDiscovery.GetWindowProcessId(currentForeground) != session.ProcessId) return;
                InputHelper.RestoreForeground(previousForeground);
                if (attempt == 0) Thread.Sleep(150);
            }
        }
        catch (Exception) { /* foreground repair must never hide the operation's real outcome */ }
    }

    /// <summary>
    /// A chat header is not the list row: a group header carries the member count
    /// ("新疆羊肉串( 14"), so an exact comparison would re-open the chat the user is already on.
    /// </summary>
    private static bool SameChatTitle(string? header, string rowName)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        if (HeaderIsExactly(header, rowName)) return true;
        var bare=ConversationListLocator.BareName(header!);
        var key=MessageFingerprint.NameKey(bare);
        var wanted=MessageFingerprint.NameKey(rowName);
        return key.Length>0&&wanted.Length>0&&(key.Contains(wanted,StringComparison.Ordinal)||wanted.Contains(key,StringComparison.Ordinal));
    }

    /// <summary>
    /// Strict: the open chat IS this conversation. Used to skip a row click, so a loose match
    /// would risk reading a different conversation — the member count is the only allowance.
    /// Compared on the name key, because OCR writes spaces inside a CJK name.
    /// </summary>
    private static bool HeaderIsExactly(string? header, string rowName) =>
        !string.IsNullOrWhiteSpace(header) &&
        MessageFingerprint.NameKey(ConversationListLocator.BareName(header!)) == MessageFingerprint.NameKey(rowName);

    private string? DisplacedChat(bool allowGlobalSearch)
    {
        if (allowGlobalSearch) return null;
        try { return Backend.OpenConversationTitle(); }
        catch (Exception) { return null; }
    }

    public IReadOnlyList<string> ReadMessages(string displayName, int limit, bool allowGlobalSearch = false, bool background = false)
    {
        return Run(() =>
        {
            var previousForeground = background ? InputHelper.CurrentForeground() : nint.Zero;
            if (allowGlobalSearch) Backend.RestoreWindowSession();
            Ready();
            var displaced = DisplacedChat(allowGlobalSearch);
            var alreadyOpen = HeaderIsExactly(displaced, displayName);
            YieldToUserIfBusy(alreadyOpen, background);
            try
            {
                // Already the open chat: no row click, so nothing moves at all.
                if (!alreadyOpen) Locate(displayName, allowGlobalSearch, background);
                return Backend.ReadChatHistory(out _, limit);
            }
            finally
            {
                RestoreChatAfterRead(displaced, displayName);
                RestoreForegroundAfterBackgroundOperation(previousForeground);
            }
        });
    }
    public IReadOnlyList<string> ReadLatestMessages(string displayName, int limit, bool allowGlobalSearch = false, bool background = false) => Run(() =>
    {
        var previousForeground = background ? InputHelper.CurrentForeground() : nint.Zero;
        if (allowGlobalSearch) Backend.RestoreWindowSession();
        Ready();
        var displaced = DisplacedChat(allowGlobalSearch);
        var alreadyOpen = HeaderIsExactly(displaced, displayName);
        YieldToUserIfBusy(alreadyOpen, background);
        try
        {
            // Already the open chat: no row click, so nothing moves at all.
            if (!alreadyOpen) Locate(displayName, allowGlobalSearch, background);
            return Backend.ReadLatestMessages(limit);
        }
        finally
        {
            RestoreChatAfterRead(displaced, displayName);
            RestoreForegroundAfterBackgroundOperation(previousForeground);
        }
    });
    public SendReceipt SendText(string displayName, string text, bool allowGlobalSearch = false)
    {
        return Run<SendReceipt>(() =>
        {
            var previousForeground = Backend.BackgroundWriting ? InputHelper.CurrentForeground() : nint.Zero;
            var submissionStarted = false;
            // Sending opens the target chat. Background mode keeps the OS foreground unchanged;
            // the explicit foreground fallback may bring WeChat forward. Remember the displaced
            // chat and put it back once the message is confirmed. Leave it on the uncertain path:
            // seeing that chat is how the user checks what actually happened.
            var displaced = allowGlobalSearch ? null : DisplacedChat(false);
            try
            {
            if (allowGlobalSearch) Backend.RestoreWindowSession();
            Ready();
            // 当前打开的就是目标会话时，直接写草稿发送——不搜索、不切换、不抢焦点。
            // 页头 OCR 读得出且确为目标才走这条快路径；读不出或不是目标则回落定位
            // （搜索/列表点击），所以 OCR 失败不会阻断，识别成功则完全后台发送。
            var alreadyOpen = false;
            try { alreadyOpen = HeaderIsExactly(Backend.OpenConversationTitle(), displayName); }
            catch { alreadyOpen = false; }
            if (!alreadyOpen)
            {
                var selected = allowGlobalSearch
                    ? Backend.SelectConversationByGlobalSearch(displayName)
                    : Backend.SelectConversationForDirectSend(displayName);
                if (!selected)
                    throw new MessagingException("conversation_not_visible", "Could not select the requested conversation row.");
            }
            var key = new ConversationKey("", displayName, null);
            var sendAttempt = Backend.WriteAndSendDirect(text);
            submissionStarted = sendAttempt.SubmissionStarted;
            if (!sendAttempt.Result.Succeeded)
                throw new MessagingException("send_failed", "The driver could not confirm the send action.");
            // Qt can keep the enabled button painted briefly after processing the
            // click. Poll the button state only; direct mode intentionally avoids
            // OCR/UIA draft reads. Never click a second time during verification.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(250);
                var verified = Backend.VerifySent(key, "");
                if (verified.Succeeded && verified.Value == true)
                {
                    RestoreChatAfterRead(displaced, displayName);
                    return new("sent", null, "send_button_disabled; UI submission verified, recipient delivery is not verified");
                }
            }
            throw new MessagingException("send_uncertain", "WeChat send button stayed enabled after submission; do not retry automatically.");
            }
            catch (Exception ex) when (!submissionStarted)
            {
                throw new SendNotAttemptedException(ex switch
                {
                    MessagingException messaging => messaging.Code,
                    WindowAutomationException window => window.Code,
                    _ => "preflight_failed"
                });
            }
            finally { RestoreForegroundAfterBackgroundOperation(previousForeground); }
        });
    }

    public IReadOnlyDictionary<string,object?> GetDiagnostics() => Run(() => { Ready(); return Backend.Diagnostics(); });

    private T Run<T>(Func<T> action, bool skipWhenSearching = false)
    {
        // A foreground search drives the real WeChat UI for a couple of seconds. The
        // periodic visible-list scan must not queue behind it: it fails fast with
        // provider_busy so the caller can simply skip this cycle and retry later.
        if (skipWhenSearching && Backend.ForegroundSearchActive)
            throw new MessagingException("provider_busy",
                "A foreground WeChat search is using the UI; this read was skipped and will run on the next cycle.");
        lock (_gate)
        {
            using var dpi = new DpiAwarenessScope();
            try { return action(); }
            catch (WindowAutomationException ex) { throw new MessagingException(ex.Code,ex.Message); }
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _backend?.DisposeOcr(); _disposed = true; }
    }
}
