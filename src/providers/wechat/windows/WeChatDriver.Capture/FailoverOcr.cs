using WeChatDriver.Windows;

namespace WeChatDriver.Capture;

/// <summary>
/// 主识别器失败时改用备用识别器再试一次。
///
/// 读会话完全依赖本机 RapidOCR 服务，而它是宿主拉起的独立 Python 子进程：它慢、被占用、
/// 或是返回 500，主引擎就抛 <c>ocr_failed</c>，这一次读取整个作废——用户只看到
/// 「读不到消息」，原因留在子进程里，产品侧什么都看不到。
///
/// 本机的 Windows OCR 语言包通常还在，用它兜一次能把「读不到」换成「读到，置信度低一些」。
/// 兜底不是静默的：<see cref="FailoverCount"/> / <see cref="LastPrimaryFailure"/> 会进能力
/// 自检（<c>get_capabilities</c> 的 ocr_engine 文案），两个识别器都失败时主引擎的原因也一并
/// 抛出，免得下一次又只剩一句「没返回有效观测」。
/// </summary>
public sealed class FailoverOcr : IOcrEngine, IDisposable
{
    private readonly IOcrEngine _primary;
    private readonly Func<IOcrEngine?> _fallbackFactory;
    private IOcrEngine? _fallback;
    private bool _fallbackResolved;

    public FailoverOcr(IOcrEngine primary, Func<IOcrEngine?> fallbackFactory)
    {
        _primary = primary;
        _fallbackFactory = fallbackFactory;
    }

    /// <summary>已发生多少次「主引擎失败、改用备用」。大于 0 说明主引擎不稳，需要排查。</summary>
    public int FailoverCount { get; private set; }

    /// <summary>最近一次主引擎的失败原文，供能力自检直接展示。</summary>
    public string? LastPrimaryFailure { get; private set; }

    public bool IsAvailable => _primary.IsAvailable || Fallback?.IsAvailable == true;

    public string Language
    {
        get
        {
            if (FailoverCount == 0) return _primary.Language;
            return $"{_primary.Language}（主识别器已失败 {FailoverCount} 次，现用 {Fallback?.Language ?? "备用"}；"
                + $"最近一次：{LastPrimaryFailure}）";
        }
    }

    private IOcrEngine? Fallback
    {
        get
        {
            if (_fallbackResolved) return _fallback;
            _fallbackResolved = true;
            try
            {
                var candidate = _fallbackFactory();
                _fallback = candidate?.IsAvailable == true ? candidate : null;
            }
            catch
            {
                // 备用识别器本身建不起来（语言包缺失等）不是致命错误：保留主引擎的失败原因。
                _fallback = null;
            }
            return _fallback;
        }
    }

    public IReadOnlyList<OcrLine> RecognizePng(byte[] pngBytes)
        => Run(engine => engine.RecognizePng(pngBytes));

    public IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] pngBytes)
        => Run(engine => engine.RecognizePngWithRects(pngBytes));

    private IReadOnlyList<T> Run<T>(Func<IOcrEngine, IReadOnlyList<T>> read)
    {
        Exception? failure = null;
        try
        {
            return read(_primary);
        }
        catch (Exception error)
        {
            // 识别器的失败都是 OCR 问题（超时、5xx、返回结构变了），一律值得换一次；
            // 连 ObjectDisposedException 这种也算——总比整次读取作废好。
            failure = error;
        }

        // 没有失败就不可能走到这里：上面要么已返回，要么已赋值。
        var primaryError = failure ?? new WindowAutomationException("ocr_failed", "OCR 调用失败。");

        var fallback = Fallback;
        if (fallback is null) throw primaryError;

        try
        {
            var result = read(fallback);
            FailoverCount++;
            LastPrimaryFailure = Describe(primaryError);
            return result;
        }
        catch (Exception fallbackError)
        {
            // 两个都失败时抛主引擎的原因（它才是根因），把备用失败附在后面，别让信息丢掉。
            LastPrimaryFailure = Describe(primaryError);
            throw new WindowAutomationException(CodeOf(primaryError),
                $"{primaryError.Message} | 备用识别器（{fallback.Language}）也失败：{fallbackError.Message}");
        }
    }

    private static string CodeOf(Exception error)
        => error is WindowAutomationException known ? known.Code : "ocr_failed";

    private static string Describe(Exception error)
        => error is WindowAutomationException known
            ? $"{known.Code}: {known.Message}"
            : $"{error.GetType().Name}: {error.Message}";

    public void Dispose()
    {
        if (_primary is IDisposable primary) primary.Dispose();
        if (_fallback is IDisposable fallback) fallback.Dispose();
    }
}
