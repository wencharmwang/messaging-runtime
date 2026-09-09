using System.Text.Json;
using MessagingRuntime.Providers;
using MessagingRuntime.Storage;
using Xunit;

namespace MessagingRuntime.Tests;

public sealed class MessagingServiceTests
{
    [Fact]
    public void ProvenPreSubmitFailureIsNotSentWithCause_AndNeverAutomaticallyRetried()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider { FailBeforeSubmit = true };
        providers.Register(provider);
        var runtime = new MessagingService(providers, store, new(SendMode.UserDirected), "agent");
        var id = Guid.NewGuid().ToString();
        var result = runtime.SendMessage("test-main", "a", "private draft", id, origin: SendOrigin.UserRequest);
        Assert.Equal("not_sent", result.Status);
        Assert.Equal("not_submitted:conversation_unverified", result.Receipt!.Verification);
        provider.FailBeforeSubmit = false;
        Assert.Equal("not_sent", runtime.SendMessage("test-main", "a", "private draft", id, origin: SendOrigin.UserRequest).Status);
        Assert.Equal("not_sent", runtime.GetSendStatus(id).Status);
        Assert.Equal(1, provider.SendCalls);
    }

    [Fact]
    public void CachedReadsDontOperateUi_RefreshDoes_AndErrorsPreservePreviousSnapshot()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider();
        providers.Register(provider);
        var runtime = new MessagingService(providers, store, new(), "agent");
        runtime.GetMessages("test-main", "a");
        runtime.GetMessages("test-main", "a");
        Assert.Equal(1, provider.ReadCalls);
        runtime.GetMessages("test-main", "a", refresh: true);
        Assert.Equal(2, provider.ReadCalls);
        provider.FailRead = true;
        Assert.Equal("read_failed", Assert.Throws<MessagingException>(() => runtime.SyncConversation("test-main", "a")).Code);
        Assert.Equal(5, runtime.GetMessages("test-main", "a").Messages.Count);
        Assert.Equal(3, provider.ReadCalls);
    }

    [Fact]
    public void InvalidPaginationDoesNotTouchProvider()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider();
        providers.Register(provider);
        var runtime = new MessagingService(providers, store, new(), "agent");
        Assert.Throws<MessagingException>(() => runtime.GetMessages("test-main", "a", limit: 501));
        Assert.Throws<MessagingException>(() => runtime.GetMessages("test-main", "a", before: "x", refresh: true));
        Assert.Equal(0, provider.ReadCalls);
    }

    [Fact]
    public void UnreadUnknownIsNotReportedAsZero()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        providers.Register(new TestProvider());
        var runtime = new MessagingService(providers, store, new(), "agent");
        var result = runtime.GetUnreadConversations("test-main");
        Assert.Equal("a", result.Conversations.Single().Id);
        Assert.Equal("b", result.UnknownConversationIds.Single());
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void ApprovedRequestIsBoundToContentAgentRecipient_AndOnlySendsOnce()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider();
        providers.Register(provider);
        var runtime = new MessagingService(providers, store, new(), "agent");
        var requestId = Guid.NewGuid().ToString();
        Assert.Equal("approval_required", runtime.SendMessage("test-main", "a", "private body", requestId).Status);
        Assert.Equal(0, provider.SendCalls);
        store.DecideSend(requestId, true);
        Assert.Equal("idempotency_conflict", Assert.Throws<MessagingException>(() => runtime.SendMessage("test-main", "b", "private body", requestId)).Code);
        Assert.Equal("idempotency_conflict", Assert.Throws<MessagingException>(() => runtime.SendMessage("test-main", "a", "changed body", requestId)).Code);
        var otherAgent = new MessagingService(providers, store, new(), "other");
        Assert.Equal("idempotency_conflict", Assert.Throws<MessagingException>(() => otherAgent.SendMessage("test-main", "a", "private body", requestId)).Code);
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "private body", requestId).Status);
        Assert.Equal("sent", runtime.SendMessage("test-main", "a", "private body", requestId).Status);
        Assert.Equal(1, provider.SendCalls);
        var audit = JsonSerializer.Serialize(store.RecentAudit());
        Assert.DoesNotContain("private body", audit);
        Assert.DoesNotContain("changed body", audit);
        Assert.Contains("local_operator", audit);
        Assert.Contains("send_attempt", audit);
    }

    [Fact]
    public void UncertainSendsAreNeverRetried_AndDoNotLeakExceptionText()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider { FailSend = true };
        providers.Register(provider);
        var runtime = new MessagingService(providers, store, new(SendMode.AllowList, [new("agent", "test-main", "a")]), "agent");
        var id = Guid.NewGuid().ToString();
        var error = Assert.Throws<MessagingException>(() => runtime.SendMessage("test-main", "a", "SECRET BODY", id));
        Assert.Equal("send_uncertain", error.Code);
        Assert.DoesNotContain("SENSITIVE", error.Message);
        Assert.Equal("uncertain", runtime.SendMessage("test-main", "a", "SECRET BODY", id).Status);
        Assert.Equal(1, provider.SendCalls);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(store.RecentAudit()));
    }

    [Fact]
    public void ReadOnlyAndAllowListAreEnforcedBeforeAnySend()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        var provider = new TestProvider();
        providers.Register(provider);
        var readOnly = new MessagingService(providers, store, new(SendMode.ReadOnly), "agent");
        Assert.Equal("policy_denied", Assert.Throws<MessagingException>(() => readOnly.SendMessage("test-main", "a", "text", Guid.NewGuid().ToString())).Code);
        var allowList = new MessagingService(providers, store, new(SendMode.AllowList, [new("different-agent", "test-main", "a")]), "agent");
        Assert.Equal("policy_denied", Assert.Throws<MessagingException>(() => allowList.SendMessage("test-main", "a", "text", Guid.NewGuid().ToString())).Code);
        Assert.Equal(0, provider.SendCalls);
    }

    [Fact]
    public void RejectedRequestsAndInterruptedAttemptsRemainTerminalAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"messaging-send-{Guid.NewGuid():N}.db");
        var rejected = new SendRequest(Guid.NewGuid().ToString(), "agent", "p", "c", "text");
        var interrupted = rejected with { RequestId = Guid.NewGuid().ToString() };
        try
        {
            using (var store = new MessageStore(path))
            {
                store.RegisterSend(rejected, "pending");
                store.DecideSend(rejected.RequestId, false);
                store.RegisterSend(interrupted, "approved");
                Assert.True(store.ClaimSend(interrupted.RequestId));
            }
            using (var store = new MessageStore(path))
            {
                Assert.Equal("rejected", store.RegisterSend(rejected, "pending").Status);
                Assert.Equal("sending", store.RegisterSend(interrupted, "approved").Status);
                Assert.False(store.ClaimSend(interrupted.RequestId));
                Assert.Throws<MessagingException>(() => store.DecideSend(rejected.RequestId, true));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnsupportedProvidersAndNativeRepliesFailClearly()
    {
        using var store = new MessageStore(":memory:");
        using var providers = new ProviderManager();
        providers.Register(new UnconfiguredProvider("whatsapp-main", "whatsapp"));
        providers.Register(new TestProvider());
        var runtime = new MessagingService(providers, store, new(), "agent");
        Assert.Equal("not_configured", runtime.GetProvider("whatsapp-main").Status);
        Assert.Equal("unsupported_capability", Assert.Throws<MessagingException>(() => runtime.ListConversations("whatsapp-main")).Code);
        Assert.Equal("unsupported_capability", Assert.Throws<MessagingException>(() => runtime.SendMessage("test-main", "a", "text", Guid.NewGuid().ToString(), "quoted-id")).Code);
        Assert.Equal("provider_not_found", Assert.Throws<MessagingException>(() => runtime.ListConversations("missing")).Code);
        Assert.Equal("duplicate_provider", Assert.Throws<MessagingException>(() => providers.Register(new TestProvider())).Code);
    }
}
