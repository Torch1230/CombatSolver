"""Explicit offline fitter with root support enforced during tree growth.

Shares the authoritative graph, fresh prefix, export and deadline with the
context fitter. Each suffix round compares Hessian-3, prefix-column histogram
and all-power histogram proposals using actual training loss. No new runtime
mode or feature schema; no development/final-test outcomes enter fitting.
"""
import argparse
import json
from pathlib import Path
import time

import numpy as np
import xgboost as xgb

from context_gates import compile_products, expand_products
from ranking_data import derivatives
from root_context_fit import count_product_splits, fit
from root_context_round import choose_update, leaf_values
from root_histogram import RootHistogram, contextual_columns
from xgboost_fit import Deadline, convert_tree, training_parameters


def fit_histogram_rounds(matrix, names, products, roots, edges, margins, trees,
                         check_deadline, until, *, rounds=16, signal_names):
    check_deadline()
    expanded = expand_products(matrix, names, products)
    product_columns = list(range(len(names), expanded.shape[1]))
    # Validate names, indices and capacity before constructing either cache.
    all_columns = contextual_columns(names, signal_names, products)
    lookup = {name: i for i, name in enumerate(names)}
    prefix_columns = sorted({lookup[n] for n in signal_names}
                            | {g for g, _ in products} | set(product_columns))
    if not prefix_columns:
        raise ValueError('Prefix proposal has no observation columns')
    caches, cache_report = [], []
    for label, columns in [('prefix-signals', prefix_columns), ('all-powers', all_columns)]:
        check_deadline()
        began = time.monotonic()
        cache = RootHistogram(expanded, columns, products=product_columns,
                              check_deadline=check_deadline)
        caches.append((label, cache))
        cache_report.append(dict(name=label, columns=len(columns), bytes=cache.bins.nbytes,
                                 seconds=time.monotonic() - began))
    forest_sum = np.zeros(len(matrix), dtype=np.float64)
    for tree in trees:
        forest_sum += leaf_values(tree, matrix)
    data = xgb.DMatrix(expanded, nthread=4)
    updates, reports = [], []
    for iteration in range(rounds):
        check_deadline()
        current = .1 * forest_sum + margins
        gradient, hessian = derivatives(current, edges)
        data.set_base_margin(current.astype(np.float32))
        booster = xgb.train(dict(training_parameters(3), max_depth=4), data,
            num_boost_round=1, obj=lambda _scores, _data: (gradient, hessian),
            callbacks=[Deadline(until)])
        ordinary = convert_tree(json.loads(booster.get_dump(dump_format='json')[0]),
                                expanded.shape[1])
        proposals, diagnostics = [('hessian3', ordinary)], {}
        for label, cache in caches:
            check_deadline()
            began = time.monotonic()
            tree, diagnostic = cache.fit(roots, gradient, hessian,
                                         check_deadline=check_deadline)
            proposals.append((label, tree))
            diagnostics[label] = dict(diagnostic, seconds=time.monotonic() - began)
        retained, forest_sum, report = choose_update(proposals, expanded, roots, gradient,
            hessian, forest_sum, margins, edges)
        compiled = compile_products(retained, len(names), products)
        np.testing.assert_array_equal(leaf_values(compiled, matrix), leaf_values(retained, expanded))
        updates.append(compiled)
        reports.append(dict(report, iteration=iteration, histogramProposals=diagnostics,
            histogramCaches=cache_report if iteration == 0 else [],
            productSplits=count_product_splits(retained, len(names))))
    return updates, .1 * forest_sum + margins, reports


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--seconds', type=int, default=1200)
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds, round_fitter=fit_histogram_rounds,
        backend='root-supported-histogram-xgboost-cpu')
