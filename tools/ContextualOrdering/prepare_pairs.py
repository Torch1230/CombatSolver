#!/usr/bin/env python3
"""Pairs supported by different completed continuation witnesses in real candidate pools.

A pruned candidate without a witnessed continuation stays unknown. Outcomes from
several completed runs are matched by exact executable prefix within the same root.
These labels compare observed continuations, not the unknown optimal value of states.
"""
import argparse
from collections import defaultdict
import itertools
import json
from pathlib import Path
import subprocess

from features import NAMES, capture
from compare import context_mismatch


def read(path):
    return json.loads(Path(path).read_text())


def prepare(manifest, runs, out, harness):
    out.mkdir(parents=True, exist_ok=False)
    train_ids = {case['id'] for case in read(manifest)['cases'] if case['split'] == 'train'}
    records = [json.loads(line) for line in (runs / 'observations.jsonl').read_text().splitlines()]
    by_case = defaultdict(list)
    rejected = []
    for record in records:
        if record['case'] not in train_ids:
            raise ValueError('Training input contains a held-out root: ' + record['case'])
        if record['status'] != 'Comparable':
            rejected.append([record['case'], record['variant'], record['status']])
        elif not record['quality']['won'] and record['quality']['survives']:
            rejected.append([record['case'], record['variant'], 'UnfinishedContinuation'])
        else:
            by_case[record['case']].append(record['variant'])
    comparisons = []
    for case, variants in by_case.items():
        for a, b in itertools.combinations(variants, 2):
            mismatch = context_mismatch(read(runs / case / a / 'harness-result.json'),
                                        read(runs / case / b / 'harness-result.json'))
            if mismatch:
                raise ValueError(f'Incomparable witnesses: {case}/{a}/{b}: {mismatch}')
            comparisons.append({'id': json.dumps([case, a, b]),
                'candidate': str((runs / case / a / 'quality.json').resolve()),
                'baseline': str((runs / case / b / 'quality.json').resolve())})
    (out / 'comparison-input.json').write_text(json.dumps(comparisons))
    subprocess.run(['dotnet', str(harness.resolve()), '--compare-quality-batch',
                    str(out / 'comparison-input.json'), str(out / 'comparisons.json')], check=True)
    order = {}
    for comparison in read(out / 'comparisons.json'):
        case, a, b = json.loads(comparison['id'])
        order[case, a, b] = comparison['materialComparison']
        order[case, b, a] = -comparison['materialComparison']
    pairs = []
    coverage = []
    for case, variants in by_case.items():
        witnesses = {}
        for variant in variants:
            for prefix in read(runs / case / variant / 'ordering-selected-prefixes.json'):
                old = witnesses.get(prefix)
                if old is None or old != variant and order[case, variant, old] < 0:
                    witnesses[prefix] = variant
        known, unknown, pools_with_pairs = 0, 0, 0
        seen = set()
        for variant in variants:
            if variant != 'baseline':
                continue  # Other runs supply witnesses; fit the actual baseline decision pools.
            pools = defaultdict(dict)
            trace = runs / case / variant / 'ordering-observations.jsonl'
            for line in trace.open():
                item = json.loads(line)
                observation = item['observation']
                if observation['stage'] != 'GlobalRetention':
                    continue
                witness = witnesses.get(item['prefix'])
                if witness is None:
                    unknown += 1
                    continue
                known += 1
                if observation['isTerminal'] or observation['retention']['evaluation']['projectedPlayerHp'] <= 0:
                    continue
                pools[observation['boundaryId']][item['prefix']] = (observation, witness)
            for pool in pools.values():
                produced = False
                for (pa, (a, wa)), (pb, (b, wb)) in itertools.combinations(pool.items(), 2):
                    sign = 0 if wa == wb else order[case, wa, wb]
                    if sign == 0:
                        continue
                    if sign > 0:
                        pa, pb, a, b, wa, wb = pb, pa, b, a, wb, wa
                    if (pa, pb) in seen:
                        continue
                    seen.add((pa, pb))
                    pairs.append({'case': case, 'preferredPrefix': pa, 'otherPrefix': pb,
                        'preferredWitness': wa, 'otherWitness': wb,
                        'preferred': capture(a), 'other': capture(b),
                        # Exact production baseline ranks; no Python reconstruction of the heuristic.
                        'preferredBaseScore': a['score'], 'otherBaseScore': b['score'],
                        'preferredRecordedRank': a['retention']['beamRankScore'],
                        'otherRecordedRank': b['retention']['beamRankScore'], 'observedOrdering': variant})
                    produced = True
                pools_with_pairs += produced
        coverage.append({'case': case, 'known': known, 'unknown': unknown, 'pairedPools': pools_with_pairs})
    document = {'schemaVersion': 1, 'labelMeaning': 'best_observed_completed_continuation_not_optimal_value',
                'featureNames': NAMES, 'rejected': rejected, 'coverage': coverage, 'pairs': pairs}
    (out / 'pairs.json').write_text(json.dumps(document, ensure_ascii=False, indent=2) + '\n')
    print(f'{len(pairs)} distinct witnessed pairs across {len(by_case)} training roots; unknowns retained in coverage.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--runs', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--harness', type=Path, required=True)
    args = parser.parse_args()
    prepare(args.manifest, args.runs, args.out, args.harness)
