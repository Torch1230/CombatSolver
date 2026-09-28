#!/usr/bin/env python3
"""Summarize a frozen paired benchmark without redefining terminal quality."""
import argparse
import collections
import csv
import math
from pathlib import Path
import random
import statistics

from dataset import read
from overnight import write


def classify(signs, verified):
    if not verified or len(signs) != 2:
        return 'unverified'
    if all(s == 0 for s in signs):
        return 'equal'
    if all(s <= 0 for s in signs):
        return 'improved'
    if all(s >= 0 for s in signs):
        return 'regressed'
    return 'mixed'


def cost_summary(rows):
    if not rows:
        return {'cases': 0}
    return {'cases': len(rows), 'faster': sum(r['secondsRatio'] < 1 for r in rows),
            'atLeast10PercentFaster': sum(r['secondsRatio'] <= .9 for r in rows),
            'bothRepeatsFaster': sum(r['bothRepeatsFaster'] for r in rows),
            'rssLower': sum(r['rssRatio'] < 1 for r in rows),
            'baselineSeconds': sum(r['baselineSeconds'] for r in rows),
            'candidateSeconds': sum(r['candidateSeconds'] for r in rows),
            'aggregateSecondsRatio': sum(r['candidateSeconds'] for r in rows) / sum(r['baselineSeconds'] for r in rows),
            'geometricMeanSecondsRatio': math.exp(statistics.mean(math.log(r['secondsRatio']) for r in rows)),
            'medianSecondsRatio': statistics.median(r['secondsRatio'] for r in rows),
            'baselineMeanRssMiB': statistics.mean(r['baselineRss'] for r in rows) / 2**20,
            'candidateMeanRssMiB': statistics.mean(r['candidateRss'] for r in rows) / 2**20,
            'baselineMaximumObservedRssMiB': max(r['baselineMaxRss'] for r in rows) / 2**20,
            'candidateMaximumObservedRssMiB': max(r['candidateMaxRss'] for r in rows) / 2**20,
            'baselineMeanManagedHeapMiB': statistics.mean(r['baselineManagedHeap'] for r in rows) / 2**20,
            'candidateMeanManagedHeapMiB': statistics.mean(r['candidateManagedHeap'] for r in rows) / 2**20,
            'baselineMeanAllocatedGiB': statistics.mean(r['baselineAllocated'] for r in rows) / 2**30,
            'candidateMeanAllocatedGiB': statistics.mean(r['candidateAllocated'] for r in rows) / 2**30}


def cluster_intervals(rows, classification='classification'):
    groups = collections.defaultdict(list)
    for row in rows:
        groups[row['encounter']].append(row)
    families = sorted(groups)
    if len(families) < 2:
        return {'families': len(families), 'available': False}
    rng = random.Random(20260928)
    samples = collections.defaultdict(list)
    for _ in range(2000):
        sample = [r for family in rng.choices(families, k=len(families)) for r in groups[family]]
        samples['improvedFraction'].append(sum(r[classification] == 'improved' for r in sample) / len(sample))
        samples['regressedFraction'].append(sum(r[classification] == 'regressed' for r in sample) / len(sample))
        safe = [r for r in sample if r[classification] in ('improved', 'equal') and 'secondsRatio' in r]
        if safe:
            samples['nondegradingGeometricMeanSecondsRatio'].append(
                math.exp(statistics.mean(math.log(r['secondsRatio']) for r in safe)))
    def interval(values):
        values.sort()
        return [values[int((len(values) - 1) * p)] for p in (.025, .975)]
    return {'families': len(families), 'available': True, 'replicates': 2000,
            'description': 'Descriptive percentile intervals resampling encounter families; synthetic development set, not population confidence guarantees',
            'percentile95': {k: interval(v) for k, v in samples.items()}}


def summarize(protocol, report):
    comparisons = {r['id']: r['materialComparison'] for r in report['comparisons']}
    core_comparisons = {r['id']: r['coreComparison'] for r in report['comparisons'] if 'coreComparison' in r}
    grouped = collections.defaultdict(list)
    for record in report['records']:
        grouped[record['id']].append(record)
    rows = []
    for case in read(protocol['manifest'])['cases']:
        records = grouped[case['id']]
        if len(records) < 4:
            continue
        if len(records) != 4 or len({r['run'] for r in records}) != 4:
            raise ValueError('Unexpected duplicate runs')
        lookup = {r['run']: r for r in records}
        signs = [comparisons[case['id'] + '/repeat-' + str(i)] for i in (0, 1)
                 if case['id'] + '/repeat-' + str(i) in comparisons]
        core_signs = [core_comparisons[case['id'] + '/repeat-' + str(i)] for i in (0, 1)
                      if case['id'] + '/repeat-' + str(i) in core_comparisons]
        verified = all(r['state'] == 'completed' and not r['issues'] for r in records)
        row = {'id': case['id'], 'character': records[0]['character'], 'encounter': records[0]['encounter'],
               'kind': case['kind'], 'classification': classify(signs, verified), 'comparisons': signs,
               'coreClassification': classify(core_signs, verified), 'coreComparisons': core_signs,
               'stableWithinBothArms': all(comparisons.get(case['id'] + '/' + arm + '-stability') == 0
                                           for arm in ('baseline', 'candidate')),
               'coreStableWithinBothArms': all(core_comparisons.get(case['id'] + '/' + arm + '-stability') == 0
                                               for arm in ('baseline', 'candidate')),
               'issues': {r['run']: r.get('issues', [r.get('reason')]) for r in records
                          if r['state'] != 'completed' or r.get('issues')}}
        if all(r['state'] == 'completed' for r in records):
            for arm in ('baseline', 'candidate'):
                arm_records = [lookup[arm + '-' + str(i)] for i in (0, 1)]
                for output, field in [('Seconds', 'seconds'), ('Rss', 'rss'), ('ManagedHeap', 'managedHeap'),
                                      ('ManagedLive', 'managedLive'), ('Allocated', 'allocated')]:
                    row[arm + output] = statistics.mean(r[field] for r in arm_records)
                row[arm + 'MaxRss'] = max(r['rss'] for r in arm_records)
                row[arm + 'TimeBoundaryRuns'] = sum(r['timeBoundary'] for r in arm_records)
                row[arm + 'Wins'] = [r['quality']['won'] for r in arm_records]
                row[arm + 'HpLosses'] = [r['quality']['projectedBattleHpLost'] for r in arm_records]
                row[arm + 'Metrics'] = {k: statistics.mean(r['metrics'][k] for r in arm_records)
                                       for k in arm_records[0]['metrics']}
            row['secondsRatio'] = row['candidateSeconds'] / row['baselineSeconds']
            row['rssRatio'] = row['candidateRss'] / row['baselineRss']
            row['bothRepeatsFaster'] = all(lookup['candidate-' + str(i)]['seconds'] < lookup['baseline-' + str(i)]['seconds'] for i in (0, 1))
            row['winLostInAnyPair'] = verified and any(a and not b for a, b in zip(row['baselineWins'], row['candidateWins']))
            row['winGainedInAnyPair'] = verified and any(b and not a for a, b in zip(row['baselineWins'], row['candidateWins']))
            row['bothArmsAlwaysWon'] = all(row['baselineWins'] + row['candidateWins'])
            row['bothArmsAlwaysLost'] = not any(row['baselineWins'] + row['candidateWins'])
            row['pairedHpLossChanges'] = [b - a for a, b in zip(row['baselineHpLosses'], row['candidateHpLosses'])]
        rows.append(row)
    def counts(items, classification='classification'):
        return dict(collections.Counter(r[classification] for r in items))
    timed = [r for r in rows if 'secondsRatio' in r]
    safe = [r for r in timed if r['classification'] in ('improved', 'equal')]
    core_safe = [r for r in timed if r['coreClassification'] in ('improved', 'equal')]
    return {'completed': report['completed'], 'casesFinished': len(rows),
            'plannedCases': len(read(protocol['manifest'])['cases']), 'counts': counts(rows),
            'coreCounts': counts(rows, 'coreClassification'),
            'byCharacter': {key: counts([r for r in rows if r['character'] == key]) for key in sorted({r['character'] for r in rows})},
            'byKind': {key: counts([r for r in rows if r['kind'] == key]) for key in sorted({r['kind'] for r in rows})},
            'coreByCharacter': {key: counts([r for r in rows if r['character'] == key], 'coreClassification')
                                for key in sorted({r['character'] for r in rows})},
            'coreByKind': {key: counts([r for r in rows if r['kind'] == key], 'coreClassification')
                           for key in sorted({r['kind'] for r in rows})},
            'byCharacterCosts': {key: cost_summary([r for r in timed if r['character'] == key])
                                 for key in sorted({r['character'] for r in timed})},
            'unstableWithinArmCases': sum(not r['stableWithinBothArms'] for r in rows),
            'coreUnstableWithinArmCases': sum(not r['coreStableWithinBothArms'] for r in rows),
            'equalWinCases': sum(r['classification'] == 'equal' and r.get('bothArmsAlwaysWon', False) for r in rows),
            'equalDefeatCases': sum(r['classification'] == 'equal' and r.get('bothArmsAlwaysLost', False) for r in rows),
            'coreEqualWinCases': sum(r['coreClassification'] == 'equal' and r.get('bothArmsAlwaysWon', False) for r in rows),
            'coreEqualDefeatCases': sum(r['coreClassification'] == 'equal' and r.get('bothArmsAlwaysLost', False) for r in rows),
            'winLostCases': sum(r.get('winLostInAnyPair', False) for r in rows),
            'winGainedCases': sum(r.get('winGainedInAnyPair', False) for r in rows),
            'allCompletedCosts': cost_summary(timed), 'nondegradingCosts': cost_summary(safe),
            'stableNondegradingCosts': cost_summary([r for r in safe if r['stableWithinBothArms']]),
            'coreNondegradingCosts': cost_summary(core_safe),
            'coreStableNondegradingCosts': cost_summary([r for r in core_safe if r['coreStableWithinBothArms']]),
            'stableNondegradingWinCosts': cost_summary([r for r in safe if r['stableWithinBothArms']
                                                       and r['bothArmsAlwaysWon']]),
            'coreStableNondegradingWinCosts': cost_summary([r for r in core_safe if r['coreStableWithinBothArms']
                                                           and r['bothArmsAlwaysWon']]),
            'coreUncertainty': cluster_intervals(rows, 'coreClassification') if core_comparisons else {'available': False},
            'uncertainty': cluster_intervals(rows), 'rows': rows,
            'finalTestOpened': False, 'modelSha256': protocol['modelSha256']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('protocol', type=Path)
    args = parser.parse_args()
    protocol = read(args.protocol)
    out = Path(protocol['out'])
    result = summarize(protocol, read(out / 'report.json'))
    write(out / 'summary.json', result)
    with (out / 'cases.csv').open('w') as stream:
        fields = ['id', 'character', 'encounter', 'kind', 'classification', 'comparisons',
                  'coreClassification', 'coreComparisons', 'coreStableWithinBothArms',
                  'stableWithinBothArms', 'baselineSeconds', 'candidateSeconds', 'secondsRatio',
                  'baselineRss', 'candidateRss', 'baselineWins', 'candidateWins',
                  'baselineHpLosses', 'candidateHpLosses', 'issues']
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction='ignore')
        writer.writeheader()
        writer.writerows(result['rows'])
    print({k: v for k, v in result.items() if k not in
           ('rows', 'byCharacter', 'byKind', 'coreByCharacter', 'coreByKind', 'byCharacterCosts')})
