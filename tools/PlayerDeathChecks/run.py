#!/usr/bin/env python3
"""Compile player-death sequencing and the production power-removal policy."""
import argparse
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--damage-source', type=Path,
    default=root / 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs')
args = parser.parse_args()

def extract(path, declaration):
    text = path.read_text()
    start = text.index(declaration)
    opening = text.index('{', start)
    end, depth = opening + 1, 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]

out = root / '.local/player-death-checks'
out.mkdir(parents=True, exist_ok=True)
handler = extract(args.damage_source, '    private bool HandlePlayerDeath(')
cleanup = extract(root / 'src/Search/SimulatedCombatState.DeathLifecycle.cs', '    public void RemovePowersAfterDeath(')
complete = extract(root / 'src/Search/SimulatedCombatState.DeathLifecycle.cs', '    public void CompletePlayerDeath(')
complete = complete.replace('MegaCrit.Sts2.Core.Entities.Players.Player', 'Player')
(out / 'Production.cs').write_text('namespace CombatSolver;\npartial class Simulator\n{\n' + handler
    + '\n}\npartial class SimulatedCombatState\n{\n' + cleanup + '\n' + complete + '\n}\n')
(out / 'Program.cs').write_text((Path(__file__).parent / 'Program.cs').read_text())
(out / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><LangVersion>13</LangVersion>
<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup></Project>''')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Checks.csproj'), '-c', 'Release'], check=True)
