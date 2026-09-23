#!/usr/bin/env python3
"""Serial A/B runner for a hashed OfflineSearchHarness matrix manifest.

Manifest paths are repository-relative. Each case gets one A/B pair by default;
order alternates AB/BA across cases and repetitions. Failed runs remain in runs.json
and their pair rows are retained in pairs.json.
"""
import argparse
import hashlib
import json
import math
import os
import re
import subprocess
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MEMBER_FIELDS = (
    'beamWidth', 'secondRankBand', 'baseScoreOnly', 'aggressivePowerCommitment',
    'nodeBudget', 'ran', 'selected', 'compared', 'skippedReason', 'expandedNodes',
    'transitionCount', 'termination', 'terminal', 'won', 'battleHpLost', 'potionCount',
    'offensiveRefinement', 'boundedRefinement',
)
TIME_PATTERNS = ('TURN_LAYER_BUDGET reason=time', 'SEARCH_TIME_BUDGET')
PAIR_PROFILE_FIELDS = (
    'profile', 'maxExpandedNodes', 'maxDegreeOfParallelism', 'budgetMilliseconds',
    'potionPolicy', 'searchMode', 'usePortfolio', 'fixedSearchBudget', 'enableNoGcRegion',
)


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def canonical_hash(value):
    data = json.dumps(value, ensure_ascii=False, sort_keys=True,
                      separators=(',', ':')).encode()
    return hashlib.sha256(data).hexdigest()


def repo_path(value):
    path = Path(value)
    return (path if path.is_absolute() else REPO / path).resolve(strict=True)


def prepare_case(case, workspace):
    request_path, scenario_path = repo_path(case['request']), repo_path(case['scenario'])
    if sha256(request_path) != case['requestSha256']:
        raise ValueError(f"request hash changed for {case['id']}: {request_path}")
    if sha256(scenario_path) != case['scenarioSha256']:
        raise ValueError(f"scenario hash changed for {case['id']}: {scenario_path}")
    request = json.loads(request_path.read_text())
    declared = Path(request['generatedScenarioPath'])
    if declared.is_absolute() and declared.exists():
        if sha256(declared) != case['scenarioSha256']:
            raise ValueError(f"request/scenario mismatch for {case['id']}: {declared}")
    elif not declared.is_absolute() and (REPO / declared).exists():
        if sha256(REPO / declared) != case['scenarioSha256']:
            raise ValueError(f"request/scenario mismatch for {case['id']}: {declared}")
    elif declared.name != scenario_path.name:
        raise ValueError(f"request/scenario name mismatch for {case['id']}: {declared}")

    # Normalize old checkout-specific absolute paths while preserving the hashed source request.
    request['generatedScenarioPath'] = str(scenario_path)
    input_path = workspace / 'inputs' / f"{case['id']}.request.json"
    input_path.parent.mkdir(parents=True, exist_ok=True)
    input_path.write_text(json.dumps(request, ensure_ascii=False, indent=2))
    return input_path


def expected_budget(config, case, no_gc_budget):
    result = {'profile': config['profile'], 'maxExpandedNodes': config['nodes'],
              'maxDegreeOfParallelism': case['dop'], 'budgetMilliseconds': config['budgetMilliseconds'],
              'potionPolicy': config['potionPolicy'], 'searchMode': config['searchMode'],
              'usePortfolio': config['usePortfolio'],
        'fixedSearchBudget': not config.get('productionBudget', False),
              'enableNoGcRegion': config['enableNoGcRegion']}
    if config['enableNoGcRegion']:
        result['noGcRegionBudgetGigabytes'] = no_gc_budget
    return result


def time_boundary(out, result):
    metrics = result.get('solverMetrics') or {}
    if (result.get('timeBoundaryObserved') or metrics.get('boundary') == 'TimeLimit'
            or result.get('boundary') == 'TimeLimit'):
        return True
    logs = sorted((out / 'logs').glob('*/*.jsonl')) if (out / 'logs').exists() else ()
    for path in logs:
        for line in path.read_text(errors='replace').splitlines():
            try: message = str(json.loads(line).get('Message', line))
            except json.JSONDecodeError: message = line
            if any(pattern in message for pattern in TIME_PATTERNS): return True
    return False


def failed_row(case, arm, repetition, label, out, dll_hash, started, reason, **details):
    return {'case': case['id'], 'label': label, 'arm': arm, 'repetition': repetition,
            'valid': False, 'reason': reason, 'dllSha256': dll_hash,
            'processSeconds': time.monotonic() - started, 'output': str(out), **details}


def run_one(case, arm, repetition, dll, dll_hash, input_path, config,
            no_gc_budget, harness, workspace):
    label = f"{case['id']}-{arm.lower()}-{repetition}"
    out = workspace / 'runs' / label
    started = time.monotonic()
    if not re.fullmatch(r'[A-Za-z0-9_.-]+', label):
        return failed_row(case, arm, repetition, label, out, dll_hash, started,
                          'unsafe run label')
    if out.exists():
        return failed_row(case, arm, repetition, label, out, dll_hash, started,
                          'refusing to overwrite prior result')
    out.mkdir(parents=True)
    command = ['dotnet', str(harness), '--request', str(input_path), '--label', label,
               '--out', str(out), '--profile', str(config['profile']), '--nodes', str(config['nodes']),
               '--dop', str(case['dop']), '--budget-ms', str(config['budgetMilliseconds']),
               '--search-mode', str(config['searchMode']), '--potion-policy', str(config['potionPolicy'])]
    if config.get('usePortfolio'):
        command.append('--use-portfolio')
    if config.get('productionBudget'):
        command.append('--production-budget')
    if config.get('enableNoGcRegion'):
        command += ['--enable-no-gc-region', '--no-gc-region-budget-gigabytes', str(no_gc_budget)]
    env = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(dll))
    timeout = int(case.get('processTimeoutSeconds', config['processTimeoutSeconds']))
    try:
        with (out / 'stdout.log').open('w') as log:
            process = subprocess.run(command, cwd=REPO, env=env, stdout=log,
                                     stderr=subprocess.STDOUT, check=False, timeout=timeout)
    except subprocess.TimeoutExpired:
        return failed_row(case, arm, repetition, label, out, dll_hash, started,
                          'harness process timeout', command=command)
    except OSError as error:
        return failed_row(case, arm, repetition, label, out, dll_hash, started,
                          f'{type(error).__name__}: {error}', command=command)

    result_path, quality_path, route_path = (out / 'result.json', out / 'quality.json', out / 'route.json')
    base = {
        'case': case['id'], 'label': label, 'arm': arm, 'repetition': repetition,
        'returnCode': process.returncode, 'processSeconds': time.monotonic() - started,
        'dllSha256': dll_hash, 'command': command, 'output': str(out),
    }
    if not result_path.is_file():
        return {**base, 'valid': False, 'reason': 'harness did not write result.json'}
    try:
        result = json.loads(result_path.read_text())
        quality_doc = json.loads(quality_path.read_text()) if quality_path.is_file() else None
        route = json.loads(route_path.read_text()) if route_path.is_file() else None
    except (OSError, json.JSONDecodeError) as error:
        return {**base, 'valid': False, 'reason': f'invalid or missing result artifact: {error}'}
    budget = result.get('budget') or {}
    expected = expected_budget(config, case, no_gc_budget)
    config_matches = all(budget.get(key) == value for key, value in expected.items())
    metrics = result.get('solverMetrics') or {}
    route_hash = canonical_hash(route) if route is not None else None
    quality = (quality_doc or {}).get('quality') or {}
    artifacts_present = quality_doc is not None and route is not None
    result_valid = (process.returncode == 0 and result.get('status') == 'Passed'
                    and result.get('reachedMilestone') == 'M2' and config_matches and artifacts_present)
    return {
        **base, 'valid': result_valid,
        'configurationMatchesManifest': config_matches, 'status': result.get('status'),
        'artifactFilesPresent': artifacts_present, 'resultValid': result_valid,
        'reachedMilestone': result.get('reachedMilestone'), 'budget': budget,
        'solverMetrics': metrics, 'quality': quality, 'route': route,
        'routeSha256': route_hash,
        'potionIds': [action.get('potionId') for action in route or []
                      if action.get('kind') == 'UsePotion' and action.get('potionId')],
        'wallSeconds': result.get('wallSeconds'),
        'peakManagedLiveBytes': result.get('peakManagedLiveBytes'),
        'peakWorkingSetBytes': result.get('peakWorkingSetBytes'),
        'totalAllocatedBytes': result.get('totalAllocatedBytes'),
        'timeBoundaryObserved': time_boundary(out, result),
        'boundary': metrics.get('boundary') or result.get('boundary'),
    }


def member_signature(row):
    members = (row.get('solverMetrics') or {}).get('portfolioMembers')
    if not isinstance(members, list):
        return None
    return [{key: member.get(key) for key in MEMBER_FIELDS} for member in members]


def ratio(numerator, denominator):
    if (not isinstance(numerator, (int, float)) or not isinstance(denominator, (int, float))
            or denominator <= 0):
        return None
    return numerator / denominator


def quality_no_worse(a, b):
    aq, bq = a.get('quality') or {}, b.get('quality') or {}
    numeric = ('projectedBattleHpLost', 'strategicHpDeficit', 'enemyHp', 'outstandingStolenResource')
    return (not aq.get('won', False) or bq.get('won', False)) \
        and (not aq.get('survives', False) or bq.get('survives', False)) \
        and all(aq.get(key) is not None and bq.get(key) is not None and bq[key] <= aq[key]
                for key in numeric)


def compare_pair(case, repetition, a, b, use_portfolio):
    aq, bq = a.get('quality') or {}, b.get('quality') or {}
    potion_equal = (aq.get('projectedBattlePotionCount') is not None
                    and aq.get('projectedBattlePotionCount') == bq.get('projectedBattlePotionCount')
                    and a.get('potionIds') is not None and a.get('potionIds') == b.get('potionIds'))
    aw, bw = a.get('solverMetrics') or {}, b.get('solverMetrics') or {}
    sig_a, sig_b = member_signature(a), member_signature(b)
    same_members = sig_a == sig_b and (not use_portfolio or sig_a is not None)
    same_work = (aw.get('totalExpanded') is not None
                 and aw.get('totalExpanded') == bw.get('totalExpanded')
                 and aw.get('totalTransitions') is not None
                 and aw.get('totalTransitions') == bw.get('totalTransitions')
                 and same_members)
    same_profile = all((a.get('budget') or {}).get(key) == (b.get('budget') or {}).get(key)
                       for key in PAIR_PROFILE_FIELDS)
    valid_pair = bool(a.get('valid') and b.get('valid') and same_profile)
    no_boundary = (a.get('timeBoundaryObserved') is False and b.get('timeBoundaryObserved') is False
                   and a.get('boundary') != 'TimeLimit' and b.get('boundary') != 'TimeLimit')
    no_worse = quality_no_worse(a, b) if a.get('quality') and b.get('quality') else False
    accepted = valid_pair and potion_equal and no_worse and same_work and no_boundary
    return {
        'case': case['id'], 'repetition': repetition, 'A': a, 'B': b,
        'validPair': valid_pair, 'sameProfileExceptNoGcBudget': same_profile,
        'potionConsumptionEqual': potion_equal, 'qualityNoWorse': no_worse,
        'routeEqual': a.get('routeSha256') is not None and a.get('routeSha256') == b.get('routeSha256'),
        'sameWorkAndMembers': same_work, 'noTimeBoundary': no_boundary,
        'acceptedForComparablePerformance': accepted,
        'wallRatioCandidateOverBaseline': ratio(b.get('wallSeconds'), a.get('wallSeconds')),
        'managedLiveRatioCandidateOverBaseline': ratio(
            b.get('peakManagedLiveBytes'), a.get('peakManagedLiveBytes')),
        'workingSetRatioCandidateOverBaseline': ratio(
            b.get('peakWorkingSetBytes'), a.get('peakWorkingSetBytes')),
    }


def geometric_mean(values):
    valid = [value for value in values if isinstance(value, (int, float)) and value > 0]
    return math.exp(sum(math.log(value) for value in valid) / len(valid)) if valid else None


def aggregate(rows):
    return {
        'pairs': len(rows),
        'wallGeomeanCandidateOverBaseline': geometric_mean(
            [row.get('wallRatioCandidateOverBaseline') for row in rows]),
        'managedLiveGeomeanCandidateOverBaseline': geometric_mean(
            [row.get('managedLiveRatioCandidateOverBaseline') for row in rows]),
        'workingSetGeomeanCandidateOverBaseline': geometric_mean(
            [row.get('workingSetRatioCandidateOverBaseline') for row in rows]),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-dll', type=Path, required=True)
    parser.add_argument('--candidate-dll', type=Path, required=True)
    parser.add_argument('--harness', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--workspace', type=Path, required=True)
    parser.add_argument('--repetitions', type=int, default=1,
                        help='A/B pairs per case; arm order alternates AB/BA')
    parser.add_argument('--candidate-no-gc-budget-gb', type=float,
                        help='override candidate budget when enabled by the manifest')
    args = parser.parse_args()
    if args.repetitions < 1:
        parser.error('--repetitions must be at least 1')

    baseline, candidate = args.baseline_dll.resolve(strict=True), args.candidate_dll.resolve(strict=True)
    harness, manifest_path = args.harness.resolve(strict=True), args.manifest.resolve(strict=True)
    manifest = json.loads(manifest_path.read_text())
    workspace = args.workspace.resolve()
    if workspace.exists() and any(workspace.iterdir()):
        parser.error(f'refusing non-empty workspace: {workspace}')
    cases, config = manifest.get('cases'), manifest
    if not isinstance(cases, list) or not cases:
        parser.error('manifest must contain a non-empty cases array')
    if len({case['id'] for case in cases}) != len(cases):
        parser.error('manifest contains duplicate case ids')
    candidate_gc = args.candidate_no_gc_budget_gb
    if candidate_gc is None:
        candidate_gc = config.get('candidateNoGcRegionBudgetGigabytes')
    if candidate_gc == 'REQUIRED_AT_RUN_TIME':
        candidate_gc = None
    if config['enableNoGcRegion'] and candidate_gc is None:
        parser.error('--candidate-no-gc-budget-gb is required when manifest budget is unspecified')
    baseline_gc = config.get('baselineNoGcRegionBudgetGigabytes')
    if config['enableNoGcRegion'] and baseline_gc is None:
        parser.error('manifest must specify baselineNoGcRegionBudgetGigabytes')
    if args.candidate_no_gc_budget_gb is not None and args.candidate_no_gc_budget_gb <= 0:
        parser.error('--candidate-no-gc-budget-gb must be positive')

    workspace.mkdir(parents=True, exist_ok=True)
    inputs = {case['id']: prepare_case(case, workspace) for case in cases}
    baseline_hash, candidate_hash = sha256(baseline), sha256(candidate)
    run_manifest = {
        'manifest': str(manifest_path), 'manifestSha256': sha256(manifest_path),
        'harness': str(harness), 'harnessSha256': sha256(harness),
        'baselineDll': str(baseline), 'baselineDllSha256': baseline_hash,
        'candidateDll': str(candidate), 'candidateDllSha256': candidate_hash,
        'candidateNoGcRegionBudgetGigabytes': candidate_gc,
        'repetitions': args.repetitions, 'startedAtUnix': time.time(), 'matrix': manifest,
    }
    (workspace / 'run-manifest.json').write_text(json.dumps(run_manifest, indent=2, ensure_ascii=False))
    all_runs, all_pairs = [], []
    for case_index, case in enumerate(cases):
        print(f"[{case_index + 1}/{len(cases)}] {case['id']} (DOP{case['dop']})", flush=True)
        for repetition in range(args.repetitions):
            order = ('A', 'B') if (case_index + repetition) % 2 == 0 else ('B', 'A')
            rows = {}
            for arm in order:
                dll, dll_hash, gc_budget = (baseline, baseline_hash, baseline_gc) if arm == 'A' else (
                    candidate, candidate_hash, candidate_gc)
                row = run_one(case, arm, repetition, dll, dll_hash, inputs[case['id']],
                              config, gc_budget, harness, workspace)
                rows[arm] = row
                all_runs.append(row)
                (workspace / 'runs.json').write_text(
                    json.dumps(all_runs, indent=2, ensure_ascii=False))
                print(json.dumps({key: row.get(key) for key in
                      ('label', 'arm', 'valid', 'reason', 'wallSeconds', 'boundary')},
                      ensure_ascii=False), flush=True)
            pair = compare_pair(case, repetition, rows.get('A', {}), rows.get('B', {}),
                                bool(config['usePortfolio']))
            all_pairs.append(pair)
            (workspace / 'pairs.json').write_text(
                json.dumps(all_pairs, indent=2, ensure_ascii=False))

    valid_pairs = [row for row in all_pairs if row['validPair']]
    comparable = [row for row in all_pairs if row['acceptedForComparablePerformance']]
    summary = {
        'cases': len(cases), 'repetitionsPerCase': args.repetitions,
        'runs': len(all_runs), 'pairs': len(all_pairs),
        'allValidPairs': aggregate(valid_pairs),
        'sameWorkNoTimeBoundaryPairs': aggregate(comparable),
        'acceptedPairCount': len(comparable),
        'failedRuns': [row for row in all_runs if not row.get('valid')],
        'rejectedPairs': [row for row in all_pairs if not row['acceptedForComparablePerformance']],
        'byDop': {},
    }
    for dop in sorted({case['dop'] for case in cases}):
        ids = {case['id'] for case in cases if case['dop'] == dop}
        summary['byDop'][str(dop)] = {
            'allValidPairs': aggregate([row for row in valid_pairs if row['case'] in ids]),
            'sameWorkNoTimeBoundaryPairs': aggregate([row for row in comparable if row['case'] in ids]),
        }
    (workspace / 'summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False))
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0 if len(comparable) == len(all_pairs) else 2


if __name__ == '__main__':
    raise SystemExit(main())
