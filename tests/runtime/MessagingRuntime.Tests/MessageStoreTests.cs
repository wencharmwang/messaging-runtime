using System.Text;
using System.Text.Json;
using MessagingRuntime.Storage;
using Xunit;

namespace MessagingRuntime.Tests;

public sealed class MessageStoreTests
{
    [Fact]
    public void SnapshotCursorSurvivesRefreshAndRestart_WithoutCollapsingIdenticalMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"messaging-test-{Guid.NewGuid():N}.db");
        string before;
        string snapshot;
        try
        {
            using (var store = new MessageStore(path))
            {
                var provider = new TestProvider();
                snapshot = store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500));
                Assert.Equal(snapshot, store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500)));
                var latest = store.GetPage("test-main", "a", 2);
                Assert.Equal(new[] { "مرحبا", "last" }, latest.Messages.Select(m => m.Content));
                Assert.True(latest.HasMoreBefore);
                Assert.False(latest.HasMoreAfter);
                before = latest.Before!;
                provider.Lines = ["new snapshot"];
                store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500));
            }
            using (var store = new MessageStore(path))
            {
                var older = store.GetPage("test-main", "a", 2, before: before);
                Assert.Equal(snapshot, older.SnapshotId);
                Assert.Equal(new[] { "same", "same" }, older.Messages.Select(m => m.Content));
                Assert.NotEqual(older.Messages[0].Id, older.Messages[1].Id);
                Assert.All(older.Messages, m => Assert.Null(m.Timestamp));
                Assert.Equal(new[] { "مرحبا", "last" }, store.GetPage("test-main", "a", 2, after: older.After).Messages.Select(m => m.Content));
                Assert.Equal("你好", store.GetPage("test-main", "a", 2, before: older.Before).Messages.Single().Content);
                Assert.Equal("new snapshot", store.GetPage("test-main", "a").Messages.Single().Content);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProviderRevisionInvalidatesHeadsButPreservesCursorsAndSendJournal()
    {
        using var store=new MessageStore(":memory:");
        var provider=new TestProvider();
        store.EnsureProviderRevision("test-main","old");
        store.SaveSnapshot("test-main","a",provider.GetMessages("a",500));
        store.SaveSnapshot("other","b",new([],false,"other"));
        var previous=store.GetPage("test-main","a",2);
        var request=new SendRequest(Guid.NewGuid().ToString(),"agent","test-main","a","draft");
        store.RegisterSend(request,"pending");
        store.EnsureProviderRevision("test-main","new");
        Assert.False(store.HasSnapshot("test-main","a"));
        Assert.True(store.HasSnapshot("other","b"));
        Assert.Empty(store.Search("test-main","last",null,50));
        Assert.NotEmpty(store.GetPage("test-main","a",2,before:previous.Before).Messages);
        Assert.Equal("pending",store.ReviewSend(request.RequestId).Status);
        store.SaveSnapshot("test-main","a",provider.GetMessages("a",500));
        store.EnsureProviderRevision("test-main","new");
        Assert.True(store.HasSnapshot("test-main","a"));
    }

    [Fact]
    public void CursorIsBoundToProviderAndConversation()
    {
        using var store = new MessageStore(":memory:");
        var provider = new TestProvider();
        store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500));
        var page = store.GetPage("test-main", "a", 2);
        Assert.Equal("invalid_cursor", Assert.Throws<MessagingException>(() => store.GetPage("another", "a", before: page.Before)).Code);
        Assert.Equal("invalid_cursor", Assert.Throws<MessagingException>(() => store.GetPage("test-main", "b", after: page.After)).Code);
        Assert.Equal("invalid_cursor", Assert.Throws<MessagingException>(() => store.GetPage("test-main", "a", before: "bad base64")).Code);
        var invalid = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { snapshot = page.SnapshotId, ordinal = 99 })));
        Assert.Equal("invalid_cursor", Assert.Throws<MessagingException>(() => store.GetPage("test-main", "a", before: invalid)).Code);
    }

    [Fact]
    public void EmptySnapshotsAndPageEdgesAreExplicit()
    {
        using var store = new MessageStore(":memory:");
        store.SaveSnapshot("p", "c", new([], false, "ocr_window"));
        var empty = store.GetPage("p", "c");
        Assert.Empty(empty.Messages);
        Assert.False(empty.IsComplete);
        Assert.Null(empty.After);
        Assert.False(empty.HasMoreBefore);
        Assert.False(empty.HasMoreAfter);
    }

    [Fact]
    public void SearchIsLiteralScopedAndOnlyUsesLatestSnapshot()
    {
        using var store = new MessageStore(":memory:");
        var provider = new TestProvider { Lines = ["100% literal", "你好世界", "O'Reilly"] };
        store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500));
        store.SaveSnapshot("test-main", "b", provider.GetMessages("b", 500));
        Assert.Single(store.Search("test-main", "%", "a", 50));
        Assert.Single(store.Search("test-main", "'", "a", 50));
        Assert.Equal(2, store.Search("test-main", "你好", null, 50).Count);
        Assert.Empty(store.Search("other", "你好", null, 50));
        provider.Lines = ["replacement"];
        store.SaveSnapshot("test-main", "a", provider.GetMessages("a", 500));
        Assert.Empty(store.Search("test-main", "你好", "a", 50));
    }

    [Fact]
    public void IncorrectProviderPayloadCannotPolluteAnotherConversation()
    {
        using var store = new MessageStore(":memory:");
        Assert.Equal("provider_contract_error", Assert.Throws<MessagingException>(() =>
            store.SaveSnapshot("test-main", "b", new TestProvider().GetMessages("a", 500))).Code);
        Assert.False(store.HasSnapshot("test-main", "b"));
    }
}
