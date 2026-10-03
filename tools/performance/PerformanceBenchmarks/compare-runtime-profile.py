#!/usr/bin/env python3
"""Compare two native run-windows.ps1 outputs; emit JSON and fail closed."""
import argparse
import datetime as dt
import hashlib
import json
import math
from pathlib import Path
import re
import sys

COST_FIELDS = {"elapsedMilliseconds", "allocatedBytes", "managedHeapBytesAfter"}
WORK_FIELDS = ("totalExpanded", "totalTransitions", "totalChoiceBranches")
QUALITY_FIELDS = ("score", "projectedBattleHpLost", "potionCount", "potionUses",
                  "onlyDeathRoutes", "finalHp", "finalEnemyHp", "combatEndedTurn")


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def epoch_ms(value):
    stamp = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(stamp.tzinfo is not None, "sample timestamp lacks a timezone")
    return stamp.timestamp() * 1000


def timed_out(value):
    if isinstance(value, dict):
        return any(timed_out(item) for item in value.values())
    if isinstance(value, list):
        return any(timed_out(item) for item in value)
    return isinstance(value, str) and re.sub(r"[^a-z]", "", value.lower()) in {
        "timelimit", "timelimited", "timeout", "remainingtimelimit", "timebudget"}


def differences(a, b, path=""):
    if type(a) is not type(b):
        return [path + " (type)"]
    if isinstance(a, dict):
        return [path + "." + k + " (missing)" for k in sorted(a.keys() ^ b.keys())] + [
            p for k in sorted(a.keys() & b.keys()) for p in differences(a[k], b[k], path + "." + k)]
    if isinstance(a, list):
        return ([path + " (length)"] if len(a) != len(b) else []) + [
            p for i, (x, y) in enumerate(zip(a, b)) for p in differences(x, y, f"{path}[{i}]")]
    return [] if a == b else [path]


def load_arm(directory, server):
    result, measure, search = (read(directory / name) for name in
                               ("result.json", "measurement.json", "search-result.json"))
    require(result["status"] == "Passed" and measure["launcherExitCode"] == 0
            and measure["gameExitCode"] == 0, "result, launcher, or observed game exit failed")
    require(measure["settingsBytesUnchanged"] is True, "saved settings changed")
    pid = measure["gamePid"]
    require(pid == result["processId"] and pid > 0, "measurement/result PID mismatch")
    runtime = search["runtime"]
    expected = {"serverGc": server, "profile": "Active" if server else "Default",
                "savedNoGcRegionEnabled": True, "effectiveNoGcRegionEnabled": not server}
    require(all(runtime[k] == v for k, v in expected.items()), "actual runtime profile mismatch")
    require(measure["runtimeProfile"] == ("server-generational" if server else "default"),
            "requested runtime profile mismatch")
    metrics = result["solverMetrics"]
    require(metrics["configuredNoGcRegionEnabled"] is (not server), "effective GC metric mismatch")
    members = {k: metrics[k] for k in ("portfolioMembers", "powerRouteMembers")}
    require(not timed_out([search["boundaryReason"], metrics["boundary"], members]), "TimeLimit observed")
    require(metrics["turnLayerTimeBudgetStops"] == 0, "turn-layer time boundary observed")
    events = []
    runtime_events = read(directory / "runtime-events.json")
    require(isinstance(runtime_events, list), "runtime-events.json must contain the captured event array")
    for entry in runtime_events:
        match = re.search(r"\b(SEARCH_WORKER_START|SEARCH_GC_LIFECYCLE) generation=(\d+)\b", entry["message"])
        if match:
            events.append((match[1], match[2], entry["time"], entry["message"]))
    require(len(events) == 2 and events[0][0] == "SEARCH_WORKER_START"
            and events[1][0] == "SEARCH_GC_LIFECYCLE" and events[0][1] == events[1][1],
            "missing, multiple, unordered, or mismatched-generation worker boundaries")
    require("completed=true" in events[1][3], "worker lifecycle did not complete")
    start, end = events[0][2], events[1][2]
    require(end > start, "nonpositive worker duration")
    samples = [(epoch_ms(s["utc"]), s) for s in measure["samples"]]
    require(all(x[0] < y[0] for x, y in zip(samples, samples[1:])), "unordered samples")
    window = [(t, s) for t, s in samples if start <= t <= end]
    require(len(window) >= 2, "insufficient samples within worker boundaries")
    cpu = window[-1][1]["cpuMilliseconds"] - window[0][1]["cpuMilliseconds"]
    rss = max(s["workingSetBytes"] for _, s in window)
    require(math.isfinite(cpu) and cpu > 0 and rss > 0, "invalid sampled CPU/RSS")
    semantic = {k: search[k] for k in ("actions", "snapshot", "policy", "resultScope", "boundaryReason")}
    require(semantic["policy"] is not None, "effective policy is missing")
    semantic["work"] = {k: metrics[k] for k in WORK_FIELDS}
    semantic["quality"] = {k: metrics[k] for k in QUALITY_FIELDS}
    semantic["members"] = {k: [{f: v for f, v in member.items() if f not in COST_FIELDS}
                                for member in rows] for k, rows in members.items()}
    for kind in ("opening", "loadout", "resolved"):
        semantic["generated." + kind] = read(directory / f"generated-scenario.{kind}.json")
    measured = {"workerWallMilliseconds": end - start, "sampledWorkerPeakWorkingSetBytes": rss,
                "sampledWorkerCpuMilliseconds": cpu, "sampleCount": len(window),
                "sampleStartOmissionMilliseconds": window[0][0] - start,
                "sampleEndOmissionMilliseconds": end - window[-1][0],
                "maximumSampleGapMilliseconds": max(y[0] - x[0] for x, y in zip(window, window[1:])),
                "memberTotalElapsedMilliseconds": metrics["totalElapsedMilliseconds"],
                "gc": {k: metrics[k] for k in ("totalGcPauseMilliseconds", "maxGcPauseMilliseconds",
                         "totalGen0Collections", "totalGen1Collections", "totalGen2Collections")},
                "runtime": runtime, "pid": pid, "generation": events[0][1],
                "workerStartEpochMilliseconds": start, "workerEndEpochMilliseconds": end}
    identity = {"dllSha256": measure["dllSha256"], "dop": measure["dop"], "clr": runtime["clr"],
                "inputSha256": hashlib.sha256((directory / "input.json").read_bytes()).hexdigest()}
    return identity, semantic, measured


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    args = parser.parse_args()
    output = {"schemaVersion": 1, "validPair": False,
              "measurementNote": "Worker boundaries are log timestamps, including GC admission/finalization. "
              "RSS is a sampled maximum; CPU uses first/last in-window samples and omits edge intervals. "
              "These are process-wide costs during worker execution, not exclusive search-thread costs."}
    try:
        arms = [load_arm(args.baseline, False), load_arm(args.candidate, True)]
        output["baseline"], output["candidate"] = arms[0][2], arms[1][2]
        output["differences"] = differences(arms[0][0], arms[1][0], "identity") + differences(
            arms[0][1], arms[1][1], "semantic")
        output["identity"] = arms[0][0]
        require(not output["differences"], "input, policy, full outcome, or search work differs")
        output["ratiosCandidateOverBaseline"] = {k: arms[1][2][k] / arms[0][2][k] for k in (
            "workerWallMilliseconds", "sampledWorkerPeakWorkingSetBytes", "sampledWorkerCpuMilliseconds")}
        output["validPair"] = True
    except (OSError, ValueError, KeyError, TypeError) as error:
        output["error"] = str(error)
    print(json.dumps(output, ensure_ascii=False, indent=2, allow_nan=False))
    return 0 if output["validPair"] else 1


if __name__ == "__main__":
    sys.exit(main())
