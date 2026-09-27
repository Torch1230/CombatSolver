#!/usr/bin/env python3
"""Freeze five new native encounters/loadouts without running either search model.

Selection uses catalog order and structural overlap only, never search outcomes.
Failure/overlap aborts; do not replace a case based on how a model performs on it.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from dataset import audit, describe, read


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n')


def prepare(args):
    args.out.mkdir(parents=True, exist_ok=False)
    training = read(args.train)
    for case in training['cases']:
        if read(case['request']).get('generatedScenarioPath'):
            case['loadout'] = str((args.training_results / case['id'] / 'evidence' /
                                   'generated-scenario.loadout.json').resolve())
    write(args.out / 'training-manifest.json', training)
    used = {describe(c)['encounter'] for c in training['cases'] if c['split'] == 'train'}
    if args.exclude_manifest:
        used.update(describe(c)['encounter'] for c in read(args.exclude_manifest)['cases'])
    catalog = read(args.catalog)
    encounters = []
    for entry in sorted(catalog['encounters'], key=lambda e: (e['actIndex'], e['id'])):
        family = entry['id'].removesuffix('_NORMAL').removesuffix('_WEAK')
        if entry['roomType'] != args.kind or family in used:
            continue
        used.add(family)
        encounters.append(entry)
    if len(encounters) < 5:
        raise ValueError('Need five unseen native encounter families')
    cases = []
    for character, encounter in zip(sorted(catalog['characters']), encounters[:5], strict=True):
        identifier = f"unseen-{character.lower()}-{encounter['id'].lower()}"
        target = args.out / identifier
        target.mkdir()
        specification = {'schemaVersion': 1, 'seed': args.seed + '-' + character,
                         'characterId': character, 'encounterId': encounter['id'],
                         'encounterKind': encounter['roomType'], 'ascension': 10,
                         'actIndex': encounter['actIndex'], 'includeStartingDeck': True,
                         'includeStartingRelics': True, 'includeAscendersBane': True,
                         'applyRelicObtainEffects': False,
                         'characterCards': {'count': 12, 'ids': [], 'upgradeLevels': 1},
                         'colorlessCards': {'count': 2, 'ids': []},
                         'relics': {'count': 3, 'ids': []}, 'potions': {'count': 2, 'ids': []},
                         'mode': 'Search', 'fixedSearchBudget': True}
        write(target / 'specification.json', specification)
        request = {'schemaVersion': 1, 'scenarioId': identifier.upper(),
                   'generatedScenarioPath': str((target / 'specification.json').resolve()),
                   'timeoutSeconds': 120, 'fixedSearchBudget': True}
        write(target / 'request.json', request)
        command = ['dotnet', str(args.harness.resolve()), '--request', str((target / 'request.json').resolve()),
                   '--label', identifier, '--out', str((target / 'setup').resolve()), '--milestone', 'M1']
        write(target / 'setup-command.json', command)
        with (target / 'setup.log').open('w') as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=70, check=True)
        cases.append({'id': identifier, 'family': 'native-' + encounter['id'], 'split': args.split,
                      'request': str((target / 'request.json').resolve()),
                      'loadout': str((target / 'setup/evidence/generated-scenario.loadout.json').resolve())})
    evaluation = {'schemaVersion': 1, 'cases': cases}
    excluded = read(args.exclude_manifest)['cases'] if args.exclude_manifest else []
    report = audit(training, {'cases': [*excluded, *cases]})
    report['modelSha256'] = hashlib.sha256(args.model.read_bytes()).hexdigest()
    report['status'] = 'sealed: setup only; no baseline or candidate search executed'
    write(args.out / (args.split + '-manifest.json'), evaluation)
    write(args.out / 'separation.json', report)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ['train', 'training-results', 'catalog', 'harness', 'model', 'out']:
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--seed', required=True)
    parser.add_argument('--kind', choices=['Monster', 'Elite', 'Boss'], default='Monster')
    parser.add_argument('--split', choices=['validation', 'test'], default='test')
    parser.add_argument('--exclude-manifest', type=Path, help='Keep these already-frozen encounter families separate too')
    prepare(parser.parse_args())
