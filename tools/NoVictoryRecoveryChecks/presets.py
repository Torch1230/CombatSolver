from pathlib import Path
import subprocess
root=Path(__file__).resolve().parents[2]
out=root/'.local/preset-budget-checks'
out.mkdir(parents=True,exist_ok=True)
text=(root/'src/Runtime/SolverSettings.cs').read_text(encoding='utf-8')
if 'ValidateMinimum(data.SearchMaxExpandedNodes, 100, nameof(data.SearchMaxExpandedNodes));' not in text:
    raise RuntimeError('Custom node budget still has a configured upper bound.')
fields=text[text.index('    private static readonly SolverPerformanceValues LowPerformance'):text.index('    private const string SettingsUri')]
(out/'Program.cs').write_text('''namespace CombatSolver;
internal sealed record SolverPerformanceValues(SolverSearchProfile Profile);
internal static class Program {
'''+fields+'''
static void Main() {
 var profiles=new[]{LowPerformance,MediumPerformance,HighPerformance,VeryHighPerformance};
 int[] nodes=[60000,120000,250000,500000];
 int[] time=[60000,120000,180000,300000];
 int[] beam=[45,60,90,135];
 for(int i=0;i<profiles.Length;i++) {
  var p=profiles[i];
  if(p.Profile.MaxExpandedNodes!=nodes[i]
    ||p.Profile.SoftTimeBudgetMilliseconds!=time[i]
    ||p.Profile.BeamWidth!=beam[i]) throw new Exception("Preset mismatch: "+i);
 }
 Console.WriteLine("PRESET_BUDGETS_OK presets=4 dimensions=nodes,time,beam");
}
}''',encoding='utf-8')
(out/'SolverSearchProfile.cs').write_text((root/'src/Search/SolverSearchProfile.cs').read_text(encoding='utf-8'),encoding='utf-8')
(out/'Checks.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>',encoding='utf-8')
subprocess.run(['dotnet','run','--project',str(out/'Checks.csproj'),'-c','Release'],check=True)
