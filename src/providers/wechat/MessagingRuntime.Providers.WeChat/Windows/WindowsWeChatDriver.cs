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
        return Run(() => Backend.EstablishSession().Succeeded || Backend.Session is not null);
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
    private void Locate(string name, bool allowGlobalSearch = false)
    {
        var opened = allowGlobalSearch
            ? Backend.SelectConversationByGlobalSearch(name)
            : Backend.SwitchConversation(name);
        if (!opened)
            throw new MessagingException("conversation_unavailable", "The conversation could not be opened for reading.");
    }

    /// <summary>
    /// Reading a WeChat conversation means opening it, which moves whatever the user is doing.
    /// Background monitoring therefore yields while WeChat is in the foreground — unless the
    /// conversation we want is already the open one, in which case nothing moves.
    /// A user-commanded operation (allowGlobalSearch) never yields: they asked for it.
    /// Called after Ready(), so an unusable window is still reported as provider_unavailable
    /// instead of being mistaken for "the user is busy in WeChat".
    /// </summary>
    private void YieldToUserIfBusy(string displayName, bool allowGlobalSearch, bool alreadyOpen)
    {
        if (allowGlobalSearch) return;
        if (alreadyOpen) return;                  // reading in place moves nothing
        if (!Backend.UserIsInWeChat()) return;
        throw new MessagingException("wechat_in_use", "The user is working in WeChat; this read waits until they stop.");
    }
    /// <summary>
    /// Reading a conversation means opening it, so a background read moves whatever the user had
    /// open. Put their chat back afterwards: they left WeChat on their own conversation, and
    /// coming back to a different one is a surprise they cannot undo. Only background reads do
    /// this — a user-commanded open is meant to stay where it went.
    /// </summary>
    private void RestoreChatAfterRead(string? displaced, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displaced)) return;
        if (SameChatTitle(displaced, displayName)) return;
        try { Backend.SwitchConversation(displaced!); }
        catch (Exception) { /* best effort: the next read re-establishes whatever it needs */ }
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

    public IReadOnlyList<string> ReadMessages(string displayName, int limit, bool allowGlobalSearch = false)
    {
        return Run(() =>
        {
            if (allowGlobalSearch) Backend.RestoreWindowSession();
            Ready();
            var displaced = DisplacedChat(allowGlobalSearch);
            var alreadyOpen = HeaderIsExactly(displaced, displayName);
            YieldToUserIfBusy(displayName, allowGlobalSearch, alreadyOpen);
            try
            {
                // Already the open chat: no row click, so nothing moves at all.
                if (!alreadyOpen) Locate(displayName, allowGlobalSearch);
                return Backend.ReadChatHistory(out _, limit);
            }
            finally { RestoreChatAfterRead(displaced, displayName); }
        });
    }
    public IReadOnlyList<string> ReadLatestMessages(string displayName, int limit, bool allowGlobalSearch = false) => Run(() =>
    {
        if (allowGlobalSearch) Backend.RestoreWindowSession();
        Ready();
        var displaced = DisplacedChat(allowGlobalSearch);
        var alreadyOpen = HeaderIsExactly(displaced, displayName);
        YieldToUserIfBusy(displayName, allowGlobalSearch, alreadyOpen);
        try
        {
            // Already the open chat: no row click, so nothing moves at all.
            if (!alreadyOpen) Locate(displayName, allowGlobalSearch);
            return Backend.ReadLatestMessages(limit);
        }
        finally { RestoreChatAfterRead(displaced, displayName); }
    });
    public SendReceipt SendText(string displayName, string text, bool allowGlobalSearch = false)
    {
        return Run<SendReceipt>(() =>
        {
            var submissionStarted = false;
            // Sending must bring WeChat forward and open the chat (there is no other way to type
            // into it), so remember what the user had open and put it back once the message is
            // confirmed. Left on the uncertain path on purpose: seeing that chat is how the user
            // checks what actually happened.
            var displaced = allowGlobalSearch ? null : DisplacedChat(false);
            try
            {
            if (allowGlobalSearch) Backend.RestoreWindowSession();
            Ready();
            var selected = allowGlobalSearch
                ? Backend.SelectConversationByGlobalSearch(displayName)
                : Backend.SelectConversationForDirectSend(displayName);
            if (!selected)
                throw new MessagingException("conversation_not_visible", "Could not select the requested conversation row.");
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
