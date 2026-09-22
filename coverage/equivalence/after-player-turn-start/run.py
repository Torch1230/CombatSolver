#!/usr/bin/env python3
"""一次六十根双臂离线批次；冻结 DLL，调用正式运行器与比较器。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

folder=Path(__file__).resolve().parent
repo=folder.parents[2]
p=argparse.ArgumentParser()
p.add_argument('--corpus',type=Path,required=True)
p.add_argument('--base-dll',type=Path,required=True)
p.add_argument('--new-dll',type=Path,required=True)
p.add_argument('--workspace',type=Path,required=True)
args=p.parse_args()
workspace=args.workspace.resolve()
if workspace.exists():raise SystemExit('批次目录已存在；不自动重跑')
workspace.mkdir(parents=True)
requests=sorted(r for r in args.corpus.glob('*-request.json') if r.name.split('-')[0] in ('EQ','FULL','GA'))
assert {g:sum(r.name.startswith(g+'-') for r in requests) for g in ('EQ','FULL','GA')}==dict(EQ=10,FULL=40,GA=10)
tools=repo/'tools/OfflineSearchHarness'
harness=tools/'bin/Release/net9.0/OfflineSearchHarness.dll'
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
files=[args.base_dll.resolve(),args.new_dll.resolve(),harness]
frozen={str(f):digest(f) for f in files}
corpus={}
for r in requests:
    corpus[r.name]=digest(r)
    spec=Path(json.loads(r.read_text())['generatedScenarioPath'])
    if not spec.is_absolute():spec=args.corpus/spec
    corpus[spec.name]=digest(spec)
for side,dll in zip(('base','new'),files):
    plan=[dict(label=side+'-'+r.name.removesuffix('-request.json'),request=str(r.resolve()),profile='High',beam=90,nodes=250000,
        maxCardBranchesPerNode=48,maxPileChoiceBranchesPerAction=28,maxHandChoiceBranchesPerAction=36,
        maxDegreeOfParallelism=1,searchBudgetMilliseconds=600000,potionPolicy='Smart',searchMode='Coordinator',dll=str(dll)) for r in requests]
    (workspace/(side+'-plan.json')).write_text(json.dumps(plan,indent=2)+'\n')
(workspace/'inputs.json').write_text(json.dumps(dict(dllHashes=frozen,corpusHashes=corpus),indent=2)+'\n')
processes=subprocess.check_output(['ps','-axo','pid=,comm=,args='],text=True)
if any(('OfflineSearchHarness.dll' in line or 'SlayTheSpire2.app/Contents/MacOS/' in line)
       and 'run.py ' not in line for line in processes.splitlines()):raise SystemExit('已有游戏或离线宿主，拒绝启动')
def run(side):
    env=dict(os.environ);home=workspace/('home-'+side);home.mkdir()
    env.update(HOME=str(home),DOTNET_CLI_HOME=str(home),DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',DOTNET_CLI_TELEMETRY_OPTOUT='1')
    with (workspace/(side+'.log')).open('w') as out:
        return subprocess.run([sys.executable,str(tools/'run_plan.py'),'--plan',str(workspace/(side+'-plan.json')),
            '--workspace',str(workspace/side),'--workers','1'],cwd=repo,env=env,stdout=out,stderr=subprocess.STDOUT).returncode
with ThreadPoolExecutor(max_workers=2) as executor:codes=list(executor.map(run,['base','new']))
assert frozen=={str(f):digest(f) for f in files},'批次内 DLL 变化'
if any(codes):raise SystemExit('运行失败或时间边界：'+str(codes))
subprocess.run([sys.executable,str(tools/'compare_results.py'),'--left',str(workspace/'base/runs'),'--left-prefix','base',
    '--right',str(workspace/'new/runs'),'--right-prefix','new','--out',str(workspace/'comparison.json')],cwd=repo,check=True)
