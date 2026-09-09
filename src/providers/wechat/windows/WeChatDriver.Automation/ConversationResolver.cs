using WeChatDriver.Core;

namespace WeChatDriver.Automation;

/// <summary>目标会话定位。禁止凭模糊昵称、坐标或模型猜测选择会话。</summary>
public sealed class ConversationResolver
{
    private readonly IWindowAutomationBackend _backend;

    public ConversationResolver(IWindowAutomationBackend backend) => _backend = backend;

    /// <summary>按规范 6.1 优先级定位会话。</summary>
    public OperationResult<ConversationKey> Resolve(ConversationKey requested)
    {
        var enumerate = _backend.EnumerateConversations();
        if (!enumerate.Succeeded || enumerate.Value is null)
            return OperationResult<ConversationKey>.FromFailure(enumerate.Failure!);

        // 优先：按显示名精确匹配（名称唯一）。
        var byName = enumerate.Value
            .Where(c => string.Equals(c.Key.DisplayName, requested.DisplayName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byName.Count == 1)
            return OperationResult<ConversationKey>.Ok(byName[0].Key, 0.9);

        // 其次：按 stableHint 匹配。
        var byHint = enumerate.Value
            .Where(c => string.Equals(c.Key.StableHint, requested.StableHint, StringComparison.Ordinal))
            .ToList();

        if (byHint.Count == 1)
            return OperationResult<ConversationKey>.Ok(byHint[0].Key, 0.95);

        if (byName.Count > 1 || byHint.Count > 1)
            return OperationResult<ConversationKey>.Fail(FailureCode.AmbiguousConversation,
                $"会话重名无法唯一定位: {requested.DisplayName}");

        return OperationResult<ConversationKey>.Fail(FailureCode.ConversationNotFound,
            $"未找到会话: {requested.DisplayName}");
    }
}
