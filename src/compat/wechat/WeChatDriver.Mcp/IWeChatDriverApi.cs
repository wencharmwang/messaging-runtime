using WeChatDriver.Core;

namespace WeChatDriver.Mcp;

/// <summary>MCP 工具返回的会话信息（仅稳定提示 + 显示名，不暴露更多本地状态）。</summary>
public sealed record ConversationInfo(string StableHint, string DisplayName);

/// <summary>读取消息结果。</summary>
public sealed record ReadMessagesOutcome(
    bool Ok,
    string? Error,
    IReadOnlyList<string> Messages,
    int Scrolls,
    int OcrCalls);

/// <summary>工具调用失败时抛出的受控异常（由 MCP 分发层捕获并转为 isError=true 结果）。</summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

/// <summary>
/// MCP 工具唯一依赖的引擎操作抽象。与具体后端解耦，便于单元测试替换。
/// 实现方必须保证线程安全（引擎非线程安全，适配器内部串行化）。
/// </summary>
public interface IWeChatDriverApi
{
    /// <summary>引擎是否已就绪（能否建立微信会话）。</summary>
    bool IsReady { get; }

    /// <summary>诊断并自愈微信窗口（最小化/屏幕外自动恢复），返回健康报告。</summary>
    WindowHealthReport DiagnoseWindow();

    /// <summary>列出当前会话列表。</summary>
    IReadOnlyList<ConversationInfo> ListConversations();

    /// <summary>按名称搜索会话（大小写不敏感子串匹配）。</summary>
    IReadOnlyList<ConversationInfo> SearchContacts(string query);

    /// <summary>切换到指定会话并滚动读取消息（早→晚）。</summary>
    ReadMessagesOutcome ReadMessages(string conversation, int maxMessages);

    /// <summary>切换到指定会话并把草稿写入微信输入框（不发送）。</summary>
    OperationResult ProposeReply(string conversation, string text);

    /// <summary>发送当前输入框内的草稿并验证（发送后输入框清空）。</summary>
    OperationResult SendDraft(string conversation);

    /// <summary>释放引擎资源（如 OCR 子进程）。</summary>
    void Dispose();
}
