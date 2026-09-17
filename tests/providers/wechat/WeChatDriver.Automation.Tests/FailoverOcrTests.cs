using WeChatDriver.Capture;
using WeChatDriver.Windows;
using Xunit;

namespace WeChatDriver.Automation.Tests;

/// <summary>
/// The local RapidOCR service is a separate Python process, and one hiccup used to fail the
/// whole read with "读不到消息". These pin the two things that must hold: a single primary
/// failure does not kill the read when a fallback recognizer exists, and when everything
/// fails the primary's own reason survives instead of collapsing into a generic sentence.
/// </summary>
public sealed class FailoverOcrTests
{
    private sealed class Engine : IOcrEngine
    {
        private readonly string _text;
        private readonly WindowAutomationException? _failure;

        public Engine(string text, WindowAutomationException? failure = null, bool available = true)
        {
            _text = text;
            _failure = failure;
            IsAvailable = available;
        }

        public bool IsAvailable { get; }
        public string Language => _text;
        public int Calls { get; private set; }

        public IReadOnlyList<OcrLine> RecognizePng(byte[] pngBytes)
        {
            Calls++;
            if (_failure is not null) throw _failure;
            return [new OcrLine(_text, 0.9)];
        }

        public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] pngBytes)
        {
            Calls++;
            if (_failure is not null) throw _failure;
            return [new OcrTextRect(_text, 0, 0, 10, 10)];
        }
    }

    private static WindowAutomationException OcrFailure()
        => new("ocr_failed", "OCR 服务返回 500：CUDA out of memory");

    [Fact]
    public void A_failing_primary_still_produces_a_read()
    {
        var primary = new Engine("paddle", OcrFailure());
        var fallback = new Engine("windows");
        var ocr = new FailoverOcr(primary, () => fallback);

        var lines = ocr.RecognizePng([1, 2, 3]);

        Assert.Equal("windows", lines[0].Text);
        Assert.Equal(1, ocr.FailoverCount);
        // The primary's reason is recorded, not swallowed: it is what the capability check shows.
        Assert.Contains("CUDA out of memory", ocr.LastPrimaryFailure);
        Assert.Contains("CUDA out of memory", ocr.Language);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public void Rect_reading_uses_the_same_failover()
    {
        var ocr = new FailoverOcr(new Engine("paddle", OcrFailure()), () => new Engine("windows"));

        var rects = ocr.RecognizePngWithRects([1, 2, 3]);

        Assert.Equal("windows", rects[0].Text);
        Assert.Equal(1, ocr.FailoverCount);
    }

    [Fact]
    public void When_both_fail_the_primary_reason_is_kept()
    {
        var ocr = new FailoverOcr(
            new Engine("paddle", OcrFailure()),
            () => new Engine("windows", new WindowAutomationException("ocr_unavailable", "Windows OCR is unavailable.")));

        var error = Assert.Throws<WindowAutomationException>(() => ocr.RecognizePng([1]));

        Assert.Equal("ocr_failed", error.Code);
        Assert.Contains("CUDA out of memory", error.Message);
        Assert.Contains("备用识别器", error.Message);
    }

    [Fact]
    public void Without_a_usable_fallback_the_original_error_is_rethrown()
    {
        var ocr = new FailoverOcr(new Engine("paddle", OcrFailure()), () => null);

        var error = Assert.Throws<WindowAutomationException>(() => ocr.RecognizePng([1]));

        Assert.Equal("ocr_failed", error.Code);
        Assert.Contains("CUDA out of memory", error.Message);
        Assert.Equal(0, ocr.FailoverCount);
    }

    [Fact]
    public void A_healthy_primary_never_touches_the_fallback()
    {
        var fallback = new Engine("windows");
        var ocr = new FailoverOcr(new Engine("paddle"), () => fallback);

        Assert.Equal("paddle", ocr.RecognizePng([1])[0].Text);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0, ocr.FailoverCount);
        Assert.Equal("paddle", ocr.Language);
    }
}
