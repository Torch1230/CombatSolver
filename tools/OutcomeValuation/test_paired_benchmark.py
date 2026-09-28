import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest

from benchmark_report import classify, summarize
from paired_benchmark import comparisons_for_case, run_process


class BenchmarkContracts(unittest.TestCase):
    def test_repeats_cannot_hide_regression_or_unverified_evidence(self):
        for signs, expected in [([-1, 0], 'improved'), ([0, 0], 'equal'),
                                ([1, 0], 'regressed'), ([-1, 1], 'mixed')]:
            self.assertEqual(classify(signs, True), expected)
            self.assertEqual(classify(signs, False), 'unverified')
        self.assertEqual(classify([-1], True), 'unverified')

    def test_missing_arm_is_not_substituted_with_another_run(self):
        records = [{'run': run, 'state': 'completed', 'qualityPath': run}
                   for run in ['candidate-0', 'candidate-1', 'baseline-1']]
        pairs = comparisons_for_case({'id': 'case'}, records)
        self.assertEqual({r['id'] for r in pairs}, {'case/repeat-1', 'case/candidate-stability'})

    def test_process_failure_and_timeout_are_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            failed = run_process([sys.executable, '-c', 'raise SystemExit(7)'], path,
                                 dict(os.environ), threading.Event(), 5)
            self.assertEqual(failed['returnCode'], 7)
            self.assertFalse(failed['timedOut'])
            timeout = run_process([sys.executable, '-c', 'import time;time.sleep(10)'], path,
                                  dict(os.environ), threading.Event(), .1)
            self.assertTrue(timeout['timedOut'])
            self.assertNotEqual(timeout['returnCode'], 0)

    def test_faster_bad_routes_and_failed_cases_remain_in_denominator(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / 'manifest.json'
            manifest.write_text(json.dumps({'cases': [{'id': name, 'kind': 'Boss'}
                                                      for name in ('safe', 'bad', 'failed')]}))
            records, comparisons = [], []
            for name, sign in [('safe', 0), ('bad', 1), ('failed', -1)]:
                for arm in ('baseline', 'candidate'):
                    for repeat in (0, 1):
                        record = {'id': name, 'run': f'{arm}-{repeat}', 'arm': arm, 'character': 'A',
                                  'encounter': name, 'state': 'completed', 'issues': [],
                                  'seconds': 10 if arm == 'baseline' else 1, 'rss': 100,
                                  'managedHeap': 50, 'managedLive': 30, 'allocated': 200,
                                  'timeBoundary': False, 'quality': {'won': name == 'safe' or arm == 'baseline',
                                  'projectedBattleHpLost': 3}, 'metrics': {'totalExpanded': 5}}
                        if name == 'failed' and arm == 'candidate' and repeat == 1:
                            record.update(state='failed', reason='timeout')
                        records.append(record)
                for suffix, value in [('repeat-0', sign), ('repeat-1', sign),
                                      ('candidate-stability', 0), ('baseline-stability', 0)]:
                    comparisons.append({'id': name + '/' + suffix, 'materialComparison': value})
            result = summarize({'manifest': str(manifest), 'modelSha256': 'frozen'},
                               {'records': records, 'comparisons': comparisons, 'completed': True})
            self.assertEqual(result['counts'], {'equal': 1, 'regressed': 1, 'unverified': 1})
            self.assertEqual(result['casesFinished'], 3)
            self.assertEqual(result['allCompletedCosts']['faster'], 2)
            self.assertEqual(result['nondegradingCosts']['faster'], 1)
            self.assertEqual(result['equalWinCases'], 1)
            self.assertEqual(result['stableNondegradingWinCosts']['faster'], 1)
            self.assertEqual(result['coreCounts'], {'unverified': 3})
            self.assertEqual(result['coreNondegradingCosts']['cases'], 0)
            # Core evidence must come from the authoritative comparison, never
            # inferred from the primary classification or a fast runtime.
            for comparison in comparisons:
                comparison['coreComparison'] = comparison['materialComparison']
            core = summarize({'manifest': str(manifest), 'modelSha256': 'frozen'},
                             {'records': records, 'comparisons': comparisons, 'completed': True})
            self.assertEqual(core['coreCounts'], {'equal': 1, 'regressed': 1, 'unverified': 1})
            self.assertEqual(core['coreNondegradingCosts']['faster'], 1)
            # A later equal-cost victory can be worse only on ending turn.
            for record in records:
                if record['id'] == 'bad':
                    record['quality']['won'] = True
            for comparison in comparisons:
                if comparison['id'].startswith('bad/'):
                    comparison['coreComparison'] = 0
            ending_turn_only = summarize({'manifest': str(manifest), 'modelSha256': 'frozen'},
                                         {'records': records, 'comparisons': comparisons, 'completed': True})
            self.assertEqual(ending_turn_only['counts'], result['counts'])
            self.assertEqual(ending_turn_only['coreCounts'], {'equal': 2, 'unverified': 1})
            self.assertEqual(ending_turn_only['coreNondegradingCosts']['faster'], 2)
            self.assertEqual(ending_turn_only['coreEqualWinCases'], 2)
            self.assertEqual(ending_turn_only['coreByCharacter'], {'A': {'equal': 2, 'unverified': 1}})


if __name__ == '__main__':
    unittest.main()
