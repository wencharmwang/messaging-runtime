using MessagingRuntime.Host;
using MessagingRuntime.Providers.WeChat;
using Xunit;

namespace MessagingRuntime.Providers.WeChat.Tests;

public sealed class WeChatProviderTests
{
    private sealed class Driver : IWeChatDriver
    {
        public IReadOnlyList<WeChatConversation> Conversations { get; set; } = [new("Alice", "pos:0"), new("王经理", "pos:1")];
        public string? LastTarget { get; private set; }
        public int Calls { get; private set; }
        public int HistoryReads { get; private set; }
        public bool Connect() { Calls++; return true; }
        public IReadOnlyList<WeChatConversation> ListConversations() { Calls++; return Conversations; }
        public IReadOnlyList<string> ReadMessages(string displayName, int limit)
        { HistoryReads++; LastTarget = displayName; return ["【对方】你好", "【我】收到", "没有方向", "【对方】[图片]"]; }
        public IReadOnlyList<string> ReadLatestMessages(string displayName, int limit)
        { LastTarget = displayName; return new[] {"【对方】相同内容", "【对方】相同内容"}.TakeLast(limit).ToArray(); }
        public SendReceipt SendText(string displayName, string text)
        { LastTarget = displayName; return new("sent", null, "test"); }
        public void Dispose() { }
    }

    [Fact]
    public void LiveObservationDoesNotReadHistoryOrCollapseRepeatedBubbles()
    {
        var driver = new Driver();
        using var provider = new WeChatProvider(driver);
        var page = provider.ObserveLatest(provider.ConversationId("Alice"), 30);
        Assert.Equal(0, driver.HistoryReads);
        Assert.Equal("Alice", driver.LastTarget);
        Assert.Equal(2, page.Messages.Count);
        Assert.All(page.Messages, m => Assert.Equal("相同内容", m.Content));
        Assert.False(page.IsComplete);
        Assert.Throws<MessagingException>(() => provider.ObserveLatest("unknown", 30));
    }

    [Fact]
    public void DiscoveryIsLazyAndCapabilitiesDoNotAdvertiseUnsupportedFeatures()
    {
        var driver = new Driver();
        using var provider = new WeChatProvider(driver);
        Assert.Equal("disconnected", provider.Info.Status);
        Assert.True(provider.Info.Capabilities.ReadMessages);
        Assert.False(provider.Info.Capabilities.UnreadCounts);
        Assert.False(provider.Info.Capabilities.Reply);
        Assert.False(provider.Info.Capabilities.SendFile);
        Assert.False(provider.Info.Capabilities.NativeMessageIds);
        Assert.Equal(0, driver.Calls);
    }

    [Fact]
    public void ConversationIdsSurviveReorderingAndAreScopedToProviderAccount()
    {
        var driver = new Driver();
        using var provider = new WeChatProvider(driver);
        var first = provider.ListConversations().Conversations.Single(c => c.Title == "王经理");
        driver.Conversations = [new("王经理", "pos:0"), new("Alice", "pos:1")];
        Assert.Equal(first.Id, provider.ListConversations().Conversations.Single(c => c.Title == "王经理").Id);
        Assert.NotEqual(first.Id, new WeChatProvider(driver, "second-account").ConversationId("王经理"));
        provider.GetMessages(first.Id, 50);
        Assert.Equal("王经理", driver.LastTarget);
    }

    [Fact]
    public void DuplicateNamesAreNeverUsedForReadOrSend()
    {
        var driver = new Driver { Conversations = [new("Alice", "pos:0"), new("Alice", "pos:1")] };
        using var provider = new WeChatProvider(driver);
        var id = provider.ListConversations().Conversations.Single().Id;
        Assert.Equal("ambiguous_conversation", Assert.Throws<MessagingException>(() => provider.GetMessages(id, 50)).Code);
        Assert.Equal("ambiguous_conversation", Assert.Throws<MessagingException>(() => provider.SendMessage(id, "text")).Code);
        Assert.Null(driver.LastTarget);
    }

    [Fact]
    public void ParserPreservesUnknownFieldsAndDoesNotInventSenderOrTimestamp()
    {
        var driver = new Driver();
        using var provider = new WeChatProvider(driver);
        var batch = provider.GetMessages(provider.ConversationId("Alice"), 50);
        Assert.False(batch.IsComplete);
        Assert.Equal(MessageDirection.Incoming, batch.Messages[0].Direction);
        Assert.Equal("你好", batch.Messages[0].Content);
        Assert.Equal(MessageDirection.Outgoing, batch.Messages[1].Direction);
        Assert.Equal(MessageDirection.Unknown, batch.Messages[2].Direction);
        Assert.Equal(MessageType.Image, batch.Messages[3].Type);
        Assert.All(batch.Messages, m => { Assert.Null(m.Timestamp); Assert.Null(m.Sender); Assert.Null(m.Raw); });
    }

    [Fact]
    public void ContactSearchIsUnicodeAwareAndReportsLimitedCoverage()
    {
        using var provider = new WeChatProvider(new Driver());
        Assert.Equal("Alice", provider.SearchContacts("ALICE").Contacts.Single().DisplayName);
        Assert.Equal("王经理", provider.SearchContacts("经理").Contacts.Single().DisplayName);
        Assert.False(provider.SearchContacts("经理").IsComplete);
        Assert.All(provider.ListConversations().Conversations, c => Assert.Null(c.UnreadCount));
    }

    [Fact]
    public void UnknownOrAmbiguousConfigurationFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"messaging-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"unexpected_option\":true}");
            Assert.Throws<System.Text.Json.JsonException>(() => RuntimeConfiguration.Load(path));
            File.WriteAllText(path, "{\"providers\":[{\"id\":\"a\",\"type\":\"wechat\"},{\"id\":\"b\",\"type\":\"wechat\"}]}");
            Assert.Throws<ArgumentException>(() => RuntimeConfiguration.Load(path));
            File.WriteAllText(path, "{\"agent_id\":\"agent\",\"policy\":{\"send_mode\":\"read_only\"},\"providers\":[]}");
            Assert.Equal(SendMode.ReadOnly, RuntimeConfiguration.Load(path).Policy.SendMode);
        }
        finally { File.Delete(path); }
    }
}
