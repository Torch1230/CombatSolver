#!/usr/bin/env python3
"""Compare fixed roots exactly and classify quality with the 0.47.0 interim ordering."""

import argparse
import json
from pathlib import Path
import sys


def read_cases(directory):
    return {path.parent.name: json.loads(path.read_text(encoding="utf-8"))
            for path in directory.glob("*/case.json")}


def first_difference(left, right, path=""):
    if type(left) is not type(right):
        return {"path": path, "left": left, "right": right}
    if isinstance(left, dict):
        for key in sorted(left.keys() | right.keys()):
            child = f"{path}.{key}" if path else key
            if key not in left or key not in right:
                return {"path": child, "left": left.get(key, "<missing>"),
                        "right": right.get(key, "<missing>")}
            difference = first_difference(left[key], right[key], child)
            if difference:
                return difference
    elif isinstance(left, list):
        if len(left) != len(right):
            return {"path": f"{path}.length", "left": len(left), "right": len(right)}
        for index, (before, after) in enumerate(zip(left, right)):
            difference = first_difference(before, after, f"{path}[{index}]")
            if difference:
                return difference
    elif left != right:
        return {"path": path, "left": left, "right": right}
    return None


def better(candidate, current):
    """Frozen SolverInterimResultOrdering.IsBetter ordering."""
    for key in ("won", "survives"):
        if candidate[key] != current[key]:
            return candidate[key]
    if candidate["deathSaveUseCount"] != current["deathSaveUseCount"]:
        return candidate["deathSaveUseCount"] < current["deathSaveUseCount"]
    theft = candidate.get("theftPolicy")
    if theft != current.get("theftPolicy"):
        raise ValueError("Theft policies differ")
    if theft == "PreserveResources" and candidate["outstandingStolenResource"] != current["outstandingStolenResource"]:
        return candidate["outstandingStolenResource"] < current["outstandingStolenResource"]
    left_burden = candidate["strategicHpDeficit"] + candidate["potionStrategicCost"]
    right_burden = current["strategicHpDeficit"] + current["potionStrategicCost"]
    if left_burden != right_burden:
        return left_burden < right_burden
    if candidate["strategicHpDeficit"] != current["strategicHpDeficit"]:
        return candidate["strategicHpDeficit"] < current["strategicHpDeficit"]
    for key in ("growthHpCredit", "growthRewardCount"):
        if candidate[key] != current[key]:
            return candidate[key] > current[key]
    if (candidate["strategicHpDeficit"] == current["strategicHpDeficit"]
            and candidate["potionStrategicCost"] == current["potionStrategicCost"]
            and candidate["projectedBattlePotionCount"] == current["projectedBattlePotionCount"]
            and candidate["projectedBattleHpLost"] != current["projectedBattleHpLost"]):
        return candidate["projectedBattleHpLost"] < current["projectedBattleHpLost"]
    left_turn = candidate["combatEndedTurn"] if candidate["combatEndedTurn"] is not None else 2147483647
    right_turn = current["combatEndedTurn"] if current["combatEndedTurn"] is not None else 2147483647
    if left_turn != right_turn:
        return left_turn < right_turn
    if candidate["projectedBattlePotionCount"] != current["projectedBattlePotionCount"]:
        return candidate["projectedBattlePotionCount"] < current["projectedBattlePotionCount"]
    if candidate["enemyHp"] != current["enemyHp"]:
        return candidate["enemyHp"] < current["enemyHp"]
    return candidate["score"] > current["score"]


def classify(left, right, budget_ms=None):
    if left is None or right is None:
        return {"classification": "不可比较", "reason": "缺少一侧结果"}
    if left.get("status") != "comparable" or right.get("status") != "comparable":
        return {"classification": "不可比较", "reason":
                f"baseline={left.get('reason') or left.get('status')}; after={right.get('reason') or right.get('status')}"}
    identity = first_difference(left["identity"], right["identity"])
    if identity:
        return {"classification": "不可比较", "reason": "根或政策不一致", "firstDifference": identity}
    if budget_ms is not None:
        for side, case in (("baseline", left), ("after", right)):
            elapsed = (case.get("observations") or {}).get("elapsedMilliseconds")
            if elapsed is not None and elapsed >= budget_ms - 1000:
                return {"classification": "不可比较",
                        "reason": f"{side} search elapsed {elapsed:.0f} ms reached time boundary"}
    fields = ("quality", "outcome", "actions", "continuations", "metrics", "pruneCounters")
    differences = {field: difference for field in fields
                   if (difference := first_difference(left.get(field), right.get(field), field))}
    if not differences:
        classification = "逐位相同"
    else:
        after_better = better(right["quality"], left["quality"])
        before_better = better(left["quality"], right["quality"])
        if after_better and before_better:
            raise ValueError("Frozen route ordering is inconsistent")
        classification = "变好" if after_better else "变差" if before_better else "质量不变"
    return {"classification": classification, "firstDifferences": differences,
            "before": left.get("outcome"), "after": right.get("outcome")}


def compare(left_directory, right_directory, budget_ms=None):
    if budget_ms is None:
        manifest = Path(__file__).resolve().parents[3] / "coverage/corpora/strategy/p0.json"
        budget_ms = json.loads(manifest.read_text(encoding="utf-8"))["searchBudgetMilliseconds"]
    left = read_cases(left_directory)
    right = read_cases(right_directory)
    return {label: classify(left.get(label), right.get(label), budget_ms)
            for label in sorted(left.keys() | right.keys())}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--left", type=Path, required=True)
    parser.add_argument("--right", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise FileExistsError(f"Comparison output already exists: {args.out}")
    report = compare(args.left, args.right)
    args.out.mkdir(parents=True)
    (args.out / "comparison.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# 策略语料对照", "", "完整动作、续用及非时序指标的首个差异见 comparison.json。", ""]
    for label, entry in report.items():
        lines.append(f"- {label}：{entry['classification']}"
                     + (f"；{entry['reason']}" if entry.get("reason") else ""))
    (args.out / "comparison.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    for line in lines[4:]:
        print(line)
    return 1 if any(entry["classification"] == "变差" for entry in report.values()) else 0


if __name__ == "__main__":
    sys.exit(main())
