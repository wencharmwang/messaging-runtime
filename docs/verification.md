# Messaging Runtime 0.1 交付记录

日期：2026-09-03。工作目录：`C:/Users/Wencharm/Documents/projects/messaging-runtime`。

## 已交付

- 平台无关 Conversation / Contact / Message / Attachment / Provider 模型。
- 可注册多 Provider 的 Runtime；Windows WeChat 使用原有 OCR 驱动，MCP 与核心无 Windows/微信依赖。
- 12 个业务语义 MCP 工具，stdio 和带本机令牌验证的 HTTP 入口。
- SQLite 不可变快照、重复快照去重、前后分页、缓存查询和显式刷新。
- 本地发送策略、含收件人名称与正文的待审批请求、本地批准/拒绝命令。
- 持久化幂等记录、发送前定位/核对、失败禁止自动重发、默认不记录正文的审计。
- WhatsApp / Telegram / LINE / KakaoTalk 的 Provider 扩展位置，未接入时如实返回 `not_configured`。
- 本机客户端配置、PowerShell 启动脚本、标准库 Python agent 接入示例。
- 原有微信应用、MCP、AgentBridge 和命令行保留。旧驱动只新增独立标题校验和前台窗口判断供新适配器使用。

## 验证

最终执行：

```powershell
& "$env:USERPROFILE/.dotnet/dotnet.exe" build MessagingRuntime.sln -c Release --no-restore --verbosity quiet
& "$env:USERPROFILE/.dotnet/dotnet.exe" test MessagingRuntime.sln -c Release --no-restore --verbosity quiet
& "$env:USERPROFILE/.dotnet/dotnet.exe" publish src/host/MessagingRuntime.Host -c Release --no-restore -o artifacts/app --verbosity quiet
runtime/python/python.exe tools/messaging_agent_example.py --config config/messaging-runtime.example.json --data-dir .runtime-data/smoke
```

| 测试项目 | 通过 |
|---|---:|
| WeChatDriver.Core.Tests | 44 |
| WeChatDriver.UIA.Tests | 4 |
| WeChatDriver.Automation.Tests | 11 |
| WeChatDriver.Mcp.Tests | 12 |
| WeChatDriver.Bench.Tests | 3 |
| MessagingRuntime.Tests | 30 |
| MessagingRuntime.Providers.WeChat.Tests | 6 |
| 合计 | **110** |

构建零警告、零错误。HTTP 测试在允许本机监听的沙箱外运行；原有 HttpListener 测试在沙箱内曾因系统限制失败，沙箱外已全部通过。

新增测试验证：不同类型 Provider 共享相同 MCP；中文/阿拉伯语和重复消息；游标隔离、刷新和重启后分页；缓存读取不重复操作 UI；未知未读状态；本地审批及主体/内容/收件人绑定；发送幂等、不确定结果和中断恢复；审计不记录消息/异常正文；严格参数验证；通知不执行工具；HTTP 令牌/Origin 和协议状态码；微信会话顺序变化、重名拒绝及能力声明。

已发布至 `artifacts/app/`。Python 示例实际启动已发布的独立进程，成功完成 MCP 握手和 Provider 发现；PowerShell 启动脚本的帮助入口正常退出。发布产物依赖 .NET 8 Core、Windows Desktop、ASP.NET Core runtime，本机 SDK 已包含这些运行库。

## 尚未验证或后续范围

本轮发送测试使用模拟 Provider，没有向真实微信联系人发送消息。新加的标题/草稿核对和发送流程尚未在真实微信窗口完成端到端验收，需要在目标微信版本、DPI 与布局下使用操作者指定的测试会话验证。

国际平台 API/Web 的真实接入、微信可靠未读计数、原生引用回复/附件发送、跨快照可靠增量事件合并、完整历史回填、本地 AI Gateway、后台同步与缓存保留策略属于后续工作。现有 OCR 对重复消息、媒体和发送者的识别局限继续明确保留在 `coverage` 与文档中，不以模拟能力代替。

## 目录迁移验证

项目迁移后，在新的 messaging-runtime 根目录重新生成了 MessagingRuntime.sln（21 个项目），重写了所有项目引用。重新构建零警告、零错误，110 项测试全部通过。

已在新目录完成发布程序的独立 MCP 握手，并用迁移后的便携 Python 成功导入 RapidOCR 和 ONNX Runtime。源码目录没有 bin/obj，输出统一位于 artifacts/。本机 MCP 配置由 tools/configure-mcp.ps1 自动生成到 .runtime-data/mcp-client.local.json。

旧目录中的两个孤立 OCR 服务属于 2026-09-02 的已退出父进程。用户明确授权后，已再次检查进程身份并关闭 PID 14112、16316，全部旧项目内容已删除。新项目独立运行不依赖旧目录。旧文件夹目前为空，因当前任务占用暂时不能移除该空目录。
## 窗口绑定与后台读取更新（2026-09-03）

最新版本使用 13 个 MCP 工具，新增运行时诊断。微信切换会话改为直接读取可见聊天列表、后台点击并核对标题；截图与 OCR 全部由 Runtime 完成。自动语音转写点击已移除。兼容入口仍共享这些底层驱动改进，新 Runtime 独有的策略与存储不覆盖旧入口。

新增回归覆盖 HWND 复用、窗口最小化、失焦中止、遮挡、尺寸/DPI 变化、同名与长名称列表、多尺寸布局、标题 OCR 缓存失效与本地识别回退、解析版本升级保留旧游标及发送记录。最新总数 130：Core 44、UIA 4、Automation 30、旧 MCP 12、Bench 3、Runtime 31、WeChat Provider 6。

真实后台读取与滚动的证据见 [窗口操作说明](window-automation.md)。发送路径增加输入前窗口归属和会话核对，但未执行真实发送验收。服务停止现会清理其 host/OCR 子进程，已实测停止后释放 HTTP 端口并成功重新启动。
