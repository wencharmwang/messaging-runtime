using System.Drawing;
using System.Text.RegularExpressions;
using WeChatDriver.Capture;
using WeChatDriver.Core;

namespace WeChatDriver.Automation;

public static class ComposerTextReader
{
    /// <summary>Compare a draft with OCR output; OCR cannot reliably distinguish straight and curly double quotes.</summary>
    public static bool MatchesDraft(string expected, string? observed)
    {
        // This normalization belongs only to visual draft verification, never to
        // request identity or the text written to WeChat. Keep each quote in place:
        // missing, added, or moved quotes must still fail verification.
        static string VisualText(string value)
        {
            var normalized = MessageFingerprint.Normalize(value)
                .Replace('\u201c', '"').Replace('\u201d', '"');
            // OCR returns visual rows, so a line wrapped by the editor becomes an
            // invented space. Remove only whitespace between CJK text/punctuation;
            // Latin word and numeric boundaries remain significant.
            return Regex.Replace(normalized,
                "(?<=[\\u3400-\\u9fff，。！？、；：]) +(?=[\\u3400-\\u9fff，。！？、；：])", "");
        }
        return observed is not null && VisualText(expected) == VisualText(observed);
    }

    public static OperationResult<string?> Read(Bitmap composer, IOcrEngine ocr, bool sendEnabled)
    {
        // Text close to a cropped edge is routinely missed by detection. Preserve the
        // original glyph pixels and surround them with the editor's background color.
        using var padded = new Bitmap(composer.Width + 64, composer.Height + 64);
        using (var graphics = Graphics.FromImage(padded))
        {
            graphics.Clear(composer.GetPixel(composer.Width - 1, composer.Height - 1));
            graphics.DrawImageUnscaled(composer, 32, 32);
        }
        var lines = ocr.RecognizePngWithRects(WindowCapture.ToPng(padded));
        var text = JoinVisualLines(lines);
        if (!string.IsNullOrWhiteSpace(text))
            return OperationResult<string?>.Ok(text.Trim(), 0.85);
        // OCR silence is not evidence of an empty draft when WeChat enables Send.
        return sendEnabled
            ? OperationResult<string?>.Fail(FailureCode.MessageTextUnavailable, "Composer has content that OCR could not read; no input is permitted.")
            : OperationResult<string?>.Ok(null, 0.0);
    }

    public static string JoinVisualLines(IReadOnlyList<OcrTextRect> fragments)
    {
        var rows = new List<List<OcrTextRect>>();
        foreach (var part in fragments.Where(p => !string.IsNullOrWhiteSpace(p.Text)).OrderBy(p => p.CenterY).ThenBy(p => p.X))
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].CenterY - part.CenterY) > Math.Min(row[0].Height, part.Height) / 2)
            { row = new(); rows.Add(row); }
            row.Add(part);
        }
        return string.Join("\n", rows.Select(row =>
        {
            var sorted = row.OrderBy(p => p.X).ToArray();
            var value = sorted[0].Text.Trim();
            for (var i = 1; i < sorted.Length; i++)
            {
                var next = sorted[i].Text.Trim();
                // OCR can split one CJK line at punctuation. These fragments are not newlines.
                var cjk = IsCjk(value[^1]) || IsCjk(next[0]) || "，。！？、；：".Contains(value[^1]);
                value += (cjk ? "" : " ") + next;
            }
            return value;
        }));
    }

    private static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff';
}
