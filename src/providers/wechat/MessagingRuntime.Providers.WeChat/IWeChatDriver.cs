namespace MessagingRuntime.Providers.WeChat;

public sealed record WeChatConversation(string DisplayName, string LocatorHint, int? UnreadCount = null,
    string? PreviewHash = null, string? ActivityFingerprint = null);

/// <summary>Small, injectable boundary around the existing Windows driver.</summary>
public interface IWeChatDriver : IDisposable
{
    bool Connect();
    IReadOnlyDictionary<string,object?> GetDiagnostics() => new Dictionary<string,object?>();
    IReadOnlyList<WeChatConversation> ListConversations();
    IReadOnlyList<string> ReadMessages(string displayName, int limit);
    IReadOnlyList<string> ReadLatestMessages(string displayName, int limit) => throw new MessagingException("unsupported_capability", "Latest observation is unavailable.");
    SendReceipt SendText(string displayName, string text);
}
