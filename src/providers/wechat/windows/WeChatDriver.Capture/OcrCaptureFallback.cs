using WeChatDriver.Core;

namespace WeChatDriver.Capture;

/// <summary>
/// 局部截图 + Windows OCR 回退。仅在 UIA 无法读取正文时触发（规范第 5.4 节）。
/// 置信度低于阈值或无法判定方向时返回 NeedsReview，禁止生成自动草稿。
/// </summary>
public sealed class OcrCaptureFallback : ICaptureFallback
{
    private readonly WindowsOcr _ocr;
    private readonly double _confidenceThreshold;

    public OcrCaptureFallback(double confidenceThreshold = 0.5, string? languageTag = null)
    {
        _ocr = new WindowsOcr(languageTag);
        _confidenceThreshold = confidenceThreshold;
    }

    public bool IsAvailable => _ocr.IsAvailable;
    public string OcrLanguage => _ocr.Language;

    public OperationResult<MessageDelta> ReadLatestMessage(WeChatWindowSession session, ConversationKey conversation)
    {
        if (!_ocr.IsAvailable)
            return OperationResult<MessageDelta>.Fail(FailureCode.NeedsReview, "Windows OCR 语言不可用", confidence: 0.0);

        // 默认候选区域：聊天区底部（相对坐标）。可用 Locator Profile 覆盖。
        const double x = 0.35, y = 0.55, w = 0.60, h = 0.38;

        Bitmap bmp;
        try
        {
            bmp = WindowCapture.CaptureRegion(session.WindowHandle, x, y, w, h);
        }
        catch (Exception ex)
        {
            return OperationResult<MessageDelta>.Fail(FailureCode.InternalError, $"截图失败: {ex.Message}", confidence: 0.0);
        }

        try
        {
            var png = WindowCapture.ToPng(bmp);
            var lines = _ocr.RecognizePng(png);
            var latest = lines.LastOrDefault();
            if (latest is null || string.IsNullOrWhiteSpace(latest.Text))
                return OperationResult<MessageDelta>.Fail(FailureCode.MessageTextUnavailable, "OCR 未识别到文字", confidence: 0.0);

            var confidence = latest.Confidence;
            if (confidence < _confidenceThreshold)
                return OperationResult<MessageDelta>.Fail(FailureCode.OcrBelowThreshold, $"OCR 置信度过低: {confidence:F2}", confidence: confidence);

            var normalized = MessageFingerprint.Normalize(latest.Text);
            var fp = MessageFingerprint.Compute(conversation.StableHint, "in", normalized, 0, DateTimeOffset.Now);
            var delta = new MessageDelta(
                $"msg_{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
                conversation,
                latest.Text,
                DateTimeOffset.Now,
                MessageSource.LocalOcr,
                confidence,
                fp);

            return OperationResult<MessageDelta>.Ok(delta, confidence);
        }
        finally
        {
            bmp.Dispose();
        }
    }
}
