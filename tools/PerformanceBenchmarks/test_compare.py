"""Guard against publishing speedups from unequal or invalid searches."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare import compare


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.a, self.b = (Path(self.temp.name) / name for name in ("a", "b"))
        fixture = {
            "result.json": {"status": "Passed", "processId": 123, "runId": "test",
                            "solverMetrics": {"boundary": "None", "totalExpanded": 10,
                                              "totalElapsedMilliseconds": 100,
                                              "totalWorkerAllocatedBytes": 200,
                                              "totalGcPauseMilliseconds": 0}},
            "details.json": {"result": "RESULT forks=12 round_prefix_captures=2 transitions=10 score=42\nT1 victory",
                             "actions": [{"kind": "PlayCard", "card": "STRIKE_IRONCLAD"}],
                             "policy": {"maxNodes": 100000}},
            "memory.json": {"pid": 123, "launcherExitCode": 0, "processPeakRssBytes": 1000,
                            "samples": [{"VmSwap": 0}]},
            "input.json": {"fixedSearchBudget": False},
            "settings.json": {"performancePreset": "VeryHigh"},
            "generated-scenario.resolved.json": {"seed": "frozen"},
            "generated-scenario.opening.json": {"rng": [1, 2, 3, 4]},
            "generated-scenario.loadout.json": {"cards": ["STRIKE_IRONCLAD"]},
        }
        for path in (self.a, self.b):
            path.mkdir()
            for name, value in fixture.items():
                (path / name).write_text(json.dumps(value))

    def change(self, name, mutation):
        path = self.b / name
        value = json.loads(path.read_text())
        mutation(value)
        path.write_text(json.dumps(value))

    def assert_drift(self):
        result = compare(self.a, self.b)
        self.assertFalse(result["oracleEqual"])
        self.assertNotIn("changePercent", result)

    def test_equal_work_with_faster_time(self):
        self.change("result.json", lambda x: x["solverMetrics"].update(totalElapsedMilliseconds=80))
        result = compare(self.a, self.b)
        self.assertTrue(result["oracleEqual"])
        self.assertAlmostEqual(result["changePercent"]["totalElapsedMilliseconds"], -20)

    def test_unknown_text_field_is_not_silently_ignored(self):
        self.change("details.json", lambda x: x.update(result=x["result"].replace("score=42", "score=42 new_quality=1")))
        self.assert_drift()

    def test_unknown_structured_metric_is_not_silently_ignored(self):
        self.change("result.json", lambda x: x["solverMetrics"].update(newQualityFlag=True))
        self.assert_drift()

    def test_full_actions_participate(self):
        self.change("details.json", lambda x: x["actions"][0].update(card="DEFEND_IRONCLAD"))
        self.assert_drift()

    def test_full_route_participates(self):
        self.change("details.json", lambda x: x.update(result=x["result"].replace("T1 victory", "T2 victory")))
        self.assert_drift()

    def test_search_budget_participates(self):
        self.change("details.json", lambda x: x["policy"].update(maxNodes=1000))
        self.assert_drift()

    def test_memory_from_another_process_is_rejected(self):
        self.change("memory.json", lambda x: x.update(pid=456))
        with self.assertRaisesRegex(ValueError, "different process"):
            compare(self.a, self.b)

    def test_timeout_is_not_a_fixed_work_speedup(self):
        self.change("result.json", lambda x: x["solverMetrics"].update(boundary="TimeLimit"))
        with self.assertRaisesRegex(ValueError, "time-limited"):
            compare(self.a, self.b)

    def test_failed_request_is_rejected(self):
        self.change("result.json", lambda x: x.update(status="Failed"))
        with self.assertRaisesRegex(ValueError, "did not pass"):
            compare(self.a, self.b)

    def test_card_fallback_extra_copy_is_accounted(self):
        self.change("details.json", lambda x: x.update(result=x["result"].replace(
            "forks=12", "forks=13 card_prefix_fallbacks=1 card_prefix_captures=4 card_prefix_reuses=8")))
        self.change("result.json", lambda x: x["solverMetrics"].update(cardChoicePrefixFallbacks=1))
        self.assertTrue(compare(self.a, self.b)["oracleEqual"])

    def test_card_fallback_cannot_hide_transition_drift(self):
        self.change("details.json", lambda x: x.update(result=x["result"].replace(
            "forks=12", "forks=13 card_prefix_fallbacks=2")))
        with self.assertRaisesRegex(ValueError, "physical Forks"):
            compare(self.a, self.b)

    def test_physical_forks_must_account_for_prefix_copies(self):
        self.change("details.json", lambda x: x.update(result=x["result"].replace("forks=12", "forks=13")))
        with self.assertRaisesRegex(ValueError, "physical Forks"):
            compare(self.a, self.b)


if __name__ == "__main__":
    unittest.main()
