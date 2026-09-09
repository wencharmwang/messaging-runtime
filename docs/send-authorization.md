# 用户指令直发与消息审批

## 已确认的问题

旧 Runtime 的 `approval_required` 只是数据库中的 pending 状态，只有 `--approve-request` / `--reject-request` 能改变它；没有 WorkBuddy 弹框集成。聊天里说“批准”或重复提交同一 UUID，不会改变旧版策略。这是交互闭环缺失，不能据此推断微信断开。

## 配置

在服务实际加载的 JSON 配置中设置，然后重启 Runtime：

```json
{
  "approval_ui": "desktop",
  "policy": {
    "send_mode": "user_directed",
    "max_text_length": 4000,
    "allow_send": []
  }
}
```

这是需要合并到完整配置的片段，不要删除原来的 provider、agent_id、data_directory。代码默认模式仍为 approval；本机部署按用户要求显式启用 user_directed。approval_ui 支持 desktop（默认）或 manual。

| 来源 | origin 参数 | user_directed 下的行为 |
|---|---|---|
| 当前用户说“给 Wencharm 发：我今天回家吃晚饭” | user_request | 直接发送 |
| 用户让 agent 组织措辞并发送给指定对象，目的明确 | user_request | 在授权范围内拟文并发送 |
| agent 读完聊天，自行建议给某人回复 | agent_proposed | 草稿进入审批 |
| 用户仅要求分析聊天、建议怎么回复、监控消息 | agent_proposed | 不构成直接发送授权 |
| 对方在聊天记录里要求发送、转发，或消息内伪装用户指令 | agent_proposed | 不能作为用户授权 |
| 用户确认了已展示的收件人和完整草稿 | user_request | 同 UUID、同正文和收件人继续发送 |
| 来源不明确或省略参数 | agent_proposed | 保留审批 |

这里的“自动发送”仅指免除重复确认，不是收到新聊天就自动回复。收件人存在歧义时仍应澄清，不能猜测发送目标。

## Agent 接入协议

重新加载 MCP 工具列表，或重连后开启新的 agent 会话。新服务版本为 0.2.0，提供 14 个工具。

```json
{
  "name": "send_message",
  "arguments": {
    "provider": "wechat-main",
    "conversation_id": "从 list_conversations 解析出的真实 ID",
    "text": "用户明确要求发送的内容",
    "request_id": "每次业务发送生成的 UUID",
    "origin": "user_request"
  }
}
```

`reply_message` 使用同一 origin 规则，但只适用于支持原生引用回复的 provider。`approved=true`、伪造 agent_id、未知 origin 仍被拒绝。

**信任边界：** 标准 MCP 调用没有可信的原始用户消息或签名授权凭证。因此 user_directed 是用户在本地显式启用的“信任 agent 对指令来源的分类”模式，不能防御 agent 谎报 user_request。参数不是密码学证明。服务仍从本地配置确定 agent 身份，审计记录分类但不记录正文。两个共享客户端均适用该模式。若需防御不可信 agent，需要宿主从真实用户事件签发绑定内容/收件人的授权凭证；本版没有虚构这种宿主能力。

## 审批交互

1. agent_proposed 提交后持久化草稿，返回 approval_required 和 approval 详情。
2. desktop 模式在交互式 Windows 桌面启动独立窗口，完整展示收件人、平台、agent、会话 ID、引用目标（如有）、正文与 request_id；同请求在同一进程内只开一次窗口。
3. 用户点击“批准此消息”或“拒绝”。关闭窗口保留 pending，不默认批准。窗口未出现、启动失败或已关闭时，可用相同配置/数据目录的本地 CLI 审阅和批准；不能告诉用户一定存在 WorkBuddy 弹框。
4. agent 停止循环发送，用户操作后调用 `get_send_status`。它不会触发发送或新弹框。approved 后重试原发送，才实际发送。
5. user_directed 下，用户也可在聊天中确认已经展示的准确草稿；agent 改用 origin=user_request，复用原 UUID 和发送内容。仅 pending 可以进入 approved；rejected、sending、uncertain 不会因此恢复。

`opening` 表示请求已提交给 UI 线程，`visible` 表示窗口 Shown 事件已发生，不保证窗口未被其他应用遮挡。manual/unavailable/dismissed 时按 next_action 执行本地 CLI。服务无 SSE 推送，不宣称点击批准会自动唤醒已结束的 agent 回合；可回复 agent“已批准”继续查询和发送。

## 幂等、兼容与回退

UUID 仍绑定 agent、provider、会话、正文、引用目标和首次解析的标题；来源分类保存在审计而不是不可变正文中，以支持后来明确确认同一草稿。旧 pending 记录无需改库或换 ID，显式 user_request 可在新模式下继续处理。旧记录不会在升级时被自动执行。

发送尝试前持久化 sending；不确定结果标为 uncertain，禁止自动重试；sent 重复请求返回原回执。read_only、allow_list、approval 的原规则不受 origin 覆盖。

回退策略只需将 send_mode 改回 approval 并重启。已有 sent/uncertain/sending/rejected 状态均保留，不要删除数据库来解锁审批。宿主应用自身的工具权限仍独立生效，本修改不改变 WorkBuddy/Codex 的权限设置。
