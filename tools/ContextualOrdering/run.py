#!/usr/bin/env python3
"""Serial, bounded corpus measurements; failures/time limits remain explicit evidence.

Variants are explicit JSON records with name,dll and optional beam/nodes/arguments.
All variants for a root run consecutively. This is collection, not native acceptance.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tools/OfflineSearchHarness'))
from run_loop_boundaries import budget_observation


def read(path):
    return json.loads(Path(path).read_text())


def run(case, variant, args):
    out = args.out / case['id'] / variant['name']
    out.mkdir(parents=True, exist_ok=False)
    mode = variant.get('searchMode', 'Evaluate')
    harness = Path(variant.get('harness', args.harness)).resolve(strict=True)
    command = ['dotnet', str(harness), '--request', case['request'],
               '--label', case['id'], '--out', str(out.resolve()), '--profile', 'Custom',
               '--beam', str(variant.get('beam', args.beam)), '--nodes', str(variant.get('nodes', args.nodes)),
               '--budget-ms', str(args.budget_ms), '--dop', '1', '--search-mode', mode,
               '--potion-policy', case.get('potionPolicy', 'Smart'), '--stop-at-zero-loss', '--measure-phases']
    command += variant.get('arguments', [])
    env = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(Path(variant['dll']).resolve(strict=True)))
    (out / 'command.json').write_text(json.dumps({'command': command, 'dll': env['OFFLINE_HARNESS_COMBATSOLVER_DLL']}, indent=2))
    started = time.monotonic()
    with (out / 'process.log').open('w') as log:
        try:
            completed = subprocess.run(command, cwd=REPO, env=env, stdout=log, stderr=subprocess.STDOUT,
                                       timeout=120, check=False)
            status = 'Completed' if completed.returncode == 0 else 'ProcessFailed'
            exit_code = completed.returncode
        except subprocess.TimeoutExpired:
            status, exit_code = 'ProcessTimeout', None
    record = {'case': case['id'], 'family': case['family'], 'split': case['split'],
              'variant': variant['name'], 'status': status, 'exitCode': exit_code,
              'processSeconds': time.monotonic() - started}
    if status == 'Completed':
        result = read(out / 'harness-result.json')
        record['quality'] = read(out / 'quality.json')['quality']
        record['metrics'] = result['solverMetrics']
        record['budget'] = budget_observation(result, out, mode)
        record['status'] = ('DiagnosticsMismatch' if record['budget']['diagnosticIssues'] else
                            'TimeLimited' if record['budget']['timeBoundaryObserved'] else
                            'BudgetEvidenceUnavailable' if record['budget']['source'] == 'unavailable' else 'Comparable')
        record['purpose'] = 'OrderingCollection' if '--observe-ordering' in command else 'SearchObservation'
    with (args.out / 'observations.jsonl').open('a') as stream:
        stream.write(json.dumps(record, ensure_ascii=False) + '\n')
    metrics = record.get('metrics', {})
    print(f"{case['id']} {variant['name']} {record['status']} "
          f"HP={metrics.get('ProjectedBattleHpLost')} T={metrics.get('CombatEndedTurn')} "
          f"expanded/transitions={metrics.get('TotalExpanded')}/{metrics.get('TotalTransitions')} "
          f"{record['processSeconds']:.2f}s", flush=True)
    return record


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--variants', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--split', choices=('train', 'validation', 'test'), default='train')
    parser.add_argument('--kind', choices=('curated', 'random'))
    parser.add_argument('--case', action='append')
    parser.add_argument('--limit', type=int)
    parser.add_argument('--nodes', type=int, default=20000)
    parser.add_argument('--beam', type=int, default=24)
    parser.add_argument('--budget-ms', type=int, default=60000)
    parser.add_argument('--harness', type=Path, default=REPO / 'tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll')
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    cases = [c for c in read(args.manifest)['cases'] if c['split'] == args.split
             and (not args.kind or c['kind'] == args.kind) and (not args.case or c['id'] in args.case)]
    if args.limit is not None:
        cases = cases[:args.limit]
    if not cases:
        raise ValueError('No cases selected')
    records = [run(case, variant, args) for case in cases for variant in read(args.variants)]
    (args.out / 'summary.json').write_text(json.dumps(records, ensure_ascii=False, indent=2) + '\n')
    sys.exit(1 if any(r['status'] in ('ProcessFailed', 'ProcessTimeout', 'DiagnosticsMismatch') for r in records) else
             2 if any(r['status'] != 'Comparable' for r in records) else 0)
