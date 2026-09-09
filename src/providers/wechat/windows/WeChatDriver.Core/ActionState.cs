namespace WeChatDriver.Core;

/// <summary>
/// 串行动作执行状态机。终态包括 Succeeded 与若干失败态，
/// 任何失败都不允许自动重试发送。
/// </summary>
public enum ActionState
{
    Idle,
    ValidateSession,
    ResolveConversation,
    VerifyConversationTitle,
    FocusComposer,
    WriteDraft,
    VerifyDraft,
    AwaitUserApproval,
    Send,
    VerifySend,
    Succeeded,
    Failed,
    NeedsForeground,
    AmbiguousConversation,
    ComposeUnsupported,
    SendUnverified
}

public static class ActionStateExtensions
{
    public static bool IsTerminal(this ActionState state) => state switch
    {
        ActionState.Succeeded => true,
        ActionState.Failed => true,
        ActionState.NeedsForeground => true,
        ActionState.AmbiguousConversation => true,
        ActionState.ComposeUnsupported => true,
        ActionState.SendUnverified => true,
        _ => false
    };

    public static bool IsFailure(this ActionState state) => state switch
    {
        ActionState.Failed => true,
        ActionState.NeedsForeground => true,
        ActionState.AmbiguousConversation => true,
        ActionState.ComposeUnsupported => true,
        ActionState.SendUnverified => true,
        _ => false
    };
}
