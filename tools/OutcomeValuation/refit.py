#!/usr/bin/env python3
"""Refit existing, scenario-audited observations; inherit their entire training cost."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from dataset import audit, evaluation_manifests, read, verify_resolved_loadout


def refit(args):
    started = time.monotonic()
    previous = read(args.prior_training / 'training-budget.json')
    inherited = previous.get('totalModelTrainingSeconds', previous['elapsedSeconds'])
    limit = min(args.seconds, 1800 - inherited)
    manifest_bytes = args.manifest.read_bytes()
    if not previous.get('completed') or previous['manifestSha256'] != hashlib.sha256(manifest_bytes).hexdigest():
        raise ValueError('Refitting requires complete prior training with the same manifest')
    if not 60 <= limit <= 1800:
        raise ValueError('Insufficient budget after inherited collection and fitting')
    separation = audit(json.loads(manifest_bytes), evaluation_manifests(
        args.evaluation_manifest, require_final_test=True))
    if previous.get('separation', {}).get('training') != separation['training']:
        raise ValueError('Underlying training requests/loadouts changed since observation collection')
    cases = [c for c in json.loads(manifest_bytes)['cases'] if c['split'] == 'train']
    inputs = read(args.prior_training / 'training-inputs.json')
    if len(cases) != len(inputs):
        raise ValueError('Observation roots differ from the training manifest')
    root_evidence = Path(previous.get('rootEvidenceDirectory', args.prior_training))
    files = []
    for case, entry in zip(cases, inputs):
        paths = [entry] if isinstance(entry, str) else entry
        if not paths or any(Path(p).parent.name != case['id'] for p in paths):
            raise ValueError('Observation files do not match their declared root')
        verify_resolved_loadout(case, root_evidence / case['id'])
        files.extend({'path': p, 'sha256': hashlib.sha256(Path(p).read_bytes()).hexdigest()} for p in paths)
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / 'training-inputs.json').write_text(json.dumps(inputs, indent=2) + '\n')
    report = {'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'completed': False, 'refitOnly': True, 'limitSeconds': limit,
              'priorTrainingSeconds': inherited, 'preparationSeconds': 0,
              'manifestSha256': previous['manifestSha256'],
              'rootEvidenceDirectory': str(root_evidence.resolve()),
              'modSha256': hashlib.sha256(args.mod.read_bytes()).hexdigest(),
              'harnessSha256': hashlib.sha256(args.harness.read_bytes()).hexdigest(),
              'inheritedObservations': files, 'records': previous['records'], 'separation': separation}

    def save():
        report['elapsedSeconds'] = time.monotonic() - started
        report['totalModelTrainingSeconds'] = inherited + report['elapsedSeconds']
        (args.out / 'training-budget.json').write_text(json.dumps(report, indent=2) + '\n')

    save()
    command = ['dotnet', str(args.harness.resolve()), '--fit-outcome-values',
               str((args.out / 'training-inputs.json').resolve()), str((args.out / 'model.json').resolve())]
    (args.out / 'command.json').write_text(json.dumps(command, indent=2) + '\n')
    environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(args.mod.resolve()))
    try:
        remaining = limit - (time.monotonic() - started)
        if remaining <= 0:
            raise TimeoutError('Input verification consumed refitting budget')
        with (args.out / 'fit.log').open('w') as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                           check=True, timeout=remaining, env=environment)
        report['completed'] = True
    finally:
        save()
    print(json.dumps({k: report[k] for k in ('completed', 'elapsedSeconds', 'totalModelTrainingSeconds')}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('prior-training', 'manifest', 'harness', 'mod', 'out'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--evaluation-manifest', type=Path, action='append', required=True,
                        help='Repeat for development validation and sealed final test')
    parser.add_argument('--seconds', type=int, default=1800)
    options = parser.parse_args()
    if not 60 <= options.seconds <= 1800:
        parser.error('--seconds must be between 60 and 1800')
    refit(options)
