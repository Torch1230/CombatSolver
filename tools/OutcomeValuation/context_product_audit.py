"""Read-only Hessian feasibility audit of a saved contextual training run.

For a finite numeric product z, every threshold leaves one child containing
only strictly positive or only strictly negative z values. Nonnegative row
Hessians therefore bound that child's mass in every descendant too. A bound
below min_child_weight rules out this feature for that boosting round; a bound
above it proves neither useful gain nor selection. No labels or models change.
"""
import argparse
import json
import math
from pathlib import Path
import resource
import time

import numpy as np
import xgboost as xgb

from context_gates import expand_products, presence_columns
from ranking_data import derivatives, loss, read_head, read_manifest


def hessian_bounds(products, hessian, minimum):
    products = np.asarray(products)
    hessian = np.asarray(hessian)
    if (products.ndim != 2 or hessian.shape != (len(products),)
            or len(hessian) == 0 or not math.isfinite(minimum) or minimum < 0
            or not np.isfinite(products).all() or not np.isfinite(hessian).all()
            or (hessian < 0).any() or (hessian > np.finfo(np.float32).max).any()):
        raise ValueError("Invalid finite product/Hessian observations")
    # CPU custom gradients enter GradientPair<float>; CPU histogram sums use
    # GradientPairPrecise<double>. Keep a conservative margin near the cutoff
    # rather than attributing a borderline result to histogram summation order.
    native = hessian.astype(np.float32).astype(np.float64)
    total = float(native.sum())
    guard = float(max(1., total) * (1e-6 + 64 * len(native) * np.finfo(np.float64).eps))
    rows = []
    for column in products.T:
        positive = float(native[column > 0].sum())
        negative = float(native[column < 0].sum())
        upper = max(positive, negative)
        rows.append(dict(positiveMass=positive, negativeMass=negative,
                         nonzeroChildUpperMass=upper, numericalGuard=guard,
                         blockedByHessian=bool(upper + guard < minimum)))
    return rows


def audit(export, fitted, output, seconds=180):
    started = time.monotonic()
    if xgb.__version__ != '3.4.1' or not 1 <= seconds <= 600:
        raise ValueError('Expected pinned XGBoost 3.4.1 and bounded audit time')
    if output.exists():
        raise ValueError('Audit output already exists')
    manifest = read_manifest(export)
    metrics = json.loads((fitted / 'metrics.json').read_text())
    if (metrics['backend'] != 'context-gated-xgboost-cpu'
            or metrics['ordinaryRounds'] != 48 or metrics['contextualRounds'] != 16):
        raise ValueError('Expected the complete contextual prototype')
    minimum = metrics['parameters']['min_child_weight']
    reports = []
    for index, head in enumerate(manifest['heads']):
        if time.monotonic() - started >= seconds:
            raise TimeoutError('Context feasibility audit budget exhausted')
        matrix, edges, margins = read_head(export, head)
        names = head['foundation']['FeatureNames']
        lookup = {name: i for i, name in enumerate(names)}
        metadata = next(m for m in metrics['heads'] if m['character'] == head['character'])
        products = [(lookup[p['gate']], lookup[p['signal']]) for p in metadata['screening']['selected']]
        if len(products) != metadata['productColumns']:
            raise ValueError('Saved product mapping differs from fitted columns')
        prefix = xgb.Booster(model_file=fitted / f'prefix-{index}.json')
        suffix = xgb.Booster(model_file=fitted / f'suffix-{index}.json')
        prefix.set_param({'nthread': 4})
        suffix.set_param({'nthread': 4})
        if prefix.num_boosted_rounds() != 48 or suffix.num_boosted_rounds() != 16:
            raise ValueError('Saved boosting phase length mismatch')
        data = xgb.DMatrix(matrix, base_margin=np.asarray(margins, dtype=np.float32), nthread=4)
        prefix_scores = prefix.predict(data, output_margin=True)
        if not math.isclose(loss(prefix_scores, edges), metadata['prefixLoss'], rel_tol=1e-10, abs_tol=1e-10):
            raise ValueError('Saved prefix does not reproduce its training loss')
        del data
        expanded = expand_products(matrix, names, products)
        product_values = expanded[:, len(names):].copy()
        gate_columns = [i for i in presence_columns(names)
                        if np.any(matrix[:, i] == 0) and np.any(matrix[:, i] == 1)]
        gate_values = expanded[:, gate_columns].copy()
        data = xgb.DMatrix(expanded, base_margin=prefix_scores, nthread=4)
        rounds = []
        for step in range(16):
            if time.monotonic() - started >= seconds:
                raise TimeoutError('Context feasibility audit budget exhausted')
            # XGBoost end=0 means all trees, not the empty prefix.
            scores = prefix_scores if step == 0 else suffix.predict(
                data, output_margin=True, iteration_range=(0, step))
            hessian = derivatives(scores, edges)[1]
            products_report = hessian_bounds(product_values, hessian, minimum)
            gates_report = hessian_bounds(gate_values, hessian, minimum)
            rounds.append(dict(round=step + 1,
                productsBlocked=sum(r['blockedByHessian'] for r in products_report),
                productsNotRuledOut=sum(not r['blockedByHessian'] for r in products_report),
                productBounds=products_report, gateBounds=gates_report))
        report = dict(character=head['character'], products=metadata['screening']['selected'],
                      variablePresenceColumns=[names[i] for i in gate_columns], rounds=rounds,
                      productsBlockedEveryRound=sum(all(r['productBounds'][i]['blockedByHessian']
                          for r in rounds) for i in range(len(products))),
                      gateColumnsBlockedEveryRound=sum(all(r['gateBounds'][i]['blockedByHessian']
                          for r in rounds) for i in range(len(gate_columns))))
        reports.append(report)
        print(json.dumps({k: v for k, v in report.items() if k not in
                          ('products', 'rounds', 'variablePresenceColumns')}), flush=True)
        del matrix, edges, margins, prefix, suffix, expanded, data, product_values, gate_values
    result = dict(completed=True, scope='Training-round necessary feasibility bounds, not battle quality or causal attribution of regressions.',
                  minimumLeafHessian=minimum, heads=reports, seconds=time.monotonic() - started,
                  peakRssBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024,
                  newTrainingRuns=0, newCombatSearches=0, finalTestOpened=False)
    output.write_text(json.dumps(result, indent=2) + '\n')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('fitted', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--seconds', type=int, default=180)
    args = parser.parse_args()
    audit(args.export, args.fitted, args.output, args.seconds)
