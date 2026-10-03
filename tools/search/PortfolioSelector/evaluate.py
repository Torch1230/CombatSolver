#!/usr/bin/env python3
"""Compare the always-run width portfolio against the learned gate on held-out battles.

Each battle is measured in fresh harness processes, alternating ABBA/BAAB by battle so machine
drift affects both variants. A = portfolio always runs its refinement members; B = the learned
model may skip members the existing gate already allowed.

Decision quality is compared with the production ordering (victory, survival, death saves,
strategic deficit, end turn, potions), not with any ranking score. Time is the harness request
wall clock; per-member milliseconds come from the portfolio telemetry.
"""
import argparse
import concurrent.futures
import json
import statistics
import subprocess
from pathlib import Path
import time

VARIANT_A = "always-run"
VARIANT_B = "learned-gate"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def run_variant(job):
    label, variant, index, model, out, runs, options, timeout = job
    output = out / f"{variant}-{index}" / label
    request = Path(options["requests"]) / f"{label}.json"
    command = ["dotnet", str(options["harness"]), "--request", str(request), "--label", label,
               "--out", str(output), "--profile", "Custom",
               "--beam", str(options["beam"]), "--nodes", str(options["nodes"]),
               "--budget-ms", str(options["budget_ms"]), "--dop", "1",
               "--search-mode", "Coordinator", "--use-portfolio"]
    if model is not None:
        command += ["--portfolio-model", str(model)]
    started = time.monotonic()
    try:
        completed = subprocess.run(["timeout", "--signal=KILL", str(timeout), *command],
                                   cwd=options["repo"], capture_output=True, text=True,
                                   timeout=timeout + 30)
        code = completed.returncode
    except subprocess.TimeoutExpired:
        code = "runner-timeout"
    wall = round(time.monotonic() - started, 2)
    result = output / "harness-result.json"
    quality = output / "quality.json"
    entry = {"label": label, "variant": variant, "index": index, "exitCode": code, "ok": False,
             "processWallSeconds": wall, "wallSeconds": None, "quality": None,
             "membersRan": None, "membersSkippedLearned": None, "memberMilliseconds": None,
             "memberExpanded": None}
    if not (result.exists() and quality.exists()):
        return entry
    payload = json.loads(result.read_text())
    metrics = payload.get("solverMetrics") or {}
    members = metrics.get("PortfolioMembers") or []
    return entry | {
        "ok": True,
        "wallSeconds": payload.get("wallSeconds"),
        "quality": json.loads(quality.read_text())["quality"],
        "membersRan": sum(1 for member in members if member.get("Ran")),
        "membersSkippedLearned": sum(
            1 for member in members
            if not member.get("Ran") and member.get("SkippedReason") == "LearnedNoImprovement"),
        "memberMilliseconds": sum(member.get("ElapsedMilliseconds") or 0 for member in members),
        "memberExpanded": sum(member.get("ExpandedNodes") or 0 for member in members),
    }


def compare_quality(b, a):
    """Production ordering: returns <0 when b is better than a, 0 when equivalent."""
    for key in ("won", "survives"):
        if bool(b[key]) != bool(a[key]):
            return -1 if b[key] else 1
    if b["deathSaveUseCount"] != a["deathSaveUseCount"]:
        return b["deathSaveUseCount"] - a["deathSaveUseCount"]
    if b["outstandingStolenResource"] != a["outstandingStolenResource"]:
        return b["outstandingStolenResource"] - a["outstandingStolenResource"]
    for key in ("strategicHpDeficit", "combatEndedTurn"):
        if b[key] != a[key]:
            return b[key] - a[key]
    if b["growthHpCredit"] != a["growthHpCredit"]:
        return a["growthHpCredit"] - b["growthHpCredit"]
    if b["growthRewardCount"] != a["growthRewardCount"]:
        return a["growthRewardCount"] - b["growthRewardCount"]
    if b["projectedBattlePotionCount"] != a["projectedBattlePotionCount"]:
        return b["projectedBattlePotionCount"] - a["projectedBattlePotionCount"]
    return 0


def median(values):
    return statistics.median(values) if values else None


def summarize(results, labels):
    """Pure aggregation over variant runs; the timeout/failure entries carry null fields."""
    per_battle = []
    for label in labels:
        entry = {"label": label}
        for variant in (VARIANT_A, VARIANT_B):
            samples = [r for r in results
                       if r["label"] == label and r["variant"] == variant and r["ok"]]
            entry[variant] = {
                "samples": len(samples),
                "wallSeconds": median([s["wallSeconds"] for s in samples]),
                "memberMilliseconds": median([s["memberMilliseconds"] for s in samples]),
                "membersRan": median([s["membersRan"] for s in samples]),
                "skippedLearned": median([s["membersSkippedLearned"] for s in samples]),
                "quality": samples[0]["quality"] if samples else None,
            }
        a, b = entry[VARIANT_A]["quality"], entry[VARIANT_B]["quality"]
        entry["qualityDelta"] = None if a is None or b is None else compare_quality(b, a)
        entry["timeDeltaSeconds"] = (
            None if entry[VARIANT_A]["wallSeconds"] is None or entry[VARIANT_B]["wallSeconds"] is None
            else round(entry[VARIANT_A]["wallSeconds"] - entry[VARIANT_B]["wallSeconds"], 3))
        per_battle.append(entry)
    usable = [entry for entry in per_battle if entry["qualityDelta"] is not None]
    return {
        "battles": len(labels),
        "usableBattles": len(usable),
        "better": sum(1 for entry in usable if entry["qualityDelta"] < 0),
        "equal": sum(1 for entry in usable if entry["qualityDelta"] == 0),
        "worse": sum(1 for entry in usable if entry["qualityDelta"] > 0),
        "qualityDeltas": [entry["qualityDelta"] for entry in usable],
        "totalWallSecondsA": round(sum(entry[VARIANT_A]["wallSeconds"] or 0 for entry in usable), 2),
        "totalWallSecondsB": round(sum(entry[VARIANT_B]["wallSeconds"] or 0 for entry in usable), 2),
        "totalMemberSecondsA": round(sum(entry[VARIANT_A]["memberMilliseconds"] or 0
                                          for entry in usable) / 1000, 2),
        "totalMemberSecondsB": round(sum(entry[VARIANT_B]["memberMilliseconds"] or 0
                                          for entry in usable) / 1000, 2),
        "battlesWithLearnedSkip": sum(1 for entry in usable
                                      if (entry[VARIANT_B]["skippedLearned"] or 0) > 0),
        "perBattle": per_battle,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--requests", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--harness", required=True, type=Path)
    parser.add_argument("--split", default="test")
    parser.add_argument("--repeat", type=int, default=2)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--beam", type=int, default=24)
    parser.add_argument("--nodes", type=int, default=20000)
    parser.add_argument("--budget-ms", type=int, default=20000)
    parser.add_argument("--timeout", type=int, default=240)
    args = parser.parse_args()

    out = args.out.resolve()
    if out.exists() and any(out.iterdir()):
        raise SystemExit(f"{out} is not empty; use a fresh --out")
    manifest = json.loads(args.manifest.read_text())["items"]
    labels = [item["label"] for item in manifest if item["split"] == args.split]
    if not labels:
        raise SystemExit(f"no battles in split {args.split}")
    options = {"repo": str(args.repo.resolve()), "harness": str(args.harness.resolve()),
               "requests": str(args.requests.resolve()), "beam": args.beam, "nodes": args.nodes,
               "budget_ms": args.budget_ms}
    model = args.model.resolve()

    jobs = []
    for index, label in enumerate(labels):
        order = [VARIANT_A, VARIANT_B] if index % 2 == 0 else [VARIANT_B, VARIANT_A]
        order = order + list(reversed(order))
        for run_index, variant in enumerate(order):
            jobs.append((label, variant, run_index, model if variant == VARIANT_B else None,
                         out, None, options, args.timeout))
    if args.repeat == 1:
        jobs = [job for job in jobs if job[2] < 2]

    started = time.monotonic()
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        for result in pool.map(run_variant, jobs):
            results.append(result)
            print(json.dumps({key: result[key] for key in
                              ("label", "variant", "index", "ok", "wallSeconds", "exitCode")
                              if key in result}), flush=True)

    summary = summarize(results, labels) | {
        "split": args.split,
        "model": str(model),
        "options": options | {"repeat": args.repeat, "workers": args.workers},
        "wallSeconds": round(time.monotonic() - started, 1),
    }
    write_json(out / "comparison.json", summary)
    print(json.dumps({key: summary[key] for key in
                      ("battles", "usableBattles", "better", "equal", "worse",
                       "totalWallSecondsA", "totalWallSecondsB", "totalMemberSecondsA",
                       "totalMemberSecondsB", "battlesWithLearnedSkip", "wallSeconds")},
                     ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
