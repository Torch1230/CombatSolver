#!/usr/bin/env python3
"""Frozen, serial ABBA/BAAB development battles; failures never disappear.

The input protocol names an engine, model, audited manifests, file hashes and
search settings. No fitting or final-test search occurs here. Each successful
run is reused on resume; failed runs are recorded, never silently retried.
"""
import argparse
import collections
import datetime
import hashlib
import os
from pathlib import Path
import signal
import subprocess
import threading
import time

from dataset import audit, describe, digest, read, verify_resolved_loadout
from evaluate import quality_evidence_issues
from overnight import write


def file_sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run_process(command, directory, environment, stop, timeout):
    write(directory / 'command.json', command)
    started = time.monotonic()
    with (directory / 'process.log').open('w') as log:
        process = subprocess.Popen(command, env=environment, stdin=subprocess.DEVNULL,
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        timed_out = False
        try:
            while process.poll() is None:
                if stop.is_set():
                    raise InterruptedError('Benchmark interrupted; unfinished run is not complete')
                if time.monotonic() - started >= timeout:
                    timed_out = True
                    break
                stop.wait(.1)
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
        return {'processSeconds': time.monotonic() - started,
                'returnCode': process.returncode, 'timedOut': timed_out}


def comparisons_for_case(case, records):
    valid = {r['run']: r for r in records if r['state'] == 'completed'}
    pairs = []
    for left, right, kind in [('candidate-0', 'baseline-0', 'repeat-0'),
                              ('candidate-1', 'baseline-1', 'repeat-1'),
                              ('candidate-0', 'candidate-1', 'candidate-stability'),
                              ('baseline-0', 'baseline-1', 'baseline-stability')]:
        if left in valid and right in valid:
            pairs.append({'id': case['id'] + '/' + kind,
                          'candidate': valid[left]['qualityPath'],
                          'baseline': valid[right]['qualityPath']})
    return pairs


def benchmark(protocol_path):
    protocol = read(protocol_path)
    for path, expected in protocol['hashes'].items():
        if file_sha(path) != expected:
            raise ValueError('Frozen input changed: ' + path)
    cases = read(protocol['manifest'])['cases']
    if not cases or {c['split'] for c in cases} != {'validation'}:
        raise ValueError('This runner only opens development validation')
    separation = audit(read(protocol['trainingManifest']),
                       {'cases': cases + read(protocol['priorDevelopmentManifest'])['cases']
                        + read(protocol['sealedManifest'])['cases']})
    if separation['datasetSha256'] != protocol['separationSha256']:
        raise ValueError('Changed scenario partition')
    os.sched_setaffinity(0, protocol['cpuAffinity'])
    os.nice(5)
    environment = dict(os.environ, **protocol['environment'],
                       OFFLINE_HARNESS_COMBATSOLVER_DLL=protocol['mod'])
    out = Path(protocol['out'])
    out.mkdir(parents=True, exist_ok=True)
    identity = file_sha(protocol_path)
    report_path = out / 'report.json'
    report = read(report_path) if report_path.exists() else {
        'protocolSha256': identity, 'records': [], 'comparisons': [], 'completed': False,
        'finalTestOpened': False, 'runtimeModelEnabled': False}
    if report['protocolSha256'] != identity:
        raise ValueError('Cannot resume another benchmark into the same output')
    stop = threading.Event()
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: stop.set())
    began = time.monotonic()
    def save(phase, **extra):
        report.update(phase=phase, updatedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                      **extra)
        write(report_path, report)
        write(out / 'status.json', {k: v for k, v in report.items()
                                    if k not in ('records', 'comparisons')}
              | {'runsRecorded': len(report['records']),
                 'runStates': dict(collections.Counter(r['state'] for r in report['records'])),
                 'pid': os.getpid(), 'sessionSeconds': time.monotonic() - began})
    try:
        for index, case in enumerate(cases):
            descriptor = describe(case)
            order = (['baseline-0', 'candidate-0', 'candidate-1', 'baseline-1'] if index % 2 == 0
                     else ['candidate-0', 'baseline-0', 'baseline-1', 'candidate-1'])
            for run in order:
                if any(r['id'] == case['id'] and r['run'] == run for r in report['records']):
                    continue
                if stop.is_set() or (out / 'STOP').exists():
                    raise InterruptedError('Stop requested')
                target = out / case['id'] / run
                target.mkdir(parents=True, exist_ok=True)
                arm = run.split('-')[0]
                save('running', currentCase=case['id'], currentRun=run, casesFinished=index)
                command = ['dotnet', protocol['harness'], '--workspace', str(target / 'workspace'),
                           '--request', case['request'], '--label', case['id'], '--out', str(target),
                           *protocol['searchArgs']]
                command += (['--automatic-search', '--use-portfolio'] if arm == 'baseline' else
                            ['--objective-search', '--outcome-value-model', protocol['model']])
                process = run_process(command, target, environment, stop, protocol['processTimeoutSeconds'])
                record = {'id': case['id'], 'run': run, 'arm': arm, 'character': descriptor['character'],
                          'encounter': descriptor['encounter'], 'kind': case['kind'], **process}
                if process['timedOut'] or process['returnCode'] != 0:
                    record.update(state='failed', reason='timeout' if process['timedOut'] else 'process failure')
                else:
                    verify_resolved_loadout(case, target)
                    result, quality = read(target / 'result.json'), read(target / 'quality.json')
                    captured = read(target / 'harness-result.json')['search']
                    root = digest([captured[k] for k in ('rootLiveStamp', 'rootContinuationStamp')])
                    previous = [r for r in report['records'] if r['id'] == case['id'] and r['state'] == 'completed']
                    if any(r['rootSha256'] != root for r in previous):
                        raise ValueError('Different native roots: ' + case['id'])
                    metrics = result['solverMetrics']
                    record.update(state='completed', rootSha256=root, quality=quality['quality'],
                                  issues=quality_evidence_issues(quality), qualityPath=str(target / 'quality.json'),
                                  seconds=result['wallSeconds'], rss=result['peakWorkingSetBytes'],
                                  managedHeap=result['peakManagedHeapBytes'],
                                  managedLive=result['peakManagedLiveBytes'],
                                  allocated=result['totalAllocatedBytes'],
                                  timeBoundary=result['timeBoundaryObserved'],
                                  metrics={k: metrics[k] for k in ('totalExpanded', 'totalTransitions',
                                           'totalElapsedMilliseconds', 'totalWorkerAllocatedBytes',
                                           'totalGcPauseMilliseconds', 'totalGen2Collections')})
                report['records'].append(record)
                save('running')
            target = out / case['id']
            compare_output = target / 'comparison.json'
            if not compare_output.exists():
                pairs = comparisons_for_case(case, [r for r in report['records'] if r['id'] == case['id']])
                compare_input = target / 'comparison-input.json'
                write(compare_input, pairs)
                compare_dir = target / 'compare'
                compare_dir.mkdir(exist_ok=True)
                result = run_process(['dotnet', protocol['harness'], '--compare-quality-batch',
                                      str(compare_input), str(compare_output)], compare_dir, environment, stop, 30)
                if result['returnCode'] or result['timedOut']:
                    raise RuntimeError('Authoritative quality comparator failed')
            existing = {r['id'] for r in report['comparisons']}
            report['comparisons'].extend(r for r in read(compare_output) if r['id'] not in existing)
            save('running', casesFinished=index + 1)
            print(f"Completed {index + 1}/{len(cases)} cases", flush=True)
        save('completed', completed=True)
    except BaseException:
        save('interrupted-or-failed')
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('protocol', type=Path)
    benchmark(parser.parse_args().protocol.resolve())
