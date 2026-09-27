#!/usr/bin/env python3
"""Collect compared victory witnesses and fit a pairwise tree ranker within 30 minutes.

Only manifest cases with split=train are read. Labels come from simulated completed
victories; an unexplored or pruned node is not a failure/optimality label. The old
search is a data-collection policy, so its coverage bias remains in the data.
"""
import argparse
import datetime
import hashlib
import json
import subprocess
import time
from pathlib import Path

from dataset import audit


def train(args):
    started = time.monotonic()
    deadline = started + args.seconds
    args.out.mkdir(parents=True, exist_ok=False)
    manifest_bytes = args.manifest.read_bytes()
    separation = (audit(json.loads(manifest_bytes), json.loads(args.evaluation_manifest.read_text()))
                  if args.evaluation_manifest else None)
    cases = [c for c in json.loads(manifest_bytes)['cases'] if c['split'] == 'train']
    if not cases:
        raise ValueError('No training roots in manifest')
    report = {'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'limitSeconds': args.seconds, 'manifestSha256': hashlib.sha256(manifest_bytes).hexdigest(),
              'harnessSha256': hashlib.sha256(args.harness.read_bytes()).hexdigest(),
              'modSha256': hashlib.sha256(args.mod.read_bytes()).hexdigest(), 'records': []}
    if separation:
        report['separation'] = separation
    files = []

    def save():
        report['elapsedSeconds'] = time.monotonic() - started
        (args.out / 'training-budget.json').write_text(json.dumps(report, indent=2))

    try:
        for case in cases:
            target = args.out / case['id']
            target.mkdir()
            command = ['dotnet', str(args.harness.resolve()), '--request', case['request'],
                       '--label', case['id'], '--out', str(target.resolve()), '--profile', 'Custom',
                       '--beam', '16', '--nodes', '6000', '--budget-ms', '12000', '--dop', '1',
                       '--search-mode', 'Coordinator', '--potion-policy', 'Disabled',
                       '--stop-at-zero-loss', '--use-portfolio', '--automatic-search', '--collect-outcome-values']
            (target / 'command.json').write_text(json.dumps(command, indent=2))
            remaining = deadline - time.monotonic() - 60
            if remaining <= 0:
                raise TimeoutError('Collection consumed fitting reserve')
            with (target / 'process.log').open('w') as output:
                subprocess.run(command, stdout=output, stderr=subprocess.STDOUT,
                               timeout=min(70, remaining), check=True)
            path = target / 'outcome-rows.json'
            files.append(str(path.resolve()))
            result = json.loads((target / 'result.json').read_text())
            report['records'].append({'id': case['id'], 'rows': len(json.loads(path.read_text())),
                                      'peakWorkingSetBytes': result['peakWorkingSetBytes']})
            save()
        paths = args.out / 'training-inputs.json'
        paths.write_text(json.dumps(files, indent=2))
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError('No remaining fitting time')
        with (args.out / 'fit.log').open('w') as output:
            subprocess.run(['dotnet', str(args.harness.resolve()), '--fit-outcome-values',
                            str(paths.resolve()), str((args.out / 'model.json').resolve())],
                           stdout=output, stderr=subprocess.STDOUT, timeout=remaining, check=True)
        report['completed'] = True
    finally:
        save()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['manifest', 'harness', 'mod', 'out']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=1800)
    parser.add_argument('--evaluation-manifest', type=Path,
                        help='Optional frozen evaluation manifest; reject scenario overlap before any process starts')
    options = parser.parse_args()
    if not 60 <= options.seconds <= 1800:
        parser.error('--seconds must be between 60 and 1800')
    train(options)
