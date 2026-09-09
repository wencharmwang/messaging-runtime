using System.Text.Json;
using System.Text.Json.Nodes;
using MessagingRuntime.Mcp;
using MessagingRuntime.Providers;
using MessagingRuntime.Storage;
using Xunit;

namespace MessagingRuntime.Tests;

public sealed class UserDirectedSendTests : IDisposable
{
    private readonly MessageStore _store = new(":memory:");
    private readonly ProviderManager _providers = new();
    private readonly TestProvider _provider = new();
    public UserDirectedSendTests() => _providers.Register(_provider);
    private MessagingService Runtime(SendMode mode = SendMode.UserDirected, IApprovalNotifier? notifier = null)
        => new(_providers, _store, new(mode), "agent", notifier);

    [Fact]
    public void ExplicitInstructionSendsOnceWithoutPrompt_AndAuditsOriginWithoutBody()
    {
        var prompt = new Prompt();
        var runtime = Runtime(notifier: prompt);
        var id = Guid.NewGuid().ToString();
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "PRIVATE TEXT", id, origin: SendOrigin.UserRequest).Status);
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "PRIVATE TEXT", id, origin: SendOrigin.UserRequest).Status);
        Assert.Equal("sent", runtime.GetSendStatus(id).Status);
        Assert.Equal(1, _provider.SendCalls);
        Assert.Equal(0, prompt.Calls);
        var audit = JsonSerializer.Serialize(_store.RecentAudit());
        Assert.Contains("user_request", audit);
        Assert.DoesNotContain("PRIVATE TEXT", audit);
    }

    [Fact]
    public void InferredReplyWaitsForRealDecision_StatusQueryHasNoSideEffects()
    {
        var prompt = new Prompt();
        var runtime = Runtime(notifier: prompt);
        var id = Guid.NewGuid().ToString();
        var result = runtime.SendMessage("test-main", "a", "draft", id);
        Assert.Equal("approval_required", result.Status);
        Assert.Equal("runtime_desktop", result.Approval!.Channel);
        Assert.Equal("approval_required", runtime.GetSendStatus(id).Status);
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(0, _provider.SendCalls);
        _store.DecideSend(id, true);
        Assert.Equal("approved", runtime.GetSendStatus(id).Status);
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "draft", id).Status);
        Assert.Equal(1, _provider.SendCalls);
    }

    [Fact]
    public void UserCanConfirmExistingDraftWithSameId_ButCannotChangePayload()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid().ToString();
        runtime.SendMessage("test-main", "a", "draft", id);
        Assert.Equal("idempotency_conflict", Assert.Throws<MessagingException>(() =>
            runtime.SendMessage("test-main", "a", "changed", id, origin: SendOrigin.UserRequest)).Code);
        Assert.Equal("approval_required", runtime.GetSendStatus(id).Status);
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "draft", id, origin: SendOrigin.UserRequest).Status);
        Assert.Equal(1, _provider.SendCalls);
    }

    [Fact]
    public void ExistingLegacyPendingRequestCanBeExplicitlyAuthorizedAfterModeChange()
    {
        var id = Guid.NewGuid().ToString();
        Runtime(SendMode.Approval).SendMessage("test-main", "a", "draft", id);
        Assert.Equal("sent", Runtime().SendMessage("test-main", "a", "draft", id, origin: SendOrigin.UserRequest).Status);
    }

    [Theory]
    [InlineData(SendMode.Approval, "approval_required")]
    [InlineData(SendMode.ReadOnly, "policy_denied")]
    [InlineData(SendMode.AllowList, "policy_denied")]
    public void OriginDoesNotOverrideOtherLocalPolicies(SendMode mode, string expected)
    {
        string actual;
        try { actual = Runtime(mode).SendMessage("test-main", "a", "text", Guid.NewGuid().ToString(), origin: SendOrigin.UserRequest).Status; }
        catch (MessagingException ex) { actual = ex.Code; }
        Assert.Equal(expected, actual);
        Assert.Equal(0, _provider.SendCalls);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("sending")]
    [InlineData("uncertain")]
    public void ExplicitOriginNeverRevivesTerminalOrInFlightRequests(string state)
    {
        var request = new SendRequest(Guid.NewGuid().ToString(), "agent", "test-main", "a", "text", ConversationTitle: "Alice");
        // Resolve title through the real registration path before constructing the terminal state.
        var runtime = Runtime();
        runtime.SendMessage(request.Provider, request.ConversationId, request.Text, request.RequestId);
        _store.DecideSend(request.RequestId, state != "rejected");
        if (state != "rejected") Assert.True(_store.ClaimSend(request.RequestId));
        if (state == "uncertain") _store.FinishSend(request.RequestId, state);
        Assert.Equal(state, runtime.SendMessage(request.Provider, request.ConversationId, request.Text, request.RequestId, origin: SendOrigin.UserRequest).Status);
        Assert.Equal(0, _provider.SendCalls);
    }

    [Fact]
    public void MissingOrFailedUiReturnsActionableManualInstructions()
    {
        foreach (var prompt in new IApprovalNotifier?[] { null, new Prompt { Fail = true } })
        {
            var result = Runtime(notifier: prompt).SendMessage("test-main", "a", "draft", Guid.NewGuid().ToString());
            Assert.Equal("approval_required", result.Status);
            Assert.Equal("local_cli", result.Approval!.Channel);
            Assert.Contains("--approve-request", result.Approval.NextAction);
            Assert.Contains("Stop send retries", result.Approval.NextAction);
        }
        Assert.Equal(0, _provider.SendCalls);
    }

    [Fact]
    public void StatusIsScopedToHostAgent_AndInvalidOriginCannotSend()
    {
        var id = Guid.NewGuid().ToString();
        Runtime().SendMessage("test-main", "a", "draft", id);
        var other = new MessagingService(_providers, _store, new(), "other");
        Assert.Equal("request_not_found", Assert.Throws<MessagingException>(() => other.GetSendStatus(id)).Code);
        Assert.Throws<MessagingException>(() => Runtime().SendMessage("test-main", "a", "draft", id, origin: (SendOrigin)100));
        Assert.Equal(0, _provider.SendCalls);
    }

    [Theory]
    [InlineData("user_request", "sent")]
    [InlineData("agent_proposed", "approval_required")]
    [InlineData(null, "approval_required")]
    [InlineData("auto", "invalid_arguments")]
    public void McpRoutesAndValidatesOrigin(string? origin, string expected)
    {
        var server = new MessagingMcpServer(Runtime());
        var args = new JsonObject { ["provider"] = "test-main", ["conversation_id"] = "a", ["text"] = "text", ["request_id"] = Guid.NewGuid().ToString() };
        if (origin is not null) args["origin"] = origin;
        var response = JsonNode.Parse(server.Handle(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "send_message", arguments = args } }))!)!;
        var body = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal(expected, (body["status"] ?? body["code"])!.GetValue<string>());
        Assert.Equal(expected == "sent" ? 1 : 0, _provider.SendCalls);
    }

    private sealed class Prompt : IApprovalNotifier
    {
        public int Calls;
        public bool Fail;
        public ApprovalInfo Notify(SendRequest request) { Calls++; if (Fail) throw new InvalidOperationException(); return Describe(request.RequestId); }
        public ApprovalInfo Describe(string requestId) => new("runtime_desktop", "visible", "Query get_send_status after user action.");
    }
    public void Dispose() { _providers.Dispose(); _store.Dispose(); }
}
