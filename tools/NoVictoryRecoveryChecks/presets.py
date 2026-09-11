from pathlib import Path
import subprocess
root=Path(__file__).resolve().parents[2]
out=root/'.local/preset-budget-checks'
out.mkdir(parents=True,exist_ok=True)
text=(root/'src/Runtime/SolverSettings.cs').read_text(encoding='utf-8')
fields=text[text.index('    private static readonly SolverPerformanceValues LowPerformance'):text.index('    private const string SettingsUri')]
(out/'Program.cs').write_text('''namespace CombatSolver;
internal sealed record SolverPerformanceValues(SolverSearchProfile ShortProfile,SolverSearchProfile DeepProfile);
internal static class Program {
'''+fields+'''
static void Main() {
 var profiles=new[]{LowPerformance,MediumPerformance,HighPerformance,VeryHighPerformance};
 int[] shorts=[2400,4800,10000,20000], deeps=[12000,24000,50000,100000];
 int[] shortTime=[5000,8000,12000,20000], deepTime=[60000,120000,180000,300000];
 int[] shortBeam=[18,24,36,54], deepBeam=[45,60,90,135];
 for(int i=0;i<profiles.Length;i++) {
  var p=profiles[i];
  if(p.ShortProfile.MaxExpandedNodes!=shorts[i]||p.DeepProfile.MaxExpandedNodes!=deeps[i]
    ||p.ShortProfile.SoftTimeBudgetMilliseconds!=shortTime[i]||p.DeepProfile.SoftTimeBudgetMilliseconds!=deepTime[i]
    ||p.ShortProfile.BeamWidth!=shortBeam[i]||p.DeepProfile.BeamWidth!=deepBeam[i]) throw new Exception("Preset mismatch: "+i);
 }
 Console.WriteLine("PRESET_BUDGETS_OK presets=4 dimensions=nodes,time,beam");
}
}''',encoding='utf-8')
(out/'SolverSearchProfile.cs').write_text((root/'src/Search/SolverSearchProfile.cs').read_text(encoding='utf-8'),encoding='utf-8')
(out/'Checks.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>',encoding='utf-8')
subprocess.run(['dotnet','run','--project',str(out/'Checks.csproj'),'-c','Release'],check=True)
