"""Compare the exact production strategic context with the pre-optimization algorithm."""
from pathlib import Path
import subprocess
from xml.sax.saxutils import escape
repo=Path(__file__).resolve().parents[2]
out=repo/'.local/strategic-keyword-checks';out.mkdir(parents=True,exist_ok=True)
source=(repo/'src/Search/StrategicEffectModel.cs').read_text()
start=source.index('[Flags]');end=source.index('internal static class StrategicEffectModel')
using=source[:source.index('[Flags]')]
(out/'Production.cs').write_text(using+source[start:end])
# Reverse only this optimization to keep the baseline's remaining formula in sync.
baseline=source[source.index('    public static StrategicEffectContext Build('):end].rstrip()
baseline=baseline[:baseline.rfind('}')]
baseline=baseline.replace('''            // Keywords can consult the card's pile. Read Exhaust only for a requested
            // metric, and reuse it only within this read-only card evaluation.
            bool? hasExhaustKeyword = needsExhaustCount
                ? card.Keywords.Contains(CardKeyword.Exhaust) : null;
            bool exhaustsOnPlay = hasExhaustKeyword == true''','''            bool exhaustsOnPlay = card.Keywords.Contains(CardKeyword.Exhaust)''')
baseline=baseline.replace('''                    hasExhaustKeyword ??= card.Keywords.Contains(CardKeyword.Exhaust);
                    if (!hasExhaustKeyword.Value) reusableShivCount++;''','''                    if (!card.Keywords.Contains(CardKeyword.Exhaust)) reusableShivCount++;''')
baseline=baseline.replace('''                    if (generated > 0 && (cardType == CardType.Power
                        || skillsExhaust && cardType == CardType.Skill
                        || (hasExhaustKeyword ??= card.Keywords.Contains(CardKeyword.Exhaust)) == true))''','''                    if (generated > 0 && (cardType == CardType.Power || exhaustsOnPlay))''')
if 'hasExhaustKeyword' in baseline or baseline.count('bool exhaustsOnPlay = card.Keywords.Contains')!=1:
 raise RuntimeError('Update the baseline transformation for the changed production algorithm')
(out/'Baseline.cs').write_text(using+'internal static class Baseline {\n'+baseline+'\n}')
for name in ['Program.cs','Stubs.cs']:(out/name).write_bytes((repo/'tools/StrategicKeywordChecks'/name).read_bytes())
(out/'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup>'''+''.join('<Compile Include="'+escape(str(repo/p))+'" />' for p in ['src/Strategy/CardMechanismFacts.cs','src/Search/SolverWeights.cs'])+'''</ItemGroup></Project>''')
subprocess.run(['dotnet','run','--project',str(out/'Checks.csproj'),'-c','Release'],cwd=repo,check=True)
