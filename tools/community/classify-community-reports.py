"""Classify an exported report snapshot without running the game or changing the server."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import re


def version(value):
    return tuple(map(int, value.split('.')))


def signature(kind, detail):
    # A signature is a diagnostic bucket, not a proven shared root cause.
    value = detail.split(' expected=')[0].split(' actual=')[0]
    value = re.sub(r'\b[0-9a-f]{32,64}\b', '<id>', value, flags=re.I)
    value = re.sub(r'\[\d+\]', '[]', value)
    value = re.sub(r'第\s*\d+\s*回合', '第 N 回合', value)
    value = re.sub(r'\b(?:generation|turn|round|CombatId|traceId)=\S+', '<context>', value)
    value = re.sub(r'\b(?:actionTurn|nodeTurn)=\d+', '<turn>', value)
    value = re.sub(r'\s+', ' ', value).strip()
    return value if re.search(r'\w*Exception[：:]', value) else kind + ': ' + (value or '细节未记录')


def classify(reports):
    groups = {}
    cases = {}
    memberships = {}
    for x in sorted(reports, key=lambda r: r['id']):
        if version(x['modVersion']) < (0, 44, 0):
            raise ValueError('Snapshot contains a report older than 0.44.0: ' + x['id'])
        session = x['combat']['sessionId'] or 'missing-session:' + x['id']
        memberships[x['id']] = []
        diagnostics = [(i['kind'], i.get('detail', '')) for i in x['issues'] if i['kind'] != 'BetterWorldline']
        classification = x.get('classification')
        if classification is not None:
            diagnostics.extend(('Replan:' + k, '') for k in ['stateMismatchReplans', 'deploymentDriftReplans', 'continuationMissingReplans', 'planExhaustedReplans', 'manualDivergenceReplans'] if classification.get(k, 0) > 0)
        if x['unexpectedReplans'] > 0 and not any(k.startswith('Replan:') for k, _ in diagnostics):
            diagnostics.append(('UnexpectedReplan', '重算原因未记录'))
        if not diagnostics and not any(i['kind'] == 'BetterWorldline' for i in x['issues']):
            diagnostics.append(('Unclassified', '没有诊断信号，需要阅读包内日志'))
        for kind, detail in diagnostics:
            key = signature(kind, detail)
            group_id = 'D-' + hashlib.sha256(key.encode()).hexdigest()[:12]
            g = groups.setdefault(group_id, {'id':group_id,'kinds':set(),'signature':key,'reportIds':set(),'sessions':set()})
            g['kinds'].add(kind)
            g['reportIds'].add(x['id'])
            g['sessions'].add(session)
            if group_id not in memberships[x['id']]:
                memberships[x['id']].append(group_id)
        hp = x['hpLoss']
        qualifies = hp is None or 'potionAdjustedHpReduction' not in hp or (
            hp['reduction'] > 0 and hp['potionAdjustedHpReduction'] >= 0)
        if qualifies and any(i['kind'] == 'BetterWorldline' for i in x['issues']):
            case = cases.setdefault(session, {'sessionId':session,'reportIds':[]})
            case['reportIds'].append(x['id'])
    by_id = {x['id']:x for x in reports}
    for g in groups.values():
        g['kinds'] = sorted(g['kinds'])
        g['reportIds'] = sorted(g['reportIds'])
        g['sessionCount'] = len(g.pop('sessions'))
        g['representativeId'] = max(g['reportIds'], key=lambda i:(version(by_id[i]['modVersion']),len(json.dumps(by_id[i]['issues'])),by_id[i]['receivedAt']))
        g['versions'] = sorted({by_id[i]['modVersion'] for i in g['reportIds']},key=version)
    for c in cases.values():
        c['reportIds'].sort()
        def rank(i):
            hp = by_id[i]['hpLoss']
            metric = hp.get('potionAdjustedHpReduction', hp.get('reduction')) if hp else None
            return (hp is not None and 'potionAdjustedHpReduction' in hp, metric is not None,
                    metric if metric is not None else 0, version(by_id[i]['modVersion']), by_id[i]['receivedAt'])
        c['representativeId'] = max(c['reportIds'],key=rank)
        r = by_id[c['representativeId']]
        c['hpLoss'] = r['hpLoss']
        c['diagnosticGroups'] = memberships[r['id']]
        c['rankMetric'] = ('potion_adjusted_hp_reduction' if c['hpLoss'] and 'potionAdjustedHpReduction' in c['hpLoss']
                           else 'reported_projected_hp_reduction_unverified_resources')
    def case_rank(case):
        hp = case['hpLoss']
        metric = hp.get('potionAdjustedHpReduction', hp.get('reduction')) if hp else None
        return (case['rankMetric'] == 'potion_adjusted_hp_reduction', metric is not None,
                metric if metric is not None else 0, case['sessionId'])
    ordered_cases = sorted(cases.values(), key=case_rank, reverse=True)
    return {'schemaVersion':1,'minimumVersion':'0.44.0','verification':'static_classification_only','reportCount':len(reports),'sessionCount':len({r['combat']['sessionId'] or 'missing-session:'+r['id'] for r in reports}),'diagnosticGroups':sorted(groups.values(),key=lambda g:(version(g['versions'][-1]),g['sessionCount'],g['id']),reverse=True),'optimizationCases':ordered_cases,'reportMemberships':memberships}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reports',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=classify(json.loads(args.reports.read_text(encoding='utf-8-sig')))
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'reports':result['reportCount'],'sessions':result['sessionCount'],'diagnosticGroups':len(result['diagnosticGroups']),'optimizationCases':len(result['optimizationCases'])},ensure_ascii=False))


if __name__ == '__main__':
    main()
