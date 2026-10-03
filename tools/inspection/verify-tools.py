#!/usr/bin/env python3
"""Check the maintained tool tree; optionally compile all .NET tools without running them."""
from pathlib import Path
from urllib.parse import unquote
import argparse
import ast
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
CATEGORIES={'build','release','testing','replay','search','performance','inspection','community','runtime'}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build',action='store_true',help='Compile projects, retaining separate logs under .local/tool-checks.')
    args=parser.parse_args()
    names=subprocess.check_output(['git','ls-files','--cached','--others','--exclude-standard','-z'],cwd=ROOT).decode().split('\0')
    paths=sorted({ROOT/n for n in names if n.startswith('tools/') and (ROOT/n).is_file()})
    errors=[]
    if (ROOT/'services').exists():errors.append('Online services belong in independent repositories')
    for p in (ROOT/'tools').iterdir():
        if p.is_dir() and p.name not in CATEGORIES:errors.append('Unexpected tools directory: '+p.name)
        if p.is_file() and p.name not in {'README.md','Directory.Build.props'}:errors.append('Loose tool file: '+p.name)
    for p in paths:
        if p.suffix=='.py':
            try:ast.parse(p.read_text(encoding='utf-8-sig'),filename=str(p))
            except SyntaxError as e:errors.append(str(e))
        elif p.suffix=='.csproj':
            tree=ET.parse(p)
            for node in tree.iter():
                for value in [node.text or '',*node.attrib.values()]:
                    if re.search(r'(?:\.\./)+(?:src/|local\.props)',value):
                        errors.append(str(p.relative_to(ROOT))+': use CombatSolverRepositoryRoot for production source paths')
                if node.tag=='Compile' and node.get('Include','').startswith('$(CombatSolverRepositoryRoot)'):
                    relative=node.get('Include').removeprefix('$(CombatSolverRepositoryRoot)')
                    if '*' not in relative and not (ROOT/relative).is_file():errors.append(str(p.relative_to(ROOT))+': missing '+relative)
        elif p.suffix=='.md':
            for raw in re.findall(r'\[[^]\n]*\]\(([^\s)]+)\)',p.read_text(encoding='utf-8-sig')):
                if re.match(r'^[a-z][a-z0-9+.-]*:',raw,re.I) or raw.startswith('#'):continue
                if not (p.parent/unquote(raw.split('#')[0])).exists():errors.append(str(p.relative_to(ROOT))+': missing link '+raw)
    powershell=shutil.which('pwsh')
    if not powershell:errors.append('PowerShell 7 required to parse maintained .ps1 scripts')
    else:
        env=os.environ.copy();env['COMBATSOLVER_TOOL_CHECK_ROOT']=str(ROOT/'tools')
        code="""$ErrorActionPreference='Stop'; $failed=0; Get-ChildItem -LiteralPath $env:COMBATSOLVER_TOOL_CHECK_ROOT -Recurse -File -Filter *.ps1 | ForEach-Object { $tokens=$null; $issues=$null; [System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$issues) | Out-Null; foreach($issue in $issues) { Write-Output $issue; $failed++ } }; if($failed) { exit 1 }; Write-Output 'POWERSHELL_SYNTAX_OK'"""
        result=subprocess.run([powershell,'-NoProfile','-Command',code],env=env,capture_output=True,text=True,encoding='utf-8')
        if result.returncode:errors.append(result.stdout+result.stderr)
    bash=(Path(shutil.which('git')).resolve().parents[1]/'bin/bash.exe') if os.name=='nt' else Path(shutil.which('bash') or '/missing-bash')
    for p in paths:
        if p.suffix=='.sh':
            if not bash.exists():errors.append('Bash required to parse '+str(p.relative_to(ROOT)));break
            command=['zsh','-n',str(p)] if p.read_text(encoding='utf-8-sig').startswith('#!/bin/zsh') and os.name!='nt' else [str(bash),'-n',str(p)]
            result=subprocess.run(command,capture_output=True,text=True,encoding='utf-8')
            if result.returncode:errors.append(str(p.relative_to(ROOT))+': '+result.stderr)
    projects=[p for p in paths if p.suffix=='.csproj']
    if not errors and args.build:
        out=ROOT/'.local/tool-checks';out.mkdir(parents=True,exist_ok=True)
        for p in projects:
            if os.name!='nt' and p.stem=='CombatSolver.MemoryCleaner':
                print('WINDOWS_ONLY_BUILD_NOT_RUN '+str(p.relative_to(ROOT)));continue
            log=out/(p.stem+'.log')
            with log.open('w',encoding='utf-8') as stream:
                result=subprocess.run(['dotnet','build',str(p),'-c','Release','-p:CopyModOnBuild=false'],cwd=ROOT,stdout=stream,stderr=subprocess.STDOUT)
            print(('BUILT ' if result.returncode==0 else 'BUILD_FAILED ')+str(p.relative_to(ROOT)),flush=True)
            if result.returncode:errors.append(str(p.relative_to(ROOT))+': '+str(log))
    for error in errors:print(error)
    print(f'TOOLS_{"FAILED" if errors else "OK"} files={len(paths)} projects={len(projects)} errors={len(errors)}')
    return bool(errors)


if __name__=='__main__':sys.exit(main())
