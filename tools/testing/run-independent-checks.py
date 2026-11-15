#!/usr/bin/env python3
"""Run game-independent .NET regression checks with Python 3 and the .NET 9 SDK."""
import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
CHECKS = (
    "BfwsResearchChecks",
    "CardHookReceiverChecks",
    "CardTargetingChecks",
    "ClientUpdateChecks",
    "CombatSolver.GcPolicyChecks",
    "CombatSolver.WavePolicyChecks",
    "DiagnosticLogTests",
    "ExpansionBatchChecks",
    "ForkableListLayoutChecks",
    "LoopDisplayChecks",
    "ModelIdCacheChecks",
    "ModelPredictionStateChecks",
    "PortfolioSelectorChecks",
    "PowerCardValuationChecks",
    "PreCombatRequestChecks",
    "PredictionStateStoreChecks",
    "ReachableHandPotentialChecks",
    "ReachableHandValueChecks",
    "RunStatisticsTests",
    "RuntimeGcProfileChecks",
    "SnapshotListBufferChecks",
    "StateFingerprintChecks",
    "SurgicalResearchChecks",
    "TranspositionFrontierChecks",
    "TurnPhaseMirrorChecks",
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checks", nargs="*", help="Check names to run; defaults to the maintained suite.")
    args = parser.parse_args()
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
