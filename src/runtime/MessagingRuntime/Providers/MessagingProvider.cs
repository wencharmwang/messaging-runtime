namespace MessagingRuntime.Providers;

/// <summary>Only messaging semantics cross this boundary. Platform UI/API details stay inside providers.</summary>
public abstract class MessagingProvider : IDisposable
{
    public abstract ProviderInfo Info { get; }
    public virtual ProviderInfo Connect() => Info;
    public virtual object GetDiagnostics() => new { provider=Info, details=new { } };
    public virtual string SnapshotRevision => "1";
    public virtual ConversationList ListConversations() => throw Unsupported("list_conversations");
    public virtual ContactList SearchContacts(string query) => throw Unsupported("search_contacts");
    /// <summary>
    /// Read a conversation.
    /// <paramref name="background"/> says who asked: true only for the background automatic-reply
    /// agent. A read moves the platform client (opening a chat changes what the person is looking
    /// at), so a background agent yields while the person is working in that client, while a read
    /// the person asked for — a button in the UI, or an interactive assistant answering on their
    /// behalf — must not be refused on their own activity.
    /// </summary>
    public virtual MessageBatch GetMessages(string conversationId, int limit, bool background = false) => throw Unsupported("read_messages");
    /// <inheritdoc cref="GetMessages"/>
    public virtual MessageBatch ObserveLatest(string conversationId, int limit, bool background = false) => throw Unsupported("observe_latest");
    public virtual SendReceipt SendMessage(string conversationId, string text) => throw Unsupported("send_text");

    /// <summary>
    /// True when a send may target a conversation that is not in the visible list because the
    /// provider can resolve the recipient itself — e.g. a user-named WeChat contact located
    /// through WeChat's own global search. Automatic paths keep passing opaque list ids, so
    /// the strict visible-list identity check still applies to them.
    /// </summary>
    public virtual bool AllowsTargetOutsideList(string conversationId) => false;

    /// <summary>
    /// Bring the platform client window back from minimized or hidden without changing the open
    /// conversation. Reads and scans never do this on their own: a client the user deliberately
    /// put away must stay away. Callers that opt in (an explicit user toggle such as the
    /// Message Agent "自动拉回微信窗口" setting) ask for it explicitly.
    /// </summary>
    public virtual void RestoreWindow() => throw Unsupported("restore_window");

    /// <summary>
    /// Scroll the conversation list back to the top.
    ///
    /// WeChat moves a conversation that just received a message to the first row but does not
    /// bring the viewport with it. A list that has been scrolled down therefore hides exactly the
    /// rows a poller needs: the conversation reads as "not in the visible list" while sitting right
    /// there above the viewport — which is why a person who opens that chat once by hand finds the
    /// assistant working again.
    ///
    /// Opt-in for the same reason <see cref="RestoreWindow"/> is: a scan must never move what the
    /// person is looking at on its own, or it fights them every polling cycle. The caller decides
    /// when it is worth it, and how often.
    /// </summary>
    public virtual void ScrollConversationListToTop() => throw Unsupported("scroll_conversation_list_to_top");
    public virtual SendReceipt ReplyMessage(string conversationId, string messageId, string text) => throw Unsupported("reply");
    public virtual SendReceipt SendFile(string conversationId, string path) => throw Unsupported("send_file");
    protected MessagingException Unsupported(string operation) => new("unsupported_capability", $"Provider '{Info.Id}' does not support {operation}.");
    public virtual void Dispose() { }
}

public sealed class ProviderManager : IDisposable
{
    private readonly Dictionary<string, MessagingProvider> _providers = new(StringComparer.Ordinal);
    public void Register(MessagingProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Info.Id);
        if (!_providers.TryAdd(provider.Info.Id, provider))
            throw new MessagingException("duplicate_provider", "Provider IDs must be unique per account.");
    }
    public IReadOnlyList<ProviderInfo> List() => _providers.Values.Select(p => p.Info).ToArray();
    public MessagingProvider Get(string id) => _providers.TryGetValue(id, out var provider) ? provider
        : throw new MessagingException("provider_not_found", "Select a configured provider ID from list_providers.");
    public void Dispose() { foreach (var provider in _providers.Values) provider.Dispose(); }
}

/// <summary>Explicit extension point, never advertises simulated platform functionality.</summary>
public sealed class UnconfiguredProvider(string id, string type) : MessagingProvider
{
    public override ProviderInfo Info => new(id, type, null, type, "not_configured", new());
    public override ProviderInfo Connect() => throw new MessagingException("provider_not_configured",
        $"'{type}' requires a provider implementation and account configuration; this release includes the WeChat adapter.");
}
