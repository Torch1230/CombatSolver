#!/usr/bin/env python3
"""Ablation A/B: does dropping the plain baseline portfolio member pay for itself?

The reconstruction in monotonicity.py reads every member's own recorded result out of one run of
the full portfolio. That reconstruction is only exact if the shared node budget never binds, so
this driver runs both arms for real, as separate processes, interleaved per battle.

Arms:
  A (control)  --use-portfolio                      five members: baseline, narrow, wide, band, base
  B (ablation) --use-portfolio --no-plain-baseline  four members: narrow, wide, band, base

Interleaving: battles alternate which arm runs first, so a slow drift in machine load cannot be
attributed to either arm. Each battle is one generated scenario, and the quality proxy is the same
one monotonicity.py uses (won desc, BattleHpLost + 9 x PotionCount asc) because the per-member
telemetry does not carry the strategic deficit.
"""
import argparse
import concurrent.futures
import json
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import monotonicity as M  # noqa: E402

ARM_CONTROL = "A"
ARM_ABLATION = "B"


def arm_arguments(arm):
    return ["--no-plain-baseline"] if arm == ARM_ABLATION else []


def target_labels(runs, composition):
    """Battles whose recorded composition matches the group we are ablating within."""
    labels = []
    for result_path in sorted(Path(runs).glob("*/harness-result.json")):
        payload = json.loads(result_path.read_text())
        members = (payload.get("solverMetrics") or {}).get("PortfolioMembers") or []
        ran = [m for m in members if m.get("Ran")]
        if "+".join(sorted({M.member_kind(m) for m in ran})) == composition:
            labels.append(payload.get("label") or result_path.parent.name)
    return labels


def run_one(job):
    label, arm, request_path, out, harness, options, timeout = job
    output = out / f"{label}-{arm}"
    command = ["dotnet", str(harness), "--request", str(request_path), "--label", f"{label}-{arm}",
               "--out", str(output), "--profile", "Custom",
               "--beam", str(options["beam"]), "--nodes", str(options["nodes"]),
               "--budget-ms", str(options["budget_ms"]), "--dop", "1",
               "--search-mode", "Coordinator", "--use-portfolio", *arm_arguments(arm)]
    started = time.monotonic()
    try:
        completed = subprocess.run(["timeout", "--signal=KILL", str(timeout), *command],
                                   cwd=options["repo"], capture_output=True, text=True,
                                   timeout=timeout + 30)
        code = completed.returncode
    except subprocess.TimeoutExpired:
        code = "runner-timeout"
    wall = time.monotonic() - started
    return {"label": label, "arm": arm, "exitCode": code,
            "processWallSeconds": round(wall, 2), "output": str(output)}


def load_run(path):
    """Reads one finished harness run into the fields both arms are compared on."""
    result = json.loads((path / "harness-result.json").read_text())
    members = (result.get("solverMetrics") or {}).get("PortfolioMembers") or []
    ran = [m for m in members if m.get("Ran")]
    quality = None
    quality_path = path / "quality.json"
    if quality_path.exists():
        quality = json.loads(quality_path.read_text()).get("quality")
    return {"members": members, "ran": ran, "quality": quality,
            "wallSeconds": result.get("wallSeconds"),
            "expanded": result.get("pruneCounters", {}).get("totalExpanded"),
            "memberMilliseconds": sum((m.get("ElapsedMilliseconds") or 0) for m in ran),
            "selected": next((m for m in ran if m.get("Selected")), None)}


def summarize(rows):
    """Paired comparison on identical battles: quality proxy, member seconds and wall clock."""
    pairs = []
    for label, arms in sorted(rows.items()):
        if ARM_CONTROL not in arms or ARM_ABLATION not in arms:
            continue
        control, ablation = arms[ARM_CONTROL], arms[ARM_ABLATION]
        control_members = [m for m in control["ran"] if M.quality(m) is not None]
        ablation_members = [m for m in ablation["ran"] if M.quality(m) is not None]
        if not control_members or not ablation_members:
            continue
        control_best = min(control_members, key=M.quality)
        ablation_best = min(ablation_members, key=M.quality)
        control_q, ablation_q = M.quality(control_best), M.quality(ablation_best)
        deficit = 1 if control_q[0] != ablation_q[0] else max(0, ablation_q[1] - control_q[1])
        pairs.append({
            "label": label,
            "deficit": deficit,
            "controlSeconds": control["memberMilliseconds"] / 1000,
            "ablationSeconds": ablation["memberMilliseconds"] / 1000,
            "controlWall": control["wallSeconds"],
            "ablationWall": ablation["wallSeconds"],
            "controlMembers": len(control_members),
            "ablationMembers": len(ablation_members),
            "controlExpanded": control["expanded"],
            "ablationExpanded": ablation["expanded"],
        })
    if not pairs:
        return {"battles": 0}
    total = len(pairs)
    return {
        "battles": total,
        "qualityCostBattles": sum(1 for p in pairs if p["deficit"] > 0),
        "qualityCostTotal": sum(p["deficit"] for p in pairs),
        "qualityCostPerBattle": round(sum(p["deficit"] for p in pairs) / total, 3),
        "worstBattleDeficit": max(p["deficit"] for p in pairs),
        "memberSecondsSavedPerBattle": round(
            sum(p["controlSeconds"] - p["ablationSeconds"] for p in pairs) / total, 2),
        "wallSecondsSavedPerBattle": round(
            sum(p["controlWall"] - p["ablationWall"] for p in pairs) / total, 2),
        "controlWallPerBattle": round(sum(p["controlWall"] for p in pairs) / total, 2),
        "ablationWallPerBattle": round(sum(p["ablationWall"] for p in pairs) / total, 2),
        "controlMembersPerBattle": round(sum(p["controlMembers"] for p in pairs) / total, 2),
        "ablationMembersPerBattle": round(sum(p["ablationMembers"] for p in pairs) / total, 2),
        "controlExpandedPerBattle": round(sum(p["controlExpanded"] for p in pairs) / total, 1),
        "ablationExpandedPerBattle": round(sum(p["ablationExpanded"] for p in pairs) / total, 1),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--harness", required=True, type=Path)
    parser.add_argument("--requests", required=True, type=Path)
    parser.add_argument("--runs", required=True, type=Path,
                        help="existing runs, used only to pick the battles of one composition")
    parser.add_argument("--composition", required=True)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--beam", type=int, default=24)
    parser.add_argument("--nodes", type=int, default=120000)
    parser.add_argument("--budget-ms", type=int, default=60000)
    parser.add_argument("--timeout", type=int, default=420)
    parser.add_argument("--limit", type=int)
    args = parser.parse_args()

    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    labels = target_labels(args.runs, args.composition)
    if args.limit:
        labels = labels[:args.limit]
    if not labels:
        raise SystemExit(f"没有战斗匹配组合 {args.composition}")

    options = {"repo": str(args.repo.resolve()), "beam": args.beam, "nodes": args.nodes,
               "budget_ms": args.budget_ms}
    jobs = []
    # 交错：相邻战斗交换两臂先后，机器负载漂移不会被算到某一臂头上。
    for index, label in enumerate(labels):
        order = [ARM_CONTROL, ARM_ABLATION] if index % 2 == 0 else [ARM_ABLATION, ARM_CONTROL]
        for arm in order:
            jobs.append((label, arm, args.requests / f"{label}.json", out,
                         args.harness.resolve(), options, args.timeout))

    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        results = list(pool.map(run_one, jobs))
    failed = [r for r in results if r["exitCode"] != 0]

    rows = defaultdict(dict)
    for result in results:
        if result["exitCode"] != 0:
            continue
        path = Path(result["output"])
        if (path / "harness-result.json").exists():
            rows[result["label"]][result["arm"]] = load_run(path)
    report = {
        "composition": args.composition,
        "labels": labels,
        "failed": [{"label": r["label"], "arm": r["arm"], "exitCode": r["exitCode"]}
                   for r in failed],
        "wallSeconds": round(time.monotonic() - started, 1),
        "comparison": summarize(rows),
    }
    (out / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
