namespace WeChatDriver.Core;

/// <summary>驱动与微信窗口之间的会话健康状态。</summary>
public enum SessionHealth
{
    Unknown,
    Healthy,
    Degraded,
    NeedsForeground,
    Blocked,
    Disposed
}

/// <summary>消息正文来源。</summary>
public enum MessageSource
{
    Uia,
    LocalOcr
}

/// <summary>草稿生命周期状态。</summary>
public enum DraftStatus
{
    Proposed,
    Edited,
    Approved,
    Written,
    Sent,
    Failed
}

/// <summary>微信窗口会话。锁定单个进程、单个主窗口。</summary>
public sealed record WeChatWindowSession(
    int ProcessId,
    nint WindowHandle,
    string ClientVersion,
    string LocatorProfileId,
    SessionHealth Health,
    DateTimeOffset LastVerifiedAt);

/// <summary>会话定位键。禁止仅依赖昵称。</summary>
public sealed record ConversationKey(
    string StableHint,
    string DisplayName,
    string? AvatarFingerprint);

/// <summary>会话可观测状态。</summary>
public sealed record ConversationState(
    ConversationKey Key,
    int? UnreadCount,
    string? LastPreviewHash,
    string? LastMessageFingerprint,
    DateTimeOffset LastObservedAt,
    bool IsVisible,
    bool IsCurrent);

/// <summary>一条增量消息（已去重）。</summary>
public sealed record MessageDelta(
    string EventId,
    ConversationKey Conversation,
    string Text,
    DateTimeOffset ObservedAt,
    MessageSource Source,
    double Confidence,
    string Fingerprint);

/// <summary>由上层 Agent 提出的草稿回复。</summary>
public sealed record DraftReply(
    string DraftId,
    ConversationKey Conversation,
    string Text,
    string OriginEventId,
    DraftStatus Status);

/// <summary>微信主窗口健康诊断结果（供 MCP 诊断工具与自愈逻辑使用）。</summary>
public sealed record WindowHealthReport(
    bool ProcessRunning,
    bool WindowFound,
    bool IsMinimized,
    bool IsOffscreen,
    int Width,
    int Height,
    int X,
    int Y,
    int Dpi,
    string Status,
    string? Detail,
    IReadOnlyList<string> Issues);
