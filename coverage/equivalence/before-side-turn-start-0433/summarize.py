#!/usr/bin/env python3
"""保存可复核的紧凑等价证据；完整双侧结果与比较明细留在批次工作区。"""
import argparse
import hashlib
import json
from pathlib import Path

folder=Path(__file__).resolve().parent
p=argparse.ArgumentParser(); p.add_argument('workspace',type=Path); args=p.parse_args()
workspace=args.workspace
report=json.loads((workspace/'comparison.json').read_text())
assert report['roots']==60 and not report['leftOnly'] and not report['rightOnly'] and not report['mismatchedRoots']
def digest(data): return hashlib.sha256(data).hexdigest()
rows=[]
for comparison in report['details']:
    name=comparison['root']; pair={}
    for side in ('base','new'):
        path=workspace/side/'runs'/(side+'-'+name)
        raw=(path/'result.json').read_bytes()
        result=json.loads(raw)
        assert result['status']=='Passed' and not result['timeBoundaryObserved'],name
        budget=result['budget']
        assert [budget[k] for k in ['beamWidth','maxExpandedNodes','maxCardBranchesPerNode','maxPileChoiceBranchesPerAction',
            'maxHandChoiceBranchesPerAction','maxDegreeOfParallelism']]==[90,250000,48,28,36,1]
        assert budget['profile']=='High' and budget['searchMode']=='Coordinator' and budget['potionPolicy']=='Smart'
        pair[side]=dict(resultSha256=digest(raw),routeSha256=digest((path/'route.json').read_bytes()),
            totalExpanded=result['solverMetrics']['totalExpanded'],totalTransitions=result['solverMetrics']['totalTransitions'])
    assert not comparison['mismatches']
    rows.append(dict(root=name,comparedFields=comparison['compared'],mismatches=[],outputs=pair,
        comparedGroupsSha256=digest(json.dumps(comparison['groups'],ensure_ascii=False,sort_keys=True,separators=(',',':')).encode())))
inputs=json.loads((workspace/'inputs.json').read_text())
metadata=dict(baseCommit='6922828d8c40011eb5de3a6bf392327bbdb7828e',implementationCommit='0584c4a2',
    profile='High',beam=90,nodes=250000,branches=[48,28,36],searchMode='Coordinator',potionPolicy='Smart',dop=1,
    workersPerSide=1,searchBudgetMilliseconds=600000,usePortfolio=False,
    roots=60,comparedFields=report['comparedFields'],mismatchedRoots=0,
    groups={g:dict(roots=sum(r['root'].startswith(g+'-') for r in rows),
        comparedFields=sum(r['comparedFields'] for r in rows if r['root'].startswith(g+'-'))) for g in ['EQ','FULL','GA']},
    note='原始 result/route 与完整 comparison 留在运行工作区；本文件记录其 SHA256 和比较器确定性字段摘要。')
# 两个 CombatSolver.dll 位于同名 Release 目录，单独用明确身份保留，不靠路径尾部去重。
metadata['dllSha256']={
    side:inputs['dllHashes'][json.loads((workspace/(side+'-plan.json')).read_text())[0]['dll']]
    for side in ('base','new')}
metadata['dllSha256'].update({
    'harness':next(v for k,v in inputs['dllHashes'].items() if k.endswith('/OfflineSearchHarness.dll'))}
)
for name,data in [('metadata.json',metadata),('results-summary.json',rows)]:
    (folder/name).write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
print(json.dumps(metadata['groups'],ensure_ascii=False))
