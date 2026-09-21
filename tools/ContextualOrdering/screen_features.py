#!/usr/bin/env python3
"""Training-only leave-family-out screening of single-feature residuals.

This measures the witness-ranking surrogate, not combat quality sensitivity.
Final selection must still run real search and independent validation.
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
from features import NAMES
from fit import fit, dot, clipped


def family(pair):
    return pair['case'].split('-train-')[0]


def loss(pairs, weights, cap):
    counts = Counter(p['case'] for p in pairs)
    result = 0.
    for p in pairs:
        margin = ((p['preferredRecordedRank'] - p['otherRecordedRank']) / 30000
                  + clipped(dot(weights, p['preferred']), cap) - clipped(dot(weights, p['other']), cap))
        result += (max(0., -margin) + math.log1p(math.exp(-abs(margin)))) / counts[p['case']] / len(counts)
    return result


def run(args):
    document = json.loads(args.pairs.read_text())
    pairs = document['pairs']
    if document['featureNames'] != list(NAMES) or any('-train-' not in p['case'] for p in pairs):
        raise ValueError('Unexpected feature schema or held-out root')
    families = sorted({family(p) for p in pairs})
    result = []
    for feature, name in enumerate(NAMES):
        folds = []
        for omitted in families:
            training = [p for p in pairs if family(p) != omitted]
            held = [p for p in pairs if family(p) == omitted]
            weights, _ = fit(training, .05, 2, 400, {feature})
            folds.append({'family': omitted, 'delta': loss(held, weights, 2) - loss(held, [0.] * 28, 2)})
        weights, report = fit(pairs, .05, 2, 400, {feature})
        result.append({'feature': name, 'weight': weights[feature], 'folds': folds,
                       'meanHeldFamilyDelta': sum(f['delta'] for f in folds) / len(folds),
                       'trainingDelta': report['loss'] - report['initialLoss']})
    result.sort(key=lambda r: r['meanHeldFamilyDelta'])
    with args.out.open('x') as stream:
        json.dump(result, stream, indent=2)
    print(json.dumps([{k: v for k, v in r.items() if k != 'folds'} for r in result[:8]], indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--pairs', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    run(p.parse_args())
