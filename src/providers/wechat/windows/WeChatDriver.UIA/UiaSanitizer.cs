using System.Security.Cryptography;
using System.Text;

namespace WeChatDriver.UIA;

/// <summary>脱敏：探测报告/日志不得包含原始聊天内容。</summary>
public static class UiaSanitizer
{
    /// <summary>对可能是聊天内容的 Name 做脱敏。保留控件类型信息，隐藏文本。</summary>
    public static string MaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        if (name.Length <= 2) return name;
        // 短到难以判定是否为聊天内容的，直接哈希。
        return $"#{Hash(name)[..8]}";
    }

    public static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>对窗口标题类做轻度脱敏（保留可识别前缀，隐藏可能含人名的部分）。</summary>
    public static string MaskTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        if (title is "微信" or "WeChat" or "Weixin") return title;
        return $"#{Hash(title)[..8]}";
    }
}
