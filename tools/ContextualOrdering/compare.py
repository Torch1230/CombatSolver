#!/usr/bin/env python3
"""Compare saved runs with native outcome policy and explicit root/budget checks.

Performance fields are raw observations; this tool does not turn discovery runs
or unpaired runs into performance evidence. Unknown/time-limited pairs stay excluded.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import subprocess


def read(path):
    return json.loads(path.read_text())


def records(directory, variant):
    return {r['case']: r for r in map(json.loads, (directory / 'observations.jsonl').read_text().splitlines())
            if r['variant'] == variant}


def context_mismatch(a, b):
    if a['search']['rootContinuationStamp'] != b['search']['rootContinuationStamp']:
        return 'RootMismatch'
    if a['budget'] != b['budget']:
        return 'BudgetMismatch'
    policy_a, policy_b = [json.loads(json.dumps(r['searchPolicy'])) for r in (a, b)]
    for policy in (policy_a, policy_b):
        for key in ('ContextualRanking', 'BaseScoreOnly', 'SecondRankBand', 'ContinuousThreatRanking', 'StopPortfolioAtHpTarget', 'BeamWeightPerturbation', 'OffensiveRefinementPortfolio', 'BoundedOffensiveRefinementPortfolio'):
            policy['Profile'].pop(key, None)
    return 'PolicyMismatch' if policy_a != policy_b else None


def compare(args):
    args.out.mkdir(parents=True, exist_ok=False)
    a, b = records(args.baseline, args.baseline_variant), records(args.candidate, args.candidate_variant)
    inputs, observations = [], []
    for case in sorted(a.keys() | b.keys()):
        ra, rb = a.get(case), b.get(case)
        item = {'case': case}
        observations.append(item)
        if ra is None or rb is None:
            item['status'] = 'MissingPair'
            continue
        if ra['status'] != 'Comparable' or rb['status'] != 'Comparable':
            item.update(status='Excluded', reasons=[ra['status'], rb['status']])
            continue
        pa, pb = args.baseline / case / args.baseline_variant, args.candidate / case / args.candidate_variant
        ha, hb = read(pa / 'harness-result.json'), read(pb / 'harness-result.json')
        mismatch = context_mismatch(ha, hb)
        if mismatch:
            item['status'] = mismatch
            continue
        item['status'] = 'Comparable'
        for name, result, path in (('baseline', ha, pa), ('candidate', hb, pb)):
            item[name] = {k: result[k] for k in ('wallSeconds', 'peakManagedHeapBytes',
                'peakManagedLiveBytes', 'peakWorkingSetBytes', 'totalAllocatedBytes')}
            item[name].update({k: result['solverMetrics'].get(k) for k in ('TotalExpanded', 'TotalTransitions',
                'ProjectedBattleHpLost', 'PotionCount', 'OnlyDeathRoutes', 'CombatEndedTurn')})
            # Keep actual outcomes beside solver metrics: total HP loss and policy-adjusted
            # deficit differ, and a defeat must never look like merely a small HP regression.
            quality = read(path / 'quality.json')
            item[name]['outcome'] = quality['quality']
            item[name]['resources'] = {k: quality['snapshot'][k] for k in
                ('playerHp', 'playerMaxHp', 'recoveredPlayerHp', 'growthRewards', 'relicCounters')}
        inputs.append({'id': case, 'candidate': str((pb / 'quality.json').resolve()),
                       'baseline': str((pa / 'quality.json').resolve())})
    (args.out / 'comparison-input.json').write_text(json.dumps(inputs))
    subprocess.run(['dotnet', str(args.harness.resolve()), '--compare-quality-batch',
                    str(args.out / 'comparison-input.json'), str(args.out / 'quality.json')], check=True)
    outcomes = {row['id']: row for row in read(args.out / 'quality.json')}
    for item in observations:
        if item['case'] in outcomes:
            result = outcomes[item['case']]
            item.update(comparison=result['comparison'], materialComparison=result['materialComparison'])
    summary = {'statuses': dict(Counter(r['status'] for r in observations)),
               'quality': dict(Counter(r['comparison'] for r in observations if 'comparison' in r)),
               'materialQuality': dict(Counter(r['materialComparison'] for r in observations if 'materialComparison' in r)),
               'observations': observations}
    comparable = [r for r in observations if r['status'] == 'Comparable']
    summary['victoryChanges'] = {
        'lossToWin': [r['case'] for r in comparable
                      if not r['baseline']['outcome']['won'] and r['candidate']['outcome']['won']],
        'winToLoss': [r['case'] for r in comparable
                      if r['baseline']['outcome']['won'] and not r['candidate']['outcome']['won']],
    }
    both_won = [r for r in comparable
                if r['baseline']['outcome']['won'] and r['candidate']['outcome']['won']]
    summary['bothWonDamageChanges'] = {
        'sampleCount': len(both_won),
        'totalHpLossDelta': sum(r['candidate']['outcome']['projectedBattleHpLost']
                               - r['baseline']['outcome']['projectedBattleHpLost'] for r in both_won),
        'policyDeficitDelta': sum(r['candidate']['outcome']['strategicHpDeficit']
                                 - r['baseline']['outcome']['strategicHpDeficit'] for r in both_won),
        'largestTotalHpLossDelta': max((r['candidate']['outcome']['projectedBattleHpLost']
                                        - r['baseline']['outcome']['projectedBattleHpLost']
                                        for r in both_won), default=None),
    }
    (args.out / 'report.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps({k: v for k, v in summary.items() if k != 'observations'}))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--baseline-variant', default='baseline')
    p.add_argument('--candidate', type=Path, required=True)
    p.add_argument('--candidate-variant', required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--harness', type=Path, required=True)
    compare(p.parse_args())
