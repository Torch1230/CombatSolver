"""Check the production snapshot-release membership algorithm against its original scan."""
from pathlib import Path
import subprocess
repo = Path(__file__).resolve().parents[2]
out = repo / '.local/snapshot-release-checks'
out.mkdir(parents=True, exist_ok=True)
source = (repo / 'src/Search/CombatBeamSolver.Retention.cs').read_text()
start = source.index('    private static void ReleaseDroppedSnapshots(')
end = source.index('\n    private static ', start + 1)
method = source[start:end].replace('private static', 'public static', 1)
(out / 'Extracted.cs').write_text('''namespace Checks;
internal sealed class SearchNode(SimulationSnapshot snapshot) { public SimulationSnapshot Snapshot = snapshot; }
internal sealed class SimulationSnapshot(int id) {
 public int Id = id;
 public static List<int> Released = [];
 public void ReleaseSimulator() => Released.Add(Id);
 // Value equality must never determine simulator ownership.
 public override bool Equals(object? other) => other is SimulationSnapshot;
 public override int GetHashCode() => 0;
}
internal static class Production {
''' + method + '\n}')
(out / 'Program.cs').write_bytes((repo / 'tools/SnapshotReleaseChecks/Program.cs').read_bytes())
(out / 'Checks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup></Project>''')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Checks.csproj'), '-c', 'Release'], cwd=repo, check=True)
