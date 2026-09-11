#!/usr/bin/env python3
"""Compile the real end-turn admission methods against deterministic boundary doubles."""
import argparse
from pathlib import Path
import subprocess
import re

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--expansion-source', type=Path,
                    default=root / 'src/Search/CombatBeamSolver.Expansion.cs')
args = parser.parse_args()
out = root / '.local/end-turn-admission-checks'
out.mkdir(parents=True, exist_ok=True)

def method(path, name):
    text = path.read_text()
    # Locate by declaration, not calls earlier in the same file.
    match = re.search(r'^    private [^\n]*\b' + name + r'\(', text, re.M)
    if not match:
        raise ValueError(f'Missing method {name}')
    start = match.start()
    opening = text.index('{', match.end())
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]

search = root / 'src/Search'
parts = [method(args.expansion_source, 'BuildAcceptedEndTurnNodes')]
for file, names in [
    ('CombatBeamSolver.ParallelExpansion.cs', ['GenerateRawEndTurnCandidates', 'PruneCommittedCrossTurnCandidates']),
    ('CombatBeamSolver.CyclePlanning.cs', ['NeedsCycleExitAdmission', 'MaterializeAdmittedCycleExitObservation']),
]:
    parts.extend(method(search / file, name) for name in names)
(out / 'Production.cs').write_text('namespace CombatSolver;\npartial class CombatBeamSolver\n{\n' + '\n'.join(parts) + '\n}\n')
(out / 'Program.cs').write_text((Path(__file__).parent / 'Program.cs').read_text())
(out / 'OwnedExpansionBatch.cs').write_text((search / 'OwnedExpansionBatch.cs').read_text())
(out / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><LangVersion>13</LangVersion>
<NoWarn>CS0649</NoWarn><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup></Project>''')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Checks.csproj'), '-c', 'Release'], check=True)
