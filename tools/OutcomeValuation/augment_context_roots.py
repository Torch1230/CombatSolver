#!/usr/bin/env python3
"""Add eight training-only relic interventions to ContextualOrdering's manifest.

Validation/test cases remain byte-for-byte references to their original roots.
This is a focused intervention set, not exhaustive relic/card coverage.
"""
import argparse
import json
from pathlib import Path


def augment(manifest_path, destination):
    destination.mkdir(parents=True, exist_ok=False)
    manifest = json.loads(manifest_path.read_text())
    original = list(manifest['cases'])
    interventions = [
        ('attack_or_block', 'PEN_NIB', 'AttacksPlayed'),
        ('energy_investment', 'NUNCHAKU', 'AttacksPlayed'),
        ('defense_engine', 'TUNING_FORK', 'SkillsPlayed'),
        ('poison_or_burst', 'SNECKO_SKULL', None),
        ('focus_investment', 'DATA_DISK', None),
    ]
    for family, relic, member in interventions:
        case = next(c for c in original if c['id'] == family + '-train-high')
        for counter in ([0, 9] if member else [0]):
            request = json.loads(Path(case['request']).read_text())
            injection = {'relicId': relic, 'addWithoutObtainedEffects': True}
            if member:
                injection['integerMembers'] = {member: counter}
            request['combatRelics'] = [injection]
            identifier = case['id'] + '-' + relic.lower() + '-' + str(counter)
            path = destination / (identifier + '.json')
            path.write_text(json.dumps(request, indent=2) + '\n')
            manifest['cases'].append(dict(case, id=identifier, request=str(path.resolve())))
    (destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    augment(args.manifest, args.out)
