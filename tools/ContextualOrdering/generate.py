#!/usr/bin/env python3
"""Reproducible synthetic ordering corpus. No player saves or claimed optimal labels.

Related pressure variants always share a split. A seed changes native combat/RNG;
curated families also rotate draw order so held-out roots are not byte-identical.
The random-deck cohort independently checks all five characters and encounter kinds.
"""
import argparse
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CHARACTERS = ('IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER')
SPLITS = ('train', 'validation', 'test')
# Each family exposes a different reason why immediate weighted value can be misleading.
# Card effects remain native; no special rule is added to the solver for these fixtures.
FAMILIES = (
    ('attack_or_block', 'IRONCLAD', 'BASH STRIKE_IRONCLAD STRIKE_IRONCLAD DEFEND_IRONCLAD DEFEND_IRONCLAD SHRUG_IT_OFF POMMEL_STRIKE', 'STRIKE_IRONCLAD DEFEND_IRONCLAD SHRUG_IT_OFF POMMEL_STRIKE BASH'),
    ('energy_investment', 'IRONCLAD', 'BLOODLETTING OFFERING BASH POMMEL_STRIKE SHRUG_IT_OFF DEFEND_IRONCLAD STRIKE_IRONCLAD', 'POMMEL_STRIKE DEFEND_IRONCLAD STRIKE_IRONCLAD SHRUG_IT_OFF BASH'),
    ('strength_setup', 'IRONCLAD', 'INFLAME BASH STRIKE_IRONCLAD STRIKE_IRONCLAD POMMEL_STRIKE SHRUG_IT_OFF DEFEND_IRONCLAD', 'STRIKE_IRONCLAD POMMEL_STRIKE DEFEND_IRONCLAD SHRUG_IT_OFF STRIKE_IRONCLAD'),
    ('exhaust_resources', 'IRONCLAD', 'BURNING_PACT TRUE_GRIT SHRUG_IT_OFF POMMEL_STRIKE BASH STRIKE_IRONCLAD DEFEND_IRONCLAD', 'WOUND STRIKE_IRONCLAD DEFEND_IRONCLAD POMMEL_STRIKE SHRUG_IT_OFF'),
    ('poison_or_burst', 'SILENT', 'DEADLY_POISON BOUNCING_FLASK NEUTRALIZE BACKFLIP DEFEND_SILENT STRIKE_SILENT STRIKE_SILENT', 'DEADLY_POISON BACKFLIP DEFEND_SILENT STRIKE_SILENT SURVIVOR'),
    ('defense_engine', 'SILENT', 'AFTERIMAGE FOOTWORK BACKFLIP NEUTRALIZE DEFEND_SILENT STRIKE_SILENT SURVIVOR', 'STRIKE_SILENT DEFEND_SILENT BACKFLIP STRIKE_SILENT DEFEND_SILENT'),
    ('draw_before_spend', 'SILENT', 'ACROBATICS BACKFLIP PREPARED NEUTRALIZE STRIKE_SILENT DEFEND_SILENT SURVIVOR', 'STRIKE_SILENT DEADLY_POISON DEFEND_SILENT BACKFLIP STRIKE_SILENT'),
    ('orb_defense', 'DEFECT', 'BALL_LIGHTNING COLD_SNAP DEFEND_DEFECT DUALCAST ZAP GLACIER STRIKE_DEFECT', 'BALL_LIGHTNING DEFEND_DEFECT STRIKE_DEFECT COLD_SNAP DEFEND_DEFECT'),
    ('focus_investment', 'DEFECT', 'DEFRAGMENT BALL_LIGHTNING COLD_SNAP DUALCAST ZAP DEFEND_DEFECT STRIKE_DEFECT', 'BALL_LIGHTNING DEFEND_DEFECT STRIKE_DEFECT COLD_SNAP DEFEND_DEFECT'),
    ('target_order', 'IRONCLAD', 'WHIRLWIND BASH STRIKE_IRONCLAD POMMEL_STRIKE SHRUG_IT_OFF DEFEND_IRONCLAD DEFEND_IRONCLAD', 'WHIRLWIND STRIKE_IRONCLAD DEFEND_IRONCLAD POMMEL_STRIKE SHRUG_IT_OFF'),
)


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def generate(out):
    out.mkdir(parents=True, exist_ok=False)
    cases = []
    for index, split in enumerate(SPLITS):
        for family, character, hand, draw in FAMILIES:
            draw = draw.split()
            draw = draw[index:] + draw[:index]
            for pressure in ('low', 'high'):
                label = f'{family}-{split}-{pressure}'
                multi = family == 'target_order'
                enemy_hp = (40 if multi else 110) + index * 3
                request = {
                    'schemaVersion': 1, 'scenarioId': 'ORDERING-' + label.upper(),
                    'characterId': character,
                    'encounterId': 'CORPSE_SLUGS_NORMAL' if multi else 'FUZZY_WURM_CRAWLER_WEAK',
                    'seed': f'ORDERING-{family}-{index}-20260922',
                    'enemyCurrentHp': enemy_hp,
                    'initialEnemyMaxHps': [enemy_hp] * (3 if multi else 1),
                    'initialEnemyCurrentHps': [enemy_hp] * (3 if multi else 1),
                    'powers': [{'powerId': 'STRENGTH_POWER', 'target': 'Enemy', 'targetIndex': i, 'amount': 6}
                               for i in range(3 if multi else 1)] if pressure == 'high' else [],
                    'initialPlayerHp': 18 if pressure == 'high' else 65,
                    'initialPlayerMaxHp': 80, 'initialPlayerEnergy': 3,
                    'clearRunDeck': True, 'clearPlayerPiles': True,
                    'cards': [{'cardId': card, 'pile': pile, 'treatAsDeckCard': True}
                              for pile, cards in [('Hand', hand.split()), ('Draw', draw)] for card in cards],
                    'fixedSearchBudget': True, 'timeoutSeconds': 120,
                    'stopAfterInitialSolverResultAssertion': True,
                }
                path = out / 'requests' / (label + '.json')
                write(path, request)
                cases.append({'id': label, 'family': family, 'split': split, 'pressure': pressure,
                              'character': character, 'request': str(path.resolve()), 'kind': 'curated'})
        for character in CHARACTERS:
            for kind in ('Monster', 'Elite', 'Boss'):
                label = f'random-{character.lower()}-{kind.lower()}-{split}'
                scenario = {
                    'schemaVersion': 1, 'seed': f'ORDERING-{character}-{kind}-{index}-20260922',
                    'characterId': character, 'encounterKind': kind, 'ascension': 10, 'actIndex': 1,
                    'includeStartingDeck': True, 'includeStartingRelics': True,
                    'includeAscendersBane': True, 'applyRelicObtainEffects': False,
                    'characterCards': {'count': 12, 'ids': [], 'upgradeLevels': 1},
                    'colorlessCards': {'count': 2, 'ids': []}, 'relics': {'count': 3, 'ids': []},
                    'potions': {'count': 2, 'ids': []}, 'mode': 'Search', 'fixedSearchBudget': True,
                }
                scenario_path = out / 'scenarios' / (label + '.json')
                write(scenario_path, scenario)
                path = out / 'requests' / (label + '.json')
                write(path, {'schemaVersion': 1, 'scenarioId': 'ORDERING-' + label.upper(),
                             'generatedScenarioPath': str(scenario_path.resolve()),
                             'fixedSearchBudget': True, 'timeoutSeconds': 120})
                cases.append({'id': label, 'family': f'random-{character}-{kind}', 'split': split,
                              'character': character, 'request': str(path.resolve()), 'kind': 'random'})
    write(out / 'manifest.json', {'schemaVersion': 1, 'baselineCommit': '2a4b1a45',
                                 'description': 'Synthetic search observations; no optimality labels.', 'cases': cases})
    return cases


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    print(f'Generated {len(generate(args.out))} roots in {args.out}')
