#!/usr/bin/env python3
"""
MCP stdio 冒烟测试：拉起 WeChatDriver.App.exe --mcp-stdio，验证 JSON-RPC 握手 + tools/list。

用法:
  python tools/compat/wechat/mcp_smoke_test.py <WeChatDriver.App.exe 路径> [--with-call]

  --with-call  额外调用 wechat_list_conversations（需要微信已登录在跑，且会加载 OCR 模型，较慢）。
"""
import json
import os
import subprocess
import sys


def main() -> int:
    args = [a for a in sys.argv[1:]]
    with_call = "--with-call" in args
    args = [a for a in args if a != "--with-call"]

    if not args:
        print("用法: python tools/compat/wechat/mcp_smoke_test.py <WeChatDriver.App.exe> [--with-call]")
        return 2
    exe = args[0]
    if not os.path.exists(exe):
        print(f"找不到 exe: {exe}")
        return 2

    proc = subprocess.Popen(
        [exe, "--mcp-stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=os.path.dirname(exe),
    )

    def send(obj):
        proc.stdin.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
        proc.stdin.flush()

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                     "clientInfo": {"name": "smoke", "version": "0"}}})
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    if with_call:
        send({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
              "params": {"name": "wechat_list_conversations", "arguments": {}}})

    proc.stdin.close()

    try:
        out = proc.stdout.read().decode("utf-8", errors="replace")
        err = proc.stderr.read().decode("utf-8", errors="replace")
        proc.wait(timeout=180)
    except subprocess.TimeoutExpired:
        proc.kill()
        print("超时：进程未在 180s 内退出（可能 OCR 模型加载中）。")
        return 1

    print("=== stdout（逐行 JSON-RPC 响应）===")
    got = 0
    for line in out.strip().splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
            print(json.dumps(obj, ensure_ascii=False, indent=2))
            got += 1
        except Exception:
            print(line)
    if err.strip():
        print("=== stderr ===")
        print(err[:3000])

    print(f"\n收到 {got} 条响应。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
