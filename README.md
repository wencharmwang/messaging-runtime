# Messaging Runtime

为 AI agent 提供平台无关、本地优先的消息能力。项目根目录为 `messaging-runtime/`，统一解决方案为 `MessagingRuntime.sln`。

0.3.1 新增实时监听接口 `observe_latest`，只读最新一屏，不向上扫描历史。无人值守功能已迁入独立的上层 [Message Agent](../message-agent/README.md)，直接使用 DeepSeek Harness 原生界面和完整任务能力；配置位于原生「设置 → 消息助手」。参见[接入说明](docs/auto-reply.md)。

```text
Agent → Messaging MCP → Runtime → Provider → 平台 API / 桌面驱动
                           │
                     SQLite / Policy / Audit
```

现有 Windows 微信驱动封装为 WeChat Provider；WhatsApp、Telegram、LINE、KakaoTalk 保留扩展接口，真实实现尚未接入。Runtime 不绑定模型或 agent harness。

## 目录

```text
messaging-runtime/
├── MessagingRuntime.sln
├── Directory.Build.props
├── src/
│   ├── runtime/              # 模型、存储、策略、审计、Provider 管理
│   ├── mcp/                  # 平台无关工具和传输
│   ├── host/                 # 程序入口、本地审批、配置
│   ├── providers/wechat/     # 微信 Provider + Windows 驱动依赖
│   └── compat/wechat/        # 原微信 UI、MCP、AgentBridge 等兼容入口
├── tests/                    # runtime / providers / compat 对应测试
├── config/                   # Runtime、MCP 模板和微信校准配置
├── tools/                    # 构建、启动、打包、OCR 和接入示例
├── docs/                     # 使用、验证与微信实现文档
├── runtime/                  # 便携 OCR 运行依赖
├── artifacts/                # 自动生成：bin、obj、app、packages
└── .runtime-data/            # 本机数据库和 MCP 客户端配置
```

详细职责见 [项目结构](docs/project-layout.md)。生成文件集中在 `artifacts/`，源码目录中不再保留 `bin/obj`。

## 构建和运行

需要 .NET 8 SDK。微信 Provider 需要 Windows 10/11、已登录微信和 OCR 环境；核心 Runtime/MCP 库面向 `net8.0`。

在项目根目录运行：

```powershell
# 自动寻找 PATH 或用户 .dotnet 目录中的 dotnet
.\tools\build.ps1 -Test -Publish

# 启动已发布程序；默认 stdio，可由任意 MCP agent 客户端拉起
.\tools\start-messaging-runtime.ps1 -RuntimeArgs @('--stdio', '--config', 'config/messaging-runtime.example.json')
```

也可直接使用：

```powershell
dotnet build MessagingRuntime.sln -c Release
dotnet test MessagingRuntime.sln -c Release --no-build
dotnet publish src/host/MessagingRuntime.Host -c Release -o artifacts/app
dotnet artifacts/app/messaging-runtime.dll --stdio --config config/messaging-runtime.example.json
```

脚本路径由自身所在位置推导，可从任意工作目录调用。示例配置的消息数据库写到 `.runtime-data/`；未传配置时使用系统本地应用数据目录。

## Agent 接入

生成包含本机正确绝对路径的 MCP 配置：

```powershell
.\tools\configure-mcp.ps1
```

生成文件为 `.runtime-data/mcp-client.local.json`，把它的 `mcpServers` 配置合并到客户端。项目再次移动后用 `-Force` 重新生成。版本管理中的 [配置模板](config/mcp-client.example.json) 不绑定任何用户名或机器路径。

只读握手示例：

```powershell
runtime/python/python.exe tools/messaging_agent_example.py --config config/messaging-runtime.example.json
```

默认仅检查握手和 Provider 能力，不读取聊天或发送消息。`--list-conversations` 才触发微信会话读取。

## 能力与边界

- 统一模型、14 个 MCP 工具、stdio 与本机带令牌的 HTTP 入口。
- 微信读取使用绑定 HWND/PID 的后台输入，直接点击当前聊天列表；运行时负责截图、OCR、布局和标题核对，agent 只调用语义工具。
- `diagnose_provider` 返回输入模式、目标窗口、OCR 状态与滚动观测，详见 [窗口操作说明](docs/window-automation.md)。
- SQLite 不可变快照、稳定游标分页、显式同步与本地搜索。
- 文本发送支持用户指令直发模式、独立桌面审批窗口、允许列表、持久化幂等记录和不含正文的审计。见 [发送授权说明](docs/send-authorization.md)。
- 微信 OCR 没有可靠原生消息 ID、发送时间或未读计数，结果如实返回未知状态和覆盖范围；不代表完整聊天历史。
- WhatsApp/Telegram 等尚为 `not_configured`；微信原生引用回复与附件发送尚不支持。
- 原微信应用和旧 MCP 保存在 `src/compat/wechat/`，共享更新后的底层驱动。新 Runtime 的策略仅覆盖新入口；不要同时运行多个程序操作同一个微信窗口。
- 数据库含明文消息和审批草稿；原始内容只在本机存储，但外部 agent 获取后可能交给其模型处理。

发布文件位于 `artifacts/app/`。需要含 OCR 的交付目录时运行 `tools/package.ps1`，输出到 `artifacts/packages/`。

参阅 [Runtime 使用说明](docs/runtime.md)、[验证记录](docs/verification.md) 和 [项目结构](docs/project-layout.md)。
