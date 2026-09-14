#!/usr/bin/env python3
"""Compare complete generated-search evidence, rejecting unknown field drift."""
import argparse
import json
from pathlib import Path
import re

# Only observed timing, allocation and scheduling fields are excluded. New RESULT
# fields participate in equality by default, including failures and quality flags.
RUNTIME = set("""worker_allocated_bytes allocated_per_transition gc0 gc1 gc2 gc_pause_ms
max_gc_pause_ms worker_yields frame_recovery_waits frame_recovery_wait_ms elapsed_ms
total_elapsed_ms total_worker_allocated_bytes total_gc0 total_gc1 total_gc2 total_gc_pause_ms
total_max_gc_pause_ms main_thread_frames p95_main_thread_gap_ms p99_main_thread_gap_ms
max_main_thread_gap_ms main_thread_gap_ms main_thread_over_33ms main_thread_over_50ms
main_thread_over_100ms managed_live_bytes managed_heap_bytes managed_fragmented_bytes
process_working_set_bytes process_private_bytes""".split())
SCHEDULING = set("""parallel_waves parallel_work_items parallel_action_waves
parallel_action_work_items deferred_round_choice_actions deferred_round_choice_width_total
deferred_round_choice_finite_fallbacks deferred_round_choice_finite_primary_layers
deferred_round_choice_finite_pending_fallbacks parallel_round_choice_waves
parallel_round_choice_work_items""".split())
PHYSICAL_FORKS = {"forks", "round_prefix_captures", "round_prefix_reuses", "card_prefix_attempts", "card_prefix_captures",
                  "card_prefix_reuses", "card_prefix_fallbacks"}
METRIC_RUNTIME = set("""gcLifecycle elapsedMilliseconds totalElapsedMilliseconds
workerAllocatedBytes totalWorkerAllocatedBytes totalGen0Collections totalGen1Collections
totalGen2Collections totalGcPauseMilliseconds maxGcPauseMilliseconds
capturedAtElapsedMilliseconds managedLiveBytes managedHeapBytes managedFragmentedBytes
workingSetBytes privateMemoryBytes gcLatencyMode noGcRegionActive noGcRegionBudgetBytes
noGcRegionRolloverCount""".split())
METRIC_SCHEDULING = set("""parallelActionReplayWaves parallelActionReplayWorkItems
deferredRoundChoiceActions deferredRoundChoiceLayerWidthTotal
deferredRoundChoiceFiniteQuotaFallbacks deferredRoundChoiceFinitePrimaryLayers
deferredRoundChoiceFinitePendingFallbacks parallelRoundChoiceReplayWaves
parallelRoundChoiceReplayWorkItems""".split())
METRIC_PHYSICAL = {"roundReplayPrefixCaptures", "roundReplayPrefixReuses", "cardChoicePrefixAttempts",
                   "cardChoicePrefixCaptures", "cardChoicePrefixReuses", "cardChoicePrefixFallbacks"}


def read(path):
    def load(name):
        return json.loads((path / name).read_text())

    result, details, memory = load("result.json"), load("details.json"), load("memory.json")
    if result["status"] != "Passed" or memory["launcherExitCode"] != 0:
        raise ValueError(f"{path}: request did not pass")
    if memory["pid"] != result["processId"] or memory["processPeakRssBytes"] <= 0:
        raise ValueError(f"{path}: missing peak or memory measured from a different process")
    metrics = result["solverMetrics"]
    if metrics["boundary"] == "TimeLimit":
        raise ValueError(f"{path}: time-limited work cannot establish equal-work performance")
    lines = details["result"].splitlines()
    fields = dict(re.findall(r"(\w+)=([^ ]+)", lines[0]))
    if int(fields["forks"]) - int(fields["round_prefix_captures"]) - int(fields.get("card_prefix_fallbacks", 0)) != int(fields["transitions"]):
        raise ValueError(f"{path}: physical Forks minus prefix captures/fallbacks do not match transitions")
    fixed = {k: v for k, v in fields.items() if k not in RUNTIME | SCHEDULING | PHYSICAL_FORKS}
    environment = load("environment.json") if (path / "environment.json").exists() else None
    oracle = {
        "fields": fixed, "actions": details["actions"], "route": lines[1:],
        "metrics": {key: value for key, value in metrics.items()
                    if key not in METRIC_RUNTIME | METRIC_SCHEDULING | METRIC_PHYSICAL},
        "input": load("input.json"),
        "policy": details["policy"], "settings": load("settings.json"),
        "resolved": load("generated-scenario.resolved.json"),
        "opening": load("generated-scenario.opening.json"),
        "loadout": load("generated-scenario.loadout.json"),
        "environment": {key: environment[key] for key in ("gcEnvironment", "cpuAffinity", "gameRoot")}
                       if environment else None,
    }
    measurements = {
        "path": str(path), "runId": result["runId"], "metrics": metrics,
        "peakRssBytes": memory["processPeakRssBytes"], "fixedFieldCount": len(fixed),
        "actionCount": len(details["actions"]),
        "environment": environment,
        "peakSwapBytes": max(s.get("VmSwap", 0) for s in memory["samples"]),
    }
    return oracle, measurements


def compare(reference, candidate):
    before, a = read(reference)
    after, b = read(candidate)
    differences = {}
    for key in before:
        if before[key] != after[key]:
            if isinstance(before[key], dict) and isinstance(after[key], dict):
                differences[key] = {field: {"A": before[key].get(field), "B": after[key].get(field)}
                                    for field in before[key].keys() | after[key].keys()
                                    if before[key].get(field) != after[key].get(field)}
            else:
                differences[key] = {"A": before[key], "B": after[key]}
    result = {"oracleEqual": not differences, "differences": differences, "A": a, "B": b}
    if not differences:
        pairs = {"peakRssBytes": (a["peakRssBytes"], b["peakRssBytes"])}
        for key in ["totalElapsedMilliseconds", "totalWorkerAllocatedBytes", "totalGcPauseMilliseconds"]:
            pairs[key] = a["metrics"][key], b["metrics"][key]
        result["changePercent"] = {key: 100 * (bv / av - 1) if av else None
                                   for key, (av, bv) in pairs.items()}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidates", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    comparisons = []
    for candidate in args.candidates:
        try:
            result = compare(args.reference, candidate)
        except (ValueError, OSError, KeyError) as error:
            result = {"candidate": str(candidate), "oracleEqual": False, "error": str(error)}
        comparisons.append(result)
        print(candidate, result["oracleEqual"], result.get("changePercent", result.get("error", result.get("differences"))))
    args.output.write_text(json.dumps({
        "schemaVersion": 1, "excludedRuntimeFields": sorted(RUNTIME),
        "excludedSchedulingFields": sorted(SCHEDULING),
        "excludedMetricRuntimeFields": sorted(METRIC_RUNTIME),
        "excludedMetricSchedulingFields": sorted(METRIC_SCHEDULING),
        "forkNormalization": "forks - round_prefix_captures - card_prefix_fallbacks == transitions",
        "comparisons": comparisons,
    }, ensure_ascii=False, indent=2) + "\n")
    return 0 if all(c["oracleEqual"] for c in comparisons) else 1


if __name__ == "__main__":
    raise SystemExit(main())
