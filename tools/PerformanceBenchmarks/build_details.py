#!/usr/bin/env python3
"""Build a disposable checkout with a complete result dump after timing ends.

The temporary Writer edit is restored on completion, exceptions and Ctrl+C.
SIGKILL/power loss cannot run that cleanup; use a disposable checkout.
"""
import argparse
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", type=Path, required=True,
                        help="Disposable source checkout with its own local.props")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    checkout, output = args.checkout.resolve(), args.output.resolve()
    if checkout == Path(__file__).resolve().parents[2]:
        parser.error("Use a separate disposable checkout, not this tool's source checkout")
    writer = checkout / "src/Testing/UnattendedTestRunner.Writer.cs"
    saved = writer.read_bytes()
    source = saved.decode()
    anchor = "        public void CaptureSolverResult(SolverResult result)\n        {"
    if source.count(anchor) != 1 or 'WriteGeneratedArtifact("details.json",' in source:
        raise ValueError("Expected exactly one uninstrumented CaptureSolverResult method")
    output.mkdir(parents=True, exist_ok=False)
    injection = ('\n            WriteGeneratedArtifact("details.json", new { '
                 'result = SolverDiagnostics.DescribeResult(result), '
                 'phase = SolverDiagnostics.DescribeSearchPhasePerformance(result), '
                 'actions = result.BestNode.Actions, '
                 'policy = CombatBugReportExporter.LatestEffectivePolicy });')
    try:
        writer.write_text(source.replace(anchor, anchor + injection))
        subprocess.run(["dotnet", "build", "CombatSolver.csproj", "-c", "Release",
                        "-p:CopyModOnBuild=false", "-o", str(output)], cwd=checkout, check=True)
        shutil.copy2(checkout / "CombatSolver.json", output / "CombatSolver.json")
    finally:
        writer.write_bytes(saved)


if __name__ == "__main__":
    main()
