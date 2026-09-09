using WeChatDriver.Automation;
using WeChatDriver.Core;

namespace WeChatDriver.Mcp;

/// <summary>
/// 把 OcrInputBackend 适配成 MCP 工具所需的 IWeChatDriverApi。
/// OcrInputBackend 非线程安全，所有入口用 SemaphoreSlim(1,1) 串行化。
/// 后端「惰性创建」：OcrInputBackend 构造会阻塞等待 OCR 模型加载（最长 90s），
/// 惰性化保证 MCP 握手（initialize/tools/list）即时响应，首次工具调用才付出加载代价。
/// </summary>
public sealed class WeChatToolsAdapter : IWeChatDriverApi
{
    private readonly Func<OcrInputBackend> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrInputBackend? _backend;
    private bool _disposed;

    public WeChatToolsAdapter(Func<OcrInputBackend> factory) => _factory = factory;

    private OcrInputBackend Backend => _backend ??= _factory();

    public bool IsReady
    {
        get
        {
            _gate.Wait();
            try { return EnsureSession(out _); }
            finally { _gate.Release(); }
        }
    }

    public WindowHealthReport DiagnoseWindow()
    {
        _gate.Wait();
        try { return Backend.DiagnoseWindow(); }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<ConversationInfo> ListConversations()
    {
        _gate.Wait();
        try
        {
            EnsureHealthyWindow();
            var r = Backend.EnumerateConversations();
            if (!r.Succeeded || r.Value is null)
                throw new McpToolException($"读取会话列表失败: {r.Failure?.Code} {r.Failure?.Message}");
            return r.Value
                .Select(c => new ConversationInfo(c.Key.StableHint, c.Key.DisplayName))
                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<ConversationInfo> SearchContacts(string query)
    {
        _gate.Wait();
        try
        {
            EnsureHealthyWindow();
            var r = Backend.EnumerateConversations();
            if (!r.Succeeded || r.Value is null)
                throw new McpToolException($"读取会话列表失败: {r.Failure?.Code} {r.Failure?.Message}");
            var q = (query ?? "").Trim();
            return r.Value
                .Select(c => new ConversationInfo(c.Key.StableHint, c.Key.DisplayName))
                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                .Where(c => q.Length == 0 ||
                            c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            c.StableHint.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public ReadMessagesOutcome ReadMessages(string conversation, int maxMessages)
    {
        _gate.Wait();
        try
        {
            EnsureHealthyWindow();
            var switched = Backend.SwitchConversation(conversation);
            if (!switched)
                return new ReadMessagesOutcome(false, $"未找到会话（切换失败）: {conversation}", Array.Empty<string>(), 0, 0);

            var lines = Backend.ReadChatHistory(out var stats, maxMessages);
            return new ReadMessagesOutcome(true, null, lines, stats.Scrolls, stats.OcrCalls);
        }
        catch (Exception ex)
        {
            return new ReadMessagesOutcome(false, ex.Message, Array.Empty<string>(), 0, 0);
        }
        finally { _gate.Release(); }
    }

    public OperationResult ProposeReply(string conversation, string text)
    {
        _gate.Wait();
        try
        {
            EnsureHealthyWindow();
            if (string.IsNullOrWhiteSpace(text))
                return OperationResult.Fail(FailureCode.WriteFailed, "草稿文本为空");
            var switched = Backend.SwitchConversation(conversation);
            if (!switched)
                return OperationResult.Fail(FailureCode.ConversationNotFound, $"未找到会话（切换失败）: {conversation}");
            return Backend.WriteComposer(new ConversationKey("", conversation, null), text);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.InternalError, ex.Message);
        }
        finally { _gate.Release(); }
    }

    public OperationResult SendDraft(string conversation)
    {
        _gate.Wait();
        try
        {
            EnsureHealthyWindow();
            var send = Backend.SendMessage(new ConversationKey("", conversation, null));
            if (!send.Succeeded) return send;
            Thread.Sleep(350);
            var verify = Backend.VerifySent(new ConversationKey("", conversation, null), "");
            if (!verify.Succeeded)
                return OperationResult.Fail(verify.Failure!.Code, verify.Failure.Message, verify.Failure.OperationId);
            return OperationResult.Ok(verify.Confidence);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.InternalError, ex.Message);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try { _backend?.DisposeOcr(); } // 用字段判空，避免 Dispose 触发惰性创建
        finally { _gate.Release(); }
    }

    private bool EnsureSession(out string? error)
    {
        if (Backend.Session is not null) { error = null; return true; }
        var r = Backend.EstablishSession();
        if (r.Succeeded && r.Value is not null) { error = null; return true; }
        error = r.Failure?.Message ?? "无法建立微信会话";
        return false;
    }

    private void EnsureSessionOrThrow()
    {
        if (!EnsureSession(out var error))
            throw new McpToolException(error ?? "无法建立微信会话");
    }

    /// <summary>
    /// 建立会话并自愈窗口（最小化/屏幕外自动还原+置前）。
    /// 窗口不可用时抛出明确失败码，而不是静默空返回（微信最小化到托盘时 PrintWindow 截空，OCR 空）。
    /// </summary>
    private void EnsureHealthyWindow()
    {
        EnsureSessionOrThrow();
        var report = Backend.DiagnoseWindow();
        if (report.Status == "Unusable")
            throw new McpToolException($"微信窗口不可用: {report.Detail}");
    }
}
