#!/usr/bin/env python3
"""Deterministic, root-balanced ridge logistic residual fit. No search in this loop.

Labels are witnessed continuations, not optimal state values. This tool never
promotes a model: acceptance requires fresh search on untouched roots.
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path

from features import NAMES


def read(path):
    return json.loads(path.read_text())


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def clipped(value, cap):
    return max(-cap, min(cap, value))


def fit(pairs, ridge, cap, iterations, active=None):
    counts = Counter(p['case'] for p in pairs)
    rows = [(p['preferred'], p['other'],
             (p['preferredRecordedRank'] - p['otherRecordedRank']) / 30000,
             1 / counts[p['case']] / len(counts)) for p in pairs]
    weights = [0.] * len(NAMES)
    # Clip is part of the objective, matching runtime. Ridge pulls toward baseline.
    def evaluate(w, gradient=False):
        loss, correct = 0., 0
        grad = [ridge * x for x in w]
        for a, b, base, importance in rows:
            va, vb = dot(w, a), dot(w, b)
            margin = base + clipped(va, cap) - clipped(vb, cap)
            loss += importance * (max(0., -margin) + math.log1p(math.exp(-abs(margin))))
            correct += margin > 0
            if gradient:
                prob = 1 / (1 + math.exp(max(-700, min(700, margin))))
                for j in range(len(w)):
                    if active is not None and j not in active:
                        continue
                    grad[j] -= importance * prob * ((a[j] if abs(va) < cap else 0.)
                                                   - (b[j] if abs(vb) < cap else 0.))
        return loss + ridge * dot(w, w) / 2, correct, grad
    initial = evaluate(weights)
    for iteration in range(iterations):
        value, _, grad = evaluate(weights, True)
        step = 1.
        for _ in range(30):
            proposal = [max(-100., min(100., w - step * g)) for w, g in zip(weights, grad)]
            if evaluate(proposal)[0] < value:
                weights = proposal
                break
            step *= .5
        else:
            break
        if step * max(map(abs, grad)) < 1e-8:
            break
    final = evaluate(weights)
    return weights, {'ridge': ridge, 'capHp': cap, 'iterations': iteration + 1,
                     'initialLoss': initial[0], 'loss': final[0],
                     'initialCorrect': initial[1], 'correct': final[1], 'pairs': len(rows),
                     'roots': dict(counts)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pairs', type=Path, required=True)
    parser.add_argument('--schema', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--ridge', type=float, default=.05)
    parser.add_argument('--cap', type=float, default=2)
    parser.add_argument('--iterations', type=int, default=1500)
    parser.add_argument('--feature', choices=NAMES, action='append')
    args = parser.parse_args()
    if args.ridge <= 0 or not 0 < args.cap <= 8 or args.iterations < 1:
        raise ValueError('Invalid optimization configuration')
    data = read(args.pairs)
    if data['featureNames'] != list(NAMES) or not data['pairs']:
        raise ValueError('Missing pairs or incompatible feature schema')
    if any('-train-' not in p['case'] for p in data['pairs']):
        raise ValueError('Held-out case entered training')
    active = None if args.feature is None else {NAMES.index(name) for name in args.feature}
    weights, report = fit(data['pairs'], args.ridge, args.cap, args.iterations, active)
    schema = read(args.schema)
    schema.update(weights=weights, maximumAdjustmentHp=args.cap,
                  modelId=f'witness-ridge{args.ridge:g}-cap{args.cap:g}-' +
                  ('all' if args.feature is None else '+'.join(args.feature)))
    # Observed pair envelope. Anything outside it preserves the original heuristic.
    vectors = [p[k] for p in data['pairs'] for k in ('preferred', 'other')]
    schema['minimum'] = [min(v[i] for v in vectors) for i in range(len(NAMES))]
    schema['maximum'] = [max(v[i] for v in vectors) for i in range(len(NAMES))]
    report['labelMeaning'] = data['labelMeaning']
    report['coefficients'] = dict(zip(NAMES, weights))
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / 'model.json').write_text(json.dumps(schema, indent=2) + '\n')
    (args.out / 'fit-report.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
