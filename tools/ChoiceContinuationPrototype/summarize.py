#!/usr/bin/env python3
"""Summarize all samples; no best-run selection or whole-search extrapolation."""
import argparse, json, statistics
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('result', type=Path)
a = p.parse_args()
data = json.loads(a.result.read_text())
summary = []
for branches in sorted({sample['branches'] for sample in data['samples']}):
    modes = {}
    for resume in (False, True):
        rows = [row for row in data['samples'] if row['branches'] == branches and row['resume'] == resume]
        elapsed = [row['ms'] * 1000 / row['repetitions'] for row in rows]
        allocated = [row['bytes'] / row['repetitions'] for row in rows]
        modes['resume' if resume else 'replay'] = {
            'samples': len(rows), 'microsecondsMedian': statistics.median(elapsed),
            'microsecondsRange': [min(elapsed), max(elapsed)],
            'bytesMedian': statistics.median(allocated),
        }
    summary.append({'branches': branches, **modes,
        'elapsedReductionPercent': 100 * (1 - modes['resume']['microsecondsMedian'] / modes['replay']['microsecondsMedian']),
        'allocationReductionPercent': 100 * (1 - modes['resume']['bytesMedian'] / modes['replay']['bytesMedian'])})
retained = {}
for resume in (False, True):
    values = [row['retainedBytes'] / row['families'] for row in data.get('retainedSamples', []) if row['resume'] == resume]
    if values:
        retained['resume' if resume else 'replay'] = {'bytesPerFamilyMedian': statistics.median(values), 'range': [min(values), max(values)]}
if retained:
    retained['reductionPercent'] = 100 * (1 - retained['resume']['bytesPerFamilyMedian'] / retained['replay']['bytesPerFamilyMedian'])
print(json.dumps({'timingAndAllocation': summary, 'controlledRetainedState': retained,
    'scope': 'Headless single-card families, not search time, CPU time, RSS, or visible performance. Inspect ranges for warm-up drift.'}, indent=2))
