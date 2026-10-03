#!/usr/bin/env python3
"""Check coverage placement, data syntax and committed material references without starting the game."""
from pathlib import Path
from collections import Counter
import json
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
DIRECTORIES = {'catalog', 'evidence', 'fixtures', 'corpora', 'archive'}
REFERENCES = re.compile(r'coverage/(?:catalog|evidence|fixtures|corpora|archive)/[A-Za-z0-9_./-]+\.(?:json|save)')
RETIRED = re.compile(r'coverage/(?:unattended|equivalence|novelty-search|runtime-gc-profile|strategy-refactor-p[02])(?:/|\b)')


def strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for item in value.values():
            yield from strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings(item)


def main():
    listed = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT).decode().split('\0')
    names = sorted({n for n in listed if n and (ROOT/n).is_file()})
    errors = []
    for path in (ROOT/'coverage').iterdir():
        if path.is_dir() and path.name not in DIRECTORIES:
            errors.append('Unexpected coverage directory: '+path.name)
        if path.is_file() and path.name != 'README.md':
            errors.append('Loose coverage file: '+path.name)
    materials = {}
    for name in names:
        path = ROOT/name
        if not name.startswith('coverage/'):
            continue
        if path.suffix not in {'.json', '.save', '.md'}:
            errors.append('Coverage contains an executable or unsupported material: '+name)
        if path.name.startswith('temp-') and not name.startswith('coverage/archive/'):
            errors.append('Temporary input belongs in .local: '+name)
        if path.suffix in {'.json', '.save'}:
            try:
                materials[name] = json.loads(path.read_text(encoding='utf-8-sig'))
            except (ValueError, UnicodeError) as error:
                errors.append(name+': '+str(error))
    for name in ['coverage/catalog/classifications.json', 'coverage/evidence/test-evidence.json']:
        if not isinstance(materials.get(name), dict):
            errors.append('Required coverage registration is missing: '+name)
    for name, value in materials.items():
        for text in strings(value):
            text = re.sub(r'https?://[^\s)<>"`]+', '', text)
            for match in REFERENCES.finditer(text):
                if not (ROOT/match[0]).is_file():
                    errors.append(name+': missing material '+match[0])
    # Fixed URLs intentionally refer to a historical tree. Raw text references
    # in current guides, tools and skills must resolve in the current repository.
    for name in names:
        path = ROOT/name
        if path.suffix not in {'.md', '.json', '.cs', '.py', '.ps1', '.sh', '.yml', '.props', '.csproj'}:
            continue
        text = path.read_text(encoding='utf-8-sig')
        text = re.sub(r'https?://[^\s)<>"`]+', '', text)
        if RETIRED.search(text):
            errors.append(name+': retired coverage path')
        for match in REFERENCES.finditer(text):
            if not (ROOT/match[0]).is_file():
                errors.append(name+': missing reference '+match[0])
    groups = Counter(Path(name).parent.as_posix() for name in materials)
    fixtures = sum(name.startswith('coverage/fixtures/') for name in materials)
    for error in sorted(set(errors)):
        print(error)
    print(f'COVERAGE_{"FAILED" if errors else "OK"} data={len(materials)} fixtures={fixtures} groups={len(groups)} errors={len(set(errors))}')
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
