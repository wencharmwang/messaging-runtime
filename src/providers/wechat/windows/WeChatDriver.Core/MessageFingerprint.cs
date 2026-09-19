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

    /// <summary>
    /// 列表活动指纹里的时间成分：只保留「真来了新消息」才会变的部分，「时钟在走」不算。
    ///
    /// 微信把最近一条消息的显示时间画在行右侧，而这个文本会**自己变**：刚刚 → 1分钟前 →
    /// 1小时前 → 11:29。指纹若直接拼原始文本，没有任何新消息也会每隔几分钟翻一次，上层就把
    /// 「时间显示变了」当成「来了新消息」，一轮一轮打开会话去读 —— 用户看到的是助手一直粘在
    /// 同一个会话上，而且没消息也一直在动。
    ///
    /// 相对时间（刚刚/刚才/N秒前/N分钟前/N小时前）折成一个记号；今天内的具体时刻原样保留 ——
    /// 前者是时钟的噪声，后者只有真的来了新消息才会前进。一旦走到具体时刻就不再自变，所以不会
    /// 反复触发。跨天（昨天/星期X/日期）本身是稳定文本，保持原样即可。
    /// </summary>
    public static string ActivityTimeKey(string? displayTime)
    {
        var text=new string((displayTime??"").Where(ch=>!char.IsWhiteSpace(ch)).ToArray());
        return text.Length==0?"":RelativeTimeCell.IsMatch(text)?"<recent>":text;
    }

    private static readonly System.Text.RegularExpressions.Regex RelativeTimeCell=new(
        @"^(?:刚刚|刚才|\d{1,2}秒前|\d{1,2}分钟前|\d{1,2}小时前)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}
