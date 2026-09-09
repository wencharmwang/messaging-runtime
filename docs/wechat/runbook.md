# Windows 微信驱动运行说明

微信实现位于 `src/providers/wechat/`。常规 agent 接入使用项目主入口，见 [Runtime 使用说明](../runtime.md)。本页仅保留底层诊断和旧程序兼容入口。

## 环境

- Windows 10/11 交互式桌面，微信已登录。
- 现有驱动主要针对微信 4.1.13.12、DPI 120、约 1118×809 的默认布局验证；版本或布局改变需要重新验证。
- OCR 优先使用项目 `runtime/python/` 中的 RapidOCR；`tools/ocr_server.py` 提供本机 OCR 服务。
- 剪贴板和输入操作属于微信适配器内部能力。不要同时运行多个程序操作同一个微信窗口。
- 校准参考位于 `config/wechat/locator-profiles/`，底层设计见 [driver-design.md](driver-design.md)。

## 兼容程序

在项目根目录运行：

```powershell
dotnet run --project src/compat/wechat/WeChatDriver.App
# 旧 MCP（保留原有 wechat_* 工具）
dotnet artifacts/bin/WeChatDriver.App/Release/net8.0-windows10.0.19041.0/WeChatDriver.App.dll --mcp-stdio
```

旧界面、MCP 和 AgentBridge 保留原有安全行为，不受新 Runtime 的审批数据库约束。新集成优先使用 `artifacts/app/messaging-runtime.dll`。

## 诊断输出

```powershell
New-Item -ItemType Directory -Force artifacts/wechat | Out-Null
dotnet run --project src/compat/wechat/WeChatDriver.App -- --probe-only --out artifacts/wechat/probe-report.json
dotnet run --project src/compat/wechat/WeChatDriver.App -- --capture artifacts/wechat/capture.png
```

截图、消息导出和测试输出属于本机诊断数据，应放到 `artifacts/wechat/`，不再写入文档目录或源码目录。历史截图、聊天导出和中间报告已在目录迁移时清理。

旧 AgentBridge 协议见 [agentbridge-protocol.md](agentbridge-protocol.md)，客户端为 `tools/compat/wechat/agent_client.py`。新 MCP 接入与本地发送审批见 [Runtime 使用说明](../runtime.md)。
