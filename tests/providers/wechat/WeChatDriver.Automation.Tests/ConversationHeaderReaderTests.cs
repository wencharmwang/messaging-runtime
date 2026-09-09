using System.Drawing;
using WeChatDriver.Automation;
using WeChatDriver.Capture;
using WeChatDriver.Windows;
using Xunit;

namespace WeChatDriver.Automation.Tests;

public sealed class ConversationHeaderReaderTests
{
    private sealed class Engine : IOcrEngine
    {
        public bool IsAvailable { get; set; }=true;
        public string Language => "test";
        public string Text="Wencharm";
        public bool Fail;
        public int Calls;
        public Func<byte[], string>? Recognizer;
        public IReadOnlyList<OcrLine> RecognizePng(byte[] png)
        {
            Calls++;
            if(Fail) throw new WindowAutomationException("ocr_failed","fixture failure");
            return [new(Recognizer?.Invoke(png) ?? Text,1)];
        }
        public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] png)=>throw new NotSupportedException();
    }

    [Fact] public void ChangedHeaderPixelsCannotReusePreviouslyVerifiedRecipient()
    {
        var engine=new Engine();var reader=new ConversationHeaderReader(engine,()=>null);
        using var frame=new Bitmap(120,40);
        Assert.Contains("Wencharm",reader.Read(frame));
        var count=engine.Calls;
        Assert.Contains("Wencharm",reader.Read(frame));
        Assert.Equal(count,engine.Calls);
        frame.SetPixel(12,12,Color.White);engine.Text="Another recipient";
        var changed=reader.Read(frame);
        Assert.Contains("Another recipient",changed);
        Assert.DoesNotContain("Wencharm",changed);
        Assert.True(engine.Calls>count);
    }

    [Fact] public void SecondaryLocalOcrCanSupplyExactNameWithoutFuzzySubstitution()
    {
        var primary=new Engine { Text="Wencham" };
        var fallback=new Engine { Text="Wencharm" };
        using var frame=new Bitmap(120,40);
        var reader=new ConversationHeaderReader(primary,()=>fallback);
        Assert.Contains("Wencharm",reader.Read(frame));
        var withoutFallback=new ConversationHeaderReader(primary,()=>new Engine { IsAvailable=false });
        Assert.DoesNotContain("Wencharm",withoutFallback.Read(frame));
    }

    [Fact] public void FailedObservationIsNotCachedAsAnEmptyChatHeader()
    {
        var engine=new Engine { Fail=true };
        var reader=new ConversationHeaderReader(engine,()=>null);
        using var frame=new Bitmap(120,40);
        Assert.Equal("ocr_failed",Assert.Throws<WindowAutomationException>(()=>reader.Read(frame)).Code);
        engine.Fail=false;
        Assert.Contains("Wencharm",reader.Read(frame));
    }

    [Fact] public void ContrastFallbackResolvesAntialiasedTitle_AndDoesNotFuzzyMatchOtherNames()
    {
        using var header = new Bitmap(120,40);
        using(var g = Graphics.FromImage(header)) g.Clear(Color.FromArgb(248,248,248));
        header.SetPixel(12,12,Color.FromArgb(90,90,90));
        header.SetPixel(13,12,Color.FromArgb(150,150,150));
        using var contrast = ConversationHeaderReader.CreateHighContrastVariant(header);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(WindowCapture.ToPng(contrast)));
        var fallback = new Engine { Recognizer = png => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)) == expected ? "Wencharm" : "Wencham" };
        var reader = new ConversationHeaderReader(new Engine { Text="Wenham" },()=>fallback);
        Assert.Contains("Wencharm",reader.Read(header));
        fallback.Recognizer = _ => "Wencham";
        header.SetPixel(20,20,Color.Black);
        Assert.DoesNotContain("Wencharm",reader.Read(header));
    }
}
