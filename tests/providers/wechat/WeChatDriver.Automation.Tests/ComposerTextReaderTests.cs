using System.Drawing;
using WeChatDriver.Automation;
using WeChatDriver.Capture;
using Xunit;

namespace WeChatDriver.Automation.Tests;

public sealed class ComposerTextReaderTests
{
    [Fact] public void DraftVerificationToleratesOnlyQuoteShapeAndExistingWhitespaceNormalization()
    {
        const string expected = "好呀，我带娃学\"雨\"\"云\"这两个字，再陪他画幅画，你放心吧～";
        Assert.True(ComposerTextReader.MatchesDraft(expected, "好呀，我带娃学“雨““云“这两个字，再陪他画幅画，你放心吧～"));
        Assert.True(ComposerTextReader.MatchesDraft(expected, "好呀，我带娃学\"雨”“云\"这两个字，再陪他画幅画，你放心吧～"));
        Assert.False(ComposerTextReader.MatchesDraft(expected, "好呀，我带娃学\"雨云\"这两个字，再陪他画幅画，你放心吧～"));
        Assert.False(ComposerTextReader.MatchesDraft(expected, expected.Replace("云", "雪")));
        Assert.False(ComposerTextReader.MatchesDraft("转账500元", "转账5000元"));
        Assert.False(ComposerTextReader.MatchesDraft("不要发送", "发送"));
        Assert.False(ComposerTextReader.MatchesDraft("学\"雨\"和\"云\"", "学\"雨和\"\"云\""));
        Assert.False(ComposerTextReader.MatchesDraft(expected, null));
    }

    private sealed class Engine(Func<byte[],IReadOnlyList<OcrLine>> recognize) : IOcrEngine
    {
        public bool IsAvailable=>true;
        public string Language=>"test";
        public IReadOnlyList<OcrLine> RecognizePng(byte[] png)=>recognize(png);
        public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] png)=>recognize(png).Select((line,index)=>new OcrTextRect(line.Text,32,32+index*24,60,18)).ToArray();
    }

    [Fact] public void UnreadableEnabledComposerIsNeverReportedEmpty()
    {
        using var bitmap=new Bitmap(100,40);
        var result=ComposerTextReader.Read(bitmap,new Engine(_=>[]),sendEnabled:true);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact] public void EmptyDisabledComposerCanBeUsedForNewDraft()
    {
        using var bitmap=new Bitmap(100,40);
        var result=ComposerTextReader.Read(bitmap,new Engine(_=>[]),sendEnabled:false);
        Assert.True(result.Succeeded);Assert.Null(result.Value);
    }

    [Fact] public void EdgeGlyphsGetPaddingWithoutResamplingOrLosingLines()
    {
        using var original=new Bitmap(100,40);
        using(var graphics=Graphics.FromImage(original)) graphics.Clear(Color.White);
        original.SetPixel(1,1,Color.Black);
        var engine=new Engine(png=>
        {
            using var image=new Bitmap(new MemoryStream(png));
            Assert.Equal(164,image.Width);Assert.Equal(104,image.Height);
            Assert.Equal(Color.Black.ToArgb(),image.GetPixel(33,33).ToArgb());
            Assert.Equal(Color.White.ToArgb(),image.GetPixel(1,1).ToArgb());
            return [new("first line",1),new("第二行",1)];
        });
        var result=ComposerTextReader.Read(original,engine,true);
        Assert.Equal("first line\n第二行",result.Value);
    }

    [Fact] public void SameLineCjkFragmentsDoNotInventWhitespaceAfterPunctuation()
    {
        var result=ComposerTextReader.JoinVisualLines([
            new("老公，",32,32,45,18),new("午饭吃了没呀？",85,33,130,18),
            new("今晚想吃什么？",220,32,130,18)]);
        Assert.Equal("老公，午饭吃了没呀？今晚想吃什么？",result);
    }

    [Fact] public void VisualRowsAndLatinWordBoundariesArePreserved()
    {
        var result=ComposerTextReader.JoinVisualLines([
            new("hello",32,32,45,18),new("world",85,32,45,18),new("第二行",32,60,60,18)]);
        Assert.Equal("hello world\n第二行",result);
    }
}
