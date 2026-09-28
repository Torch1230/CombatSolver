"""Optional contextual residual prototype; never invoked by the default fitter.

Fit 48 ordinary depth-six trees, screen <=64 binary relic products on the same
training graph, then fit 16 depth-four trees. Compile products back to the
existing raw-feature tree schema (<=64 trees, depth <=8). No new runtime path,
handwritten utility, synthetic label, or evaluation-data input is introduced.
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
from xgboost_fit import Deadline, convert_tree, predict_document, training_parameters


def fit(directory, output, seconds):
    started = time.monotonic()
    if xgb.__version__ != "3.4.1" or not 1 <= seconds <= 1200:
        raise ValueError("Requires pinned XGBoost 3.4.1 and a shared <=1200s deadline")
    if output.exists():
        raise ValueError("Output directory already exists")
    output.mkdir(parents=True)
    until = started + seconds

    def check_deadline():
        if time.monotonic() >= until:
            raise TimeoutError("Complete contextual fitting budget exhausted")

    manifest = read_manifest(directory)
    parameters = training_parameters(3)
    models, metrics, inputs, expected = {}, [], [], []
    for head in manifest["heads"]:
        check_deadline()
        head_started = time.monotonic()
        matrix, edges, margins = read_head(directory, head)
        foundation = head["foundation"]
        names = foundation["FeatureNames"]
        data = xgb.DMatrix(matrix, base_margin=np.asarray(margins, dtype=np.float32), nthread=4)
        prefix = xgb.train(parameters, data, num_boost_round=48,
                           obj=lambda scores, _: derivatives(scores, edges),
                           callbacks=[Deadline(until)])
        prefix_prediction = prefix.predict(data, output_margin=True)
        trees = [convert_tree(json.loads(t), len(names)) for t in prefix.get_dump(dump_format="json")]
        prefix_document = dict(foundation, Forest=trees)
        signals = signal_columns(prefix_document)
        products, screening = select_products(matrix, names, signals,
            *derivatives(prefix_prediction, edges), check_deadline=check_deadline)
        del data
        expanded = expand_products(matrix, names, products)
        data = xgb.DMatrix(expanded, base_margin=prefix_prediction, nthread=4)
        suffix = xgb.train(dict(parameters, max_depth=4), data, num_boost_round=16,
                           obj=lambda scores, _: derivatives(scores, edges),
                           callbacks=[Deadline(until)])
        trees += [compile_products(convert_tree(json.loads(t), expanded.shape[1]), len(names), products)
                  for t in suffix.get_dump(dump_format="json")]
        if len(trees) != 64:
            raise ValueError("Incomplete contextual model")
        document = dict(foundation, Forest=trees)
        prediction = predict_document(document, matrix)
        reference = suffix.predict(data, output_margin=True)
        np.testing.assert_allclose(prediction, reference, rtol=2e-5, atol=2e-5)
        selected = np.unique(np.linspace(0, len(matrix) - 1, min(256, len(matrix)), dtype=int))
        for row in selected:
            inputs.append(dict(Character=head["character"] or None, Features={
                name: float(matrix[row, column]) for column, name in enumerate(names)
                if matrix[row, column] != 0}))
            expected.append(float(prediction[row]))
        model_margin = prediction[edges["preferred"]] - prediction[edges["other"]]
        reference_margin = reference[edges["preferred"]] - reference[edges["other"]]
        models[head["character"]] = document
        prefix.save_model(output / ("prefix-" + str(len(models) - 1) + ".json"))
        suffix.save_model(output / ("suffix-" + str(len(models) - 1) + ".json"))
        metrics.append(dict(character=head["character"], rows=len(matrix), pairs=len(edges),
            signalColumns=[names[i] for i in signals], productColumns=len(products), screening=screening,
            initialLoss=loss(margins, edges), prefixLoss=loss(prefix_prediction, edges),
            finalLoss=loss(prediction, edges), float32MaxPredictionDrift=float(np.max(np.abs(prediction-reference))),
            float32PairSignChanges=int(np.count_nonzero(np.sign(model_margin) != np.sign(reference_margin))),
            seconds=time.monotonic()-head_started))
        print(json.dumps({k: v for k, v in metrics[-1].items()
                          if k not in ("screening", "signalColumns")}), flush=True)
        del prefix, suffix, data, expanded, matrix, edges, margins, prediction, reference, prefix_prediction
    check_deadline()
    model = models[""] if manifest["partition"] == "shared" else dict(CharacterSchema=1, CharacterModels=models)
    # Publish only the ordinary tree document; product descriptions remain
    # diagnostic sidecars, never new feature requirements for the Mod.
    for name, value in (("parity-inputs.json", inputs), ("parity-expected.json", expected),
                        ("metrics.json", dict(backend="context-gated-xgboost-cpu", version=xgb.__version__,
                            parameters=parameters, ordinaryRounds=48, contextualRounds=16,
                            contextualDepth=4, maximumProducts=64, heads=metrics,
                            seconds=time.monotonic()-started,
                            peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss*1024))):
        (output / name).write_text(json.dumps(value, allow_nan=False))
    encoded = json.dumps(model, allow_nan=False)
    check_deadline()
    (output / "model.json").write_text(encoded)
    check_deadline()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seconds", type=int, default=1200)
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds)
