"""Guard goal-policy comparisons while varying recorded portfolio algorithms."""
import copy
import unittest
from compare import context_mismatch

class ComparisonContextTests(unittest.TestCase):
    def setUp(self):
        self.baseline = {'search': {'rootContinuationStamp': 'root'}, 'budget': {'nodes': 120000},
                         'searchPolicy': {'Profile': {}, 'AcceptableBattleHpLoss': 0, 'PotionPolicy': 'Smart'}}

    def test_algorithm_switches_are_explicit_experiments_including_old_unrecorded_hosts(self):
        candidate = copy.deepcopy(self.baseline)
        candidate['searchPolicy'].update(BeamWidthPortfolioPlainBaselineMember=False, UseNoveltyPortfolio=True)
        candidate['searchPolicy']['Profile']['ReallocatedRefinementPortfolio'] = True
        self.assertIsNone(context_mismatch(self.baseline, candidate))
        self.baseline['searchPolicy'].update(BeamWidthPortfolioPlainBaselineMember=True, UseNoveltyPortfolio=False)
        self.assertIsNone(context_mismatch(self.baseline, candidate))

    def test_algorithm_changes_do_not_hide_goal_or_budget_changes(self):
        for section, key, value, reason in (
            ('searchPolicy', 'AcceptableBattleHpLoss', 6, 'PolicyMismatch'),
            ('searchPolicy', 'PotionPolicy', 'Disabled', 'PolicyMismatch'),
            ('budget', 'nodes', 240000, 'BudgetMismatch'),
            ('search', 'rootContinuationStamp', 'different-root', 'RootMismatch')):
            with self.subTest(key=key):
                candidate = copy.deepcopy(self.baseline)
                candidate['searchPolicy']['UseNoveltyPortfolio'] = True
                candidate['searchPolicy']['Profile']['ReallocatedRefinementPortfolio'] = True
                candidate[section][key] = value
                self.assertEqual(reason, context_mismatch(self.baseline, candidate))

if __name__ == '__main__':
    unittest.main()
