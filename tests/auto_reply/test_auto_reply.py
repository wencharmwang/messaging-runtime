import json
from pathlib import Path
import tempfile
import time
import unittest
from tools.auto_reply.database import Store
from tools.auto_reply.harness import validate_decision, Harness
from tools.auto_reply.service import Worker, new_messages


def message(text, direction='incoming'):
    return {'direction': direction, 'content': text, 'sender': None, 'type': 'text'}


class FakeMcp:
    def __init__(self):
        self.messages = [message('较早消息'), message('历史回复', 'outgoing')]
        self.sends = []
        self.outcome = 'sent'

    def observe(self, rule):
        return list(self.messages)

    def call(self, name, args):
        if name == 'send_message':
            self.sends.append(args)
            if self.outcome == 'sent':
                self.messages.append(message(args['text'], 'outgoing'))
            return {'status': self.outcome}
        raise AssertionError(name)


class FakeHarness:
    def __init__(self):
        self.calls = []
        self.hook = lambda: None

    def run(self, rule, job):
        self.calls.append(job)
        self.hook()
        return {'job_id': job['id'], 'event_id': job['event_id'], 'action': 'reply', 'text': '收到你的消息了。'}

    def close(self):
        pass


class ReplyFlowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.config = {'database': str(Path(self.temp.name) / 'reply.db'), 'settle_seconds': 0,
                       'job_ttl_seconds': 180, 'min_reply_interval_seconds': 0, 'poll_interval_seconds': 2}
        self.mcp, self.agent = FakeMcp(), FakeHarness()
        self.worker = Worker(self.config, self.mcp, self.agent)
        self.rule_id = self.worker.store.add('wechat-main', 'conversation', '测试会话', 'direct', '自然简短地回复')
        self.worker.store.enable(self.rule_id, True)
        self.worker.tick()

    def tearDown(self):
        self.temp.cleanup()

    def incoming(self, text='你好'):
        self.mcp.messages.append(message(text))
        self.worker.tick()
        self.worker.tick()

    def test_new_message_runs_agent_and_sends_once_not_history_or_echo(self):
        self.assertEqual([], self.mcp.sends)
        self.incoming()
        for _ in range(4):
            self.worker.tick()
        self.assertEqual(1, len(self.mcp.sends))
        self.assertEqual('agent_proposed', self.mcp.sends[0]['origin'])
        self.assertEqual('sent', self.worker.store.recent()[0]['state'])
        self.incoming('你好')  # Same text in a later position is a distinct event.
        self.assertEqual(2, len(self.mcp.sends))

    def test_group_rule_uses_same_scoped_pipeline(self):
        with self.worker.store.connect() as db:
            db.execute("UPDATE reply_rules SET kind='group' WHERE id=?", (self.rule_id,))
        self.incoming('群里的新消息')
        self.assertEqual(1, len(self.mcp.sends))

    def test_pause_during_generation_prevents_send(self):
        self.agent.hook = lambda: self.worker.store.enable(self.rule_id, False)
        self.incoming()
        self.assertEqual([], self.mcp.sends)
        self.assertEqual('cancelled', self.worker.store.recent()[0]['state'])

    def test_new_incoming_during_generation_supersedes_old_reply(self):
        self.agent.hook = lambda: self.mcp.messages.append(message('还有一件事'))
        self.incoming()
        self.assertEqual([], self.mcp.sends)
        self.assertEqual('superseded', self.worker.store.recent()[0]['state'])

    def test_manual_send_pauses_conversation(self):
        self.mcp.messages.append(message('我自己回复了', 'outgoing'))
        self.worker.tick()
        self.worker.tick()
        self.assertEqual('needs_user', self.worker.store.rule(self.rule_id)['status'])
        self.assertEqual([], self.agent.calls)

    def test_ocr_rewrite_is_not_new_message(self):
        self.mcp.messages[-1] = message('历史回夏', 'outgoing')
        self.worker.tick()
        self.worker.tick()
        self.assertEqual('blocked', self.worker.store.rule(self.rule_id)['status'])
        self.assertEqual([], self.mcp.sends)

    def test_uncertain_send_is_never_repeated(self):
        self.mcp.outcome = 'uncertain'
        self.incoming()
        for _ in range(4):
            self.worker.tick()
        self.assertEqual(1, len(self.mcp.sends))
        self.assertEqual('blocked', self.worker.store.rule(self.rule_id)['status'])

    def test_restart_preserves_baseline_and_does_not_resend(self):
        self.incoming()
        restarted = Worker(self.config, self.mcp, self.agent)
        restarted.tick()
        restarted.tick()
        self.assertEqual(1, len(self.mcp.sends))

    def test_crash_during_submission_blocks_rule(self):
        rule = self.worker.store.rule(self.rule_id)
        job = self.worker.store.enqueue(rule, self.mcp.messages, [message('你好')], 180)
        self.worker.store.transition(job['id'], 'queued', 'sending')
        restarted = Worker(self.config, self.mcp, self.agent)
        restarted.tick()
        self.assertEqual('uncertain', restarted.store.job(job['id'])['state'])
        self.assertEqual('blocked', restarted.store.rule(self.rule_id)['status'])
        self.assertEqual([], self.mcp.sends)

    def test_double_claim_and_revocation(self):
        rule = self.worker.store.rule(self.rule_id)
        job = self.worker.store.enqueue(rule, self.mcp.messages, [message('你好')], 180)
        self.worker.store.transition(job['id'], 'queued', 'ready', text='回复')
        self.assertTrue(self.worker.store.claim_send(job))
        self.assertFalse(self.worker.store.claim_send(job))

    def test_expired_job_does_not_run_model(self):
        job = self.worker.store.enqueue(self.worker.store.rule(self.rule_id), self.mcp.messages, [message('你好')], -1)
        self.worker.process(job)
        self.assertEqual('expired', self.worker.store.job(job['id'])['state'])
        self.assertEqual([], self.agent.calls)


class ProtocolTests(unittest.TestCase):
    def test_invalid_or_cross_event_decision_is_rejected(self):
        job = {'id': 'a', 'event_id': 'b'}
        for decision in ({'job_id': 'x', 'event_id': 'b', 'action': 'reply', 'text': 'hi'},
                         {'job_id': 'a', 'event_id': 'b', 'action': 'reply', 'text': 'hi\nthere'},
                         {'job_id': 'a', 'event_id': 'b', 'action': 'skip', 'text': 'hi'}):
            with self.assertRaises(ValueError):
                validate_decision(json.dumps(decision), job, 100)

    def test_overlapping_window_and_ambiguous_history(self):
        a, b, c, d = (message(x) for x in 'abcd')
        self.assertEqual([d], new_messages([a, b, c], [b, c, d]))
        with self.assertRaises(ValueError):
            new_messages([a, b], [a, b, a, b])


if __name__ == '__main__':
    unittest.main()
