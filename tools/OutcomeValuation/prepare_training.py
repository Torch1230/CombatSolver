#!/usr/bin/env python3
"""Prepare a role-balanced corpus without selecting scenes by search results.

Native setup is part of data collection: its elapsed cost is carried into train.py.
All excluded validation/test manifests are structurally audited after setup.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from dataset import audit, describe, read


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def select_encounters(catalog, excluded):
    selected = []
    used = set(excluded)
    for kind, count in [('Monster', 4), ('Elite', 3), ('Boss', 1)]:
        matches = []
        for entry in sorted(catalog['encounters'], key=lambda e: (e['actIndex'], e['id'])):
            family = entry['id'].removesuffix('_NORMAL').removesuffix('_WEAK')
            if entry['roomType'] != kind or family in used:
                continue
            matches.append(entry)
            used.add(family)
            if len(matches) == count:
                break
        if len(matches) != count:
            raise ValueError(f'Insufficient non-overlapping {kind} encounters')
        selected.extend(matches)
    return selected


def prepare(args):
    started = time.monotonic()
    args.out.mkdir(parents=True, exist_ok=False)
    catalog = read(args.catalog)
    excluded = [read(path) for path in args.exclude_manifest]
    used = {describe(c)['encounter'] for manifest in excluded for c in manifest['cases']}
    encounters = select_encounters(catalog, used)
    environment = dict(os.environ, OFFLINE_HARNESS_COMBATSOLVER_DLL=str(args.mod.resolve()))
    cases = []
    budget = {'completed': False, 'setupOnly': True,
              'modSha256': hashlib.sha256(args.mod.read_bytes()).hexdigest(),
              'harnessSha256': hashlib.sha256(args.harness.read_bytes()).hexdigest(),
              'seed': args.seed, 'limitSeconds': args.seconds}
    try:
        # Interleave roles so a failed collection cannot silently look balanced.
        for encounter in encounters:
            for character in sorted(catalog['characters']):
                identifier = f"balanced-{character.lower()}-{encounter['id'].lower()}"
                target = args.out / identifier
                target.mkdir()
                specification = {'schemaVersion': 1, 'seed': args.seed + '-' + identifier,
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
                write(target / 'request.json', {'schemaVersion': 1, 'scenarioId': identifier.upper(),
                    'generatedScenarioPath': str((target / 'specification.json').resolve()),
                    'timeoutSeconds': 120, 'fixedSearchBudget': True})
                command = ['dotnet', str(args.harness.resolve()), '--request', str((target / 'request.json').resolve()),
                           '--label', identifier, '--out', str((target / 'setup').resolve()), '--milestone', 'M1']
                write(target / 'command.json', command)
                remaining = args.seconds - (time.monotonic() - started)
                if remaining <= 0:
                    raise TimeoutError('Native preparation deadline reached')
                with (target / 'setup.log').open('w') as log:
                    subprocess.run(command, stdout=log, stderr=subprocess.STDOUT,
                                   timeout=min(70, remaining), check=True, env=environment)
                cases.append({'id': identifier, 'character': character, 'family': 'native-' + encounter['id'],
                    'split': 'train', 'request': str((target / 'request.json').resolve()),
                    'loadout': str((target / 'setup/evidence/generated-scenario.loadout.json').resolve())})
                print(identifier, flush=True)
        manifest = {'schemaVersion': 1, 'cases': cases}
        write(args.out / 'training-manifest.json', manifest)
        write(args.out / 'separation.json', [audit(manifest, heldout) for heldout in excluded])
        budget['manifestSha256'] = hashlib.sha256((args.out / 'training-manifest.json').read_bytes()).hexdigest()
        budget['completed'] = True
    finally:
        budget['elapsedSeconds'] = time.monotonic() - started
        budget['preparedRoots'] = len(cases)
        write(args.out / 'preparation-budget.json', budget)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('catalog', 'harness', 'mod', 'out'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--exclude-manifest', type=Path, action='append', required=True)
    parser.add_argument('--seed', required=True)
    parser.add_argument('--seconds', type=int, default=240)
    options = parser.parse_args()
    if not 1 <= options.seconds <= 1800:
        parser.error('Preparation budget must be between 1 and 1800 seconds')
    prepare(options)
