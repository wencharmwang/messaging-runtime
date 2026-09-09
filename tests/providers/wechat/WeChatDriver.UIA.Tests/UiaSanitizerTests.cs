using WeChatDriver.UIA;
using Xunit;

namespace WeChatDriver.UIA.Tests;

public class UiaSanitizerTests
{
    [Fact]
    public void LongName_IsMasked()
    {
        var masked = UiaSanitizer.MaskName("李女士: 10月3日还有档期吗？");
        Assert.StartsWith("#", masked);
        Assert.DoesNotContain("档期", masked);
    }

    [Fact]
    public void ShortName_Kept()
    {
        Assert.Equal("发送", UiaSanitizer.MaskName("发送"));
    }

    [Fact]
    public void FixedTitle_Kept()
    {
        Assert.Equal("微信", UiaSanitizer.MaskTitle("微信"));
    }

    [Fact]
    public void NonFixedTitle_Masked()
    {
        var masked = UiaSanitizer.MaskTitle("李女士 - 微信");
        Assert.StartsWith("#", masked);
    }
}
