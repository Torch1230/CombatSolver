#!/usr/bin/env python3
"""Paired offline fixtures: identical cards/seed, only character or Pen Nib changes.

These are mechanism contracts, not an independent model-quality benchmark.
"""
import argparse
import json
from pathlib import Path


def generate(destination):
    destination.mkdir(parents=True, exist_ok=True)
    cases = []
    for character in ['IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER']:
        for counter in [None, 8, 9]:
            name = f'{character.lower()}-pen-nib-{counter if counter is not None else "absent"}'
            request = {
                'schemaVersion': 1, 'scenarioId': name.upper(), 'characterId': character,
                'encounterId': 'FUZZY_WURM_CRAWLER_WEAK', 'seed': 'OUTCOME-RELIC-PAIR',
                'enemyCurrentHp': 26, 'initialEnemyMaxHps': [26], 'initialEnemyCurrentHps': [26],
                'initialPlayerHp': 60, 'initialPlayerMaxHp': 80, 'initialPlayerEnergy': 2,
                'clearRunDeck': True, 'clearPlayerPiles': True,
                'cards': [{'cardId': card, 'pile': 'Hand', 'treatAsDeckCard': True}
                          for card in ['STRIKE_IRONCLAD', 'TWIN_STRIKE']],
                'combatRelics': [] if counter is None else [{
                    'relicId': 'PEN_NIB', 'addWithoutObtainedEffects': True,
                    'integerMembers': {'AttacksPlayed': counter}}],
                'fixedSearchBudget': True, 'timeoutSeconds': 120,
                'stopAfterInitialSolverResultAssertion': True,
            }
            path = destination / (name + '.json')
            path.write_text(json.dumps(request, indent=2) + '\n')
            cases.append({'id': name, 'request': str(path.resolve()), 'split': 'contract',
                          'character': character, 'counter': counter})
    (destination / 'manifest.json').write_text(json.dumps({'cases': cases}, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', required=True, type=Path)
    generate(parser.parse_args().out)
