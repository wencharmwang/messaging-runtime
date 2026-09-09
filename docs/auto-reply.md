# DeepSeek Harness 消息 Agent 接入

0.3.1 起，无人值守功能位于独立上层 `message-agent` 项目。它直接使用 DeepSeek Harness 原生 Web 界面、会话和工具；Runtime 提供消息 MCP、后台微信驱动、授权与幂等回执。

详见 [Message Agent 使用说明](../../message-agent/README.md)。部署入口为 `http://127.0.0.1:3080/`，原生「设置 → 消息助手」提供联系人/群组规则与启停，自动任务出现在原生会话列表中。首次打开可运行上层 `open.ps1`。

## Runtime 接口

- `observe_latest`：最新可见消息区的一次 OCR，不向上翻页、不覆盖历史缓存；上层通过稳定快照对齐发现新消息。
- `sync_conversation`：显式历史读取，可能翻页，不用于周期监听。
- `send_message`：人工明确下达的发送指令使用 `user_request`；自动任务由独立身份读取上层持久化发件箱授权。Runtime 对请求 ID、正文、目标、规则版本与有效期进行复核。

Runtime 配置中的 `auto_reply_database` 和 `auto_reply_token_file` 指向上层 `message-agent/data`。规则和任务生命周期由上层管理。旧 `tools/auto_reply` 与其安装脚本只保留作旧版回退，当前部署已停用 `Messaging Auto Reply` 计划任务，使用 `Messaging Agent` 任务。

微信当前仍基于后台截图 OCR 轮询，窗口需登录且可捕获。群组按新增文字消息处理；原生 @、成员筛选、完整历史或收件人实际送达均不在当前驱动保证内。检测不确定或人工接管时停止自动发送，避免重复回复。
