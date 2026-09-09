using System.Text.Json;

namespace WeChatDriver.Bench;

/// <summary>百分位汇总。</summary>
public sealed class MetricsSummary
{
    public string Key { get; init; } = "";
    public int Count { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double Max { get; init; }
    public double Min { get; init; }

    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var idx = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    public static MetricsSummary From(string key, IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        return new MetricsSummary
        {
            Key = key,
            Count = sorted.Count,
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
            Max = sorted.Count > 0 ? sorted[^1] : 0,
            Min = sorted.Count > 0 ? sorted[0] : 0
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
