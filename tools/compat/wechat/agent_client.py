#!/usr/bin/env python3
"""
AgentBridge 示例客户端（仅 Python 标准库，无第三方依赖）。

演示上层 Agent 如何与 WeChatDriver 的本地桥（127.0.0.1 + 随机令牌）交互：

  listen    —— 轮询 GET /events，游标递增打印新消息事件（§4.2）
  propose   —— POST /command 提交一条 propose_draft 草稿（§4.3）

用法示例：
  python tools/compat/wechat/agent_client.py --port 54321 --token <token> listen
  python tools/compat/wechat/agent_client.py --port 54321 --token <token> propose \
      --stable-hint pos:3 --display-name 李女士 --event-id msg_1 \
      --text "您好，我先为您核对档期，稍后回复。"
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request


def _request(method: str, port: int, token: str, path: str, payload: dict | None = None) -> object:
    url = f"http://127.0.0.1:{port}{path}"
    data = None
    headers = {"X-Auth-Token": token}
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            body = resp.read().decode("utf-8")
            return json.loads(body) if body else None
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {e.code}: {detail}") from e
    except urllib.error.URLError as e:
        raise RuntimeError(f"连接失败（桥是否已启动？端口/令牌是否正确？）: {e.reason}") from e


def cmd_listen(args) -> int:
    after = 0
    print(f"监听 127.0.0.1:{args.port}，游标从 {after} 开始（Ctrl+C 退出）")
    try:
        while True:
            events = _request("GET", args.port, args.token, f"/events?after={after}")
            if isinstance(events, list) and events:
                for ev in events:
                    print(json.dumps(ev, ensure_ascii=False))
                    eid = ev.get("eventId", "")
                    # 游标 = eventId 末尾时间戳（与桥端 Seq 语义一致）
                    tail = eid.rsplit("_", 1)[-1]
                    if tail.isdigit():
                        after = max(after, int(tail))
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\n已退出")
    return 0


def cmd_propose(args) -> int:
    payload = {
        "type": "propose_draft",
        "draftId": args.draft_id,
        "eventId": args.event_id,
        "conversation": {
            "stableHint": args.stable_hint,
            "displayName": args.display_name,
        },
        "text": args.text,
    }
    result = _request("POST", args.port, args.token, "/command", payload)
    print(f"提交结果: {result}")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="WeChatDriver AgentBridge 示例客户端")
    parser.add_argument("--port", type=int, required=True, help="桥端口（从 Driver 状态栏/日志读取）")
    parser.add_argument("--token", required=True, help="桥令牌（启动时生成的随机令牌）")
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_listen = sub.add_parser("listen", help="轮询拉取 new_message 事件")
    p_listen.add_argument("--interval", type=float, default=1.0, help="轮询间隔（秒）")
    p_listen.set_defaults(func=cmd_listen)

    p_propose = sub.add_parser("propose", help="提交一条草稿")
    p_propose.add_argument("--draft-id", default=f"draft_{int(time.time())}", help="草稿 ID")
    p_propose.add_argument("--event-id", required=True, help="关联的原始事件 ID")
    p_propose.add_argument("--stable-hint", required=True, help="目标会话稳定提示（如 pos:3）")
    p_propose.add_argument("--display-name", required=True, help="目标会话显示名")
    p_propose.add_argument("--text", required=True, help="草稿正文")
    p_propose.set_defaults(func=cmd_propose)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except RuntimeError as e:
        print(f"错误: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
