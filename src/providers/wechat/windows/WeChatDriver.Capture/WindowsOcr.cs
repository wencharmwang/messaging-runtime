using System.Runtime.InteropServices.WindowsRuntime;
using WeChatDriver.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WeChatDriver.Capture;

/// <summary>Windows 内置离线 OCR（Windows.Media.Ocr）。仅本地运行，不联网上传。</summary>
public sealed class WindowsOcr : IOcrEngine
{
    private readonly OcrEngine? _engine;
    private readonly string _language;

    public bool IsAvailable => _engine is not null;
    public string Language => _language;

    public WindowsOcr(string? languageTag = null)
    {
        _engine = languageTag is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language(languageTag));
        _language = _engine?.RecognizerLanguage?.LanguageTag ?? languageTag ?? "unavailable";
    }

    /// <summary>识别 PNG 图片中的文字。不可用或失败时返回结构化异常，避免误报空聊天。</summary>
    public IReadOnlyList<OcrLine> RecognizePng(byte[] pngBytes)
    {
        if (_engine is null) throw new WindowAutomationException("ocr_unavailable","Windows OCR is unavailable.");

        // WinRT OCR 在 STA 线程上重复调用会崩溃，改在后台 MTA 线程执行。
        try
        {
            return Task.Run(() => RecognizeCore(pngBytes)).GetAwaiter().GetResult();
        }
        catch
        {
            throw new WindowAutomationException("ocr_failed","Windows OCR failed to produce a valid observation.");
        }
    }

    private IReadOnlyList<OcrLine> RecognizeCore(byte[] pngBytes)
    {
        using var bitmap = DecodePngAsync(pngBytes).GetAwaiter().GetResult();
        var result = _engine!.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            var conf = line.Words.Count == 0
                ? 0.0
                : line.Words.Average(w => (double)(w.BoundingRect.Width > 0 ? 1.0 : 0.5));
            lines.Add(new OcrLine(text, conf));
        }
        return lines;
    }

    /// <summary>识别 PNG 并返回每行文字 + 矩形坐标（区域内的相对像素）。</summary>
    public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] pngBytes)
    {
        if (_engine is null) throw new WindowAutomationException("ocr_unavailable","Windows OCR is unavailable.");
        try
        {
            return Task.Run(() => RecognizeCoreWithRects(pngBytes)).GetAwaiter().GetResult();
        }
        catch
        {
            throw new WindowAutomationException("ocr_failed","Windows OCR failed to produce a valid observation.");
        }
    }

    private IReadOnlyList<OcrTextRect> RecognizeCoreWithRects(byte[] pngBytes)
    {
        using var bitmap = DecodePngAsync(pngBytes).GetAwaiter().GetResult();
        var result = _engine!.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var rects = new List<OcrTextRect>();
        foreach (var line in result.Lines)
        {
            var text = line.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            if(line.Words.Count==0) continue;
            var left=line.Words.Min(w=>w.BoundingRect.Left);
            var top=line.Words.Min(w=>w.BoundingRect.Top);
            var right=line.Words.Max(w=>w.BoundingRect.Right);
            var bottom=line.Words.Max(w=>w.BoundingRect.Bottom);
            rects.Add(new OcrTextRect(text,(int)left,(int)top,(int)(right-left),(int)(bottom-top)));
        }
        return rects;
    }

    private static async Task<SoftwareBitmap> DecodePngAsync(byte[] pngBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
