namespace MessagingRuntime.Providers.WeChat;

public sealed record WeChatConversation(string DisplayName, string LocatorHint, int? UnreadCount = null,
    string? PreviewHash = null, string? ActivityFingerprint = null);

/// <summary>Small, injectable boundary around the existing Windows driver.</summary>
public interface IWeChatDriver : IDisposable
{
    bool Connect();
    /// <summary>True while a user-commanded send is driving WeChat's own search UI.</summary>
    bool BusyWithForegroundSearch => false;
    IReadOnlyDictionary<string,object?> GetDiagnostics() => new Dictionary<string,object?>();
    IReadOnlyList<WeChatConversation> ListConversations();
    /// <summary>Bring the WeChat main window back from minimized/hidden. Leaves the open conversation alone.</summary>
    void RestoreWindow() => throw new MessagingException("unsupported_capability", "Window restore is unavailable.");
    /// <summary>Scroll the conversation list back to the top. WeChat moves a conversation that just
    /// received a message to the first row without bringing the viewport with it, so a list that was
    /// scrolled down hides rows a poller needs. Opt-in, never part of a read.</summary>
    void ScrollConversationListToTop() => throw new MessagingException("unsupported_capability", "Conversation-list scrolling is unavailable.");
    /// <summary><paramref name="allowGlobalSearch"/> opens the conversation through WeChat's
    /// global search when it is not in the visible list (user-commanded sends only).
    /// <paramref name="background"/> marks a read started by the background automatic-reply
    /// agent, which yields while the person is working in WeChat; every read a person asked for
    /// passes false and therefore never yields to their own activity.</summary>
    IReadOnlyList<string> ReadMessages(string displayName, int limit, bool allowGlobalSearch = false, bool background = false);
    IReadOnlyList<string> ReadLatestMessages(string displayName, int limit, bool allowGlobalSearch = false, bool background = false) => throw new MessagingException("unsupported_capability", "Latest observation is unavailable.");
    SendReceipt SendText(string displayName, string text, bool allowGlobalSearch = false);
}
