"""Aggregation checks for monotonicity.py.

The audit is a pure summation over already-recorded runs, so it is tested directly with
synthetic member telemetry. Two behaviours matter most and both have been wrong once:
"saved seconds" must count the dropped members only, and a deficit must be measured against
the full portfolio's own result, not against the member that happened to be selected.
"""
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parent))
import monotonicity  # noqa: E402


def member(width, loss, won=True, potions=0, ms=1000, terminal=True,
           band=False, base=False, ran=True):
    return {"BeamWidth": width, "SecondRankBand": band, "BaseScoreOnly": base, "Ran": ran,
            "Selected": False, "Terminal": terminal, "Won": won, "BattleHpLost": loss,
            "PotionCount": potions, "ElapsedMilliseconds": ms}


def entry(members, name="b"):
    ran = [m for m in members if m["Ran"]]
    return (Path(f"/runs/{name}/harness-result.json"), ran,
            [m for m in ran if monotonicity.quality(m) is not None])


class QualityTests(unittest.TestCase):
    def test_quality_orders_won_before_loss(self):
        worse_win = monotonicity.quality(member(24, 60))
        better_loss = monotonicity.quality(member(24, 0, won=False))
        # A win is never beaten by a lower HP loss.
        self.assertLess(worse_win, better_loss)

    def test_quality_charges_potions(self):
        self.assertEqual(monotonicity.quality(member(24, 10, potions=2))[1],
                         10 + 2 * monotonicity.POTION_HP)

    def test_nonterminal_member_has_no_quality(self):
        self.assertIsNone(monotonicity.quality(member(24, 10, terminal=False)))
        self.assertIsNone(monotonicity.quality(member(24, 10, ran=False)))


class MonotonicityTests(unittest.TestCase):
    def test_wider_that_is_worse_counts_as_inversion(self):
        entries = [entry([member(16, 10, ms=500), member(24, 10, ms=600), member(36, 30, ms=900)])]
        report = monotonicity.summarize_monotonicity(entries, 24)
        # Pairs are (16,24) and (16,36) and (24,36); only 36-vs-24 and 36-vs-16 are inversions.
        self.assertEqual(report["widthPairsCompared"], 3)
        self.assertEqual(report["widthInversions"], 2)
        self.assertEqual(report["battlesWithInversion"], 1)
        self.assertEqual(report["battleInversionRate"], 1.0)

    def test_ordering_variants_are_excluded_from_the_width_ladder(self):
        entries = [entry([member(24, 10, base=True, ms=100), member(24, 40, band=True, ms=100)])]
        report = monotonicity.summarize_monotonicity(entries, 24)
        # band/base share the baseline width and vary ordering, so they are not width pairs.
        self.assertEqual(report["widthPairsCompared"], 0)
        self.assertIsNone(report["inversionPairRate"])

    def test_narrower_winning_is_recorded(self):
        entries = [entry([member(16, 5, ms=500), member(24, 20, ms=600), member(36, 40, ms=900)])]
        report = monotonicity.summarize_monotonicity(entries, 24)
        self.assertEqual(report["bestWidthMemberKind"], {"w=16": 1})


class KnobTests(unittest.TestCase):
    def test_ordering_and_width_knobs_are_separated(self):
        entries = [
            entry([member(24, 20), member(16, 30), member(36, 40),
                   member(24, 5, band=True), member(24, 50, base=True)], name="band-wins"),
            entry([member(24, 20), member(16, 30), member(36, 5),
                   member(24, 40, band=True), member(24, 50, base=True)], name="width-wins"),
            entry([member(24, 5), member(16, 30), member(36, 40),
                   member(24, 40, band=True), member(24, 50, base=True)], name="baseline-wins"),
        ]
        report = monotonicity.summarize_knobs(entries)
        self.assertEqual(report["winnerKnob"], {"baseline": 1, "ordering": 1, "width": 1})

    def test_improvement_is_measured_against_the_baseline_member(self):
        entries = [entry([member(24, 20), member(24, 4, base=True)])]
        report = monotonicity.summarize_knobs(entries)
        self.assertEqual(report["improvementOverBaseline"]["ordering"]["deficit"], 16)
        self.assertEqual(report["improvementOverBaseline"]["ordering"]["battles"], 1)


class SubsetTests(unittest.TestCase):
    def test_saved_seconds_counts_only_dropped_members(self):
        entries = [entry([member(24, 10, ms=1000), member(16, 10, ms=2000),
                          member(24, 10, base=True, ms=3000)])]
        report = monotonicity.summarize_subsets(entries)
        # Keeping only the baseline drops w=16 and base: 2000 + 3000 ms per battle.
        self.assertEqual(report["w=24"]["savedSecondsPerBattle"], 5.0)
        self.assertEqual(report["w=24"]["keptSecondsPerBattle"], 1.0)

    def test_deficit_is_measured_against_the_full_portfolio(self):
        entries = [entry([member(24, 40, ms=1000), member(16, 10, ms=1000)])]
        report = monotonicity.summarize_subsets(entries)
        # The full portfolio finds the 10-loss route; keeping the baseline alone costs 30.
        self.assertEqual(report["w=24"]["deficitPerBattle"], 30.0)
        self.assertEqual(report["w=24"]["battlesWithCost"], 1)
        self.assertEqual(report["w=24"]["worstBattleDeficit"], 30)
        self.assertEqual(report["w=16+w=24"]["deficitPerBattle"], 0.0)

    def test_outcome_class_change_costs_a_whole_unit(self):
        entries = [entry([member(24, 50, won=False, ms=1000), member(16, 0, ms=1000)])]
        report = monotonicity.summarize_subsets(entries)
        self.assertEqual(report["w=24"]["deficitPerBattle"], 1.0)

    def test_frontier_drops_dominated_subsets(self):
        report = {"cheap": {"savedSecondsPerBattle": 9.0, "deficitPerBattle": 0.1},
                  "dear": {"savedSecondsPerBattle": 8.0, "deficitPerBattle": 0.1},
                  "worse": {"savedSecondsPerBattle": 9.0, "deficitPerBattle": 0.2}}
        self.assertEqual(sorted(monotonicity.frontier(report)), ["cheap"])


class LoadingTests(unittest.TestCase):
    def test_report_is_json_serializable(self):
        entries = [entry([member(24, 10, ms=1000), member(16, 10, ms=2000)])]
        report = {"monotonicity": monotonicity.summarize_monotonicity(entries, 24),
                  "knobs": monotonicity.summarize_knobs(entries),
                  "subsets": monotonicity.summarize_subsets(entries)}
        json.dumps(report)


if __name__ == "__main__":
    unittest.main()
