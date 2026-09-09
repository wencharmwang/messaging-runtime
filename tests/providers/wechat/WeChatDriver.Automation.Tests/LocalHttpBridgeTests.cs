using System.Net;
using System.Text;
using System.Text.Json;
using WeChatDriver.AgentBridge;
using WeChatDriver.Core;
using Xunit;

namespace WeChatDriver.Automation.Tests;

/// <summary>LocalHttpBridge 的 HTTP 层集成测试（仅 127.0.0.1 + 随机令牌）。</summary>
public sealed class LocalHttpBridgeTests : IDisposable
{
    private readonly LocalHttpBridge _bridge = new();
    private readonly HttpClient _http = new();

    public LocalHttpBridgeTests()
    {
        _bridge.Start("test-token");
    }

    public void Dispose()
    {
        _http.Dispose();
        _bridge.Dispose();
    }

    private string BaseUrl => $"http://127.0.0.1:{_bridge.Port}";

    private static MessageDelta MakeDelta(string text, string eventId) =>
        new(eventId, new ConversationKey("pos:3", "李女士", null), text, DateTimeOffset.Now,
            MessageSource.LocalOcr, 0.91, "fp-1");

    [Fact]
    public async Task Publish_ThenGetEvents_ReturnsEvent()
    {
        _bridge.Publish(MakeDelta("10月3日还有档期吗？", "msg_20260830114012345"));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/events?after=0");
        req.Headers.Add("X-Auth-Token", "test-token");
        using var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
        var first = doc.RootElement[0];
        Assert.Equal("new_message", first.GetProperty("type").GetString());
        Assert.Equal("10月3日还有档期吗？", first.GetProperty("text").GetString());
        Assert.Equal("local_ocr", first.GetProperty("source").GetString());
    }

    [Fact]
    public async Task GetEvents_AfterCursor_ReturnsOnlyNewer()
    {
        _bridge.Publish(MakeDelta("较早的消息", "msg_10000000000000000"));
        _bridge.Publish(MakeDelta("较新的消息", "msg_20000000000000000"));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/events?after=15000000000000000");
        req.Headers.Add("X-Auth-Token", "test-token");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("较新的消息", doc.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task PostCommand_ProposeDraft_RaisesDraftProposed()
    {
        DraftReply? raised = null;
        _bridge.DraftProposed += (_, d) => raised = d;

        const string payload =
            "{\"type\":\"propose_draft\",\"draftId\":\"draft_1\"," +
            "\"eventId\":\"msg_20260830114012345\"," +
            "\"conversation\":{\"stableHint\":\"pos:3\",\"displayName\":\"李女士\"}," +
            "\"text\":\"您好，我先为您核对档期。\"}";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/command") { Content = content };
        req.Headers.Add("X-Auth-Token", "test-token");
        using var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(raised);
        Assert.Equal("draft_1", raised!.DraftId);
        Assert.Equal("您好，我先为您核对档期。", raised.Text);
        Assert.Equal("msg_20260830114012345", raised.OriginEventId);
        Assert.Equal(DraftStatus.Proposed, raised.Status);
    }

    [Fact]
    public async Task GetEvents_WrongToken_Returns401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/events?after=0");
        req.Headers.Add("X-Auth-Token", "wrong-token");
        using var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PostCommand_UnknownType_Returns400()
    {
        using var content = new StringContent("{\"type\":\"send_now\"}", Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/command") { Content = content };
        req.Headers.Add("X-Auth-Token", "test-token");
        using var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_BeyondCap_DropsOldest()
    {
        for (var i = 0; i < LocalHttpBridge.MaxQueuedEvents + 50; i++)
            _bridge.Publish(MakeDelta($"消息 {i}", $"msg_{i}"));

        Assert.Equal(LocalHttpBridge.MaxQueuedEvents, _bridge.QueuedEvents);

        // 单次拉取上限 50 条，且最旧事件已被丢弃（msg_0 已不在队列）。
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/events?after=0");
        req.Headers.Add("X-Auth-Token", "test-token");
        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.GetArrayLength() <= 50);
    }
}
