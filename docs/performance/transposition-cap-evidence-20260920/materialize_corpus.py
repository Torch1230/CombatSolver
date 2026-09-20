"""Materialize EQ/FULL/GA inputs for fixed comparison or the three VeryHigh probes."""
import argparse
import json
from pathlib import Path

HEAVY_ROOTS = {'FULL-REGENT-ELITE-00', 'FULL-SILENT-ELITE-03', 'FULL-REGENT-BOSS-00'}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--dll', type=Path, required=True)
parser.add_argument('--prefix', required=True)
parser.add_argument('--heavy', action='store_true')
parser.add_argument('--root', choices=sorted(HEAVY_ROOTS))
parser.add_argument('--transposition-entry-limit', type=int)
args = parser.parse_args()
if args.root and not args.heavy:
    parser.error('--root requires --heavy')
if args.transposition_entry_limit is not None and args.transposition_entry_limit <= 0:
    parser.error('Use a positive cap; omit the option for the production default.')
destination = args.out.resolve()
destination.mkdir(parents=True, exist_ok=True)
plan = []
for row in json.loads(Path(__file__).with_name('corpus.json').read_text()):
    root = row['root']
    if args.heavy and root not in HEAVY_ROOTS:
        continue
    if args.root and root != args.root:
        continue
    spec = destination / f'{root}-spec.json'
    request = destination / f'{root}-request.json'
    spec.write_text(json.dumps(row['spec'], indent=2))
    request.write_text(json.dumps({**row['request'], 'generatedScenarioPath': str(spec)}, indent=2))
    item = dict(label=f'{args.prefix}-{root}', request=str(request), dll=str(args.dll.resolve()),
        profile='VeryHigh' if args.heavy else 'High',
        maxDegreeOfParallelism=1, searchBudgetMilliseconds=300000 if args.heavy else 600000,
        searchMode='Coordinator', potionPolicy='Smart')
    if args.heavy:
        item['productionBudget'] = True
    else:
        item.update(beam=90, nodes=50000, maxCardBranchesPerNode=48,
            maxPileChoiceBranchesPerAction=28, maxHandChoiceBranchesPerAction=36)
    if args.transposition_entry_limit is not None:
        item['transpositionEntryLimit'] = args.transposition_entry_limit
    plan.append(item)
(destination / 'plan.json').write_text(json.dumps(plan, indent=2))
