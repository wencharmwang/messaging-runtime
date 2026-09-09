using MessagingRuntime.Providers;
using MessagingRuntime.Storage;

namespace MessagingRuntime;

public sealed class MessagingService(ProviderManager providers, MessageStore store, RuntimePolicy policy, string agentId,
    IApprovalNotifier? approvalNotifier = null, Func<SendRequest, string>? sendAuthorization = null, object? operationGate = null)
{
    private readonly object _gate = operationGate ?? new();
    public IReadOnlyList<ProviderInfo> ListProviders() => Run("list_providers", null, null, providers.List);
    public object GetDiagnostics(string provider) => Run("diagnose_provider",provider,null,() => providers.Get(provider).GetDiagnostics());
    public ProviderInfo Connect(string provider) => Run("connect_provider", provider, null, () => providers.Get(provider).Connect());
    public ProviderInfo GetProvider(string provider) => Run("get_provider", provider, null, () => providers.Get(provider).Info);

    public ConversationList ListConversations(string provider) => Run("list_conversations", provider, null, () =>
    {
        var p = providers.Get(provider);
        Require(p.Info.Capabilities.ListConversations, "list_conversations");
        return p.ListConversations();
    });

    public UnreadConversations GetUnreadConversations(string provider) => Run("get_unread_conversations", provider, null, () =>
    {
        var p = providers.Get(provider);
        Require(p.Info.Capabilities.ListConversations, "list_conversations");
        var list = p.ListConversations();
        var unknown = list.Conversations.Where(c => c.UnreadCount is null).Select(c => c.Id).ToArray();
        return new UnreadConversations(list.Conversations.Where(c => c.UnreadCount > 0).ToArray(), unknown,
            list.IsComplete && unknown.Length == 0 && p.Info.Capabilities.UnreadCounts, list.Coverage);
    });

    public ContactList SearchContacts(string provider, string query) => Run("search_contacts", provider, null, () =>
    {
        Text(query, "query");
        var p = providers.Get(provider);
        Require(p.Info.Capabilities.SearchContacts, "search_contacts");
        return p.SearchContacts(query);
    });

    public MessagePage GetMessages(string provider, string conversationId, int limit = 50,
        string? before = null, string? after = null, bool refresh = false) => Run("get_messages", provider, conversationId, () =>
    {
        ValidatePage(limit, before, after, refresh);
        Text(conversationId, "conversation_id");
        var p = providers.Get(provider);
        Require(p.Info.Capabilities.ReadMessages, "read_messages");
        store.EnsureProviderRevision(provider,p.SnapshotRevision);
        if (refresh || (before is null && after is null && !store.HasSnapshot(provider, conversationId)))
            store.SaveSnapshot(provider, conversationId, p.GetMessages(conversationId, 500));
        return store.GetPage(provider, conversationId, limit, before, after);
    });

    public MessagePage SyncConversation(string provider, string conversationId, int limit = 500) => Run("sync_conversation", provider, conversationId, () =>
    {
        ValidatePage(limit, null, null, false);
        Text(conversationId, "conversation_id");
        var p = providers.Get(provider);
        Require(p.Info.Capabilities.ReadMessages, "read_messages");
        store.EnsureProviderRevision(provider,p.SnapshotRevision);
        store.SaveSnapshot(provider, conversationId, p.GetMessages(conversationId, limit));
        return store.GetPage(provider, conversationId, Math.Min(50, limit));
    });

    public MessageBatch ObserveLatest(string provider, string conversationId, int limit = 50) => Run("observe_latest", provider, conversationId, () =>
    {
        ValidatePage(limit, null, null, false);
        Text(conversationId, "conversation_id");
        return providers.Get(provider).ObserveLatest(conversationId, limit);
    });

    public IReadOnlyList<Message> SearchMessages(string provider, string query, string? conversationId = null, int limit = 50)
        => Run("search_messages", provider, conversationId, () =>
    {
        var source=providers.Get(provider);
        store.EnsureProviderRevision(provider,source.SnapshotRevision);
        Text(query, "query");
        ValidatePage(limit, null, null, false);
        return store.Search(provider, query, conversationId, limit);
    });

    public SendOutcome SendMessage(string provider, string conversationId, string text, string requestId, string? replyTo = null,
        SendOrigin origin = SendOrigin.AgentProposed)
        => Run(replyTo is null ? "send_message" : "reply_message", provider, conversationId, () =>
    {
        Text(conversationId, "conversation_id");
        if (!Guid.TryParseExact(requestId, "D", out var guid))
            throw new MessagingException("invalid_arguments", "request_id must be a UUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).");
        var p = providers.Get(provider);
        Require(replyTo is null ? p.Info.Capabilities.SendText : p.Info.Capabilities.Reply, replyTo is null ? "send_text" : "reply");
        var request = new SendRequest(guid.ToString("D"), agentId, provider, conversationId, text, replyTo);
        var status = sendAuthorization is null ? policy.Evaluate(request, origin) : sendAuthorization(request);
        var existing = store.FindSendRequest(request.RequestId);
        if (existing is not null) request = request with { ConversationTitle = existing.ConversationTitle };
        else if (p.Info.Capabilities.ListConversations)
        {
            var candidates = p.ListConversations().Conversations.Where(c => c.Id == conversationId).ToArray();
            if (candidates.Length == 0)
                throw new MessagingException("conversation_not_found", "Resolve the recipient with list_conversations before requesting a send.");
            if (candidates.Length > 1 || (candidates[0].Metadata?.TryGetValue("ambiguous", out var ambiguous) == true && ambiguous == "true"))
                throw new MessagingException("ambiguous_conversation", "Recipient identity is ambiguous; no send request was created.");
            request = request with { ConversationTitle = candidates[0].Title };
        }
        var outcome = store.RegisterSend(request, status);
        store.Audit(new(DateTimeOffset.UtcNow, agentId, "send_origin", provider, conversationId,
            origin == SendOrigin.UserRequest ? "user_request" : "agent_proposed", request.RequestId));
        if (policy.SendMode == SendMode.UserDirected && origin == SendOrigin.UserRequest && outcome.Status == "pending")
        {
            store.AuthorizeUserRequest(request.RequestId);
            outcome = store.RegisterSend(request, status);
        }
        if (outcome.Status == "pending")
        {
            ApprovalInfo info;
            try { info = approvalNotifier?.Notify(request) ?? ManualApproval(); }
            catch { info = ManualApproval("unavailable"); }
            return outcome with { Status = "approval_required", Approval = info };
        }
        if (outcome.Status != "approved") return outcome;
        if (!store.ClaimSend(request.RequestId))
            throw new MessagingException("request_in_progress", "The request has already been claimed. Do not create a new request to retry it.");
        // Persist the attempt before entering the provider. A crash leaves 'sending', which is never retried automatically.
        store.Audit(new(DateTimeOffset.UtcNow, agentId, "send_attempt", provider, conversationId, "sending", request.RequestId));
        try
        {
            var receipt = replyTo is null ? p.SendMessage(conversationId, text) : p.ReplyMessage(conversationId, replyTo, text);
            var finalStatus = receipt.Status == "sent" ? "sent" : "uncertain";
            store.FinishSend(request.RequestId, finalStatus, receipt);
            return new SendOutcome(request.RequestId, finalStatus, receipt);
        }
        catch (SendNotAttemptedException ex)
        {
            var receipt = new SendReceipt("not_sent", null, $"not_submitted:{ex.Code}");
            store.FinishSend(request.RequestId, "not_sent", receipt);
            return new SendOutcome(request.RequestId, "not_sent", receipt);
        }
        catch
        {
            store.FinishSend(request.RequestId, "uncertain");
            throw new MessagingException("send_uncertain", "Sending could not be verified. Inspect the conversation before any further send; this request will not be retried.");
        }
    }, requestId);

    public SendOutcome GetSendStatus(string requestId) => Run("get_send_status", null, null, () =>
    {
        if (!Guid.TryParseExact(requestId, "D", out var id))
            throw new MessagingException("invalid_arguments", "request_id must be a UUID.");
        var review = store.ReviewSend(id.ToString("D"));
        if (review.Request.AgentId != agentId)
            throw new MessagingException("request_not_found", "Unknown send request.");
        var outcome = store.RegisterSend(review.Request, review.Status);
        return outcome.Status == "pending"
            ? outcome with { Status = "approval_required", Approval = approvalNotifier?.Describe(outcome.RequestId) ?? ManualApproval() }
            : outcome;
    }, requestId);

    public static ApprovalInfo ManualApproval(string state = "manual_required") => new("local_cli", state,
        "No WorkBuddy popup is connected. A local operator must use --review-request then --approve-request or --reject-request with the same runtime config/data directory. Stop send retries; use get_send_status. After approval, retry the identical request_id and payload.");

    public SendOutcome SendFile(string provider, string conversationId, string path, string requestId)
        => Run<SendOutcome>("send_file", provider, conversationId, () =>
    {
        providers.Get(provider);
        throw new MessagingException("unsupported_capability", "File sending is not enabled in this release; text-file CLI sending in the legacy driver is not attachment sending.");
    }, requestId);

    private T Run<T>(string operation, string? provider, string? conversation, Func<T> action, string? requestId = null)
    {
        lock (_gate)
        {
            try
            {
                var result = action();
                store.Audit(new(DateTimeOffset.UtcNow, agentId, operation, provider, conversation,
                    result is SendOutcome send ? send.Status : "ok", requestId));
                return result;
            }
            catch (MessagingException ex)
            {
                store.Audit(new(DateTimeOffset.UtcNow, agentId, operation, provider, conversation, ex.Code, requestId));
                throw;
            }
            catch
            {
                store.Audit(new(DateTimeOffset.UtcNow, agentId, operation, provider, conversation, "provider_error", requestId));
                throw new MessagingException("provider_error", "Operation failed. Check provider status and local configuration.");
            }
        }
    }
    private static void Require(bool supported, string operation)
    { if (!supported) throw new MessagingException("unsupported_capability", $"Provider does not support {operation}."); }
    private static void Text(string value, string name)
    { if (string.IsNullOrWhiteSpace(value)) throw new MessagingException("invalid_arguments", $"{name} is required."); }
    private static void ValidatePage(int limit, string? before, string? after, bool refresh)
    {
        if (limit is < 1 or > 500 || (before is not null && after is not null) || (refresh && (before is not null || after is not null)))
            throw new MessagingException("invalid_arguments", "limit must be 1–500. Use one cursor; refresh cannot be combined with a cursor.");
    }
}
