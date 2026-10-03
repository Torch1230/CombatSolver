#!/usr/bin/env python3
"""Compare one controlled Beam weight probe against a fixed-root corpus baseline."""

import argparse
import copy
import json
from pathlib import Path

from compare import better, first_difference, read_cases


def classify(baseline, probe, term, scale):
    if baseline is None or probe is None:
        return {"classification": "不可比较", "reason": "缺少一侧结果"}
    if baseline.get("status") != "comparable" or probe.get("status") != "comparable":
        return {"classification": "不可比较", "reason":
                f"baseline={baseline.get('reason') or baseline.get('status')}; "
                f"probe={probe.get('reason') or probe.get('status')}"}
    left_identity = copy.deepcopy(baseline["identity"])
    right_identity = copy.deepcopy(probe["identity"])
    left_profile = left_identity["policy"]["profile"]
    right_profile = right_identity["policy"]["profile"]
    if left_profile.get("beamWeightPerturbation") is not None:
        return {"classification": "不可比较", "reason": "基线已有 Beam 权重扰动"}
    actual = right_profile.get("beamWeightPerturbation")
    if (not isinstance(actual, dict) or actual.get("term") != term
            or actual.get("scale") != scale):
        return {"classification": "不可比较", "reason": "扰动参数与请求不符",
                "actual": actual}
    left_profile.pop("beamWeightPerturbation", None)
    right_profile.pop("beamWeightPerturbation", None)
    difference = first_difference(left_identity, right_identity)
    if difference:
        return {"classification": "不可比较", "reason": "根或其他政策不一致",
                "firstDifference": difference}
    after_better = better(probe["quality"], baseline["quality"])
    before_better = better(baseline["quality"], probe["quality"])
    if after_better and before_better:
        raise ValueError("Frozen route ordering is inconsistent")
    classification = "变好" if after_better else "变差" if before_better else "质量不变"
    return {"classification": classification,
            "before": baseline["outcome"], "after": probe["outcome"],
            "qualityDifference": first_difference(baseline["quality"], probe["quality"]),
            "actionDifference": first_difference(baseline["actions"], probe["actions"]),
            "workDifference": first_difference(baseline["metrics"], probe["metrics"])}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--term", choices=("CurrentEnergy", "PersistentBuffDelta", "EnemyHp"),
                        required=True)
    parser.add_argument("--scale", type=float, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if not 0 <= args.scale <= 2:
        parser.error("--scale must be between 0 and 2")
    if args.out.exists():
        raise FileExistsError(f"Sensitivity output already exists: {args.out}")
    left = read_cases(args.baseline)
    right = read_cases(args.probe)
    if not right:
        raise ValueError("Probe directory contains no corpus cases")
    results = {label: classify(left.get(label), right[label], args.term, args.scale)
               for label in sorted(right)}
    args.out.mkdir(parents=True)
    (args.out / "sensitivity.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = [f"# Beam 权重敏感度：{args.term} × {args.scale:g}", ""]
    for label, result in results.items():
        lines.append(f"- {label}：{result['classification']}"
                     + (f"；{result['reason']}" if result.get("reason") else ""))
    (args.out / "sensitivity.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines[2:]))
    return 1 if any(result["classification"] in ("变差", "不可比较")
                    for result in results.values()) else 0


if __name__ == "__main__":
    raise SystemExit(main())
