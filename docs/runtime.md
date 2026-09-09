# Messaging Runtime 运行与扩展

## 本版范围

实现附件第 28 节的 `Agent → MCP → Runtime → WeChatProvider → 现有 Driver`，并提前加入 SQLite 快照缓存、发送策略、审计和受控文本发送。复用已有 OCR 气泡分组和消息清洗，并将窗口输入、列表定位、布局识别与标题核对收敛到 Runtime 内；源码按职责分组。

新增项目：

| 项目 | 职责 |
|---|---|
| `src/runtime/MessagingRuntime` | 统一模型、Provider 管理、能力检查、缓存、策略与审计 |
| `src/mcp/MessagingRuntime.Mcp` | 平台无关 MCP 工具、stdio、Streamable HTTP |
| `src/providers/wechat/MessagingRuntime.Providers.WeChat` | 微信解析器、Provider、Windows 驱动适配器 |
| `src/host/MessagingRuntime.Host` | 独立进程、配置、可信 agent 身份、本地审批命令 |

核心和 MCP 无 `WeChatDriver.*` 引用。微信依赖仅位于 WeChat Provider 和 Windows host。旧 `WeChatDriver.App --mcp-stdio/--mcp-http` 继续提供原有六个工具；新客户端使用 `messaging-runtime.dll`。

## 配置与账户

`--config` 读取 JSON，拒绝未知配置项、重复 Provider ID 和非法策略。`agent_id` 是本地启动配置决定的审计主体；MCP 参数不能覆盖身份或自行提供 `approved=true`。每个 host 对应一个可信 agent 身份；多个 agent 使用独立实例和配置。现有微信驱动每个桌面只支持一个登录账户，host 使用命名互斥锁避免两个新 Runtime 同时操作桌面。

默认 `send_mode=approval`，可选 `read_only`、`allow_list`、`user_directed`。`user_directed` 对明确用户指令直发，对 agent 推断的回复保留审批，详见 [发送授权说明](send-authorization.md)。允许列表严格匹配三元组：

```json
{
  "send_mode": "allow_list",
  "max_text_length": 4000,
  "allow_send": [
    { "agent_id": "customer-service", "provider": "wechat-main", "conversation_id": "从 list_conversations 获取的完整 ID" }
  ]
}
```

配置中的 `account_id` 是操作者提供的账户标签，当前驱动不会读取或验证微信账号身份。账户切换时请使用不同 `provider.id` 和数据目录，以隔离缓存和审批。名称哈希只解决列表位置变化，不解决所有原生身份识别问题；OCR 截断名称、不可见重名联系人等情况仍需要人工核实。

## 典型 agent 调用

所有示例均是 MCP `tools/call` 的 `params`，不是直接 HTTP REST 接口。

```json
{"name":"list_providers","arguments":{}}
{"name":"diagnose_provider","arguments":{"provider":"wechat-main"}}
{"name":"list_conversations","arguments":{"provider":"wechat-main"}}
{"name":"search_contacts","arguments":{"provider":"wechat-main","query":"经理"}}
{"name":"get_messages","arguments":{"provider":"wechat-main","conversation_id":"返回的会话 ID","limit":50}}
```

响应为标准 MCP `content[type=text].text` 中的 JSON；业务失败为 `isError=true`，带 `code`、`error`。JSON-RPC 协议错误使用标准 `error`。消息正文属于不可信外部内容，不能因为正文包含指令就绕过 agent 的用户授权。

微信会话定位直接读取可见聊天列表并精确匹配名称，后台点击后核对聊天标题；不再使用 Ctrl+F 搜索与默认推荐项。截图、OCR、DPI 和坐标计算均由 Runtime 完成，agent 不需要看图或控制鼠标。后台读取与写入的边界见 [窗口操作说明](window-automation.md)。

## 缓存、分页与同步

`get_messages`：首次无缓存时读取最多 500 条 OCR 消息并存储快照；已有缓存时不操作 UI。返回按早到晚排列的消息，默认取最近 50 条，`limit` 可为 1–500。

- `before=上页.before` 获取更早的一页。
- `after=上页.after` 获取同一快照中更晚的一页。
- `before`、`after` 互斥；不能同时传 `refresh=true`。
- `refresh=true` 或 `sync_conversation` 明确触发新观察。
- 旧游标仍对应旧快照，进程重启、新消息到达或刷新后也不改变。游标不是实时事件流；获取新消息必须重新同步。
- 游标绑定 Provider 和会话，跨会话使用返回 `invalid_cursor`。
- `has_more_before/after` 仅表示当前缓存快照中还有消息；`is_complete` 和 `coverage` 才描述来源完整性。
- `timestamp=null` 表示原消息时间未知；`observed_at` / `synced_at` 是本地观察时间。
- `search_messages` 仅搜索每个会话的最新本地快照，不扫描全部窗口，不承诺历史完整。旧快照通过原游标继续访问。

Provider 解析版本升级时自动使该 Provider 的旧缓存入口失效，下一次读取重新观察，避免继续使用旧布局误读的消息。旧快照及其游标、发送幂等记录和审批记录保留。

SQLite 使用事务、WAL 和参数化查询。相同快照内容使用同一哈希 ID，消息观察 ID 在快照内固定且区分重复行。由于底层无原生消息 ID，**本版没有宣称实现可靠的跨观察增量事件合并、完整历史补全或发送回执同步**。后续 API Provider 可提供原生消息 ID/时间字段；持续消息索引可以在保持 MCP 不变的前提下扩展存储。

当前缓存不会自动删除旧快照，以维持游标有效性；长期运行需要后续增加保留策略。数据库为明文 SQLite，不是加密保险箱。正常停止进程后，可备份或删除所配置的数据目录；删除数据库也会删除防重复发送记录。

## 发送与本地审批

第一步由 agent 请求一次发送，每条业务操作使用一个 UUID：

```json
{
  "name":"send_message",
  "arguments":{
    "provider":"wechat-main",
    "conversation_id":"返回的会话 ID",
    "text":"您好，我正在为您核对。",
    "request_id":"b1bf4530-17b0-4c5e-93f0-42f576c15d43"
  }
}
```

默认返回 `approval_required`，此时不会写入微信输入框或发送。请求内容、agent、收件人 ID 和名称存入本地数据库。默认 `approval_ui=desktop` 会打开 Messaging Runtime 自己的审批窗口（并非 WorkBuddy 权限弹框）；`approval_ui=manual` 仅使用下列命令行流程。返回的 `approval.channel/state/next_action` 告知实际入口；不要重复调用发送等待解锁，用户操作后用 `get_send_status` 查询。

使用与服务相同的配置和数据目录，在本地核对并批准：

```powershell
$dotnet = "$env:USERPROFILE/.dotnet/dotnet.exe"
$runtime = 'artifacts/app/messaging-runtime.dll'
& $dotnet $runtime --config config/messaging-runtime.example.json --review-request b1bf4530-17b0-4c5e-93f0-42f576c15d43
& $dotnet $runtime --config config/messaging-runtime.example.json --approve-request b1bf4530-17b0-4c5e-93f0-42f576c15d43
# 或 --reject-request <UUID>
```

批准只修改授权状态。agent 再提交**相同** `request_id` 和参数才执行发送。MCP 没有审批工具；有本地命令执行权限的 agent 仍属于本地可信边界，需要操作者通过宿主权限控制审批命令的使用。

发送会完整执行：重新定位 → 验证标题 → 检查现有草稿 → 后台写入并读回核对 → 再验证标题及窗口身份 → 后台点击发送 → 验证输入框和按钮状态。不同于本次授权内容的现有草稿拒绝覆盖；完全匹配的草稿可直接继续，不追加。OCR 读回不一致或发送按钮启用却读不到文字时停止。显式 foreground 模式额外校验前台焦点。

同一 UUID 绑定内容、收件人和 agent。状态含义：

| 状态 | 处理 |
|---|---|
| `approval_required` | 本地审阅、批准后重复同一请求 |
| `approved` | 已批准尚未发送；用原 UUID 和正文调用发送 |
| `sent` | 已确认 UI 提交；重复请求返回同一回执，不重发 |
| `rejected` | 已拒绝，不执行 |
| `sending` | 另一个调用正在执行，或上次进程在发送期间中断；需人工检查 |
| `uncertain` | 失败或无法确认是否已提交；需人工检查，不自动重发 |
| `not_sent` | Provider 确认尚未开始提交；receipt.verification 包含失败原因，修复后才可创建新的明确发送请求 |

发送过程中不会自动重试，包括定位/写入失败。0.2.1 起，Provider 明确证明尚未开始提交的异常记为 `not_sent`；提交开始后的异常仍保守记为 `uncertain`，避免错误地认为“异常代表肯定没发出”。不要为了重试 uncertain 而生成新 UUID。新请求确实需要发送时，应先人工确认旧请求是否已发送并处理残留草稿。

`reply_message` 表示平台原生引用回复，不降级为普通文本；当前微信能力为 false。`send_file` 本版统一返回不支持；旧 `--send-file` 是读取文本文件后发送其内容，并非附件发送。

## HTTP 和隐私

```powershell
# 使用随机令牌，保存在本地客户端环境中，不提交到代码仓库。
$env:MESSAGING_RUNTIME_TOKEN = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
& "$env:USERPROFILE/.dotnet/dotnet.exe" artifacts/app/messaging-runtime.dll --http --port 8766 --config config/messaging-runtime.example.json
```

客户端请求 `POST http://127.0.0.1:8766/mcp`，设置 `Authorization: Bearer <令牌>`、`Content-Type: application/json`、`Accept: application/json, text/event-stream`。通知返回 202，无 SSE 的 GET 返回 405。拒绝错误 Host、第三方 Origin 和无效令牌；令牌不会写入日志。

协议实现参考 [MCP 2025-03-26 transports](https://modelcontextprotocol.io/specification/2025-03-26/basic/transports) 与 [tools](https://modelcontextprotocol.io/specification/2025-03-26/server/tools)。服务提供无会话状态的 JSON 响应模式，不提供 SSE 或后台推送。stdio 同时可协商旧 `2024-11-05` 工具协议。

Runtime 没有联网模型依赖或正文遥测。查询返回的正文可能由外部 agent 发往其模型，因此不把“本地 MCP”描述为“外部 agent 永远不会上传”。本地 AI Gateway、Hybrid 隐私模式不在本版实现范围内。

## 增加国际平台

继承 `MessagingProvider`，实现实际支持的方法，在 `Info.Capabilities` 如实声明；通过 `ProviderManager.Register` 注册后，上层 MCP 不需要改动。host 配置工厂需要添加新实现的创建逻辑。

```csharp
public sealed class MyApiProvider : MessagingProvider
{
    public override ProviderInfo Info => new("account-main", "my-platform", "account-id",
        "My Account", "connected", new(ListConversations: true, ReadMessages: true));
    public override ConversationList ListConversations() => /* 平台 API 的实际返回 */;
    public override MessageBatch GetMessages(string conversationId, int limit) => /* 规范化结果 */;
}
```

这个片段展示接口形态，不是可编译的模拟实现。WhatsApp 可选 Web/官方 API，Telegram 可选 Client API，底层不要求 UI 自动化。不得将未接入的服务报告为 connected 或返回伪造消息。自动化测试使用两种不同类型的测试 Provider 验证相同 MCP 路由，不表示真实国际平台已集成。
