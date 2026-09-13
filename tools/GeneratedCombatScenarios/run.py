#!/usr/bin/env python3
"""Run a reproducible generated suite through the existing native unattended launcher."""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=Path(__file__).with_name('random.json'))
    parser.add_argument('--count', type=int, default=1)
    parser.add_argument('--seed', help='Override base seed; suites append -0000, -0001, ...')
    parser.add_argument('--mode', choices=['Setup', 'Search', 'Deploy'])
    parser.add_argument('--output', type=Path, required=True, help='New directory for all inputs/results')
    parser.add_argument('--build', type=Path, help='Frozen DLL and manifest directory')
    parser.add_argument('--instance', help='Dedicated instance owned and stopped by this suite')
    parser.add_argument('--timeout-seconds', type=int, default=120)
    parser.add_argument('--continue-on-failure', action='store_true')
    parser.add_argument('--write-inputs-only', action='store_true', help='Write unresolved inputs without starting a game')
    parser.add_argument('launcher_args', nargs=argparse.REMAINDER, help='Native launcher options after --')
    args = parser.parse_args()
    if not 1 <= args.count <= 1000 or not 1 <= args.timeout_seconds <= 120:
        parser.error('count must be 1..1000 and timeout-seconds 1..120')
    config = json.loads(args.config.read_text(encoding='utf-8-sig'))
    base_seed = args.seed or config.get('seed', 'GENERATED-COMBAT-001')
    if not isinstance(base_seed, str) or not base_seed.strip():
        parser.error('seed must be a nonempty string')
    extra = args.launcher_args[1:] if args.launcher_args[:1] == ['--'] else args.launcher_args
    # These flags define process/evidence ownership and must not be shadowed by passthrough.
    forbidden = {'headlessinstance', 'generatedscenariopath', 'evidencedirectory', 'scenarioid',
                 'timeoutseconds', 'keepgameopen', 'exitoncomplete', 'stopinstance', 'holdafterinitialsearch',
                 'combatsolverbuilddir'}
    for argument in extra:
        key = argument.split('=', 1)[0].split(':', 1)[0].replace('-', '').lower()
        if key in forbidden:
            parser.error('suite-owned option cannot be passed through: ' + argument)
    root = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    instance = args.instance or ('generated-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d%H%M%S') + '-' + str(os.getpid()))
    windows = sys.platform == 'win32'
    launcher = (['pwsh', '-NoProfile', '-File', str(root/'tools/run-unattended-test.ps1')]
                if windows else ['bash', str(root/'tools/run-unattended-test.sh')])
    def flag(name):
        return '-' + ''.join(part.capitalize() for part in name.split('-')) if windows else '--' + name
    stop = launcher + [flag('headless-instance'), instance, flag('stop-instance')]
    manifest = []
    for index in range(args.count):
        case = output/f'{index:04d}'
        case.mkdir()
        spec = dict(config)
        spec['seed'] = base_seed if args.count == 1 else f'{base_seed}-{index:04d}'
        if args.mode:
            spec['mode'] = args.mode
        input_path = case/'input.json'
        input_path.write_text(json.dumps(spec, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
        command = launcher + [flag('generated-scenario-path'), str(input_path),
            flag('evidence-directory'), str(case), flag('scenario-id'), f'GENERATED-{index:04d}',
            flag('headless-instance'), instance, flag('timeout-seconds'), str(args.timeout_seconds),
            flag('keep-game-open')]
        if args.build:
            command += [flag('combat-solver-build-dir'), str(args.build.resolve())]
        command += extra
        (case/'command.json').write_text(json.dumps(command, indent=2)+'\n', encoding='utf-8')
        manifest.append(dict(index=index, seed=spec['seed'], input=str(input_path), command=command))
    (output/'suite.json').write_text(json.dumps(dict(schemaVersion=1, instance=instance, cases=manifest), indent=2)+'\n', encoding='utf-8')
    if args.write_inputs_only:
        print(f'Wrote {len(manifest)} unresolved inputs to {output}; no game or model resolution performed.')
        return 0
    failed = False
    try:
        subprocess.run(stop, cwd=root, check=True, stdout=subprocess.DEVNULL)
        for case in manifest:
            directory = Path(case['input']).parent
            with (directory/'launcher.log').open('w', encoding='utf-8') as log:
                execution = subprocess.run(case['command'], cwd=root, stdout=log, stderr=subprocess.STDOUT)
            result_path = directory/'result.json'
            result = json.loads(result_path.read_text(encoding='utf-8-sig')) if result_path.exists() else {}
            passed = execution.returncode == 0 and result.get('status') == 'Passed'
            row = dict(index=case['index'], seed=case['seed'], status='Passed' if passed else 'Failed',
                runId=result.get('runId'), characterId=result.get('characterId'), encounterId=result.get('encounterId'),
                stage=result.get('stage'), error=result.get('error'), elapsedMilliseconds=result.get('elapsedMilliseconds'),
                solverMetrics=result.get('solverMetrics'), evidence=str(directory), launcherExitCode=execution.returncode)
            with (output/'results.jsonl').open('a', encoding='utf-8') as results:
                results.write(json.dumps(row, ensure_ascii=False)+'\n')
            print(f"{case['index']+1}/{len(manifest)} {row['status']} seed={case['seed']} evidence={directory}", flush=True)
            if not passed:
                failed = True
                if not args.continue_on_failure:
                    break
                subprocess.run(stop, cwd=root, check=True, stdout=subprocess.DEVNULL)
    finally:
        subprocess.run(stop, cwd=root, check=True, stdout=subprocess.DEVNULL)
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
