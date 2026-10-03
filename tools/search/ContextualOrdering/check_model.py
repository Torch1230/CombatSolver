#!/usr/bin/env python3
"""Reproduce runtime model contracts and feature parity on traces plus edge cases."""
import argparse
import json
from pathlib import Path
import random
import subprocess
from features import capture


def run(args):
    randomizer = random.Random(20260922)
    samples = []
    for path in sorted(args.runs.glob('*/baseline/ordering-observations.jsonl')):
        for index, line in enumerate(path.open()):
            if index % 100 == 0:
                observation = json.loads(line)['observation']
                if observation['stage'] != 'GlobalRetention':
                    continue
                samples.append({'observation': observation, 'expected': capture(observation)})
    if not samples:
        raise ValueError('No baseline observations')
    for index in range(100):
        observation = json.loads(json.dumps(samples[index % len(samples)]['observation']))
        for key in ('playerHp', 'playerMaxHp', 'enemyHp', 'turn'):
            observation[key] = randomizer.randrange(0, 2000)
        for key in observation['retention']['evaluation']:
            observation['retention']['evaluation'][key] = randomizer.randrange(-10, 2000)
        samples.append({'observation': observation, 'expected': capture(observation)})
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / 'input.json').write_text(json.dumps(samples))
    subprocess.run(['dotnet', str(args.harness.resolve()), '--check-ranking',
                    str(args.out / 'input.json'), str(args.out / 'result.json')], check=True)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--runs', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--harness', type=Path, required=True)
    run(p.parse_args())
