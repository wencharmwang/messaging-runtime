using WeChatDriver.Automation;
using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Automation.Tests;

public class ConversationResolverTests
{
    [Fact]
    public void UniqueName_Resolves()
    {
        var backend = new FakeWindowAutomationBackend();
        backend.SeedConversation(new ConversationState(new ConversationKey("pos:0", "李女士", null), null, null, null, DateTimeOffset.Now, true, false));
        var resolver = new ConversationResolver(backend);

        var result = resolver.Resolve(new ConversationKey("", "李女士", null));

        Assert.True(result.Succeeded);
        Assert.Equal("pos:0", result.Value?.StableHint);
    }

    [Fact]
    public void DuplicateName_Ambiguous()
    {
        var backend = new FakeWindowAutomationBackend();
        backend.SeedConversation(new ConversationState(new ConversationKey("pos:0", "李女士", null), null, null, null, DateTimeOffset.Now, true, false));
        backend.SeedConversation(new ConversationState(new ConversationKey("pos:1", "李女士", null), null, null, null, DateTimeOffset.Now, true, false));
        var resolver = new ConversationResolver(backend);

        var result = resolver.Resolve(new ConversationKey("", "李女士", null));

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCode.AmbiguousConversation, result.Failure?.Code);
    }
}
