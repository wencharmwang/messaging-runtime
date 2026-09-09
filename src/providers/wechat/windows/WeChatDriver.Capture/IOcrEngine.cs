namespace WeChatDriver.Capture;

/// <summary>OCR 结果行。</summary>
public sealed record OcrLine(string Text, double Confidence);

/// <summary>带矩形坐标的 OCR 结果（用于点击/定位）。坐标为 OCR 输入图片内的像素。</summary>
public sealed record OcrTextRect(string Text, int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
}

/// <summary>OCR 引擎抽象：Windows 内置 OCR 或 RapidOCR(PaddleOCR onnxruntime)。</summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }
    string Language { get; }
    IReadOnlyList<OcrLine> RecognizePng(byte[] pngBytes);
    IReadOnlyList<OcrTextRect> RecognizePngWithRects(byte[] pngBytes);
}
