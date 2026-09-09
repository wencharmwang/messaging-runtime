using WeChatDriver.Core;

namespace WeChatDriver.Capture;

/// <summary>局部截图 + 本地 OCR 回退。仅在 UIA 无法读取正文时触发（规范第 5.4 节，分阶段第 7 步）。</summary>
public interface ICaptureFallback
{
    /// <summary>
    /// 读取聊天区底部最新文字。返回的 MessageSource 应为 LocalOcr。
    /// 低于阈值或无法判定方向时必须返回 NeedsReview，禁止生成自动草稿。
    /// </summary>
    OperationResult<MessageDelta> ReadLatestMessage(WeChatWindowSession session, ConversationKey conversation);
}

/// <summary>未实现回退：始终返回 NeedsReview。OCR 在 UIA 主通道验收后实现。</summary>
public sealed class NotImplementedCaptureFallback : ICaptureFallback
{
    public OperationResult<MessageDelta> ReadLatestMessage(WeChatWindowSession session, ConversationKey conversation) =>
        OperationResult<MessageDelta>.Fail(FailureCode.NeedsReview, "OCR 回退尚未启用", confidence: 0.0);
}
