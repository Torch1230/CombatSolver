"""Train a small portfolio gate; test observations are never opened.

Manifest labels name battle directories under --runs. Repeated runs of one battle
must share battleId (otherwise label is the battle identity) and the same split.
Only validation chooses the skip threshold; tree settings are fixed before fit.
"""
import argparse
from dataclasses import dataclass, field
import hashlib
import json
import math
from pathlib import Path
import time
from uuid import UUID

import numpy as np
import sklearn
from sklearn.tree import DecisionTreeClassifier


SCHEMA_VERSION = 1
FEATURE_NAMES = [
    "initial_hp", "maximum_hp", "card_count", "power_count", "enemy_count", "turn",
    "searchable_potions", "boss", "beam", "node_budget", "card_branches", "pile_branches",
    "hand_branches", "baseline_won", "baseline_loss", "baseline_actions", "baseline_turns",
    "baseline_expanded", "baseline_transitions", "baseline_milliseconds",
    "incumbent_loss", "incumbent_potions", "remaining_node_fraction", "remaining_time_fraction",
    "member_width_ratio", "member_second_band", "member_base_score", "dop",
    "growth_targets", "relic_target_count", "theft_policy", "forced_potion_directives",
]
SKIP_THRESHOLDS = (0.0, 0.025, 0.05)
RANGE_FEATURES = tuple(range(13)) + tuple(range(24, len(FEATURE_NAMES)))


@dataclass(frozen=True)
class Observation:
    features: tuple
    improved: bool
    elapsed_milliseconds: float
    battle: str


@dataclass
class Split:
    rows: list = field(default_factory=list)
    total_rows: int = 0
    battles: set = field(default_factory=set)
    labels: list = field(default_factory=list)


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def read_manifest(path):
    document = read_json(path)
    items = document.get("items") if isinstance(document, dict) else document
    if not isinstance(items, list):
        raise ValueError("manifest must be an array of items")
    labels, battle_splits, result = set(), {}, []
    for item in items:
        if not isinstance(item, dict):
            raise ValueError("manifest items must be objects")
        label, split = item.get("label"), item.get("split")
        if (not isinstance(label, str) or not label or label in (".", "..")
                or any(c in label for c in ("/", "\\", "\x00"))):
            raise ValueError("manifest label must be a single directory name")
        if split not in ("train", "validation", "test"):
            raise ValueError(f"{label}: unknown split {split!r}")
        if label in labels:
            raise ValueError(f"duplicate manifest label: {label}")
        labels.add(label)
        battle = item.get("battleId", label)
        if not isinstance(battle, str) or not battle:
            raise ValueError(f"{label}: battleId must be a nonempty string")
        if battle in battle_splits and battle_splits[battle] != split:
            raise ValueError(f"battle {battle!r} crosses splits")
        battle_splits[battle] = split
        result.append((label, split, battle))
    return result


def metadata(document, path):
    if not isinstance(document, dict):
        raise ValueError(f"{path}: observation document must be an object")
    if type(document.get("schemaVersion")) is not int or document["schemaVersion"] != SCHEMA_VERSION:
        raise ValueError(f"{path}: unsupported schemaVersion")
    if document.get("featureNames") != FEATURE_NAMES:
        raise ValueError(f"{path}: incompatible featureNames or feature order")
    result = {key: document.get(key) for key in
              ("schemaVersion", "solverAssemblyId", "gameAssemblyId", "featureNames")}
    for key in ("solverAssemblyId", "gameAssemblyId"):
        value = result[key]
        if not isinstance(value, str):
            raise ValueError(f"{path}: {key} must be a GUID")
        try:
            UUID(value)
        except ValueError as error:
            raise ValueError(f"{path}: {key} must be a GUID") from error
    return result


def usable_observation(row, battle, location):
    if not isinstance(row, dict) or type(row.get("ran")) is not bool:
        raise ValueError(f"{location}: ran must be boolean")
    if row.get("labelUsable") is not None and type(row["labelUsable"]) is not bool:
        raise ValueError(f"{location}: labelUsable must be boolean or null")
    if not row["ran"] or row.get("labelUsable") is not True:
        return None
    if type(row.get("improved")) is not bool:
        raise ValueError(f"{location}: usable improved must be boolean")
    features = row.get("features")
    if (not isinstance(features, list) or len(features) != len(FEATURE_NAMES)
            or any(type(value) not in (int, float) or not math.isfinite(value)
                   for value in features)):
        raise ValueError(f"{location}: features must be finite numbers in schema order")
    with np.errstate(over="ignore"):
        converted = np.asarray(features, dtype=np.float32)
    if not np.isfinite(converted).all():
        raise ValueError(f"{location}: features must be representable as float32")
    elapsed = row.get("elapsedMilliseconds")
    if type(elapsed) not in (int, float) or not math.isfinite(elapsed) or elapsed < 0:
        raise ValueError(f"{location}: elapsedMilliseconds must be finite and nonnegative")
    return Observation(tuple(features), row["improved"], elapsed, battle)


def load_splits(manifest, runs):
    items = read_manifest(manifest)
    splits = {name: Split() for name in ("train", "validation")}
    common, sources = None, set()
    for label, split, battle in items:
        if split == "test":
            continue
        path = Path(runs) / label / "portfolio-observations.json"
        source = path.resolve()
        if source in sources:
            raise ValueError(f"duplicate observation source: {path}")
        sources.add(source)
        document = read_json(path)
        current = metadata(document, path)
        if common is None:
            common = current
        elif current != common:
            raise ValueError(f"{path}: mixed assembly/schema/features")
        observations = document.get("observations")
        if not isinstance(observations, list):
            raise ValueError(f"{path}: observations must be an array")
        data = splits[split]
        data.labels.append(label)
        data.battles.add(battle)
        data.total_rows += len(observations)
        for index, row in enumerate(observations):
            usable = usable_observation(row, battle, f"{path}:observations[{index}]")
            if usable is not None:
                data.rows.append(usable)
    if not splits["train"].rows:
        raise ValueError("no usable train observations; cannot export a model with real evidence")
    return common, splits


def fit_tree(rows, max_depth, min_samples_leaf):
    matrix = np.asarray([row.features for row in rows], dtype=np.float32)
    labels = np.asarray([row.improved for row in rows], dtype=np.int8)
    classifier = DecisionTreeClassifier(
        max_depth=max_depth, min_samples_leaf=min_samples_leaf, random_state=0)
    started = time.perf_counter()
    classifier.fit(matrix, labels)
    elapsed = (time.perf_counter() - started) * 1000
    return classifier, matrix, elapsed


def export_model(classifier, matrix, rows, common, minimum_battles):
    tree = classifier.tree_
    paths = classifier.decision_path(matrix).tocsc()
    nodes = []

    def visit(source):
        indices = paths.indices[paths.indptr[source]:paths.indptr[source + 1]]
        leaf = tree.children_left[source] == tree.children_right[source]
        node = {
            "feature": -1 if leaf else int(tree.feature[source]),
            "threshold": 0.0 if leaf else float(tree.threshold[source]),
            "left": -1,
            "right": -1,
            "improvementRate": sum(rows[i].improved for i in indices) / len(indices),
            "samples": int(len(indices)),
            "battles": len({rows[i].battle for i in indices}),
        }
        target = len(nodes)
        nodes.append(node)
        if not leaf:
            node["left"] = visit(tree.children_left[source])
            node["right"] = visit(tree.children_right[source])
        return target

    visit(0)
    # C# checks original doubles for support before casting split features to float32.
    raw = np.asarray([row.features for row in rows], dtype=np.float64)
    return {
        **common,
        "minimum": raw.min(axis=0).tolist(),
        "maximum": raw.max(axis=0).tolist(),
        "skipThreshold": 0.0,
        "minimumBattles": minimum_battles,
        "nodes": nodes,
    }


def leaf_index(model, features):
    with np.errstate(over="ignore"):
        values = np.asarray(features, dtype=np.float32)
    cursor = 0
    while model["nodes"][cursor]["feature"] >= 0:
        node = model["nodes"][cursor]
        cursor = node["left"] if float(values[node["feature"]]) <= node["threshold"] else node["right"]
    return cursor


def should_skip(model, features):
    if len(features) != len(FEATURE_NAMES) or not all(math.isfinite(value) for value in features):
        return False
    if any(features[i] < model["minimum"][i] or features[i] > model["maximum"][i]
           for i in RANGE_FEATURES):
        return False
    leaf = model["nodes"][leaf_index(model, features)]
    return (leaf["battles"] >= model["minimumBattles"]
            and leaf["improvementRate"] <= model["skipThreshold"])


def metrics(data, skip):
    skipped = [row for row in data.rows if skip(row)]
    missed = [row for row in skipped if row.improved]
    return {
        "totalRows": data.total_rows,
        "validRows": len(data.rows),
        "excludedRows": data.total_rows - len(data.rows),
        "battleCount": len(data.battles),
        "validBattleCount": len({row.battle for row in data.rows}),
        "improvedRows": sum(row.improved for row in data.rows),
        "skippedRows": len(skipped),
        "skippedBattleCount": len({row.battle for row in skipped}),
        "skipRate": len(skipped) / len(data.rows) if data.rows else 0.0,
        "missedImprovements": len(missed),
        "missedImprovementBattles": len({row.battle for row in missed}),
        "observedElapsedMilliseconds": sum(row.elapsed_milliseconds for row in data.rows),
        "offlineSavedMilliseconds": sum(row.elapsed_milliseconds for row in skipped),
    }


def choose_threshold(model, train, validation, min_samples_leaf):
    candidates = []
    enough_samples = len(train.rows) >= min_samples_leaf
    for threshold in SKIP_THRESHOLDS:
        candidate = {**model, "skipThreshold": threshold}
        measured = metrics(validation, lambda row: should_skip(candidate, row.features))
        safe = (enough_samples and measured["missedImprovements"] == 0
                and measured["offlineSavedMilliseconds"] > 0)
        candidates.append({"skipThreshold": threshold, "eligible": safe, "validation": measured})
    safe_candidates = [candidate for candidate in candidates if candidate["eligible"]]
    if safe_candidates:
        chosen = max(safe_candidates, key=lambda candidate: (
            candidate["validation"]["offlineSavedMilliseconds"], -candidate["skipThreshold"]))
        model["skipThreshold"] = chosen["skipThreshold"]
        status, reason = "selected", "zero_validation_misses_with_observed_savings"
    else:
        model["skipThreshold"] = 0.0
        model["minimumBattles"] = max(model["minimumBattles"], model["nodes"][0]["battles"] + 1)
        status = "no_candidate"
        reason = ("insufficient_training_samples" if not enough_samples else
                  "no_usable_validation" if not validation.rows else
                  "no_safe_candidate_with_observed_savings")
    return {"status": status, "reason": reason, "candidates": candidates}


def train(manifest, runs, model_path, report_path, *, max_depth=2, min_samples_leaf=12,
          minimum_battles=4):
    if max_depth not in (2, 3) or min_samples_leaf not in (8, 12) or minimum_battles not in (3, 4):
        raise ValueError("use max_depth 2/3, min_samples_leaf 8/12 and minimum_battles 3/4")
    model_path, report_path = Path(model_path), Path(report_path)
    outputs = {model_path.resolve(), report_path.resolve()}
    if len(outputs) != 2 or Path(manifest).resolve() in outputs:
        raise ValueError("model, report and manifest paths must be distinct")
    started = time.perf_counter()
    common, splits = load_splits(manifest, runs)
    for split in splits.values():
        for label in split.labels:
            source = Path(runs) / label / "portfolio-observations.json"
            if source.resolve() in outputs:
                raise ValueError("outputs must not overwrite observations")
    classifier, matrix, fit_milliseconds = fit_tree(splits["train"].rows, max_depth, min_samples_leaf)
    model = export_model(classifier, matrix, splits["train"].rows, common, minimum_battles)
    selection = choose_threshold(model, splits["train"], splits["validation"], min_samples_leaf)
    encoded_model = (json.dumps(model, indent=2, allow_nan=False) + "\n").encode("utf-8")
    report = {
        "schemaVersion": SCHEMA_VERSION,
        "status": selection["status"],
        "solverAssemblyId": common["solverAssemblyId"],
        "gameAssemblyId": common["gameAssemblyId"],
        "modelSha256": hashlib.sha256(encoded_model).hexdigest(),
        "trainingMilliseconds": fit_milliseconds,
        "hyperparameters": {"maxDepth": max_depth, "minSamplesLeaf": min_samples_leaf,
                            "minimumBattles": minimum_battles, "randomState": 0},
        "libraryVersions": {"scikit-learn": sklearn.__version__, "numpy": np.__version__},
        "labelSemantics": "improved is the recorded production-policy comparator result; no ranking relabeling",
        "battleIdentity": "manifest battleId when present, otherwise label; repeated runs require the same battleId",
        "metricScope": "ran=true and labelUsable=true observations only; other rows retain the original search",
        "savingsScope": "offline sum of recorded skipped-member milliseconds; not end-to-end savings",
        "testObservationsRead": False,
        "selection": {**selection, "skipThreshold": model["skipThreshold"],
                      "minimumBattles": model["minimumBattles"]},
    }
    for name, data in splits.items():
        report[name] = {
            **metrics(data, lambda row: should_skip(model, row.features)),
            "labels": data.labels,
            "baselines": {
                "always-run": metrics(data, lambda row: False),
                "always-skip": metrics(data, lambda row: True),
            },
        }
    report["totalMilliseconds"] = (time.perf_counter() - started) * 1000
    encoded_report = (json.dumps(report, indent=2, allow_nan=False) + "\n").encode("utf-8")
    model_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    model_path.write_bytes(encoded_model)
    report_path.write_bytes(encoded_report)
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--runs", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--max-depth", choices=(2, 3), default=2, type=int)
    parser.add_argument("--min-samples-leaf", choices=(8, 12), default=12, type=int)
    parser.add_argument("--minimum-battles", choices=(3, 4), default=4, type=int)
    args = parser.parse_args(argv)
    try:
        report = train(args.manifest, args.runs, args.model, args.report,
                       max_depth=args.max_depth, min_samples_leaf=args.min_samples_leaf,
                       minimum_battles=args.minimum_battles)
    except (ValueError, OSError) as error:
        parser.error(str(error))
    print(json.dumps({"status": report["status"], "reason": report["selection"]["reason"],
                      "modelSha256": report["modelSha256"], "model": str(args.model),
                      "report": str(args.report)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
