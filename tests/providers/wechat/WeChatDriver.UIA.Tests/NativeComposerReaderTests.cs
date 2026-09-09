using System.Drawing;
using Xunit;

namespace WeChatDriver.UIA.Tests;

public class NativeComposerReaderTests
{
    [Fact]
    public void OnlyEditorInsideComposerIsEligible()
    {
        var composer = new Rectangle(375, 1211, 893, 104);
        Assert.True(NativeComposerReader.IsComposerBounds(composer, new Rectangle(380, 1215, 880, 95)));
        Assert.False(NativeComposerReader.IsComposerBounds(composer, new Rectangle(75, 20, 250, 35))); // search field
        Assert.False(NativeComposerReader.IsComposerBounds(composer, new Rectangle(375, 90, 893, 1100))); // message history
        Assert.False(NativeComposerReader.IsComposerBounds(composer, new Rectangle(0, 0, 1296, 1388))); // full window
        Assert.False(NativeComposerReader.IsComposerBounds(composer, new Rectangle(1200, 1250, 30, 20))); // icon
    }
}
