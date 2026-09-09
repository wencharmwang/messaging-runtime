"""Model-independent stdio MCP example. Never sends messages. Uses only the standard library."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default=str(Path.home() / '.dotnet' / 'dotnet.exe'))
    parser.add_argument('--config')
    parser.add_argument('--data-dir')
    parser.add_argument('--provider', default='wechat-main')
    parser.add_argument('--list-conversations', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    command = [args.dotnet, str(root / 'artifacts/app/messaging-runtime.dll'), '--stdio']
    if args.config:
        command.extend(['--config', str(Path(args.config).resolve())])
    if args.data_dir:
        command.extend(['--data-dir', str(Path(args.data_dir).resolve())])
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=sys.stderr, text=True, encoding='utf-8',
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    counter = 0

    def request(method, params):
        nonlocal counter
        counter += 1
        process.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': counter, 'method': method,
                                       'params': params}, ensure_ascii=False) + '\n')
        process.stdin.flush()
        line = process.stdout.readline()
        if not line:
            raise RuntimeError('Runtime closed the protocol stream; check stderr.')
        response = json.loads(line)
        if 'error' in response:
            raise RuntimeError(response['error'])
        return response['result']

    try:
        hello = request('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {},
                                       'clientInfo': {'name': 'python-agent-example', 'version': '1.0'}})
        print(json.dumps(hello, ensure_ascii=False, indent=2))
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        for name, arguments in [('list_providers', {})] + (
                [('list_conversations', {'provider': args.provider})] if args.list_conversations else []):
            result = request('tools/call', {'name': name, 'arguments': arguments})
            print(result['content'][0]['text'])
            if result.get('isError'):
                raise RuntimeError('MCP tool failed; see structured error above.')
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=10)
    if process.returncode:
        raise SystemExit(process.returncode)


if __name__ == '__main__':
    main()
