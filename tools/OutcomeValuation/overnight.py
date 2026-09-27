#!/usr/bin/env python3
"""Resumable, explicitly time-bounded offline collection and development evaluation.

Linux supervisor only; uses the existing portable harness and fitter. Never opens
final-test outcomes or changes the production model. No third-party dependencies.
"""
import argparse
from collections import Counter, defaultdict, deque
from concurrent.futures import ThreadPoolExecutor, as_completed
import datetime as dt
import fcntl
import hashlib
import json
import math
import os
from pathlib import Path
import random
import shutil
import signal
import subprocess
import threading
import time

from dataset import audit, describe, evaluation_manifests, read, verify_resolved_loadout
from evaluate import quality_evidence_issues


def write(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')
    temporary.replace(path)


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def family(identifier):
    for suffix in ('_NORMAL', '_WEAK'):
        if identifier.endswith(suffix):
            return identifier[:-len(suffix)]
    return identifier


def encounter_buckets(catalog, heldout):
    excluded = {describe(c)['encounter'] for c in heldout['cases']}
    buckets = defaultdict(list)
    seen = set()
    for encounter in sorted(catalog['encounters'], key=lambda e: e['id']):
        key = (encounter['actIndex'], encounter['roomType'])
        name = family(encounter['id'])
        if name in excluded or name in seen:
            continue
        seen.add(name)
        buckets[key].append(encounter)
    if not buckets:
        raise ValueError('No training encounters remain after held-out exclusion')
    return [buckets[k] for k in sorted(buckets)]


def specification(catalog, buckets, seed, index):
    characters = sorted(catalog['characters'])
    character = characters[index % len(characters)]
    bucket_index = index // len(characters) % len(buckets)
    cycle = index // (len(characters) * len(buckets))
    bucket = buckets[bucket_index]
    encounter = bucket[(cycle + index % len(characters)) % len(bucket)]
    rng = random.Random(f'{seed}:{index}:loadout')
    return {'schemaVersion': 1, 'seed': f'{seed}-{index:06d}',
            'characterId': character, 'encounterId': encounter['id'],
            'encounterKind': encounter['roomType'], 'ascension': rng.choice([0, 5, 10]),
            'actIndex': encounter['actIndex'], 'includeStartingDeck': True,
            'includeStartingRelics': True, 'includeAscendersBane': True,
            'applyRelicObtainEffects': False,
            'characterCards': {'count': rng.choice([8, 12, 18]), 'ids': [], 'upgradeLevels': 1},
            'colorlessCards': {'count': 2, 'ids': []},
            'relics': {'count': rng.choice([1, 3, 6]), 'ids': []},
            'potions': {'count': 2, 'ids': []}, 'mode': 'Search', 'fixedSearchBudget': True}


def balanced_roots(records, maximum, seed):
    """Select actual roots, equally rotating available role/act/kind strata."""
    groups = defaultdict(list)
    for record in records:
        if record['state'] == 'collected' and record['hasPreferences']:
            groups[tuple(record['stratum'])].append(record)
    rng = random.Random(seed)
    queues = []
    for key in sorted(groups):
        members = groups[key]
        rng.shuffle(members)
        queues.append(deque(members))
    rng.shuffle(queues)
    selected = []
    while queues and len(selected) < maximum:
        remaining = []
        for queue in queues:
            if len(selected) == maximum:
                break
            selected.append(queue.popleft())
            if queue:
                remaining.append(queue)
        queues = remaining
    return selected


def training_inputs(records, maximum_rows_per_root, partition='shared'):
    if partition not in ('shared', 'character'):
        raise ValueError('Unknown training partition')
    paths = [r['input'] for r in records]
    # Default remains compatible with frozen legacy harnesses. Non-default
    # budgets require the explicit schema, never silently drop the requested cap.
    if maximum_rows_per_root == 2048 and partition == 'shared':
        return paths
    specification = {'schemaVersion': 1, 'maximumRowsPerRoot': maximum_rows_per_root, 'roots': paths}
    if partition != 'shared':
        specification['partition'] = partition
    return specification


def witnessed_rows(path):
    rows = read(path)
    outcomes = defaultdict(set)
    for row in rows:
        outcome = row['Outcome']
        victory = outcome['Won'] and outcome['Survives'] and not row['CompletedDefeat']
        defeat = not outcome['Won'] and not outcome['Survives'] and row['CompletedDefeat']
        # This is a label census, not feature conversion. The fitter requires
        # explicit projection before schema-6 vectors can train the current model.
        if (row['FeatureSchema'] not in (6, 7, 8) or not (victory or defeat) or outcome['Score'] != 0
                or not row['Groups'] or row['RemainingActions'] < 0 or not row['Features']
                or any(not math.isfinite(v) for v in row['Features'].values())):
            raise ValueError('Invalid completed witness; quarantining the entire root')
        # This is a conservative eligibility filter, not a replacement comparator.
        # The C# fitter remains the authority on meaningful policy preferences.
        # The fitter deliberately treats two defeats as tied, regardless of
        # remaining enemy HP or how quickly the player died.
        signature = json.dumps([outcome, row['RemainingActions']], sort_keys=True) if victory else 'defeat'
        for group in row['Groups']:
            outcomes[group].add(signature)
    return len(rows), any(len(values) > 1 for values in outcomes.values())


def freeze_manifest(source, target):
    target.mkdir(parents=True)
    cases = []
    for original in read(source)['cases']:
        case = dict(original)
        directory = target / case['id']
        directory.mkdir()
        request = read(case['request'])
        if request.get('generatedScenarioPath'):
            shutil.copy2(request['generatedScenarioPath'], directory / 'specification.json')
            request['generatedScenarioPath'] = str(directory / 'specification.json')
        write(directory / 'request.json', request)
        case['request'] = str(directory / 'request.json')
        if case.get('loadout'):
            shutil.copy2(case['loadout'], directory / 'loadout.json')
            case['loadout'] = str(directory / 'loadout.json')
        cases.append(case)
    write(target / 'manifest.json', {'schemaVersion': 1, 'cases': cases})
    return target / 'manifest.json'


def freeze_engine(harness, mod, engine):
    shutil.copytree(harness.resolve().parent, engine / 'harness')
    shutil.copy2(mod, engine / 'CombatSolver.dll')
    runtime = engine / 'harness/OfflineSearchHarness.runtimeconfig.json'
    config = read(runtime)
    properties = config['runtimeOptions']['configProperties']
    # Freeze managed dependency DLLs too, so Steam updates cannot mix assemblies.
    for name in ('Sts2DataDir', 'RitsuLibDir', 'RitsuLibCompatDir', 'RitsuLibSharedDir'):
        dependency = engine / name
        dependency.mkdir()
        for path in Path(properties[name]).glob('*.dll'):
            shutil.copy2(path, dependency / path.name)
        properties[name] = str(dependency)
    properties['CombatSolverDll'] = str(engine / 'CombatSolver.dll')
    write(runtime, config)


def freeze_baseline(case, source, target):
    verify_resolved_loadout(case, source)
    target.mkdir(parents=True)
    for name in ('quality.json', 'result.json', 'harness-result.json'):
        shutil.copy2(source / name, target / name)
    if case.get('loadout'):
        (target / 'evidence').mkdir()
        shutil.copy2(source / 'evidence/generated-scenario.loadout.json',
                     target / 'evidence/generated-scenario.loadout.json')


def initialize(args):
    began = time.monotonic()
    root = args.out.resolve()
    root.mkdir(parents=True, exist_ok=False)
    freeze_engine(args.harness, args.mod, root / 'engine')
    if args.fit_harness:
        freeze_engine(args.fit_harness, args.fit_mod, root / 'training-engine')
    scripts = root / 'scripts'
    scripts.mkdir()
    for name in ('overnight.py', 'dataset.py', 'evaluate.py'):
        shutil.copy2(Path(__file__).with_name(name), scripts / name)
    validation = freeze_manifest(args.validation, root / 'inputs/validation')
    sealed = freeze_manifest(args.sealed_test, root / 'inputs/sealed-test')
    if ({c['split'] for c in read(validation)['cases']} != {'validation'}
            or {c['split'] for c in read(sealed)['cases']} != {'test'}):
        raise ValueError('Separate validation and sealed final-test manifests are mandatory')
    baseline_index = read(args.baselines)
    baselines = {}
    for case in read(validation)['cases']:
        entry = baseline_index[case['id']]
        if entry['identity']['mod'] != sha(args.mod):
            raise ValueError('Historical baseline belongs to a different search implementation')
        expected = {'beam': 24, 'nodes': 12000, 'budgetMilliseconds': 30000, 'dop': 1,
                    'searchMode': 'Coordinator', 'potionPolicy': 'Disabled', 'stopAtZeroLoss': True}
        if any(entry['identity']['settings'][k] != v for k, v in expected.items()):
            raise ValueError('Historical baseline has incompatible search settings')
        source = Path(entry['directory'])
        target = root / 'baselines' / case['id']
        freeze_baseline(case, source, target)
        baselines[case['id']] = {**entry, 'directory': str(target)}
    write(root / 'baseline-index.json', baselines)
    shutil.copy2(args.catalog, root / 'catalog.json')
    deadline = dt.datetime.fromisoformat(args.until)
    if deadline.tzinfo is None or deadline.timestamp() <= time.time():
        raise ValueError('--until must be a future time with an explicit timezone')
    heldout = evaluation_manifests([validation, sealed], require_final_test=True)
    buckets = encounter_buckets(read(root / 'catalog.json'), heldout)
    if args.prior_job:
        prior = read(args.prior_job / 'status.json')
        if prior['state'] == 'running':
            raise ValueError('Stop the prior job before importing its evidence')
        # Keep process costs and failed fits as well as usable rows. Native input
        # and row hashes are rechecked; changing engines cannot reset the ledger.
        inherited = {r['case']['id']: r for r in prior['cases']}
        inherited.update({r['case']['id']: r for r in (
            read(p) for p in (args.prior_job / 'cases').glob('*/record.json'))})
        prior['cases'] = list(inherited.values())
        for record in prior['cases']:
            if record['state'] == 'collecting':
                record['state'] = 'interrupted'
            if record['state'] == 'collected':
                audit({'cases': [record['case']]}, heldout)
                if sha(record['input']) != record['inputSha256']:
                    raise ValueError('Prior training rows changed: ' + record['input'])
                record['rows'], record['hasPreferences'] = witnessed_rows(record['input'])
        write(root / 'prior-status.json', prior)
    plan = {'schema': 1, 'createdUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
            'until': args.until, 'workers': args.workers, 'batchSize': 45,
            'maximumRootsPerFit': args.fit_roots, 'maximumRowsPerRoot': args.fit_rows_per_root,
            'fitPartition': args.fit_partition,
            'maxCases': args.max_cases, 'maxBytes': args.max_gib * 1024**3,
            'fitTimeoutSeconds': args.fit_seconds, 'seed': args.seed,
            'fittingEngine': 'training-engine' if args.fit_harness else 'engine',
            'priorJob': str(args.prior_job.resolve()) if args.prior_job else None,
            'preparationSeconds': time.monotonic() - began,
            'trainingBuckets': [(b[0]['actIndex'], b[0]['roomType'], len(b)) for b in buckets],
            'screenIds': args.screen_id, 'historicalBaselineTimingIsSpeedProof': False,
            'productionPromotion': False, 'finalTestSearch': False,
            'costPolicy': f'Overnight collection/training explicitly authorized; each fit capped at {args.fit_seconds}s',
            'frozenFiles': {str(p.relative_to(root)): sha(p) for p in root.rglob('*') if p.is_file()}}
    if len(set(plan['screenIds'])) != len(plan['screenIds']) or not set(plan['screenIds']) <= set(baselines):
        raise ValueError('Screen IDs must be distinct validation cases')
    write(root / 'plan.json', plan)
    print(json.dumps({'job': str(root), 'plan': plan}, ensure_ascii=False), flush=True)


class StopRequested(Exception):
    pass


class Job:
    def __init__(self, root):
        self.root = root.resolve()
        self.plan = read(root / 'plan.json')
        for name, checksum in self.plan['frozenFiles'].items():
            if sha(root / name) != checksum:
                raise ValueError(f'Frozen job input changed: {name}')
        self.deadline = dt.datetime.fromisoformat(self.plan['until']).timestamp()
        self.stop = threading.Event()
        self.lock = threading.Lock()
        self.started = time.monotonic()
        self.environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(root / 'engine/CombatSolver.dll'),
                                DOTNET_PROCESSOR_COUNT='4', DOTNET_GCHeapHardLimit='0x200000000')
        self.harness = root / 'engine/harness/OfflineSearchHarness.dll'
        self.fitting_engine = root / self.plan.get('fittingEngine', 'engine')
        self.heldout = evaluation_manifests([root / 'inputs/validation/manifest.json',
                                            root / 'inputs/sealed-test/manifest.json'], require_final_test=True)
        self.validation = read(root / 'inputs/validation/manifest.json')['cases']
        self.baselines = read(root / 'baseline-index.json')
        self.catalog = read(root / 'catalog.json')
        self.buckets = encounter_buckets(self.catalog, self.heldout)
        self.state = (read(root / 'status.json') if (root / 'status.json').exists() else
                      read(root / 'prior-status.json') if (root / 'prior-status.json').exists() else {
                          'rounds': [], 'cases': [], 'activeSeconds': 0,
                          'childSeconds': {}, 'latestRollIn': None})
        self.prior_seconds = self.state['activeSeconds']
        self.state.update(pid=os.getpid(), processStartTicks=Path('/proc/self/stat').read_text().split()[21],
                          state='running', until=self.plan['until'])

    def check_stop(self):
        if self.stop.is_set() or (self.root / 'STOP').exists() or time.time() >= self.deadline:
            raise StopRequested('deadline, STOP file or termination signal')

    def save(self, phase):
        self.state.update(phase=phase, activeSeconds=self.prior_seconds + time.monotonic() - self.started,
                          updatedUtc=dt.datetime.now(dt.timezone.utc).isoformat())
        write(self.root / 'status.json', self.state)
        counts = Counter(c['state'] for c in self.state['cases'])
        collected = [c for c in self.state['cases'] if c['state'] == 'collected']
        eligible = sum(c['hasPreferences'] for c in collected)
        (self.root / 'STATUS.md').write_text(
            f"State: {self.state['state']} / {phase}\n\nPID: {os.getpid()}\n\n"
            f"Deadline: {self.plan['until']}\n\nCases: {dict(counts)}\n\n"
            f"Rounds: {len(self.state['rounds'])}\n\n"
            f"Collected rows: {sum(c['rows'] for c in collected)}; eligible preference roots: {eligible}\n\n"
            f"Active seconds (including evaluation): {self.state['activeSeconds']:.1f}\n\n"
            "Research only. Final test remains sealed. No production model is enabled.\n")

    def command(self, args, directory, kind, timeout):
        self.check_stop()
        directory.mkdir(parents=True, exist_ok=True)
        harness = self.fitting_engine / 'harness/OfflineSearchHarness.dll' if kind == 'fit' else self.harness
        environment = (dict(self.environment, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(self.fitting_engine / 'CombatSolver.dll'))
                       if kind == 'fit' else self.environment)
        command = ['dotnet', str(harness), *map(str, args)]
        write(directory / f'{kind}-command.json', command)
        began = time.monotonic()
        with (directory / f'{kind}.log').open('w') as output:
            process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
                                       stdin=subprocess.DEVNULL, env=environment, start_new_session=True)
            try:
                while process.poll() is None:
                    self.check_stop()
                    if time.monotonic() - began >= timeout:
                        raise subprocess.TimeoutExpired(command, timeout)
                    self.stop.wait(0.25)
                if process.returncode:
                    raise subprocess.CalledProcessError(process.returncode, command)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()
                elapsed = time.monotonic() - began
                with self.lock:
                    totals = self.state['childSeconds']
                    totals[kind] = totals.get(kind, 0) + elapsed
                write(directory / f'{kind}-process.json', {'seconds': elapsed, 'returnCode': process.returncode})

    def search_args(self, case, directory, collection=False):
        return ['--workspace', directory / 'workspace', '--request', case['request'], '--label', case['id'],
                '--out', directory, '--profile', 'Custom', '--beam', 16 if collection else 24,
                '--nodes', 6000 if collection else 12000, '--budget-ms', 12000 if collection else 30000,
                '--dop', 1, '--search-mode', 'Coordinator', '--potion-policy', 'Disabled']

    def collect(self, index, roll_in):
        self.check_stop()
        identifier = f'root-{index:06d}'
        directory = self.root / 'cases' / identifier
        directory.mkdir(parents=True, exist_ok=False)
        spec = specification(self.catalog, self.buckets, self.plan['seed'], index)
        write(directory / 'specification.json', spec)
        request = directory / 'request.json'
        write(request, {'schemaVersion': 1, 'scenarioId': identifier.upper(),
                        'generatedScenarioPath': str(directory / 'specification.json'),
                        'timeoutSeconds': 120, 'fixedSearchBudget': True})
        case = {'id': identifier, 'character': spec['characterId'], 'family': 'native-' + spec['encounterId'],
                'split': 'train', 'request': str(request),
                'loadout': str(directory / 'setup/evidence/generated-scenario.loadout.json')}
        record = {'case': case, 'index': index, 'state': 'collecting',
                  'stratum': [spec['characterId'], spec['actIndex'], spec['encounterKind']],
                  'rollInModel': roll_in if index % 4 == 3 else None}
        write(directory / 'record.json', record)
        try:
            self.command(['--workspace', directory / 'workspace', '--request', request, '--label', identifier,
                          '--out', directory / 'setup', '--milestone', 'M1'], directory, 'setup', 70)
            write(directory / 'separation.json', audit({'cases': [case]}, self.heldout))
            target = directory / 'search'
            args = self.search_args(case, target, collection=True) + ['--collect-outcome-values']
            args += (['--objective-search', '--outcome-value-model', record['rollInModel']]
                     if record['rollInModel'] else ['--automatic-search', '--use-portfolio'])
            self.command(args, target, 'collect', 70)
            verify_resolved_loadout(case, target)
            path = target / 'outcome-rows.json'
            count, preferences = witnessed_rows(path)
            record.update(state='collected', rows=count, hasPreferences=preferences,
                          input=str(path), inputSha256=sha(path),
                          selectedOutcomeIssues=quality_evidence_issues(read(target / 'quality.json')),
                          peakWorkingSetBytes=read(target / 'result.json')['peakWorkingSetBytes'])
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError) as error:
            # A failed isolated experiment is retained, never a fabricated label.
            record.update(state='quarantined', error=f'{type(error).__name__}: {error}')
        finally:
            write(directory / 'record.json', record)
        return record

    def evaluate(self, model, directory):
        directory.mkdir()
        by_id = {c['id']: c for c in self.validation}
        screen = [by_id[i] for i in self.plan['screenIds']]
        remaining = [c for c in self.validation if c['id'] not in self.plan['screenIds']]
        report = {'model': str(model), 'modelSha256': sha(model), 'records': [], 'comparisons': [],
                  'completed': False, 'historicalBaselineTimingIsSpeedProof': False}
        for stage, cases in [('screen', screen), ('extended', remaining)]:
            pairs = []
            for case in cases:
                target = directory / case['id']
                self.command(self.search_args(case, target) + ['--stop-at-zero-loss', '--objective-search',
                             '--outcome-value-model', model], target, 'evaluate', 70)
                verify_resolved_loadout(case, target)
                baseline = Path(self.baselines[case['id']]['directory'])
                old_root, new_root = (read(p / 'harness-result.json')['search'] for p in (baseline, target))
                if any(old_root[k] != new_root[k] for k in ('rootLiveStamp', 'rootContinuationStamp')):
                    raise ValueError('Evaluation root drift: ' + case['id'])
                quality, old_quality = read(target / 'quality.json'), read(baseline / 'quality.json')
                result = read(target / 'result.json')
                record = {'id': case['id'], 'quality': quality['quality'], 'baseline': old_quality['quality'],
                          'issues': quality_evidence_issues(quality),
                          'baselineIssues': quality_evidence_issues(old_quality),
                          'seconds': result['wallSeconds'], 'rss': result['peakWorkingSetBytes'],
                          'nodes': result['solverMetrics']['totalExpanded'],
                          'timeBoundary': result['timeBoundaryObserved']}
                report['records'].append(record)
                write(directory / 'report.json', report)
                pairs.append({'id': case['id'], 'candidate': str(target / 'quality.json'),
                              'baseline': str(baseline / 'quality.json')})
            write(directory / f'{stage}-input.json', pairs)
            self.command(['--compare-quality-batch', directory / f'{stage}-input.json',
                          directory / f'{stage}-comparison.json'], directory, f'compare-{stage}', 30)
            comparisons = read(directory / f'{stage}-comparison.json')
            report['comparisons'].extend({'id': c['id'], 'materialComparison': c['materialComparison']}
                                         for c in comparisons)
            reliable = {r['id'] for r in report['records'] if not r['issues'] and not r['baselineIssues']}
            report['regressions'] = [c['id'] for c in report['comparisons']
                                     if c['id'] in reliable and c['materialComparison'] > 0]
            report['improvements'] = [c['id'] for c in report['comparisons']
                                      if c['id'] in reliable and c['materialComparison'] < 0]
            report['unverified'] = [r['id'] for r in report['records'] if r['issues'] or r['baselineIssues']]
            report['stage'] = stage
            write(directory / 'report.json', report)
            if stage == 'screen' and (report['regressions'] or report['unverified']):
                break
        report['completed'] = True
        report['fullDevelopmentCoverage'] = len(report['records']) == len(self.validation)
        report['eligibleForFinalReview'] = (report['fullDevelopmentCoverage']
                                            and not report['regressions'] and not report['unverified'])
        write(directory / 'report.json', report)
        return {k: report[k] for k in ('model', 'modelSha256', 'regressions', 'improvements',
                                     'unverified', 'fullDevelopmentCoverage', 'eligibleForFinalReview')}

    def fit(self):
        number = len(self.state['rounds'])
        selected = balanced_roots(self.state['cases'], self.plan['maximumRootsPerFit'],
                                  f"{self.plan['seed']}:fit:{number}")
        if len(selected) < 20:
            return
        directory = self.root / 'rounds' / f'{number:04d}'
        directory.mkdir(parents=True, exist_ok=False)
        manifest = {'cases': [r['case'] for r in selected]}
        write(directory / 'training-manifest.json', manifest)
        write(directory / 'separation.json', audit(manifest, self.heldout))
        for record in selected:
            if sha(record['input']) != record['inputSha256']:
                raise ValueError('Training rows changed: ' + record['input'])
        write(directory / 'training-inputs.json', training_inputs(selected, self.plan.get('maximumRowsPerRoot', 2048),
                                                               self.plan.get('fitPartition', 'shared')))
        record = {'number': number, 'directory': str(directory), 'state': 'fitting',
                  'rootCount': len(selected), 'inputHashes': {r['case']['id']: r['inputSha256'] for r in selected}}
        self.state['rounds'].append(record)
        self.save('fitting')
        try:
            self.command(['--fit-outcome-values', directory / 'training-inputs.json', directory / 'model.json'],
                         directory, 'fit', self.plan['fitTimeoutSeconds'])
            record['fitMetrics'] = json.loads((directory / 'fit.log').read_text().splitlines()[-1])
            record['state'] = 'evaluating'
            self.state['latestRollIn'] = str(directory / 'model.linear.json')
            self.save('evaluating')
            record['candidates'] = []
            for name in ('model.linear.json', 'model.json'):
                try:
                    result = self.evaluate(directory / name, directory / (name + '.evaluation'))
                except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
                    # A failed candidate does not erase another candidate's trial.
                    result = {'model': str(directory / name), 'state': 'failed',
                              'error': f'{type(error).__name__}: {error}'}
                record['candidates'].append(result)
                self.save('evaluating')
            record['state'] = 'completed'
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
            record.update(state='failed', error=f'{type(error).__name__}: {error}')
        finally:
            write(directory / 'record.json', record)
            self.save('round finished')

    def run(self):
        # Durable records win over an interrupted status checkpoint. Never assume
        # an interrupted search/fit succeeded, and never overwrite its evidence.
        inherited = read(self.root / 'prior-status.json')['cases'] if (self.root / 'prior-status.json').exists() else []
        cases = inherited + [read(p) for p in sorted((self.root / 'cases').glob('*/record.json'))]
        for record in cases:
            if record['state'] == 'collecting':
                record.update(state='interrupted', error='Previous supervisor did not complete this experiment')
                write(self.root / 'cases' / record['case']['id'] / 'record.json', record)
        self.state['cases'] = cases
        for record in self.state['rounds']:
            if record['state'] in ('fitting', 'evaluating'):
                record['state'] = 'interrupted'
        self.save('starting')
        try:
            while True:
                self.check_stop()
                index = max((r['index'] for r in self.state['cases']), default=-1) + 1
                if index >= self.plan['maxCases']:
                    self.state['state'] = 'case limit reached'
                    break
                used = sum(p.stat().st_size for p in self.root.rglob('*') if p.is_file())
                if used >= self.plan['maxBytes'] or shutil.disk_usage(self.root).free < 8 * 1024**3:
                    self.state['state'] = 'storage limit reached'
                    break
                self.save('collecting')
                count = min(self.plan['batchSize'], self.plan['maxCases'] - index)
                before = sum(c['state'] == 'collected' and c['hasPreferences'] for c in self.state['cases'])
                with ThreadPoolExecutor(max_workers=self.plan['workers']) as pool:
                    futures = [pool.submit(self.collect, i, self.state['latestRollIn']) for i in range(index, index + count)]
                    try:
                        for future in as_completed(futures):
                            self.state['cases'].append(future.result())
                            self.save('collecting')
                    except BaseException:
                        self.stop.set()
                        for future in futures:
                            future.cancel()
                        raise
                after = sum(c['state'] == 'collected' and c['hasPreferences'] for c in self.state['cases'])
                if after == before:
                    self.state['state'] = 'no usable new preferences; inspect quarantine'
                    break
                self.fit()
        except StopRequested as error:
            self.stop.set()
            self.state.update(state='stopped', stopReason=str(error))
        except BaseException as error:
            self.stop.set()
            self.state.update(state='failed', error=f'{type(error).__name__}: {error}')
            raise
        finally:
            self.save('finished')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    setup = sub.add_parser('prepare')
    for name in ('out', 'harness', 'mod', 'catalog', 'validation', 'sealed-test', 'baselines'):
        setup.add_argument('--' + name, type=Path, required=True)
    setup.add_argument('--until', required=True)
    setup.add_argument('--seed', required=True)
    setup.add_argument('--workers', type=int, choices=range(1, 5), default=4)
    setup.add_argument('--max-cases', type=int, default=3000)
    setup.add_argument('--max-gib', type=int, default=32)
    setup.add_argument('--fit-roots', type=int, default=128, help='Maximum distinct balanced roots per fit (20..1024)')
    setup.add_argument('--fit-rows-per-root', type=int, default=2048, help='Maximum observations per actual root (64..8192)')
    setup.add_argument('--fit-seconds', type=int, default=600, help='Hard wall-clock timeout for one fit (1..1800)')
    setup.add_argument('--fit-partition', choices=['shared', 'character'], default='shared',
                       help='Fit one shared predictor or a self-contained native-character conditional predictor')
    setup.add_argument('--screen-id', action='append', required=True)
    setup.add_argument('--prior-job', type=Path, help='Import a stopped job with its full cost and failure ledger')
    setup.add_argument('--fit-harness', type=Path, help='Optional separate frozen fitter; collection/evaluation keep their engine')
    setup.add_argument('--fit-mod', type=Path, help='Mod assembly paired with --fit-harness; model schema/MVID remain validated')
    runner = sub.add_parser('run')
    runner.add_argument('directory', type=Path)
    args = parser.parse_args()
    if args.command == 'prepare':
        if args.max_cases < 1 or args.max_gib < 1:
            parser.error('Positive case and storage limits required')
        if not (20 <= args.fit_roots <= 1024 and 64 <= args.fit_rows_per_root <= 8192
                and 1 <= args.fit_seconds <= 1800):
            parser.error('Training limits out of bounds')
        if bool(args.fit_harness) != bool(args.fit_mod):
            parser.error('--fit-harness and --fit-mod must be provided together')
        initialize(args)
    else:
        with (args.directory / 'supervisor.lock').open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            job = Job(args.directory.resolve())
            os.nice(5)
            os.sched_setaffinity(0, sorted(os.sched_getaffinity(0))[:8])
            signal.signal(signal.SIGTERM, lambda *_: job.stop.set())
            signal.signal(signal.SIGINT, lambda *_: job.stop.set())
            job.run()


if __name__ == '__main__':
    main()
