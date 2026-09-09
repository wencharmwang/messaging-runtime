namespace MessagingRuntime;

public enum SendMode { Approval, ReadOnly, AllowList, UserDirected }
public enum SendOrigin { AgentProposed, UserRequest }
public sealed record SendPermission(string AgentId, string Provider, string ConversationId);
public sealed record RuntimePolicy(SendMode SendMode = SendMode.Approval,
    IReadOnlyList<SendPermission>? AllowSend = null, int MaxTextLength = 4000)
{
    public string Evaluate(SendRequest request, SendOrigin origin = SendOrigin.AgentProposed)
    {
        if (!Enum.IsDefined(origin))
            throw new MessagingException("invalid_arguments", "Unknown send origin.");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxTextLength)
            throw new MessagingException("invalid_text", $"Text must contain 1–{MaxTextLength} characters.");
        return SendMode switch
        {
            SendMode.ReadOnly => throw new MessagingException("policy_denied", "Sending is disabled by the local runtime policy."),
            SendMode.AllowList when AllowSend?.Contains(new SendPermission(request.AgentId, request.Provider, request.ConversationId)) == true => "approved",
            SendMode.AllowList => throw new MessagingException("policy_denied", "This agent and conversation are not in the local send allow list."),
            SendMode.UserDirected when origin == SendOrigin.UserRequest => "approved",
            _ => "pending"
        };
    }
}
