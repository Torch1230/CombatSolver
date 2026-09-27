#!/usr/bin/env python3
"""Collect completed continuation witnesses and fit a pairwise tree ranker within 30 minutes.

Only manifest cases with split=train are read. Labels come from simulated completed
victories and engine-confirmed terminal defeats; an unexplored or pruned node is
not a failure/optimality label. The old
search is a data-collection policy, so its coverage bias remains in the data.
"""
import argparse
import datetime
import hashlib
import json
import os
import subprocess
import time
from pathlib import Path

from dataset import audit


def train(args):
    started = time.monotonic()
    prior_budget = json.loads((args.prior_training / 'training-budget.json').read_text()) if args.prior_training else None
    prior_seconds = (prior_budget.get('totalModelTrainingSeconds', prior_budget['elapsedSeconds'])
                     if prior_budget else 0)
    limit = min(args.seconds, 1800 - prior_seconds)
    if limit < 60:
        raise ValueError('Insufficient remaining budget including previous collection/fitting')
    deadline = started + limit
    args.out.mkdir(parents=True, exist_ok=False)
    manifest_bytes = args.manifest.read_bytes()
    environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(args.mod.resolve()))
    prior_inputs = None
    if prior_budget:
        if not prior_budget.get('completed') or prior_budget['manifestSha256'] != hashlib.sha256(manifest_bytes).hexdigest():
            raise ValueError('Prior training must be complete and use the identical training manifest')
        if args.roll_in_model.read_bytes() != (args.prior_training / 'model.json').read_bytes():
            raise ValueError('Roll-in model must match the accounted prior training result')
        prior_inputs = json.loads((args.prior_training / 'training-inputs.json').read_text())
    separation = (audit(json.loads(manifest_bytes), json.loads(args.evaluation_manifest.read_text()))
                  if args.evaluation_manifest else None)
    cases = [c for c in json.loads(manifest_bytes)['cases'] if c['split'] == 'train']
    if not cases:
        raise ValueError('No training roots in manifest')
    if prior_inputs is not None and len(prior_inputs) != len(cases):
        raise ValueError('Prior training inputs do not match root count')
    report = {'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'limitSeconds': limit, 'priorTrainingSeconds': prior_seconds,
              'manifestSha256': hashlib.sha256(manifest_bytes).hexdigest(),
              'harnessSha256': hashlib.sha256(args.harness.read_bytes()).hexdigest(),
              'modSha256': hashlib.sha256(args.mod.read_bytes()).hexdigest(), 'records': []}
    if separation:
        report['separation'] = separation
    if args.roll_in_model:
        report['rollInModelSha256'] = hashlib.sha256(args.roll_in_model.read_bytes()).hexdigest()
    files = []

    def save():
        report['elapsedSeconds'] = time.monotonic() - started
        report['totalModelTrainingSeconds'] = prior_seconds + report['elapsedSeconds']
        (args.out / 'training-budget.json').write_text(json.dumps(report, indent=2))

    try:
        for index, case in enumerate(cases):
            target = args.out / case['id']
            target.mkdir()
            command = ['dotnet', str(args.harness.resolve()), '--request', case['request'],
                       '--label', case['id'], '--out', str(target.resolve()), '--profile', 'Custom',
                       '--beam', '16', '--nodes', '6000', '--budget-ms', '12000', '--dop', '1',
                       '--search-mode', 'Coordinator', '--potion-policy', 'Disabled',
                       '--stop-at-zero-loss', '--use-portfolio', '--automatic-search', '--collect-outcome-values']
            if args.roll_in_model:
                command.remove('--use-portfolio')
                command.remove('--automatic-search')
                command += ['--objective-search', '--outcome-value-model', str(args.roll_in_model.resolve())]
            (target / 'command.json').write_text(json.dumps(command, indent=2))
            remaining = deadline - time.monotonic() - 60
            if remaining <= 0:
                raise TimeoutError('Collection consumed fitting reserve')
            with (target / 'process.log').open('w') as output:
                subprocess.run(command, stdout=output, stderr=subprocess.STDOUT,
                               timeout=min(70, remaining), check=True, env=environment)
            path = target / 'outcome-rows.json'
            if args.prior_training:
                previous = args.prior_training / case['id']
                old_root, new_root = (json.loads((directory / 'harness-result.json').read_text())['search']
                                      for directory in (previous, target))
                for stamp in ('rootContinuationStamp', 'rootLiveStamp'):
                    if old_root[stamp] != new_root[stamp]:
                        raise ValueError(f"Root changed between collection policies: {case['id']} / {stamp}")
                inherited = prior_inputs[index]
                inherited = [inherited] if isinstance(inherited, str) else inherited
                if len(inherited) >= 8:
                    raise ValueError('At most eight collection policies per root')
                files.append([*inherited, str(path.resolve())])
            else:
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
                           stdout=output, stderr=subprocess.STDOUT, timeout=remaining, check=True, env=environment)
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
    parser.add_argument('--roll-in-model', type=Path, help='Collect trajectories from this frozen learned search policy')
    parser.add_argument('--prior-training', type=Path,
                        help='Mix prior observations for identical roots; previous training cost counts toward 1800s')
    options = parser.parse_args()
    if not 60 <= options.seconds <= 1800:
        parser.error('--seconds must be between 60 and 1800')
    if options.prior_training and not options.roll_in_model:
        parser.error('--prior-training requires --roll-in-model')
    train(options)
