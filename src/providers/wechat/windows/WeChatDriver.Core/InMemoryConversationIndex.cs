using System.Collections.Concurrent;

namespace WeChatDriver.Core;

/// <summary>会话索引：内存实现。只对比变化会话，维护指纹去重。</summary>
public sealed class InMemoryConversationIndex : IConversationIndex
{
    private readonly object _gate = new();
    private Dictionary<string, ConversationState> _last = new();
    private readonly ConcurrentDictionary<string, byte> _fingerprints = new();

    private static string Key(ConversationState s) => s.Key.StableHint;

    public IReadOnlyList<ConversationState> Diff(IReadOnlyList<ConversationState> current)
    {
        lock (_gate)
        {
            var changed = new List<ConversationState>();
            foreach (var s in current)
            {
                var k = Key(s);
                if (!_last.TryGetValue(k, out var prev))
                {
                    changed.Add(s);
                    continue;
                }

                var prevFp = prev.LastMessageFingerprint;
                var curFp = s.LastMessageFingerprint;
                if (prevFp != curFp && curFp is not null)
                    changed.Add(s);
            }

            return changed;
        }
    }

    public void Apply(IReadOnlyList<ConversationState> current)
    {
        lock (_gate)
        {
            _last = current.ToDictionary(Key, s => s);
        }
    }

    public bool SeenFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return false;
        return _fingerprints.ContainsKey(fingerprint);
    }

    public void MarkFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        _fingerprints[fingerprint] = 0;
        // 有界去重：避免无限增长。
        if (_fingerprints.Count > 10_000)
        {
            foreach (var k in _fingerprints.Keys.Take(5_000))
                _fingerprints.TryRemove(k, out _);
        }
    }
}
