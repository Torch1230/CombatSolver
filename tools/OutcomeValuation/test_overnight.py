import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from overnight import Job, StopRequested, balanced_roots, encounter_buckets, specification, witnessed_rows


class OvernightContracts(unittest.TestCase):
    def test_all_role_act_kind_strata_are_scheduled_without_heldout_families(self):
        with tempfile.TemporaryDirectory() as temporary:
            request = Path(temporary) / 'request.json'
            request.write_text(json.dumps({'characterId': 'A', 'encounterId': 'RESERVED_WEAK',
                                          'clearRunDeck': True, 'clearPlayerPiles': True,
                                          'cards': [{'cardId': 'CARD'}]}))
            heldout = {'cases': [{'id': 'test', 'family': 'test', 'split': 'test', 'request': str(request)}]}
            catalog = {'characters': ['A', 'B', 'C', 'D', 'E'], 'encounters': [
                {'actIndex': act, 'roomType': kind, 'id': f'{act}_{kind}'}
                for act in range(3) for kind in ('Monster', 'Elite', 'Boss')] + [
                    {'actIndex': 0, 'roomType': 'Monster', 'id': 'RESERVED_NORMAL'}]}
            buckets = encounter_buckets(catalog, heldout)
            specs = [specification(catalog, buckets, 'seed', i) for i in range(45)]
            self.assertEqual(len({(s['characterId'], s['actIndex'], s['encounterKind']) for s in specs}), 45)
            self.assertFalse(any(s['encounterId'].startswith('RESERVED') for s in specs))

    def test_fit_root_cap_does_not_let_a_common_stratum_crowd_out_others(self):
        records = [{'state': 'collected', 'hasPreferences': True, 'stratum': [str(i % 5), 0, 'Boss'],
                    'id': i} for i in range(100)]
        records += [{'state': 'collected', 'hasPreferences': True, 'stratum': ['rare', 2, 'Elite'], 'id': 101}]
        records += [{'state': 'quarantined', 'stratum': ['bad'], 'id': 102}]
        selected = balanced_roots(records, 12, 'seed')
        self.assertEqual(len(selected), 12)
        self.assertEqual(len({r['id'] for r in selected}), 12)
        self.assertIn(101, [r['id'] for r in selected])
        self.assertNotIn(102, [r['id'] for r in selected])

    def test_pruned_or_nonfinite_rows_cannot_become_training_labels(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'rows.json'
            row = {'FeatureSchema': 6, 'Features': {'hp': 20}, 'Outcome': {
                'Won': False, 'Survives': False, 'Score': 0}, 'CompletedDefeat': False,
                'Groups': [1], 'RemainingActions': 1}
            path.write_text(json.dumps([row]))
            with self.assertRaisesRegex(ValueError, 'Invalid completed witness'):
                witnessed_rows(path)
            row['CompletedDefeat'] = True
            self.assertIsNotNone(path.write_text(json.dumps([row])))
            self.assertEqual(witnessed_rows(path), (1, False))
            row['Features']['hp'] = float('nan')
            path.write_text(json.dumps([row]))
            with self.assertRaises(ValueError):
                witnessed_rows(path)

    def test_deadline_terminates_and_reaps_the_owned_child(self):
        with tempfile.TemporaryDirectory() as temporary:
            job = Job.__new__(Job)
            job.root = Path(temporary)
            job.harness = job.root / 'unused.dll'
            job.environment = dict(os.environ)
            job.stop = threading.Event()
            job.lock = threading.Lock()
            job.state = {'childSeconds': {}}
            job.deadline = time.time() + 0.4
            actual_popen = subprocess.Popen
            children = []
            def start_child(command, **kwargs):
                process = actual_popen([sys.executable, '-c', 'import time; time.sleep(30)'], **kwargs)
                children.append(process)
                return process
            with patch('overnight.subprocess.Popen', side_effect=start_child):
                with self.assertRaises(StopRequested):
                    job.command([], job.root, 'test', 20)
            self.assertEqual(len(children), 1)
            self.assertIsNotNone(children[0].poll())
            self.assertLess(job.state['childSeconds']['test'], 5)
            self.assertNotEqual(json.loads((job.root / 'test-process.json').read_text())['returnCode'], 0)


if __name__ == '__main__':
    unittest.main()
