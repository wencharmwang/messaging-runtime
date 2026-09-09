using WeChatDriver.Automation;
using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Automation.Tests;

public class ActionExecutorTests
{
    private static ActionExecutor Executor(FakeWindowAutomationBackend backend) =>
        new(backend, new InMemoryStore());

    private static ConversationKey Key => new("pos:0", "李女士", null);

    [Fact]
    public void WriteDraft_WithValuePattern_Succeeds()
    {
        var backend = new FakeWindowAutomationBackend(supportsValuePattern: true);
        backend.SeedConversation(new ConversationState(Key, null, null, null, DateTimeOffset.Now, true, false));
        var executor = Executor(backend);

        var result = executor.Execute(new ActionRequest("op1", Key, "您好", "msg1", AllowSend: false));

        Assert.True(result.Succeeded);
        Assert.Equal(ActionState.AwaitUserApproval, executor.CurrentState);
    }

    [Fact]
    public void WriteDraft_AmbiguousConversation_Fails()
    {
        var backend = new FakeWindowAutomationBackend();
        // 同名两个会话 → 定位歧义。
        var k1 = new ConversationState(new ConversationKey("pos:0", "李女士", null), null, null, null, DateTimeOffset.Now, true, false);
        var k2 = new ConversationState(new ConversationKey("pos:1", "李女士", null), null, null, null, DateTimeOffset.Now, true, false);
        backend.SeedConversation(k1);
        backend.SeedConversation(k2);
        var executor = Executor(backend);

        // 请求带空 stableHint，仅凭昵称无法唯一定位 → 歧义。
        var result = executor.Execute(new ActionRequest("op2", new ConversationKey("", "李女士", null), "您好", "msg2", AllowSend: false));

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCode.AmbiguousConversation, result.Failure?.Code);
        Assert.Equal(ActionState.AmbiguousConversation, executor.CurrentState);
    }

    [Fact]
    public void Send_WithoutValuePattern_FailsComposeUnsupported()
    {
        var backend = new FakeWindowAutomationBackend(supportsValuePattern: false);
        backend.SeedConversation(new ConversationState(Key, null, null, null, DateTimeOffset.Now, true, false));
        var executor = Executor(backend);

        var result = executor.Execute(new ActionRequest("op3", Key, "您好", "msg3", AllowSend: false));

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCode.ComposeUnsupported, result.Failure?.Code);
    }
}
