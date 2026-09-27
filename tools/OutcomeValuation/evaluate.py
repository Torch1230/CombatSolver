#!/usr/bin/env python3
"""Compare automatic production search with a frozen learned candidate on separated scenes.

Each arm uses a fresh process. Validation can guide later changes; a test manifest
cannot be silently reused to choose a different model or implementation.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from dataset import audit, evaluation_manifests, read, verify_resolved_loadout


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def candidate_identity(args, separation):
    # Search budgets and dataset edits are model selection too. Freezing only
    # the binaries would allow repeated tuning against the same final test.
    return {**{name: hashlib.sha256(getattr(args, name).read_bytes()).hexdigest()
               for name in ('model', 'mod', 'harness')},
            'datasetSha256': separation['datasetSha256'],
            'settings': {'beam': args.beam, 'nodes': args.nodes,
                         'budgetMilliseconds': 30000, 'dop': 1,
                         'searchMode': 'Coordinator', 'potionPolicy': 'Disabled',
                         'stopAtZeroLoss': True, 'batchSeconds': args.seconds}}


def claim_final_test(manifest, identity):
    usage = manifest.parent / 'test-usage.json'
    if usage.exists() and read(usage)['identity'] != identity:
        raise ValueError('This test was already opened for another candidate, configuration or dataset; '
                         'it is now development data')
    if not usage.exists():
        write(usage, {'identity': identity,
                     'openedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()})


def evaluate(args):
    cases = read(args.manifest)['cases']
    if {c['split'] for c in cases} not in ({'validation'}, {'test'}):
        raise ValueError('Evaluate one nonempty validation or final test split at a time')
    manifests = [args.manifest]
    if any(c['split'] == 'test' for c in cases):
        if not args.validation_manifest:
            raise ValueError('Final test requires the development validation manifest for three-way separation')
        if {c['split'] for c in read(args.validation_manifest)['cases']} != {'validation'}:
            raise ValueError('Development manifest must contain only validation cases')
        manifests.insert(0, args.validation_manifest)
    separation = audit(read(args.train), evaluation_manifests(manifests))
    identity = candidate_identity(args, separation)
    args.out.mkdir(parents=True, exist_ok=False)
    environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(args.mod.resolve()))
    if any(c['split'] == 'test' for c in cases):
        claim_final_test(args.manifest, identity)
    report = {'identity': identity, 'separation': separation, 'records': [], 'completed': False}
    write(args.out / 'report.json', report)
    started = time.monotonic()
    pairs = []
    try:
        for index, case in enumerate(cases):
            arms = ['baseline', 'candidate'] if index % 2 == 0 else ['candidate', 'baseline']
            roots = []
            for arm in arms:
                target = args.out / case['id'] / arm
                target.mkdir(parents=True)
                command = ['dotnet', str(args.harness.resolve()), '--request', case['request'],
                           '--label', case['id'], '--out', str(target.resolve()), '--profile', 'Custom',
                           '--beam', str(args.beam), '--nodes', str(args.nodes), '--budget-ms', '30000',
                           '--dop', '1', '--search-mode', 'Coordinator', '--potion-policy', 'Disabled',
                           '--stop-at-zero-loss']
                command += (['--automatic-search', '--use-portfolio'] if arm == 'baseline' else
                            ['--objective-search', '--outcome-value-model', str(args.model.resolve())])
                write(target / 'command.json', command)
                remaining = args.seconds - (time.monotonic() - started)
                if remaining <= 0:
                    raise TimeoutError('Evaluation batch deadline reached')
                with (target / 'process.log').open('w') as log:
                    subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                   timeout=min(70, remaining), check=True, env=environment)
                result, quality = read(target / 'result.json'), read(target / 'quality.json')
                verify_resolved_loadout(case, target)
                captured = read(target / 'harness-result.json')['search']
                roots.append((captured['rootContinuationStamp'], captured['rootLiveStamp']))
                metrics = result['solverMetrics']
                record = {'case': case['id'], 'arm': arm, 'quality': quality['quality'],
                          'snapshotRisk': quality['snapshot']['hasRisk'],
                          'snapshotBoundary': quality['snapshot']['boundaryReason'],
                          'seconds': result['wallSeconds'], 'nodes': metrics['totalExpanded'],
                          'transitions': metrics['totalTransitions'], 'rss': result['peakWorkingSetBytes'],
                          'timeBoundary': result['timeBoundaryObserved']}
                report['records'].append(record)
                write(args.out / 'report.json', report)
                print(json.dumps(record), flush=True)
            if roots[0] != roots[1]:
                raise ValueError(f"Different native/predicted roots: {case['id']}")
            pairs.append({'id': case['id'], 'candidate': str((args.out / case['id'] / 'candidate/quality.json').resolve()),
                          'baseline': str((args.out / case['id'] / 'baseline/quality.json').resolve())})
        write(args.out / 'comparison-input.json', pairs)
        subprocess.run(['dotnet', str(args.harness.resolve()), '--compare-quality-batch',
                        str(args.out / 'comparison-input.json'), str(args.out / 'comparison.json')],
                       timeout=30, check=True, env=environment)
        report['completed'] = True
    finally:
        report['elapsedSeconds'] = time.monotonic() - started
        write(args.out / 'report.json', report)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('train', 'manifest', 'model', 'mod', 'harness', 'out'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--validation-manifest', type=Path,
                        help='Required when opening a final test; used only for structural separation')
    parser.add_argument('--beam', type=int, default=24)
    parser.add_argument('--nodes', type=int, default=12000)
    parser.add_argument('--seconds', type=int, default=600)
    options = parser.parse_args()
    if min(options.beam, options.nodes, options.seconds) <= 0:
        parser.error('Beam, nodes and seconds must be positive')
    evaluate(options)
