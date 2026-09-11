#!/usr/bin/env python3
"""Compile production damage entry points with deterministic per-target effects."""
import argparse
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--source', type=Path,
    default=root / 'src/Engine/InCombat/Simulation/CombatPredictionSimulator.Damage.cs')
args = parser.parse_args()
source = args.source.read_text()
end = source.index('    // Mirrors the per-target body of CreatureCmd.Damage.')
out = root / '.local/damage-dealer-checks'
out.mkdir(parents=True, exist_ok=True)
(out / 'Production.cs').write_text(source[:end] + '}\n')
for name in ['Program.cs', 'Stubs.cs']:
    (out / name).write_text((Path(__file__).parent / name).read_text())
(out / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><LangVersion>13</LangVersion>
<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup></Project>''')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Checks.csproj'), '-c', 'Release'], check=True)
