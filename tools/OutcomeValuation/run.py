#!/usr/bin/env python3
"""Small, frozen, zero-training rollout experiment with a whole-batch deadline.

The deadline includes input generation, process startup, search and comparison.
All failures and time cutoffs remain in the report. No third-party Python packages.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools/ContextualOrdering'))
from generate import FAMILIES


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def generate(out, suite='screen'):
    cases = []
    # Ten known mechanism families, but new seeds. This is a screening cohort,
    # not independent mechanism generalization and not an optimality dataset.
    for family, character, hand, draw in (FAMILIES if suite == 'screen' else ()):
        multi = family == 'target_order'
        hp = 40 if multi else 110
        request = {
            'schemaVersion': 1, 'scenarioId': 'OUTCOME-' + family.upper(),
            'characterId': character,
            'encounterId': 'CORPSE_SLUGS_NORMAL' if multi else 'FUZZY_WURM_CRAWLER_WEAK',
            'seed': 'OUTCOME-20260927-' + family,
            'enemyCurrentHp': hp, 'initialEnemyMaxHps': [hp] * (3 if multi else 1),
            'initialEnemyCurrentHps': [hp] * (3 if multi else 1),
            'initialPlayerHp': 35, 'initialPlayerMaxHp': 80, 'initialPlayerEnergy': 3,
            'clearRunDeck': True, 'clearPlayerPiles': True,
            'cards': [{'cardId': card, 'pile': pile, 'treatAsDeckCard': True}
                      for pile, cards in [('Hand', hand.split()), ('Draw', draw.split())]
                      for card in cards],
            'powers': [{'powerId': 'STRENGTH_POWER', 'target': 'Enemy',
                        'targetIndex': i, 'amount': 4} for i in range(3 if multi else 1)],
            'fixedSearchBudget': True, 'timeoutSeconds': 120,
            'stopAfterInitialSolverResultAssertion': True,
        }
        path = out / 'requests' / (family + '.json')
        write(path, request)
        cases.append({'id': family, 'request': str(path), 'kind': 'curated'})
    characters = ('REGENT', 'NECROBINDER') if suite == 'screen' else (
        'IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER')
    for character in characters:
        label = 'random-' + character.lower()
        scenario = {
            'schemaVersion': 1, 'seed': ('OUTCOME-20260927-' if suite == 'screen'
                                        else 'OUTCOME-20260927-heldout-') + character,
            'characterId': character, 'encounterKind': 'Monster' if suite == 'screen' else 'Elite', 'ascension': 10,
            'actIndex': 1, 'includeStartingDeck': True, 'includeStartingRelics': True,
            'includeAscendersBane': True, 'applyRelicObtainEffects': False,
            'characterCards': {'count': 12, 'ids': [], 'upgradeLevels': 1},
            'colorlessCards': {'count': 2, 'ids': []}, 'relics': {'count': 3, 'ids': []},
            'potions': {'count': 0, 'ids': []}, 'mode': 'Search', 'fixedSearchBudget': True,
        }
        scenario_path = out / 'scenarios' / (label + '.json')
        write(scenario_path, scenario)
        path = out / 'requests' / (label + '.json')
        write(path, {'schemaVersion': 1, 'scenarioId': label,
                     'generatedScenarioPath': str(scenario_path), 'fixedSearchBudget': True,
                     'timeoutSeconds': 120})
        cases.append({'id': label, 'request': str(path), 'kind': 'random'})
    write(out / 'manifest.json', {'cases': cases, 'training': False})
    return cases


def run_process(command, log, deadline):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return 'BatchDeadline', None
    with log.open('w') as stream:
        try:
            result = subprocess.run(command, cwd=REPO, stdout=stream,
                                    stderr=subprocess.STDOUT, timeout=min(120, remaining), check=False)
            return ('Completed' if result.returncode == 0 else 'Failed'), result.returncode
        except subprocess.TimeoutExpired:
            return 'Timeout', None


def read(path):
    return json.loads(path.read_text())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--seconds', type=float, default=1200)
    parser.add_argument('--nodes', type=int, default=12000)
    parser.add_argument('--budget-ms', type=int, default=30000)
    parser.add_argument('--probes', type=int, default=8)
    parser.add_argument('--limit', type=int, default=12)
    parser.add_argument('--suite', choices=('screen', 'heldout'), default='screen')
    parser.add_argument('--verify-incremental', action='store_true')
    parser.add_argument('--resume', action='store_true', help='Reuse completed outputs with identical commands.')
    args = parser.parse_args()
    if not 0 < args.seconds <= 1800 or not 1 <= args.limit <= 12:
        parser.error('Batch seconds must be in (0, 1800], limit in 1..12.')
    if args.nodes < 2 or not 1 <= args.probes <= 16 or args.budget_ms <= 0:
        parser.error('Invalid search budget/probe count.')
    args.out = args.out.resolve()
    args.out.mkdir(parents=True, exist_ok=args.resume)
    harness = REPO / 'tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
    mod = REPO / '.godot/mono/temp/bin/Release/CombatSolver.dll'
    signature = {'nodes': args.nodes, 'beam': 24, 'probes': args.probes, 'suite': args.suite,
                 'budgetMs': args.budget_ms, 'deadlineSeconds': args.seconds, 'limit': args.limit,
                 'verifyIncremental': args.verify_incremental,
                 'harnessSha256': hashlib.sha256(harness.read_bytes()).hexdigest(),
                 'modSha256': hashlib.sha256(mod.read_bytes()).hexdigest()}
    clock_path = args.out / 'budget.json'
    if args.resume:
        previous = read(clock_path)
        if previous['signature'] != signature:
            raise ValueError('Resume requires identical budget, inputs and assemblies.')
        # A completed invocation contributes its measured elapsed time. After an
        # interrupted invocation, conservatively charge all wall time since start.
        prior_seconds = previous['spentSeconds']
        if previous.get('activeSince') is not None:
            prior_seconds += max(0, time.time() - previous['activeSince'])
    else:
        prior_seconds = 0
    started = time.monotonic()
    deadline = started + args.seconds - prior_seconds
    write(clock_path, {'signature': signature, 'spentSeconds': prior_seconds, 'activeSince': time.time()})
    cases = (read(args.out / 'manifest.json')['cases'] if args.resume else generate(args.out, args.suite))[:args.limit]
    inputs = hashlib.sha256(json.dumps(cases, sort_keys=True).encode())
    for case in cases:
        request = Path(case['request'])
        inputs.update(request.read_bytes())
        scenario = read(request).get('generatedScenarioPath')
        if scenario:
            inputs.update(Path(scenario).read_bytes())
    input_hash = inputs.hexdigest()
    if args.resume and read(args.out / 'plan.json')['inputSha256'] != input_hash:
        raise ValueError('Resume input files changed.')
    write(args.out / 'plan.json', {**signature, 'inputSha256': input_hash,
                                  'cases': cases, 'trainingSeconds': 0})
    records, pairs = [], []
    consecutive_failures = 0
    for index, case in enumerate(cases):
        root_records = []
        # Alternate paired ordering to avoid giving the same variant every cold start.
        variants = ('baseline', 'rollout') if index % 2 == 0 else ('rollout', 'baseline')
        for variant in variants:
            if time.monotonic() >= deadline:
                break
            out = args.out / case['id'] / variant
            out.mkdir(parents=True, exist_ok=args.resume)
            command = ['dotnet', str(harness), '--request', case['request'], '--label', case['id'],
                       '--out', str(out), '--profile', 'Custom', '--beam', '24',
                       '--nodes', str(args.nodes), '--budget-ms', str(args.budget_ms),
                       '--dop', '1', '--search-mode', 'Evaluate', '--potion-policy', 'Disabled',
                       '--stop-at-zero-loss']
            if variant == 'rollout':
                command += ['--outcome-probes', str(args.probes)]
            if args.verify_incremental:
                command += ['--verify-incremental']
            begin = time.monotonic()
            reused = args.resume and (out / 'result.json').exists()
            if reused:
                if read(out / 'command.json') != command or read(out / 'result.json')['status'] != 'Passed':
                    raise ValueError('Cannot reuse mismatched/unsuccessful output: ' + str(out))
                status, code = 'Completed', 0
            else:
                write(out / 'command.json', command)
                status, code = run_process(command, out / 'process.log', deadline)
            record = {'id': case['id'], 'variant': variant, 'status': status, 'exitCode': code,
                      'processSeconds': None if reused else time.monotonic() - begin,
                      'reused': reused, 'directory': str(out)}
            if status == 'Completed':
                result = read(out / 'result.json')
                record.update(quality=read(out / 'quality.json')['quality'],
                              metrics=result['solverMetrics'], timeBoundary=result['timeBoundaryObserved'],
                              searchWallSeconds=result['wallSeconds'],
                              root=result['rootContinuationStamp'],
                              catalog=result.get('catalogFingerprint'))
                if variant == 'rollout':
                    record['probes'] = read(out / 'outcome-probes.json')
            records.append(record)
            consecutive_failures = 0 if status == 'Completed' else consecutive_failures + 1
            root_records.append(record)
            with (args.out / 'observations.jsonl').open('a') as stream:
                stream.write(json.dumps(record, ensure_ascii=False) + '\n')
            print(f"{case['id']} {variant}: {status} seconds={record['processSeconds']} "
                  f"quality={record.get('quality', {})}", flush=True)
            if consecutive_failures >= 2:
                break
        if len(root_records) == 2 and all(r['status'] == 'Completed' for r in root_records):
            a, b = root_records
            if a['root'] != b['root'] or a['catalog'] != b['catalog']:
                raise ValueError('Root mismatch: ' + case['id'])
            pairs.append({'id': case['id'], 'candidate': str(args.out / case['id'] / 'rollout/quality.json'),
                          'baseline': str(args.out / case['id'] / 'baseline/quality.json')})
        if consecutive_failures >= 2:
            break
    write(args.out / 'pairs.json', pairs)
    comparison_status, _ = run_process(
        ['dotnet', str(harness), '--compare-quality-batch', str(args.out / 'pairs.json'),
         str(args.out / 'comparison.json')], args.out / 'comparison.log', deadline)
    elapsed = prior_seconds + time.monotonic() - started
    write(clock_path, {'signature': signature, 'spentSeconds': elapsed, 'activeSince': None})
    write(args.out / 'summary.json', {'collectionAndComparisonSeconds': elapsed,
                                     'trainingSeconds': 0, 'comparisonStatus': comparison_status,
                                     'completedPairs': len(pairs), 'plannedPairs': len(cases),
                                     'records': records})
    return 0 if comparison_status == 'Completed' and len(pairs) == len(cases) else 2


if __name__ == '__main__':
    sys.exit(main())
