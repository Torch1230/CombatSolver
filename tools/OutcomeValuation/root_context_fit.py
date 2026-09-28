"""Explicit-root contextual prototype, never invoked by the default fitter.

Fresh 48-tree Hessian-3 prefix plus 16 supported contextual updates. Products
compile to existing raw-feature trees; authoritative C# labels, margins and
root assignments are mandatory. No development or final-test input is read.
"""
import argparse
import json
from pathlib import Path
import resource
import time

import numpy as np
import xgboost as xgb

from context_gates import compile_products, expand_products, select_products, signal_columns
from ranking_data import derivatives, loss, read_head, read_manifest
from root_context_round import best_product_stump, choose_update, leaf_values
from root_provenance import read_assignments
from xgboost_fit import Deadline, convert_tree, predict_document, training_parameters


def fit_context_rounds(matrix, names, products, roots, edges, margins, trees,
                       check_deadline, until, *, rounds=16):
    expanded = expand_products(matrix, names, products)
    forest_sum = np.zeros(len(matrix), dtype=np.float64)
    for tree in trees:
        forest_sum += leaf_values(tree, matrix)
    data = xgb.DMatrix(expanded, nthread=4)
    updates, reports = [], []
    for iteration in range(rounds):
        check_deadline()
        # C# sums unscaled leaves in double, then applies the forest scale.
        # Every derivative uses the actually retained, pruned model.
        current = .1 * forest_sum + margins
        gradient, hessian = derivatives(current, edges)
        data.set_base_margin(current.astype(np.float32))
        proposals = []
        for label, minimum_hessian in [('hessian3', 3), ('root-pruned', 0)]:
            check_deadline()
            booster = xgb.train(dict(training_parameters(minimum_hessian), max_depth=4), data,
                num_boost_round=1, obj=lambda _scores, _data: (gradient, hessian),
                callbacks=[Deadline(until)])
            imported = convert_tree(json.loads(booster.get_dump(dump_format='json')[0]), expanded.shape[1])
            proposals.append((label, imported))
        stump, stump_report = best_product_stump(expanded, len(names), roots, gradient, hessian,
                                                check_deadline)
        proposals.append(('supported-product-stump', stump))
        tree, forest_sum, report = choose_update(proposals, expanded, roots, gradient, hessian,
                                               forest_sum, margins, edges)
        compiled = compile_products(tree, len(names), products)
        np.testing.assert_array_equal(leaf_values(compiled, matrix), leaf_values(tree, expanded))
        updates.append(compiled)
        reports.append(dict(report, iteration=iteration, stumpProposal=stump_report,
                            productSplits=count_product_splits(tree, len(names))))
    return updates, .1 * forest_sum + margins, reports


def count_product_splits(tree, columns):
    if tree['Feature'] == -1:
        return 0
    return (int(tree['Feature'] >= columns) + count_product_splits(tree['Left'], columns)
            + count_product_splits(tree['Right'], columns))


def fit(directory, output, seconds):
    started = time.monotonic()
    if xgb.__version__ != '3.4.1' or not 1 <= seconds <= 1200:
        raise ValueError('Requires pinned XGBoost 3.4.1 and a shared <=1200s deadline')
    if output.exists():
        raise ValueError('Output directory already exists')
    output.mkdir(parents=True)
    until = started + seconds

    def check_deadline():
        if time.monotonic() >= until:
            raise TimeoutError('Complete root-aware fitting budget exhausted')

    manifest = read_manifest(directory)
    models, metrics, inputs, expected = {}, [], [], []
    parameters = training_parameters(3)
    for head in manifest['heads']:
        check_deadline()
        head_started = time.monotonic()
        matrix, edges, margins = read_head(directory, head)
        roots = read_assignments(directory, head, edges)
        foundation = head['foundation']
        names = foundation['FeatureNames']
        np.testing.assert_array_equal(predict_document(foundation, matrix), margins)
        data = xgb.DMatrix(matrix, base_margin=np.asarray(margins, dtype=np.float32), nthread=4)
        prefix = xgb.train(parameters, data, num_boost_round=48,
                           obj=lambda scores, _: derivatives(scores, edges), callbacks=[Deadline(until)])
        reference = prefix.predict(data, output_margin=True)
        trees = [convert_tree(json.loads(t), len(names)) for t in prefix.get_dump(dump_format='json')]
        prefix_document = dict(foundation, Forest=trees)
        prediction = predict_document(prefix_document, matrix)
        np.testing.assert_allclose(prediction, reference, rtol=2e-5, atol=2e-5)
        prefix_drift = float(np.max(np.abs(prediction - reference)))
        signals = signal_columns(prefix_document)
        products, screening = select_products(matrix, names, signals, *derivatives(prediction, edges),
                                              check_deadline=check_deadline)
        del data
        updates, fitted, rounds = fit_context_rounds(matrix, names, products, roots, edges, margins,
                                                     trees, check_deadline, until)
        document = dict(foundation, Forest=trees + updates)
        if len(document['Forest']) != 64:
            raise ValueError('Incomplete root-aware model')
        actual = predict_document(document, matrix)
        np.testing.assert_array_equal(actual, fitted)
        selected = np.unique(np.linspace(0, len(matrix) - 1, min(256, len(matrix)), dtype=int))
        for row in selected:
            inputs.append(dict(Character=head['character'] or None, Features={
                name: float(matrix[row, column]) for column, name in enumerate(names)
                if matrix[row, column] != 0}))
            expected.append(float(actual[row]))
        models[head['character']] = document
        prefix.save_model(output / ('prefix-' + str(len(models) - 1) + '.json'))
        metrics.append(dict(character=head['character'], rows=len(matrix), pairs=len(edges),
            participatingRoots=head['participatingRoots'], signalColumns=[names[i] for i in signals],
            productColumns=len(products), screening=screening, initialLoss=loss(margins, edges),
            prefixLoss=loss(prediction, edges), finalLoss=loss(actual, edges),
            prefixFloat32MaxPredictionDrift=prefix_drift, exportedPredictionDrift=0.,
            productSplits=sum(r['productSplits'] for r in rounds), rounds=rounds,
            seconds=time.monotonic() - head_started))
        print(json.dumps({k: v for k, v in metrics[-1].items()
                          if k not in ('screening', 'signalColumns', 'rounds')}), flush=True)
        del prefix, matrix, edges, margins, roots, reference, prediction, fitted, actual
    model = models[''] if manifest['partition'] == 'shared' else dict(CharacterSchema=1, CharacterModels=models)
    for name, value in [('parity-inputs.json', inputs), ('parity-expected.json', expected),
                        ('metrics.json', dict(backend='root-supported-context-xgboost-cpu',
                            version=xgb.__version__, parameters=parameters, ordinaryRounds=48,
                            contextualRounds=16, contextualDepth=4, minimumRootParticipation=3,
                            maximumProducts=64, heads=metrics, seconds=time.monotonic() - started,
                            peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024))]:
        check_deadline()
        (output / name).write_text(json.dumps(value, allow_nan=False))
    encoded = json.dumps(model, allow_nan=False)
    check_deadline()
    (output / 'model.json').write_text(encoded)
    check_deadline()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--seconds', type=int, default=1200)
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds)
