"""Verify claims, queue completion, and preservation of user-written Issue content."""
import copy
import importlib.util
from pathlib import Path
import unittest
from urllib.parse import unquote

spec = importlib.util.spec_from_file_location('claims', Path(__file__).with_name('sync-community-claims.py'))
claims = importlib.util.module_from_spec(spec)
spec.loader.exec_module(claims)
BATCHES = [{'batchId': 'B012', 'issueNumber': 149}, {'batchId': 'Q003', 'issueNumber': 183}]
QUEUE_ISSUE = 200
ENTRY_BODY = '欢迎参与。\n\n> 写在前面：用户自己的感谢文字。\n\n[查看队列](https://github.com/Torch1230/CombatSolver/issues/200)\n'
BODY = '''保留用户写的介绍。

<!-- community-task-queue:bugfix -->
| 批次 | 主题概览 | 认领者 | 完成状态 |
| --- | --- | --- | --- |
| [B012 · #149](https://github.com/Torch1230/CombatSolver/issues/149) | 机制描述 | 未认领 | 未完成 |
<!-- /community-task-queue:bugfix -->

用户写在表格之间的说明。

<!-- community-task-queue:worldline -->
| 批次 | 主题概览 | 认领者 | 完成状态 |
| --- | --- | --- | --- |
| [Q003 · #183](https://github.com/Torch1230/CombatSolver/issues/183) | 世界线 | 未认领 | 未完成 |
<!-- /community-task-queue:worldline -->

保留用户写的结尾。
'''


def user(login):
    return {'login': login, 'type': 'User'}


def comment(cid, login, body):
    return {'id': cid, 'body': body, 'user': user(login)}


class FakeGitHub:
    def __init__(self):
        self.issues = {171: {'title': '用户改过的标题', 'body': ENTRY_BODY, 'html_url': 'https://github.com/Torch1230/CombatSolver/issues/171'},
                       QUEUE_ISSUE: {'title': '用户维护的队列标题', 'body': BODY, 'html_url': 'https://github.com/Torch1230/CombatSolver/issues/200'},
                       149: {'assignees': [], 'state': 'open', 'labels': [{'name': 'bug'}, {'name': '待定位'}]},
                       183: {'assignees': [], 'state': 'open', 'labels': [{'name': 'enhancement'}, {'name': '待定位'}]}}
        self.comments = {149: [], 183: []}
        self.labels = {}
        self.writes = []

    def request(self, route, method='GET', payload=None):
        if route == 'labels':
            self.writes.append((route, method, copy.deepcopy(payload)))
            self.labels[payload['name']] = copy.deepcopy(payload)
            return copy.deepcopy(payload)
        number = int(route.split('/')[1])
        if method == 'GET':
            return copy.deepcopy(self.issues[number])
        self.writes.append((route, method, copy.deepcopy(payload)))
        issue = self.issues[number]
        if '/labels' in route:
            if method == 'POST':
                existing = {label['name'] for label in issue['labels']}
                issue['labels'] += [{'name': name} for name in payload['labels'] if name not in existing]
            elif method == 'DELETE':
                name = unquote(route.rsplit('/', 1)[1])
                issue['labels'] = [label for label in issue['labels'] if label['name'] != name]
            return copy.deepcopy(issue['labels'])
        elif route.endswith('/assignees'):
            if method == 'POST':
                issue['assignees'] += [user(login) for login in payload['assignees']]
            elif method == 'DELETE':
                issue['assignees'] = [u for u in issue['assignees'] if u['login'] not in payload['assignees']]
        else:
            assert method == 'PATCH' and set(payload) == {'body'}
            issue.update(payload)
        return copy.deepcopy(issue)

    def pages(self, route):
        if route == 'labels':
            return copy.deepcopy(list(self.labels.values()))
        return copy.deepcopy(self.comments[int(route.split('/')[1])])


class ClaimTests(unittest.TestCase):
    def test_claims_from_existing_freeform_requests_and_profile_links(self):
        api = FakeGitHub()
        api.comments[149] = [comment(1, 'Charlie-chulong', '认领 B012 整批（五个机制主题），麻烦分配给 Charlie-chulong。')]
        api.comments[183] = [comment(2, 'Another-User', '认领 Q003 整批')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [user('Charlie-chulong')])
        self.assertIn('[Charlie-chulong](https://github.com/Charlie-chulong)', api.issues[QUEUE_ISSUE]['body'])
        self.assertEqual(api.issues[171]['title'], '用户改过的标题')
        self.assertEqual(api.issues[QUEUE_ISSUE]['title'], '用户维护的队列标题')
        self.assertEqual(claims.STATE_PATTERN.sub('', api.issues[171]['body']).rstrip(), ENTRY_BODY.rstrip())
        for text in ['保留用户写的介绍。', '用户写在表格之间的说明。', '保留用户写的结尾。']:
            self.assertIn(text, api.issues[QUEUE_ISSUE]['body'])

    def test_unclaimed_then_release_and_reclaim(self):
        api = FakeGitHub()
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[QUEUE_ISSUE]['body'].count('未认领'), 2)
        api.comments[149] = [comment(1, 'First', '认领'), comment(2, 'First', '取消认领'), comment(3, 'Second', '/claim')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [user('Second')])
        self.assertIn('[Second](https://github.com/Second)', api.issues[QUEUE_ISSUE]['body'])

    def test_claim_conflict_and_another_person_cannot_release_owner(self):
        api = FakeGitHub()
        api.issues[149]['assignees'] = [user('MaintainerAssigned')]
        api.comments[149] = [comment(1, 'Intruder', '认领 B012 整批'), comment(2, 'Intruder', '取消认领')]
        result = claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [user('MaintainerAssigned')])
        self.assertEqual(result['actions'][0]['reason'], 'closed_or_assigned')

    def test_same_person_can_claim_two_batches_and_release_one(self):
        api = FakeGitHub()
        api.comments[149] = [comment(9, 'Person', '认领')]
        api.comments[183] = [comment(2, 'Person', '认领')]
        result = claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[183]['assignees'], [user('Person')])
        self.assertEqual(api.issues[149]['assignees'], [user('Person')])
        self.assertEqual([a['batchId'] for a in result['actions']], ['Q003', 'B012'])
        api.comments[149].append(comment(10, 'Person', '/unclaim'))
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [])
        self.assertEqual(api.issues[183]['assignees'], [user('Person')])

    def test_closed_batch_quotes_wrong_batch_and_bot_cannot_claim(self):
        api = FakeGitHub()
        api.issues[149]['state'] = 'closed'
        api.comments[149] = [comment(1, 'Person', '认领')]
        bot = comment(4, 'robot', '/claim'); bot['user']['type'] = 'Bot'
        api.comments[183] = [comment(2, 'Person', '> 认领'), comment(3, 'Person', '认领 B012 整批'), bot,
                             comment(5, 'Person', '```\n认领\n```')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [])
        self.assertEqual(api.issues[183]['assignees'], [])

    def test_cursor_avoids_replaying_claim_after_manual_unassignment(self):
        api = FakeGitHub()
        api.comments[149] = [comment(1, 'Person', '认领')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        api.issues[149]['assignees'] = []
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.issues[149]['assignees'], [])

    def test_new_run_handles_all_missed_comment_events(self):
        api = FakeGitHub()
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        api.comments[149] = [comment(10, 'First', '认领'), comment(11, 'First', '/unclaim'), comment(12, 'Second', '认领')]
        result = claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual([a['action'] for a in result['actions']], ['claimed', 'unclaimed', 'claimed'])
        self.assertEqual(claims.read_state(api.issues[171]['body'])['lastCommentIds']['149'], 12)
        count = len(api.writes)
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(len(api.writes), count)

    def test_existing_owner_column_manual_edit_and_multiple_assignees(self):
        api = FakeGitHub()
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        api.issues[QUEUE_ISSUE]['body'] = api.issues[QUEUE_ISSUE]['body'].replace('保留用户写的介绍。', '新的手写内容。')
        api.issues[149]['assignees'] = [user('First'), user('Second')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        body = api.issues[QUEUE_ISSUE]['body']
        self.assertIn('新的手写内容。', body)
        self.assertEqual(body.count('认领者'), 2)
        self.assertIn('[First](https://github.com/First)、[Second](https://github.com/Second)', body)

    def test_queue_table_mismatch_fails_before_assignment_or_body_write(self):
        api = FakeGitHub()
        api.issues[QUEUE_ISSUE]['body'] = api.issues[QUEUE_ISSUE]['body'].replace('/issues/149)', '/issues/999)')
        with self.assertRaises(ValueError): claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.writes, [])

    def test_dry_run_preserves_assignments_and_entry(self):
        api = FakeGitHub()
        api.comments[149] = [comment(1, 'Person', '认领')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE, dry_run=True)
        self.assertEqual(api.writes, [])
        self.assertEqual(api.issues[QUEUE_ISSUE]['body'], BODY)
        self.assertEqual(api.issues[171]['body'], ENTRY_BODY)
        self.assertEqual(api.issues[149]['assignees'], [])

    def test_claim_status_labels_follow_manual_assignment_and_preserve_other_labels(self):
        api = FakeGitHub()
        api.issues[149]['labels'] += [{'name': 'help wanted'}, {'name': '任务:故障'}]
        api.issues[149]['assignees'] = [user('First')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual({l['name'] for l in api.issues[149]['labels']}, {'bug', 'help wanted', '任务:故障', '已认领'})
        self.assertEqual({l['name'] for l in api.issues[183]['labels']}, {'enhancement', '待认领'})
        self.assertEqual(set(api.labels), {'待认领', '已认领'})
        api.issues[149]['assignees'] = []
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual({l['name'] for l in api.issues[149]['labels']}, {'bug', 'help wanted', '任务:故障', '待认领'})

    def test_claim_and_unclaim_update_labels(self):
        api = FakeGitHub()
        api.comments[149] = [comment(1, 'Person', '/claim')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual({l['name'] for l in api.issues[149]['labels']}, {'bug', '已认领'})
        api.comments[149].append(comment(2, 'Person', '/unclaim'))
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual({l['name'] for l in api.issues[149]['labels']}, {'bug', '待认领'})

    def test_inconsistent_status_labels_are_reconciled_and_other_issues_are_untouched(self):
        api = FakeGitHub()
        api.issues[149]['labels'] += [{'name': '待认领'}, {'name': '已认领'}]
        api.issues[999] = {'assignees': [], 'state': 'open', 'labels': [{'name': '待定位'}]}
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual({l['name'] for l in api.issues[149]['labels']}, {'bug', '待认领'})
        self.assertEqual(api.issues[999]['labels'], [{'name': '待定位'}])


    def test_completed_batch_and_reopening_update_only_status_and_keep_owner(self):
        api = FakeGitHub()
        api.issues[149].update(state='closed', state_reason='completed', assignees=[user('Finisher')])
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        row = next(line for line in api.issues[QUEUE_ISSUE]['body'].splitlines() if '/issues/149)' in line)
        self.assertIn('[Finisher](https://github.com/Finisher) | 已完成 |', row)
        self.assertIn('机制描述', row)
        api.issues[149].update(state='open', state_reason='reopened')
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        row = next(line for line in api.issues[QUEUE_ISSUE]['body'].splitlines() if '/issues/149)' in line)
        self.assertIn('[Finisher](https://github.com/Finisher) | 未完成 |', row)

    def test_cancelled_and_migrated_batches_are_not_completed(self):
        api = FakeGitHub()
        api.issues[149].update(state='closed', state_reason='not_planned')
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        row = next(line for line in api.issues[QUEUE_ISSUE]['body'].splitlines() if '/issues/149)' in line)
        self.assertIn('| 已关闭（未完成） |', row)

    def test_missing_status_column_or_markers_fail_before_claim(self):
        for broken_body in [BODY.replace('完成状态', '进度'),
                            BODY.replace('<!-- /community-task-queue:bugfix -->', '')]:
            with self.subTest(body=broken_body):
                api = FakeGitHub()
                api.issues[QUEUE_ISSUE]['body'] = broken_body
                api.comments[149] = [comment(1, 'Person', '/claim')]
                with self.assertRaises(ValueError):
                    claims.synchronize(api, BATCHES, QUEUE_ISSUE)
                self.assertEqual(api.writes, [])

    def test_example_row_outside_queue_markers_and_crlf_are_preserved(self):
        api = FakeGitHub()
        example = '| [B012](https://github.com/Torch1230/CombatSolver/issues/149) | 手写示例 | 示例作者 | 示例状态 |\n'
        api.issues[QUEUE_ISSUE]['body'] = (example + BODY).replace('\n', '\r\n')
        api.comments[149] = [comment(1, 'Person', '/claim')]
        claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        body = api.issues[QUEUE_ISSUE]['body']
        self.assertTrue(body.startswith(example.replace('\n', '\r\n')))
        self.assertNotIn('\n', body.replace('\r\n', ''))

    def test_displayed_number_mismatch_fails_before_claim(self):
        api = FakeGitHub()
        api.issues[QUEUE_ISSUE]['body'] = BODY.replace('B012 · #149', 'B012 · #999')
        api.comments[149] = [comment(1, 'Person', '/claim')]
        with self.assertRaises(ValueError):
            claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(api.writes, [])

    def test_queue_save_failure_preserves_comment_cursor_for_retry(self):
        class FailingQueue(FakeGitHub):
            def request(self, route, method='GET', payload=None):
                if route == f'issues/{QUEUE_ISSUE}' and method == 'PATCH':
                    raise RuntimeError('Queue write failed')
                return super().request(route, method, payload)
        api = FailingQueue()
        api.comments[149] = [comment(1, 'Person', '/claim')]
        with self.assertRaisesRegex(RuntimeError, 'Queue write failed'):
            claims.synchronize(api, BATCHES, QUEUE_ISSUE)
        self.assertEqual(claims.read_state(api.issues[171]['body'])['lastCommentIds'], {})


if __name__ == '__main__':
    unittest.main()
