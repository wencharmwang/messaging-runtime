# 项目结构与迁移

根目录已调整为 `messaging-runtime/`，解决方案改名为 `MessagingRuntime.sln`。源码按职责组织；微信模块保留既有程序集名称和命名空间，以保持兼容。

| 路径 | 内容 |
|---|---|
| `src/runtime/MessagingRuntime` | 统一模型、SQLite 快照、策略、审计与 Provider 管理 |
| `src/mcp/MessagingRuntime.Mcp` | 独立于平台的 MCP 协议、工具和传输 |
| `src/host/MessagingRuntime.Host` | Windows 主程序、配置与审批命令 |
| `src/providers/wechat/MessagingRuntime.Providers.WeChat` | 微信适配器与解析器 |
| `src/providers/wechat/windows/` | 现有 Core、Windows、UIA、Capture、Automation 五个底层项目 |
| `src/compat/wechat/` | 原 App、AgentBridge、Mcp、Store、Bench 五个兼容项目 |
| `tests/runtime/` | 核心和协议测试 |
| `tests/providers/wechat/` | 微信适配器和底层驱动测试 |
| `tests/compat/wechat/` | 旧协议和基准模块测试 |
| `config/wechat/locator-profiles/` | 保留的微信版本/DPI 校准资料 |
| `runtime/python/` | 保留一份便携 OCR 依赖及其许可证 |

国际平台实现以后放在 `src/providers/whatsapp/`、`src/providers/telegram/` 等同级目录，并通过 `MessagingProvider` 接口接入。无需把它们放进微信目录。

构建产物全部集中到 `artifacts/bin/`、`artifacts/obj/`、`artifacts/app/`。本机消息、审批和客户端配置放在 `.runtime-data/`。这两个目录均被版本控制忽略。清理源码时不要删除正在使用的消息库或审批幂等记录。

新根目录未迁入重复分发目录、旧发布产物、旧构建缓存、临时诊断脚本、聊天导出/总结、逐屏截图、历史基准输出、临时日志和旧工具会话记录。只保留必需源码、测试、运行依赖、校准资料和可复用文档。原有 smoke 数据库只有工具发现的测试审计记录，也未迁入新项目。

迁移时逐文件校验保留文件的 SHA-256，重写全部项目引用后重新生成解决方案，并在新根目录完成编译、测试和启动验证。统计见 [migration-summary.json](migration-summary.json)。

用户明确授权后，旧源码、运行环境副本、dist、历史文档及全部旧构建产物已删除，两个已确认的孤立 OCR 进程（14112、16316）已关闭。新项目文件已完成迁移。旧文件夹内容已清空，仅因当前任务占用而暂时无法移除空目录；关闭使用旧目录的任务后可删除空目录。
