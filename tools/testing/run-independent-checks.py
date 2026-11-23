#!/usr/bin/env python3
"""Run game-independent .NET regression checks with Python 3 and the .NET 9 SDK."""
import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
CHECKS = (
    "ClientUpdateChecks",
    "DiagnosticLogTests",
    "LoopDisplayChecks",
    "PreCombatRequestChecks",
    "RunStatisticsTests",
    "StateFingerprintChecks",
)
EXCLUDED = {
    "PerformanceRecordingTests": "Requires an output directory or an existing EventPipe trace and a mode.",
}


def validate_catalog(checks_root):
    discovered = set()
    for project in checks_root.glob("*/*.csproj"):
        name = project.parent.name
        if project.name != name + ".csproj":
            raise ValueError(f"Unexpected check project layout: {project.relative_to(checks_root)}")
        discovered.add(name)
    registered = set(CHECKS) | set(EXCLUDED)
    unclassified = discovered - registered
    missing = registered - discovered
    if unclassified or missing:
        raise ValueError(
            "Check catalog differs from checks/*/*.csproj: "
            f"unclassified={sorted(unclassified)}, missing={sorted(missing)}. "
            "Add each project to CHECKS or EXCLUDED with a reason."
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checks", nargs="*", help="Check names to run; defaults to the maintained suite.")
    parser.add_argument("--check-catalog", action="store_true", help="Validate project registration without running .NET.")
    args = parser.parse_args()
    try:
        validate_catalog(ROOT / "tools/testing/checks")
    except ValueError as error:
        parser.error(str(error))
    if args.check_catalog:
        for name, reason in sorted(EXCLUDED.items()):
            print(f"EXCLUDED {name}: {reason}")
        print(f"CHECK_CATALOG registered={len(CHECKS)} excluded={len(EXCLUDED)}")
        return False
    unknown = set(args.checks) - set(CHECKS)
    if unknown:
        parser.error("Unknown checks: " + ", ".join(sorted(unknown)))
    failed = []
    for name in args.checks or CHECKS:
        project = ROOT / "tools/testing/checks" / name / (name + ".csproj")
        print(f"RUN {name}", flush=True)
        # Options after the project arguments can reach the check program. In particular,
        # --nologo makes PreCombatRequestChecks select no cases, so do not pass it here.
        result = subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], cwd=ROOT)
        if result.returncode:
            failed.append(name)
        print(f"{'FAIL' if result.returncode else 'PASS'} {name}", flush=True)
    count = len(args.checks or CHECKS)
    print(f"INDEPENDENT_CHECKS total={count} passed={count - len(failed)} failed={len(failed)}", flush=True)
    return bool(failed)


if __name__ == "__main__":
    sys.exit(main())
