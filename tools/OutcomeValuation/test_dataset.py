import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

from dataset import audit, verify_resolved_loadout
from evaluate import candidate_identity, claim_final_test
from prepare_training import select_encounters


class SeparationContracts(unittest.TestCase):
    def test_final_test_cannot_be_reused_to_tune_search_budgets_or_data(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binary = root / 'fixture.bin'
            binary.write_bytes(b'frozen candidate')
            args = SimpleNamespace(model=binary, mod=binary, harness=binary,
                                   beam=24, nodes=12000, seconds=600)
            identity = candidate_identity(args, {'datasetSha256': 'original-scenes'})
            manifest = root / 'test.json'
            claim_final_test(manifest, identity)
            claim_final_test(manifest, identity)  # Repetitions of the frozen candidate are allowed.
            args.nodes = 24000
            with self.assertRaisesRegex(ValueError, 'configuration or dataset'):
                claim_final_test(manifest, candidate_identity(args, {'datasetSha256': 'original-scenes'}))
            args.nodes = 12000
            with self.assertRaisesRegex(ValueError, 'configuration or dataset'):
                claim_final_test(manifest, candidate_identity(args, {'datasetSha256': 'edited-scenes'}))
            self.assertEqual(json.loads((root / 'test-usage.json').read_text())['identity'], identity)

    def test_structural_leakage_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = {'characterId': 'A', 'encounterId': 'SLIMES_NORMAL',
                       'clearRunDeck': True, 'clearPlayerPiles': True,
                       'cards': [{'cardId': 'STRIKE', 'pile': 'Hand'}] * 10}
            def manifest(name, value, family, split):
                path = root / (name + '.json')
                path.write_text(json.dumps(value))
                return {'cases': [{'id': name, 'family': family, 'split': split, 'request': str(path)}]}
            train = manifest('train', request, 'template', 'train')
            # Merely renaming IDs, seeds, HP, counters or pile order cannot bypass the gate.
            changed = {**request, 'seed': 'new', 'initialPlayerHp': 1,
                       'cards': [{'cardId': 'STRIKE', 'pile': 'Draw', 'upgradeLevel': 1}] * 10}
            with self.assertRaisesRegex(ValueError, 'same encounter family'):
                audit(train, manifest('renamed', changed, 'other', 'test'))
            changed = {**changed, 'encounterId': 'NEW_ENEMY'}
            with self.assertRaisesRegex(ValueError, 'near-duplicate deck'):
                audit(train, manifest('same_deck', changed, 'other', 'test'))
            changed['cards'] = [{'cardId': 'BLOCK'}] * 10
            with self.assertRaisesRegex(ValueError, 'same template family'):
                audit(train, manifest('same_template', changed, 'template', 'test'))
            changed['encounterId'] = 'SLIMES_WEAK'
            with self.assertRaisesRegex(ValueError, 'same encounter family'):
                audit(train, manifest('weak_variant', changed, 'other', 'test'))
            changed['encounterId'] = 'NEW_ENEMY'
            report = audit(train, manifest('independent', changed, 'other', 'test'))
            self.assertTrue(report['passed'])
            self.assertEqual(report['maximumDeckSimilarity'], 0)

    def test_unresolved_generator_is_not_independent_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'request.json'
            path.write_text(json.dumps({'generatedScenarioPath': 'new_seed.json'}))
            case = {'id': 'random', 'family': 'random', 'split': 'train', 'request': str(path)}
            with self.assertRaisesRegex(ValueError, 'native resolved loadout'):
                audit({'cases': [case]}, {'cases': [{**case, 'split': 'test'}]})

    def test_actual_search_equipment_must_match_audited_equipment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'evidence').mkdir()
            frozen = root / 'frozen.json'
            value = {'encounterId': 'TRAIN_ENEMY', 'deck': [{'id': 'BLOCK'}]}
            frozen.write_text(json.dumps(value))
            actual = root / 'evidence/generated-scenario.loadout.json'
            actual.write_text(json.dumps(value))
            case = {'id': 'frozen', 'loadout': str(frozen)}
            verify_resolved_loadout(case, root)
            actual.write_text(json.dumps({**value, 'deck': [{'id': 'STRIKE'}]}))
            with self.assertRaisesRegex(ValueError, 'differs from the frozen native setup'):
                verify_resolved_loadout(case, root)

    def test_balanced_encounters_exclude_variants_without_outcomes(self):
        entries = [{'id': f'{kind}_{i}_NORMAL', 'actIndex': 0, 'roomType': kind}
                   for kind in ('Monster', 'Elite', 'Boss') for i in range(8)]
        entries += [{'id': 'Monster_1_WEAK', 'actIndex': 0, 'roomType': 'Monster'}]
        chosen = select_encounters({'encounters': entries}, {'Monster_0', 'Elite_0', 'Boss_0'})
        self.assertEqual(len(chosen), 8)
        families = [x['id'].removesuffix('_NORMAL').removesuffix('_WEAK') for x in chosen]
        self.assertEqual(len(set(families)), 8)
        self.assertFalse(set(families) & {'Monster_0', 'Elite_0', 'Boss_0'})
        self.assertEqual([x['roomType'] for x in chosen], ['Monster'] * 4 + ['Elite'] * 3 + ['Boss'])


if __name__ == '__main__':
    unittest.main()
