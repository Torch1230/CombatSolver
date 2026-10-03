"""Merge publication-level diagnostic variants and exclude already published items.

Input is the public community index. Output contains static leads, not proven bugs.
"""
import argparse
import json
from pathlib import Path
import re

BASE_CHARACTERS = {'IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER'}

def publication_signature(value):
    if '固定搜索前缀与全部回合准备选牌分支都不相容' in value:
        return 'InvalidOperationException：固定搜索前缀与全部回合准备选牌分支都不相容'
    if '固定搜索前缀动作无效' in value and 'endsPlayerTurn=True' in value:
        return 'InvalidOperationException：结束回合卡的固定搜索前缀动作无效'
    for pattern in [r'(选牌回放时找不到 [A-Z_0-9]+)', r'(回放时找不到手牌 [A-Z_0-9]+)',
                    r'(原生选牌页面找不到 [A-Z_0-9]+)', r'(回合开始选牌?时找不到 [A-Z_0-9]+)']:
        match = re.search(pattern, value)
        if match:
            return value.split('：')[0] + '：' + match[1]
    value = re.sub(r'deployment:\d+:\d+:', 'deployment:<turn>:<action>:', value)
    value = re.sub(r'(action=)\d+/\d+', r'\1<action>', value)
    value = re.sub(r'(action_index|state_occurrence)=\d+', r'\1=<n>', value)
    value = re.sub(r'(仍有|剩余) \d+ (个|组)计划', r'\1 N \2计划', value)
    value = re.sub(r'(KNOWLEDGE_DEMON):\d+:\d+/', r'\1:<instance>/', value)
    value = re.sub(r'原生选牌页面要求选择 \d+\.\.\d+ 张，当前计划选择 \d+ 张',
                   '原生选牌页面与计划的选择数量不匹配', value)
    return value


def collect(index, version_prefix='0.47.'):
    by_id = {r['reportId']: r for r in index['reports']}
    previous_tasks = index['tasks'] + index.get('excludedTasks', []) + index.get('historicalTasks', [])
    published = {g for t in previous_tasks for g in t.get('groupIds', [])}
    published_keys = {publication_signature(g['signature']) for g in index['diagnosticGroups'] if g['id'] in published}
    published_representatives = {i for t in previous_tasks for i in t['representativeIds']}
    allowed = {'SearchFailure', 'SearchSetupFailure', 'DeploymentFailure', 'ChoiceExecutionFailure',
               'TurnSetupFailure', 'TurnSetupStateMismatch', 'SearchCapacityFailure', 'UnsupportedCombatSemantic',
               'UnexpectedChoice', 'TimeoutFailure'}
    buckets = {}
    for group in index['diagnosticGroups']:
        key = publication_signature(group['signature'])
        if group['id'] in published or key in published_keys or not set(group['kinds']) & allowed:
            continue
        if any(s in key for s in ['PotionPolicyUnsatisfiedException', 'OperationCanceledException', 'TaskCanceledException']):
            continue
        bucket = buckets.setdefault(key, {'publicationSignature': key, 'groupIds': [], 'reportIds': set(), 'kinds': set()})
        bucket['groupIds'].append(group['id'])
        bucket['reportIds'].update(group['reportIds'])
        bucket['kinds'].update(group['kinds'])
    def version(value):
        return tuple(map(int, value.split('.')))
    for bucket in buckets.values():
        bucket['excludedCharacterReportIds'] = sorted(i for i in bucket['reportIds'] if by_id[i]['characterId'] not in BASE_CHARACTERS)
        bucket['reportIds'] = sorted(i for i in bucket['reportIds'] if by_id[i]['characterId'] in BASE_CHARACTERS and by_id[i]['version'].startswith(version_prefix))
        bucket['kinds'] = sorted(bucket['kinds'])
        if not bucket['reportIds']:
            bucket['versions'] = []
            bucket['sessionCount'] = 0
            bucket['representativeId'] = None
            bucket['category'] = 'third_party_content'
            continue
        bucket['versions'] = sorted({by_id[i]['version'] for i in bucket['reportIds']}, key=version)
        bucket['sessionCount'] = len({by_id[i]['sessionId'] or i for i in bucket['reportIds']})
        candidates = [i for i in bucket['reportIds'] if i not in published_representatives and by_id[i]['archiveAvailableAtSelection'] and not by_id[i].get('archiveRetiredAfterPublication', False)]
        bucket['representativeId'] = max(candidates, key=lambda i: (version(by_id[i]['version']), i)) if candidates else None
        bucket['category'] = ('third_party_support' if 'UnsupportedCombatSemantic' in bucket['kinds'] or
                              any(x in bucket['publicationSignature'] for x in ['NotSupportedException', 'PredictionUnsupportedException', 'CanonicalModelException', 'WATCHER_', 'WatcherMod.', 'GuZhenRen.', 'YuiExtra.'])
                              else 'fault_investigation')
    return sorted(buckets.values(), key=lambda b: (version(b['versions'][-1]) if b['versions'] else (), b['sessionCount'], b['publicationSignature']), reverse=True)


def match_theme(signature, registry, source_frames=()):
    for theme in registry['themes']:
        if theme['kind'] != 'diagnostic':
            continue
        if theme.get('requiresSourceSymbolForMatch') and not any(symbol in frame for symbol in theme['sourceSymbols'] for frame in source_frames):
            continue
        if any(re.search(pattern, signature) for pattern in theme['signaturePatterns']):
            return theme
    return None


def duplicate_theme_reports(index, registry, version_prefix='0.47.'):
    by_id = {r['reportId']: r for r in index['reports']}
    matched = {}
    def receipt(report_id, theme):
        return {'reportId': report_id, 'themeId': theme['themeId'], 'reason': 'duplicate_theme',
                'action': 'delete_report_and_archive_and_skip', 'batchId': theme['batchId'],
                'issueUrl': theme['issueUrl'], 'assetUrl': theme['assetUrl'],
                'assetId': theme['assetId'], 'assetSizeBytes': theme['assetSizeBytes']}
    for group in index['diagnosticGroups']:
        theme = match_theme(group['signature'], registry, group.get('firstProjectFrames', []))
        if theme is None:
            continue
        for report_id in group['reportIds']:
            report = by_id[report_id]
            if not report['version'].startswith(version_prefix) or report['characterId'] not in BASE_CHARACTERS:
                continue
            if report.get('archiveRetiredAfterPublication'):
                continue
            matched.setdefault(report_id, receipt(report_id, theme))
    encounter_themes = {t['encounterId']: t for t in registry['themes'] if t['kind'] == 'optimization'}
    for case in index.get('optimizationCases', []):
        representative = by_id.get(case['representativeId'])
        if representative is None:
            continue
        theme = encounter_themes.get(representative['encounterId'])
        if theme is None:
            continue
        for report_id in case['reportIds']:
            report = by_id.get(report_id)
            if report is None or not report['version'].startswith(version_prefix) or report.get('archiveRetiredAfterPublication') or report['characterId'] not in BASE_CHARACTERS:
                continue
            matched.setdefault(report_id, receipt(report_id, theme))
    return sorted(matched.values(), key=lambda r: r['reportId'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--index', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--theme-registry', type=Path, required=True)
    parser.add_argument('--version-prefix', default='0.47.')
    args = parser.parse_args()
    index = json.loads(args.index.read_text(encoding='utf-8-sig'))
    registry = json.loads(args.theme_registry.read_text(encoding='utf-8-sig'))
    duplicates = duplicate_theme_reports(index, registry, args.version_prefix)
    discarded_ids = {r['reportId'] for r in duplicates}
    groups = [g for g in collect(index, args.version_prefix)
              if g['representativeId'] and g['representativeId'] not in discarded_ids
              and match_theme(g['publicationSignature'], registry) is None]
    result = {'schemaVersion': 2, 'versionPrefix': args.version_prefix,
              'duplicateThemeReports': duplicates, 'newCandidates': groups}
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'newCandidates': len(groups), 'duplicateThemeArchivesToDelete': len(duplicates)}))


if __name__ == '__main__':
    main()
