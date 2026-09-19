using WeChatDriver.Automation;
using Xunit;

namespace WeChatDriver.Automation.Tests;

/// <summary>
/// 列表行的名字和聊天页头不是同一次 OCR 读出来的，字符级别对不上。
///
/// 实测（2026-09-17 真实失败）：「王丛喜一果爸爸星期」这一条会话，三次点击都确实打开了它，
/// 但页头读回来的是「王丛喜一果爸爸」——行名把右侧时间列的开头粘了进来，页头没有。原来的
/// 比对要求页头**包含**行名，于是永远不成立，读取被判成 conversation_unverified，
/// 用户看到的是"这条读不到"。这里钉住容忍规则，以及它不该变成什么都放行。
/// </summary>
public sealed class HeaderNameMatchTests
{
    [Fact]
    public void RowTimeTailOnTheHeaderSideStillCounts()
    {
        // 行名多出时间列残留；页头是干净的名字。
        Assert.True(ConversationListLocator.HeaderMatches("王丛喜一果爸爸", "王丛喜一果爸爸星期"));
        Assert.True(ConversationListLocator.HeaderMatches("王丛喜一果爸爸", "王丛喜一果爸爸昨天"));
        Assert.True(ConversationListLocator.HeaderMatches("王丛喜一果爸爸", "王丛喜一果爸爸 17:40"));
    }

    [Fact]
    public void OneCharacterOcrDifferenceStillCounts()
    {
        // 原有行为保留：多/少一个字符（实测 小璐璐 / 小璐路璐）。
        Assert.True(ConversationListLocator.HeaderMatches("小璐璐", "小璐路璐"));
        Assert.True(ConversationListLocator.HeaderMatches("周二轮滑1班（16:40-17:40）", "周二轮滑1班（16:4017:40）·Q"));
    }

    [Fact]
    public void AHeaderThatContainsTheWantedNameStillCounts()
    {
        // 原有行为保留：页头带群成员数（新疆羊肉串( 14 / 新疆羊肉串）。
        Assert.True(ConversationListLocator.HeaderMatches("新疆羊肉串( 14", "新疆羊肉串"));
    }

    [Fact]
    public void DifferentConversationsDoNotMatch()
    {
        // 容忍规则不能变成什么都放行 —— 点错行就会读到别人的聊天。
        Assert.False(ConversationListLocator.HeaderMatches("弘阳17栋网格群", "新疆羊肉串"));
        Assert.False(ConversationListLocator.HeaderMatches("炸抱抱一果", "树袋熊"));
        Assert.False(ConversationListLocator.HeaderMatches("", "树袋熊"));
        Assert.False(ConversationListLocator.HeaderMatches("(页头没有读到任何文字)", "树袋熊"));
    }

    [Fact]
    public void ReadOnlyNoisyRowMatchToleratesDuplicatedGlyphsAndASuffix()
    {
        Assert.True(ConversationListLocator.NoisyRowCouldMatchHeader("小璐璐", "小璐路璐路ONR"));
        Assert.False(ConversationListLocator.NoisyRowCouldMatchHeader("小璐", "小璐路璐路ONR"));
        Assert.False(ConversationListLocator.NoisyRowCouldMatchHeader("弘阳17栋网格群", "小璐路璐路ONR"));
    }

    [Fact]
    public void PreviewMatchIgnoresDirectionButRejectsShortCommonReplies()
    {
        Assert.True(ConversationListLocator.PreviewCouldMatchMessage("小璐：周六下午一起轮滑", "【对方】周六下午一起轮滑"));
        Assert.True(ConversationListLocator.PreviewCouldMatchMessage("本周活动安排如下…", "【我】本周活动安排如下请查收"));
        Assert.False(ConversationListLocator.PreviewCouldMatchMessage("好的", "【对方】好的"));
        Assert.False(ConversationListLocator.PreviewCouldMatchMessage("周六下午一起轮滑", "【对方】明天上午开会"));
    }

    [Fact]
    public void HeaderFormKeepsANameThatHasNothingToTrim()
    {
        Assert.Equal("树袋熊", ConversationListLocator.HeaderForm("树袋熊"));
        Assert.Equal("Bros", ConversationListLocator.HeaderForm("Bros"));
        // 只去尾部噪声，名字中间的时间不动 —— 那可能是名字的一部分（班级时间）。
        Assert.Equal("周二轮滑1班（16:4017:40）·Q", ConversationListLocator.HeaderForm("周二轮滑1班（16:4017:40）·Q"));
    }
}
