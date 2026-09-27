"""Optional convex sparse ranking fit over the authoritative C# preference graph.

All coefficients are learned jointly. A positive/negative decomposition makes
the elastic-net objective smooth with nonnegative bounds; exact bound zeros
remain absent from native feature capture. No runtime Python is required.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import resource
import time

import numpy as np
from scipy import optimize, sparse

from ranking_data import read_head, read_manifest

OPTIONS = dict(maxiter=256, maxls=40, ftol=1e-10, gtol=1e-6)


def prepare(matrix, edges):
    """Use only witnessed pair differences; root-constant identities stay zero."""
    raw = sparse.csr_matrix(matrix, dtype=np.float64)
    delta = raw[edges["preferred"]] - raw[edges["other"]]
    delta.eliminate_zeros()
    weight = np.asarray(edges["weight"], dtype=np.float64)
    weight = weight / weight.sum()
    rms = np.sqrt(np.asarray(delta.multiply(delta).T @ weight).ravel())
    active = rms > 0
    scale = np.maximum(1., rms[active])
    return delta[:, active].multiply(1 / scale).tocsr(), weight, active, scale


def objective(split, delta, weight, l1, l2):
    size = delta.shape[1]
    coefficients = split[:size] - split[size:]
    margin = delta @ coefficients
    probability = np.exp(-np.logaddexp(0, margin))
    gradient = np.asarray(delta.T @ (-weight * probability)).ravel() + l2 * coefficients
    value = (float(weight @ np.logaddexp(0, -margin))
             + l1 * float(split.sum()) + .5 * l2 * float(coefficients @ coefficients))
    return value, np.concatenate((gradient + l1, -gradient + l1))


def kkt_residual(coefficients, delta, weight, l1, l2):
    margin = delta @ coefficients
    gradient = np.asarray(delta.T @ (-weight * np.exp(-np.logaddexp(0, margin)))).ravel()
    gradient += l2 * coefficients
    residual = np.where(coefficients != 0, gradient + l1 * np.sign(coefficients),
                        np.maximum(0., np.abs(gradient) - l1))
    return float(np.max(np.abs(residual), initial=0.))


def score_raw(matrix, coefficients):
    """Match sorted-column C# double accumulation over float32 observations."""
    matrix = np.asarray(matrix, dtype=np.float32)
    scores = np.zeros(len(matrix))
    for i in np.flatnonzero(coefficients):
        scores += matrix[:, i].astype(np.float64) * coefficients[i]
    return scores


def fit(directory: Path, output: Path, seconds=1200, stop: Path | None = None,
        l1=.001, l2=.001):
    started = time.monotonic()
    if not math.isfinite(l1) or l1 <= 0 or not math.isfinite(l2) or l2 < 0:
        raise ValueError("Require finite positive L1 and finite nonnegative L2")
    if not 1 <= seconds <= 1200 or output.exists():
        raise ValueError("Use a new output directory and at most 1200 seconds for all heads")
    manifest = read_manifest(directory)
    output.mkdir(parents=True)
    models, metrics, parity_inputs, parity_expected = {}, [], [], []

    def check_deadline():
        if time.monotonic() - started >= seconds or stop is not None and stop.exists():
            raise TimeoutError("Complete-model budget exhausted or STOP requested")

    for head in manifest["heads"]:
        check_deadline()
        head_started = time.monotonic()
        matrix, edges, _ = read_head(directory, head)
        delta, weight, active, scale = prepare(matrix, edges)
        if delta.shape[1] == 0:
            raise ValueError("No observed feature can distinguish the witnessed preferences")
        initial = np.zeros(delta.shape[1] * 2)
        calls = 0

        def fun(split):
            nonlocal calls
            check_deadline()
            calls += 1
            value, gradient = objective(split, delta, weight, l1, l2)
            if not math.isfinite(value) or not np.isfinite(gradient).all():
                raise ValueError("Non-finite sparse ranking objective")
            return value, gradient

        initial_value = fun(initial)[0]
        result = optimize.minimize(fun, initial, jac=True, method="L-BFGS-B",
                                  bounds=[(0., None)] * len(initial), options=OPTIONS)
        if (result.status not in (0, 1) or not np.isfinite(result.x).all()
                or (result.x < 0).any() or result.fun > initial_value):
            raise ValueError("Unusable sparse optimization result: " + str(result.message))
        size = delta.shape[1]
        normalized = result.x[:size] - result.x[size:]
        coefficients = np.zeros(matrix.shape[1])
        coefficients[active] = normalized / scale
        document = dict(head["foundation"], LinearWeights=coefficients.tolist())
        models[head["character"]] = document
        selected = np.unique(np.linspace(0, len(matrix) - 1, min(256, len(matrix)), dtype=int))
        scores = score_raw(matrix[selected], coefficients)
        for i, predicted in zip(selected, scores):
            parity_inputs.append(dict(Character=head["character"] or None, Features={
                name: float(matrix[i, j]) for j, name in enumerate(document["FeatureNames"]) if matrix[i, j] != 0}))
            parity_expected.append(float(predicted))
        metrics.append(dict(character=head["character"], roots=head["participatingRoots"],
            rows=len(matrix), pairs=len(edges), features=matrix.shape[1],
            varyingFeatures=int(active.sum()), nonzeroFeatures=int(np.count_nonzero(coefficients)),
            differenceNonzeros=delta.nnz, initialObjective=initial_value, finalObjective=float(result.fun),
            finalMeanPairLoss=float(weight @ np.logaddexp(0, -(delta @ normalized))),
            kktResidual=kkt_residual(normalized, delta, weight, l1, l2),
            iterations=int(result.nit), objectiveCalls=calls, converged=bool(result.success),
            optimizerStatus=int(result.status), optimizerMessage=str(result.message),
            seconds=time.monotonic() - head_started))
        (output / "partial-metrics.json").write_text(json.dumps(metrics, allow_nan=False))
        print(json.dumps(metrics[-1]), flush=True)
        del matrix, edges, delta, weight, initial, result, normalized, coefficients
    model = models[""] if manifest["partition"] == "shared" else dict(CharacterSchema=1, CharacterModels=models)
    for name, value in (("parity-inputs.json", parity_inputs), ("parity-expected.json", parity_expected),
                       ("metrics.json", dict(backend="sparse-linear", l1=l1, l2=l2,
                           optimizer="L-BFGS-B positive-negative decomposition", options=OPTIONS,
                           heads=metrics, seconds=time.monotonic() - started,
                           peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024))):
        (output / name).write_text(json.dumps(value, allow_nan=False))
    serialized = json.dumps(model, allow_nan=False)
    check_deadline()
    (output / "model.json").write_text(serialized)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seconds", type=int, default=1200)
    parser.add_argument("--stop", type=Path)
    parser.add_argument("--l1", type=float, default=.001)
    parser.add_argument("--l2", type=float, default=.001)
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds, args.stop, args.l1, args.l2)
