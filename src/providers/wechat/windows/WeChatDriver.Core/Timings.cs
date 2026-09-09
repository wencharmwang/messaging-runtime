namespace WeChatDriver.Core;

/// <summary>分段时间记录。禁止只记录端到端总耗时。</summary>
public sealed class Timings
{
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset? TEventDetected { get; set; }
    public DateTimeOffset? TUiaReadDone { get; set; }
    public DateTimeOffset? TOcrDone { get; set; }
    public DateTimeOffset? TDeltaEmitted { get; set; }
    public DateTimeOffset? TDraftReceived { get; set; }
    public DateTimeOffset? TConversationResolved { get; set; }
    public DateTimeOffset? TComposerFocused { get; set; }
    public DateTimeOffset? TDraftWritten { get; set; }
    public DateTimeOffset? TSendVerified { get; set; }

    public double? Ms(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null || b is null ? null : (b.Value - a.Value).TotalMilliseconds;

    /// <summary>输出为可序列化的指标字典，仅含耗时，不含原文。</summary>
    public IReadOnlyDictionary<string, double?> Snapshot() => new Dictionary<string, double?>
    {
        ["t_event_to_uia_read"] = Ms(TEventDetected, TUiaReadDone),
        ["t_event_to_delta"] = Ms(TEventDetected, TDeltaEmitted),
        ["t_ocr"] = Ms(TUiaReadDone, TOcrDone),
        ["t_delta_to_draft"] = Ms(TDeltaEmitted, TDraftReceived),
        ["t_resolve_conversation"] = Ms(TDraftReceived, TConversationResolved),
        ["t_focus_composer"] = Ms(TConversationResolved, TComposerFocused),
        ["t_write_draft"] = Ms(TComposerFocused, TDraftWritten),
        ["t_verify_send"] = Ms(TDraftWritten, TSendVerified)
    };
}
