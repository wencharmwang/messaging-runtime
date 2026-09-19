using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Core.Tests;

public class MessageFingerprintTests
{
    [Fact]
    public void SameInput_ProducesSameFingerprint()
    {
        var t = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.FromHours(8));
        var a = MessageFingerprint.Compute("conv", "in", "你好", 0, t);
        var b = MessageFingerprint.Compute("conv", "in", "你好", 0, t);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentText_ProducesDifferentFingerprint()
    {
        var t = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.FromHours(8));
        var a = MessageFingerprint.Compute("conv", "in", "你好", 0, t);
        var b = MessageFingerprint.Compute("conv", "in", "再见", 0, t);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        Assert.Equal("a b", MessageFingerprint.Normalize("  a   b  "));
    }

    /// <summary>
    /// 列表行的时间列是「时钟在走」而不是「来了新消息」：微信会把同一条消息的显示时间从
    /// 刚刚改写成 1分钟前、1小时前。它每改写一次，原来的活动指纹就翻一次，上层于是每几分钟
    /// 打开一次会话去读 —— 用户看到的就是没消息也一直在动。
    /// </summary>
    [Theory]
    [InlineData("刚刚", "1分钟前")]
    [InlineData("刚刚", "59分钟前")]
    [InlineData("3分钟前", "1小时前")]
    [InlineData("1 分钟前", "12分钟前")]
    public void ActivityTimeKey_IgnoresRelativeClockDrift(string before, string after)
    {
        Assert.Equal(MessageFingerprint.ActivityTimeKey(before), MessageFingerprint.ActivityTimeKey(after));
    }

    /// <summary>
    /// 反过来，指纹必须保留真的会前进的那部分：今天的消息从「刚刚」走到具体时刻、跨天变成
    /// 「昨天…」，都是新消息才会造成的差异，不能被一并抹平，否则就会漏掉真实来信。
    /// </summary>
    [Theory]
    [InlineData("刚刚", "11:29")]
    [InlineData("11:29", "11:35")]
    [InlineData("11:29", "昨天21:39")]
    [InlineData("昨天21:39", "星期三")]
    public void ActivityTimeKey_KeepsGenuineForwardMoves(string before, string after)
    {
        Assert.NotEqual(MessageFingerprint.ActivityTimeKey(before), MessageFingerprint.ActivityTimeKey(after));
    }

    [Fact]
    public void ActivityTimeKey_EmptyStaysEmpty()
    {
        Assert.Equal("", MessageFingerprint.ActivityTimeKey(null));
        Assert.Equal("", MessageFingerprint.ActivityTimeKey("   "));
    }
}
