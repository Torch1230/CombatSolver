#!/usr/bin/env python3
"""Summarize controlled Beam weight probes against one fixed-root baseline."""

import argparse
import json
import math
from pathlib import Path

from compare import read_cases
from sensitivity import classify


TERMS = {"CurrentEnergy", "PersistentBuffDelta", "EnemyHp"}


def parse_probe(value):
    try:
        setting, directory = value.split("=", 1)
        term, scale_text = setting.split(":", 1)
        scale = float(scale_text)
    except ValueError as error:
        raise argparse.ArgumentTypeError("probe must be Term:Scale=directory") from error
    if term not in TERMS or not math.isfinite(scale) or not 0 <= scale <= 2:
        raise argparse.ArgumentTypeError("invalid Beam term or scale")
    return term, scale, Path(directory)


def summarize(baseline, probes):
    matrix = {}
    for term, scale, directory in probes:
        key = f"{term}:{scale:g}"
        if key in matrix:
            raise ValueError(f"Duplicate probe: {key}")
        cases = read_cases(directory)
        if not cases:
            raise ValueError(f"Probe directory contains no corpus cases: {directory}")
        matrix[key] = {label: classify(baseline.get(label), case, term, scale)
                       for label, case in sorted(cases.items())}
    return matrix


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--probe", type=parse_probe, action="append", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise FileExistsError(f"Matrix output already exists: {args.out}")
    baseline = read_cases(args.baseline)
    if not baseline:
        raise ValueError("Baseline directory contains no corpus cases")
    matrix = summarize(baseline, args.probe)
    args.out.mkdir(parents=True)
    (args.out / "matrix.json").write_text(
        json.dumps(matrix, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# Beam 权重敏感度矩阵", "", "| 扰动 | 固定根 | 质量 | 首个质量差异 | 战损前→后 | 用药前→后 |", "|---|---|---|---|---|---|"]
    for probe, cases in matrix.items():
        for label, result in cases.items():
            before = result.get("before") or {}
            after = result.get("after") or {}
            difference = result.get("qualityDifference") or {}
            quality = (f"{difference['path']}: {difference['left']}→{difference['right']}"
                       if difference else result.get("reason", "-"))
            loss = f"{before.get('projectedBattleHpLost', '-')}→{after.get('projectedBattleHpLost', '-')}"
            potions = f"{before.get('potionCount', '-')}→{after.get('potionCount', '-')}"
            lines.append(f"| {probe} | {label} | {result['classification']} | {quality} | {loss} | {potions} |")
            print(f"{probe} {label}: {result['classification']}")
    (args.out / "matrix.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return 1 if any(result["classification"] in ("变差", "不可比较")
                    for cases in matrix.values() for result in cases.values()) else 0


if __name__ == "__main__":
    raise SystemExit(main())
