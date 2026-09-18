#!/usr/bin/env python3
"""Compile tools without running their entry points; keep exact exit codes/logs."""
import argparse
import json
import os
from pathlib import Path
import py_compile
import subprocess

parser=argparse.ArgumentParser(); parser.add_argument('--out',type=Path,required=True)
args=parser.parse_args(); repo=Path(__file__).resolve().parents[2]; out=args.out.resolve(); out.mkdir(parents=True,exist_ok=True)
env=os.environ.copy(); env.setdefault('DOTNET_ROOT','/opt/homebrew/Cellar/dotnet/9.0.8/libexec'); env['DOTNET_ROLL_FORWARD']='Major'
tracked=subprocess.check_output(['git','-C',str(repo),'ls-files','tools']).decode().splitlines()
rows=[]
for file in tracked:
    path=repo/file
    if path.suffix=='.py':
        try:
            py_compile.compile(str(path),cfile=str(out/(file.replace('/','_')+'.pyc')),doraise=True)
            rows.append(dict(file=file,kind='py_compile',exit=0))
        except py_compile.PyCompileError as error:
            rows.append(dict(file=file,kind='py_compile',exit=1,error=str(error)))
    if path.suffix=='.csproj':
        if subprocess.run(['pgrep','-f','[d]otnet .*OfflineSearchHarness.dll --request'],stdout=subprocess.DEVNULL).returncode==0:
            raise RuntimeError('Offline batch active; no build allowed')
        log=out/(path.parent.name+'.log')
        with log.open('w') as stream:
            result=subprocess.run(['dotnet','build',str(path),'-c','Release','-p:CopyModOnBuild=false'],cwd=repo,env=env,stdout=stream,stderr=subprocess.STDOUT)
        rows.append(dict(file=file,kind='dotnet-build',exit=result.returncode,log=str(log.relative_to(repo))))
        print(file,result.returncode,flush=True)
    (out/'results.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2))
print('TOOL_CHECKS',len(rows),'failed',sum(r['exit']!=0 for r in rows),flush=True)
