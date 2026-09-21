#!/usr/bin/env python3
"""Serial fixed-fixture A/B observations. Not a replacement for native assertions.

Both DLLs must support the same fixed-fixture adapter. Evaluate mode only (one
solver). Every subprocess has a 120-second deadline. Existing outputs are refused;
timings never overlap. Exit 2 means incomparable budget-limited observations,
exit 1 means a comparable difference, fixture failure, or harness failure.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

REPO = Path(__file__).resolve().parents[2]
METRICS = (
    'TotalExpanded', 'TotalTransitions', 'TotalChoiceBranches',
    'ProjectedBattleHpLost', 'FinalHp', 'FinalEnemyHp', 'CombatEndedTurn',
    'PotionCount', 'Score', 'OnlyDeathRoutes', 'Boundary',
    'TotalElapsedMilliseconds', 'TotalWorkerAllocatedBytes',
    'CycleReplayAttempts', 'CycleReplayActions', 'CycleReplayVictories',
    'TurnLayerBudgetStops', 'TurnLayerTimeBudgetStops', 'TurnLayerNodeBudgetStops',
)
QUALITY = ('ProjectedBattleHpLost', 'FinalHp', 'FinalEnemyHp',
           'CombatEndedTurn', 'PotionCount', 'OnlyDeathRoutes', 'Boundary')


def read(path):
    return json.loads(path.read_text())


def budget_observation(result, out):
    """Old DLLs lack split counters; recover reasons from their flushed diagnostics."""
    logs = list(out.glob('logs/*/*.jsonl'))
    counts = {'time': 0, 'nodes': 0}
    global_time = False
    for path in logs:
        for line in path.read_text().splitlines():
            if not line.strip():
                continue
            message = json.loads(line).get('Message', '')
            match = re.search(r'\bTURN_LAYER_BUDGET reason=(\w+)', message)
            if match:
                reason = match[1]
                if reason not in counts:
                    raise ValueError(f'Unknown turn-layer budget reason: {reason}')
                counts[reason] += 1
            global_time |= 'SEARCH_TIME_BUDGET' in message
    metrics = result['solverMetrics']
    time_count = metrics.get('TurnLayerTimeBudgetStops')
    node_count = metrics.get('TurnLayerNodeBudgetStops')
    issues = []
    if time_count is not None and node_count is not None:
        source = 'solverMetrics'
        if min(time_count, node_count) < 0 or metrics.get('TurnLayerBudgetStops') != time_count + node_count:
            issues.append('split budget counters do not sum to total')
        if logs and counts != {'time': time_count, 'nodes': node_count}:
            issues.append('budget counters disagree with diagnostic log')
    elif logs and time_count is None and node_count is None:
        source = 'diagnosticLog'
        time_count, node_count = counts['time'], counts['nodes']
        if metrics.get('TurnLayerBudgetStops', time_count + node_count) != time_count + node_count:
            issues.append('logged budget reasons do not sum to total')
    else:
        source = 'unavailable'
    return {
        'scope': 'solver', 'source': source,
        'turnLayerTimeStops': time_count, 'turnLayerNodeStops': node_count,
        'timeBoundaryObserved': bool(result.get('timeBoundaryObserved', False)
                                    or global_time or counts['time'] or time_count),
        'diagnosticIssues': issues,
    }


def observation_status(budget, failures):
    # Preserve failed fixture observations, but do not call wall-clock truncation
    # an implementation regression or silently admit it to a performance average.
    if budget['diagnosticIssues']:
        return 'DiagnosticsMismatch'
    if budget['timeBoundaryObserved']:
        return 'TimeLimited'
    if budget['source'] == 'unavailable':
        return 'BudgetEvidenceUnavailable'
    return 'FixtureMismatch' if failures else 'Comparable'


def run(case, label, dll, args, suite):
    out = args.out / case['name'] / label
    out.mkdir(parents=True, exist_ok=False)
    command = ['dotnet', str(args.harness), '--request', str(REPO / case['request']),
               '--label', label, '--out', str(out), '--profile', suite['profile'],
               '--nodes', str(case['nodes']), '--budget-ms', str(suite['budgetMilliseconds']),
               '--dop', str(args.dop), '--potion-policy', case['potionPolicy'],
               '--search-mode', 'Evaluate']
    if case['stopAtZeroLoss']:
        command.append('--stop-at-zero-loss')
    if args.verify_incremental:
        command.append('--verify-incremental')
    env = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(dll))
    observation = {'label': label, 'command': command, 'searchMode': 'Evaluate'}
    with (out / 'stdout.log').open('w') as log:
        try:
            process = subprocess.run(command, cwd=REPO, env=env, stdout=log,
                                     stderr=subprocess.STDOUT, timeout=120, check=False)
        except subprocess.TimeoutExpired:
            return dict(observation, valid=False, status='HarnessFailed', error='Process exceeded 120 seconds')
    result_path = out / 'harness-result.json'
    if process.returncode or not result_path.exists():
        return dict(observation, valid=False, status='HarnessFailed', exitCode=process.returncode,
                    error='Harness failed; inspect stdout.log')
    result = read(result_path)
    metrics = result['solverMetrics']
    observation.update(metrics={k: metrics.get(k) for k in METRICS},
                       root=result['search']['rootContinuationStamp'])
    for key in ('wallSeconds', 'peakManagedHeapBytes', 'peakManagedLiveBytes',
                'peakWorkingSetBytes', 'totalAllocatedBytes'):
        observation[key] = result[key]
    route = read(out / 'route.json')
    # These explicit suite checks are deliberately separate from native expected* assertions.
    failures = []
    if metrics['FinalEnemyHp'] != 0 or metrics['OnlyDeathRoutes']:
        failures.append('surviving victory')
    if 'enemyCount' in case and len(re.findall(r'(?:^|;)E\d+=', observation['root'])) != case['enemyCount']:
        failures.append('root enemy count')
    played = {a['cardId'] for a in route if a['kind'] == 'PlayCard'}
    if not set(case.get('requiredRouteCards', [])) <= played:
        failures.append('required route cards')
    for key, value in case.get('expectedMetrics', {}).items():
        if metrics[key] != value:
            failures.append(key)
    if metrics['TotalChoiceBranches'] < case.get('minimumChoiceBranches', 0):
        failures.append('choice branches')
    if label.startswith('B') and not 0 <= metrics['CycleReplayActions'] <= 4096:
        failures.append('per-solver replay action cap')
    observation['fixtureCheckFailures'] = failures
    observation['budgetObservation'] = budget_observation(result, out)
    observation['status'] = observation_status(observation['budgetObservation'], failures)
    observation['valid'] = observation['status'] == 'Comparable'
    observation['routeActions'] = len(route)
    observation['routeSha256'] = hashlib.sha256(
        json.dumps(route, sort_keys=True).encode()).hexdigest()
    return observation


def compare_runs(name, runs):
    valid = all(r['valid'] for r in runs)
    item = {'name': name, 'valid': valid, 'runs': runs}
    if all('root' in r for r in runs):
        a = runs[0]
        item['sameRoot'] = all(r['root'] == a['root'] for r in runs)
        item['sameRoute'] = all(r['routeSha256'] == a['routeSha256'] for r in runs)
        item['sameQualityMetrics'] = all(all(r['metrics'][k] == a['metrics'][k] for k in QUALITY) for r in runs)
    if any(r['status'] in ('HarnessFailed', 'FixtureMismatch', 'DiagnosticsMismatch') for r in runs):
        item['status'] = 'Failed'
    elif not valid:
        item['status'] = 'Inconclusive'
    else:
        item['status'] = 'Equivalent' if all(item[k] for k in ('sameRoot', 'sameRoute', 'sameQualityMetrics')) else 'Different'
    return item


def report_exit_code(cases):
    if any(c['status'] in ('Failed', 'Different') for c in cases):
        return 1
    return 2 if any(c['status'] == 'Inconclusive' for c in cases) else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--suite', type=Path, default=REPO / 'coverage/unattended/loop-boundaries-20260921/suite.json')
    parser.add_argument('--baseline-dll', type=Path, required=True)
    parser.add_argument('--candidate-dll', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--harness', type=Path, default=REPO / 'tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll')
    parser.add_argument('--cases', nargs='+', help='Only these case names')
    parser.add_argument('--dop', type=int, default=1)
    parser.add_argument('--verify-incremental', action='store_true', help='Correctness only; no performance claims')
    parser.add_argument('--abba', action='store_true', help='Preplanned A1/B1/B2/A2 samples')
    args = parser.parse_args()
    args.out = args.out.resolve()
    args.baseline_dll = args.baseline_dll.resolve(strict=True)
    args.candidate_dll = args.candidate_dll.resolve(strict=True)
    suite = read(args.suite)
    if suite.get('searchMode', 'Evaluate') != 'Evaluate' or any(
            c.get('searchMode', 'Evaluate') != 'Evaluate' for c in suite['cases']):
        parser.error('This suite supports Evaluate only; 4096 is not a Coordinator request cap')
    cases = [c for c in suite['cases'] if not args.cases or c['name'] in args.cases]
    if not cases or (args.cases and set(args.cases) != {c['name'] for c in cases}):
        parser.error('Unknown or empty case selection')
    args.out.mkdir(parents=True, exist_ok=False)
    report = {'mode': 'incremental-correctness' if args.verify_incremental else 'offline-observation',
              'searchMode': 'Evaluate', 'replayBudgetScope': 'solver', 'dop': args.dop, 'cases': []}
    for case in cases:
        order = ['A1', 'B1', 'B2', 'A2'] if args.abba else ['A1', 'B1']
        runs = [run(case, label, args.baseline_dll if label.startswith('A') else args.candidate_dll,
                    args, suite) for label in order]
        item = compare_runs(case['name'], runs)
        report['cases'].append(item)
        (args.out / 'comparison.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps({k: v for k, v in item.items() if k != 'runs'}), flush=True)
    # Route/quality differences are observations needing review, not silently accepted.
    return report_exit_code(report['cases'])


if __name__ == '__main__':
    raise SystemExit(main())
