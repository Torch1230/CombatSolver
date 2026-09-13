#!/usr/bin/env python3
"""Run one bounded census request using an existing, reviewed unattended command template."""
import argparse
import json
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument('--template', type=Path, required=True)
p.add_argument('--template-root', type=Path, required=True)
p.add_argument('--build', type=Path, required=True)
p.add_argument('--evidence', type=Path, required=True)
p.add_argument('--instance', required=True)
a = p.parse_args()
root, out = a.template_root.resolve(), a.evidence.resolve()
out.mkdir(parents=True, exist_ok=False)
cmd = json.loads(a.template.read_text())
cmd[0] = str(root / cmd[0])
assert '--timeout-seconds' in cmd and int(cmd[cmd.index('--timeout-seconds') + 1]) <= 120
for flag in ['--checkpoint-archive-path', '--replay-policy-override-path', '--sts2-game-root']:
    if flag in cmd:
        i = cmd.index(flag) + 1
        if not Path(cmd[i]).is_absolute(): cmd[i] = str(root / cmd[i])
for flag, value in [('--combat-solver-build-dir', str(a.build.resolve())),
                    ('--evidence-directory', str(out)),
                    ('--headless-instance', a.instance),
                    ('--scenario-id', 'CHOICE-CONTINUATION-CENSUS')]:
    cmd[cmd.index(flag) + 1] = value
(out/'command.json').write_text(json.dumps(cmd, indent=2) + '\n')
stop = [cmd[0], '--headless-instance', a.instance, '--stop-instance']
subprocess.run(stop, check=True, stdout=subprocess.DEVNULL)
try:
    with (out/'launcher.log').open('w') as log:
        run = subprocess.run(cmd, cwd=root, stdout=log, stderr=subprocess.STDOUT)
    result = json.loads((out/'result.json').read_text()) if (out/'result.json').exists() else {}
    print(json.dumps({key: result.get(key) for key in ['runId', 'status', 'error', 'solverMetrics']}, ensure_ascii=False), flush=True)
    if run.returncode or result.get('status') != 'Passed': raise RuntimeError('Census request did not pass')
finally:
    subprocess.run(stop, check=True, stdout=subprocess.DEVNULL)
