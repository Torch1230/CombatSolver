"""Compile the production beam-width portfolio combinator and its refinement gate against
controlled member runs.

The combinator source is copied verbatim; the comparison rule, the interim result record and the
theft recovery order are extracted from their production files so the checks exercise the same
ordering the coordinator uses, not a restatement of it.
"""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
out = root / '.local/beam-width-portfolio-checks'
out.mkdir(parents=True, exist_ok=True)


def block(path, declaration):
    text = path.read_text(encoding='utf-8')
    start = text.index(declaration)
    opening = text.index('{', start)
    end, depth = opening + 1, 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]


search = root / 'src/Search'
(out / 'BeamWidthPortfolio.cs').write_text(
    (search / 'BeamWidthPortfolio.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'BeamWidthPortfolioGate.cs').write_text(
    (search / 'BeamWidthPortfolioGate.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'PowerCommitmentPortfolioGate.cs').write_text(
    (search / 'PowerCommitmentPortfolioGate.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'PowerCommitmentSeatPolicy.cs').write_text(
    (search / 'PowerCardValuation/Commitments/PowerCommitmentSeatPolicy.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'SolverSearchProfile.cs').write_text(
    (search / 'SolverSearchProfile.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'Ordering.cs').write_text(
    'namespace CombatSolver;\n'
    + block(search / 'TheftEncounterStrategy.cs', 'internal enum SolverTheftPolicy') + '\n'
    + block(root / 'src/Runtime/SolverProgress.cs', 'internal sealed record SolverInterimResult(') + '\n'
    + 'internal static class SolverInterimResultOrdering {\n'
    + block(search / 'SolverInterimResultOrdering.cs', '    public static int ComparePrimaryQuality(') + '\n}\n'
    + 'internal static class TheftEncounterStrategy {\n'
    + block(search / 'TheftEncounterStrategy.cs', '    public static int CompareRecovery(SolverTheftPolicy? policy,') + '\n}\n'
    + 'internal static partial class CombatSearchCoordinator {\n'
    + block(search / 'CombatSearchCoordinator.cs',
            '    internal static bool IsBetterPotionPolicyResult(\n        SolverTheftPolicy? theftPolicy,') + '\n}\n',
    encoding='utf-8')
(out / 'Program.cs').write_text(
    (Path(__file__).parent / 'Program.cs').read_text(encoding='utf-8'), encoding='utf-8')
(out / 'Checks.csproj').write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
    '<TargetFramework>net9.0</TargetFramework><LangVersion>13.0</LangVersion><Nullable>enable</Nullable>'
    '<ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
    '</PropertyGroup></Project>', encoding='utf-8')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Checks.csproj'), '-c', 'Release'], check=True)
