#!/usr/bin/env python3
"""Audit scenario separation before collecting/fitting or opening an evaluation set.

Seed, health, pile order, upgrades and relic counters do not define a new scenario.
Generated cases need their native resolved loadout; generator seeds are insufficient.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path


def read(path):
    return json.loads(Path(path).read_text())


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def verify_resolved_loadout(case, output):
    """Prove the search actually used the native equipment audited before it ran."""
    if case.get('loadout'):
        actual = read(Path(output) / 'evidence/generated-scenario.loadout.json')
        if actual != read(case['loadout']):
            raise ValueError(f"{case['id']}: search loadout differs from the frozen native setup")


def describe(case):
    request = read(case['request'])
    if request.get('generatedScenarioPath'):
        if not case.get('loadout'):
            raise ValueError(f"{case['id']}: generated case requires a native resolved loadout")
        loadout = read(case['loadout'])
        specification = read(request['generatedScenarioPath'])
        if loadout['seed'] != specification['seed'] or loadout['characterId'] != specification['characterId']:
            raise ValueError(f"{case['id']}: loadout does not match generator seed/character")
        if specification.get('encounterId') and loadout['encounterId'] != specification['encounterId']:
            raise ValueError(f"{case['id']}: loadout does not match fixed encounter")
        character, encounter = loadout['characterId'], loadout['encounterId']
        cards = Counter(c['id'] for c in loadout['deck'])
        relics = sorted(loadout['relics'])
        inputs = [request, specification, loadout]
    else:
        character, encounter = request['characterId'], request['encounterId']
        if not request.get('clearRunDeck') or not request.get('clearPlayerPiles'):
            raise ValueError(f"{case['id']}: implicit deck requires a resolved loadout")
        cards = Counter(c['cardId'] for c in request['cards'])
        relics = sorted(r['relicId'] for r in request.get('combatRelics', []))
        inputs = [request]
    if not cards or not case.get('family'):
        raise ValueError(f"{case['id']}: missing scenario family or deck")
    # Native weak/normal variants remain the same encounter family.
    for suffix in ('_NORMAL', '_WEAK'):
        if encounter.endswith(suffix):
            encounter = encounter[:-len(suffix)]
            break
    return {'id': case['id'], 'family': case['family'], 'character': character,
            'encounter': encounter, 'cards': dict(sorted(cards.items())), 'relics': relics,
            'inputSha256': digest(inputs)}


def audit(training, evaluation):
    train = [describe(c) for c in training['cases'] if c['split'] == 'train']
    heldout = [describe(c) for c in evaluation['cases']]
    if not train or not heldout or any(c['split'] not in ('validation', 'test') for c in evaluation['cases']):
        raise ValueError('Require nonempty train and validation/test groups')
    overlaps = []
    maximum_similarity = 0.0
    for a in train:
        for b in heldout:
            reasons = []
            if a['family'] == b['family']:
                reasons.append('same template family')
            if a['encounter'] == b['encounter']:
                reasons.append('same encounter family')
            if a['character'] == b['character']:
                left, right = Counter(a['cards']), Counter(b['cards'])
                similarity = sum((left & right).values()) / sum((left | right).values())
                maximum_similarity = max(maximum_similarity, similarity)
                # This is a conservative dataset gate, not a statistical independence proof.
                if similarity >= 0.85:
                    reasons.append('same or near-duplicate deck (multiset Jaccard >= 0.85)')
            if reasons:
                overlaps.append({'train': a['id'], 'evaluation': b['id'], 'reasons': reasons})
    if overlaps:
        raise ValueError('Scenario leakage: ' + json.dumps(overlaps, ensure_ascii=False))
    return {'schema': 1, 'training': train, 'evaluation': heldout,
            'maximumDeckSimilarity': maximum_similarity, 'passed': True,
            'datasetSha256': digest([train, heldout])}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--train', type=Path, required=True)
    parser.add_argument('--evaluation', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    report = audit(read(args.train), read(args.evaluation))
    args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
