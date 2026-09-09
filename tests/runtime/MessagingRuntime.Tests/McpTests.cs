using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagingRuntime.Mcp;
using MessagingRuntime.Providers;
using MessagingRuntime.Storage;
using Xunit;

namespace MessagingRuntime.Tests;

public sealed class McpTests : IDisposable
{
    private readonly MessageStore _store = new(":memory:");
    private readonly ProviderManager _providers = new();
    private readonly MessagingMcpServer _server;
    private readonly TestProvider _wechat = new("wechat-test", "wechat");

    public McpTests()
    {
        _providers.Register(_wechat);
        _providers.Register(new TestProvider("api-test", "telegram"));
        _server = new(new MessagingService(_providers, _store, new(), "trusted-host-agent"));
    }
    private JsonNode Call(string method, object? parameters = null)
    {
        var text = _server.Handle(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }));
        return JsonNode.Parse(text!)!;
    }
    private JsonNode Tool(string name, object args) => Call("tools/call", new { name, arguments = args });
    private static JsonNode Body(JsonNode response) => JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

    [Fact]
    public void HandshakeAndSemanticToolDiscoveryDoNotLoadPlatformDriver()
    {
        var response = Call("initialize", new { protocolVersion = "2025-03-26" });
        Assert.Equal("messaging-runtime", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(MessagingMcpServer.ProtocolVersion, response["result"]!["protocolVersion"]!.GetValue<string>());
        var tools = Call("tools/list")["result"]!["tools"]!.AsArray();
        Assert.Equal(15, tools.Count);
        Assert.Contains(tools,t=>t!["name"]!.GetValue<string>()=="diagnose_provider");
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("get_messages", names);
        Assert.Contains("observe_latest", names);
        Assert.Contains("get_unread_conversations", names);
        Assert.DoesNotContain("click", names);
        Assert.DoesNotContain("approve_request", names);
        Assert.All(tools, t => Assert.False(t!["inputSchema"]!["additionalProperties"]!.GetValue<bool>()));
        Assert.Equal(0, _wechat.ReadCalls);
    }

    [Theory]
    [InlineData("wechat-test")]
    [InlineData("api-test")]
    public void SameMcpContractRoutesToDifferentProviderTypes(string provider)
    {
        var response = Tool("get_messages", new { provider, conversation_id = "a", limit = 2 });
        Assert.False(response["result"]!["isError"]!.GetValue<bool>());
        var body = Body(response);
        Assert.Equal(2, body["messages"]!.AsArray().Count);
        Assert.Equal(provider, body["messages"]![0]!["provider"]!.GetValue<string>());
        Assert.Null(body["messages"]![0]!["timestamp"]);
        Assert.NotNull(body["snapshot_id"]);
    }

    [Theory]
    [InlineData("{\"provider\":\"wechat-test\",\"conversation_id\":\"a\",\"limit\":\"5\"}")]
    [InlineData("{\"provider\":\"wechat-test\",\"conversation_id\":\"a\",\"limit\":501}")]
    [InlineData("{\"provider\":\"wechat-test\",\"conversation_id\":\"a\",\"refresh\":null}")]
    [InlineData("{\"provider\":\"wechat-test\",\"conversation_id\":\"a\",\"agent_id\":\"admin\"}")]
    [InlineData("{\"provider\":\"wechat-test\"}")]
    public void BadArgumentsAreToolErrorsWithNoUiEffects(string arguments)
    {
        var response = Tool("get_messages", JsonNode.Parse(arguments)!);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", Body(response)["code"]!.GetValue<string>());
        Assert.Equal(0, _wechat.ReadCalls);
    }

    [Fact]
    public void AgentCannotSelfApproveOrSpoofIdentity()
    {
        var response = Tool("send_message", new { provider = "wechat-test", conversation_id = "a", text = "text", request_id = Guid.NewGuid().ToString(), approved = true });
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var normal = Tool("send_message", new { provider = "wechat-test", conversation_id = "a", text = "text", request_id = Guid.NewGuid().ToString() });
        Assert.Equal("approval_required", Body(normal)["status"]!.GetValue<string>());
        Assert.Equal(0, _wechat.SendCalls);
        Assert.Equal("trusted-host-agent", _store.RecentAudit().First().AgentId);
    }

    [Theory]
    [InlineData("{bad", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("null", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":42}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"unknown\"}", -32601)]
    public void MalformedRpcIsContained(string request, int code)
    { Assert.Equal(code, JsonNode.Parse(_server.Handle(request)!)!["error"]!["code"]!.GetValue<int>()); }

    [Fact]
    public void NotificationsNeverExecuteTools_AndStdioContainsOnlyRpcLines()
    {
        var notification = "{\"jsonrpc\":\"2.0\",\"method\":\"tools/call\",\"params\":{\"name\":\"get_messages\",\"arguments\":{\"provider\":\"wechat-test\",\"conversation_id\":\"a\"}}}";
        Assert.Null(_server.Handle(notification));
        Assert.Equal(0, _wechat.ReadCalls);
        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n" + notification + "\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
        using var output = new StringWriter();
        MessagingTransports.RunStdio(_server, input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal("2.0", JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>()));
    }

    [Fact]
    public async Task HttpRequiresTokenAndTrustedOrigin_AndUsesCorrectNotificationAndGetStatus()
    {
        var token = new string('x', 40);
        var app = MessagingTransports.CreateHttp(_server, token);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var unauth = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            using var crossOrigin = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            using var get = await client.GetAsync("/mcp");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            using var initialized = await client.PostAsync("/mcp", new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);
            Assert.Equal("", await initialized.Content.ReadAsStringAsync());
            using var ping = await client.PostAsync("/mcp", new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
            Assert.NotNull(JsonNode.Parse(await ping.Content.ReadAsStringAsync())!["result"]);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task PrivateWorkerTokenUsesItsOwnAuthorization_WithoutChangingInteractivePolicy()
    {
        var gate = new object();
        var normal = new MessagingMcpServer(new MessagingService(_providers, _store, new(), "interactive", operationGate: gate));
        var worker = new MessagingMcpServer(new MessagingService(_providers, _store, new(SendMode.ReadOnly), "auto-reply",
            sendAuthorization: request => request.Text == "permitted" ? "approved" : throw new MessagingException("policy_denied", "No outbox"), operationGate: gate));
        var token = new string('n', 40);
        var workerToken = new string('w', 40);
        await using var app = MessagingTransports.CreateHttp(normal, token, workerServer: worker, workerToken: workerToken);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        async Task<JsonNode> Send(string credential, string text, string origin)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "send_message", arguments = new {
                    provider = "wechat-test", conversation_id = "a", text, origin, request_id = Guid.NewGuid().ToString() } }
            }), Encoding.UTF8, "application/json"));
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        try
        {
            Assert.Equal("approval_required", Body(await Send(token, "permitted", "agent_proposed"))["status"]!.GetValue<string>());
            Assert.Equal(0, _wechat.SendCalls);
            Assert.Equal("sent", Body(await Send(workerToken, "permitted", "agent_proposed"))["status"]!.GetValue<string>());
            Assert.Equal("policy_denied", Body(await Send(workerToken, "unapproved", "user_request"))["code"]!.GetValue<string>());
            Assert.Equal(1, _wechat.SendCalls);
        }
        finally { await app.StopAsync(); }
    }

    public void Dispose() { _providers.Dispose(); _store.Dispose(); }
}
