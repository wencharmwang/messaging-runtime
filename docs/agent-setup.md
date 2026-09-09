# Codex 与 WorkBuddy 本机接入

已配置两个客户端共用一个 Messaging Runtime HTTP 服务，避免两个 stdio 进程争抢微信桌面。服务只监听 `http://127.0.0.1:8766/mcp`，使用随机 Bearer 令牌认证。

## 配置位置

- Codex：`C:\Users\Wencharm\.codex\config.toml`，`mcp_servers.messaging-runtime`。
- WorkBuddy：`C:\Users\Wencharm\.workbuddy\mcp.json`，`mcpServers.messaging-runtime`。
- 本机客户端 JSON：项目根目录下 `.runtime-data/mcp-client.local.json` 与 `.runtime-data/mcp-client.http.local.json`。
- 服务启动参数及令牌：项目根目录下 `.runtime-data/shared-service.json`。令牌仅保存在本机配置中，不要提交或分享这些文件。
- Runtime 业务配置：`config/messaging-runtime.example.json`。当前发送模式为 `user_directed`，`approval_ui=desktop`。

已有客户端配置已保留；修改前的完整备份在 `.runtime-data/agent-config-backups/20260903-105828/`。

## 启动与使用

服务由 Windows 任务计划程序中的 `Messaging Runtime` 托管。当前用户登录后自动运行，使用 Interactive 登录类型、普通用户权限，保持在微信所在的桌面会话中；任务不设置运行时限；常驻 Python 监护进程在 host 退出后等待 5 秒重启，连续失败最多重试 3 次，稳定运行 60 秒后清零失败计数。任务执行项目内 `runtime/python/pythonw.exe tools/start_shared_runtime.py --supervise`，不会弹出控制台。此前的 Startup 快捷方式已备份并移除。重复启动任务会复用正在运行的实例。

手动启动或在异常退出后恢复，在 PowerShell 执行：

```powershell
Start-ScheduledTask -TaskName 'Messaging Runtime'
Get-ScheduledTask -TaskName 'Messaging Runtime'
Get-NetTCPConnection -LocalPort 8766 -State Listen
```

重启客户端或重新加载 MCP 后开启新任务。测试提示词：

> 调用 messaging-runtime 的 list_providers，检查 wechat-main 的能力；本次只检查连接，不读取聊天，不发送消息。

要读取会话，请先登录 Windows 微信，再让 agent 调用 `connect_provider` 和 `list_conversations`。目前只有微信适配器已实现，WhatsApp、Telegram 仍是占位。

两个客户端共用 Runtime 配置的 `local-agent` 审计身份、缓存和审批记录；本版不会按 HTTP 客户端分别鉴别 agent。微信操作在 Runtime 内串行执行。需要按客户端隔离身份和权限时，应进一步实现多客户端身份路由。

发送按 runtime 的 `user_directed` 策略处理：明确用户指令使用 `origin=user_request` 直接发送；agent 自行拟定回复使用 `origin=agent_proposed`（默认），进入 Runtime 独立审批窗口。与 Codex/WorkBuddy 自身的工具执行权限分别生效。详见 [发送授权说明](send-authorization.md)。

## 故障与恢复

- 服务输出：`.runtime-data/shared-service.log`。
- 启动器错误：`.runtime-data/shared-service-launcher.log`（发生错误时创建）。
- 服务 PID 记录：`.runtime-data/shared-service.pid`，停止前应核实 PID 对应本项目的 `dotnet` 进程；PID 可能被系统复用。
- 配置或令牌修改后，需要重启服务并同步更新两个客户端。
- 如需停止服务：`Stop-ScheduledTask -TaskName 'Messaging Runtime'`；取消自动启动：`Disable-ScheduledTask -TaskName 'Messaging Runtime'`。若要完全移除此接入，还需删除两个客户端中的 `messaging-runtime` 条目并注销对应 Windows 任务。
- 此共享服务运行时，不要再以 stdio 单独启动同一微信 Runtime。

## 本次验证

2026-09-03：从 Codex 与 WorkBuddy 的实际配置读取连接参数，两个连接并发执行初始化、工具发现及 `list_providers` 均成功；各发现 12 个工具。无令牌请求返回 401。Codex CLI 已识别启用的 `streamable_http` 配置。验证未读取聊天或发送消息。客户端当前任务中的工具刷新需要重载 MCP 或开启新任务。
## 2026-09-03 连接拒绝修复

出现 `ECONNREFUSED 127.0.0.1:8766` 时，客户端配置和令牌匹配，但此前的服务进程已退出，退出原因未记录。启动方式已从一次性后台进程改为 Windows 托管：确认 supervisor 的父进程为 Windows `svchost.exe`，与微信处于同一交互会话，不依附 Codex 的临时命令进程。两客户端使用原 HTTP 地址和令牌，重新握手、发现 12 个工具并调用 `list_providers` 均通过。若客户端保留失败状态，在 MCP 设置中重连该服务。

恢复实现采用常驻监护进程：本机实测中，Windows 的 RestartOnFailure 设置没有在按需启动后按期重试，因此主要恢复逻辑由 `--supervise` 直接执行，不依赖该设置。监护进程连续恢复失败时会停止并记录错误，检查后可手动启动 Windows 任务。

恢复复测通过：受控终止空闲 host 后，常驻监护进程等待 5 秒自动创建新 host；两客户端重新握手、发现 12 个工具并调用 `list_providers` 均成功。验证未操作微信会话。记录：`.runtime-data/service-recovery-check.json`。

## 窗口操作更新（2026-09-03）

共享服务已更新为 13 个 MCP 工具，新增 `diagnose_provider`。Codex、WorkBuddy 继续使用现有地址和令牌；若工具列表尚未刷新，重新加载 MCP。读取通过 Runtime 自己的后台窗口输入、截图和 OCR 完成，调用示例和边界见 [窗口操作说明](window-automation.md)。

监督进程现使用 Windows Job Object 管理 host 及 OCR 子进程。`Stop-ScheduledTask -TaskName 'Messaging Runtime'` 会同时退出其子进程，避免停止或升级时旧 host 继续占用 8766；本机已验证停止后监听消失，重启后连接恢复。
