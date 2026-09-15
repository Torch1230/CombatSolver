#!/usr/bin/env python3
"""Run the dedicated headless contract; never starts a visible Steam session."""
import argparse, json, subprocess
from pathlib import Path
p=argparse.ArgumentParser()
p.add_argument('--build', type=Path, required=True)
p.add_argument('--evidence', type=Path, required=True)
p.add_argument('--game-root', type=Path, required=True)
p.add_argument('--instance', default='choice-prototype-20260914')
p.add_argument('--card', choices=['dagger','acrobatics','prepared'], default='dagger')
a=p.parse_args()
metadata=json.loads((a.build.resolve()/'prototype-build.json').read_text())
if metadata.get('experimentalOnly') is not True or not metadata.get('sourceCommit', '').startswith('1ef4601'):
    raise SystemExit('A dedicated prototype build at 1ef4601 is required.')
root=Path(__file__).resolve().parents[2]
evidence=a.evidence.resolve()
evidence.mkdir(parents=True, exist_ok=False)
base=['bash','tools/run-unattended-test.sh','--headless-instance',a.instance,
      '--sts2-game-root',str(a.game_root.resolve())]
command=base+['--combat-solver-build-dir',str(a.build.resolve()),'--scenario-id',{'dagger':'CHOICE-PROTOTYPE','acrobatics':'CHOICE-PROTOTYPE-ACROBATICS','prepared':'CHOICE-PROTOTYPE-PREPARED'}[a.card],
    '--character-id','SILENT','--encounter-id','FUZZY_WURM_CRAWLER_WEAK','--enemy-current-hp','999',
    '--stop-after-combat-root-snapshot-assertion','--enable-no-gc-region-for-test','0',
    '--timeout-seconds','120','--evidence-directory',str(evidence),'--keep-game-open']
(evidence/'command.json').write_text(json.dumps(command, indent=2)+'\n')
try:
    subprocess.run(base+['--stop-instance'],cwd=root,check=True)
    subprocess.run(command,cwd=root,check=True)
    result=json.loads((evidence/'result.json').read_text())
    if result['status'] != 'Passed':
        raise RuntimeError('Prototype scenario did not pass.')
    checks=result['completedChecks']
    if a.card == 'dagger':
        if 'ChoicePrototype:NativeDaggerThrow:FullContinuation' not in checks:
            raise RuntimeError('Missing dedicated prototype/native checks; verify the selected build.')
        expected=['choice-prototype.json']
    else:
        card_id=a.card.upper()
        for upgrade in [0, 1]:
            if not any(c.startswith(f'ChoicePrototype:{card_id}+{upgrade}:') and c.endswith(':native-full-continuation:fixed-work-ABBA') for c in checks):
                raise RuntimeError('Missing dedicated priority-card/native checks; verify the selected build.')
        expected=[f'choice-prototype-{card_id}-{upgrade}.json' for upgrade in [0, 1]]
    for name in expected:
        measurement=json.loads((evidence/name).read_text())
        if not measurement.get('samples'):
            raise RuntimeError('Prototype measurement is missing.')
finally:
    subprocess.run(base+['--stop-instance'],cwd=root,check=True)
