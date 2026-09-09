using System.Diagnostics;
using System.Text.Json;
using WeChatDriver.Core;

namespace WeChatDriver.Bench;

/// <summary>100 次真实路径基准：增量读取 → 去重 → 输出 MessageDelta。</summary>
public sealed class BenchRunner
{
    private readonly IWindowAutomationBackend _backend;
    private readonly IConversationIndex _index;

    public BenchRunner(IWindowAutomationBackend backend, IConversationIndex index)
    {
        _backend = backend;
        _index = index;
    }

    public sealed record BenchResult(
        int Iterations,
        IReadOnlyList<double> EnumMs,
        IReadOnlyList<double> ReadMs,
        IReadOnlyList<double> DeltaMs,
        int OcrTriggered,
        IReadOnlyList<FailureInfo> Failures,
        CapabilityMatrix Capabilities,
        string Environment);

    public BenchResult RunReadOnlyBenchmark(int iterations = 100)
    {
        var enumMs = new List<double>();
        var readMs = new List<double>();
        var deltaMs = new List<double>();
        var failures = new List<FailureInfo>();
        var ocrTriggered = 0;

        var sessionResult = _backend.EstablishSession();
        var caps = sessionResult.Succeeded
            ? (_backend.ProbeCapabilities().Value ?? EmptyCaps())
            : EmptyCaps();

        for (var i = 0; i < iterations; i++)
        {
            var t0 = Stopwatch.GetTimestamp();

            var enumerate = _backend.EnumerateConversations();
            if (!enumerate.Succeeded || enumerate.Value is null)
            {
                failures.Add(enumerate.Failure!);
                continue;
            }

            var t1 = Stopwatch.GetTimestamp();
            enumMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);

            var changed = _index.Diff(enumerate.Value);
            var deltaCount = 0;
            foreach (var conv in changed)
            {
                var read = _backend.ReadLatestMessageText(conv.Key);
                if (!read.Succeeded || read.Value is null)
                {
                    // UIA 不足时仅记录，不伪造 OCR 触发（本阶段 OCR 未启用）。
                    if (read.Failure?.Code == FailureCode.MessageTextUnavailable) ocrTriggered++;
                    failures.Add(read.Failure ?? new FailureInfo(FailureCode.MessageTextUnavailable, "读取失败", DateTimeOffset.Now));
                    continue;
                }

                var fp = MessageFingerprint.Compute(conv.Key.StableHint, "in", MessageFingerprint.Normalize(read.Value), 0, DateTimeOffset.Now);
                if (_index.SeenFingerprint(fp)) continue;

                _index.MarkFingerprint(fp);
                deltaCount++;
            }

            _index.Apply(enumerate.Value);

            var t2 = Stopwatch.GetTimestamp();
            readMs.Add((t2 - t1) * 1000.0 / Stopwatch.Frequency);
            deltaMs.Add((t2 - t0) * 1000.0 / Stopwatch.Frequency);
        }

        return new BenchResult(iterations, enumMs, readMs, deltaMs, ocrTriggered, failures, caps,
            $"Windows {Environment.OSVersion.Version} / WeChat probe");
    }

    private static CapabilityMatrix EmptyCaps() => new("unknown", "unknown", Array.Empty<CapabilityEntry>());

    public string ToMarkdown(BenchResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 微信 Driver 只读基准报告");
        sb.AppendLine();
        sb.AppendLine($"- 迭代次数: {r.Iterations}");
        sb.AppendLine($"- 环境: {r.Environment}");
        sb.AppendLine($"- OCR 回退触发次数: {r.OcrTriggered}");
        sb.AppendLine($"- 失败次数: {r.Failures.Count}");
        sb.AppendLine();
        sb.AppendLine("## 分段时间（ms）");
        sb.AppendLine();
        sb.AppendLine("| 阶段 | P50 | P95 | Max | Min | 样本数 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        Append(sb, "会话枚举", MetricsSummary.From("enum", r.EnumMs));
        Append(sb, "消息读取", MetricsSummary.From("read", r.ReadMs));
        Append(sb, "端到端增量", MetricsSummary.From("delta", r.DeltaMs));
        sb.AppendLine();
        sb.AppendLine("## 能力矩阵");
        sb.AppendLine();
        foreach (var e in r.Capabilities.Entries)
            sb.AppendLine($"- {e.Name}: {(e.Available ? "✅" : "❌")} {e.Detail}");
        sb.AppendLine();
        sb.AppendLine("## 失败分布");
        foreach (var g in r.Failures.GroupBy(f => f.Code))
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, string name, MetricsSummary s) =>
        sb.AppendLine($"| {name} | {s.P50:F1} | {s.P95:F1} | {s.Max:F1} | {s.Min:F1} | {s.Count} |");

    public string ToJson(BenchResult r) => JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
}
