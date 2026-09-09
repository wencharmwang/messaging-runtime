using System.Collections.Concurrent;

namespace WeChatDriver.Core;

/// <summary>内存存储：测试与无磁盘场景使用。</summary>
public sealed class InMemoryStore : ILocalStore
{
    private readonly ConcurrentDictionary<string, FailureInfo> _failures = new();
    private readonly ConcurrentDictionary<string, double?> _metrics = new();

    public void Initialize() { }

    public void SaveConversationState(ConversationState state) { }

    public void SaveMetric(string operationId, string key, double? value) =>
        _metrics[$"{operationId}:{key}"] = value;

    public void SaveFailure(FailureInfo failure) =>
        _failures[Guid.NewGuid().ToString("N")] = failure;

    public IReadOnlyList<FailureInfo> RecentFailures(int limit = 50) =>
        _failures.Values.Take(limit).ToList();

    public void ClearAll()
    {
        _failures.Clear();
        _metrics.Clear();
    }
}
