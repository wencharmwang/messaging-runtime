using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeChatDriver.Core;
using WeChatDriver.Mcp;
using Xunit;

namespace WeChatDriver.Mcp.Tests;

/// <summary>MCP 核心（JSON-RPC 握手 + 工具分发）与 stdio 传输的单元测试，用假 API 隔离微信依赖。</summary>
public sealed class McpServerTests
{
    private sealed class FakeApi : IWeChatDriverApi
    {
        public List<ConversationInfo> Conversations { get; } = new()
        {
            new("pos:0", "李女士"),
            new("pos:1", "Wencharm"),
            new("pos:2", "文件传输助手")
        };

        public bool IsReady => true;

        public WindowHealthReport DiagnoseWindow() =>
            new(true, true, false, false, 1118, 809, 100, 100, 120, "Healthy", "窗口 1118x809 @ 120 DPI", Array.Empty<string>());

        public IReadOnlyList<ConversationInfo> ListConversations() => Conversations;

        public IReadOnlyList<ConversationInfo> SearchContacts(string query) =>
            Conversations.Where(c => c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        public ReadMessagesOutcome ReadMessages(string conversation, int maxMessages) =>
            new(true, null, new[] { "【对方】你好", "【我】在的，请讲" }, 2, 2);

        public OperationResult ProposeReply(string conversation, string text) =>
            string.IsNullOrWhiteSpace(text)
                ? OperationResult.Fail(FailureCode.WriteFailed, "草稿文本为空")
                : OperationResult.Ok(0.9);

        public OperationResult SendDraft(string conversation) => OperationResult.Ok(0.9);

        public void Dispose() { }
    }

    private static McpServer MakeServer(out FakeApi api)
    {
        api = new FakeApi();
        return new McpServer(api, "wechat-driver", "1.0.0");
    }

    private static JsonNode Call(McpServer server, string method, int id, JsonNode? @params = null)
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) req["params"] = @params;
        var resp = server.Handle(req.ToJsonString());
        Assert.NotNull(resp);
        return JsonNode.Parse(resp!)!;
    }

    [Fact]
    public void Initialize_ReturnsProtocolVersionAndToolsCapability()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "initialize", 1);

        Assert.Equal("2.0", resp["jsonrpc"]!.GetValue<string>());
        Assert.Equal(McpServer.ProtocolVersion, resp["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("wechat-driver", resp["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(resp["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public void ToolsList_ReturnsSixWechatTools()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "tools/list", 1);

        var tools = resp["result"]!["tools"]!.AsArray();
        Assert.Equal(6, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("wechat_diagnose", names);
        Assert.Contains("wechat_list_conversations", names);
        Assert.Contains("wechat_search_contact", names);
        Assert.Contains("wechat_read_messages", names);
        Assert.Contains("wechat_propose_reply", names);
        Assert.Contains("wechat_send_draft", names);
    }

    [Fact]
    public void ToolsCall_ListConversations_ReturnsConversations()
    {
        var server = MakeServer(out var api);
        var resp = Call(server, "tools/call", 1, new JsonObject
        {
            ["name"] = "wechat_list_conversations",
            ["arguments"] = new JsonObject()
        });

        Assert.False(resp["result"]!["isError"]!.GetValue<bool>());
        var text = resp["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("李女士", text);
        Assert.Contains("文件传输助手", text);
    }

    [Fact]
    public void ToolsCall_SearchContact_Filters()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "tools/call", 1, new JsonObject
        {
            ["name"] = "wechat_search_contact",
            ["arguments"] = new JsonObject { ["query"] = "文件" }
        });

        Assert.False(resp["result"]!["isError"]!.GetValue<bool>());
        var text = resp["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("文件传输助手", text);
        Assert.DoesNotContain("李女士", text);
    }

    [Fact]
    public void ToolsCall_ReadMessages_ReturnsMessages()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "tools/call", 1, new JsonObject
        {
            ["name"] = "wechat_read_messages",
            ["arguments"] = new JsonObject { ["conversation"] = "Wencharm", ["maxMessages"] = 30 }
        });

        Assert.False(resp["result"]!["isError"]!.GetValue<bool>());
        var text = resp["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("【对方】你好", text);
        Assert.Contains("【我】在的", text);
    }

    [Fact]
    public void ToolsCall_UnknownTool_ReturnsIsError()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "tools/call", 1, new JsonObject
        {
            ["name"] = "does_not_exist",
            ["arguments"] = new JsonObject()
        });

        Assert.True(resp["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_ProposeReply_EmptyText_IsError()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "tools/call", 1, new JsonObject
        {
            ["name"] = "wechat_propose_reply",
            ["arguments"] = new JsonObject { ["conversation"] = "Wencharm", ["text"] = "" }
        });

        Assert.True(resp["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void Notification_Initialized_ReturnsNull()
    {
        var server = MakeServer(out _);
        var resp = server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Null(resp);
    }

    [Fact]
    public void ParseError_ReturnsIdNullError()
    {
        var server = MakeServer(out _);
        var resp = server.Handle("{not valid json");
        Assert.NotNull(resp);
        var node = JsonNode.Parse(resp!);
        Assert.Equal(-32700, node!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownMethod_ReturnsError()
    {
        var server = MakeServer(out _);
        var resp = Call(server, "no/such/method", 7);
        Assert.NotNull(resp["error"]);
    }

    [Fact]
    public void StdioTransport_RoundTripsToolsList()
    {
        var server = MakeServer(out _);

        using var input = new MemoryStream(
            Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n"));
        using var output = new MemoryStream();
        using var log = new MemoryStream();

        var transport = new McpStdioTransport(server, input, output, log);
        var exit = transport.Run();

        Assert.Equal(0, exit);
        var result = Encoding.UTF8.GetString(output.ToArray()).Trim();
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("result", out var toolsProp));
        Assert.Equal(6, toolsProp.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public void StdioTransport_MultipleRequestsAndNotification()
    {
        var server = MakeServer(out _);
        var payload =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var output = new MemoryStream();
        using var log = new MemoryStream();

        var transport = new McpStdioTransport(server, input, output, log);
        transport.Run();

        var result = Encoding.UTF8.GetString(output.ToArray());
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 2 条响应（通知不产生响应）。
        Assert.Equal(2, lines.Length);
        Assert.Contains("protocolVersion", lines[0]);
        Assert.Contains("tools", lines[1]);
    }
}
