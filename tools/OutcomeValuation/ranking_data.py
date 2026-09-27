"""Authoritative offline C# ranking exports: observations, pair graph and losses.

No game policy is reconstructed here. Both external fitters use these same
validated float32 observations and equal-root-weighted witnessed preferences.
"""
import hashlib
import json
import math
from pathlib import Path

import numpy as np

EDGE_DTYPE = np.dtype([("preferred", "<i4"), ("other", "<i4"), ("weight", "<f8")])


def read_manifest(directory):
    manifest = json.loads((directory / "manifest.json").read_text())
    if (manifest["exportSchema"] != 1 or manifest["partition"] not in ("shared", "character")
            or manifest["matrixFormat"] != "row-major-little-endian-float32-explicit-zero"
            or manifest["edgeFormat"] != "little-endian-int32-int32-float64"
            or manifest["marginFormat"] != "little-endian-float64" or not manifest["heads"]):
        raise ValueError("Unsupported exported ranking format")
    characters = [head["character"] for head in manifest["heads"]]
    if (len(characters) != len(set(characters))
            or manifest["partition"] == "shared" and characters != [""]
            or manifest["partition"] == "character" and any(not c for c in characters)):
        raise ValueError("Invalid or repeated model heads")
    return manifest


def validate_foundation(document):
    names, weights = document["FeatureNames"], document["LinearWeights"]
    if (document["Schema"] not in (9, 10) or document.get("FactorWeights") is not None
            or any(not isinstance(n, str) or not n.strip() for n in names)
            or len(set(names)) != len(names) or len(weights) != len(names)
            or not np.isfinite(weights).all() or len(document["Forest"]) != 1):
        raise ValueError("Expected a compatible, factor-free linear foundation")
    tree = document["Forest"][0]
    if (tree["Feature"] != -1 or tree["Mean"] != 0 or tree.get("Left") is not None
            or tree.get("Right") is not None or not math.isfinite(tree["Threshold"])):
        raise ValueError("The foundation must have no residual trees")


def derivatives(scores, edges):
    preferred, other, weight = (edges[k] for k in EDGE_DTYPE.names)
    margin = scores[preferred].astype(np.float64) - scores[other]
    # Stable sigmoid(-margin), without changing the loss at extreme margins.
    probability = np.exp(-np.logaddexp(0, margin))
    g = weight * probability
    h = weight * probability * (1 - probability)
    gradient = (np.bincount(other, weights=g, minlength=len(scores))
                - np.bincount(preferred, weights=g, minlength=len(scores)))
    # Per edge H = h[[1,-1],[-1,1]]. 2hI-H = h[[1,1],[1,1]] is PSD.
    upper = 2 * (np.bincount(preferred, weights=h, minlength=len(scores))
                 + np.bincount(other, weights=h, minlength=len(scores)))
    return gradient, upper


def loss(scores, edges):
    margin = scores[edges["preferred"]].astype(np.float64) - scores[edges["other"]]
    return float(np.sum(edges["weight"] * np.logaddexp(0, -margin)))


def read_head(directory, head):
    validate_foundation(head["foundation"])
    rows, columns = head["rows"], len(head["foundation"]["FeatureNames"])
    if rows < 2 or columns < 1 or head["pairs"] < 2:
        raise ValueError("Insufficient exported observations")
    for key, size in (("matrix", rows * columns * 4), ("edges", head["pairs"] * 16),
                      ("margins", rows * 8)):
        name = head[key]
        if Path(name).name != name:
            raise ValueError("Export names must be local files")
        path = directory / name
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != size or digest != head["sha256"][name]:
            raise ValueError("Export size or hash mismatch: " + name)
    matrix = np.memmap(directory / head["matrix"], dtype="<f4", mode="r", shape=(rows, columns))
    edges = np.memmap(directory / head["edges"], dtype=EDGE_DTYPE, mode="r")
    margins = np.memmap(directory / head["margins"], dtype="<f8", mode="r")
    if (not np.isfinite(matrix).all() or not np.isfinite(margins).all()
            or not np.isfinite(edges["weight"]).all() or (edges["weight"] <= 0).any()
            or (edges["preferred"] == edges["other"]).any()
            or any((edges[k] < 0).any() or (edges[k] >= rows).any() for k in ("preferred", "other"))):
        raise ValueError("Invalid exported numerical values or pair endpoints")
    if not math.isclose(float(edges["weight"].sum()), head["participatingRoots"], rel_tol=1e-12):
        raise ValueError("Exported root weights do not sum to participating roots")
    return matrix, edges, margins

