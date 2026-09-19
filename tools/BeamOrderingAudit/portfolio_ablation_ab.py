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


def has_valid_result(path):
    """跑完的结果和跑到一半失败的结果同名同位置，只能看内容区分。

    这条路来自一次真实事故：跑批量 A/B 的过程中 /tmp（tmpfs）的 inode 被占满，
    Harmony 打补丁时写临时文件失败，于是**每一场都在同一个地方抛
    `Win32Exception: No space left on device`**。失败时 harness 照样写
    `harness-result.json`，只是里面只有 `error` 与 `steps`、没有 `solverMetrics`。
    只看文件存在会让 `--resume` 认为这些战斗已经跑过，于是无声地丢掉大部分样本。
    """
    result_path = path / "harness-result.json"
    if not result_path.exists():
        return False
    try:
        payload = json.loads(result_path.read_text())
    except (OSError, json.JSONDecodeError):
        return False
    return "solverMetrics" in payload


def load_run(path):
    """Reads one finished harness run into the fields both arms are compared on."""
    result = json.loads((path / "harness-result.json").read_text())
    members = (result.get("solverMetrics") or {}).get("PortfolioMembers") or []
    ran = [m for m in members if m.get("Ran")]
    quality = None
    quality_path = path / "quality.json"
    if quality_path.exists():
        quality = json.loads(quality_path.read_text()).get("quality")
    route_path = path / "route.json"
    route = canonical_route(json.loads(route_path.read_text())) if route_path.exists() else None
    return {"members": members, "ran": ran, "quality": quality, "route": route,
            "wallSeconds": result.get("wallSeconds"),
            "expanded": result.get("pruneCounters", {}).get("totalExpanded"),
            "memberMilliseconds": sum((m.get("ElapsedMilliseconds") or 0) for m in ran),
            "selected": next((m for m in ran if m.get("Selected")), None)}


# 路线里只留下会改变游戏状态的字段。标题、本地化名、展示用计数都是同一决策的装饰，
# 拿它们进比较会把"选择完全一致"误判成"选择不同"。
ROUTE_FIELDS = ("kind", "turn", "cardId", "cardOccurrence", "targetIndex", "targetCombatId",
                "choice", "nestedChoices", "nestedChoicesBeforePrimary", "potionSlot",
                "potionId", "replayCount", "cardStateKey", "cardStateOccurrence", "cardUpgradeLevel")


def canonical_route(actions):
    def freeze(value):
        return json.dumps(value, sort_keys=True, ensure_ascii=False) \
            if isinstance(value, (dict, list)) else value
    return [tuple(freeze(action.get(field)) for field in ROUTE_FIELDS) for action in actions]


def composition(run):
    """这一臂实际跑起来的成员身份（按排序形状）。"""
    return tuple(sorted(M.member_kind(m) for m in run["ran"]))


def work_signature(run):
    """组合里每个成员各自展开了多少节点——搜索状态集合的逐成员指纹。

    这个驱动原本就是为"绕过共享预算重建"写的：某一臂的实际工作量才是无偏成本。
    对"只改指纹数值、不改相等关系"的开关（--state-key-salt）来说，它还是更强的判据：
    相等关系没变，所以只要这个签名逐位相同，两条搜索轨迹就是同一个。
    """
    return sorted((m.get("BeamWidth"), m.get("ExpandedNodes"), m.get("TransitionCount"))
                  for m in run["ran"])



def declared_beam(extra, fallback):
    """臂自己声明的 --beam（后给的覆盖全局值），没声明就是全局值。"""
    beam = fallback
    for index, token in enumerate(extra):
        if token == "--beam" and index + 1 < len(extra):
            beam = int(extra[index + 1])
    return beam


def validate_arm_declarations(arms, fallback_beam):
    """有任何一个臂改写束宽时，其余臂必须也写明自己的束宽。

    这条规则来自一次真实错误：对照臂漏写 --beam，静默继承了全局的窄束宽，
    整轮"生产宽度标定"其实测的不是生产宽度，而报告表面看上去完全正常。
    只比对"声明值 vs 观测值"抓不到它——没声明的臂声明的就是它继承到的值，必然自洽。
    所以必须在开跑前就把这种歧义挡掉。
    """
    declares = {arm: [token for token in extra if token == "--beam"] for arm, extra in arms.items()}
    silent = sorted(arm for arm, tokens in declares.items() if not tokens)
    overriding = sorted(arm for arm, tokens in declares.items() if tokens)
    if overriding and silent:
        raise SystemExit(
            f"臂 {overriding} 改写了 --beam，但臂 {silent} 没有声明，会静默继承全局束宽 "
            f"{fallback_beam}。请给每个臂都写明 --beam，或在全局统一指定束宽。")


def audit_arm_configuration(rows, arms, fallback_beam):
    """每个臂实际跑出的成员宽度必须自证身份。

    这一条是因为真的犯过一次：对照臂忘了写 --beam，于是静默继承了全局的窄束宽，
    整轮"生产宽度标定"其实测的不是生产宽度，而结果表面上看仍然完整。
    这里把观测到的宽度写进报告，并在臂声明的束宽没出现在观测里时直接失败。
    """
    observed = {}
    for by_arm in rows.values():
        for arm, run in by_arm.items():
            widths = {m["BeamWidth"] for m in run["ran"] if m.get("BeamWidth")}
            if widths:
                observed.setdefault(arm, set()).update(widths)
    audit = {}
    for arm, extra in arms.items():
        widths = sorted(observed.get(arm, set()))
        expected = declared_beam(extra, fallback_beam)
        audit[arm] = {"declaredBeam": expected, "observedWidths": widths}
        if widths and expected not in widths:
            raise SystemExit(
                f"臂 {arm} 声明束宽 {expected}，但实际观测到的成员宽度是 {widths}。"
                f"检查 --arm 的额外参数是否漏了 --beam。")
    return audit


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
                "sameWork": work_signature(control_run) == work_signature(arm_run),
                "sameComposition": composition(control_run) == composition(arm_run),
                "controlComposition": composition(control_run),
                "armComposition": composition(arm_run),
                "sameRoute": control_run["route"] == arm_run["route"],
                "sameQualityObject": control_run["quality"] == arm_run["quality"],
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
            "workIdenticalBattles": sum(1 for p in pairs if p["sameWork"]),
            "compositionChangedBattles": sum(1 for p in pairs if not p["sameComposition"]),
            "compositionChanges": [
                {"label": p["label"], "control": list(p["controlComposition"]),
                 "arm": list(p["armComposition"])}
                for p in pairs if not p["sameComposition"]],
            "routeIdenticalBattles": sum(1 for p in pairs if p["sameRoute"]),
            "qualityIdenticalBattles": sum(1 for p in pairs if p["sameQualityObject"]),
            "workDifferingBattles": [p["label"] for p in pairs if not p["sameWork"]],
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
    parser.add_argument("--resume", action="store_true",
                        help="跳过已有有效结果的任务（按内容判断，不看文件是否存在）")
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
    validate_arm_declarations(arms, args.beam)
    # 先把每个臂实际会用的束宽打出来：漏写 --beam 时它会静默继承全局值，看不到就会白跑一轮。
    for arm, extra in arms.items():
        token = "--beam" if "--beam" in extra else "--beam (继承全局)"
        print(f"臂 {arm}: 束宽={declared_beam(extra, args.beam)} 来自 {token}；额外参数={extra or '(无)'}",
              flush=True)
    jobs = []
    # 交错：相邻战斗轮换臂的先后，机器负载漂移不会被算到某一臂头上。
    for index, label in enumerate(labels):
        order = names[index % len(names):] + names[:index % len(names)]
        for arm in order:
            jobs.append((label, arm, arms[arm], args.requests / f"{label}.json", out,
                         args.harness.resolve(), options, args.timeout))

    started = time.monotonic()
    all_jobs = list(jobs)
    if args.resume:
        jobs = [job for job in jobs if not has_valid_result(out / f"{job[0]}-{job[1]}")]
        print(f"--resume：跳过 {len(all_jobs) - len(jobs)} 个已有有效结果的任务，"
              f"本次跑 {len(jobs)} 个。", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        results = list(pool.map(run_one, jobs))
    failed = [r for r in results if r["exitCode"] != 0]

    # 从磁盘读回，而不是只读本次跑出来的那些：`--resume` 跳过的那部分同样要进比较。
    rows = defaultdict(dict)
    for label, arm in {(job[0], job[1]) for job in all_jobs}:
        path = out / f"{label}-{arm}"
        if has_valid_result(path):
            rows[label][arm] = load_run(path)
    report = {
        "composition": args.composition,
        "arms": {name: " ".join(extra) or "(none)" for name, extra in arms.items()},
        "armConfiguration": audit_arm_configuration(rows, arms, args.beam),
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
