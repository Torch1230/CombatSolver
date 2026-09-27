"""Bounded CPU tanh residual on the authoritative C# outcome preference graph.

A fixed learned linear foundation plus one hidden layer; no handwritten card
utility, game policy reconstruction, runtime Python or external inference engine.
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


def unpack(theta, columns, units):
    end = columns * units
    return theta[:end].reshape(columns, units), theta[end:end + units], theta[end + units:]


def objective(theta, matrix, edges, margins, roots, units, penalty):
    weights, bias, output = unpack(theta, matrix.shape[1], units)
    hidden = np.tanh(matrix @ weights + bias)
    scores = margins + hidden @ output
    gradient, _ = derivatives(scores, edges)
    gradient /= roots
    back = gradient[:, None] * output * (1 - hidden * hidden)
    dw = np.asarray(matrix.T @ back) + penalty * weights
    db = back.sum(axis=0) + penalty * bias
    dv = hidden.T @ gradient + penalty * output
    value = loss(scores, edges) / roots + .5 * penalty * float(theta @ theta)
    return value, np.concatenate((dw.ravel(), db, dv))


def predict_document(document, matrix):
    """Canonical observation/column/hidden accumulation used by the C# kernel."""
    matrix = np.asarray(matrix, dtype=np.float32)
    term = document['Neural']
    weights = np.asarray(term['InputWeights'], dtype=np.float64)
    hidden = np.broadcast_to(term['HiddenBias'], (len(matrix), len(term['HiddenBias']))).copy()
    linear = np.zeros(len(matrix))
    for i, coefficient in enumerate(document['LinearWeights']):
        value = matrix[:, i].astype(np.float64)
        if coefficient:
            linear += coefficient * value
        if np.any(weights[i]):
            hidden += value[:, None] * weights[i]
    for k, coefficient in enumerate(term['OutputWeights']):
        linear += np.tanh(hidden[:, k]) * coefficient
    return linear


def self_test():
    from ranking_data import EDGE_DTYPE
    matrix = sparse.csr_matrix([[1., .2, 0], [1., -.3, 1], [0, 1, .4], [0, .1, -.2]])
    edges = np.array([(0, 1, 1.), (2, 3, 1.)], dtype=EDGE_DTYPE)
    margins = np.array([.1, -.2, .3, .2]);units = 2
    theta = np.random.default_rng(7).normal(0, .15, (matrix.shape[1] + 2) * units)
    value, gradient = objective(theta, matrix, edges, margins, 2, units, .01)
    numeric = np.empty(len(theta));step = 1e-6
    for i in range(len(theta)):
        left = theta.copy();right = theta.copy();left[i] -= step;right[i] += step
        numeric[i] = (objective(right, matrix, edges, margins, 2, units, .01)[0]
                      - objective(left, matrix, edges, margins, 2, units, .01)[0]) / (2 * step)
    error = float(np.max(np.abs(numeric - gradient)))
    if error >= 1e-7:raise AssertionError('Finite-difference residual gradient mismatch')
    duplicate = edges.copy();duplicate['weight'] *= 2
    value2, gradient2 = objective(theta, matrix, duplicate, margins, 4, units, .01)
    np.testing.assert_allclose([value], [value2], atol=1e-14, rtol=0)
    np.testing.assert_allclose(gradient, gradient2, atol=1e-14, rtol=0)
    # Context has no within-pair difference but can condition a nonlinear contrast.
    _, context_gradient = objective(theta, matrix, edges, margins, 2, units, 0)
    if np.linalg.norm(context_gradient[:units]) <= 1e-8:raise AssertionError('Context interaction was lost')
    raw = matrix.toarray() * np.array([3., 7., 2.]);w,b,v = unpack(theta, 3, units)
    doc = dict(LinearWeights=[0.,0.,0.], Neural=dict(InputWeights=(w / np.array([3.,7.,2.])[:,None]).tolist(), HiddenBias=b.tolist(), OutputWeights=v.tolist()))
    expected = np.tanh(raw.astype(np.float32).astype(float) / np.array([3.,7.,2.]) @ w + b) @ v
    np.testing.assert_allclose(predict_document(doc, raw), expected, atol=1e-14, rtol=0)
    return dict(checks=4, maximumFiniteDifferenceError=error)


def fit(directory, output, seconds, units=16, penalty=.001, iterations=128):
    start = time.monotonic()
    if (not 1 <= seconds <= 1200 or not 1 <= units <= 32 or not 1 <= iterations <= 256
            or not math.isfinite(penalty) or penalty <= 0):
        raise ValueError('Invalid bounded neural training configuration')
    if output.exists():raise ValueError('Output directory already exists')
    output.mkdir(parents=True)
    manifest = read_manifest(directory)
    models, metrics, parity_inputs, parity_expected = {}, [], [], []
    deadline = start + seconds
    for head in manifest['heads']:
        head_start = time.monotonic();raw, edges, margins = read_head(directory, head)
        foundation = head['foundation'];columns = raw.shape[1]
        if foundation['Schema'] not in (9,10) or foundation.get('Neural') is not None:
            raise ValueError('Expected a factor-free learned linear foundation')
        # Only training-side variation chooses active inputs. All-root constants
        # add no information beyond the learned hidden bias; root-varying context stays.
        active = np.flatnonzero(np.max(raw, axis=0) != np.min(raw, axis=0))
        if not len(active):raise ValueError('No varying training observations')
        x = sparse.csr_matrix(raw[:, active], dtype=np.float64)
        scale = np.maximum(1., np.sqrt(np.asarray(x.power(2).mean(axis=0)).ravel()))
        x = x.multiply(1 / scale).tocsr()
        rng = np.random.default_rng(0)
        initial = np.concatenate((rng.normal(0, .02, (len(active), units)).ravel(),
                                  np.zeros(units), rng.normal(0, .02, units)))
        calls = 0
        def bounded(theta):
            nonlocal calls
            if time.monotonic() >= deadline:raise TimeoutError('Complete-model training limit exhausted')
            calls += 1
            return objective(theta, x, edges, margins, head['participatingRoots'], units, penalty)
        initial_objective = bounded(initial)[0]
        result = optimize.minimize(bounded, initial, method='L-BFGS-B', jac=True,
            options=dict(maxiter=iterations, maxls=20, maxcor=10, ftol=1e-9, gtol=1e-6))
        if result.status not in (0,1) or not np.isfinite(result.x).all() or not math.isfinite(result.fun):
            raise RuntimeError('Neural optimization failed: ' + str(result.message))
        w,b,v = unpack(result.x, len(active), units)
        folded = np.zeros((columns, units));folded[active] = w / scale[:,None]
        doc = dict(foundation, Schema=11, Neural=dict(InputWeights=folded.tolist(),
                    HiddenBias=b.tolist(), OutputWeights=v.tolist()))
        # Independent folded-units numerical check over every training observation.
        predicted = np.asarray(margins) + np.tanh(x @ w + b) @ v
        folded_prediction = np.asarray(margins) + np.tanh(sparse.csr_matrix(raw) @ folded + b) @ v
        np.testing.assert_allclose(predicted, folded_prediction, atol=1e-8, rtol=1e-10)
        selected = np.unique(np.linspace(0,len(raw)-1,min(256,len(raw)),dtype=int))
        canonical = predict_document(doc,raw[selected])
        np.testing.assert_allclose(canonical,predicted[selected],atol=1e-8,rtol=1e-10)
        for i,score in zip(selected,canonical):
            parity_inputs.append(dict(Character=head['character'] or None,Features={
                name:float(raw[i,j]) for j,name in enumerate(foundation['FeatureNames']) if raw[i,j]!=0}))
            parity_expected.append(float(score))
        models[head['character']] = doc
        metric = dict(character=head['character'], rows=len(raw),pairs=len(edges),activeColumns=len(active),
            initialLoss=loss(margins,edges),finalLoss=loss(predicted,edges),initialObjective=initial_objective,
            finalObjective=float(result.fun),iterations=int(result.nit),functionCalls=calls,
            converged=bool(result.success),optimizerStatus=int(result.status),optimizerMessage=str(result.message),
            gradientInfinityNorm=float(np.max(np.abs(result.jac))),seconds=time.monotonic()-head_start)
        metrics.append(metric);print(json.dumps(metric),flush=True)
        del raw,edges,margins,x,folded,predicted,folded_prediction
    if time.monotonic() >= deadline:raise TimeoutError('Complete-model training limit exhausted before publication')
    model = models[''] if manifest['partition']=='shared' else dict(CharacterSchema=1,CharacterModels=models)
    for name,value in [('model.json',model),('parity-inputs.json',parity_inputs),('parity-expected.json',parity_expected),
        ('metrics.json',dict(backend='scipy-tanh-residual',units=units,penalty=penalty,maximumIterations=iterations,
            heads=metrics,seconds=time.monotonic()-start,peakWorkingSetBytes=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss*1024))]:
        (output/name).write_text(json.dumps(value,allow_nan=False))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export',type=Path,nargs='?');parser.add_argument('output',type=Path,nargs='?')
    parser.add_argument('--seconds',type=int,default=1200);parser.add_argument('--units',type=int,default=16)
    parser.add_argument('--penalty',type=float,default=.001);parser.add_argument('--iterations',type=int,default=128)
    parser.add_argument('--self-test',action='store_true');args=parser.parse_args()
    if args.self_test:print(json.dumps(self_test()))
    else:
        if args.export is None or args.output is None:parser.error('export and output are required')
        fit(args.export,args.output,args.seconds,args.units,args.penalty,args.iterations)
