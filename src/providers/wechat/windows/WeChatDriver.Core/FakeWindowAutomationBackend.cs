namespace WeChatDriver.Core;

/// <summary>假后端：用于无微信环境下的状态机/流水线测试。</summary>
public sealed class FakeWindowAutomationBackend : IWindowAutomationBackend
{
    private readonly List<ConversationState> _conversations = new();
    private string? _composerText;
    private string? _lastSent;
    private readonly bool _supportsValuePattern;

    public FakeWindowAutomationBackend(bool supportsValuePattern = true)
    {
        _supportsValuePattern = supportsValuePattern;
    }

    public void SeedConversation(ConversationState state) => _conversations.Add(state);

    public OperationResult<WeChatWindowSession> EstablishSession() =>
        OperationResult<WeChatWindowSession>.Ok(
            new WeChatWindowSession(1234, 0x1234, "4.1.13.12", "fake", SessionHealth.Healthy, DateTimeOffset.Now));

    public OperationResult<CapabilityMatrix> ProbeCapabilities() =>
        OperationResult<CapabilityMatrix>.Ok(new CapabilityMatrix(
            "4.1.13.12",
            "fake",
            new List<CapabilityEntry>
            {
                new("conversation_list", true),
                new("chat_title", true),
                new("composer", true),
                new("send_control", _supportsValuePattern)
            }));

    public OperationResult<IReadOnlyList<ConversationState>> EnumerateConversations() =>
        OperationResult<IReadOnlyList<ConversationState>>.Ok(_conversations.AsReadOnly());

    public OperationResult<string?> ReadLatestMessageText(ConversationKey conversation) =>
        OperationResult<string?>.Ok(_conversations.FirstOrDefault(c => c.Key.StableHint == conversation.StableHint)?.LastPreviewHash);

    public OperationResult WriteComposer(ConversationKey conversation, string text)
    {
        if (!_supportsValuePattern)
            return OperationResult.Fail(FailureCode.ComposeUnsupported, "输入框不支持 ValuePattern 一次性写入");
        _composerText = text;
        return OperationResult.Ok();
    }

    public OperationResult<string?> ReadComposerText() => OperationResult<string?>.Ok(_composerText);

    public OperationResult SendMessage(ConversationKey conversation)
    {
        _lastSent = _composerText;
        _composerText = null;
        return OperationResult.Ok();
    }

    public OperationResult<bool> VerifySent(ConversationKey conversation, string expectedFingerprint) =>
        OperationResult<bool>.Ok(_lastSent is not null && _composerText is null);
}
