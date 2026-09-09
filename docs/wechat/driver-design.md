# Windows 微信专用 Driver：首版开发说明

## 0. 给开发 Agent 的执行指令

实现一个**仅面向 Windows 的本地微信辅助 Driver**。它必须运行在已登录用户的交互式桌面会话中，观察微信桌面客户端的界面变化，将新增文字消息以结构化事件交给上层 Agent，并在用户明确确认后，快速、可验证地把草稿写回正确的微信会话。

首版的成功标准不是“自动聊天很聪明”，而是：在指定 Windows 和微信版本、固定窗口布局下，能稳定地完成**增量读取 → 生成草稿 → 用户确认 → 写入/发送验证**，并把每一步延迟和失败原因记录下来。

先完成 `P0-A`（只读 + 草稿 + 性能基准），再完成 `P0-B`（显式确认后的发送）。不要跳过 P0-A 直接做自动发送。

### 严格边界

- 仅使用用户授权的 Windows UI Automation、窗口捕获、剪贴板和正常桌面输入能力。
- 不实现微信协议模拟、网络请求伪造、读取或修改微信内部数据库、进程注入、绕过登录/验证或规避平台安全机制。
- 不做无人值守自动发送。P0-B 的发送必须由本地 UI 的用户明确确认触发。
- 任何目标会话、输入内容或发送结果无法验证时，必须中止动作，保留草稿并显示失败原因；禁止猜测、重定向或盲目重试。
- 不将原始聊天内容上传到第三方诊断、遥测或日志服务。默认只保存本地、可清除的最小数据。

## 1. 支持范围

### P0 环境

- Windows 10 或 Windows 11，先在一台真实测试机锁定版本。
- 一个明确的微信 Windows 客户端版本；启动时读取其版本，版本未知时进入 `Degraded` 模式。
- 固定显示缩放（首测建议 100%）和固定微信窗口尺寸/侧栏布局。
- 单一微信进程、单一目标窗口、单一执行队列。
- 首版只支持文字消息与文字草稿，不处理图片、语音、文件、小程序卡片、红包、转账、群公告或群聊自动回复。

### P0-A：必须交付

1. 发现微信窗口并建立会话。
2. 导出、显示并保存经脱敏处理的 UI Automation 探测报告。
3. 增量发现会话列表/未读状态变化。
4. 读取最近一条文字消息；UIA 不足时仅截取聊天区局部并做本地 OCR。
5. 输出 `NewMessage` 事件给上层 Agent。
6. 本地悬浮/托盘面板展示草稿，支持编辑和复制。
7. 记录每一步的耗时、置信度和失败码。
8. 用 100 次录制样本完成性能和可靠性基准。

### P0-B：在 P0-A 通过验收后交付

1. 打开指定且已校验的会话。
2. 将草稿写入微信输入框。
3. 用户在本地面板点击“确认发送”后才发送。
4. 验证发送后的可观测结果；失败则停在草稿状态。

### 明确不做

- 全量历史聊天记录导入或长期留存。
- 自动营销群发、自动加好友、自动接受好友申请。
- 依据模型置信度自动发送。
- 多账号、多进程并行操作。
- 后台 Windows Service、Session 0、远程无人桌面控制。
- macOS、移动端和企业微信接入。

## 2. 总体架构

```text
微信 Windows 客户端
       │
       ▼
Windows 本地 Driver（每用户交互式会话）
  ├─ WindowSession：窗口发现、版本/布局指纹、状态机
  ├─ UIAObserver：UIA 事件、CacheRequest、会话/输入框读取
  ├─ CaptureFallback：聊天区局部捕获与本地 OCR
  ├─ ConversationIndex：会话状态、消息指纹、去重
  ├─ ActionExecutor：串行打开、写入、发送与验证
  ├─ Approval UI：草稿、编辑、明确确认、失败展示
  ├─ AgentBridge：本地事件/命令接口
  └─ LocalStore：SQLite + DPAPI 保护的最小状态
       │
       ▼
上层 Agent（仅接收结构化消息，返回草稿，不负责点击）
```

### 关键原则

- 上层 Agent 永远不决定坐标、按钮或页面导航。
- `ActionExecutor` 是唯一允许控制微信的组件，且必须单线程/单队列。
- UIA 是主通道；局部截图 OCR 只是读消息的回退通道。
- 截图、全树遍历和模型调用不在每次操作的热路径中。
- `ConversationIndex` 只处理变化会话，绝不周期性打开所有聊天或重读历史。

## 3. 推荐工程结构与技术选型

创建 .NET 8 解决方案，优先使用 C# 和 WPF：

```text
src/
  WeChatDriver.App/            # WPF 托盘程序、悬浮确认条
  WeChatDriver.Core/           # 领域模型、状态机、接口、失败码
  WeChatDriver.Windows/        # Win32、进程/窗口、DPI、交互会话检查
  WeChatDriver.UIA/            # UI Automation 观察、定位和 Pattern 操作
  WeChatDriver.Capture/        # Windows.Graphics.Capture/DXGI、局部 OCR
  WeChatDriver.Automation/     # 串行动作执行与发送后验证
  WeChatDriver.AgentBridge/    # 仅本机 localhost 的事件/命令桥
  WeChatDriver.Store/          # SQLite、DPAPI、日志保留和清理
  WeChatDriver.Bench/          # 录制、回放、指标汇总
tests/
  WeChatDriver.Core.Tests/
  WeChatDriver.UIA.Tests/
  WeChatDriver.Automation.Tests/
  WeChatDriver.Bench.Tests/
docs/
  locator-profiles/
  benchmarks/
```

可选依赖：

- `UIAutomationClient` / `UIAutomationTypes`：主 UIA 接口。可以使用 FlaUI 作为薄封装，但核心定位器和状态机必须由本项目控制。
- `Microsoft.Data.Sqlite`：本地状态与基准数据。
- `Windows.Graphics.Capture` 或 DXGI：局部窗口/区域捕获。不要用全屏、全窗截图作为常态。
- 本地 OCR：优先 Windows OCR 或离线 OCR 引擎；禁止为 OCR 默认上传图片。
- `cua-driver`：仅实现为可替换的 `IWindowAutomationBackend` 原型/诊断适配器。不得将 MCP 或通用 Agent 回合放到生产热路径。

## 4. 核心模型与本地接口

### 4.1 领域模型

```csharp
record WeChatWindowSession(
    int ProcessId,
    nint WindowHandle,
    string ClientVersion,
    string LocatorProfileId,
    SessionHealth Health,
    DateTimeOffset LastVerifiedAt);

record ConversationKey(string StableHint, string DisplayName, string? AvatarFingerprint);

record ConversationState(
    ConversationKey Key,
    int? UnreadCount,
    string? LastPreviewHash,
    string? LastMessageFingerprint,
    DateTimeOffset LastObservedAt,
    bool IsVisible,
    bool IsCurrent);

record MessageDelta(
    string EventId,
    ConversationKey Conversation,
    string Text,
    DateTimeOffset ObservedAt,
    MessageSource Source,       // UIA | LocalOcr
    double Confidence,
    string Fingerprint);

record DraftReply(
    string DraftId,
    ConversationKey Conversation,
    string Text,
    string OriginEventId,
    DraftStatus Status);        // Proposed | Edited | Approved | Written | Sent | Failed
```

`ConversationKey` 不得只依赖昵称；至少包含昵称、可见位置/结构信息和可选头像指纹。若不能唯一定位，返回 `AmbiguousConversation`，禁止发送。

### 4.2 Driver → Agent 事件

通过仅限 `127.0.0.1` 的本地接口或命名管道传递。连接必须使用启动时生成的本地随机令牌；不得监听局域网。

```json
{
  "type": "new_message",
  "eventId": "msg_...",
  "conversation": {
    "stableHint": "...",
    "displayName": "李女士"
  },
  "text": "10月3日还有档期吗？",
  "observedAt": "2026-08-27T20:00:00+08:00",
  "source": "uia",
  "confidence": 0.98
}
```

### 4.3 Agent → Driver 命令

```json
{
  "type": "propose_draft",
  "draftId": "draft_...",
  "eventId": "msg_...",
  "conversation": { "stableHint": "...", "displayName": "李女士" },
  "text": "您好，我先为您核对 10 月 3 日的档期，稍后回复您。"
}
```

Agent 只能提出草稿。`approve_and_send` 只允许由本地桌面 UI 触发，且 Driver 必须重新验证目标会话。

## 5. UIA 读取与缓存设计

### 5.1 会话建立

1. 找到目标微信进程和主窗口；拒绝多个候选窗口。
2. 记录可执行文件版本、窗口标题、DPI、窗口大小和 Locator Profile 指纹。
3. 使用 UIA 获取最小必要树，不要全树序列化到日志或 Agent。
4. 探测下列对象并输出诊断报告：会话列表、会话行、未读标记、聊天标题、消息区域、输入框、发送控件。
5. 缺少任何关键对象时，标记能力矩阵，不要伪造“支持”。

### 5.2 CacheRequest

订阅/读取时只缓存需要的属性与模式：

- `NameProperty`、`AutomationIdProperty`、`ControlTypeProperty`
- `BoundingRectangleProperty`、`IsOffscreenProperty`、`IsEnabledProperty`
- `HasKeyboardFocusProperty`、`ValueProperty`、`SelectionProperty`
- `ValuePattern`、`InvokePattern`、`SelectionItemPattern`、`ScrollPattern`

优先监听目标窗口或会话列表的 `StructureChanged`、名称/值变化和选择变化事件。微信不暴露可靠事件时，使用低频轻量轮询会话列表的可见行指纹；禁止全窗口截图轮询。

### 5.3 增量消息读取

```text
会话列表变化
  → 识别有变化的会话行
  → 读取该行的标题/未读/摘要
  → 与 ConversationIndex 对比
  → 仅对变化会话读取最新可见文字
  → 生成 fingerprint 并去重
  → 发送 MessageDelta
```

消息指纹建议为：`conversationStableHint + direction + normalizedText + visibleOrder + observedMinute` 的哈希。它用于去重，不是永久用户标识。

短时间连续消息做 800–1200 ms 防抖：合并为一次 Agent 调用，但原始 `MessageDelta` 均留在本地内存队列中。

### 5.4 局部截图/OCR 回退

仅在以下条件同时满足时触发：

1. 会话变化已被可靠发现；
2. UIA 没有可用的最新文字；
3. Locator Profile 已验证聊天区边界；
4. 截图范围被限制为聊天区底部的候选消息区域。

OCR 输出必须携带置信度。低于阈值或无法判定消息方向时，生成 `NeedsReview`，不让 Agent 生成自动草稿。

## 6. 动作执行状态机

所有动作通过一个串行 `ActionExecutor` 执行。每条命令有唯一 `operationId`、不可重入锁和超时。

```text
Idle
  → ValidateSession
  → ResolveConversation
  → VerifyConversationTitle
  → FocusComposer
  → WriteDraft
  → VerifyDraft
  → AwaitUserApproval
  → Send
  → VerifySend
  → Succeeded | Failed | NeedsForeground | AmbiguousConversation
```

### 6.1 目标会话定位

定位优先级：

1. 当前已经打开且标题/指纹匹配的会话；
2. `ConversationIndex` 中仍可见且可被 UIA 唯一定位的会话行；
3. 微信自身搜索框中输入明确的会话名，再以标题/额外指纹验证；
4. 失败或重名：`AmbiguousConversation`。

不得凭模糊昵称、上一次坐标或模型猜测选择会话。

### 6.2 写入和发送

写入优先级：

1. 对已验证的输入控件执行 `ValuePattern.SetValue` 或同等一次性 UIA 写入。
2. 若不支持，建立真实输入焦点，写入系统剪贴板，再使用一次 `Ctrl+V`。
3. 若仍不可验证，返回 `ComposeUnsupported`。

发送前要求：

- 本地 UI 的显式确认；
- 当前聊天标题再次匹配；
- 输入框内容哈希与草稿文本哈希匹配；
- 本次操作未超时且窗口布局指纹未变化。

发送键/按钮不能硬编码为 Enter。先探测发送控件和当前用户的微信发送快捷键配置；配置未知时，只写入草稿，不发送。

发送后验证优先顺序：

1. 输入框清空且聊天区可观察到对应的己方消息；
2. 输入框清空，且发送控件/状态发生可验证变化；
3. 无法验证：`SendUnverified`，在 UI 中明确提示，禁止自动二次发送。

## 7. Locator Profile

每个受支持微信版本维护独立 Profile，存放在 `docs/locator-profiles/`。Profile 含：

```json
{
  "profileId": "wechat-win-<version>-dpi100-layout1",
  "clientVersion": "<exact version>",
  "dpi": 100,
  "windowBounds": { "width": 1280, "height": 800 },
  "requiredCapabilities": ["conversation_list", "chat_title", "composer"],
  "uiaSelectors": {},
  "fallbackRegions": {
    "latestMessageArea": { "x": 0.35, "y": 0.55, "width": 0.60, "height": 0.38 }
  },
  "sendStrategy": "unknown"
}
```

Profile 不通过启动自检就不能进入发送状态。窗口尺寸或 DPI 改变时，立即使 Profile 失效，回到只读/草稿模式。

## 8. 本地 UI

P0 只需一个托盘程序和轻量确认窗口：

```text
李女士 · 新消息
10 月 3 日还有档期吗？

[可编辑草稿文本框]

[复制] [写入微信] [确认发送] [转人工] [忽略]
```

- `确认发送` 在 P0-A 隐藏或禁用。
- 显示当前 Driver 状态：`Healthy`、`Degraded`、`NeedsForeground`、`Blocked`。
- 每次写入/发送展示目标会话、操作时间、校验结果和失败码。
- 默认不显示完整历史聊天记录。

## 9. 性能预算与指标

将时间分段记录，禁止只记录端到端总耗时：

```text
t_event_detected
t_uia_read_done
t_ocr_done (optional)
t_delta_emitted
t_draft_received
t_conversation_resolved
t_composer_focused
t_draft_written
t_send_verified
```

首个验收目标（均为同一固定测试环境内的目标，不含模型生成时间）：

| 指标 | 目标 |
|---|---:|
| UIA 热路径读取 P50 / P95 | ≤ 250 ms / ≤ 700 ms |
| 已缓存会话定位并写入草稿 P50 / P95 | ≤ 700 ms / ≤ 1.5 s |
| OCR 回退触发率 | 单独报告，不掩盖在均值中 |
| 错会话写入 | 0 |
| 未验证发送后自动重试 | 0 |

性能日志只保存耗时、失败码、哈希和能力状态；原文文本默认不写入性能日志。

## 10. 基准与测试

### 10.1 100 次真实路径基准

准备一组经用户授权、可删除的测试会话。运行 100 次：

1. 新文字消息到达；
2. Driver 发现并输出 `MessageDelta`；
3. 注入固定草稿；
4. 打开/验证目标会话；
5. 写入草稿；
6. 在 P0-B 中由测试人员确认发送；
7. 校验并输出结果。

输出 `docs/benchmarks/<timestamp>.json` 与 Markdown 汇总，至少包含 P50、P95、错误类别、OCR 触发率、UIA 能力矩阵和微信/Windows/DPI/窗口布局版本。

### 10.2 必测失败场景

- 微信未启动、已退出、锁屏、最小化、窗口被关闭。
- 多个微信窗口或多账号候选。
- 会话重名。
- 窗口尺寸、DPI、客户端版本、侧栏布局改变。
- UIA 树为空、控件不可用或回调未到达。
- 输入框不可写、粘贴失败、发送快捷键未知。
- 用户在 Driver 操作期间手动切换会话。
- Agent 返回过长文本、空文本或不匹配的 `eventId`。
- 发送后无法验证、网络异常或微信弹出错误提示。

所有失败必须产生结构化失败码，而不是仅抛异常。

## 11. cua-driver 的使用边界

可以用 cua-driver 完成两件事：

1. 在 Windows 测试机上导出 UIA/DXGI 能力、录制轨迹并建立基准；
2. 实现 `IWindowAutomationBackend` 的可替换适配器，比较它与直接 UIA 实现的效果。

不可做：

- 生产环境每一条消息都让 MCP Agent 调用 cua-driver 截图、思考、点击、再截图。
- 将 cua-driver 的通用工具调用顺序视为微信专用 Driver 的热路径。
- 以其成功返回代替本项目的会话/草稿/发送后验证。

先用它做测量；只有其直接调用的性能与稳定性符合本文件第 9 节，才保留为生产后端候选。

## 12. 分阶段交付顺序

1. 建立解决方案、托盘程序、日志、状态机与 Fake UIA Backend。
2. 完成真实微信的窗口发现和只读 UIA 探测报告。
3. 完成 ConversationIndex、增量读取、去重与 `NewMessage` 本地事件。
4. 完成草稿确认 UI 与 AgentBridge；此时仍不写入微信。
5. 实现 `WriteDraft` 与输入框校验，完成 100 次只写入基准。
6. 实现显式确认的发送及发送后验证，完成 100 次 P0-B 基准。
7. 仅在 UIA 无法读取消息正文时加入局部截图/OCR 回退。

每完成一步，先补自动化测试、失败码和基准数据，再进入下一步。

## 13. 最终交付物

- 可构建的 .NET 8 源码与解决方案文件。
- Windows 托盘程序，可在指定测试环境运行。
- 目标微信版本的 Locator Profile 与 UIA 探测报告。
- 本地 AgentBridge 协议说明及示例客户端。
- 100 次基准原始数据和汇总报告。
- 自动化测试、手工测试清单、已知限制与回滚/禁用发送说明。
- 一份运行手册：依赖、权限、固定布局要求、如何清除本地数据、如何在版本不匹配时恢复只读模式。

## 14. 开始开发前的必答问题

开发 Agent 在写发送逻辑前必须用真实 Windows 测试机回答：

1. 目标微信版本中，会话列表、聊天标题、输入框和发送控件分别暴露了哪些 UIA 属性/Pattern？
2. UIA 是否能可靠读取新增文字消息？如果不能，局部 OCR 的准确率是多少？
3. `ValuePattern` 是否能一次性写入输入框？若不能，剪贴板粘贴的成功率是多少？
4. 在不抢占前台的情况下，写入和发送分别能否成功？若不能，系统应如何提示用户切到前台？
5. 固定环境中 100 次操作的 P50/P95 和错会话率是多少？

在这些问题有实测答案之前，不宣称支持自动回复，也不扩展 CRM、任务提醒或垂直行业工作流。
