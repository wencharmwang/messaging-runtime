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
}
