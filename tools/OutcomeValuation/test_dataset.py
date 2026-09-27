import json
import hashlib
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

from dataset import audit, evaluation_manifests, verify_resolved_loadout
from evaluate import candidate_identity, claim_final_test, evaluate, quality_evidence_issues
from prepare_training import select_encounters
from refit import refit


class SeparationContracts(unittest.TestCase):
    def test_completed_outcome_requires_no_prediction_or_snapshot_boundary(self):
        result = {'resultScope': 'SearchCompletion', 'boundaryReason': 'NodeLimit',
                  'quality': {'won': True, 'survives': True, 'combatEndedTurn': 3},
                  'snapshot': {'hasRisk': False, 'boundaryReason': 'None',
                               'playerDead': False, 'allEnemiesDead': True}}
        self.assertEqual(quality_evidence_issues(result), [])
        for field, value, expected in [('hasRisk', True, 'prediction risk'),
                                        ('boundaryReason', 'PendingChoice', 'unresolved snapshot boundary')]:
            changed = {**result, 'snapshot': {**result['snapshot'], field: value}}
            self.assertIn(expected, quality_evidence_issues(changed))
        self.assertIn('partial result scope', quality_evidence_issues(
            {**result, 'resultScope': 'CurrentTurnAdoption'}))
        self.assertIn('memory-truncated search', quality_evidence_issues(
            {**result, 'boundaryReason': 'MemoryNoProgress'}))

    def test_unfinished_search_is_not_a_terminal_defeat(self):
        result = {'resultScope': 'SearchCompletion', 'boundaryReason': 'NodeLimit',
                  'quality': {'won': False, 'survives': False, 'combatEndedTurn': None},
                  'snapshot': {'hasRisk': False, 'boundaryReason': 'None',
                               'playerDead': True, 'allEnemiesDead': False}}
        self.assertEqual(quality_evidence_issues(result), [])
        unfinished = {**result, 'snapshot': {**result['snapshot'], 'playerDead': False}}
        self.assertIn('no completed victory or engine-confirmed terminal defeat',
                      quality_evidence_issues(unfinished))

    def test_refit_rejects_changed_request_behind_an_unchanged_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def manifest(name, split, card):
                request = root / (name + '-request.json')
                request.write_text(json.dumps({'characterId': 'A', 'encounterId': name,
                    'clearRunDeck': True, 'clearPlayerPiles': True, 'cards': [{'cardId': card}]}))
                value = {'cases': [{'id': name, 'family': name, 'split': split, 'request': str(request)}]}
                path = root / (name + '-manifest.json')
                path.write_text(json.dumps(value))
                return path, value, request
            train, training, request = manifest('training', 'train', 'BLOCK')
            validation, heldout, _ = manifest('validation', 'validation', 'STRIKE')
            test, _, _ = manifest('test', 'test', 'POWER')
            budget = {'completed': True, 'elapsedSeconds': 10,
                      'manifestSha256': hashlib.sha256(train.read_bytes()).hexdigest(),
                      'separation': audit(training, heldout)}
            (root / 'training-budget.json').write_text(json.dumps(budget))
            changed = json.loads(request.read_text())
            changed['initialPlayerHp'] = 1
            request.write_text(json.dumps(changed))
            with self.assertRaisesRegex(ValueError, 'Underlying training requests/loadouts changed'):
                refit(SimpleNamespace(prior_training=root, seconds=60, manifest=train,
                                      evaluation_manifest=[validation, test]))

    def test_validation_and_test_cannot_share_scenes_or_decks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def case(name, split, card, encounter):
                path = root / (name + '.json')
                path.write_text(json.dumps({'characterId': 'A', 'encounterId': encounter,
                    'clearRunDeck': True, 'clearPlayerPiles': True,
                    'cards': [{'cardId': card}] * 10}))
                return {'id': name, 'family': name, 'split': split, 'request': str(path)}
            training = {'cases': [case('train', 'train', 'BLOCK', 'FIRST')]}
            validation = case('validation', 'validation', 'STRIKE', 'SECOND_NORMAL')
            test = case('test', 'test', 'POWER', 'SECOND_WEAK')
            with self.assertRaisesRegex(ValueError, 'same encounter family'):
                audit(training, {'cases': [validation, test]})
            test = case('test', 'test', 'STRIKE', 'THIRD')
            with self.assertRaisesRegex(ValueError, 'near-duplicate deck'):
                audit(training, {'cases': [validation, test]})
            test = case('test', 'test', 'POWER', 'THIRD')
            report = audit(training, {'cases': [validation, test]})
            self.assertEqual(report['splitCounts'], {'train': 1, 'validation': 1, 'test': 1})
            self.assertEqual(len(report['comparisons']), 3)
            # Moving a root between validation/test changes the frozen identity too.
            moved = audit(training, {'cases': [{**validation, 'split': 'test'},
                                               {**test, 'split': 'validation'}]})
            self.assertNotEqual(report['datasetSha256'], moved['datasetSha256'])

    def test_training_and_final_evaluation_require_development_split(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / 'test.json'
            manifest.write_text(json.dumps({'cases': [{'split': 'test'}]}))
            with self.assertRaisesRegex(ValueError, 'both validation and sealed test'):
                evaluation_manifests([manifest], require_final_test=True)
            with self.assertRaisesRegex(ValueError, 'development validation manifest'):
                evaluate(SimpleNamespace(manifest=manifest, validation_manifest=None))

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
