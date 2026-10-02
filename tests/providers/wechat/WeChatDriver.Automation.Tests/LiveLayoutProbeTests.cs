using System.Drawing;
using WeChatDriver.Automation;
using WeChatDriver.Capture;
using WeChatDriver.Windows;
using Xunit;
using Xunit.Abstractions;

namespace WeChatDriver.Automation.Tests;

// Opt-in, read-only local compatibility probe. Never types or sends anything.
public sealed class LiveLayoutProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void InspectCurrentWindowWithoutInput()
    {
        if (Environment.GetEnvironmentVariable("WECHAT_LIVE_LAYOUT_PROBE") != "1") return;
        using var dpi = new DpiAwarenessScope();
        var session = WindowDiscovery.Discover();
        Assert.True(session.Succeeded);
        var hwnd = WindowDiscovery.FindMainWindowLoose(session.Value!.ProcessId);
        using var frame = WindowCapture.CaptureWindow(hwnd);
        var layout = WeChatLayout.Detect(frame, WindowDiscovery.GetDpi(hwnd));
        Assert.True(layout.HasOpenConversation);
        using var composer = frame.Clone(layout.Composer, frame.PixelFormat);
        var corner = composer.GetPixel(2, 2);
        var oldGatePasses = false;
        for (var y = 2; y < composer.Height; y += 3)
        for (var x = 2; x < composer.Width; x += 3)
        {
            var pixel = composer.GetPixel(x, y);
            if (Math.Abs(pixel.R-corner.R)+Math.Abs(pixel.G-corner.G)+Math.Abs(pixel.B-corner.B)>60)
                oldGatePasses = true;
        }
        output.WriteLine($"Version={WindowDiscovery.GetProcessFileVersion(session.Value.ProcessId)}; Composer={layout.Composer}; Old ink gate={oldGatePasses}; Full layout=ready");
        if (Environment.GetEnvironmentVariable("WECHAT_LIVE_DRAFT_PROBE") == "1")
        {
            int Px(int value) => (int)Math.Round(value * layout.Dpi / 120.0);
            var type = typeof(OcrInputBackend);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var bottom = (int)type.GetField("SendCenterFromBottomPx", flags)!.GetRawConstantValue()!;
            var height = (int)type.GetField("SendButtonHeightPx", flags)!.GetRawConstantValue()!;
            using var button = WindowCapture.CaptureRegionFromBottom(hwnd, .88, .11, Px(bottom), Px(height));
            var found = ((int x, int y, bool found))type.GetMethod("FindGreenButtonCenter", flags)!.Invoke(null, [button])!;
            Assert.True(found.found, "Visible draft must enable the detected Send button.");
            using var old = WindowCapture.CaptureRegionFromBottom(hwnd, .88, .11, Px(57), Px(40));
            var previous = ((int x, int y, bool found))type.GetMethod("FindGreenButtonCenter", flags)!.Invoke(null, [old])!;
            output.WriteLine($"Old button crop={previous.found}; New button crop={found.found}");
        }
    }
}
