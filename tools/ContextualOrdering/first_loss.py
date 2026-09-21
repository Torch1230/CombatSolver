#!/usr/bin/env python3
"""Locate observed exclusions of a better witnessed route in outer prune pools.

Prefix exclusion alone does not prove loss of a state/optimal value. Reports retain
same-state aliases and policy labels. The last observed boundary is conservatively
ignored because a capped trace can end halfway through a pool.
"""
import argparse
from collections import defaultdict
import json
from pathlib import Path


def run(args):
    prefixes = json.loads((args.witness / 'ordering-selected-prefixes.json').read_text())
    positions = {prefix: i for i, prefix in enumerate(prefixes)}
    pools = defaultdict(lambda: {'global': {}, 'final': {}})
    last_boundary = -1
    for line in (args.baseline / 'ordering-observations.jsonl').open():
        row = json.loads(line)
        o = row['observation']
        if o['stage'] not in ('GlobalRetention', 'RetentionPoolFinal'):
            continue
        boundary = o['boundaryId']
        last_boundary = max(last_boundary, boundary)
        stage = 'global' if o['stage'] == 'GlobalRetention' else 'final'
        pools[boundary][stage][row['prefix']] = o
    rows = []
    for boundary, pool in pools.items():
        if boundary == last_boundary or not pool['final']:
            continue
        for prefix in pool['global'].keys() & positions.keys():
            o = pool['global'][prefix]
            if prefix in pool['final']:
                continue
            aliases = [f['policyLabel'] for f in pool['final'].values() if f['stateKey'] == o['stateKey']]
            rows.append({'prefixIndex': positions[prefix], 'boundary': boundary, 'prefix': prefix,
                         'stateAliasesRetained': aliases, 'candidate': o,
                         'selected': [{'prefix': p, 'policy': f['policyLabel'],
                                       'evaluation': pool['global'].get(p, {}).get('retention')}
                                      for p, f in pool['final'].items()]})
    rows.sort(key=lambda row: (row['prefixIndex'], row['boundary']))
    result = {'meaning': 'observed_prefix_exclusion_not_proof_of_state_or_optimum_loss',
              'lastBoundaryIgnored': last_boundary, 'routePrefixes': len(prefixes), 'exclusions': rows}
    with args.out.open('x') as stream:
        json.dump(result, stream, indent=2)
    for row in rows[:3]:
        o = row['candidate']
        r = o['retention']
        print(f"prefix={row['prefixIndex']} boundary={row['boundary']} rank={r.get('rawRank')} "
              f"required={r.get('requiredIndex')} route={r.get('routingIndex')} "
              f"aliases={len(row['stateAliasesRetained'])} score={r.get('beamRankScore')}")
    print(f'{len(rows)} observed exclusions; unobserved prefixes remain unknown.')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--witness', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    run(p.parse_args())
