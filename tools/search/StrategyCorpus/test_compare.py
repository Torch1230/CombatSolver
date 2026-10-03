import unittest

from compare import better, classify


QUALITY = {
    "won": True, "survives": True, "deathSaveUseCount": 0,
    "theftPolicy": None, "outstandingStolenResource": 0,
    "strategicHpDeficit": 0, "potionStrategicCost": 36,
    "projectedBattlePotionCount": 4, "growthHpCredit": 0,
    "growthRewardCount": 0, "projectedBattleHpLost": 0,
    "combatEndedTurn": 5, "enemyHp": 0, "score": 100,
}


class ComparisonTests(unittest.TestCase):
    def test_potion_trade_keeps_hp_first(self):
        candidate = {**QUALITY, "strategicHpDeficit": 16,
                     "potionStrategicCost": 9, "projectedBattlePotionCount": 1}
        self.assertTrue(better(candidate, QUALITY))
        self.assertFalse(better(QUALITY, candidate))

    def test_route_and_work_are_distinct(self):
        base = {"status": "comparable", "identity": {"root": "same"},
                "quality": QUALITY, "outcome": {"hp": 0}, "actions": [{"cardId": "A"}],
                "continuations": [], "metrics": {"totalExpanded": 10}, "pruneCounters": {}}
        changed = {**base, "actions": [{"cardId": "B"}]}
        self.assertEqual("质量不变", classify(base, changed)["classification"])
        self.assertEqual("逐位相同", classify(base, base)["classification"])
        self.assertEqual("不可比较", classify(base, {**base, "identity": {"root": "other"}})["classification"])

    def test_victory_and_loss(self):
        lost = {**QUALITY, "won": False, "survives": False}
        self.assertTrue(better(QUALITY, lost))
        self.assertFalse(better(lost, QUALITY))

    def test_time_boundary_is_not_exact_evidence(self):
        case = {"status": "comparable", "identity": {"root": "same"},
                "observations": {"elapsedMilliseconds": 109992}}
        self.assertEqual("不可比较", classify(case, case, 110000)["classification"])

    def test_quality_direction(self):
        baseline = {"status": "comparable", "identity": {"root": "same"},
                    "quality": QUALITY}
        cheaper = {**baseline, "quality": {**QUALITY, "potionStrategicCost": 9,
                                            "projectedBattlePotionCount": 1}}
        self.assertEqual("变好", classify(baseline, cheaper)["classification"])
        self.assertEqual("变差", classify(cheaper, baseline)["classification"])


if __name__ == "__main__":
    unittest.main()
