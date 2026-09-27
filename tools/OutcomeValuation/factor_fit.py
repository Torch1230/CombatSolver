"""Offline rank-8 factor interactions over the authoritative C# preference graph.

Fixed learned linear utility plus a second-order factorization machine. This
fits no neural network and requires no Python dependency in the Mod. Training
loss is diagnostic; held-out battles remain the decision-quality gate.
"""
from __future__ import annotations

import argparse
import json
import math
import resource
import time
from pathlib import Path

import numpy as np
from scipy import optimize, sparse

from ranking_data import derivatives, loss, read_head, read_manifest

RANK = 8
REGULARIZATION = 0.001
OPTIONS = dict(maxiter=128, maxls=20, ftol=1e-9, gtol=1e-6)


def objective(flat, matrix, squared, edges, base, rank, regularization):
    factors = flat.reshape(matrix.shape[1], rank)
    projection = matrix @ factors
    scores = base + .5 * (np.sum(projection * projection, axis=1)
                         - squared @ np.sum(factors * factors, axis=1))
    normalizer = float(edges["weight"].sum())
    gradient = derivatives(scores, edges)[0] / normalizer
    grad = (matrix.T @ (gradient[:, None] * projection)
            - np.asarray(squared.T @ gradient).reshape(-1, 1) * factors
            + regularization * factors)
    value = loss(scores, edges) / normalizer + .5 * regularization * float(np.sum(factors * factors))
    return value, np.asarray(grad).ravel()


def normalize(matrix, edges):
    raw = sparse.csr_matrix(matrix, dtype=np.float64)
    row_weight = (np.bincount(edges["preferred"], weights=edges["weight"] / 2, minlength=len(matrix))
                  + np.bincount(edges["other"], weights=edges["weight"] / 2, minlength=len(matrix)))
    # Root-balanced endpoint RMS. The floor avoids magnifying rare binary identities.
    scale = np.maximum(1., np.sqrt(np.asarray(raw.multiply(raw).T @ row_weight).ravel() / row_weight.sum()))
    return raw.multiply(1 / scale).tocsr(), scale


def score_raw(matrix, factors, linear):
    """Canonical float32 observation / double column-order C# prediction."""
    matrix = np.asarray(matrix, dtype=np.float32)
    projection = np.zeros((len(matrix), factors.shape[1]))
    diagonal = np.zeros_like(projection)
    scores = np.zeros(len(matrix))
    for i in range(matrix.shape[1]):
        values = matrix[:, i].astype(np.float64)
        scores += values * linear[i]
        product = values[:, None] * factors[i]
        projection += product
        diagonal += product * product
    for k in range(factors.shape[1]):
        scores += .5 * (projection[:, k] * projection[:, k] - diagonal[:, k])
    return scores


def fit(directory: Path, output: Path, seconds=1200, stop: Path | None = None):
    started = time.monotonic()
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
        head_start = time.monotonic()
        matrix, edges, margins = read_head(directory, head)
        x, scale = normalize(matrix, edges)
        x2 = x.multiply(x)
        active = np.asarray(x.getnnz(axis=0)).ravel() > 0
        initial = np.random.default_rng(0).normal(0, .01, size=(x.shape[1], RANK))
        initial[~active] = 0
        calls = 0

        def fun(flat):
            nonlocal calls
            check_deadline()
            calls += 1
            result = objective(flat, x, x2, edges, margins, RANK, REGULARIZATION)
            if not math.isfinite(result[0]) or not np.isfinite(result[1]).all():
                raise ValueError("Non-finite interaction objective")
            return result

        initial_loss = fun(initial.ravel())[0]
        result = optimize.minimize(fun, initial.ravel(), jac=True, method="L-BFGS-B", options=OPTIONS)
        # A declared fixed iteration limit is permitted and reported as unconverged.
        # Other optimizer failures must not become a publishable candidate.
        if (result.status not in (0, 1) or not np.isfinite(result.x).all()
                or result.fun > initial_loss):
            raise ValueError("Unusable optimization result: " + str(result.message))
        factors = result.x.reshape(x.shape[1], RANK) / scale[:, None]
        if np.any(factors[~active] != 0):
            raise ValueError("Unobserved columns acquired factors")
        document = dict(head["foundation"], Schema=10, FactorWeights=factors.tolist())
        models[head["character"]] = document
        selected = np.unique(np.linspace(0, len(matrix) - 1, min(256, len(matrix)), dtype=int))
        scores = score_raw(matrix[selected], factors, document["LinearWeights"])
        for i, predicted in zip(selected, scores):
            parity_inputs.append(dict(Character=head["character"] or None, Features={
                name: float(matrix[i, j]) for j, name in enumerate(document["FeatureNames"]) if matrix[i, j] != 0}))
            parity_expected.append(float(predicted))
        projection = x @ result.x.reshape(x.shape[1], RANK)
        predicted = margins + .5 * (np.sum(projection * projection, axis=1)
                    - x2 @ np.sum(result.x.reshape(x.shape[1], RANK) ** 2, axis=1))
        metrics.append(dict(character=head["character"], roots=head["participatingRoots"],
                            rows=len(matrix), pairs=len(edges), features=x.shape[1], activeFeatures=int(active.sum()),
                            initialPairLoss=loss(margins, edges), finalPairLoss=loss(predicted, edges),
                            initialObjective=initial_loss, finalObjective=float(result.fun),
                            iterations=int(result.nit), objectiveCalls=calls, converged=bool(result.success),
                            optimizerStatus=int(result.status), optimizerMessage=str(result.message),
                            seconds=time.monotonic() - head_start))
        (output / "partial-metrics.json").write_text(json.dumps(metrics, allow_nan=False))
        print(json.dumps(metrics[-1]), flush=True)
        del matrix, edges, margins, x, x2, initial, result, factors, projection, predicted
    model = models[""] if manifest["partition"] == "shared" else dict(CharacterSchema=1, CharacterModels=models)
    for name, value in (("parity-inputs.json", parity_inputs), ("parity-expected.json", parity_expected),
                        ("metrics.json", dict(backend="factor-interactions", rank=RANK,
                            regularization=REGULARIZATION, optimizer="L-BFGS-B", options=OPTIONS,
                            heads=metrics, seconds=time.monotonic() - started,
                            peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024))):
        (output / name).write_text(json.dumps(value, allow_nan=False))
    # Publish only a complete model, and only after serialization also meets the cap.
    serialized = json.dumps(model, allow_nan=False)
    check_deadline()
    (output / "model.json").write_text(serialized)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--seconds", type=int, default=1200)
    parser.add_argument("--stop", type=Path)
    args = parser.parse_args()
    fit(args.export, args.output, args.seconds, args.stop)
