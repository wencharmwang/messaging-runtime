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
        });
    }
    private void Locate(string name)
    {
        if (!Backend.SwitchConversation(name))
            throw new MessagingException("conversation_unverified", "The opened conversation title could not be verified.");
    }
    public IReadOnlyList<string> ReadMessages(string displayName, int limit)
    {
        return Run(() =>
        {
            Ready();
            Locate(displayName);
            return Backend.ReadChatHistory(out _, limit);
        });
    }
    public IReadOnlyList<string> ReadLatestMessages(string displayName, int limit) => Run(() =>
    {
        Ready();
        Locate(displayName);
        return Backend.ReadLatestMessages(limit);
    });
    public SendReceipt SendText(string displayName, string text)
    {
        return Run<SendReceipt>(() =>
        {
            var submissionStarted = false;
            try
            {
            Ready();
            if (!Backend.SelectConversationForDirectSend(displayName))
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
                    return new("sent", null, "send_button_disabled; UI submission verified, recipient delivery is not verified");
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

    private T Run<T>(Func<T> action)
    {
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
