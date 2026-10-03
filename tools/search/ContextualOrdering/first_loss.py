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
    last_boundary = defaultdict(lambda: -1)
    with (args.baseline / 'ordering-observations.jsonl').open(encoding='utf-8') as observations:
        for line in observations:
            row = json.loads(line)
            o = row['observation']
            if o['stage'] not in ('GlobalRetention', 'RetentionPoolFinal'):
                continue
            key = (o['solverId'], o['boundaryId'])
            last_boundary[o['solverId']] = max(last_boundary[o['solverId']], o['boundaryId'])
            stage = 'global' if o['stage'] == 'GlobalRetention' else 'final'
            pools[key][stage][row['prefix']] = o
    rows = []
    for (solver_id, boundary), pool in pools.items():
        if boundary == last_boundary[solver_id] or not pool['final']:
            continue
        for prefix in pool['global'].keys() & positions.keys():
            o = pool['global'][prefix]
            if prefix in pool['final']:
                continue
            aliases = [f['policyLabel'] for f in pool['final'].values() if f['stateKey'] == o['stateKey']]
            retention = o.get('retention') or {}
            rows.append({'prefixIndex': positions[prefix], 'solverId': solver_id,
                         'boundary': boundary, 'prefix': prefix,
                         'stage': 'global_retention' if retention.get('selectedIndex') is None
                         else 'post_global_arbitration',
                         'stateAliasesRetained': aliases, 'candidate': o,
                         'selected': [{'prefix': p, 'policy': f['policyLabel'],
                                       'evaluation': pool['global'].get(p, {}).get('retention')}
                                      for p, f in pool['final'].items()]})
    rows.sort(key=lambda row: (row['prefixIndex'], row['solverId'], row['boundary']))
    result = {'meaning': 'observed_prefix_exclusion_not_proof_of_state_or_optimum_loss',
              'lastBoundaryIgnoredBySolver': dict(last_boundary),
              'routePrefixes': len(prefixes), 'exclusions': rows}
    with args.out.open('x') as stream:
        json.dump(result, stream, indent=2)
    for row in rows[:3]:
        o = row['candidate']
        r = o['retention']
        print(f"solver={row['solverId']} prefix={row['prefixIndex']} "
              f"boundary={row['boundary']} stage={row['stage']} rank={r.get('rawRank')} "
              f"required={r.get('requiredIndex')} route={r.get('routingIndex')} "
              f"aliases={len(row['stateAliasesRetained'])} score={r.get('beamRankScore')}")
    print(f'{len(rows)} observed exclusions; unobserved prefixes remain unknown.')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--baseline', type=Path, required=True)
    p.add_argument('--witness', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    run(p.parse_args())
