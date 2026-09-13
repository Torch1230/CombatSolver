#!/usr/bin/env python3
"""Conditional benefit model; never a measured speedup or a confidence interval."""
import argparse
import json
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('census', type=Path)
p.add_argument('inputs', type=Path)
p.add_argument('output', type=Path)
a = p.parse_args()
i = json.loads(a.inputs.read_text())
r = json.loads(a.census.read_text())['runs'][-1]
c = r['census']
manual_ticks = sum(card['ManualTicks'] for card in c['Cards'].values())
rows = []
all_cards = ['DAGGER_THROW', 'ACROBATICS', 'SURVIVOR', 'PREPARED']
for label, names, eligible in [
    ('single_card_initial_screen', ['DAGGER_THROW'], True),
    ('four_cards_initial_screen', all_cards, True),
    ('four_cards_all_shapes_optimistic', all_cards, False),
]:
    ticks = allocation = 0.0
    repeats = groups = 0
    prefix = 'Eligible' if eligible else ''
    for name in names:
        card, family = c['Cards'][name], c['Families'][name]
        visits = family[prefix + 'Visits']
        repeated = family['Repeated' + prefix + 'Visits']
        rate = repeated / visits if visits else 0
        ticks += card[prefix + 'PrefixTicks'] * rate
        allocation += card[prefix + 'PrefixAllocated'] * rate
        repeats += repeated
        groups += family[prefix + 'Groups']
    f = i['manualCpuFraction'] * ticks / manual_ticks
    # Retention is a sensitivity parameter, not a measured probability.
    sensitivity = []
    for retention in [0, .25, .5, .75, 1]:
        net = f * retention
        sensitivity.append(dict(netRetention=retention, cpuReductionPercent=100*net,
            conditionalWallSeconds=(i['referenceSeconds']-i['referenceGcSeconds'])*(1-net)+i['referenceGcSeconds'], cpuSpeedup=1/(1-net)))
    rows.append(dict(scope=label, groups=groups, repeatVisits=repeats,
        estimatedRepeatedPrefixAllocatedBytes=allocation,
        estimatedRepeatedPrefixScopeSeconds=ticks/c['StopwatchFrequency'],
        grossCpuReductionPercent=100*f,
        grossAllocatedReductionPercent=100*allocation/i['referenceAllocatedBytes'],
        estimatedPrefixBytesPerRepeatedVisit=allocation/repeats,
        sensitivity=sensitivity))
result = dict(kind='conditional model, not benchmark', censusRunId=r['runId'], inputs=i,
    model='manualCpuFraction * repeatedPrefixScopeTicks / allManualScopeTicks', rows=rows,
    broadChoiceSensitivity=[dict(removedFractionOfChoiceSubtree=q,
        grossCpuReductionPercent=100*i['choiceReplayInclusiveFraction']*q,
        speedup=1/(1-i['choiceReplayInclusiveFraction']*q)) for q in [.1,.25,.5,1]],
    requiredReductionForReferenceToTenSecondsPercent=100*(1-10/i['referenceSeconds']),
    referenceGcSharePercent=100*i['referenceGcSeconds']/i['referenceSeconds'])
a.output.write_text(json.dumps(result, indent=2)+'\n')
for row in rows:
    print(row['scope'], 'CPU gross %', round(row['grossCpuReductionPercent'], 3),
          'allocation MB', round(row['estimatedRepeatedPrefixAllocatedBytes']/1e6, 2))
