#!/usr/bin/env python3
"""固定六十根、两侧各一个宿主；调用仓库原有运行器和比较器，不重建 DLL。"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

folder=Path(__file__).resolve().parent
repo=folder.parents[2]
p=argparse.ArgumentParser()
p.add_argument('--corpus',type=Path,help='首次捕获语料目录；以后复用已提交的 inputs')
p.add_argument('--base-dll',type=Path,required=True)
p.add_argument('--new-dll',type=Path,required=True)
p.add_argument('--workspace',type=Path,required=True)
p.add_argument('--prepare-only',action='store_true')
args=p.parse_args()
inputs=folder/'inputs'
inputs.mkdir(exist_ok=True)
if args.corpus:
    for request in sorted(args.corpus.glob('*-request.json')):
        if request.name.split('-')[0] not in ('EQ','FULL','GA'): continue
        data=json.loads(request.read_text())
        spec=Path(data['generatedScenarioPath'])
        if not spec.is_absolute(): spec=args.corpus/spec
        target=inputs/spec.name
        if target.exists() and target.read_bytes()!=spec.read_bytes(): raise RuntimeError('语料已变化：'+str(spec))
        shutil.copy2(spec,target)
        data['generatedScenarioPath']=str(target.relative_to(repo))
        (inputs/request.name).write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
requests=sorted(inputs.glob('*-request.json'))
assert {prefix:sum(r.name.startswith(prefix+'-') for r in requests) for prefix in ('EQ','FULL','GA')}==dict(EQ=10,FULL=40,GA=10)
workspace=args.workspace.resolve()
workspace.mkdir(parents=True,exist_ok=True)
tools=repo/'tools/OfflineSearchHarness'
harness=tools/'bin/Release/net9.0/OfflineSearchHarness.dll'
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
files=[args.base_dll.resolve(),args.new_dll.resolve(),harness]
fingerprints={str(f):digest(f) for f in files}
for side,dll in [('base',files[0]),('new',files[1])]:
    plan=[dict(label=side+'-'+r.name.removesuffix('-request.json'),request=str(r),profile='High',beam=90,nodes=250000,
        maxCardBranchesPerNode=48,maxPileChoiceBranchesPerAction=28,maxHandChoiceBranchesPerAction=36,
        maxDegreeOfParallelism=1,searchBudgetMilliseconds=600000,potionPolicy='Smart',searchMode='Coordinator',dll=str(dll)) for r in requests]
    (workspace/(side+'-plan.json')).write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n')
(workspace/'inputs.json').write_text(json.dumps(dict(dllHashes=fingerprints,
    corpusHashes={f.name:digest(f) for f in sorted(inputs.iterdir())}),ensure_ascii=False,indent=2)+'\n')
if args.prepare_only:
    print('EQUIVALENCE_PREPARED roots=60'); sys.exit(0)
processes=subprocess.check_output(['ps','-axo','pid=,comm=,args='],text=True)
if any(('OfflineSearchHarness.dll' in line or 'SlayTheSpire2.app/Contents/MacOS/' in line)
       and 'run.py ' not in line for line in processes.splitlines()):
    raise RuntimeError('已有游戏或离线宿主，请排空后再启动两进程批次。')
def run(side):
    env=dict(os.environ)
    home=workspace/('home-'+side)
    home.mkdir(exist_ok=True)
    env.update(HOME=str(home),DOTNET_CLI_HOME=str(home),DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',DOTNET_CLI_TELEMETRY_OPTOUT='1')
    with (workspace/(side+'.log')).open('w') as output:
        return subprocess.run([sys.executable,str(tools/'run_plan.py'),'--plan',str(workspace/(side+'-plan.json')),
            '--workspace',str(workspace/side),'--workers','1'],cwd=repo,env=env,stdout=output,stderr=subprocess.STDOUT).returncode
with ThreadPoolExecutor(max_workers=2) as executor: codes=list(executor.map(run,['base','new']))
if {str(f):digest(f) for f in files}!=fingerprints: raise RuntimeError('批次期间 DLL 已变化')
if any(codes): raise SystemExit('批次有失败或时间边界：'+str(codes))
subprocess.run([sys.executable,str(tools/'compare_results.py'),'--left',str(workspace/'base/runs'),'--left-prefix','base',
    '--right',str(workspace/'new/runs'),'--right-prefix','new','--out',str(workspace/'comparison.json')],cwd=repo,check=True)
