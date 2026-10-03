import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('veryhigh_gate', Path(__file__).with_name('check-veryhigh.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class VeryHighAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.case = {'id': 'test', 'baseline': [], 'candidate': []}
        for arm, times in [('baseline', [40, 42]), ('candidate', [19, 20])]:
            for index, seconds in enumerate(times):
                path = self.root / f'{arm}-{index}'
                path.mkdir()
                self.case[arm].append(str(path))
                result = dict(status='Passed', exitCode=0, rootContinuationStamp='same-root',
                              searchMode='Coordinator', profile='VeryHigh',
                              budget=dict(gate.FROZEN_BUDGET), wallSeconds=seconds,
                              timeBoundary=False, timeBoundaryObserved=False,
                              peakProcessWorkingSetBytes=1000 if arm == 'baseline' else 1100)
                quality = {'quality': dict(won=True, survives=True, projectedBattleHpLost=4,
                                          strategicHpDeficit=4, potionStrategicCost=0,
                                          projectedBattlePotionCount=0, combatEndedTurn=5, score=1),
                           'snapshot': {'hasRisk': False}}
                for name, value in [('result.json', result), ('quality.json', quality), ('search-policy.json', {})]:
                    (path / name).write_text(json.dumps(value))

    def change(self, name, callback, arm='candidate', index=0):
        path = Path(self.case[arm][index]) / name
        value = json.loads(path.read_text())
        callback(value)
        path.write_text(json.dumps(value))

    def test_exact_speed_and_memory_thresholds(self):
        self.assertTrue(gate.check_case(self.case)['passed'])

    def test_single_slow_candidate_cannot_be_hidden_by_mean(self):
        self.change('result.json', lambda d: d.update(wallSeconds=20.001), index=1)
        self.assertFalse(gate.check_case(self.case)['speedPassed'])

    def test_memory_over_threshold_rejected(self):
        self.change('result.json', lambda d: d.update(peakProcessWorkingSetBytes=1101))
        self.assertFalse(gate.check_case(self.case)['memoryPassed'])

    def test_more_hp_loss_rejected_despite_fewer_turns(self):
        self.change('quality.json', lambda d: d['quality'].update(projectedBattleHpLost=5, combatEndedTurn=1))
        self.assertFalse(gate.check_case(self.case)['qualityPassed'])

    def test_equal_loss_different_turns_allowed(self):
        self.change('quality.json', lambda d: d['quality'].update(combatEndedTurn=20, score=-10))
        self.assertTrue(gate.check_case(self.case)['passed'])

    def test_missing_hwm_not_replaced_with_sampled_peak(self):
        self.change('result.json', lambda d: (d.pop('peakProcessWorkingSetBytes'), d.update(peakWorkingSetBytes=1)))
        self.assertIn('high-water', gate.check_case(self.case)['error'])

    def test_risky_prediction_and_time_boundary_inconclusive(self):
        self.change('quality.json', lambda d: d['snapshot'].update(hasRisk=True))
        self.assertIn('prediction risk', gate.check_case(self.case)['error'])
        self.change('quality.json', lambda d: d['snapshot'].update(hasRisk=False))
        self.change('result.json', lambda d: d.update(timeBoundary=True))
        self.assertIn('time boundary', gate.check_case(self.case)['error'])

    def test_changed_root_or_budget_rejected(self):
        self.change('result.json', lambda d: d.update(rootContinuationStamp='different-root'))
        self.assertIn('Different combat roots', gate.check_case(self.case)['error'])
        self.change('result.json', lambda d: d.update(rootContinuationStamp='same-root'))
        self.change('result.json', lambda d: d['budget'].update(maxExpandedNodes=1))
        self.assertIn('frozen production', gate.check_case(self.case)['error'])

    def test_unstable_baseline_compares_against_both_samples(self):
        self.change('quality.json', lambda d: d['quality'].update(projectedBattleHpLost=3), arm='baseline')
        self.assertFalse(gate.check_case(self.case)['qualityPassed'])
        for index in (0, 1):
            self.change('quality.json', lambda d: d['quality'].update(projectedBattleHpLost=3), index=index)
        self.assertTrue(gate.check_case(self.case)['passed'])

    def test_both_arms_cannot_lower_the_budget_together(self):
        for arm in ('baseline', 'candidate'):
            for index in (0, 1):
                self.change('result.json', lambda d: d['budget'].update(maxExpandedNodes=1000), arm, index)
        self.assertIn('frozen production', gate.check_case(self.case)['error'])

    def test_timed_baseline_is_request_quality_evidence(self):
        self.change('result.json', lambda d: d.update(timeBoundary=True), arm='baseline')
        report = gate.check_case(self.case)
        self.assertTrue(report['passed'])
        self.assertTrue(report['baselineTimed'])

    def test_missing_time_boundary_cannot_pass(self):
        self.change('result.json', lambda d: d.pop('timeBoundary'))
        self.assertIn('missing time-boundary', gate.check_case(self.case)['error'])

    def test_fast_sentinel_still_requires_quality(self):
        for index in (0, 1):
            self.change('result.json', lambda d: d.update(wallSeconds=10), arm='baseline', index=index)
        self.assertTrue(gate.check_case(self.case)['passed'])
        self.change('quality.json', lambda d: d['quality'].update(won=False))
        self.assertFalse(gate.check_case(self.case)['passed'])


if __name__ == '__main__':
    unittest.main()
