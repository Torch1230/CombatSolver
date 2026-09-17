#!/usr/bin/env python3
"""Collect portfolio observations from the offline harness for one selector experiment.

Writes per-battle request/scenario inputs, runs the offline harness with the observation
switch, and emits a manifest whose splits are assigned per battle (never per row).

Every battle is one generated scenario; repeated runs of the same battle would have to
share the label's battleId in the manifest.
"""
import argparse
import concurrent.futures
import json
import os
import subprocess
from pathlib import Path
import time
from uuid import uuid4

CHARACTERS = ("IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER")
KINDS = ("Monster", "Elite", "Boss")

SCENARIO_TEMPLATE = {
    "schemaVersion": 1,
    "ascension": 10,
    "actIndex": 1,
    "includeStartingDeck": True,
    "includeStartingRelics": True,
    "includeAscendersBane": True,
    "applyRelicObtainEffects": False,
    "characterCards": {"count": 12, "ids": [], "upgradeLevels": 1},
    "colorlessCards": {"count": 2, "ids": []},
    "relics": {"count": 3, "ids": []},
    "potions": {"count": 2, "ids": []},
    "mode": "Search",
    "fixedSearchBudget": True,
}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def build_plan(seeds_per_combo):
    plan = []
    for kind in KINDS:
        for character in CHARACTERS:
            for index in range(seeds_per_combo):
                label = f"sel-{character.lower()}-{kind.lower()}-{index:02d}"
                plan.append((label, character, kind,
                             f"SELECTOR-{character}-{kind.upper()}-{index:02d}-20260917"))
    return plan


def split_for(index, total):
    """Per-battle split: 60% train, 20% validation, 20% test, assigned by battle order."""
    position = index / total
    if position < 0.6:
        return "train"
    if position < 0.8:
        return "validation"
    return "test"


def prepare_inputs(out, label, character, kind, seed):
    scenario = {**SCENARIO_TEMPLATE, "seed": seed, "characterId": character, "encounterKind": kind}
    scenario_path = (out / "inputs" / f"{label}.json").resolve()
    write_json(scenario_path, scenario)
    request_path = (out / "requests" / f"{label}.json").resolve()
    write_json(request_path, {
        "schemaVersion": 1,
        "runId": uuid4().hex,
        "scenarioId": f"PORTFOLIO-SELECTOR-{label}",
        "generatedScenarioPath": str(scenario_path),
        "timeoutSeconds": 240,
        "fixedSearchBudget": True,
    })
    return request_path


def run_one(job):
    label, request_path, runs, harness, options, timeout = job
    output = runs / label
    command = ["dotnet", str(harness), "--request", str(request_path), "--label", label,
               "--out", str(output), "--profile", "Custom",
               "--beam", str(options["beam"]), "--nodes", str(options["nodes"]),
               "--budget-ms", str(options["budget_ms"]), "--dop", "1",
               "--search-mode", "Coordinator", "--use-portfolio", "--observe-portfolio"]
    started = time.monotonic()
    try:
        completed = subprocess.run(["timeout", "--signal=KILL", str(timeout), *command],
                                   cwd=options["repo"], capture_output=True, text=True, timeout=timeout + 30)
        code = completed.returncode
    except subprocess.TimeoutExpired:
        code = "runner-timeout"
    wall = time.monotonic() - started
    observations = output / "portfolio-observations.json"
    rows = 0
    if observations.exists():
        try:
            rows = len(json.loads(observations.read_text())["observations"])
        except (json.JSONDecodeError, KeyError):
            rows = -1
    return {"label": label, "exitCode": code, "wallSeconds": round(wall, 2),
            "observationRows": rows, "hasObservations": observations.exists(),
            "hasQuality": (output / "quality.json").exists()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--harness", required=True, type=Path)
    parser.add_argument("--seeds-per-combo", type=int, default=6)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--beam", type=int, default=24)
    parser.add_argument("--nodes", type=int, default=20000)
    parser.add_argument("--budget-ms", type=int, default=20000)
    parser.add_argument("--timeout", type=int, default=240)
    parser.add_argument("--limit", type=int)
    args = parser.parse_args()

    out = args.out.resolve()
    runs = out / "runs"
    plan = build_plan(args.seeds_per_combo)
    if args.limit:
        plan = plan[:args.limit]
    if runs.exists() and any(runs.iterdir()):
        raise SystemExit(f"{runs} already has runs; use a fresh --out")

    options = {"repo": str(args.repo.resolve()), "beam": args.beam, "nodes": args.nodes,
               "budget_ms": args.budget_ms}
    jobs = []
    for label, character, kind, seed in plan:
        request_path = prepare_inputs(out, label, character, kind, seed)
        jobs.append((label, request_path, runs, args.harness.resolve(), options, args.timeout))

    started = time.monotonic()
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        for result in pool.map(run_one, jobs):
            results.append(result)
            print(json.dumps(result), flush=True)

    total = len(plan)
    items = [{"label": label, "split": split_for(index, total), "battleId": label}
             for index, (label, _, _, _) in enumerate(plan)]
    with_observations = sum(1 for result in results if result["hasObservations"])
    write_json(out / "manifest.json", {"items": items})
    summary = {
        "battles": total,
        "battlesWithObservations": with_observations,
        "observationRows": sum(max(0, result["observationRows"]) for result in results),
        "failed": [result["label"] for result in results if result["exitCode"] != 0],
        "wallSeconds": round(time.monotonic() - started, 1),
        "options": options | {"dop": 1, "workers": args.workers, "timeout": args.timeout},
        "runs": str(runs),
    }
    write_json(out / "collection-summary.json", summary)
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
