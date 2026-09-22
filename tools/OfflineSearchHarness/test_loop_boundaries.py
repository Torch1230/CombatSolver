"""Regression checks for reproducibility classification; no combat emulation."""
import json
from pathlib import Path
import tempfile
import unittest

from run_loop_boundaries import (
    QUALITY, replay_budget_observation, budget_observation, compare_runs, observation_status, report_exit_code,
)


class LoopBoundaryClassificationTests(unittest.TestCase):
    def observe(self, metrics, messages=(), time_boundary=False):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            logs = out / 'logs' / 'process'
            logs.mkdir(parents=True)
            (logs / 'process.jsonl').write_text(''.join(
                json.dumps({'Message': message}) + '\n' for message in messages))
            return budget_observation({'solverMetrics': metrics,
                                       'timeBoundaryObserved': time_boundary}, out)

    def sample(self, label, budget, turn=2, failures=()):
        status = observation_status(budget, failures)
        return {'label': label, 'status': status, 'valid': status == 'Comparable',
                'root': 'same-root', 'routeSha256': f'turn-{turn}',
                'fixtureCheckFailures': list(failures), 'budgetObservation': budget,
                'metrics': {key: turn if key == 'CombatEndedTurn' else 0 for key in QUALITY}}

    def test_audited_abba_t3_is_inconclusive_and_difference_is_preserved(self):
        nodes = self.observe({'TurnLayerBudgetStops': 1},
                             ['TURN_LAYER_BUDGET reason=nodes completed_turns=0'])
        time = self.observe({'TurnLayerBudgetStops': 2},
                            ['TURN_LAYER_BUDGET reason=time completed_turns=0',
                             'TURN_LAYER_BUDGET reason=time completed_turns=1'])
        runs = [self.sample('A1', nodes), self.sample('B1', nodes),
                self.sample('B2', time, 3, ['CombatEndedTurn']), self.sample('A2', nodes)]
        item = compare_runs('cap', runs)
        self.assertEqual('Inconclusive', item['status'])
        self.assertEqual(2, report_exit_code([item]))
        self.assertFalse(item['sameRoute'])
        self.assertFalse(item['sameQualityMetrics'])
        self.assertEqual(['CombatEndedTurn'], item['runs'][2]['fixtureCheckFailures'])
        self.assertEqual(2, time['turnLayerTimeStops'])

    def test_node_only_t3_is_still_a_real_comparable_difference(self):
        budget = self.observe({}, ['TURN_LAYER_BUDGET reason=nodes'])
        item = compare_runs('cap', [self.sample('A', budget), self.sample('B', budget, 3)])
        self.assertEqual('Different', item['status'])
        self.assertEqual(1, report_exit_code([item]))

    def test_new_split_counters_match_flushed_reasons(self):
        budget = self.observe({'TurnLayerBudgetStops': 3, 'TurnLayerTimeBudgetStops': 2,
                               'TurnLayerNodeBudgetStops': 1},
                              ['TURN_LAYER_BUDGET reason=time', 'TURN_LAYER_BUDGET reason=nodes',
                               'TURN_LAYER_BUDGET reason=time'])
        self.assertEqual('solverMetrics', budget['source'])
        self.assertEqual([], budget['diagnosticIssues'])
        self.assertEqual('TimeLimited', observation_status(budget, []))

    def test_counter_disagreement_is_not_accepted_as_clock_noise(self):
        budget = self.observe({'TurnLayerBudgetStops': 1, 'TurnLayerTimeBudgetStops': 0,
                               'TurnLayerNodeBudgetStops': 1}, ['TURN_LAYER_BUDGET reason=time'])
        self.assertEqual('DiagnosticsMismatch', observation_status(budget, []))

    def test_global_time_is_excluded_without_local_stops(self):
        for messages, flag in [(['SEARCH_TIME_BUDGET elapsed_ms=20000'], False), ([], True)]:
            with self.subTest(messages=messages, flag=flag):
                budget = self.observe({}, messages, flag)
                self.assertEqual('TimeLimited', observation_status(budget, []))

    def test_missing_old_dll_diagnostics_is_not_assumed_node_limited(self):
        with tempfile.TemporaryDirectory() as directory:
            budget = budget_observation({'solverMetrics': {}}, Path(directory))
        self.assertEqual('BudgetEvidenceUnavailable', observation_status(budget, []))

    def test_harness_failure_takes_precedence_over_inconclusive_sample(self):
        time = self.observe({}, ['TURN_LAYER_BUDGET reason=time'])
        item = compare_runs('cap', [self.sample('A', time),
                                   {'valid': False, 'status': 'HarnessFailed'}])
        self.assertEqual(1, report_exit_code([item]))

    def test_coordinator_never_substitutes_selected_solver_for_request_replay_count(self):
        self.assertEqual(('unavailable', None), replay_budget_observation({'CycleReplayActions': 6}, 'Coordinator'))
        self.assertEqual(('request', 4096), replay_budget_observation(
            {'CycleReplayActions': 6, 'TotalCycleReplayActions': 4096}, 'Coordinator'))

    def test_coordinator_uses_all_solver_log_reasons(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            logs = out / 'logs' / 'process'
            logs.mkdir(parents=True)
            (logs / 'process.jsonl').write_text(json.dumps({'Message': 'TURN_LAYER_BUDGET reason=time'}) + '\n')
            result = {'solverMetrics': {'TurnLayerTimeBudgetStops': 0, 'TurnLayerNodeBudgetStops': 0}}
            budget = budget_observation(result, out, 'Coordinator')
            self.assertEqual('request', budget['scope'])
            self.assertEqual('TimeLimited', observation_status(budget, []))

    def test_cap_suite_has_no_fixed_turn_requirement_and_scope_is_explicit(self):
        path = Path(__file__).resolve().parents[2] / 'coverage/unattended/loop-boundaries-20260921/suite.json'
        suite = json.loads(path.read_text())
        cap = next(case for case in suite['cases'] if case['name'] == 'letter-replay-cap')
        self.assertNotIn('CombatEndedTurn', cap['expectedMetrics'])
        self.assertEqual('Evaluate', suite['searchMode'])


if __name__ == '__main__':
    unittest.main()
