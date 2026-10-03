#!/usr/bin/env python3
"""Freeze the existing generated and historical stress inputs for DOP16 screening.

Writes requests and a run_plan.py plan; never builds, deploys or starts a game.
Historical Short/Deep limits are deliberately replaced by the current preset.
"""
import argparse
import hashlib
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]


def prepare(output, dll):
    output.mkdir(parents=True, exist_ok=False)
    requests = output / 'requests'
    requests.mkdir()
    cases = []
    for source in sorted((REPO / 'coverage/corpora/novelty').glob('*.json')):
        if not (source.stem.startswith('dev-') or source.stem.startswith('holdout-')):
            continue
        options = json.loads(source.read_text())
        if 'characterId' not in options or 'encounterKind' not in options:
            continue
        cases.append((source.stem, {
            'schemaVersion': 1, 'scenarioId': 'VH16-' + source.stem,
            'characterId': options['characterId'], 'cards': [],
            'generatedScenarioPath': str(source),
        }, str(source.relative_to(REPO)), 'Smart'))
    queen = REPO / 'coverage/corpora/runtime-gc/pressure-queen-ironclad-boss.json'
    cases.append(('pressure-queen', {
        'schemaVersion': 1, 'scenarioId': 'VH16-PRESSURE-QUEEN',
        'characterId': 'IRONCLAD', 'cards': [], 'generatedScenarioPath': str(queen),
    }, str(queen.relative_to(REPO)), 'Smart'))

    # Reconstruct the declared inputs, not their old results or time/node limits.
    evidence = REPO / 'docs/archive/performance/veryhigh-pressure-survey-20260908.json'
    rows = json.loads(evidence.read_text())['rows']
    scalar = {
        '--character-id': 'characterId', '--seed': 'seed',
        '--encounter-id': 'encounterId', '--ascension': 'ascension',
        '--act-index-for-test': 'actIndexForTest', '--enemy-current-hp': 'enemyCurrentHp',
        '--initial-player-hp': 'initialPlayerHp',
        '--initial-player-max-hp': 'initialPlayerMaxHp',
        '--initial-player-energy': 'initialPlayerEnergy',
        '--initial-player-stars': 'initialPlayerStars',
        '--pre-combat-player-current-hp-override': 'preCombatPlayerCurrentHpOverride',
    }
    arrays = {
        '--initial-enemy-current-hps-json': 'initialEnemyCurrentHps',
        '--initial-enemy-move-ids-json': 'initialEnemyMoveIds',
        '--cards-json': 'cards',
    }
    files = {
        '--cards-path': 'cards', '--run-cards-path': 'runCards',
        '--powers-path': 'powers', '--relics-path': 'relics', '--potions-path': 'potions',
    }
    switches = {
        '--clear-run-deck': 'clearRunDeck', '--clear-player-piles': 'clearPlayerPiles',
        '--preserve-native-combat-state-for-test': 'preserveNativeCombatStateForTest',
        '--mark-encounter-as-second-boss-for-test': 'markEncounterAsSecondBossForTest',
    }
    native_cards = json.loads((REPO / 'coverage/fixtures/runtime/performance-veryhigh-mecha-native.json').read_text())['runCards']
    for row in rows:
        if row['label'].startswith('full-'):
            continue  # same input already represented by the screening row
        args = row['command']
        request = {'schemaVersion': 1, 'scenarioId': 'VH16-' + row['label'], 'cards': []}
        for flag, key in scalar.items():
            if flag in args:
                value = args[args.index(flag) + 1]
                request[key] = int(value) if key not in ('characterId', 'seed', 'encounterId') else value
        for flag, key in arrays.items():
            if flag in args:
                request[key] = json.loads(args[args.index(flag) + 1])
        for flag, key in files.items():
            if flag in args:
                path = args[args.index(flag) + 1]
                request[key] = native_cards if path == '<extracted-mecha-run-cards.json>' else json.loads((REPO / path).read_text())
        for flag, key in switches.items():
            if flag in args:
                request[key] = True
        potion = args[args.index('--potion-policy-for-test') + 1] if '--potion-policy-for-test' in args else 'Smart'
        cases.append((row['label'], request, str(evidence.relative_to(REPO)) + '#' + row['label'], potion))

    # Additional old large-pile input, separate from the 2305-card stress case.
    cases.append(('silent-large-deck', {
        'schemaVersion': 1, 'scenarioId': 'VH16-SILENT-LARGE-DECK',
        'characterId': 'SILENT', 'seed': 'SEARCH_PERF_SILENT_LARGE_DECK',
        'encounterId': 'AEONGLASS_BOSS', 'ascension': 5, 'actIndexForTest': 2,
        'enemyCurrentHp': 512, 'initialEnemyMoveIds': ['EBB_MOVE'],
        'initialPlayerHp': 65, 'initialPlayerMaxHp': 65, 'initialPlayerEnergy': 3,
        'clearPlayerPiles': True,
        'cards': json.loads((REPO / 'coverage/fixtures/search/search-performance-silent-large-deck-cards.json').read_text()),
    }, 'docs/performance/PERFORMANCE_FIXTURES.md', 'Smart'))
    plan, manifest = [], []
    for label, request, source, potion in cases:
        request = dict(request)
        generated_hash = None
        if request.get('generatedScenarioPath'):
            spec = Path(request['generatedScenarioPath']).read_bytes()
            frozen_spec = requests / (label + '-scenario.json')
            frozen_spec.write_bytes(spec)
            request['generatedScenarioPath'] = str(frozen_spec)
            generated_hash = hashlib.sha256(spec).hexdigest()
        request_path = requests / (label + '.json')
        request_path.write_text(json.dumps(request, ensure_ascii=False, indent=2) + '\n')
        plan.append({
            'label': label + '-A1', 'request': str(request_path), 'profile': 'VeryHigh',
            'maxDegreeOfParallelism': 16, 'searchBudgetMilliseconds': 300000,
            'searchMode': 'Coordinator', 'usePortfolio': True, 'potionPolicy': potion,
            'productionBudget': True, 'enableNoGcRegion': True,
            'noGcRegionBudgetGigabytes': 16, 'dll': str(dll),
        })
        manifest.append({'id': label, 'source': source, 'potionPolicy': potion,
                         'requestSha256': hashlib.sha256(request_path.read_bytes()).hexdigest(),
                         'generatedSpecSha256': generated_hash})
    (output / 'plan.json').write_text(json.dumps(plan, indent=2) + '\n')
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return len(cases)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dll', type=Path, required=True)
    args = parser.parse_args()
    print(f'Prepared {prepare(args.output.resolve(), args.dll.resolve(strict=True))} cases.')
