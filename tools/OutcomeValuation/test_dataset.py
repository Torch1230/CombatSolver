import json
from pathlib import Path
import tempfile
import unittest

from dataset import audit


class SeparationContracts(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
