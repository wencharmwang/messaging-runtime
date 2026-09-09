# AgentBridge 本地协议说明 · 与示例客户端

> 对应 MVP §4.2 / §4.3 / §13。实现位于 `src/compat/wechat/WeChatDriver.AgentBridge/LocalHttpBridge.cs`，示例客户端 `tools/compat/wechat/agent_client.py`。

## 1. 定位与安全边界

AgentBridge 是 Driver 与上层 Agent 之间**仅限本机**的事件/命令桥：

- 只监听 `127.0.0.1`，**不监听局域网**。
- 启动时生成**一次性随机令牌**（`Guid.NewGuid().ToString("N")`），每个请求都必须携带，否则返回 `401`。
- 端口由系统随机分配（`TcpListener(IPAddress.Loopback, 0)`），桥启动后从日志/状态栏读取 `BridgePort` 与 `BridgeToken`。
- 上层 Agent **只能提出草稿**（`propose_draft`），**不能**直接触发发送；发送必须由本地面板用户显式确认（`approve_and_send` 只在 Driver 内部由 UI 触发）。

## 2. 认证

所有请求都必须带 HTTP 头：

```
X-Auth-Token: <BridgeToken>
```

- 缺失或不匹配 → `401 {"error":"unauthorized"}`。
- 令牌只在本次进程生命周期内有效；进程重启后令牌变化。

## 3. 端点

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `/events?after=<seq>` | 拉取 `seq` 之后的新事件（每次最多 50 条） |
| `POST` | `/command` | 发送 `propose_draft` 命令 |

### 3.1 事件拉取 `GET /events`

- `after` 为游标，语义是「事件序号」；未传视为 `0`。
- 事件序号取自 `eventId` 末尾的时间戳（`msg_20260830114012345` → seq `20260830114012345`），单调递增。
- 返回 JSON 数组，元素即 §4.2 事件结构（见下）。

示例请求：

```bash
curl -H "X-Auth-Token: <token>" "http://127.0.0.1:<port>/events?after=0"
```

示例响应：

```json
[
  {
    "type": "new_message",
    "eventId": "msg_20260830114012345",
    "conversation": {
      "stableHint": "pos:3",
      "displayName": "李女士"
    },
    "text": "10月3日还有档期吗？",
    "observedAt": "2026-08-30T11:40:12.345+08:00",
    "source": "local_ocr",
    "confidence": 0.91
  }
]
```

### 3.2 命令提交 `POST /command`

请求体（§4.3）：

```json
{
  "type": "propose_draft",
  "draftId": "draft_...",
  "eventId": "msg_20260830114012345",
  "conversation": { "stableHint": "pos:3", "displayName": "李女士" },
  "text": "您好，我先为您核对 10 月 3 日的档期，稍后回复您。"
}
```

- `type` 只接受 `propose_draft`，其余返回 `400 {"error":"bad_command"}`。
- 成功返回 `200 {"ok":true}`；Driver 内部将草稿升为 `DraftStatus.Proposed` 并触发 `DraftProposed` 事件（由本地面板接管后续流程）。

示例请求：

```bash
curl -X POST -H "X-Auth-Token: <token>" \
     -H "Content-Type: application/json" \
     -d '{"type":"propose_draft","draftId":"draft_1","eventId":"msg_1","conversation":{"stableHint":"pos:3","displayName":"李女士"},"text":"您好，我先为您核对档期。"}' \
     "http://127.0.0.1:<port>/command"
```

## 4. 数据结构（与 §4.2/§4.3 对齐）

### 4.1 Driver → Agent 事件（`new_message`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `type` | string | 固定 `"new_message"` |
| `eventId` | string | 事件唯一 ID，`msg_<yyyyMMddHHmmssfff>` |
| `conversation.stableHint` | string | 稳定定位提示（如 `pos:3`），禁止仅依赖昵称 |
| `conversation.displayName` | string | 会话显示名 |
| `text` | string | 去重后的消息正文（可能含 `[音频 N秒]` 等标注） |
| `observedAt` | string(ISO 8601) | 观测时间 |
| `source` | string | `"uia"` 或 `"local_ocr"`（微信 4.x 恒为 `local_ocr`） |
| `confidence` | double | OCR 置信度 0~1 |

### 4.2 Agent → Driver 命令（`propose_draft`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `type` | string | 固定 `"propose_draft"` |
| `draftId` | string | 草稿 ID |
| `eventId` | string | 关联的原始事件 ID |
| `conversation.stableHint` | string | 目标会话稳定提示 |
| `conversation.displayName` | string | 目标会话显示名 |
| `text` | string | 草稿正文 |

## 5. 示例客户端

见 `tools/compat/wechat/agent_client.py`（Python 标准库实现，无第三方依赖）：

```bash
# 1) 只拉事件（游标从 0 开始，循环打印新消息）
python tools/compat/wechat/agent_client.py --port <port> --token <token> listen

# 2) 提交一条草稿
python tools/compat/wechat/agent_client.py --port <port> --token <token> propose \
    --stable-hint pos:3 --display-name 李女士 --event-id msg_1 --text "您好，稍后回复。"
```

## 6. 已知限制（诚实记录）

1. 事件队列为**进程内存队列**（`ConcurrentQueue`），不落盘、无持久化；Driver 重启后历史事件丢失。
2. `/events` 只按 `eventId` 末尾时间戳做游标分页，**不消费/弹出**已读事件；长时间运行队列会持续增长（P0 可接受，后续可改为环形缓冲）。
3. 桥依赖 `HttpListener`，需要当前 Windows 用户具备监听 localhost 的权限（本机已验证可行）。
4. 桥仅在 GUI 宿主模式（无 `--*` 命令行参数的托盘程序）下随 `DriverHost.Initialize()` 启动；命令行诊断子命令（`--bench` 等）不会启动桥。
