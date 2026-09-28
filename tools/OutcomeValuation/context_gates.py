"""Training-only binary relic interactions, compiled to ordinary model trees.

Only native relic presence (zero or one) may gate an observed numeric column.
No card/relic utility is assigned here. Product columns never enter the runtime
feature schema: a product split becomes a presence test and an ordinary split.
"""
from collections import Counter
import math

import numpy as np


def presence_columns(names):
    return [i for i, name in enumerate(names)
            if name.startswith("relic/") and name.endswith("/present")
            and len(name.split("/")) == 3]


def signal_columns(document, maximum=64):
    """Use fitted columns plus generic observations, including unused ones.

    A purely conditional signal can have zero marginal gain and be absent from
    the prefix. Reserve half the slots for existing aggregate observations.
    These are raw input categories, not hand-assigned utilities or relic rules.
    """
    if maximum < 2:
        raise ValueError("At least two signal slots required")
    counts = Counter()

    def visit(node):
        if node["Feature"] < 0:
            return
        counts[node["Feature"]] += 1
        visit(node["Left"])
        visit(node["Right"])

    for tree in document["Forest"]:
        visit(tree)
    for i, weight in enumerate(document["LinearWeights"]):
        if weight:
            counts[i] += 1
    gates = set(presence_columns(document["FeatureNames"]))
    names = document["FeatureNames"]
    fitted = sorted((i for i in counts if i not in gates),
                    key=lambda i: (-counts[i], names[i]))[:maximum // 2]
    generic = [i for i, name in enumerate(names) if i not in fitted and (
        name.startswith(("battle/", "player/", "resource/", "osty/"))
        or name.startswith("pile/") and ("/type/" in name or name.count("/") == 2))]
    return fitted + sorted(generic, key=lambda i: names[i])[:maximum - len(fitted)]


def select_products(matrix, names, signals, gradient, hessian, *, maximum=64,
                    check_deadline=lambda: None):
    """Screen interactions on training gradients; not a feature-importance score.

    The denominator uses the same diagonal Hessian upper bound as the fitter.
    Allocate one row vector per candidate, never the full Cartesian matrix.
    """
    if (matrix.ndim != 2 or matrix.shape[1] != len(names) or maximum < 1
            or gradient.shape != (len(matrix),) or hessian.shape != gradient.shape
            or not np.isfinite(gradient).all() or not np.isfinite(hessian).all()
            or (hessian < 0).any() or len(set(signals)) != len(signals)
            or any(not 0 <= i < len(names) for i in signals)):
        raise ValueError("Invalid contextual screening inputs")
    candidates = []
    for gate in presence_columns(names):
        check_deadline()
        values = matrix[:, gate]
        if not np.isin(values, [0, 1]).all():
            raise ValueError("Relic presence must be binary: " + names[gate])
        if not np.any(values == 0) or not np.any(values == 1):
            continue
        for signal in signals:
            check_deadline()
            if signal == gate:
                continue
            product = np.multiply(values, matrix[:, signal], dtype=np.float64)
            if not np.isfinite(product).all():
                raise ValueError("Non-finite contextual observation")
            correlation = float(np.dot(gradient, product))
            curvature = float(np.dot(hessian, product * product))
            merit = correlation * correlation / (curvature + .001)
            if not math.isfinite(merit):
                raise ValueError("Non-finite contextual screening score")
            if merit > 0:
                candidates.append((merit, gate, signal))
    candidates.sort(key=lambda item: (-item[0], names[item[1]], names[item[2]]))
    return [(gate, signal) for _, gate, signal in candidates[:maximum]], {
        "positiveCandidates": len(candidates),
        "selected": [dict(gate=names[gate], signal=names[signal], screeningMerit=merit)
                     for merit, gate, signal in candidates[:maximum]],
    }


def expand_products(matrix, names, products):
    """Append a bounded number of exact float32 binary-gated observations."""
    eligible = set(presence_columns(names))
    if (matrix.ndim != 2 or matrix.shape[1] != len(names)
            or len(products) > 64 or len(set(products)) != len(products)
            or any(gate not in eligible or not 0 <= signal < len(names)
                   or gate == signal for gate, signal in products)):
        raise ValueError("Invalid contextual products")
    result = np.empty((len(matrix), len(names) + len(products)), dtype=np.float32)
    result[:, :len(names)] = matrix
    if not np.isfinite(result[:, :len(names)]).all():
        raise ValueError("Missing or non-finite observations are unsupported")
    for i, (gate, signal) in enumerate(products):
        if not np.isin(result[:, gate], [0, 1]).all():
            raise ValueError("Relic presence must be binary: " + names[gate])
        result[:, len(names) + i] = result[:, gate] * result[:, signal]
    return result


def compile_products(tree, columns, products):
    """Convert an already imported depth-four tree to raw-column depth <= 8.

    Thresholds already follow production's <= convention. If the gate is zero,
    the product is zero regardless of the signal; otherwise it is the signal.
    The binary-domain assumption is established by native presence extraction
    and checked on every supplied training matrix by expand_products.
    """
    if columns < 1 or len(products) > 64 or any(
            not 0 <= gate < columns or not 0 <= signal < columns or gate == signal
            for gate, signal in products):
        raise ValueError("Invalid product column mapping")

    def visit(node, depth):
        feature = node["Feature"]
        if depth > 4 or not math.isfinite(node["Threshold"]) or not math.isfinite(node["Mean"]):
            raise ValueError("Invalid contextual tree or depth")
        if feature == -1:
            if node.get("Left") is not None or node.get("Right") is not None:
                raise ValueError("Invalid contextual leaf")
            return dict(node)
        if not 0 <= feature < columns + len(products):
            raise ValueError("Invalid contextual split column")
        left, right = visit(node["Left"], depth + 1), visit(node["Right"], depth + 1)
        if feature < columns:
            return dict(node, Left=left, Right=right)
        gate, signal = products[feature - columns]
        numeric = dict(node, Feature=signal, Left=left, Right=right)
        absent = left if 0 <= node["Threshold"] else right
        return dict(Feature=gate, Threshold=0, Mean=0, Left=absent, Right=numeric)

    return visit(tree, 0)
