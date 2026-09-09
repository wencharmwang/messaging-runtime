using System.Text.Json.Serialization;

namespace WeChatDriver.Core;

/// <summary>Driver → Agent 事件载荷。仅经 127.0.0.1 + 本地随机令牌传递。</summary>
public sealed record DriverEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = "new_message";
    [JsonPropertyName("eventId")] public string EventId { get; init; } = "";
    [JsonPropertyName("conversation")] public ConversationDto? Conversation { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
    [JsonPropertyName("observedAt")] public DateTimeOffset ObservedAt { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "uia";
    [JsonPropertyName("confidence")] public double Confidence { get; init; }

    public static DriverEvent FromDelta(MessageDelta delta) => new()
    {
        EventId = delta.EventId,
        Conversation = new ConversationDto(delta.Conversation.StableHint, delta.Conversation.DisplayName),
        Text = delta.Text,
        ObservedAt = delta.ObservedAt,
        Source = delta.Source == MessageSource.Uia ? "uia" : "local_ocr",
        Confidence = delta.Confidence
    };
}

public sealed record ConversationDto(
    [property: JsonPropertyName("stableHint")] string StableHint,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>Agent → Driver 命令。</summary>
public sealed record AgentCommand
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("draftId")] public string DraftId { get; init; } = "";
    [JsonPropertyName("eventId")] public string EventId { get; init; } = "";
    [JsonPropertyName("conversation")] public ConversationDto? Conversation { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";
}
