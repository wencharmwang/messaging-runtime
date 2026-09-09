using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Core.Tests;

public class OperationResultTests
{
    [Fact]
    public void Ok_CarriesValueAndConfidence()
    {
        var r = OperationResult<string>.Ok("hello", 0.9);
        Assert.True(r.Succeeded);
        Assert.Equal("hello", r.Value);
        Assert.Equal(0.9, r.Confidence);
    }

    [Fact]
    public void Fail_CarriesStructuredFailureCode()
    {
        var r = OperationResult<string>.Fail(FailureCode.AmbiguousConversation, "重名", "op1");
        Assert.False(r.Succeeded);
        Assert.Equal(FailureCode.AmbiguousConversation, r.Failure?.Code);
        Assert.Equal("op1", r.Failure?.OperationId);
    }

    [Fact]
    public void ActionState_TerminalStates()
    {
        Assert.True(ActionState.Succeeded.IsTerminal());
        Assert.True(ActionState.AmbiguousConversation.IsTerminal());
        Assert.False(ActionState.WriteDraft.IsTerminal());
    }
}
