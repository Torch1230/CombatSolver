#!/usr/bin/env python3
"""Status-mapping self-test for the test ledger.

The self-test is not a fabrication layer: two cases are real catalogued entries
taken verbatim from the generated catalog and executed for real, one expected to
pass (the maintained structure gate) and one contract harness whose outcome is
whatever it really is at this commit.  Asserting an absolute outcome for the
second would bake a temporary repository state into the tool, so it is checked
for internal consistency and reported instead.

The remaining cases are control commands whose only job is to pin the state
mapping:

* a real command that prints the declared marker  -> Passed
* a real command with exit 0 but no marker        -> Failed
* a real command that exits non-zero              -> Failed
* a project path that does not exist              -> Blocked (missing command target)
* an executable that does not exist               -> Blocked (missing executable)
* a declared dependency that is absent            -> Blocked (unmet requirement)
* the documented game launcher                    -> Blocked (policy refusal, never runs)
* the coverage writer                             -> Blocked (policy refusal, never runs)
* an entry outside the run filter                 -> NotRun
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import catalog as catalog_module

REAL_CONTRACT_ENTRY_ID = "python:tools/NoVictoryRecoveryChecks/presets.py"
REAL_GATE_ENTRY_ID = "gate:tools/verify-refactor-boundaries.sh"
FILTER = r"^(selftest:|python:|gate:tools/verify-refactor-boundaries\.sh$)"  # excludes the command: control


def _entry(entry_id: str, command: list[str] | None, **overrides) -> dict:
    entry = {
        "entryId": entry_id,
        "kind": "SelftestControl",
        "platform": "any",
        "cwd": ".",
        "command": command,
        "commandText": " ".join(command) if command else None,
        "documentedCommand": None,
        "scenarioId": None,
        "matrixIndex": None,
        "targetPath": None,
        "markerPatterns": [],
        "markerSource": None,
        "verification": "exit_code",
        "requires": [],
        "fixturePaths": [],
        "mutates": [],
        "notes": "selftest control case",
        "source": {"id": "selftest", "path": "tools/TestLedger/selftest.py", "line": None},
    }
    entry.update(overrides)
    return entry


def control_entries() -> list[dict]:
    return [
        _entry("selftest:marker-present", ["python3", "-c", "print('SELFTEST_LEDGER_OK checks=1')"],
               markerPatterns=["SELFTEST_LEDGER_OK"], markerSource="selftest:expected"),
        _entry("selftest:marker-missing", ["python3", "-c", "print('selftest marker control ran')"],
               markerPatterns=["SELFTEST_MARKER_THAT_IS_NEVER_PRINTED_OK"],
               markerSource="selftest:expected"),
        _entry("selftest:exit-nonzero", ["python3", "-c", "import sys; sys.exit(3)"]),
        _entry("selftest:timeout", ["python3", "-c", "import time; time.sleep(20)"],
               timeoutSeconds=2, notes="timeout control: a timeout is never Passed"),
        _entry("selftest:missing-project",
               ["dotnet", "run", "--project",
                "tools/TestLedgerDoesNotExist/TestLedgerDoesNotExist.csproj", "-c", "Release"],
               targetPath="tools/TestLedgerDoesNotExist/TestLedgerDoesNotExist.csproj",
               requires=["dotnet"]),
        _entry("selftest:missing-executable", ["test-ledger-no-such-command-b073f1634", "--check"]),
        _entry("selftest:missing-dependency", ["npm", "test"], cwd="tools/OnlinePresence",
               requires=["node", "npm",
                         "node_modules:tools/TestLedger/node_modules-not-installed"]),
        _entry("selftest:refused-game",
               ["bash", "-c", "./tools/run-unattended-test.sh --scenario-id SELFTEST-REFUSAL-PROBE"]),
        _entry("selftest:refused-coverage",
               ["dotnet", "run", "--project", "tools/CoverageCatalog/CoverageCatalog.csproj",
                "-c", "Release", "--", ".", "--verify"]),
        _entry("command:not-run-control", ["python3", "-c", "print('never executed')"]),
    ]


EXPECTATIONS = [
    # (case name, entry id, expected status, expected exit code, reason prefix)
    ("real-gate-entry-passed", REAL_GATE_ENTRY_ID, "Passed", 0, None),
    ("marker-present", "selftest:marker-present", "Passed", 0, None),
    ("marker-missing", "selftest:marker-missing", "Failed", 0, "marker_missing"),
    ("exit-nonzero", "selftest:exit-nonzero", "Failed", 3, "exit_code_3"),
    ("timeout", "selftest:timeout", "Failed", None, "timeout_after_"),
    ("missing-project", "selftest:missing-project", "Blocked", None, "missing_command_target:"),
    ("missing-executable", "selftest:missing-executable", "Blocked", None, "missing_executable:"),
    ("missing-dependency", "selftest:missing-dependency", "Blocked", None, "unmet_requirement:"),
    ("refused-game", "selftest:refused-game", "Blocked", None, "refused_by_policy:game_launch"),
    ("refused-coverage", "selftest:refused-coverage", "Blocked", None,
     "refused_by_policy:coverage_write"),
    ("not-run-control", "command:not-run-control", "NotRun", None, "excluded_by_filter:"),
]


def run_selftest(repo: Path, work_directory: Path, cli: Path) -> int:
    work_directory.mkdir(parents=True, exist_ok=True)
    real_catalog = catalog_module.build(repo)
    real_entries = {entry["entryId"]: entry for entry in real_catalog["entries"]}
    for entry_id in (REAL_CONTRACT_ENTRY_ID, REAL_GATE_ENTRY_ID):
        if entry_id not in real_entries:
            print(f"SELFTEST_FAIL real-catalog-entry: {entry_id} is not in the generated catalog")
            return 1
    entries = [real_entries[REAL_CONTRACT_ENTRY_ID], real_entries[REAL_GATE_ENTRY_ID]]
    entries += control_entries()
    selftest_catalog = {
        "schemaVersion": catalog_module.SCHEMA_VERSION,
        "kind": "combat-solver-test-catalog",
        "generatedAtUtc": catalog_module.utc_now(),
        "generator": "tools/TestLedger/selftest.py",
        "repoRoot": str(repo),
        "sourceRevision": real_catalog["sourceRevision"],
        "scopes": {"all": ["*"]},
        "entryCountsByKind": {},
        "sources": [{"id": "selftest", "path": "tools/TestLedger/selftest.py",
                     "entryCount": len(entries)}],
        "entries": entries,
    }
    # The timeout control needs a short per-entry limit; the engine reads it from
    # the run options, so the whole self-test run uses a short budget and the
    # sleep-based case is the only long one.
    catalog_path = work_directory / "selftest-catalog.json"
    catalog_path.write_text(json.dumps(selftest_catalog, indent=2, ensure_ascii=False) + "\n")
    ledger_dir = work_directory / "ledger"
    completed = subprocess.run(
        [sys.executable, str(cli), "run", "--repo", str(repo), "--catalog", str(catalog_path),
         "--scope", "all", "--filter", FILTER, "--ledger-dir", str(ledger_dir),
         "--timeout-seconds", "300"],
        capture_output=True, text=True)
    print("SELFTEST_LEDGER_STDOUT_BEGIN")
    print(completed.stdout.rstrip())
    print("SELFTEST_LEDGER_STDOUT_END")
    if completed.returncode not in (0, 1):
        print(f"SELFTEST_FAIL ledger-run: unexpected exit code {completed.returncode}")
        print(completed.stderr.rstrip())
        return 1
    ledger_path = ledger_dir / "ledger.jsonl"
    records = [json.loads(line) for line in ledger_path.read_text().splitlines() if line.strip()]
    by_id = {record["entryId"]: record for record in records}
    failures: list[str] = []
    if len(records) != len(entries):
        failures.append(f"ledger has {len(records)} lines for {len(entries)} catalog entries")
    for name, entry_id, status, exit_code, reason_prefix in EXPECTATIONS:
        record = by_id.get(entry_id)
        if record is None:
            failures.append(f"{name}: {entry_id} missing from the ledger")
            continue
        if record["status"] != status:
            failures.append(f"{name}: status {record['status']} != {status} "
                            f"(reason={record['reason']})")
            continue
        if exit_code is not None and record["exitCode"] != exit_code:
            failures.append(f"{name}: exit code {record['exitCode']} != {exit_code}")
            continue
        if reason_prefix and not str(record["reason"] or "").startswith(reason_prefix):
            failures.append(f"{name}: reason {record['reason']!r} does not start with {reason_prefix!r}")
            continue
        print(f"SELFTEST_OK {name} status={status} exit_code={record['exitCode']} "
              f"reason={record['reason'] or '-'}")

    # Invariant over every executed entry: Passed requires exit 0 and, when a
    # marker is declared, that marker in the captured log.
    for record in records:
        executed = record["exitCode"] is not None or record["logPath"] is not None
        if not executed:
            if record["status"] not in ("Blocked", "NotRun"):
                failures.append(f"{record['entryId']}: {record['status']} without execution")
            if record["logPath"] is not None:
                failures.append(f"{record['entryId']}: {record['status']} wrote a log")
            continue
        if not record["logPath"] or not Path(record["logPath"]).is_file():
            failures.append(f"{record['entryId']}: executed without a log file")
        expected_passed = (record["exitCode"] == 0
                           and (not record["markerPatterns"] or record["markerFound"]))
        if (record["status"] == "Passed") != expected_passed:
            failures.append(f"{record['entryId']}: status {record['status']} contradicts "
                            f"exit={record['exitCode']} marker_found={record['markerFound']}")

    contract_record = by_id.get(REAL_CONTRACT_ENTRY_ID)
    if contract_record is not None:
        print(f"SELFTEST_NOTE real-contract-entry {REAL_CONTRACT_ENTRY_ID} "
              f"status={contract_record['status']} exit_code={contract_record['exitCode']} "
              f"reason={contract_record['reason'] or '-'} log={contract_record['logPath']}")
    if failures:
        for failure in failures:
            print(f"SELFTEST_FAIL {failure}")
        return 1
    print(f"SELFTEST_SUMMARY cases={len(EXPECTATIONS)} failures=0 ledger={ledger_path}")
    return 0
