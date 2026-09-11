namespace MessagingRuntime.Providers.WeChat;

public sealed class WeChatProvider(IWeChatDriver driver, string id = "wechat-main", string? accountId = null,
    string displayName = "WeChat") : MessagingProvider
{
    private string _status = "disconnected";
    public override string SnapshotRevision => "wechat-window-bound-v2";
    public override object GetDiagnostics()
    {
        var details=driver.GetDiagnostics();
        _status="connected";
        return new { provider=Info, details };
    }
    public override ProviderInfo Info => new(id, "wechat", accountId, displayName, _status,
        new(ListConversations: true, ReadMessages: true, SearchContacts: true, SendText: true));

    public override ProviderInfo Connect()
    {
        _status = driver.Connect() ? "connected" : "unavailable";
        return Info;
    }

    public override ConversationList ListConversations()
    {
        var rows = driver.ListConversations();
        _status = "connected";
        var conversations = rows.Where(r => !string.IsNullOrWhiteSpace(r.DisplayName))
            .GroupBy(r => r.DisplayName, StringComparer.Ordinal).Select(group =>
            {
                var row = group.First();
                return new Conversation(ConversationId(row.DisplayName), id, ConversationType.Unknown, row.DisplayName,
                    UnreadCount: group.Count() == 1 ? row.UnreadCount : null,
                    Metadata: new Dictionary<string, string>
                    {
                        ["identity_source"] = "display_name_hash",
                        ["ambiguous"] = (group.Count() > 1).ToString().ToLowerInvariant(),
                        ["activity_fingerprint"] = row.ActivityFingerprint ?? "",
                        ["preview_hash"] = row.PreviewHash ?? ""
                    });
            }).ToArray();
        return new(conversations, false, "visible_wechat_conversations_with_activity_fingerprints; native account/contact IDs and exact unread counts are unavailable");
    }

    public string ConversationId(string name) => "wechat:" + RuntimeJson.Hash(new { provider = id, accountId, name });

    /// <summary>见 <see cref="MessagingProvider.AllowsTargetOutsideList"/>。</summary>
    public override bool AllowsTargetOutsideList(string conversationId) =>
        !conversationId.StartsWith("wechat:", StringComparison.Ordinal);

    /// <summary>见 <see cref="MessagingProvider.RestoreWindow"/>。</summary>
    public override void RestoreWindow() => driver.RestoreWindow();

    public override ContactList SearchContacts(string query)
    {
        var list = ListConversations();
        return new(list.Conversations.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Contact(c.Id, id, c.Title, Metadata: c.Metadata)).ToArray(), false,
            "visible_conversation_names only. A user-commanded send may pass the exact display name "
            + "as conversation_id instead: the driver then locates it through WeChat's global search.");
    }

    /// <summary>解析结果：显示名 + 是否允许用微信全局搜索打开。</summary>
    private sealed record Resolution(string Name, bool AllowGlobalSearch);

    /// <summary>
    /// 解析目标会话。
    /// `wechat:*` 是可见列表派生的身份标识，必须命中可见列表（自动回复走这条，保证精确匹配）。
    /// 其余值按「用户点名的显示名」处理 —— 用户主动下令发送时目标可能不在可见列表里，
    /// 此时交给驱动用微信全局搜索定位，只有搜不到才报 contact_not_found 让用户核对名称。
    /// </summary>
    private Resolution Resolve(string conversationId)
    {
        if (!conversationId.StartsWith("wechat:", StringComparison.Ordinal))
        {
            var named = conversationId.Trim();
            if (named.Length == 0)
                throw new MessagingException("conversation_not_found", "Empty conversation name.");
            return new Resolution(named, AllowGlobalSearch: true);
        }
        var list = ListConversations();
        var match = list.Conversations.SingleOrDefault(c => c.Id == conversationId)
            ?? throw new MessagingException("conversation_not_found", "Conversation must be visible in list_conversations. Refresh the list after a rename.");
        if (match.Metadata?["ambiguous"] == "true")
            throw new MessagingException("ambiguous_conversation", "Multiple visible conversations share this name. Give the recipient a unique remark before continuing.");
        return new Resolution(match.Title, AllowGlobalSearch: false);
    }

    public override MessageBatch GetMessages(string conversationId, int limit)
    {
        var target = Resolve(conversationId);
        var lines = driver.ReadMessages(target.Name, limit, target.AllowGlobalSearch);
        return new(lines.Select(line => WeChatParser.Parse(id, conversationId, line)).ToArray(), false,
            "bounded_ocr_snapshot; synthetic observation IDs; unknown timestamps/senders; OCR may omit or merge messages");
    }

    public override MessageBatch ObserveLatest(string conversationId, int limit)
    {
        var target = Resolve(conversationId);
        var lines = driver.ReadLatestMessages(target.Name, limit, target.AllowGlobalSearch);
        return new(lines.Select(line => WeChatParser.Parse(id, conversationId, line)).ToArray(), false,
            "latest_visible_ocr_snapshot; no history scroll; top clipped bubble omitted; observation IDs are not native IDs");
    }
    public override SendReceipt SendMessage(string conversationId, string text)
    {
        var target = Resolve(conversationId);
        return driver.SendText(target.Name, text, target.AllowGlobalSearch);
    }
    public override void Dispose() => driver.Dispose();
}
