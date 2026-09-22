"""Extract the production progress-retirement types and run their contract checks."""
from pathlib import Path
import subprocess
import argparse

repo = Path(__file__).resolve().parents[2]
out = repo / ".local/progress-retirement-checks"
out.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser()
parser.add_argument("--source-ref", help="extract production sources with git show for a negative/compatibility check")
parser.add_argument("--expect-failure", action="store_true", help="require the source-ref contract to fail at the known retirement assertion")
args = parser.parse_args()
if args.expect_failure and not args.source_ref:
    parser.error("--expect-failure requires --source-ref")


def extract_type(source: str, marker: str) -> str:
    start = source.index(marker)
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[start:index + 1]
    raise RuntimeError(f"unterminated type: {marker}")


def extract_record(source: str, marker: str) -> str:
    start = source.index(marker)
    end = source.index(");", start) + 2
    return source[start:end]


if args.source_ref:
    def source_at(path: str) -> str:
        return subprocess.check_output(
            ["git", "show", f"{args.source_ref}:{path}"], cwd=repo, text=True)
else:
    def source_at(path: str) -> str:
        return (repo / path).read_text()

progress = source_at("src/Runtime/SolverProgress.cs")
sessions = source_at("src/Runtime/SolverControllerSessions.cs")
types = "\n\n".join([
    extract_type(progress, "internal enum SearchTakeoverKind"),
    extract_record(progress, "internal sealed record SearchTakeoverRequest"),
    extract_type(progress, "internal sealed class SolverRouteAdoptionSeed"),
    extract_type(progress, "internal sealed class SearchInteractionState"),
    extract_type(sessions, "internal sealed class SearchProgressDisplayState"),
])

(out / "Production.cs").write_text("""using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace CombatSolver;

internal sealed record PlanAction(int Id, object? Payload = null);
internal enum SolverResultScope { SearchCompletion, RouteAdoption }
internal sealed class SolverResult(SolverResultScope scope)
{
    public SolverResultScope ResultScope { get; } = scope;
}
internal sealed record LiveCombatStamp(string StateText);
internal sealed record SolverProgress(long ElapsedMilliseconds, SolverRouteAdoptionSeed? RouteAdoptionSeed = null);
internal static class SolverWeights
{
    public const long ProgressUiIntervalMilliseconds = 1;
}

""" + types + "\n")
(out / "Program.cs").write_bytes((Path(__file__).parent / "Program.cs").read_bytes())
(out / "Checks.csproj").write_text("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup></Project>""")
completed = subprocess.run(
    ["dotnet", "run", "--project", str(out / "Checks.csproj"), "-c", "Release"],
    cwd=repo,
    text=True,
    capture_output=True,
)
output = completed.stdout + completed.stderr
print(output, end="")
if args.expect_failure:
    marker = "worker progress retired failed"
    if completed.returncode == 0 or marker not in output:
        raise SystemExit(
            f"expected source ref {args.source_ref} to fail at exact marker {marker!r}; "
            f"exit={completed.returncode}")
    print(f"PROGRESS_RETIREMENT_BASELINE_EXPECTED_FAILURE ref={args.source_ref} marker={marker}")
elif completed.returncode != 0:
    raise SystemExit(completed.returncode)
