"""Claim registered batches and sync labels, queue ownership, and completion.

Run through GitHub Actions, or locally with an authenticated gh CLI. Comments are
data: only an exact first-line claim/unclaim command can change its author's assignment.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import quote


REPOSITORY = 'Torch1230/CombatSolver'
ENTRY_ISSUE = 171
CLAIM_LABELS = {
    '待认领': {'color': 'fbca04', 'description': '等待贡献者认领的社区任务批次'},
    '已认领': {'color': '0e8a16', 'description': '已有负责人的社区任务批次'},
}
CLAIM_STATUS_LABELS = {'待定位', *CLAIM_LABELS}
STATE_PATTERN = re.compile(r'<!-- combatsolver-community-claims: (\{[^\n]*\}) -->')
COMMAND_PATTERN = re.compile(r'(认领|取消认领|放弃认领)(?:\s*([BQ]\d{3}))?(?:\s*整批)?[。！!]?', re.IGNORECASE)
COMMAND_INTRO_PATTERN = re.compile(r'(认领|取消认领|放弃认领)(?:\s*([BQ]\d{3}))?\s*整批(?=$|[\s（(，,。.!！：:])', re.IGNORECASE)
ROW_PATTERN = re.compile(r'^\s*\|\s*\[([BQ]\d{3})(?:\s*·\s*#(\d+))?\]\(https://github\.com/Torch1230/CombatSolver/issues/(\d+)\)\s*\|')
QUEUE_MARKER_PATTERN = re.compile(r'<!-- (?P<closing>/?)community-task-queue:(?P<section>bugfix|worldline) -->')


class GitHub:
    def request(self, route, method='GET', payload=None):
        args = ['gh', 'api', f'repos/{REPOSITORY}/{route}', '--method', method]
        if payload is not None:
            args += ['--input', '-']
        result = subprocess.run(args, input=None if payload is None else json.dumps(payload).encode(),
                                capture_output=True, check=True)
        return json.loads(result.stdout) if result.stdout else None

    def pages(self, route):
        result = []
        for page in range(1, 1001):
            items = self.request(f'{route}?per_page=100&page={page}')
            result.extend(items)
            if len(items) < 100:
                return result
        raise RuntimeError('GitHub pagination exceeded supported page count')


def parse_command(body, batch_id):
    first_line = body.strip().splitlines()[0] if body.strip() else ''
    if first_line == '/claim':
        return 'claim'
    if first_line == '/unclaim':
        return 'unclaim'
    match = COMMAND_PATTERN.fullmatch(first_line) or COMMAND_INTRO_PATTERN.match(first_line)
    if match is None or (match[2] is not None and match[2].upper() != batch_id):
        return None
    return 'claim' if match[1] == '认领' else 'unclaim'


def read_state(body):
    matches = list(STATE_PATTERN.finditer(body))
    if not matches:
        return {'schemaVersion': 1, 'lastCommentIds': {}}
    if len(matches) != 1:
        raise ValueError('Entry contains multiple claim state markers')
    state = json.loads(matches[0][1])
    if state['schemaVersion'] != 1 or not isinstance(state['lastCommentIds'], dict):
        raise ValueError('Unsupported claim state')
    if any(not k.isdecimal() or type(v) is not int or v < 0 for k, v in state['lastCommentIds'].items()):
        raise ValueError('Invalid claim comment cursor')
    return state


def owner_cell(assignees):
    if not assignees:
        return '未认领'
    names = []
    for user in assignees:
        login = user['login']
        if not re.fullmatch(r'[A-Za-z0-9-]+(?:\[bot\])?', login):
            raise ValueError('Invalid GitHub login')
        names.append(f'[{login}](https://github.com/{login})')
    return '、'.join(names)


def completion_cell(issue):
    if issue['state'] == 'open':
        return '未完成'
    if issue['state'] == 'closed':
        return '已完成' if issue.get('state_reason') == 'completed' else '已关闭（未完成）'
    raise ValueError('Unsupported issue state')


def update_queue_body(body, batches, issues):
    """Update only owner and completion cells inside the two marked queue tables."""
    registered = {b['batchId']: b for b in batches}
    lines = body.splitlines(keepends=True)
    seen = set()
    sections = set()
    active_section = None
    section_start = None
    row_tables = []
    for i, line in enumerate(lines):
        marker = QUEUE_MARKER_PATTERN.fullmatch(line.strip())
        if marker is not None:
            section = marker['section']
            if marker['closing']:
                if active_section != section:
                    raise ValueError('Queue section closing marker does not match')
                active_section = None
            else:
                if active_section is not None or section in sections:
                    raise ValueError('Duplicate or nested queue section')
                active_section = section
                section_start = i + 1
                sections.add(section)
            continue
        if active_section is None:
            continue
        match = ROW_PATTERN.match(line)
        if match is None:
            continue
        bid = match[1]
        if bid not in registered or int(match[3]) != registered[bid]['issueNumber'] or bid in seen:
            raise ValueError('Queue batch link does not match publication ledger')
        if match[2] is not None and int(match[2]) != int(match[3]):
            raise ValueError('Displayed issue number does not match queue link')
        seen.add(bid)
        header_index = i - 1
        while header_index >= section_start and lines[header_index].lstrip().startswith('|'):
            header_index -= 1
        row_tables.append((i, header_index + 1, registered[bid]))
    if active_section is not None or sections != {'bugfix', 'worldline'}:
        raise ValueError('Queue section markers are incomplete')
    if seen != set(registered):
        raise ValueError('Queue table and publication ledger contain different batches')

    headers = {}
    for _, header_index, _ in row_tables:
        if header_index in headers:
            continue
        cells = [cell.strip() for cell in lines[header_index].strip().strip('|').split('|')]
        if cells[0] != '批次' or len(cells) != len(set(cells)) or not {'认领者', '完成状态'} <= set(cells):
            raise ValueError('Unsupported batch table header')
        headers[header_index] = (cells.index('认领者'), cells.index('完成状态'), len(cells))
    for i, header_index, batch in row_tables:
        owner_column, completion_column, width = headers[header_index]
        issue = issues[batch['issueNumber']]
        end = '\r\n' if lines[i].endswith('\r\n') else '\n' if lines[i].endswith('\n') else ''
        line = lines[i].rstrip('\r\n')
        fields = line.split('|')
        if len(fields) - 2 != width:
            raise ValueError('Unexpected batch row width')
        fields[owner_column + 1] = ' ' + owner_cell(issue['assignees']) + ' '
        fields[completion_column + 1] = ' ' + completion_cell(issue) + ' '
        lines[i] = '|'.join(fields) + end
    return ''.join(lines)


def update_entry_state(body, state):
    """Keep the entry text intact; store consumed comment cursors in its marker."""
    marker = '<!-- combatsolver-community-claims: ' + json.dumps(state, ensure_ascii=False, separators=(',', ':')) + ' -->'
    if STATE_PATTERN.search(body):
        return STATE_PATTERN.sub(lambda _: marker, body)
    return body.rstrip('\r\n') + '\n\n' + marker + '\n'


def synchronize_labels(api, batches, issues, dry_run):
    repository_labels = {label['name'] for label in api.pages('labels')}
    for name, properties in CLAIM_LABELS.items():
        if name not in repository_labels and not dry_run:
            api.request('labels', 'POST', dict(name=name, **properties))
    changes = []
    for batch in batches:
        number = batch['issueNumber']
        desired = '已认领' if issues[number]['assignees'] else '待认领'
        existing = {label['name'] for label in issues[number]['labels']}
        obsolete = sorted((existing & CLAIM_STATUS_LABELS) - {desired})
        if desired in existing and not obsolete:
            continue
        if not dry_run:
            if desired not in existing:
                api.request(f'issues/{number}/labels', 'POST', {'labels': [desired]})
            for name in obsolete:
                api.request(f'issues/{number}/labels/{quote(name, safe="")}', 'DELETE')
        changes.append({'batchId': batch['batchId'], 'issueNumber': number,
                        'label': desired, 'removed': obsolete})
    return changes


def synchronize(api, batches, queue_issue, dry_run=False):
    if type(queue_issue) is not int or queue_issue <= 0 or queue_issue == ENTRY_ISSUE or queue_issue in {b['issueNumber'] for b in batches}:
        raise ValueError('Invalid queue issue number')
    entry = api.request(f'issues/{ENTRY_ISSUE}')
    state = read_state(entry['body'])
    cursors = dict(state['lastCommentIds'])
    queue = api.request(f'issues/{queue_issue}')
    issues = {b['issueNumber']: api.request(f"issues/{b['issueNumber']}") for b in batches}
    # Validate both queue tables before changing assignments or labels.
    update_queue_body(queue['body'], batches, issues)
    pending = []
    # Read all unseen comments, so a newer queued run also handles events it superseded.
    for batch in batches:
        number = batch['issueNumber']
        previous = cursors.get(str(number), 0)
        comments = api.pages(f'issues/{number}/comments')
        for comment in comments:
            if comment['id'] > previous:
                pending.append((comment['id'], batch, comment))
        cursors[str(number)] = max([previous] + [c['id'] for c in comments])
    actions = []
    for _, batch, comment in sorted(pending, key=lambda item: item[0]):
        number = batch['issueNumber']
        command = parse_command(comment['body'], batch['batchId'])
        author = comment['user']
        if command is None or author['type'] != 'User':
            continue
        issue = issues[number]
        login = author['login']
        owners = {a['login'].lower() for a in issue['assignees']}
        if command == 'claim':
            if issue['state'] != 'open' or owners:
                actions.append({'commentId': comment['id'], 'action': 'claim_ignored', 'reason': 'closed_or_assigned'})
                continue
            if dry_run:
                issue['assignees'] = [author]
            else:
                assigned = api.request(f'issues/{number}/assignees', 'POST', {'assignees': [login]})
                if login.lower() not in {a['login'].lower() for a in assigned['assignees']}:
                    raise RuntimeError('GitHub did not assign the commenter')
                issues[number] = assigned
            actions.append({'commentId': comment['id'], 'action': 'claimed', 'batchId': batch['batchId'], 'login': login})
        elif login.lower() in owners:
            if dry_run:
                issue['assignees'] = [a for a in issue['assignees'] if a['login'].lower() != login.lower()]
            else:
                issues[number] = api.request(f'issues/{number}/assignees', 'DELETE', {'assignees': [login]})
                if login.lower() in {a['login'].lower() for a in issues[number]['assignees']}:
                    raise RuntimeError('GitHub did not remove the commenter')
            actions.append({'commentId': comment['id'], 'action': 'unclaimed', 'batchId': batch['batchId'], 'login': login})
    # Read the current body after assignment work, preserving intervening editorial edits.
    current_queue = api.request(f'issues/{queue_issue}')
    queue_body = update_queue_body(current_queue['body'], batches, issues)
    queue_changed = queue_body != current_queue['body']
    current = api.request(f'issues/{ENTRY_ISSUE}')
    current_state = read_state(current['body'])
    for number, cursor in current_state['lastCommentIds'].items():
        cursors[number] = max(cursors.get(number, 0), cursor)
    body = update_entry_state(current['body'], {'schemaVersion': 1, 'lastCommentIds': cursors})
    changed = body != current['body']
    label_changes = synchronize_labels(api, batches, issues, dry_run)
    result = {'dryRun': dry_run, 'entryTitle': current['title'], 'entryBodyChanged': changed,
              'queueTitle': current_queue['title'], 'queueUrl': current_queue['html_url'], 'queueBodyChanged': queue_changed,
              'batches': len(batches), 'actions': actions, 'labelChanges': label_changes}
    if queue_changed and not dry_run:
        api.request(f'issues/{queue_issue}', 'PATCH', {'body': queue_body})
    # Advance comment cursors only after the queue has been saved successfully.
    if changed and not dry_run:
        saved = api.request(f'issues/{ENTRY_ISSUE}', 'PATCH', {'body': body})
        result['entryTitle'] = saved['title']
        result['entryUrl'] = saved['html_url']
    return result


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ledger', type=Path, default=Path(__file__).resolve().parents[2]/'docs/community/publication-ledger.json')
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--receipt', type=Path)
    args = parser.parse_args()
    ledger = json.loads(args.ledger.read_text(encoding='utf-8'))
    if ledger['repository'] != REPOSITORY or os.environ.get('GITHUB_REPOSITORY', REPOSITORY) != REPOSITORY:
        raise ValueError('Unsupported repository')
    batches = ledger['batches']
    if len({b['issueNumber'] for b in batches}) != len(batches):
        raise ValueError('Duplicate batch issue number')
    if os.environ.get('GITHUB_EVENT_NAME') in {'issue_comment', 'issues'}:
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
        if event['issue'].get('pull_request') or event['issue']['number'] not in {b['issueNumber'] for b in batches}:
            print(json.dumps({'action': 'ignored', 'reason': 'issue_not_registered'}))
            return
    result = synchronize(GitHub(), batches, ledger['queueIssueNumber'], args.dry_run)
    if args.receipt:
        args.receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    main()
