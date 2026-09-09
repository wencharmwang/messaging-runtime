using System.Text;
using System.Text.Json;
using WeChatDriver.Core;

namespace WeChatDriver.Bench;

/// <summary>分段时间汇总。</summary>
public sealed record StageSummary(string Name, int Count, double P50, double P95, double Max, double Min);

/// <summary>失败码分布条目。</summary>
public sealed record FailureCount(string Code, int Count);

/// <summary>
/// 一次基准运行的完整结果，可序列化为 JSON（原始数据）与 Markdown（汇总报告）。
/// 对应 MVP §10.1：至少包含 P50/P95、错误类别、OCR 触发率、UIA 能力矩阵与版本/DPI/布局。
/// </summary>
public sealed record BenchReport(
    DateTimeOffset GeneratedAt,
    string Environment,
    string Backend,
    string ClientVersion,
    string LocatorProfileId,
    int Dpi,
    string WindowBounds,
    int Iterations,
    IReadOnlyList<StageSummary> Stages,
    IReadOnlyList<FailureCount> Failures,
    double OcrTriggerRate,
    IReadOnlyList<CapabilityEntry> Capabilities,
    IReadOnlyList<string> Notes)
{
    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 微信 Driver 基准报告 · {GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"- 环境: {Environment}");
        sb.AppendLine($"- 后端: {Backend}");
        sb.AppendLine($"- 微信 {ClientVersion} · Profile {LocatorProfileId} · DPI {Dpi} · 窗口 {WindowBounds}");
        sb.AppendLine($"- 迭代次数: {Iterations}");
        sb.AppendLine($"- OCR 触发率: {OcrTriggerRate * 100:F0}%");
        sb.AppendLine();
        sb.AppendLine("## 分段时间（ms）");
        sb.AppendLine();
        sb.AppendLine("| 阶段 | P50 | P95 | Max | Min | 样本数 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var s in Stages)
            sb.AppendLine($"| {s.Name} | {s.P50:F1} | {s.P95:F1} | {s.Max:F1} | {s.Min:F1} | {s.Count} |");
        sb.AppendLine();
        sb.AppendLine("## 能力矩阵");
        sb.AppendLine();
        foreach (var c in Capabilities)
            sb.AppendLine($"- {c.Name}: {(c.Available ? "✅" : "❌")} {c.Detail}");
        sb.AppendLine();
        sb.AppendLine("## 失败分布");
        if (Failures.Count == 0)
        {
            sb.AppendLine("- 无失败");
        }
        else
        {
            foreach (var f in Failures)
                sb.AppendLine($"- {f.Code}: {f.Count}");
        }
        if (Notes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## 备注");
            foreach (var n in Notes)
                sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    /// <summary>从原始样本生成 StageSummary（复用 MetricsSummary 的百分位计算）。</summary>
    public static StageSummary Summary(string name, IReadOnlyList<double> samples)
    {
        var m = MetricsSummary.From(name, samples);
        return new StageSummary(name, m.Count, m.P50, m.P95, m.Max, m.Min);
    }

    /// <summary>将报告写入磁盘（JSON 原始数据 + Markdown 汇总），返回两文件路径。</summary>
    public static (string JsonPath, string MdPath) WriteToDisk(string directory, string tag, BenchReport report)
    {
        Directory.CreateDirectory(directory);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(directory, $"{ts}-{tag}.json");
        var mdPath = Path.Combine(directory, $"{ts}-{tag}.md");
        File.WriteAllText(jsonPath, report.ToJson());
        File.WriteAllText(mdPath, report.ToMarkdown());
        return (jsonPath, mdPath);
    }
}
