using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Core.Tests;

public class InMemoryConversationIndexTests
{
    private static ConversationState Conv(string hint, string fp) =>
        new(new ConversationKey(hint, $"name-{hint}", null), null, null, fp, DateTimeOffset.Now, true, false);

    [Fact]
    public void FirstSeen_ReportsChanged()
    {
        var index = new InMemoryConversationIndex();
        var changed = index.Diff(new[] { Conv("a", "fp1") });
        Assert.Single(changed);
    }

    [Fact]
    public void UnchangedFingerprint_ReportsNothing()
    {
        var index = new InMemoryConversationIndex();
        index.Apply(new[] { Conv("a", "fp1") });
        var changed = index.Diff(new[] { Conv("a", "fp1") });
        Assert.Empty(changed);
    }

    [Fact]
    public void ChangedFingerprint_ReportsConversation()
    {
        var index = new InMemoryConversationIndex();
        index.Apply(new[] { Conv("a", "fp1") });
        var changed = index.Diff(new[] { Conv("a", "fp2") });
        Assert.Single(changed);
        Assert.Equal("fp2", changed[0].LastMessageFingerprint);
    }

    [Fact]
    public void FingerprintDedup_MarksSeen()
    {
        var index = new InMemoryConversationIndex();
        Assert.False(index.SeenFingerprint("x"));
        index.MarkFingerprint("x");
        Assert.True(index.SeenFingerprint("x"));
    }
}
