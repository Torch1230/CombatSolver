#!/usr/bin/env python3
"""Conservative, per-case acceptance gate for independent A1/B1/B2/A2 runs.

Input is a JSON array of {id, baseline: [run_dir, run_dir], candidate: [...]}.
Missing, failed or risky evidence never passes. A timed baseline can establish a
request-quality comparison, but never completed-work throughput. This checks saved
prediction quality; native deployment/differential validation is still required.
"""
import argparse
import json
import math
from pathlib import Path

# This batch is pinned to upstream 72363308. A future preset needs a new batch,
# not a reduced budget with the old VeryHigh label.
FROZEN_BUDGET = dict(profile='VeryHigh', beamWidth=135, maxExpandedNodes=500000,
                     maxCardBranchesPerNode=72, maxPileChoiceBranchesPerAction=42,
                     maxHandChoiceBranchesPerAction=54, maxDegreeOfParallelism=16,
                     budgetMilliseconds=300000, searchMode='Coordinator',
                     usePortfolio=True, fixedSearchBudget=False,
                     enableNoGcRegion=True, noGcRegionBudgetGigabytes=16)


def read_run(directory, baseline=False):
    directory = Path(directory)
    result = json.loads((directory / 'result.json').read_text())
    quality = json.loads((directory / 'quality.json').read_text())
    policy = json.loads((directory / 'search-policy.json').read_text())
    if result.get('status') != 'Passed' or result.get('exitCode', 0) != 0:
        raise ValueError(f'{directory}: unsuccessful run')
    if not all(isinstance(result.get(k), bool) for k in ('timeBoundary', 'timeBoundaryObserved')):
        raise ValueError(f'{directory}: missing time-boundary evidence')
    if not baseline and (result['timeBoundary'] or result['timeBoundaryObserved']):
        raise ValueError(f'{directory}: time boundary prevents completed-work acceptance')
    if quality['snapshot']['hasRisk']:
        raise ValueError(f'{directory}: prediction risk')
    if not result.get('rootContinuationStamp'):
        raise ValueError(f'{directory}: missing root identity')
    if result.get('searchMode') != 'Coordinator' or result.get('profile') != 'VeryHigh':
        raise ValueError(f'{directory}: requires full Coordinator / VeryHigh')
    if any(result['budget'].get(k) != v for k, v in FROZEN_BUDGET.items()):
        raise ValueError(f'{directory}: requires the frozen production VeryHigh / DOP16 budget')
    seconds = result['wallSeconds']
    # Kernel HWM is preferred when provided by an independent process monitor.
    # Retain sampled RSS for old screening runs, but it cannot pass the final gate.
    memory = result.get('peakProcessWorkingSetBytes')
    if memory is None:
        raise ValueError(f'{directory}: missing process high-water memory measurement')
    if not all(isinstance(x, (float, int)) and math.isfinite(x) and x > 0 for x in (seconds, memory)):
        raise ValueError(f'{directory}: invalid time/memory')
    return result, quality, policy


def no_quality_regression(baseline, candidate):
    """A component-wise gate: permits rounds/actions to differ, never trades HP.

    Unknown quality fields require equality. Full production policy remains the
    authority for interpretation; component-wise dominance is deliberately stricter.
    """
    lower = {'outstandingStolenResource', 'projectedBattleHpLost', 'strategicHpDeficit',
             'potionStrategicCost', 'projectedBattlePotionCount', 'enemyHp', 'deathSaveUseCount'}
    higher = {'won', 'survives', 'growthHpCredit', 'growthRewardCount'}
    ignored = {'combatEndedTurn', 'score'}
    if baseline.keys() != candidate.keys():
        return False
    for key, value in baseline.items():
        if key in ignored:
            continue
        if key in lower and candidate[key] <= value:
            continue
        if key in higher and candidate[key] >= value:
            continue
        if candidate[key] != value:
            return False
    return True


def check_case(case):
    report = {'id': case['id'], 'passed': False}
    try:
        if len(case['baseline']) < 2 or len(case['candidate']) < 2:
            raise ValueError('At least two independent samples per arm are required')
        a = [read_run(p, baseline=True) for p in case['baseline']]
        b = [read_run(p) for p in case['candidate']]
        reference = a[0]
        for run in a + b:
            if run[0]['rootContinuationStamp'] != reference[0]['rootContinuationStamp']:
                raise ValueError('Different combat roots')
            if run[0].get('catalogFingerprint') != reference[0].get('catalogFingerprint'):
                raise ValueError('Different input catalogs')
            if run[0]['budget'] != reference[0]['budget'] or run[2] != reference[2]:
                raise ValueError('Different budgets or search policies')
        quality_pass = all(no_quality_regression(ar[1]['quality'], br[1]['quality']) for ar in a for br in b)
        a_seconds = [r[0]['wallSeconds'] for r in a]
        b_seconds = [r[0]['wallSeconds'] for r in b]
        a_memory = [r[0]['peakProcessWorkingSetBytes'] for r in a]
        b_memory = [r[0]['peakProcessWorkingSetBytes'] for r in b]
        slow = max(a_seconds) > 20
        speedup = min(a_seconds) / max(b_seconds)
        memory_ratio = max(b_memory) / min(a_memory)
        report.update(slow=slow, baselineSeconds=a_seconds, candidateSeconds=b_seconds,
                      baselinePeakBytes=a_memory, candidatePeakBytes=b_memory,
                      minimumSpeedup=speedup, maximumMemoryRatio=memory_ratio,
                      baselineTimed=any(r[0]['timeBoundary'] or r[0]['timeBoundaryObserved'] for r in a),
                      baselineQualityStable=all(r[1]['quality'] == reference[1]['quality'] for r in a),
                      qualityPassed=quality_pass,
                      speedPassed=not slow or speedup >= 2,
                      memoryPassed=memory_ratio <= 1.1)
        report['passed'] = quality_pass and report['speedPassed'] and report['memoryPassed']
    except (KeyError, ValueError, TypeError, OSError) as error:
        report['error'] = str(error)
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pairs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    cases = json.loads(args.pairs.read_text())
    reports = [check_case(c) for c in cases]
    passed = bool(reports) and all(r['passed'] for r in reports)
    with args.output.open('x') as stream:
        json.dump({'passed': passed, 'cases': reports, 'scope': 'Offline predictions; native validation required'}, stream, indent=2)
    print(f'{sum(r["passed"] for r in reports)}/{len(reports)} cases passed; overall={passed}')
    raise SystemExit(0 if passed else 1)
