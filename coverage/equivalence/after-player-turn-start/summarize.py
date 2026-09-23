#!/usr/bin/env python3
"""保留确定性比较摘要与原始产物哈希，不提交两份相同路线。"""
import argparse
import hashlib
import json
from pathlib import Path

folder=Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('workspace',type=Path);args=p.parse_args()
w=args.workspace
report=json.loads((w/'comparison.json').read_text())
assert report['roots']==60 and not report['leftOnly'] and not report['rightOnly'] and not report['mismatchedRoots']
def sha(data):return hashlib.sha256(data).hexdigest()
rows=[]
for c in report['details']:
    assert not c['mismatches']
    pair={}
    for side in ['base','new']:
        d=w/side/'runs'/(side+'-'+c['root']);raw=(d/'result.json').read_bytes();r=json.loads(raw);b=r['budget']
        assert r['status']=='Passed' and r['valid'] and not r['timeBoundaryObserved']
        assert [b[k] for k in ['beamWidth','maxExpandedNodes','maxCardBranchesPerNode','maxPileChoiceBranchesPerAction','maxHandChoiceBranchesPerAction','maxDegreeOfParallelism']]==[90,250000,48,28,36,1]
        assert [b[k] for k in ['profile','searchMode','potionPolicy']]==['High','Coordinator','Smart']
        assert b['budgetMilliseconds']==600000 and b['fixedSearchBudget'] and not b['usePortfolio']
        pair[side]=dict(resultSha256=sha(raw),routeSha256=sha((d/'route.json').read_bytes()),
            totalExpanded=r['solverMetrics']['totalExpanded'],totalTransitions=r['solverMetrics']['totalTransitions'])
    rows.append(dict(root=c['root'],comparedFields=c['compared'],mismatches=[],outputs=pair,
        comparedGroupsSha256=sha(json.dumps(c['groups'],ensure_ascii=False,sort_keys=True,separators=(',',':')).encode())))
inputs=json.loads((w/'inputs.json').read_text())
metadata=dict(baseCommit='523aea571f109426a3c7fb015207b863df78548b',implementationCommit='641f28b8',
    profile='High',beam=90,nodes=250000,branches=[48,28,36],searchMode='Coordinator',potionPolicy='Smart',dop=1,
    workersPerSide=1,searchBudgetMilliseconds=600000,usePortfolio=False,roots=60,comparedFields=report['comparedFields'],mismatchedRoots=0,
    groups={g:dict(roots=sum(r['root'].startswith(g+'-') for r in rows),comparedFields=sum(r['comparedFields'] for r in rows if r['root'].startswith(g+'-'))) for g in ['EQ','FULL','GA']},
    dllSha256={side:inputs['dllHashes'][json.loads((w/(side+'-plan.json')).read_text())[0]['dll']] for side in ['base','new']},
    harnessSha256=next(v for k,v in inputs['dllHashes'].items() if k.endswith('/OfflineSearchHarness.dll')),
    corpusHashes=inputs['corpusHashes'])
for n,data in [('metadata.json',metadata),('results-summary.json',rows)]:
    (folder/n).write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(metadata['groups'],ensure_ascii=False))
