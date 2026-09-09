namespace WeChatDriver.Core;

/// <summary>结构化失败码。所有失败路径必须产生失败码，禁止仅抛异常。</summary>
public enum FailureCode
{
    None = 0,

    // 会话/窗口
    WeChatNotRunning = 1001,
    MultipleCandidateWindows = 1002,
    WindowNotFound = 1003,
    SessionDisposed = 1004,
    NonInteractiveSession = 1005,

    // 版本/布局
    UnknownClientVersion = 1101,
    LocatorProfileMismatch = 1102,
    LayoutChanged = 1103,
    DpiChanged = 1104,

    // UIA 能力
    UiaTreeEmpty = 1201,
    ConversationListMissing = 1202,
    ChatTitleMissing = 1203,
    ComposerMissing = 1204,
    SendControlMissing = 1205,
    UiaCallbackNotArrived = 1206,

    // 消息读取
    MessageTextUnavailable = 1301,
    OcrBelowThreshold = 1302,
    OcrDirectionUnknown = 1303,
    NeedsReview = 1304,

    // 会话定位
    AmbiguousConversation = 1401,
    ConversationNotFound = 1402,

    // 写入
    ComposeUnsupported = 1501,
    WriteFailed = 1502,
    ClipboardFailed = 1503,
    DraftHashMismatch = 1504,

    // 发送
    SendControlUnknown = 1601,
    SendUnverified = 1602,
    SendFailed = 1603,

    // 校验
    Timeout = 1701,
    TitleMismatch = 1702,
    ForegroundLost = 1703,
    NeedsForeground = 1704,

    // 通用
    InternalError = 1999
}

/// <summary>失败码附带的上下文信息。</summary>
public sealed record FailureInfo(
    FailureCode Code,
    string Message,
    DateTimeOffset OccurredAt,
    string? OperationId = null,
    double? Confidence = null);
