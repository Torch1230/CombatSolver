"""Compile the production escalation policy and request loop against controlled results."""
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
out = root / '.local/no-victory-recovery-checks'
out.mkdir(parents=True, exist_ok=True)
def method(path, declaration):
    text = path.read_text(encoding='utf-8')
    start = text.index(declaration)
    opening = text.index('{', start)
    end, depth = opening + 1, 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]
source = root / 'src/Search/CombatSearchCoordinator.FailureRecovery.cs'
methods = [method(source, '    internal static SolverResult EscalateSearchWhenNoVictory('),
           method(source, '    internal static SolverSearchProfile? BuildNoVictoryEscalationProfile(')]
weights = (root / 'src/Search/SolverWeights.cs').read_text(encoding='utf-8')
constants = [re.search(r'    public const int '+name+r' = [^;]+;', weights)[0] for name in
    ['NoVictoryEscalationFactor','MaximumNoVictoryEscalations','MaximumEscalatedBeamWidth','MaximumEscalatedBranchesPerAction']]
(out/'Production.cs').write_text('using System.Diagnostics;\nnamespace CombatSolver;\ninternal static partial class CombatSearchCoordinator {\n'+'\n'.join(methods)+'\n}\ninternal static class SolverWeights {\n'+'\n'.join(constants)+'\n}', encoding='utf-8')
(out/'SolverSearchProfile.cs').write_text((root/'src/Search/SolverSearchProfile.cs').read_text(encoding='utf-8'),encoding='utf-8')
original = method(root/'src/Testing/UnattendedTestRunner.NoVictoryEscalation.cs', '    private static void AssertNoVictoryEscalationPolicy(')
(out/'OriginalChecks.cs').write_text('namespace CombatSolver; internal static class OriginalChecks { public static void Run() => AssertNoVictoryEscalationPolicy();\n'+original+'\n}',encoding='utf-8')
(out/'Program.cs').write_text((Path(__file__).parent/'Program.cs').read_text(encoding='utf-8'),encoding='utf-8')
(out/'Checks.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>',encoding='utf-8')
subprocess.run(['dotnet','run','--project',str(out/'Checks.csproj'),'-c','Release'],check=True)
