using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessagingRuntime;

public static class RuntimeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options)))).ToLowerInvariant();
}

public enum ConversationType { Unknown, Direct, Group, Channel }
public enum MessageDirection { Unknown, Incoming, Outgoing, System }
public enum MessageType { Unknown, Text, Image, File, Audio, Video, Link, Location, System }

public sealed record ProviderCapabilities(
    bool ListConversations = false, bool ReadMessages = false, bool SearchContacts = false,
    bool UnreadCounts = false, bool SendText = false, bool Reply = false,
    bool SendFile = false, bool NativeMessageIds = false, bool MessageTimestamps = false);

public sealed record ProviderInfo(string Id, string Type, string? AccountId, string DisplayName,
    string Status, ProviderCapabilities Capabilities);

public sealed record Contact(string Id, string Provider, string DisplayName,
    string? Nickname = null, string? Remark = null, string? Avatar = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record Attachment(string Id, string Type, string? Name = null,
    string? MimeType = null, string? LocalPath = null);

public sealed record Message(string Id, string Provider, string ConversationId,
    Contact? Sender, MessageDirection Direction, MessageType Type, string Content,
    DateTimeOffset? Timestamp = null, string? ReplyTo = null,
    IReadOnlyList<Attachment>? Attachments = null, string Status = "observed",
    string? Raw = null, DateTimeOffset? ObservedAt = null);

public sealed record Conversation(string Id, string Provider, ConversationType Type, string Title,
    IReadOnlyList<Contact>? Participants = null, int? UnreadCount = null,
    Message? LastMessage = null, DateTimeOffset? LastMessageAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ConversationList(IReadOnlyList<Conversation> Conversations,
    bool IsComplete, string Coverage);
public sealed record ContactList(IReadOnlyList<Contact> Contacts, bool IsComplete, string Coverage);
public sealed record UnreadConversations(IReadOnlyList<Conversation> Conversations,
    IReadOnlyList<string> UnknownConversationIds, bool IsComplete, string Coverage);

// The source reports its actual coverage. OCR observation time is never a message timestamp.
public sealed record MessageBatch(IReadOnlyList<Message> Messages, bool IsComplete, string Coverage);
public sealed record MessagePage(IReadOnlyList<Message> Messages, string SnapshotId,
    string? Before, string? After, bool HasMoreBefore, bool HasMoreAfter,
    bool IsComplete, string Coverage, DateTimeOffset SyncedAt);
public sealed record SendReceipt(string Status, string? MessageId, string Verification);
public sealed record SendRequest(string RequestId, string AgentId, string Provider,
    string ConversationId, string Text, string? ReplyTo = null, string? ConversationTitle = null);
public sealed record ApprovalInfo(string Channel, string State, string NextAction);
public sealed record SendOutcome(string RequestId, string Status, SendReceipt? Receipt = null,
    ApprovalInfo? Approval = null);

// Implemented by the local host, never by an MCP caller.
public interface IApprovalNotifier
{
    ApprovalInfo Notify(SendRequest request);
    ApprovalInfo Describe(string requestId);
}
public sealed record AuditEntry(DateTimeOffset At, string AgentId, string Operation,
    string? Provider, string? ConversationId, string Outcome, string? RequestId = null);

public sealed class MessagingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Providers may throw this ONLY when no message submission action has started.
// All other failures remain uncertain, including failures during the submit call.
public sealed class SendNotAttemptedException(string code) : Exception("Message submission was not attempted.")
{
    public string Code { get; } = code;
}
