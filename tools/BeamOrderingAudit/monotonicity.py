#!/usr/bin/env python3
"""Demo 0: measure beam-search monotonicity and portfolio cost/benefit from recorded runs.

Cost: zero. This reads `solverMetrics.PortfolioMembers` out of existing harness results; it does
not run the solver and does not change any production code.

Why a fixed composition. `BeamWidthPortfolioGate` admits members per battle, so runs differ in
which members even ran: some battles fall back to the baseline alone. Comparing subsets across
that mix would compare different battle sets. This script therefore groups battles by the set of
members that actually ran and reports each group separately, so every subset in a table is
evaluated on identical battles.

Two questions per group:

1. Monotonicity. Members are independent searches from one root that differ only in the mid-search
   ordering (wider beam, second-rank band, or base-score-only). If more search were monotone, a
   wider member could never return a strictly worse route than a narrower one. This counts how
   often that happens, and how the winning member is distributed across widths.

2. Cost/benefit. Every subset of member kinds is a shippable portfolio and the portfolio keeps the
   best comparable member, so recorded per-member quality is reusable. Each subset reports seconds
   saved per battle against quality cost per battle.

Quality proxy: member telemetry records Won, BattleHpLost and PotionCount, but not the strategic
HP deficit, growth credit or combat end turn that SolverInterimResultOrdering.ComparePrimaryQuality
orders on. The proxy is (won desc, BattleHpLost + 9 x PotionCount asc), matching the convention in
tools/PortfolioSelector/member_value.py. Differences below one HP are invisible to it, so a strict
inequality here is a lower bound on the real disagreement, and a tie here is not proof of equal
routes.
"""
import argparse
import itertools
import json
from collections import defaultdict
from pathlib import Path

POTION_HP = 9


def member_kind(member):
    """Member identity by ordering shape, not by width: two members can share a width."""
    if member.get("BaseScoreOnly"):
        return "base"
    if member.get("SecondRankBand"):
        return "band"
    return f"w={member['BeamWidth']}"


def width_ratio(member, baseline_width):
    return round(member["BeamWidth"] / baseline_width, 4)


def quality(member):
    """Proxy for the production ordering; None when the member produced no outcome at all."""
    if not member.get("Ran") or not member.get("Terminal") or member.get("BattleHpLost") is None:
        return None
    return (0 if member.get("Won") else 1,
            member["BattleHpLost"] + POTION_HP * (member.get("PotionCount") or 0))


def load_groups(runs):
    """Group battles by composition: the ordered set of member kinds that actually ran."""
    groups = defaultdict(list)
    for path in sorted(Path(runs).glob("*/harness-result.json")):
        payload = json.loads(path.read_text())
        members = (payload.get("solverMetrics") or {}).get("PortfolioMembers") or []
        ran = [m for m in members if m.get("Ran")]
        usable = [m for m in ran if quality(m) is not None]
        groups["+".join(sorted({member_kind(m) for m in ran}))].append((path, ran, usable))
    return groups


def summarize_monotonicity(entries, baseline_width):
    """Width-ladder inversions and per-width solo strength, on one fixed battle set."""
    inversion_pairs = 0
    comparable_pairs = 0
    battles_with_inversion = 0
    ladder_wins = defaultdict(int)
    width_counts = defaultdict(int)
    width_best_counts = defaultdict(int)
    for _, ran, usable in entries:
        best = min(usable, key=quality)
        width_best_counts[member_kind(best)] += 1
        for member in usable:
            width_counts[member_kind(member)] += 1
        # The width ladder holds the ordering shape fixed and varies only the width resource.
        ladder = sorted((m for m in usable
                         if not m.get("SecondRankBand") and not m.get("BaseScoreOnly")),
                        key=lambda m: m["BeamWidth"])
        if len(ladder) >= 2:
            best_ladder = min(ladder, key=quality)
            ladder_wins[member_kind(best_ladder)] += 1
        inverted = False
        for narrow, wide in itertools.combinations(ladder, 2):
            comparable_pairs += 1
            if quality(wide) > quality(narrow):
                inversion_pairs += 1
                inverted = True
        if inverted:
            battles_with_inversion += 1
    return {
        "battles": len(entries),
        "baselineWidth": baseline_width,
        "widthPairsCompared": comparable_pairs,
        "widthInversions": inversion_pairs,
        "inversionPairRate": round(inversion_pairs / comparable_pairs, 4) if comparable_pairs else None,
        "battlesWithInversion": battles_with_inversion,
        "battleInversionRate": round(battles_with_inversion / len(entries), 4) if entries else None,
        "memberRunsPerKind": dict(sorted(width_counts.items())),
        "bestOverallMemberKind": dict(sorted(width_best_counts.items(), key=lambda kv: -kv[1])),
        "bestWidthMemberKind": dict(sorted(ladder_wins.items(), key=lambda kv: -kv[1])),
    }


def summarize_knobs(entries):
    """Which knob produced the winning member: the width, or only the ordering?

    Member 0 is the request's own baseline. `band` and `base` share the baseline's width and
    change only the mid-search ordering, so they isolate ordering leverage at fixed width.
    """
    counts = defaultdict(int)
    gain = defaultdict(lambda: {"battles": 0, "deficit": 0, "worst": 0})
    for _, ran, usable in entries:
        if not ran:
            continue
        baseline = ran[0]
        if quality(baseline) is None:
            continue
        winner = min(usable, key=quality)
        kind = member_kind(winner)
        if winner is baseline:
            knob = "baseline"
        elif kind in ("band", "base"):
            knob = "ordering"
        else:
            knob = "width"
        counts[knob] += 1
        improvement = quality(baseline)[1] - quality(winner)[1]
        if quality(winner)[0] != quality(baseline)[0]:
            improvement = 0 if improvement else 1
        if improvement > 0:
            entry = gain[knob]
            entry["battles"] += 1
            entry["deficit"] += improvement
            entry["worst"] = max(entry["worst"], improvement)
    total = sum(counts.values())
    return {
        "battles": total,
        "winnerKnob": dict(sorted(counts.items(), key=lambda kv: -kv[1])),
        "winnerKnobRate": {k: round(v / total, 4) for k, v in counts.items()} if total else {},
        "improvementOverBaseline": {
            knob: {**entry,
                   "deficitPerBattle": round(entry["deficit"] / total, 3) if total else None}
            for knob, entry in gain.items()},
    }


def summarize_subsets(entries):
    """Every shippable subset on one fixed battle set: seconds saved against quality cost."""
    kinds = sorted({member_kind(m) for _, _, usable in entries for m in usable})
    report = {}
    for size in range(1, len(kinds) + 1):
        for subset in itertools.combinations(kinds, size):
            keep = set(subset)
            entry = {"battles": 0, "battlesWithCost": 0, "deficit": 0, "worst": 0,
                     "savedSeconds": 0.0, "keptSeconds": 0.0}
            for _, _, usable in entries:
                by_kind = {member_kind(m): m for m in usable}
                kept = [by_kind[k] for k in keep if k in by_kind]
                if not kept:
                    continue
                actual = min(usable, key=quality)
                entry["battles"] += 1
                entry["savedSeconds"] += sum(
                    (m.get("ElapsedMilliseconds") or 0) / 1000
                    for m in usable if member_kind(m) not in keep)
                entry["keptSeconds"] += sum(
                    (m.get("ElapsedMilliseconds") or 0) / 1000 for m in kept)
                best = min(kept, key=quality)
                cost = 1 if quality(best)[0] != quality(actual)[0] else max(
                    0, quality(best)[1] - quality(actual)[1])
                if cost > 0:
                    entry["battlesWithCost"] += 1
                    entry["deficit"] += cost
                    entry["worst"] = max(entry["worst"], cost)
            if not entry["battles"]:
                continue
            report["+".join(subset)] = {
                "battles": entry["battles"],
                "savedSecondsPerBattle": round(entry["savedSeconds"] / entry["battles"], 2),
                "keptSecondsPerBattle": round(entry["keptSeconds"] / entry["battles"], 2),
                "battlesWithCost": entry["battlesWithCost"],
                "battlesWithCostRate": round(entry["battlesWithCost"] / entry["battles"], 4),
                "deficitPerBattle": round(entry["deficit"] / entry["battles"], 3),
                "worstBattleDeficit": entry["worst"],
            }
    return report


def frontier(report):
    """Subsets no other subset beats on both saved seconds and quality cost."""
    entries = list(report.items())
    result = []
    for name, entry in entries:
        dominated = any(
            other["savedSecondsPerBattle"] >= entry["savedSecondsPerBattle"]
            and other["deficitPerBattle"] <= entry["deficitPerBattle"]
            and (other["savedSecondsPerBattle"] > entry["savedSecondsPerBattle"]
                 or other["deficitPerBattle"] < entry["deficitPerBattle"])
            for other_name, other in entries if other_name != name)
        if not dominated:
            result.append((name, entry))
    return dict(sorted(result, key=lambda kv: kv[1]["savedSecondsPerBattle"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", required=True, type=Path)
    parser.add_argument("--min-battles", type=int, default=20,
                        help="skip composition groups smaller than this")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    report = {"qualityProxy": f"won desc, BattleHpLost + {POTION_HP} x PotionCount asc",
              "compositionGroups": {}}
    for composition, entries in sorted(load_groups(args.runs).items(),
                                       key=lambda kv: -len(kv[1])):
        if len(entries) < args.min_battles:
            continue
        widths = [m["BeamWidth"] for _, ran, _ in entries for m in ran
                  if not m.get("SecondRankBand") and not m.get("BaseScoreOnly")]
        baseline_width = sorted(widths)[len(widths) // 2] if widths else None
        subsets = summarize_subsets(entries)
        report["compositionGroups"][composition] = {
            "monotonicity": summarize_monotonicity(entries, baseline_width),
            "knobs": summarize_knobs(entries),
            "subsets": subsets,
            "paretoFrontier": frontier(subsets),
        }
    if args.out:
        args.out.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
