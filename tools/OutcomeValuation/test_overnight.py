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

from overnight import Job, StopRequested, balanced_roots, encounter_buckets, freeze_baseline, specification, training_inputs, witnessed_rows
from dataset import verify_resolved_loadout


class OvernightContracts(unittest.TestCase):
    def test_training_budget_preserves_actual_roots_and_legacy_inputs(self):
        records = [{'input': 'first.json'}, {'input': 'second.json'}]
        self.assertEqual(training_inputs(records, 2048), ['first.json', 'second.json'])
        self.assertEqual(training_inputs(records, 512), {
            'schemaVersion': 1, 'maximumRowsPerRoot': 512, 'roots': ['first.json', 'second.json']})
        self.assertEqual(training_inputs(records, 2048, 'character'), {
            'schemaVersion': 1, 'maximumRowsPerRoot': 2048, 'roots': ['first.json', 'second.json'],
            'partition': 'character'})
        with self.assertRaisesRegex(ValueError, 'Unknown training partition'):
            training_inputs(records, 512, 'unknown')

    def test_frozen_baseline_preserves_native_equipment_for_the_next_job(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'original'
            (source / 'evidence').mkdir(parents=True)
            loadout = source / 'evidence/generated-scenario.loadout.json'
            loadout.write_text(json.dumps({'deck': ['A', 'B'], 'relics': ['C']}))
            for name in ('quality.json', 'result.json', 'harness-result.json'):
                (source / name).write_text('{}')
            case = {'loadout': str(loadout), 'id': 'native-case'}
            freeze_baseline(case, source, root / 'first')
            freeze_baseline(case, root / 'first', root / 'second')
            verify_resolved_loadout(case, root / 'second')
            self.assertEqual(loadout.read_bytes(), (root / 'second/evidence/generated-scenario.loadout.json').read_bytes())

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
            different_death = {**row, 'Outcome': {**row['Outcome'], 'EnemyHp': 99}, 'RemainingActions': 25}
            path.write_text(json.dumps([row, different_death]))
            self.assertEqual(witnessed_rows(path), (2, False), 'Two deaths do not supply a preference')
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

    def test_upgrading_the_fitter_does_not_replace_the_search_engine(self):
        with tempfile.TemporaryDirectory() as temporary:
            job = Job.__new__(Job)
            job.root = Path(temporary)
            job.harness = job.root / 'search/harness.dll'
            job.fitting_engine = job.root / 'training'
            job.environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL='frozen-search.dll')
            job.stop, job.lock = threading.Event(), threading.Lock()
            job.state = {'childSeconds': {}}
            job.deadline = time.time() + 10
            actual_popen, observed = subprocess.Popen, []
            def start_child(command, **kwargs):
                observed.append((command, kwargs['env']))
                return actual_popen([sys.executable, '-c', 'pass'], **kwargs)
            with patch('overnight.subprocess.Popen', side_effect=start_child):
                job.command([], job.root / 'fit', 'fit', 5)
                job.command([], job.root / 'search', 'evaluate', 5)
            self.assertEqual(observed[0][0][1], str(job.fitting_engine / 'harness/OfflineSearchHarness.dll'))
            self.assertEqual(observed[0][1]['OFFLINE_HARNESS_COMBATSOLVER_DLL'], str(job.fitting_engine / 'CombatSolver.dll'))
            self.assertEqual(observed[1][0][1], str(job.harness))
            self.assertEqual(observed[1][1]['OFFLINE_HARNESS_COMBATSOLVER_DLL'], 'frozen-search.dll')


if __name__ == '__main__':
    unittest.main()
