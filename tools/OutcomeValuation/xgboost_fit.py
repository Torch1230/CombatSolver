"""Optional CPU residual fitter. Labels and linear foundation come only from C#.

Install xgboost-cpu==3.4.1 in a private environment, never in the Mod runtime.
The exported pair graph has a coupled Hessian. Use its diagonal upper bound,
not a claim that the edges are independent row labels. Development battles
remain the acceptance criterion; fitting loss is only a diagnostic.
"""
from __future__ import annotations

import argparse
import json
import math
import resource
import time
from pathlib import Path

import numpy as np
import xgboost as xgb

from ranking_data import EDGE_DTYPE, derivatives, loss, read_head, read_manifest
PARAMETERS = dict(tree_method="hist", device="cpu", nthread=4, max_depth=6,
                  max_bin=32, eta=0.1, reg_lambda=0.001, reg_alpha=0,
                  min_child_weight=0, gamma=0, subsample=1, colsample_bytree=1,
                  base_score=0, boost_from_average=0, seed=0,
                  objective="reg:squarederror", disable_default_eval_metric=1)


def training_parameters(minimum_leaf_hessian=0.0):
    if not math.isfinite(minimum_leaf_hessian) or minimum_leaf_hessian < 0:
        raise ValueError("Minimum leaf Hessian must be finite and nonnegative")
    # Each actual root contributes total edge weight 1. With the 2h diagonal
    # upper bound, its total Hessian over both endpoints is at most 1.
    # A leaf threshold of 3 therefore needs contributions from at least 3
    # roots; this is a conservative bound, not an exact distinct-root count.
    return dict(PARAMETERS, min_child_weight=minimum_leaf_hessian)


def convert_tree(node, columns, depth=0):
    if depth > 6:
        raise ValueError("Unexpected tree depth")
    if "leaf" in node:
        value = float(np.float32(node["leaf"]))
        if not math.isfinite(value) or "children" in node:
            raise ValueError("Invalid XGBoost leaf")
        # XGBoost leaves already include shrinkage. Production applies 0.1.
        return dict(Feature=-1, Threshold=0, Mean=value / 0.1, Left=None, Right=None)
    name = node["split"]
    if not name.startswith("f") or not name[1:].isdigit():
        raise ValueError("Only numeric feature indices are supported")
    feature = int(name[1:])
    threshold = float(np.float32(node["split_condition"]))
    children = {child["nodeid"]: child for child in node["children"]}
    if (not 0 <= feature < columns or not math.isfinite(threshold)
            or len(children) != 2 or node["yes"] == node["no"]
            or set(children) != {node["yes"], node["no"]}
            or node["missing"] not in children):
        raise ValueError("Invalid numeric XGBoost split")
    # The engine casts every observation to float32, then compares <= in double.
    # Double predecessor makes this exactly equivalent to XGBoost's strict <,
    # including zero, float32 subnormals and the minimum finite float32.
    return dict(Feature=feature, Threshold=math.nextafter(threshold, -math.inf), Mean=0,
                Left=convert_tree(children[node["yes"]], columns, depth + 1),
                Right=convert_tree(children[node["no"]], columns, depth + 1))


def predict_document(document, matrix):
    matrix = np.asarray(matrix, dtype=np.float32)
    if not np.isfinite(matrix).all():
        raise ValueError("Missing or non-finite observations are unsupported")
    forest = np.zeros(len(matrix), dtype=np.float64)

    def visit(node, indices):
        if node["Feature"] == -1:
            forest[indices] += node["Mean"]
        else:
            left = matrix[indices, node["Feature"]].astype(np.float64) <= node["Threshold"]
            visit(node["Left"], indices[left])
            visit(node["Right"], indices[~left])

    for tree in document["Forest"]:
        visit(tree, np.arange(len(matrix)))
    linear = np.zeros(len(matrix), dtype=np.float64)
    for i, weight in enumerate(document["LinearWeights"]):
        if weight:
            linear += weight * matrix[:, i].astype(np.float64)
    return 0.1 * forest + linear


class Deadline(xgb.callback.TrainingCallback):
    def __init__(self, until):
        self.until = until

    def before_iteration(self, model, epoch, evals_log):
        if time.monotonic() >= self.until:
            raise TimeoutError("Complete-model fitting budget exhausted")
        return False


def fit(directory: Path, output: Path, seconds: int, minimum_leaf_hessian=0.0):
    start = time.monotonic()
    parameters = training_parameters(minimum_leaf_hessian)
    if xgb.__version__ != "3.4.1" or not 1 <= seconds <= 1200:
        raise ValueError("Expected pinned CPU XGBoost 3.4.1 and at most 1200 seconds")
    if output.exists():
        raise ValueError("Output directory already exists")
    output.mkdir(parents=True)
    manifest = read_manifest(directory)
    models, metrics, parity_inputs, parity_expected = {}, [], [], []
    for head in manifest["heads"]:
        head_start = time.monotonic()
        matrix, edges, margins = read_head(directory, head)
        foundation = head["foundation"]
        if (foundation["Schema"] not in (9, 10) or foundation.get("FactorWeights") is not None
                or head["character"] in models):
            raise ValueError("Unsupported or repeated model head")
        dmatrix = xgb.DMatrix(matrix, base_margin=np.asarray(margins, dtype=np.float32), nthread=4)
        booster = xgb.train(parameters, dmatrix, num_boost_round=64,
                            obj=lambda scores, _: derivatives(scores, edges),
                            callbacks=[Deadline(start + seconds)])
        trees = [convert_tree(json.loads(tree), matrix.shape[1])
                 for tree in booster.get_dump(dump_format="json")]
        if len(trees) != 64:
            raise ValueError("Incomplete boosting model")
        document = dict(foundation, Forest=trees)
        predicted = predict_document(document, matrix)
        reference = booster.predict(dmatrix, output_margin=True)
        # XGBoost accumulates in float32; production accumulates in double.
        # This validates bounded numerical drift, not bit-identical arithmetic.
        np.testing.assert_allclose(predicted, reference, rtol=2e-5, atol=2e-5)
        drift = float(np.max(np.abs(predicted - reference)))
        pair_margin = predicted[edges["preferred"]] - predicted[edges["other"]]
        reference_margin = reference[edges["preferred"]] - reference[edges["other"]]
        changed = int(np.count_nonzero(np.sign(pair_margin) != np.sign(reference_margin)))
        selected = np.unique(np.linspace(0, len(matrix) - 1, min(256, len(matrix)), dtype=int))
        for i in selected:
            parity_inputs.append(dict(Character=head["character"] or None, Features={
                name: float(matrix[i, j]) for j, name in enumerate(foundation["FeatureNames"])
                if matrix[i, j] != 0}))
            parity_expected.append(float(predicted[i]))
        models[head["character"]] = document
        booster.save_model(output / ("head-" + str(len(models) - 1) + ".json"))
        metrics.append(dict(character=head["character"], rows=len(matrix), pairs=len(edges),
                            initialLoss=loss(margins, edges), finalLoss=loss(predicted, edges),
                            float32MaxPredictionDrift=drift, float32PairSignChanges=changed,
                            seconds=time.monotonic() - head_start))
        print(json.dumps(metrics[-1]), flush=True)
        del booster, dmatrix, matrix, edges, margins, predicted, reference
    if time.monotonic() - start > seconds:
        raise TimeoutError("Complete-model fitting budget exhausted before publication")
    model = models[""] if manifest["partition"] == "shared" else dict(CharacterSchema=1, CharacterModels=models)
    for name, value in (("model.json", model), ("parity-inputs.json", parity_inputs),
                        ("parity-expected.json", parity_expected), ("metrics.json", dict(
                            backend="xgboost-cpu", version=xgb.__version__, parameters=parameters,
                            rounds=64, heads=metrics, seconds=time.monotonic() - start,
                            peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024))):
        (output / name).write_text(json.dumps(value, allow_nan=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seconds", type=int, default=1200)
    parser.add_argument("--minimum-leaf-hessian", type=float, default=0.0,
                        help="Minimum child Hessian; root-balanced pairs bound each root contribution by 1")
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds, args.minimum_leaf_hessian)
