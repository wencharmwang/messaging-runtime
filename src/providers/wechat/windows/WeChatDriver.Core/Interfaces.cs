using System.Collections.ObjectModel;

namespace WeChatDriver.Core;

/// <summary>窗口自动化后端抽象。允许替换为直接 UIA 或诊断适配器。</summary>
public interface IWindowAutomationBackend
{
    OperationResult<WeChatWindowSession> EstablishSession();
    OperationResult<CapabilityMatrix> ProbeCapabilities();
    OperationResult<string?> ReadLatestMessageText(ConversationKey conversation);
    OperationResult<IReadOnlyList<ConversationState>> EnumerateConversations();
    OperationResult WriteComposer(ConversationKey conversation, string text);
    OperationResult<string?> ReadComposerText();
    OperationResult SendMessage(ConversationKey conversation);
    OperationResult<bool> VerifySent(ConversationKey conversation, string expectedFingerprint);
}

/// <summary>能力矩阵条目。</summary>
public sealed record CapabilityEntry(string Name, bool Available, string? Detail = null);

/// <summary>UIA 能力矩阵。</summary>
public sealed record CapabilityMatrix(
    string ClientVersion,
    string LocatorProfileId,
    IReadOnlyList<CapabilityEntry> Entries)
{
    public bool Has(string name) => Entries.Any(e => e.Name == name && e.Available);
}

/// <summary>会话索引：只处理变化会话，绝不周期性重读历史。</summary>
public interface IConversationIndex
{
    /// <summary>对比会话列表快照，返回有变化的会话。</summary>
    IReadOnlyList<ConversationState> Diff(IReadOnlyList<ConversationState> current);
    void Apply(IReadOnlyList<ConversationState> current);
    bool SeenFingerprint(string fingerprint);
    void MarkFingerprint(string fingerprint);
}

/// <summary>串行动作执行器（唯一允许控制微信的组件）。</summary>
public interface IActionExecutor
{
    OperationResult Execute(ActionRequest request);
    void Cancel(string operationId);
    ActionState CurrentState { get; }
}

/// <summary>一次动作请求。</summary>
public sealed record ActionRequest(
    string OperationId,
    ConversationKey Conversation,
    string DraftText,
    string OriginEventId,
    bool AllowSend);

/// <summary>Agent 桥：仅限本机的本地事件/命令接口。</summary>
public interface IAgentBridge
{
    void Start(string token);
    void Stop();
    event EventHandler<MessageDelta>? NewMessage;
    event EventHandler<DraftReply>? DraftProposed;
}

/// <summary>本地面板 UI 抽象。</summary>
public interface IApprovalUi
{
    void ShowDraft(MessageDelta delta, DraftReply draft);
    event EventHandler<ApprovalDecision>? Decision;
}

/// <summary>用户在本地面板的决策。</summary>
public sealed record ApprovalDecision(string DraftId, ApprovalAction Action, string? EditedText = null);

public enum ApprovalAction
{
    Copy,
    WriteDraft,
    ApproveAndSend,
    Handoff,
    Ignore
}

/// <summary>本地最小状态存储。</summary>
public interface ILocalStore
{
    void Initialize();
    void SaveConversationState(ConversationState state);
    void SaveMetric(string operationId, string key, double? value);
    void SaveFailure(FailureInfo failure);
    IReadOnlyList<FailureInfo> RecentFailures(int limit = 50);
    void ClearAll();
}
