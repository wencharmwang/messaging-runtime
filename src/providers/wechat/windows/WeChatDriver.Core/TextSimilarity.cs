namespace WeChatDriver.Core;

/// <summary>文本相似度：用于 OCR 重复行去重（同一气泡不同屏 OCR 有轻微噪声）。</summary>
public static class TextSimilarity
{
    /// <summary>字符 bigram 的 Dice 系数（0~1）。中文短文本效果好。</summary>
    public static double Dice(string a, string b)
    {
        if (a == b) return 1.0;
        var sa = ToBigrams(a);
        var sb = ToBigrams(b);
        if (sa.Count == 0 || sb.Count == 0) return 0.0;
        var inter = 0;
        foreach (var g in sa)
            if (sb.Contains(g)) inter++;
        return 2.0 * inter / (sa.Count + sb.Count);
    }

    /// <summary>编辑距离（Levenshtein）。</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>归一化编辑距离相似度（0~1），用于捕捉 bigram 因单字噪声断裂的近重复。</summary>
    public static double EditSimilarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    /// <summary>
    /// 判断是否可视为重复。混合判据：bigram Dice ≥ 0.78，或较长字符串（≥4 字）编辑距离相似度 ≥ 0.85。
    /// 后者用于兜住 OCR 单字噪声导致 bigram 断裂的近重复（如「明天下午」→「明天不午」）。
    /// </summary>
    public static bool IsDuplicate(string a, string b, double threshold = 0.78)
    {
        if (Dice(a, b) >= threshold) return true;
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen >= 4 && EditSimilarity(a, b) >= 0.85;
    }

    private static HashSet<string> ToBigrams(string s)
    {
        var set = new HashSet<string>();
        s = s.Trim();
        if (s.Length == 0) return set;
        if (s.Length == 1)
        {
            set.Add(s);
            return set;
        }
        for (var i = 0; i < s.Length - 1; i++)
            set.Add(s.Substring(i, 2));
        return set;
    }
}
