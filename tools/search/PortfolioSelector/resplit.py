#!/usr/bin/env python3
"""Reassign manifest splits so validation and test hold real, kind-stratified battles.

Splits are assigned only across battles that produced at least one usable observation, and
round-robin within each encounter kind. Battles without usable labels stay in train, so they
never consume a validation or test slot. Label values are never consulted.
"""
import argparse
import json
from pathlib import Path

KINDS = ("monster", "elite", "boss")
TRAIN_SHARE = 3  # out of 5


def kind_of(label):
    for kind in KINDS:
        if f"-{kind}-" in label:
            return kind
    raise SystemExit(f"label does not name an encounter kind: {label}")


def usable_count(runs, label):
    path = runs / label / "portfolio-observations.json"
    if not path.exists():
        return 0
    return sum(1 for row in json.loads(path.read_text())["observations"]
               if row.get("ran") and row.get("labelUsable"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--runs", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    items = json.loads(args.manifest.read_text())["items"]
    usable = {item["label"]: usable_count(args.runs, item["label"]) for item in items}
    pools = {kind: sorted(label for label, count in usable.items()
                          if count > 0 and kind_of(label) == kind)
             for kind in KINDS}
    assigned = {}
    for kind, labels in pools.items():
        for index, label in enumerate(labels):
            slot = index % 5
            assigned[label] = "train" if slot < TRAIN_SHARE else (
                "validation" if slot == TRAIN_SHARE else "test")
    result = []
    for item in items:
        split = assigned.get(item["label"], "train")
        if usable[item["label"]] == 0 and split != "train":
            raise SystemExit(f"{item['label']} has no usable labels but was assigned {split}")
        result.append({"label": item["label"], "split": split, "battleId": item.get("battleId", item["label"])})
    args.out.write_text(json.dumps({"items": result}, indent=2) + "\n")
    summary = {}
    for kind, labels in pools.items():
        counts = {"train": 0, "validation": 0, "test": 0}
        for label in labels:
            counts[assigned[label]] += 1
        summary[kind] = counts
    print(json.dumps({
        "pools": {kind: len(labels) for kind, labels in pools.items()},
        "assigned": summary,
        "total": {"train": sum(1 for item in result if item["split"] == "train"),
                  "validation": sum(1 for item in result if item["split"] == "validation"),
                  "test": sum(1 for item in result if item["split"] == "test")},
    }, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
