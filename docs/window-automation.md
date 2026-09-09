# 微信窗口操作与运行时诊断

## 调用方式

Agent 只调用消息语义工具。微信的窗口发现、截图、OCR、列表定位、点击、滚动和结果核对都由本机 Runtime 完成；无需 agent 提供截图、OCR 结果或屏幕坐标。

```json
{"name":"connect_provider","arguments":{"provider":"wechat-main"}}
{"name":"list_conversations","arguments":{"provider":"wechat-main"}}
{"name":"sync_conversation","arguments":{"provider":"wechat-main","conversation_id":"列表返回的 ID","limit":20}}
{"name":"diagnose_provider","arguments":{"provider":"wechat-main"}}
```

当前提供 13 个 MCP 工具。`get_messages` 默认读本地缓存；需要实际观察当前微信时调用 `sync_conversation`，或不带游标的 `get_messages(refresh=true)`。

## 会话定位

每次重新识别可见聊天列表，保存名称及当前行坐标，拒绝同名歧义。文件传输助手也通过列表进入，不使用 Ctrl+F、搜索推荐项或默认 Enter。点击后必须在运行时捕获的聊天标题中找到精确名称，后续每屏读取及滚动前后继续检查目标。

标题使用本地 RapidOCR 多尺度识别，并在可用时使用 Windows OCR 补充小字号英文；系统未安装英文 OCR 时可使用用户语言 OCR。标题缓存仅复用完全相同的标题像素。不会用 rn/m 等近似字符替换来放宽收件人验证。标题被截断或始终无法精确识别时，明确失败。

本版只支持当前可见列表中的会话；没有实现自动遍历整个列表或通讯录。目标不可见、名称重复时先让操作者调整列表或备注，再重试。`search_contacts` 是在已观察的会话名称中筛选，并不打开微信搜索界面。

## 后台读取

- 通过微信进程与主窗口联合定位，绑定 HWND/PID；每个输入事件检查窗口归属、可见性、最小化状态、尺寸和 DPI。
- 使用窗口局部物理像素，动态识别侧栏、标题、消息区、输入框边界，修复宿主 DPI 虚拟化导致侧栏混入正文的问题。
- `PrintWindow` 捕获目标窗口；点击、滚动使用定向窗口消息并验证接收控件归属。读取不激活窗口、不移动系统鼠标、不注入全局键盘或滚轮事件。
- 点击后的标题必须匹配；滚动后的画面变化计入诊断。API 接受窗口消息不等于已经滚动成功，画面稳定也可能表示到达边界，因此仍以有限 OCR 覆盖范围返回，不声称完整历史。
- 失败时返回结构化错误；不自动退回 agent 截图或全局输入。OCR 失败不再被转换成“空消息列表”。读取不会根据正文中的“转文字”等字符串点击消息。

设计参考 [CUA Windows 驱动说明](https://github.com/trycua/cua/blob/main/libs/cua-driver/rust/Skills/cua-driver/WINDOWS.md) 的窗口绑定、显式后台能力与结果验证思路。本项目独立实现 C# Win32 驱动，没有引入 CUA 服务或将电脑操作移交给 agent。CUA 本身也有平台和控件限制，不能据此假定所有应用支持后台输入。

## 写入与限制

0.2.2 起默认后台写入和发送：窗口绑定字符输入、OCR 全文读回、后台点击观测到的发送按钮。前台可以保持为 agent。保留 `wechat_write_mode=foreground` 作为显式备选模式，详见 [后台发送](wechat/background-send.md)。两种模式均保留发送策略、精确目标校验和幂等记录，提交结果不确定时不自动重发。

微信必须已登录、处于当前用户的交互桌面、窗口已还原。后台读取允许其他窗口在前台，但不保证最小化、锁屏、远程桌面断开或所有微信版本可用。当前布局针对微信 4.x 的浅色三栏窗口；其他主题、紧凑布局或标题无法辨认时应报告不支持，不猜测坐标。

## 诊断

`diagnose_provider` 返回 Runtime 自己观察到的状态：

| 字段 | 含义 |
|---|---|
| `process_id` / `window_handle` | 绑定的目标进程和窗口 |
| `foreground` | 微信此刻是否为前台 |
| `read_input_mode` | 当前读取路径：`background_window_messages` |
| `navigation` | 当前定位路径：`visible_list_click` |
| `dpi` / `window_width` / `window_height` | 物理尺寸与缩放 |
| `list_region` / `header_region` / `message_region` | Runtime 识别的区域 |
| `ocr_available` / `ocr_engine` / `header_fallback` | 本地 OCR 状态 |
| `current_header` | 多个本地识别候选，用精确匹配核对 |
| `last_read` | 最近成功读取的屏数、滚动次数、画面变化次数与观察条数；换会话或读取失败前清空 |

`last_read.message_count` 是清洗后观察条数；返回给调用方的条数仍受请求 limit 约束。诊断不包含消息正文或截图。

| 错误码 | 处理 |
|---|---|
| `conversation_not_found` / `conversation_not_visible` | 目标必须在当前可见聊天列表内，重新列出会话 |
| `ambiguous_conversation` | 同名会话无法唯一定位，先修改备注 |
| `background_unavailable` | 后台点击未得到目标标题或窗口拒绝事件；不执行全局输入兜底 |
| `conversation_changed` | 读取中会话发生变化，停止后重新同步 |
| `window_changed` / `window_unavailable` | 窗口归属变化、隐藏或最小化；恢复微信，必要时重启 Runtime 重新绑定 |
| `layout_changed` / `layout_unverified` | 尺寸、DPI 或布局变化，重新观察；不支持的布局需适配 |
| `foreground_lost` / `window_occluded` | 写入路径失焦或被遮挡，停止输入 |
| `capture_failed` / `ocr_failed` / `ocr_unavailable` | 检查本地捕获和 OCR 依赖，不能解释成空聊天 |

## 本机验证（2026-09-03）

Windows 微信 4.1.13.12，120 DPI，物理窗口 1089×962。通过实际 WorkBuddy MCP 配置调用共享服务，运行时读取前三个会话：文件传输助手、微信团队各返回 0 条文字，Wencharm 返回 20 条。非空会话读取 6 屏、向上滚动 5 次，记录到 6 次内容画面变化（含向下定位）。每个读取调用前后，前台窗口 HWND 与鼠标坐标均相同，微信始终不在前台。

整个验收没有使用 agent 截图、agent OCR 或 agent 鼠标控制，也没有发送消息。机器上的原始结构化验证记录为 `.runtime-data/window-safety-live.json`，不含消息正文。以上是本机版本的证据，不是对所有窗口状态的保证。
