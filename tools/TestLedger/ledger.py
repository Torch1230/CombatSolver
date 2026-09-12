#!/usr/bin/env python3
"""Run catalogued entries and emit a four-state ledger.

States and how they are decided (nothing is inferred from static evidence):

* ``Passed``  - the command actually ran, exited ``0`` and printed the declared
  marker when the catalog declares one.
* ``Failed``  - the command actually ran and either exited non-zero, timed out,
  or exited ``0`` without the declared marker.
* ``Blocked`` - the command did not run because policy refuses it, its target is
  missing, or a declared requirement (platform, game, node_modules, fixture,
  environment variable, NuGet package, display session, ...) is unmet.
* ``NotRun``  - the entry is registered and runnable but this run did not select
  it (scope or filter), with no environment blocker identified.

``StaticPassed``, matrix ``SkippedMissingFixture``, launcher ``timeout`` and
absent commands are never recorded as ``Passed``.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

STATUS_PASSED = "Passed"
STATUS_FAILED = "Failed"
STATUS_BLOCKED = "Blocked"
STATUS_NOT_RUN = "NotRun"
STATUSES = (STATUS_PASSED, STATUS_FAILED, STATUS_BLOCKED, STATUS_NOT_RUN)

# Commands the ledger refuses unless the operator explicitly opts in: they start
# the game or rewrite coverage/*.json, which a test ledger must never do by
# accident.
POLICY = {
    "game_launch": re.compile(
        r"(?:^|[\s/\\])(?:run-unattended-test|run-headless-matrix|run-checkpoint-batch"
        r"|run-visible-steam-benchmark)\.(?:sh|ps1)(?:\s|$)"),
    "coverage_write": re.compile(r"(?:^|[\s/\\])CoverageCatalog(?:\.csproj)?(?:\s|$)"),
}

# Requirement tokens without a colon name an executable the entry needs.
REQUIREMENT_EXECUTABLES = frozenset({
    "dotnet", "python3", "node", "npm", "bash", "pwsh", "rg", "jq", "flock", "sha256sum",
})
# Requirements without a colon that name an environment condition, not a binary.
NAMED_REQUIREMENTS = frozenset({
    "display_session", "local_props", "game_executable", "game_assemblies", "ritsu_lib",
    "combat_solver_build", "external_input", "browser_environment",
})
BROWSER_EXECUTABLES = ("google-chrome", "chromium", "chromium-browser", "microsoft-edge", "firefox")
MAX_LOG_READ_BYTES = 4 * 1024 * 1024


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Probe:
    """Evaluates the requirements the catalog attaches to every entry."""

    def __init__(self, repo: Path):
        self.repo = repo
        self.cache: dict[str, dict] = {}

    def requirement(self, requirement: str) -> dict:
        if requirement not in self.cache:
            self.cache[requirement] = self._evaluate(requirement)
        return dict(self.cache[requirement])

    def requirements(self, requirements: list[str]) -> list[dict]:
        return [self.requirement(name) for name in requirements]

    # -- path resolution -------------------------------------------------
    def _steam_root(self) -> Path:
        override = os.environ.get("COMBATSOLVER_STEAM_ROOT")
        if override:
            return Path(override)
        local = Path.home() / ".local/share/Steam"
        legacy = Path.home() / ".steam/steam"
        if local.is_dir():
            return local
        if legacy.is_dir():
            return legacy
        return local

    def _local_props(self) -> dict:
        path = self.repo / "local.props"
        if not path.is_file():
            return {}
        text = path.read_text(errors="replace")
        values = {}
        for name in ("Sts2Dir", "Sts2DataDir", "RitsuLibDir"):
            match = re.search(rf"<{name}>(.*?)</{name}>", text)
            if match:
                values[name] = match.group(1).strip()
        return values

    def _sts2_dir(self) -> Path:
        props = self._local_props()
        if props.get("Sts2Dir"):
            return Path(props["Sts2Dir"])
        return self._steam_root() / "steamapps/common/Slay the Spire 2"

    def _sts2_data_dir(self) -> Path:
        props = self._local_props()
        if props.get("Sts2DataDir"):
            return Path(props["Sts2DataDir"])
        variant = ("data_sts2_windows_x86_64" if sys.platform.startswith("win")
                   else "data_sts2_linuxbsd_x86_64")
        return self._sts2_dir() / variant

    def _ritsu_dir(self) -> Path:
        props = self._local_props()
        if props.get("RitsuLibDir"):
            return Path(props["RitsuLibDir"])
        return (self._steam_root()
                / "steamapps/workshop/content/2868840/3747602295/lib/0.111.0")

    # -- individual requirements -----------------------------------------
    def _evaluate(self, requirement: str) -> dict:
        met, detail = self._compute(requirement)
        return {"requirement": requirement, "met": met, "detail": detail}

    def _compute(self, requirement: str) -> tuple[bool, str]:
        if requirement.startswith("platform:"):
            wanted = requirement.split(":", 1)[1]
            if wanted == "any":
                return True, "platform independent"
            if wanted == "windows":
                met = sys.platform.startswith("win")
                return met, "windows host" if met else f"host is {sys.platform}"
            if wanted == "linux":
                met = sys.platform.startswith("linux")
                return met, "linux host" if met else f"host is {sys.platform}"
            return False, f"unknown platform token {wanted}"
        if requirement == "display_session":
            met = bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"))
            return met, "display session present" if met else "no DISPLAY/WAYLAND_DISPLAY"
        if requirement == "local_props":
            met = (self.repo / "local.props").is_file()
            return met, "local.props present" if met else "local.props missing"
        if requirement == "game_executable":
            executable = self._sts2_dir() / "SlayTheSpire2"
            return executable.is_file(), str(executable)
        if requirement == "game_assemblies":
            data = self._sts2_data_dir()
            missing = [name for name in ("sts2.dll", "GodotSharp.dll", "0Harmony.dll")
                       if not (data / name).is_file()]
            return not missing, str(data) if not missing else f"missing under {data}: {', '.join(missing)}"
        if requirement == "ritsu_lib":
            ritsu = self._ritsu_dir()
            missing = [name for name in ("STS2-RitsuLib.dll",) if not (ritsu / name).is_file()]
            manifest = ritsu.parent.parent / "mod_manifest.json"
            if not manifest.is_file():
                missing.append(str(manifest))
            return not missing, str(ritsu) if not missing else f"missing: {', '.join(missing)}"
        if requirement == "combat_solver_build":
            built = self.repo / ".godot/mono/temp/bin/Release/CombatSolver.dll"
            return built.is_file(), str(built)
        if requirement.startswith("node_modules:"):
            directory = self.repo / requirement.split(":", 1)[1]
            return directory.is_dir(), str(directory)
        if requirement.startswith("fixture:"):
            fixture = self.repo / requirement.split(":", 1)[1]
            return fixture.is_file(), str(fixture)
        if requirement.startswith("env:"):
            name = requirement.split(":", 1)[1]
            value = os.environ.get(name, "")
            return bool(value), f"{name}={'set' if value else 'unset'}"
        if requirement.startswith("nuget:"):
            package = requirement.split(":", 1)[1]
            cached = Path.home() / ".nuget/packages" / package
            return cached.is_dir(), str(cached)
        if requirement == "external_input":
            return False, "entry needs an external input artifact the catalog does not provide"
        if requirement == "browser_environment":
            found = next((name for name in BROWSER_EXECUTABLES if shutil.which(name)), None)
            return bool(found), found or "no browser executable on PATH"
        if ":" not in requirement:
            found = shutil.which(requirement)
            return bool(found), found or f"{requirement} not on PATH"
        return False, f"unknown requirement token {requirement}"


def policy_refusal(entry: dict, allow_game_launch: bool, allow_coverage_write: bool) -> str | None:
    text = entry.get("commandText") or ""
    for name, pattern in POLICY.items():
        if not pattern.search(text):
            continue
        if name == "game_launch" and allow_game_launch:
            continue
        if name == "coverage_write" and allow_coverage_write:
            continue
        return name
    return None


def scope_kinds(catalog_doc: dict, scope: str) -> tuple[str, ...]:
    kinds = tuple(catalog_doc.get("scopes", {}).get(scope, ()))
    if not kinds:
        raise SystemExit(f"test-ledger: unknown scope '{scope}'")
    return kinds


def _in_scope(entry: dict, kinds: tuple[str, ...]) -> bool:
    return "*" in kinds or entry["kind"] in kinds


def _safe_name(entry_id: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "_", entry_id).strip("_")[:150]


def _read_log_text(path: Path) -> str:
    try:
        size = path.stat().st_size
        with path.open("rb") as handle:
            if size > MAX_LOG_READ_BYTES:
                handle.seek(-MAX_LOG_READ_BYTES, os.SEEK_END)
            return handle.read().decode("utf-8", errors="replace")
    except OSError:
        return ""


def match_markers(patterns: list[str], text: str) -> list[str]:
    found = []
    for pattern in patterns:
        try:
            if re.search(pattern, text):
                found.append(pattern)
        except re.error:
            continue
    return found


def missing_executable(repo: Path, entry: dict) -> str | None:
    """A command whose executable is absent never ran, so it cannot be Failed."""
    arguments = entry.get("command") or []
    if not arguments:
        return None
    candidate = arguments[0]
    if "/" in candidate or "\\" in candidate:
        path = Path(candidate)
        if not path.is_absolute():
            path = repo / candidate
        return None if path.exists() else candidate
    return None if shutil.which(candidate) else candidate


def execute(entry: dict, repo: Path, log_path: Path, timeout_seconds: int) -> dict:
    """Run one entry; the log keeps the raw command output for audit."""
    arguments = list(entry["command"])
    cwd = (repo / entry.get("cwd", ".")).resolve()
    log_path.parent.mkdir(parents=True, exist_ok=True)
    started_at = utc_now()
    started = time.monotonic()
    exit_code: int | None = None
    timed_out = False
    spawn_error: str | None = None
    with log_path.open("w") as log:
        log.write(f"# entryId={entry['entryId']} kind={entry['kind']}\n")
        log.write(f"# cwd={cwd}\n# command={' '.join(arguments)}\n")
        log.flush()
        try:
            process = subprocess.Popen(
                arguments, cwd=str(cwd), stdout=log, stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL, start_new_session=True)
        except OSError as error:
            spawn_error = f"{type(error).__name__}: {error}"
            process = None
        if process is not None:
            try:
                exit_code = process.wait(timeout=timeout_seconds)
            except subprocess.TimeoutExpired:
                timed_out = True
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    process.kill()
                exit_code = process.wait()
        elapsed_ms = int((time.monotonic() - started) * 1000)
        log.write(f"\n# exit_code={exit_code} timeout={timed_out} elapsed_ms={elapsed_ms}\n")
    text = _read_log_text(log_path)
    matched = match_markers(entry.get("markerPatterns") or [], text)
    required = bool(entry.get("markerPatterns"))
    if spawn_error:
        return {"status": STATUS_FAILED, "exitCode": None, "timedOut": False,
                "reason": f"spawn_failed:{spawn_error}", "markerFound": None,
                "observedMarkers": [], "elapsedMs": elapsed_ms}
    if timed_out:
        return {"status": STATUS_FAILED, "exitCode": exit_code, "timedOut": True,
                "reason": f"timeout_after_{timeout_seconds}s", "markerFound": bool(matched),
                "observedMarkers": matched, "elapsedMs": elapsed_ms}
    if exit_code != 0:
        return {"status": STATUS_FAILED, "exitCode": exit_code, "timedOut": False,
                "reason": f"exit_code_{exit_code}", "markerFound": bool(matched) if required else None,
                "observedMarkers": matched, "elapsedMs": elapsed_ms}
    if required and not matched:
        return {"status": STATUS_FAILED, "exitCode": 0, "timedOut": False,
                "reason": "marker_missing", "markerFound": False,
                "observedMarkers": [], "elapsedMs": elapsed_ms}
    return {"status": STATUS_PASSED, "exitCode": 0, "timedOut": False, "reason": None,
            "markerFound": bool(matched) if required else None,
            "observedMarkers": matched, "elapsedMs": elapsed_ms}


def run(repo: Path, catalog_doc: dict, catalog_path: Path, scope: str, filter_pattern: str | None,
        ledger_dir: Path, timeout_seconds: int, allow_game_launch: bool,
        allow_coverage_write: bool, log_prefix: str = "logs") -> dict:
    kinds = scope_kinds(catalog_doc, scope)
    probe = Probe(repo)
    ledger_dir.mkdir(parents=True, exist_ok=True)
    log_directory = ledger_dir / log_prefix
    jsonl_path = ledger_dir / "ledger.jsonl"
    summary_path = ledger_dir / "summary.json"
    filter_regex = re.compile(filter_pattern) if filter_pattern else None
    run_id = (f"{utc_now()}-{hashlib.sha256(str(catalog_path).encode()).hexdigest()[:8]}")
    started_at = utc_now()
    started = time.monotonic()
    revision = catalog_doc.get("sourceRevision", {})
    results: list[dict] = []
    print(f"LEDGER_BEGIN run_id={run_id} entries={len(catalog_doc['entries'])} scope={scope} "
          f"filter={filter_pattern or '-'} commit={revision.get('commitShort', 'unknown')}")

    for entry in catalog_doc["entries"]:
        requirements = probe.requirements(entry.get("requires") or [])
        unmet = [item for item in requirements if not item["met"]]
        target = entry.get("targetPath")
        target_path = (repo / target) if target else None
        target_missing = target_path is not None and not target_path.exists()
        selected_by_scope = _in_scope(entry, kinds)
        selected_by_filter = filter_regex is None or bool(filter_regex.search(entry["entryId"]))
        refusal = policy_refusal(entry, allow_game_launch, allow_coverage_write)
        absent_executable = missing_executable(repo, entry)

        record = {
            "schemaVersion": 1,
            "entryId": entry["entryId"],
            "kind": entry["kind"],
            "platform": entry["platform"],
            "source": entry["source"],
            "documentedCommand": entry.get("documentedCommand"),
            "commandText": entry.get("commandText"),
            "command": entry.get("command"),
            "cwd": entry.get("cwd", "."),
            "scenarioId": entry.get("scenarioId"),
            "matrixIndex": entry.get("matrixIndex"),
            "verification": entry.get("verification"),
            "markerPatterns": entry.get("markerPatterns") or [],
            "markerSource": entry.get("markerSource"),
            "observedMarkers": [],
            "markerFound": None,
            "exitCode": None,
            "timedOut": False,
            "requires": entry.get("requires") or [],
            "metRequirements": [item["requirement"] for item in requirements if item["met"]],
            "unmetRequirements": [item["requirement"] for item in unmet],
            "requirementDetails": requirements,
            "excludedByScope": not selected_by_scope,
            "excludedByFilter": not selected_by_filter,
            "selected": selected_by_scope and selected_by_filter,
            "policyRefusal": refusal,
            "notes": entry.get("notes"),
            "mutates": entry.get("mutates") or [],
            "sourceRevision": revision,
            "startedAtUtc": None,
            "finishedAtUtc": None,
            "elapsedMs": 0,
            "logPath": None,
            "status": STATUS_NOT_RUN,
            "reason": None,
            "evidence": None,
        }

        if refusal is not None:
            record.update(status=STATUS_BLOCKED, reason=f"refused_by_policy:{refusal}",
                          evidence="entry not executed: ledger policy refusal")
        elif absent_executable is not None:
            record.update(status=STATUS_BLOCKED, reason=f"missing_executable:{absent_executable}",
                          evidence="entry not executed: command executable absent")
        elif target_missing:
            record.update(status=STATUS_BLOCKED, reason=f"missing_command_target:{target}",
                          evidence="entry not executed: command target absent")
        elif unmet:
            record.update(status=STATUS_BLOCKED, reason=f"unmet_requirement:{unmet[0]['requirement']}",
                          evidence=f"entry not executed: {unmet[0]['detail']}")
        elif not selected_by_scope:
            record.update(status=STATUS_NOT_RUN, reason=f"excluded_by_scope:{scope}",
                          evidence="entry is registered and runnable; this run did not select it")
        elif not selected_by_filter:
            record.update(status=STATUS_NOT_RUN, reason=f"excluded_by_filter:{filter_pattern}",
                          evidence="entry is registered and runnable; the run filter did not select it")
        else:
            log_path = log_directory / f"{_safe_name(entry['entryId'])}.log"
            record["startedAtUtc"] = utc_now()
            # An entry may pin its own limit; the run limit is the default.
            outcome = execute(entry, repo, log_path,
                              int(entry.get("timeoutSeconds") or timeout_seconds))
            record.update(outcome)
            record["finishedAtUtc"] = utc_now()
            record["logPath"] = str(log_path)
            marker = outcome["observedMarkers"][0] if outcome["observedMarkers"] else None
            record["evidence"] = (
                f"exit_code={outcome['exitCode']} marker={marker or '-'} log={log_path}")

        results.append(record)
        print(f"LEDGER_ENTRY entry_id={record['entryId']} status={record['status']} "
              f"exit_code={record['exitCode']} marker={record['observedMarkers'][0] if record['observedMarkers'] else '-'} "
              f"elapsed_ms={record['elapsedMs']} reason={record['reason'] or '-'}")

    finished_at = utc_now()
    elapsed_ms = int((time.monotonic() - started) * 1000)
    with jsonl_path.open("w") as handle:
        for record in results:
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    summary = summarise(repo, run_id, results, scope, filter_pattern, catalog_path, ledger_dir,
                        started_at, finished_at, elapsed_ms, timeout_seconds, revision)
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
    counts = summary["counts"]
    print("LEDGER_CATEGORIES " + json.dumps(summary["categories"], ensure_ascii=False, sort_keys=True))
    print(f"LEDGER_END run_id={run_id} total={counts['total']} passed={counts[STATUS_PASSED]} "
          f"failed={counts[STATUS_FAILED]} blocked={counts[STATUS_BLOCKED]} not_run={counts[STATUS_NOT_RUN]} "
          f"jsonl={jsonl_path} summary={summary_path}")
    return summary


def summarise(repo: Path, run_id: str, results: list[dict], scope: str, filter_pattern: str | None,
              catalog_path: Path, ledger_dir: Path, started_at: str, finished_at: str,
              elapsed_ms: int, timeout_seconds: int, revision: dict) -> dict:
    counts = {"total": len(results)}
    for status in STATUSES:
        counts[status] = sum(1 for record in results if record["status"] == status)
    by_kind: dict[str, dict] = {}
    for record in results:
        bucket = by_kind.setdefault(record["kind"], {"total": 0, **{s: 0 for s in STATUSES}})
        bucket["total"] += 1
        bucket[record["status"]] += 1
    refusals: dict[str, int] = {}
    unmet: dict[str, int] = {}
    for record in results:
        if record["policyRefusal"]:
            refusals[record["policyRefusal"]] = refusals.get(record["policyRefusal"], 0) + 1
        for requirement in record["unmetRequirements"]:
            unmet[requirement] = unmet.get(requirement, 0) + 1

    def category(predicate) -> dict:
        selected = [record for record in results if predicate(record)]
        bucket = {"total": len(selected)}
        for status in STATUSES:
            bucket[status] = sum(1 for record in selected if record["status"] == status)
        return bucket

    categories = {
        "game": category(lambda r: r["kind"].startswith(("Matrix", "SteamBenchmark")) or any(
            requirement in r["requires"] for requirement in
            ("game_executable", "game_assemblies", "ritsu_lib", "combat_solver_build"))),
        "windows": category(lambda r: r["platform"] == "windows"),
        "node": category(lambda r: r["kind"].startswith("Node")),
        "visible_steam": category(lambda r: r["kind"].startswith("SteamBenchmark")),
    }
    for name in ("game", "windows", "node", "visible_steam"):
        categories[name]["uncovered"] = (categories[name]["total"] - categories[name][STATUS_PASSED])
    return {
        "schemaVersion": 1,
        "kind": "combat-solver-test-ledger-summary",
        "runId": run_id,
        "scope": scope,
        "filter": filter_pattern,
        "startedAtUtc": started_at,
        "finishedAtUtc": finished_at,
        "elapsedMs": elapsed_ms,
        "timeoutSecondsPerEntry": timeout_seconds,
        "defaultTimeoutSeconds": timeout_seconds,
        "repoRoot": str(repo),
        "catalogPath": str(catalog_path),
        "catalogSha256": hashlib.sha256(catalog_path.read_bytes()).hexdigest()
        if catalog_path.is_file() else None,
        "ledgerDirectory": str(ledger_dir),
        "sourceRevision": revision,
        "counts": counts,
        "countsByKind": dict(sorted(by_kind.items())),
        "policyRefusals": dict(sorted(refusals.items())),
        "unmetRequirementCounts": dict(sorted(unmet.items(), key=lambda item: (-item[1], item[0]))),
        "categories": categories,
        "unattemptedRunnable": sorted(
            record["entryId"] for record in results
            if record["status"] == STATUS_NOT_RUN and not record["unmetRequirements"]),
        "neverPassed": {
            "staticEvidenceRead": False,
            "skippedCountedAsPassed": False,
            "timeoutCountedAsPassed": False,
            "missingCommandCountedAsPassed": False,
        },
    }


def load_catalog(path: Path) -> dict:
    catalog_doc = json.loads(path.read_text())
    if catalog_doc.get("kind") != "combat-solver-test-catalog":
        raise SystemExit(f"test-ledger: {path} is not a test catalog")
    return catalog_doc
