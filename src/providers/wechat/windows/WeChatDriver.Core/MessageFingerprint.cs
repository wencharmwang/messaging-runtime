using System.Security.Cryptography;
using System.Text;

namespace WeChatDriver.Core;

/// <summary>消息指纹：用于去重，不是永久用户标识。</summary>
public static class MessageFingerprint
{
    public static string Compute(
        string conversationStableHint,
        string direction,
        string normalizedText,
        int visibleOrder,
        DateTimeOffset observedMinute)
    {
        var minute = observedMinute.ToString("yyyyMMddHHmm");
        var raw = $"{conversationStableHint}|{direction}|{normalizedText}|{visibleOrder}|{minute}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    public static string Normalize(string text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Comparison key for a conversation name: letters, digits and Han characters only.
    /// The OCR engine inserts spaces inside a CJK run (a chat titled 小璐璐 also comes back as
    /// "小 璐 璐") and is inconsistent about full/half-width punctuation, so a name must be
    /// compared on this key — Normalize only collapses runs of whitespace, which is not enough.
    /// Matching only: the conversation id is still a hash of the raw name.
    /// </summary>
    public static string NameKey(string text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).ToArray());

    public static string DraftHash(string text) => Compute("draft", "out", Normalize(text), 0, DateTimeOffset.UtcNow);

    public static string ActivityHash(string text)
    {
        var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text)));
        return Convert.ToHexString(bytes)[..16];
    }
}
