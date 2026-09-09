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
    public virtual MessageBatch GetMessages(string conversationId, int limit) => throw Unsupported("read_messages");
    public virtual MessageBatch ObserveLatest(string conversationId, int limit) => throw Unsupported("observe_latest");
    public virtual SendReceipt SendMessage(string conversationId, string text) => throw Unsupported("send_text");
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
