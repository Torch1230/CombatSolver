#!/usr/bin/env python3
"""Small contracts for the audit tools, independent of game data."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
from clones import detect

def sample(file, identifier):
    return dict(file=file, lines=[dict(line=i+1,tokens=[dict(kind='IdentifierToken',text=identifier),dict(kind='EqualsToken',text='='),dict(kind='NumericLiteralToken',text=str(i)),dict(kind='SemicolonToken',text=';')]) for i in range(12)])

a,b=sample('a.cs','x'),sample('b.cs','x')
assert len(detect([a,b]))==1 and detect([a,b])[0]['category']=='literal'
b=sample('b.cs','y')
assert detect([a,b])[0]['category']=='identifier-only'
b['lines'][6]['tokens'][2]['text']='999'
assert detect([a,b])==[]  # Literal changes must not normalize away.
repo=Path(__file__).resolve().parents[2]
tool=repo/'tools/CodeDebt/CodeMetrics/bin/Release/net9.0/CodeMetrics.dll'
with tempfile.TemporaryDirectory(prefix='code-debt-',dir=repo/'.local/debt') as tmp:
    root=Path(tmp); (root/'src').mkdir()
    (root/'src/a.cs').write_text('partial class Shape { private int value; void M(bool enabled) { if (enabled && value > 0) { value++; } } }')
    (root/'src/b.cs').write_text('partial class Shape { void N() { value++; } }')
    env=os.environ.copy(); env.setdefault('DOTNET_ROOT','/opt/homebrew/Cellar/dotnet/9.0.8/libexec'); env['DOTNET_ROLL_FORWARD']='Major'
    subprocess.run(['dotnet',str(tool),str(root),str(root/'output')],env=env,check=True)
    methods=json.loads((root/'output/methods.json').read_text())
    m=next(m for m in methods if m['name']=='M'); assert m['complexity']==3 and m['depth']==1 and m['parameters']==1
    types=json.loads((root/'output/types.json').read_text()); assert types[0]['partialFiles']==2 and types[0]['members']==3
    coupling=json.loads((root/'output/coupling.json').read_text()); assert coupling[0]['file']=='src/b.cs' and coupling[0]['distinctFields']==1
print('CODE_DEBT_SELF_TEST_OK clone-literals/renaming/method-metrics/partial-coupling')
