using System.Collections.Concurrent;
using System.Diagnostics;
using WeChatDriver.Core;

namespace WeChatDriver.Automation;

/// <summary>串行动作执行器：唯一允许控制微信的组件。单线程/单队列。</summary>
public sealed class ActionExecutor : IActionExecutor
{
    private readonly IWindowAutomationBackend _backend;
    private readonly ILocalStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingOperation> _pending = new();

    public ActionState CurrentState { get; private set; } = ActionState.Idle;

    public ActionExecutor(IWindowAutomationBackend backend, ILocalStore store)
    {
        _backend = backend;
        _store = store;
    }

    private sealed record PendingOperation(ActionRequest Request, string DraftHash);

    /// <summary>执行动作。P0-A 阶段 AllowSend=false，仅写入草稿。</summary>
    public OperationResult Execute(ActionRequest request)
    {
        _gate.Wait();
        try
        {
            var timings = new Timings { OperationId = request.OperationId };
            var stopwatch = Stopwatch.StartNew();

            // 1. ValidateSession
            CurrentState = ActionState.ValidateSession;
            var session = _backend.EstablishSession();
            if (!session.Succeeded)
                return Fail(session.Failure!.Code, session.Failure.Message, timings);

            // 2. ResolveConversation
            CurrentState = ActionState.ResolveConversation;
            var resolver = new ConversationResolver(_backend);
            var resolved = resolver.Resolve(request.Conversation);
            if (!resolved.Succeeded || resolved.Value is null)
                return Fail(resolved.Failure!.Code, resolved.Failure.Message, timings);

            // 3. VerifyConversationTitle
            CurrentState = ActionState.VerifyConversationTitle;
            if (!VerifyTitle(resolved.Value, request.Conversation))
                return Fail(FailureCode.TitleMismatch, "聊天标题不匹配", timings);

            // 4. FocusComposer
            CurrentState = ActionState.FocusComposer;

            // 5. WriteDraft
            CurrentState = ActionState.WriteDraft;
            var write = _backend.WriteComposer(resolved.Value, request.DraftText);
            if (!write.Succeeded)
                return Fail(write.Failure!.Code, write.Failure.Message, timings);

            // 6. VerifyDraft
            CurrentState = ActionState.VerifyDraft;
            var verify = VerifyDraft(request.DraftText);
            if (!verify.Succeeded)
                return Fail(verify.Failure!.Code, verify.Failure.Message, timings);

            var draftHash = MessageFingerprint.DraftHash(request.DraftText);
            _pending[request.OperationId] = new PendingOperation(request, draftHash);

            // 7. AwaitUserApproval / Send
            if (request.AllowSend)
                return SendInternal(request.OperationId, timings);

            CurrentState = ActionState.AwaitUserApproval;
            stopwatch.Stop();
            Record(timings);
            return OperationResult.Ok(0.9, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用户在本地面板确认后，恢复发送。</summary>
    public OperationResult ApproveAndSend(string operationId)
    {
        _gate.Wait();
        try
        {
            var timings = new Timings { OperationId = operationId };
            return SendInternal(operationId, timings);
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperationResult SendInternal(string operationId, Timings timings)
    {
        if (!_pending.TryGetValue(operationId, out var op))
            return Fail(FailureCode.InternalError, "未找到待发送操作", timings);

        var stopwatch = Stopwatch.StartNew();

        // 发送前必须重新验证目标会话 + 草稿哈希。
        CurrentState = ActionState.VerifyConversationTitle;
        if (!VerifyTitle(op.Request.Conversation, op.Request.Conversation))
            return Fail(FailureCode.TitleMismatch, "发送前标题校验失败", timings);

        var composerText = _backend.ReadComposerText();
        if (!composerText.Succeeded ||
            MessageFingerprint.Normalize(composerText.Value ?? "") != MessageFingerprint.Normalize(op.Request.DraftText))
            return Fail(FailureCode.DraftHashMismatch, "发送前输入框内容与草稿不一致", timings);

        CurrentState = ActionState.Send;
        var send = _backend.SendMessage(op.Request.Conversation);
        if (!send.Succeeded)
            return Fail(send.Failure!.Code, send.Failure.Message, timings);

        CurrentState = ActionState.VerifySend;
        var verify = _backend.VerifySent(op.Request.Conversation, op.DraftHash);
        if (!verify.Succeeded)
        {
            CurrentState = ActionState.SendUnverified;
            return Fail(verify.Failure!.Code, verify.Failure.Message, timings);
        }

        _pending.TryRemove(operationId, out _);
        CurrentState = ActionState.Succeeded;
        timings.TSendVerified = DateTimeOffset.Now;
        stopwatch.Stop();
        Record(timings);
        return OperationResult.Ok(0.9, stopwatch.Elapsed);
    }

    private bool VerifyTitle(ConversationKey resolved, ConversationKey requested) =>
        string.Equals(resolved.DisplayName, requested.DisplayName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(resolved.StableHint, requested.StableHint, StringComparison.Ordinal);

    private OperationResult VerifyDraft(string draftText)
    {
        var text = _backend.ReadComposerText();
        if (!text.Succeeded)
            return OperationResult.Fail(FailureCode.DraftHashMismatch, "无法读取输入框内容");

        return MessageFingerprint.Normalize(text.Value ?? "") == MessageFingerprint.Normalize(draftText)
            ? OperationResult.Ok(0.95)
            : OperationResult.Fail(FailureCode.DraftHashMismatch, "写入草稿后内容校验不一致");
    }

    private OperationResult Fail(FailureCode code, string message, Timings timings)
    {
        _store.SaveFailure(new FailureInfo(code, message, DateTimeOffset.Now, timings.OperationId));
        CurrentState = MapFailureState(code);
        Record(timings);
        return OperationResult.Fail(code, message, timings.OperationId);
    }

    private static ActionState MapFailureState(FailureCode code) => code switch
    {
        FailureCode.NeedsForeground or FailureCode.ForegroundLost => ActionState.NeedsForeground,
        FailureCode.AmbiguousConversation => ActionState.AmbiguousConversation,
        FailureCode.ComposeUnsupported => ActionState.ComposeUnsupported,
        FailureCode.SendUnverified => ActionState.SendUnverified,
        _ => ActionState.Failed
    };

    private void Record(Timings timings)
    {
        foreach (var (k, v) in timings.Snapshot())
            _store.SaveMetric(timings.OperationId, k, v);
    }

    public void Cancel(string operationId)
    {
        _pending.TryRemove(operationId, out _);
        CurrentState = ActionState.Idle;
    }
}
