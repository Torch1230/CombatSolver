#!/usr/bin/env python3
"""Counterfactual value of each portfolio member, from recorded per-member telemetry.

For every battle, the selected route is the best single-member result (the portfolio keeps the
best comparable member). Dropping one member therefore changes the final route exactly when that
member was the winner. This reports, per member kind, what its time buys: win rate, share of
battles it wins, and its HP-equivalent distance to the actual winner when it does not win.

HP equivalent follows the project's own convention in the width-portfolio reports:
loss + 9 x potions, with potions only counted when the member reached a terminal result.
"""
import argparse
import collections
import json
from pathlib import Path

POTION_HP = 9


def member_kind(member):
    if member.get("BaseScoreOnly"):
        return "base"
    if member.get("SecondRankBand"):
        return "band"
    if member.get("BeamWidth") and not member.get("Ran"):
        return "skipped"
    return f"w={member['BeamWidth']}"


def hp_equivalent(member):
    if not member.get("Terminal") or member.get("BattleHpLost") is None:
        return None
    return member["BattleHpLost"] + POTION_HP * (member.get("PotionCount") or 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", required=True, type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    stats = collections.defaultdict(lambda: {"ran": 0, "terminal": 0, "won": 0, "selected": 0,
                                            "milliseconds": 0.0, "deficit": 0, "deficitSamples": 0,
                                            "worse": 0, "better": 0, "equal": 0})
    battles = 0
    for result_path in sorted(args.runs.glob("*/harness-result.json")):
        payload = json.loads(result_path.read_text())
        members = (payload.get("solverMetrics") or {}).get("PortfolioMembers") or []
        if not members:
            continue
        battles += 1
        winner = next((m for m in members if m.get("Selected")), None)
        winner_hp = hp_equivalent(winner) if winner else None
        winner_won = bool(winner and winner.get("Won"))
        for member in members:
            if not member.get("Ran"):
                continue
            kind = member_kind(member)
            entry = stats[kind]
            entry["ran"] += 1
            entry["milliseconds"] += member.get("ElapsedMilliseconds") or 0
            if member.get("Terminal"):
                entry["terminal"] += 1
            if member.get("Won"):
                entry["won"] += 1
            if member.get("Selected"):
                entry["selected"] += 1
            value = hp_equivalent(member)
            if winner_hp is None or value is None:
                continue
            if bool(member.get("Won")) != winner_won:
                # A different outcome class than the final route is a qualitative difference.
                entry["worse" if winner_won else "better"] += 1
                continue
            entry["deficit"] += max(0, value - winner_hp)
            entry["deficitSamples"] += 1
            if value < winner_hp:
                entry["better"] += 1
            elif value > winner_hp:
                entry["worse"] += 1
            else:
                entry["equal"] += 1

    report = {"battles": battles, "potionHpEquivalent": POTION_HP, "members": {}}
    for kind, entry in sorted(stats.items(), key=lambda kv: -kv[1]["milliseconds"]):
        report["members"][kind] = {
            **entry,
            "seconds": round(entry["milliseconds"] / 1000, 1),
            "secondsPerBattle": round(entry["milliseconds"] / 1000 / battles, 2) if battles else None,
            "winsPerSecond": round(entry["selected"] / (entry["milliseconds"] / 1000), 4)
            if entry["milliseconds"] else None,
            "meanDeficitVsWinner": round(entry["deficit"] / entry["deficitSamples"], 2)
            if entry["deficitSamples"] else None,
        }

    # Drop-one counterfactual: the portfolio keeps the best comparable member, so dropping a kind
    # replaces the final route with the best remaining member. Members run independent searches from
    # the same root, so recorded per-member quality is reusable; the shared node budget means a
    # dropped member would also free nodes for the others, which this cannot model.
    drop = collections.defaultdict(lambda: {"battlesWithCost": 0, "deficit": 0, "worst": 0,
                                           "seconds": 0.0})
    for result_path in sorted(args.runs.glob("*/harness-result.json")):
        payload = json.loads(result_path.read_text())
        members = [m for m in ((payload.get("solverMetrics") or {}).get("PortfolioMembers") or [])
                   if m.get("Ran") and hp_equivalent(m) is not None]
        if len(members) < 2:
            continue
        actual = min(members, key=lambda m: (0 if m.get("Won") else 1, hp_equivalent(m)))
        actual_hp, actual_won = hp_equivalent(actual), bool(actual.get("Won"))
        for member in members:
            kind = member_kind(member)
            entry = drop[kind]
            entry["seconds"] += (member.get("ElapsedMilliseconds") or 0) / 1000
            others = [m for m in members if member_kind(m) != kind]
            if not others:
                continue
            best = min(others, key=lambda m: (0 if m.get("Won") else 1, hp_equivalent(m)))
            best_won = bool(best.get("Won"))
            if best_won != actual_won:
                cost = 1
            else:
                cost = max(0, hp_equivalent(best) - actual_hp)
            if cost > 0:
                entry["battlesWithCost"] += 1
                entry["deficit"] += cost
                entry["worst"] = max(entry["worst"], cost)
    report["dropOneCost"] = {
        kind: {**entry,
               "seconds": round(entry["seconds"], 1),
               "secondsPerBattle": round(entry["seconds"] / battles, 2) if battles else None,
               "deficitPerBattle": round(entry["deficit"] / battles, 3) if battles else None}
        for kind, entry in sorted(drop.items(), key=lambda kv: kv[1]["seconds"])
    }
    if args.out:
        args.out.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
