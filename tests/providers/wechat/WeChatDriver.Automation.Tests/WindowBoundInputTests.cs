using System.Drawing;
using WeChatDriver.Automation;
using WeChatDriver.Capture;
using WeChatDriver.Windows;
using Xunit;

namespace WeChatDriver.Automation.Tests;

public sealed class WindowBoundInputTests
{
    private sealed class Native : IWindowInputNative
    {
        public WindowInputState State = new(42,true,false,1100,960,false);
        public int Clicks, Wheels, Keys, Activations;
        public bool PointOwned=true;
        public Action? AfterWheel;
        public Action? AfterCharacter;
        public string Characters = "";
        public WindowInputState Inspect(nint hwnd)=>State;
        public bool Activate(nint hwnd){Activations++;return State.Foreground;}
        public bool OwnsPoint(nint hwnd,int x,int y)=>PointOwned;
        public void Click(nint hwnd,int x,int y,bool background){Clicks++;Assert.Equal((nint)100,hwnd);Assert.True(background||State.Foreground);}
        public void Wheel(nint hwnd,int x,int y,int delta,bool background){Wheels++;AfterWheel?.Invoke();}
        public void Chord(nint hwnd,ushort[] keys)=>Keys++;
        public void Character(nint hwnd,int x,int y,char value) { Assert.Equal((nint)100,hwnd); Characters+=value; AfterCharacter?.Invoke(); }
    }

    [Fact] public void BackgroundReadTargetsWindowWithoutActivationOrForeground()
    {
        var native=new Native { PointOwned=false };
        var input=new TargetWindowInput(100,42,native);
        input.ExpectGeometry(1100,960);
        input.Click(200,150);
        input.Wheel(700,400,-120,3);
        Assert.Equal(1,native.Clicks);Assert.Equal(3,native.Wheels);
        Assert.Equal(0,native.Activations);Assert.Equal(0,native.Keys);
    }

    [Fact] public void ForegroundLossStopsTheRemainingWheelEvents()
    {
        var native=new Native { State=new(42,true,false,1100,960,true) };
        native.AfterWheel=()=>native.State=native.State with { Foreground=false };
        var input=new TargetWindowInput(100,42,native);
        var error=Assert.Throws<WindowAutomationException>(()=>input.Wheel(700,400,120,10,background:false));
        Assert.Equal("foreground_lost",error.Code);Assert.Equal(1,native.Wheels);
    }

    [Fact] public void ReusedHandleDoesNotReceiveInput()
    {
        var native=new Native { State=new(99,true,false,1100,960,true) };
        var input=new TargetWindowInput(100,42,native);
        Assert.Equal("window_changed",Assert.Throws<WindowAutomationException>(()=>input.Click(100,100)).Code);
        Assert.Equal(0,native.Clicks);
    }

    [Fact] public void ResizeInvalidatesObservedCoordinates()
    {
        var native=new Native();var input=new TargetWindowInput(100,42,native);
        input.ExpectGeometry(1100,960);native.State=native.State with { Width=1300 };
        Assert.Equal("layout_changed",Assert.Throws<WindowAutomationException>(()=>input.Click(100,100)).Code);
        Assert.Equal(0,native.Clicks);
        input.BeginObservation();input.ExpectGeometry(1300,960);input.Click(100,100);
        Assert.Equal(1,native.Clicks);
    }

    [Fact] public void DpiChangeInvalidatesInputEvenIfWindowSizeIsUnchanged()
    {
        var native=new Native();var input=new TargetWindowInput(100,42,native);
        input.ExpectGeometry(1100,960,120);native.State=native.State with { Dpi=144 };
        Assert.Equal("layout_changed",Assert.Throws<WindowAutomationException>(()=>input.Click(100,100)).Code);
        Assert.Equal(0,native.Clicks);
    }

    [Fact] public void FailedActivationCannotSendKeyboardInput()
    {
        // The guarded activation ladder was refused outright: WeChat never came forward, so no
        // real input was sent. This used to assert `foreground_lost`, which is the *other*
        // failure — activation accepted but the window still not foreground. (That one is
        // pinned by ForegroundLossStopsTheRemainingWheelEvents, which goes through
        // RequireForeground directly; ActivateForWriting calls the real Win32 ladder and so
        // cannot be driven by the stub.)
        var native=new Native();var input=new TargetWindowInput(100,42,native);
        Assert.Equal("foreground_unavailable",Assert.Throws<WindowAutomationException>(()=>input.ActivateForWriting()).Code);
        Assert.Throws<WindowAutomationException>(()=>input.Chord(0x11,0x46));
        Assert.Equal(0,native.Keys);
    }

    [Fact] public void OccludingWindowBlocksGlobalMouseInput()
    {
        var native=new Native { State=new(42,true,false,1100,960,true),PointOwned=false };
        var input=new TargetWindowInput(100,42,native);
        Assert.Equal("window_occluded",Assert.Throws<WindowAutomationException>(()=>input.Click(100,100,background:false)).Code);
        Assert.Equal(0,native.Clicks);
    }

    [Fact] public void MinimizedWindowDoesNotGetRestoredOrClicked()
    {
        var native=new Native { State=new(42,true,true,1100,960,false) };
        var input=new TargetWindowInput(100,42,native);
        Assert.Equal("window_unavailable",Assert.Throws<WindowAutomationException>(()=>input.Click(100,100)).Code);
        Assert.Equal(0,native.Activations);Assert.Equal(0,native.Clicks);
    }

    [Fact] public void BackgroundTextDoesNotActivateOrUseGlobalKeyboard()
    {
        var native=new Native { PointOwned=false };
        var input=new TargetWindowInput(100,42,native);
        var verifications=0;
        input.WriteText(500,800,"今晚回家吃饭",()=>verifications++);
        Assert.Equal("今晚回家吃饭",native.Characters);
        Assert.Equal(0,native.Activations);
        Assert.Equal(0,native.Keys);
        Assert.True(verifications>=6);
        Assert.False(native.State.Foreground);
    }

    [Fact] public void BackgroundTextStopsWhenBoundWindowChanges()
    {
        var native=new Native();
        native.AfterCharacter=()=>native.State=native.State with {ProcessId=99};
        var input=new TargetWindowInput(100,42,native);
        Assert.Equal("window_changed",Assert.Throws<WindowAutomationException>(()=>input.WriteText(500,800,"hello",()=>{})).Code);
        Assert.Equal("h",native.Characters);
    }

    [Theory]
    [InlineData("hello\nworld")]
    [InlineData("hello\rworld")]
    [InlineData("hello\tworld")]
    public void BackgroundTextNeverTurnsControlCharactersIntoSubmitEvents(string text)
    {
        var native=new Native();var input=new TargetWindowInput(100,42,native);
        Assert.Equal("background_text_unsupported",Assert.Throws<WindowAutomationException>(()=>input.WriteText(500,800,text,()=>{})).Code);
        Assert.Empty(native.Characters);Assert.Equal(0,native.Keys);
    }
}

public sealed class ConversationListLocatorTests
{
    [Fact] public void FileHelperAndLongNamesKeepTheirObservedRows()
    {
        var rects=new[]
        {
            new OcrTextRect("文件传输助手",60,10,150,25),
            new OcrTextRect("[文件] demo.md",60,40,150,20),
            new OcrTextRect("一个超过十二个字但确实有效的群聊名称",60,95,180,25),
            new OcrTextRect("短预览",60,125,80,20),
            new OcrTextRect("昨天20:45",190,95,80,20)
        };
        var rows=ConversationListLocator.Parse(rects,120);
        Assert.Equal(2,rows.Count);
        Assert.Equal("文件传输助手",rows[0].Name);Assert.Equal(22,rows[0].Y);
        Assert.Contains("超过十二",rows[1].Name);
        Assert.Null(ConversationListLocator.Unique(rows,"不存在"));
    }

    [Fact] public void DuplicateVisibleNamesAreNotResolvedByFirstResult()
    {
        var rows=new[] { new ConversationRow("同名",100,100),new ConversationRow("同名",100,200) };
        Assert.Equal("ambiguous_conversation",Assert.Throws<WindowAutomationException>(()=>ConversationListLocator.Unique(rows,"同名")).Code);
    }

    [Theory]
    [InlineData(120,40)]
    [InlineData(180,60)]
    public void PreviewGroupingScalesWithDpi(int dpi,int offset)
    {
        var rows=ConversationListLocator.Parse(new[] {
            new OcrTextRect("联系人",10,10,100,20),
            new OcrTextRect("短预览",10,10+offset,100,20),
            new OcrTextRect("第二人",10,10+offset*3,100,20)},dpi);
        Assert.Equal(2,rows.Count);
    }

    [Fact] public void UnsupportedTrailingEmojiDoesNotBecomeASearchEllipsis()
    {
        Assert.Equal("树袋熊",ConversationListLocator.CleanName("树袋熊。"));
        Assert.Equal("树袋熊",ConversationListLocator.CleanName("树袋熊?"));
        Assert.Equal("树袋熊",ConversationListLocator.CleanName("树袋熊.."));
        Assert.Equal("这是一个确实非常长的会话名…",ConversationListLocator.CleanName("这是一个确实非常长的会话名.."));
    }
}

public sealed class WeChatLayoutTests
{
    [Theory]
    [InlineData(30, 46, 65)]
    [InlineData(250, 237, 216)]
    public void ReadyLayoutAllowsUniformEmptyComposer(int background, int sidebar, int border)
    {
        using var frame = new Bitmap(1100, 960);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(background, background, background));
            using var brush = new SolidBrush(Color.FromArgb(sidebar, sidebar, sidebar));
            g.FillRectangle(brush, 0, 0, 500, 960);
            using var pen = new Pen(Color.FromArgb(border, border, border));
            g.DrawLine(pen, 500, 100, 1099, 100);
            g.DrawLine(pen, 510, 790, 1090, 790);
        }
        var layout = WeChatLayout.Detect(frame, 120);
        Assert.True(layout.HasOpenConversation);
        // This is a ready editor even though every pixel has exactly one colour.
        for (var y = layout.Composer.Top; y < layout.Composer.Bottom; y++)
        for (var x = layout.Composer.Left; x < layout.Composer.Right; x++)
            Assert.Equal(background, frame.GetPixel(x, y).R);
    }

    [Theory]
    [InlineData(1100,960,350,120)]
    [InlineData(1450,1000,430,120)]
    [InlineData(1650,1440,525,180)]
    public void PaneDetectionKeepsSidebarAndComposerOutOfMessages(int width,int height,int split,int dpi)
    {
        int Px(int v)=>(int)Math.Round(v*dpi/120.0);
        using var frame=new Bitmap(width,height);
        using(var g=Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(250,250,250));
            using var sidebar=new SolidBrush(Color.FromArgb(237,237,237));
            g.FillRectangle(sidebar,0,0,split,height);
            using var line=new Pen(Color.FromArgb(216,216,216));
            g.DrawLine(line,split,Px(100),width-1,Px(100));
            g.DrawLine(line,split+Px(10),height-Px(190),width-Px(10),height-Px(190));
        }
        var layout=WeChatLayout.Detect(frame,dpi);
        Assert.True(layout.Messages.Left>split);
        Assert.True(layout.Messages.Top>Px(100));
        Assert.True(layout.Messages.Bottom<height-Px(190));
        Assert.True(layout.List.Right<=split);
        Assert.True(layout.Header.Bottom<layout.Messages.Top);
    }

    [Fact] public void BlankOrUnsupportedLayoutFailsRatherThanReturningEmptyChat()
    {
        using var frame=new Bitmap(1100,960);
        using(var g=Graphics.FromImage(frame))g.Clear(Color.White);
        Assert.Equal("layout_unverified",Assert.Throws<WindowAutomationException>(()=>WeChatLayout.Detect(frame,120)).Code);
    }
}
