using System.Text.RegularExpressions;

namespace WeChatDriver.Core;

/// <summary>
/// 消息输出清洗：把滚动读取 + OCR 产生的重复段、超长消息跨屏截断段、界面时间戳/纯数字噪声，
/// 合并/过滤成干净的消息列表。供 ReadChatHistory 输出前调用，让「复制消息」拿到可直接交给 Agent 的文本。
/// </summary>
public static class MessageCleaner
{
    /// <summary>
    /// 界面时间戳/日期分隔线：微信在跨天/跨会话消息之间显示的「星期X」「6月4日」「昨天」等标签，
    /// 以及消息旁的时间「14:18」，不是消息内容。整行匹配才过滤，避免误伤「6月4日我们要开会」这类正文。
    /// </summary>
    private static readonly Regex TimestampNoise = new(
        @"^(?:星期[一二三四五六日天]|\d{1,2}月\d{1,2}日|\d{4}年\d{1,2}月\d{1,2}日|\d{4}[/\-.]\d{1,2}[/\-.]\d{1,2}|昨天|前天|今天)\s*(?:\d{1,2}:\d{2})?\s*$|^\d{1,2}:\d{2}\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 发送者名字标签：微信在气泡上方显示发送者名，OCR 会把它拼到消息首尾（如「Wencharm 我先了解…」、
    /// 「…pip 26 可用 1Wencharm」）。以特征后缀「charm」识别 Wencharm 及其 OCR 变体
    /// （Wehcharm/Wehcharm/encharm/vencharm 等），只剥离出现在**首/尾**的实例，
    /// 不误伤正文中的「联系人名（Wencharm）」（名字后面还有「）」，非边界）。
    /// </summary>
    private static readonly Regex SenderNameBoundary = new(
        @"^(?:[A-Za-z]{0,6}charm)+|(?:[A-Za-z]{0,6}charm)+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 清洗消息列表（按「早→晚」顺序输入）：
    /// 0. 过滤 UI 噪声：剥离首/尾的发送者名字标签（Wencharm 及其 OCR 变体），再过滤时间戳/日期分隔线、4~8 位纯数字；
    /// 1. 相邻拼接：把超长消息跨屏产生的「部分重叠截断段」拼回一条；
    /// 2. 去重：精确重复、互为子串（长文本）、高相似（编辑距离&gt;0.95）、bigram 高度重叠（≥0.85，分散噪声）保留更长的一条。
    /// 短消息（&lt;20 字）只做精确去重，避免误合并。
    /// </summary>
    public static IReadOnlyList<string> Clean(IReadOnlyList<string> messages)
    {
        var meaningful = FilterUiNoise(messages).ToList();

        // 第一步：相邻拼接截断段。
        var merged = new List<string>();
        foreach (var m in meaningful)
        {
            if (merged.Count == 0)
            {
                merged.Add(m);
                continue;
            }

            var joined = TryMerge(merged[^1], m);
            if (joined is not null) merged[^1] = joined;
            else merged.Add(m);
        }

        // 第二步：去重。
        var cleaned = new List<string>();
        foreach (var m in merged)
        {
            var dupIndex = cleaned.FindIndex(r =>
            {
                if (r == m) return true; // 精确重复
                if (r.Length >= 20 && m.Length >= 20)
                {
                    if (r.Contains(m) || m.Contains(r)) return true;          // 截断段（子串）
                    if (IsApproxPrefix(r, m) || IsApproxPrefix(m, r)) return true; // 近似前缀（容忍 OCR 噪声）
                    if (TextSimilarity.EditSimilarity(r, m) > 0.95) return true; // 高相似
                    if (TextSimilarity.Dice(r, m) >= 0.85) return true;          // 分散噪声：bigram 高度重叠（同一长消息多屏 OCR 噪声分散）
                }
                return false;
            });

            if (dupIndex >= 0)
            {
                if (cleaned[dupIndex].Length < m.Length) cleaned[dupIndex] = m;
            }
            else
            {
                cleaned.Add(m);
            }
        }
        return cleaned;
    }

    /// <summary>Remove visual labels without merging or deduplicating real bubbles.</summary>
    public static IReadOnlyList<string> FilterUiNoise(IReadOnlyList<string> messages)
    {
        var meaningful = new List<string>();
        foreach (var raw in messages)
        {
            var m = MessageFingerprint.Normalize(raw);
            if (m.Length < 1) continue;
            var prefix = DirectionPrefixOf(m);
            var body = StripSenderName(StripDirectionPrefix(m));
            if (body.Length < 1 || IsTimestampNoise(body) || IsPureNumberNoise(body)) continue;
            meaningful.Add(prefix + body);
        }
        return meaningful;
    }

    /// <summary>
    /// 是否为界面时间戳/日期分隔线（整行匹配）。
    /// </summary>
    private static bool IsTimestampNoise(string text) => TimestampNoise.IsMatch(text);

    /// <summary>消息方向标识（与 Automation/OcrInputBackend 保持一致）。</summary>
    public const string MinePrefix = "【我】";
    public const string OtherPrefix = "【对方】";

    /// <summary>
    /// 判断是否为微信「我发的」气泡的浅绿色背景。微信「我」气泡为浅绿（G 通道显著高于 R/B，实测 ~RGB(144,240,144)），
    /// 而「对方」气泡与聊天背景均为近白（~RGB(240,240,240)，R/G/B 接近），无法仅靠颜色区分对方/背景。
    /// 故只用绿色识别「我」，用于替代仅靠气泡文本右边界判方向（后者在消息被底部截断、最后一行很短时会误判）。
    /// </summary>
    public static bool IsMineBubbleGreen(int r, int g, int b)
        => g > 150 && g > r + 15 && g > b + 15;

    /// <summary>返回文本开头的方向标识（【我】/【对方】），无则返回空串。</summary>
    private static string DirectionPrefixOf(string text)
    {
        if (text.StartsWith(MinePrefix, StringComparison.Ordinal)) return MinePrefix;
        if (text.StartsWith(OtherPrefix, StringComparison.Ordinal)) return OtherPrefix;
        return "";
    }

    /// <summary>剥离文本开头的方向标识（【我】/【对方】）。</summary>
    public static string StripDirectionPrefix(string text)
    {
        var p = DirectionPrefixOf(text);
        return p.Length > 0 ? text[p.Length..] : text;
    }

    /// <summary>剥离消息首/尾的发送者名字标签（Wencharm 及其 OCR 变体），返回剥离后的文本。</summary>
    private static string StripSenderName(string text)
        => SenderNameBoundary.Replace(text, "").Trim();

    /// <summary>
    /// 是否为 4~8 位纯数字噪声。微信消息区的消息 ID/时间戳等界面元素会被 OCR 成独立纯数字行
    /// （如「504753」）。排除 1~3 位（音频秒数已在上游标注）与 9 位以上（可能是手机号），
    /// 仅过滤最像界面元素的 4~8 位纯数字。会误伤极少数独立 4~8 位数字消息（如验证码），属已知权衡。
    /// </summary>
    private static bool IsPureNumberNoise(string text)
        => text.Length is >= 4 and <= 8 && text.All(char.IsDigit);

    /// <summary>
    /// 判断 a 是否是 b 的「近似前缀」：b 的前 len(a) 字符与 a 高度相似（编辑距离相似度 &gt; 0.92）。
    /// 用于合并超长消息跨屏产生的截断段——截断段有共同开头但 OCR 噪声导致不完全相等。
    /// 仅长文本（≥20 字）参与，避免短消息开头巧合误合并。
    /// </summary>
    private static bool IsApproxPrefix(string a, string b)
    {
        if (a.Length < 20 || b.Length < a.Length) return false;
        var prefix = b[..a.Length];
        return TextSimilarity.EditSimilarity(a, prefix) > 0.92;
    }

    /// <summary>
    /// 尝试拼接两条相邻消息：若「a 的后缀」与「b 的前缀」存在 ≥10 字的重叠，则拼接为一条。
    /// 只对长文本（≥20 字）生效；重叠长度限制在 10~30 字，避免短文本巧合误拼。
    /// </summary>
    private static string? TryMerge(string a, string b)
    {
        if (a.Length < 20 || b.Length < 20) return null;
        var maxLen = Math.Min(a.Length, Math.Min(b.Length, 30));
        for (var len = maxLen; len >= 10; len--)
        {
            if (a[^len..] == b[..len])
                return a + b[len..];
        }
        return null;
    }
}
