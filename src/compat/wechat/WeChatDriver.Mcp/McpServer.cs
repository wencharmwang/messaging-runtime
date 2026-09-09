using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeChatDriver.Mcp;

/// <summary>
/// 最小但合规的 MCP Server（JSON-RPC 2.0）。工具型 server 只需协议面：
/// initialize / tools/list / tools/call / ping / notifications/initialized。
/// 工具结果统一以 JSON 文本返回；业务失败以 isError=true 返回，不抛异常到传输层。
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2024-11-05";

    private readonly IWeChatDriverApi _api;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly SemaphoreSlim _toolGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public McpServer(IWeChatDriverApi api, string serverName, string serverVersion)
    {
        _api = api;
        _serverName = serverName;
        _serverVersion = serverVersion;
    }

    /// <summary>处理一条（或一批）JSON-RPC 消息，返回响应 JSON 字符串；通知返回 null。</summary>
    public string? Handle(string messageJson)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(messageJson); }
        catch { return "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32700,\"message\":\"Parse error\"}}"; }

        if (root is JsonArray batch)
        {
            var results = new JsonArray();
            foreach (var item in batch)
            {
                var resp = HandleSingle(item);
                if (resp is not null) results.Add(resp);
            }
            return results.Count == 0 ? null : results.ToJsonString();
        }

        var single = HandleSingle(root);
        return single?.ToJsonString();
    }

    private JsonObject? HandleSingle(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        var hasId = obj.ContainsKey("id") && obj["id"] is not null;
        JsonNode? id = hasId ? obj["id"]!.DeepClone() : null;
        var method = obj["method"]?.GetValue<string>() ?? "";

        if (string.IsNullOrEmpty(method))
            return hasId ? Error(id!, -32600, "Invalid Request") : null;

        try
        {
            var result = Dispatch(method, obj["params"]);
            if (result is null) return null;            // 通知（notifications/initialized 等）
            return hasId ? Ok(id!, result) : null;      // 有方法无 id 的请求视为通知
        }
        catch (McpToolException ex)
        {
            return hasId ? Error(id!, -32000, ex.Message) : null;
        }
        catch (Exception ex)
        {
            return hasId ? Error(id!, -32603, ex.Message) : null;
        }
    }

    private JsonNode? Dispatch(string method, JsonNode? @params) => method switch
    {
        "initialize" => HandleInitialize(),
        "notifications/initialized" => null,
        "ping" => new JsonObject(),
        "tools/list" => HandleToolsList(),
        "tools/call" => HandleToolsCall(@params),
        "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
        "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
        _ => throw new McpToolException($"未知方法: {method}")
    };

    private JsonObject HandleInitialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false }
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = _serverName,
            ["version"] = _serverVersion
        },
        ["instructions"] = "微信 Driver MCP：读取消息 / 列出会话 / 搜索联系人 / 草稿回复。发送需用户确认。"
    };

    private JsonObject HandleToolsList() => new() { ["tools"] = BuildToolDefinitions() };

    private JsonObject HandleToolsCall(JsonNode? @params)
    {
        if (@params is not JsonObject p)
            throw new McpToolException("tools/call 缺少参数");

        var name = p["name"]?.GetValue<string>() ?? throw new McpToolException("缺少工具名");
        var args = p["arguments"] as JsonObject ?? new JsonObject();

        _toolGate.Wait();
        try
        {
            var result = InvokeTool(name, args);
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(result, JsonOpts)
                }),
                ["isError"] = false
            };
        }
        catch (McpToolException ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts)
                }),
                ["isError"] = true
            };
        }
        finally { _toolGate.Release(); }
    }

    private object InvokeTool(string name, JsonObject args) => name switch
    {
        "wechat_diagnose" => DiagnoseTool(),
        "wechat_list_conversations" => ListConversationsTool(),
        "wechat_search_contact" => SearchContactTool(args),
        "wechat_read_messages" => ReadMessagesTool(args),
        "wechat_propose_reply" => ProposeReplyTool(args),
        "wechat_send_draft" => SendDraftTool(args),
        _ => throw new McpToolException($"未知工具: {name}")
    };

    // ---- 工具实现 ----

    private object DiagnoseTool()
    {
        var r = _api.DiagnoseWindow();
        return new
        {
            status = r.Status,
            detail = r.Detail,
            processRunning = r.ProcessRunning,
            windowFound = r.WindowFound,
            minimized = r.IsMinimized,
            offscreen = r.IsOffscreen,
            window = r.WindowFound ? new { width = r.Width, height = r.Height, x = r.X, y = r.Y, dpi = r.Dpi } : null,
            issues = r.Issues,
            note = "status=Healthy 可正常读写；Degraded 仍可只读（布局/DPI 偏离标定）；Minimized/Offscreen 已自动恢复；Unusable 表示微信未运行或主窗口不可见。"
        };
    }

    private object ListConversationsTool()
    {
        var list = _api.ListConversations();
        return new
        {
            count = list.Count,
            conversations = list.Select(c => new { name = c.DisplayName, stableHint = c.StableHint })
        };
    }

    private object SearchContactTool(JsonObject args)
    {
        var query = GetString(args, "query");
        if (query.Length == 0) throw new McpToolException("缺少 query 参数");
        var list = _api.SearchContacts(query);
        return new
        {
            query,
            count = list.Count,
            conversations = list.Select(c => new { name = c.DisplayName, stableHint = c.StableHint })
        };
    }

    private object ReadMessagesTool(JsonObject args)
    {
        var conversation = GetString(args, "conversation");
        if (conversation.Length == 0) throw new McpToolException("缺少 conversation 参数");
        var max = GetInt(args, "maxMessages", 50);
        max = Math.Clamp(max, 1, 500);

        var outcome = _api.ReadMessages(conversation, max);
        if (!outcome.Ok) throw new McpToolException(outcome.Error ?? "读取失败");

        return new
        {
            conversation,
            count = outcome.Messages.Count,
            messages = outcome.Messages,
            note = "消息按 早→晚 顺序；【我】=本人发送，【对方】=对方发送"
        };
    }

    private object ProposeReplyTool(JsonObject args)
    {
        var conversation = GetString(args, "conversation");
        if (conversation.Length == 0) throw new McpToolException("缺少 conversation 参数");
        var text = GetString(args, "text");
        if (text.Length == 0) throw new McpToolException("缺少 text 参数");

        var r = _api.ProposeReply(conversation, text);
        if (!r.Succeeded)
            throw new McpToolException($"{r.Failure?.Code}: {r.Failure?.Message}");

        return new
        {
            ok = true,
            conversation,
            draft = text,
            note = "草稿已写入微信输入框（未发送）。请用户确认后，再调用 wechat_send_draft，或由用户在微信中手动按 Enter 发送。"
        };
    }

    private object SendDraftTool(JsonObject args)
    {
        var conversation = GetString(args, "conversation");
        if (conversation.Length == 0) throw new McpToolException("缺少 conversation 参数");

        var r = _api.SendDraft(conversation);
        if (!r.Succeeded)
            throw new McpToolException($"{r.Failure?.Code}: {r.Failure?.Message}");

        return new
        {
            ok = true,
            conversation,
            note = "已发送并验证输入框已清空"
        };
    }

    // ---- 工具定义 ----

    private static JsonArray BuildToolDefinitions() => new(
        Tool("wechat_diagnose",
            "诊断微信主窗口健康状态（是否最小化/屏幕外/尺寸/DPI 偏离标定），并自动恢复（还原+置前）。用于读取前自检；当 list/search/read 异常时可先调用本工具确认窗口状态。",
            Schema(Array.Empty<string>())),
        Tool("wechat_list_conversations",
            "列出微信当前会话列表（显示名 + 稳定定位提示）。用于定位要读取或回复的会话。",
            Schema(Array.Empty<string>())),
        Tool("wechat_search_contact",
            "按名称搜索会话（大小写不敏感子串匹配）。query 为会话名关键词。",
            Schema(new[] { "query" }, new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } })),
        Tool("wechat_read_messages",
            "切换到指定会话并滚动读取聊天记录（早→晚），含【我】/【对方】方向标注。conversation 为会话名；maxMessages 为最大条数（默认 50，上限 500）。",
            Schema(new[] { "conversation" }, new JsonObject
            {
                ["conversation"] = new JsonObject { ["type"] = "string" },
                ["maxMessages"] = new JsonObject { ["type"] = "integer", ["default"] = 50 }
            })),
        Tool("wechat_propose_reply",
            "把回复草稿写入指定会话的微信输入框（只写草稿，不发送）。发送需用户确认。",
            Schema(new[] { "conversation", "text" }, new JsonObject
            {
                ["conversation"] = new JsonObject { ["type"] = "string" },
                ["text"] = new JsonObject { ["type"] = "string" }
            })),
        Tool("wechat_send_draft",
            "发送当前输入框内已写入的草稿（发送后验证输入框清空）。调用前应已得到用户确认。",
            Schema(new[] { "conversation" }, new JsonObject
            {
                ["conversation"] = new JsonObject { ["type"] = "string" }
            }))
    );

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject Schema(IReadOnlyList<string> required, JsonObject? properties = null) => new()
    {
        ["type"] = "object",
        ["properties"] = properties ?? new JsonObject(),
        ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)!).ToArray())
    };

    // ---- 参数解析 ----

    private static string GetString(JsonObject args, string key)
    {
        var n = args[key];
        if (n is null) return "";
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? "";
        return n.ToString();
    }

    private static int GetInt(JsonObject args, string key, int fallback)
    {
        var n = args[key];
        if (n is null) return fallback;
        if (n is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        return int.TryParse(n.ToString(), out var parsed) ? parsed : fallback;
    }

    // ---- 响应构造 ----

    private static JsonObject Ok(JsonNode id, JsonNode result)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result };
        o["id"] = id;
        return o;
    }

    private static JsonObject Error(JsonNode id, int code, string message)
    {
        var o = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        o["id"] = id;
        return o;
    }
}
