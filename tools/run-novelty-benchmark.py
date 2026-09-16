#!/usr/bin/env python3
"""Measure one generated search in an owned, fresh Linux headless process."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smart", action="store_true")
    parser.add_argument("--native", action="store_true")
    parser.add_argument("--control-checks", action="store_true")
    parser.add_argument("--gc-scope", action="store_true")
    parser.add_argument("--expect-reclaim", action="store_true")
    parser.add_argument("--options", dest="research_options", type=Path, required=True)
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument("--ritsu-workshop-root", type=Path,
                        help="Optional frozen RitsuLib source, forwarded to the native launcher")
    parser.add_argument("--instance", default=f"performance-{os.getpid()}")
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--dop", type=int, default=16)
    parser.add_argument("--memory-mib", type=int, default=32768)
    args = parser.parse_args()
    if not Path("/proc/self/status").exists():
        parser.error("This optional benchmark runner requires Linux /proc; use the native launchers on other platforms.")
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}", args.instance):
        parser.error("Invalid instance name")
    if args.timeout <= 0 or not 1 <= args.dop <= 64 or args.memory_mib <= 0:
        parser.error("Expected a positive timeout/memory reservation and DOP 1..64")
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    settings = json.loads(args.settings.read_text())
    spec = json.loads(args.input.read_text())
    if spec.get("mode") != "Search":
        parser.error("Input must explicitly use Search mode")
    if args.smart: (output / "smart.flag").write_text("1\n")
    if args.native: (output / "native.flag").write_text("1\n")
    for name in ("control_checks", "gc_scope", "expect_reclaim"):
        if getattr(args, name): (output / (name.replace("_", "-") + ".flag")).write_text("1\n")
    write_json(output / "research-options.json", json.loads(args.research_options.read_text()))
    write_json(output / "settings.json", settings)
    write_json(output / "input.json", spec)
    gc_keys = ("DOTNET_gcServer", "COMPlus_gcServer", "DOTNET_GCHeapCount", "COMPlus_GCHeapCount",
               "DOTNET_GCConserveMemory", "COMPlus_GCConserveMemory")
    write_json(output / "environment.json", {
        "build": str(args.build.resolve()),
        "assemblySha256": hashlib.sha256((args.build / "CombatSolver.dll").read_bytes()).hexdigest(),
        "gcEnvironment": {key: os.environ.get(key) for key in gc_keys},
        "cpuAffinity": sorted(os.sched_getaffinity(0)),
        "gameRoot": str(args.game_root.resolve()),
        "ritsuWorkshopRoot": str(args.ritsu_workshop_root.resolve()) if args.ritsu_workshop_root else None,
    })
    state_home = Path(os.environ.get("XDG_STATE_HOME", str(Path.home() / ".local/state")))
    runtime = Path(os.environ.get("COMBATSOLVER_HEADLESS_ROOT", str(
        state_home / "CombatSolver/headless-instances" / args.instance))).resolve()
    launcher = ["bash", str(root / "tools/run-unattended-test.sh")]
    stop = launcher + ["--headless-instance", args.instance, "--stop-instance"]
    # Let the native launcher establish/check ownership before touching settings.
    subprocess.run(stop, cwd=root, check=True, stdout=subprocess.DEVNULL)
    owner = json.loads((runtime / "runtime-owner.json").read_text())
    if owner["worktree"] != str(root) or owner["instance"] != args.instance or owner["root"] != str(runtime):
        raise ValueError("Unexpected runtime owner")
    data = runtime / "data/SlayTheSpire2"
    data.mkdir(parents=True, exist_ok=True)
    write_json(data / "combat_solver_settings.json", settings)
    enabled = settings["enableNoGcRegion"]
    cmd = launcher + [
        "--headless-instance", args.instance,
        "--combat-solver-build-dir", str(args.build.resolve()),
        "--sts2-game-root", str(args.game_root.resolve()),
        "--generated-scenario-path", str(output / "input.json"),
        "--evidence-directory", str(output), "--scenario-id", "GENERATED-NOVELTY-SEARCH",
        "--timeout-seconds", str(args.timeout),
        "--headless-memory-reservation-mib", str(args.memory_mib),
        "--headless-cpu-reservation", str(args.dop),
        "--performance-preset-for-test", settings["performancePreset"],
        "--search-budget-override-milliseconds", str(round(settings["searchTimeLimitSeconds"] * 1000)),
        "--search-max-degree-of-parallelism-for-test", str(args.dop),
        "--enable-no-gc-region-for-test", "1" if enabled else "0",
        "--enable-detailed-diagnostic-logs-for-test", "0", "--keep-game-open",
    ]
    if args.native:
        cmd += ["--deployment-fast-mode-for-test", "Instant", "--deployment-inter-action-delay-seconds-for-test", "0"]
    if enabled:
        cmd += ["--no-gc-region-budget-gigabytes-for-test", str(settings["noGcRegionBudgetGigabytes"]),
                "--allow-no-gc-fallback-for-test"]
    if args.ritsu_workshop_root:
        cmd += ["--ritsu-workshop-root", str(args.ritsu_workshop_root.resolve())]
    write_json(output / "command.json", cmd)
    samples, pid, peak, run = [], None, 0, None
    started = time.monotonic()
    try:
        with (output / "launcher.log").open("w") as log:
            run = subprocess.Popen(cmd, cwd=root, stdout=log, stderr=subprocess.STDOUT)
            while True:
                marker = runtime / "process.json"
                if pid is None and marker.exists():
                    try:
                        pid = json.loads(marker.read_text())["pid"]
                    except json.JSONDecodeError:
                        pass  # The marker may be in the middle of its initial write.
                if pid is not None:
                    try:
                        lines = Path(f"/proc/{pid}/status").read_text().splitlines()
                        values = {line.split(":")[0]: int(line.split()[1]) * 1024
                                  for line in lines if line.startswith(("VmRSS:", "VmHWM:", "VmSwap:"))}
                        peak = max(peak, values.get("VmHWM", 0))
                        samples.append({"seconds": time.monotonic() - started, **values})
                    except FileNotFoundError:
                        pass  # A failed or timed-out owned process may already have exited.
                if run.poll() is not None:
                    break
                time.sleep(.25)
        result_path = output / "result.json"
        result = json.loads(result_path.read_text()) if result_path.exists() else {"status": "MissingResult"}
        write_json(output / "memory.json", {
            "pid": pid, "launcherExitCode": run.returncode, "status": result["status"],
            "processPeakRssBytes": peak,
            "source": "Linux kernel VmHWM, fresh process including setup; sampled every 250 ms",
            "samples": samples,
        })
        # The owned runtime retains earlier process sessions. Export this measured
        # process only, so old runs cannot be mistaken for the current search.
        for session in (data / "logs/CombatSolver").glob(f"{pid}-*"):
            if session.is_dir():
                shutil.copytree(session, output / "diagnostic-logs" / session.name)
        metrics = result.get("solverMetrics") or {}
        print(json.dumps({"output": str(output), "status": result["status"],
                          "error": result.get("error"), "peakRssBytes": peak,
                          "metrics": {key: metrics.get(key) for key in [
                              "boundary", "totalExpanded", "totalTransitions", "totalElapsedMilliseconds",
                              "totalWorkerAllocatedBytes", "totalGcPauseMilliseconds",
                              "projectedBattleHpLost", "combatEndedTurn"]}}, ensure_ascii=False), flush=True)
        return 0 if run.returncode == 0 and result["status"] == "Passed" and peak > 0 else 1
    finally:
        try:
            if run is not None and run.poll() is None:
                run.terminate()
                run.wait(timeout=10)
        finally:
            subprocess.run(stop, cwd=root, check=True, stdout=subprocess.DEVNULL)


if __name__ == "__main__":
    raise SystemExit(main())
