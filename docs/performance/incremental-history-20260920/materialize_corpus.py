"""Materialize the recorded EQ/FULL/GA requests and a fixed-budget harness plan."""
import argparse
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--dll', type=Path, required=True)
parser.add_argument('--prefix', required=True)
args = parser.parse_args()
destination = args.out.resolve()
destination.mkdir(parents=True, exist_ok=True)
plan = []
for row in json.loads(Path(__file__).with_name('corpus.json').read_text()):
    root = row['root']
    spec = destination / f'{root}-spec.json'
    request = destination / f'{root}-request.json'
    spec.write_text(json.dumps(row['spec'], indent=2))
    request.write_text(json.dumps({**row['request'], 'generatedScenarioPath': str(spec)}, indent=2))
    plan.append(dict(label=f'{args.prefix}-{root}', request=str(request), dll=str(args.dll.resolve()),
        profile='High', beam=90, nodes=50000, maxCardBranchesPerNode=48,
        maxPileChoiceBranchesPerAction=28, maxHandChoiceBranchesPerAction=36,
        maxDegreeOfParallelism=1, searchBudgetMilliseconds=600000,
        searchMode='Coordinator', potionPolicy='Smart'))
(destination / 'plan.json').write_text(json.dumps(plan, indent=2))
