#!/usr/bin/env python3
"""Build only in a clean disposable 1ef4601 worktree, restoring all injected source in finally."""
import argparse, json, shutil, subprocess
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--source', type=Path, required=True, help='Clean disposable worktree at 1ef4601 with local game references')
p.add_argument('--output', type=Path, required=True, help='New output directory')
a = p.parse_args()
source, output = a.source.resolve(), a.output.resolve()
here = Path(__file__).resolve().parent
if source == here.parents[1] or not (source / '.git').is_file():
    raise SystemExit('Use a separate disposable git worktree, never the working repository.')
head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip()
if not head.startswith('1ef4601'):
    raise SystemExit('This prototype is pinned to source 1ef4601; rebase and audit the patch before changing this guard.')
if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=source):
    raise SystemExit('Disposable source has tracked changes; refusing to overwrite them.')
if output.exists():
    raise SystemExit('Output must be new; keep prior measured binaries immutable.')
tracked = [
    'src/Engine/Common/PredictionStateStore.cs', 'src/Engine/Common/PredictionTrace.cs',
    'src/Engine/InCombat/Simulation/CombatPredictionHistory.cs',
    'src/Engine/InCombat/Simulation/CombatPredictionSimulator.cs',
    'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Card.cs',
    'src/Prediction/TurnStartChoiceSupport.cs', 'src/Testing/UnattendedTestRunner.Executor.cs',
]
created = [source / 'src/Testing' / ('ChoicePrototype.' + name) for name in ['Executor.cs', 'Contract.cs', 'Boundaries.cs', 'PriorityCards.cs']]
if any(path.exists() for path in created):
    raise SystemExit('Injected test sources already exist; refusing to overwrite them.')
lock = source / '.choice-prototype-build.lock'
with lock.open('x') as guard:
    guard.write('Do not edit or build this disposable worktree concurrently.\n')
saved = {path: (source / path).read_bytes() for path in tracked}
try:
    subprocess.run(['git', 'apply', '--check', str(here / 'engine.patch')], cwd=source, check=True)
    subprocess.run(['git', 'apply', str(here / 'engine.patch')], cwd=source, check=True)
    for path in created:
        shutil.copy2(here / path.name.removeprefix('ChoicePrototype.'), path)
    output.mkdir(parents=True)
    command = ['dotnet', 'build', 'CombatSolver.csproj', '-c', 'Release', '-p:CopyModOnBuild=false', '-o', str(output)]
    subprocess.run(command, cwd=source, check=True)
    shutil.copy2(source / 'CombatSolver.json', output / 'CombatSolver.json')
    (output / 'prototype-build.json').write_text(json.dumps({'sourceCommit': head, 'command': command,
        'experimentalOnly': True, 'defaultSearchIntegration': False}, indent=2) + '\n')
finally:
    for path, content in saved.items():
        (source / path).write_bytes(content)
    for path in created:
        path.unlink(missing_ok=True)
    lock.unlink()
