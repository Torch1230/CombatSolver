"""Aggregation checks for evaluate.py.

The A/B runner has no network or game dependency in its summation step, so the aggregation is
tested directly with synthetic, already-collected run entries. Failed runs must degrade to
"not usable" instead of raising, and the report must stay JSON-serializable.
"""
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parent))
import evaluate  # noqa: E402


def quality(won=True, loss=10, potions=0, deficit=0, turn=5):
    return {"won": won, "survives": True, "deathSaveUseCount": 0, "outstandingStolenResource": 0,
            "strategicHpDeficit": deficit, "combatEndedTurn": turn,
            "growthHpCredit": 0, "growthRewardCount": 0,
            "projectedBattlePotionCount": potions, "projectedBattleHpLost": loss}


def entry(label, variant, wall, members=2000, skipped=0, quality_value=None, ok=True):
    return {"label": label, "variant": variant, "index": 0, "exitCode": 0, "ok": ok,
            "processWallSeconds": wall, "wallSeconds": wall, "quality": quality_value,
            "membersRan": 4 - skipped, "membersSkippedLearned": skipped,
            "memberMilliseconds": members, "memberExpanded": 1000}


class SummarizeTests(unittest.TestCase):
    def test_better_keeps_quality_ordering(self):
        # The comparator mirrors the production interim ordering: strategic deficit and end turn,
        # not the raw projected HP loss.
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality(deficit=12)),
                   entry("a", evaluate.VARIANT_B, 20.0, quality_value=quality(deficit=10))]
        report = evaluate.summarize(results, ["a"])
        self.assertEqual((report["better"], report["equal"], report["worse"]), (1, 0, 0))
        self.assertEqual(report["usableBattles"], 1)
        self.assertAlmostEqual(report["totalWallSecondsA"], 30.0)
        self.assertAlmostEqual(report["totalWallSecondsB"], 20.0)

    def test_end_turn_and_potions_decide_when_deficit_ties(self):
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality(turn=5)),
                   entry("a", evaluate.VARIANT_B, 30.0, quality_value=quality(turn=4))]
        self.assertEqual(evaluate.summarize(results, ["a"])["better"], 1)
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_B, 30.0, quality_value=quality(potions=1))]
        report = evaluate.summarize(results, ["a"])
        self.assertEqual(report["worse"], 1)
        self.assertEqual(report["better"], 0)

    def test_failed_runs_are_not_usable(self):
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_B, 0.5, quality_value=None, ok=False)]
        report = evaluate.summarize(results, ["a"])
        self.assertEqual(report["usableBattles"], 0)
        self.assertIsNone(report["perBattle"][0]["qualityDelta"])
        self.assertIsNone(report["perBattle"][0]["timeDeltaSeconds"])

    def test_learned_skip_is_counted(self):
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_B, 25.0, skipped=2, quality_value=quality())]
        report = evaluate.summarize(results, ["a"])
        self.assertEqual(report["battlesWithLearnedSkip"], 1)
        self.assertEqual(report["equal"], 1)

    def test_report_is_json_serializable(self):
        results = [entry("a", evaluate.VARIANT_A, 30.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_B, 30.0, quality_value=quality()),
                   entry("b", evaluate.VARIANT_A, 1.0, quality_value=None, ok=False),
                   entry("b", evaluate.VARIANT_B, 1.0, quality_value=None, ok=False)]
        report = evaluate.summarize(results, ["a", "b"])
        json.dumps(report | {"options": {"harness": "tools/x.dll"}})
        self.assertEqual(report["battles"], 2)
        self.assertEqual(report["usableBattles"], 1)

    def test_median_over_repeated_samples(self):
        results = [entry("a", evaluate.VARIANT_A, 10.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_A, 20.0, quality_value=quality()),
                   entry("a", evaluate.VARIANT_B, 5.0, quality_value=quality())]
        report = evaluate.summarize(results, ["a"])
        self.assertAlmostEqual(report["totalWallSecondsA"], 15.0)
        self.assertAlmostEqual(report["totalWallSecondsB"], 5.0)
        self.assertEqual(report["perBattle"][0][evaluate.VARIANT_A]["samples"], 2)


if __name__ == "__main__":
    unittest.main()
