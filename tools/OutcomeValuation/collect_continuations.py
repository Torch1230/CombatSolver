#!/usr/bin/env python3
"""Finite, resumable training-root collection with a frozen learner and native teacher.

This does not fit, select or enable a model. Development/final manifests are used
only to audit separation; their requests are never sent to the search host.
"""
import argparse
import datetime
import os
from pathlib import Path
import threading

from dataset import audit, describe, digest, read, verify_resolved_loadout
from overnight import write
from paired_benchmark import file_sha, run_process


def collect(protocol_path, output):
    protocol = read(protocol_path)
    training = read(protocol['trainingManifest'])
    heldout = {'cases': [c for p in protocol['evaluationManifests'] for c in read(p)['cases']]}
    separation = audit(training, heldout)
    if {c['split'] for c in heldout['cases']} != {'validation', 'test'}:
        raise ValueError('Require development and sealed final manifests.')
    cases = {c['id']: c for c in training['cases'] if c['split'] == 'train'}
    entries = protocol['roots']
    if len(cases) != len(training['cases']) or len(entries) != len(cases):
        raise ValueError('Collection must name each unique training root exactly once.')
    if len({r['id'] for r in entries}) != len(entries) or {r['id'] for r in entries} != set(cases):
        raise ValueError('Collection roots differ from the audited training manifest.')
    for path, expected in protocol['hashes'].items():
        if file_sha(path) != expected:
            raise ValueError('Frozen collection input changed: ' + path)
    descriptors = {key: describe(case) for key, case in cases.items()}
    for entry in entries:
        if descriptors[entry['id']]['inputSha256'] != entry['inputSha256']:
            raise ValueError('Frozen native setup changed: ' + entry['id'])
    output.mkdir(parents=True, exist_ok=True)
    identity = digest(protocol)
    report_path = output / 'collection.json'
    report = read(report_path) if report_path.exists() else {
        'protocolSha256': identity, 'startedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'separation': separation, 'records': [], 'completed': False,
    }
    if report['protocolSha256'] != identity:
        raise ValueError('Cannot resume a different collection protocol.')
    os.sched_setaffinity(0, protocol['cpuAffinity'])
    environment = dict(os.environ, DOTNET_PROCESSOR_COUNT=str(len(protocol['cpuAffinity'])),
                       DOTNET_GCHeapHardLimit='0x200000000',
                       OFFLINE_HARNESS_COMBATSOLVER_DLL=protocol['mod'])
    completed = {r['id'] for r in report['records']}
    stop = threading.Event()
    for index, entry in enumerate(entries):
        if entry['id'] in completed:
            continue  # Recorded failures stay failures; no silent retry.
        case = cases[entry['id']]
        target = Path(entry['reuseDirectory']) if entry.get('reuseDirectory') else output / case['id']
        target.mkdir(parents=True, exist_ok=True)
        report.update(currentCase=case['id'], completed=False)
        write(report_path, report)
        if entry.get('reuseDirectory'):
            process = read(target / 'collection-record.json')
        else:
            command = ['dotnet', protocol['harness'], '--workspace', str(target / 'workspace'),
                       '--request', case['request'], '--label', case['id'], '--out', str(target),
                       *protocol['searchArgs'], '--objective-search', '--outcome-value-model',
                       protocol['model'], '--collect-outcome-values']
            process = run_process(command, target, environment, stop, protocol['processTimeoutSeconds'])
        record = {k: process[k] for k in ('processSeconds', 'returnCode', 'timedOut')}
        record.update(id=case['id'], directory=str(target), reused=bool(entry.get('reuseDirectory')))
        if process['returnCode'] != 0 or process['timedOut']:
            record['state'] = 'failed'
        else:
            verify_resolved_loadout(case, target)
            captured = read(target / 'harness-result.json')['search']
            prior = read(entry['priorResult'])['search']
            if any(captured[k] != prior[k] for k in ('rootLiveStamp', 'rootContinuationStamp')):
                raise ValueError('Native root differs from its original training collection: ' + case['id'])
            corrections = read(target / 'outcome-corrections.json')
            rows = read(target / 'outcome-correction-rows.json')
            result = read(target / 'result.json')
            trials = corrections['trials']
            record.update(state='completed', requested=corrections['requested'],
                          completed=corrections['completed'], labelled=len(rows),
                          wins=sum(row['Outcome']['Won'] for row in rows),
                          turns=[trial['turn'] for trial in trials],
                          teacherSeconds=corrections['elapsedMilliseconds'] / 1000,
                          newlyWitnessed=sum(t['before'] is None and t['after'] is not None for t in trials),
                          unknownToVictory=sum(t['before'] is None and t['after'] is not None
                                               and t['after']['won'] for t in trials),
                          peakWorkingSetBytes=result['peakWorkingSetBytes'],
                          rootKey=digest([captured[k] for k in ('rootLiveStamp', 'rootContinuationStamp')]),
                          rowHashes={name: file_sha(target / name) for name in
                                     ('outcome-rows.json', 'imitation-rows.json', 'outcome-correction-rows.json')})
        report['records'].append(record)
        write(report_path, report)
        print(f"{index + 1}/{len(entries)} {case['id']}: {record['state']}; "
              f"queries={record.get('completed', 0)}, labels={record.get('labelled', 0)}", flush=True)
    report.update(completed=True, currentCase=None,
                  finishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    write(report_path, report)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('protocol', type=Path)
    parser.add_argument('out', type=Path)
    args = parser.parse_args()
    collect(args.protocol.resolve(), args.out.resolve())
