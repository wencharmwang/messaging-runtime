"""Observe authorized conversations and dispatch durable jobs through a single worker."""
import json
from pathlib import Path
import threading
import time
import unicodedata
import urllib.request
from .database import Store
from .harness import Harness


class Mcp:
    def __init__(self, settings_file, token_file):
        settings = json.loads(Path(settings_file).read_text(encoding='utf-8-sig'))
        self.url = f"http://127.0.0.1:{settings['port']}/mcp"
        self.token = Path(token_file).read_text().strip()
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def call(self, name, arguments):
        body = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'tools/call',
                           'params': {'name': name, 'arguments': arguments}}, ensure_ascii=False).encode('utf-8')
        request = urllib.request.Request(self.url, body, headers={'Authorization': 'Bearer ' + self.token,
                        'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
        with self.opener.open(request, timeout=90) as response:
            envelope = json.load(response)
        if 'error' in envelope:
            raise RuntimeError('MCP protocol error')
        result = envelope['result']
        data = json.loads(result['content'][0]['text'])
        if result.get('isError'):
            raise RuntimeError('Runtime: ' + data.get('code', 'tool_failed'))
        return data

    def observe(self, rule):
        page = self.call('observe_latest', {'provider': rule['provider'], 'conversation_id': rule['conversation'], 'limit': 30})
        return canonical_messages(page['messages'])


def canonical_messages(messages):
    result = []
    for m in messages:
        if m.get('direction') not in ('incoming', 'outgoing') or not isinstance(m.get('content'), str):
            raise ValueError('Message direction or content is unknown')
        # Observation IDs/timestamps vary between OCR snapshots and cannot identify events.
        result.append({'direction': m['direction'], 'content': unicodedata.normalize('NFC', m['content']).strip(),
                       'sender': m.get('sender'), 'type': m.get('type', 'unknown')})
    return result


def new_messages(previous, current):
    """Align an unambiguous exact tail. An OCR edit is uncertainty, not a new event."""
    if current == previous:
        return []
    if not previous:
        return current
    if not current:
        raise ValueError('Conversation became empty; cannot establish continuity')
    minimum = min(2, len(previous))
    for length in range(min(len(previous), len(current)), minimum - 1, -1):
        tail = previous[-length:]
        matches = [i for i in range(len(current) - length + 1) if current[i:i+length] == tail]
        if len(matches) > 1:
            raise ValueError('Repeated messages make the snapshot alignment ambiguous')
        if len(matches) == 1:
            return current[matches[0] + length:]
    raise ValueError('OCR changed or history shifted without sufficient context')


class Worker:
    def __init__(self, config, mcp=None, harness=None):
        self.config = config
        self.store = Store(config['database'])
        self.mcp = mcp or Mcp(config['runtime_settings_file'], config['worker_token_file'])
        self.harness = harness or Harness(config)
        self.stop = threading.Event()
        self.last_error = ''
        self.started_at = time.time()
        self.store.recover()

    def check_rule(self, rule):
        current = self.store.rule(rule['id'])
        return current['enabled'] and current['revision'] == rule['revision'] and current['status'] not in ('blocked', 'needs_user')

    def poll(self, rule):
        messages = self.mcp.observe(rule)
        changed = self.store.observation(rule, messages, self.config['settle_seconds'])
        if changed is None:
            return
        previous, current = changed
        appended = new_messages(previous, current)
        if any(m['direction'] == 'outgoing' for m in appended):
            self.store.status(rule['id'], rule['revision'], 'needs_user', '发现人工发出的消息，已暂停自动回复；重新启用可恢复')
            return
        incoming = [m for m in appended if m['direction'] == 'incoming' and m['type'] == 'text' and m['content']]
        if not incoming:
            self.store.replace_baseline(rule, current)
            return
        # Non-text messages are never interpreted as instructions to the model.
        self.store.enqueue(rule, current, incoming, self.config['job_ttl_seconds'])

    def process(self, job):
        rule = self.store.rule(job['rule_id'])
        if not self.check_rule(rule) or rule['revision'] != job['rule_revision']:
            self.store.finish(job, 'cancelled', 'Rule disabled or changed')
            return
        if job['expires_at'] <= time.time():
            self.store.finish(job, 'expired', 'Reply task expired')
            return
        if time.time() - rule['last_sent'] < self.config['min_reply_interval_seconds']:
            return
        if not self.store.transition(job['id'], 'queued', 'generating'):
            return
        self.store.status(rule['id'], rule['revision'], 'generating', 'DeepSeek Harness 正在处理新消息')
        try:
            try:
                decision = self.harness.run(rule, job)
            finally:
                self.harness.close()
            if not self.check_rule(rule):
                self.store.finish(job, 'cancelled', 'Rule changed during generation')
                return
            if decision['action'] != 'reply':
                self.store.finish(job, decision['action'])
                self.store.status(rule['id'], rule['revision'], 'needs_user' if decision['action'] == 'needs_user' else 'listening',
                                  'Agent 请求人工处理' if decision['action'] == 'needs_user' else '')
                return
            # Reobserve before creating outbox: do not send a response to stale conversation state.
            observed = self.mcp.observe(rule)
            before = json.loads(job['context'])['messages']
            appended = new_messages(before, observed)
            if appended:
                self.store.finish(job, 'superseded', 'Conversation changed while Agent was responding')
                if any(m['direction'] == 'outgoing' for m in appended):
                    self.store.status(rule['id'], rule['revision'], 'needs_user', '人工已接管会话')
                return
            if not self.store.transition(job['id'], 'generating', 'ready', text=decision['text'], decision=json.dumps(decision, ensure_ascii=False)):
                return
            if not self.store.claim_send(job):
                self.store.finish(job, 'cancelled', 'Rule changed or task expired before submission')
                return
            self.store.status(rule['id'], rule['revision'], 'sending', '正在后台发送并验证')
            try:
                outcome = self.mcp.call('send_message', {'provider': job['provider'], 'conversation_id': job['conversation'],
                         'text': decision['text'], 'request_id': job['request_id'], 'origin': 'agent_proposed'})
            except Exception:
                # The call may have reached the UI. Querying status is safe; resending is not.
                try:
                    outcome = self.mcp.call('get_send_status', {'request_id': job['request_id']})
                except Exception:
                    outcome = {'status': 'uncertain'}
            state = outcome.get('status', 'uncertain')
            if state != 'sent':
                self.store.finish(job, state, 'Sending stopped; inspect Runtime receipt before retrying')
                self.store.status(rule['id'], rule['revision'], 'blocked', '发送未确认：' + state + '；不会自动重发')
                return
            self.store.finish(job, 'sent', 'UI submission verified; recipient delivery not verified')
            # Exact expected append preserves any later incoming messages for the next observation.
            own = {'direction': 'outgoing', 'content': unicodedata.normalize('NFC', decision['text']).strip(), 'sender': None, 'type': 'text'}
            self.store.replace_baseline(rule, observed + [own])
            self.store.status(rule['id'], rule['revision'], 'listening', '已提交回复，继续监听')
        except Exception as error:
            self.harness.close()
            current = self.store.job(job['id'])
            if current['state'] not in ('cancelled', 'sent'):
                self.store.finish(job, 'needs_user', safe_error(error))
                self.store.status(rule['id'], rule['revision'], 'blocked', safe_error(error))

    def tick(self):
        for rule in self.store.rules():
            if self.stop.is_set():
                return
            if not rule['enabled'] or rule['status'] in ('blocked', 'needs_user'):
                continue
            try:
                self.poll(rule)
            except (ValueError, KeyError) as error:
                self.store.status(rule['id'], rule['revision'], 'blocked', safe_error(error))
            except Exception as error:
                self.store.status(rule['id'], rule['revision'], 'waiting_connection', safe_error(error))
        for job in self.store.pending():
            if self.stop.is_set():
                return
            self.process(job)

    def run(self):
        try:
            while not self.stop.is_set():
                try:
                    self.tick()
                    self.last_error = ''
                except Exception as error:
                    self.last_error = safe_error(error)
                self.stop.wait(self.config['poll_interval_seconds'])
        finally:
            self.harness.close()


def safe_error(error):
    if isinstance(error, (ValueError, RuntimeError, TimeoutError)):
        # Do not expose arbitrary wire bodies, API credentials, or model output in status.
        message = str(error)
        if 'sk-' not in message and len(message) <= 300:
            return message
    return type(error).__name__ + ': 操作失败，请检查连接、配置或执行日志'
