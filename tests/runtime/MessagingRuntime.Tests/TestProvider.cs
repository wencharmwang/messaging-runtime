using MessagingRuntime.Providers;

namespace MessagingRuntime.Tests;

internal sealed class TestProvider(string id = "test-main", string type = "test") : MessagingProvider
{
    public override ProviderInfo Info => new(id, type, "test-account", "Test", "connected",
        new(ListConversations: true, ReadMessages: true, SearchContacts: true, SendText: true));
    public int ReadCalls { get; private set; }
    public int SendCalls { get; private set; }
    public bool FailRead { get; set; }
    public bool FailSend { get; set; }
    public bool FailBeforeSubmit { get; set; }
    public string[] Lines { get; set; } = ["你好", "same", "same", "مرحبا", "last"];
    public override ConversationList ListConversations() => new([
        new("a", id, ConversationType.Direct, "Alice", UnreadCount: 2),
        new("b", id, ConversationType.Group, "Unknown unread", UnreadCount: null)
    ], false, "test-window");
    public override ContactList SearchContacts(string query) => new([new("a", id, "Alice")], false, "test");
    public override MessageBatch GetMessages(string conversationId, int limit)
    {
        ReadCalls++;
        if (FailRead) throw new MessagingException("read_failed", "Simulated failure");
        return new(Lines.Take(limit).Select(text => new Message("", id, conversationId, null,
            MessageDirection.Incoming, MessageType.Text, text)).ToArray(), false, "test-window");
    }
    public override SendReceipt SendMessage(string conversationId, string text)
    {
        SendCalls++;
        if (FailBeforeSubmit) throw new SendNotAttemptedException("conversation_unverified");
        if (FailSend) throw new IOException("SENSITIVE exception text must not appear in audit");
        return new("sent", "native-1", "test-provider-ack");
    }
}
