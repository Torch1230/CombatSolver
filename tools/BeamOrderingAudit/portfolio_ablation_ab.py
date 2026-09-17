#!/usr/bin/env python3
"""Ablation A/B: compare two candidate search configurations on identical battles.

Two things this driver exists for:

1. The reconstruction in monotonicity.py reads every member's own recorded result out of one run of
   the full portfolio. That reconstruction is only exact if the shared node budget never binds, so
   this driver runs both arms for real, as separate processes, interleaved per battle.
2. Selecting the best of many subset shapes on the same battles overfits the selection. Running a
   short candidate list as real arms on a fixed battle set is the cheap check that a chosen shape
   is not just the luckiest row of a frontier.

Interleaving: battles alternate which arm runs first, so a slow drift in machine load cannot be
attributed to either arm. Each battle is one generated scenario.

Quality proxy: (won desc, BattleHpLost + 9 x PotionCount asc), because the per-member telemetry
does not carry the strategic deficit. Cost is reported in member milliseconds and in expanded
nodes. Nodes are the unbiased unit: process warmup inflates whichever member runs first, so any
"sum of member seconds" is not an additive cost, while node counts are.
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

# 默认对照：完整组合 对 去掉普通基线成员。
DEFAULT_ARMS = {ARM_CONTROL: [], ARM_ABLATION: ["--no-plain-baseline"]}


def parse_arms(specs):
    """--arm 名字=额外参数；可重复。不给就用默认的两臂。"""
    if not specs:
        return dict(DEFAULT_ARMS)
    arms = {}
    for spec in specs:
        name, _, extra = spec.partition("=")
        name = name.strip()
        if not name:
            raise SystemExit(f"--arm 缺少名字：{spec}")
        arms[name] = [token for token in extra.split() if token]
    if len(arms) < 2:
        raise SystemExit("至少需要两臂才能比较。")
    return arms


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
    label, arm, extra, request_path, out, harness, options, timeout = job
    output = out / f"{label}-{arm}"
    command = ["dotnet", str(harness), "--request", str(request_path), "--label", f"{label}-{arm}",
               "--out", str(output), "--profile", "Custom",
               "--beam", str(options["beam"]), "--nodes", str(options["nodes"]),
               "--budget-ms", str(options["budget_ms"]), "--dop", "1",
               "--search-mode", "Coordinator", "--use-portfolio", *extra]
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


def summarize(rows, arms, control):
    """每一条非对照臂都与对照臂在相同战斗上配对，质量用同一代理，成本同时报毫秒与节点。"""
    report = {}
    for arm in arms:
        if arm == control:
            continue
        pairs = []
        for label, by_arm in sorted(rows.items()):
            if control not in by_arm or arm not in by_arm:
                continue
            control_run, arm_run = by_arm[control], by_arm[arm]
            control_members = [m for m in control_run["ran"] if M.quality(m) is not None]
            arm_members = [m for m in arm_run["ran"] if M.quality(m) is not None]
            if not control_members or not arm_members:
                continue
            control_best = min(control_members, key=M.quality)
            arm_best = min(arm_members, key=M.quality)
            control_q, arm_q = M.quality(control_best), M.quality(arm_best)
            deficit = 1 if control_q[0] != arm_q[0] else max(0, arm_q[1] - control_q[1])
            pairs.append({
                "label": label,
                "deficit": deficit,
                "controlSeconds": control_run["memberMilliseconds"] / 1000,
                "armSeconds": arm_run["memberMilliseconds"] / 1000,
                "controlWall": control_run["wallSeconds"],
                "armWall": arm_run["wallSeconds"],
                "controlMembers": len(control_members),
                "armMembers": len(arm_members),
                "controlExpanded": control_run["expanded"],
                "armExpanded": arm_run["expanded"],
            })
        if not pairs:
            report[arm] = {"battles": 0}
            continue
        total = len(pairs)
        saved_nodes = sum(p["controlExpanded"] - p["armExpanded"] for p in pairs)
        saved_nodes_share = (saved_nodes / sum(p["controlExpanded"] for p in pairs)
                             if sum(p["controlExpanded"] for p in pairs) else None)
        report[arm] = {
            "battles": total,
            "qualityCostBattles": sum(1 for p in pairs if p["deficit"] > 0),
            "qualityCostTotal": sum(p["deficit"] for p in pairs),
            "qualityCostPerBattle": round(sum(p["deficit"] for p in pairs) / total, 3),
            "worstBattleDeficit": max(p["deficit"] for p in pairs),
            "memberSecondsSavedPerBattle": round(
                sum(p["controlSeconds"] - p["armSeconds"] for p in pairs) / total, 2),
            "wallSecondsSavedPerBattle": round(
                sum(p["controlWall"] - p["armWall"] for p in pairs) / total, 2),
            "controlWallPerBattle": round(sum(p["controlWall"] for p in pairs) / total, 2),
            "armWallPerBattle": round(sum(p["armWall"] for p in pairs) / total, 2),
            "controlMembersPerBattle": round(sum(p["controlMembers"] for p in pairs) / total, 2),
            "armMembersPerBattle": round(sum(p["armMembers"] for p in pairs) / total, 2),
            "expandedSavedPerBattle": round(saved_nodes / total, 1),
            "expandedSavedShare": round(saved_nodes_share, 4) if saved_nodes_share else None,
        }
    return report


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
    parser.add_argument("--arm", action="append", default=[],
                        help="名字=额外 CLI 参数；可重复，后给的 --beam 会覆盖全局值")
    parser.add_argument("--control", default=ARM_CONTROL)
    args = parser.parse_args()

    arms = parse_arms(args.arm)
    if args.control not in arms:
        raise SystemExit(f"--control {args.control} 不在臂列表 {sorted(arms)} 中。")
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    labels = target_labels(args.runs, args.composition)
    if args.limit:
        labels = labels[:args.limit]
    if not labels:
        raise SystemExit(f"没有战斗匹配组合 {args.composition}")

    options = {"repo": str(args.repo.resolve()), "beam": args.beam, "nodes": args.nodes,
               "budget_ms": args.budget_ms}
    names = list(arms)
    jobs = []
    # 交错：相邻战斗轮换臂的先后，机器负载漂移不会被算到某一臂头上。
    for index, label in enumerate(labels):
        order = names[index % len(names):] + names[:index % len(names)]
        for arm in order:
            jobs.append((label, arm, arms[arm], args.requests / f"{label}.json", out,
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
        "arms": {name: " ".join(extra) or "(none)" for name, extra in arms.items()},
        "control": args.control,
        "labels": labels,
        "failed": [{"label": r["label"], "arm": r["arm"], "exitCode": r["exitCode"]}
                   for r in failed],
        "wallSeconds": round(time.monotonic() - started, 1),
        "comparison": summarize(rows, names, args.control),
    }
    (out / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
