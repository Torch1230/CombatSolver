#!/usr/bin/env python3
"""Command line entry point for the CombatSolver test ledger.

    catalog   build the machine-readable catalog from repository sources
    run       execute catalogued entries and write the four-state JSONL ledger
    selftest  verify the status mapping with real existing entries

See tools/TestLedger/README.md and docs/TEST_MATRIX.md.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

SCRIPT_DIRECTORY = Path(__file__).resolve().parent
if str(SCRIPT_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIRECTORY))

import catalog as catalog_module  # noqa: E402
import ledger as ledger_module  # noqa: E402
import selftest as selftest_module  # noqa: E402

DEFAULT_REPO = SCRIPT_DIRECTORY.parent.parent


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="test-ledger",
        description="Generate and run the independent CombatSolver test ledger.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    catalog_parser = subparsers.add_parser("catalog", help="generate the test catalog")
    catalog_parser.add_argument("--repo", default=str(DEFAULT_REPO))
    catalog_parser.add_argument("--output", default=None,
                                help="catalog path (default: <repo>/.local/test-ledger/test-catalog.json)")

    run_parser = subparsers.add_parser("run", help="run catalogued entries and write the ledger")
    run_parser.add_argument("--repo", default=str(DEFAULT_REPO))
    run_parser.add_argument("--catalog", default=None,
                            help="catalog path (default: <repo>/.local/test-ledger/test-catalog.json)")
    run_parser.add_argument("--scope", default="pure-contract",
                            choices=sorted(catalog_module.SCOPES),
                            help="entry kinds this run may execute")
    run_parser.add_argument("--filter", default=None, help="regex over entry ids")
    run_parser.add_argument("--ledger-dir", default=None,
                            help="output directory (default: <repo>/.local/test-ledger)")
    run_parser.add_argument("--timeout-seconds", type=int, default=600,
                            help="per-entry timeout; a timeout is recorded as Failed")
    run_parser.add_argument("--log-prefix", default="logs")
    run_parser.add_argument("--allow-game-launch", action="store_true",
                            help="allow entries that start the game (off by default)")
    run_parser.add_argument("--allow-coverage-writes", action="store_true",
                            help="allow entries that rewrite coverage/*.json (off by default)")

    selftest_parser = subparsers.add_parser("selftest", help="verify the status mapping")
    selftest_parser.add_argument("--repo", default=str(DEFAULT_REPO))
    selftest_parser.add_argument("--work-directory", default="/tmp/test-ledger-selftest")
    return parser


def main(argv: list[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    repo = Path(arguments.repo).resolve()
    if not (repo / "tools").is_dir() or not (repo / "docs/TEST_MATRIX.md").is_file():
        raise SystemExit(f"test-ledger: {repo} is not a CombatSolver checkout")

    if arguments.command == "catalog":
        destination = (Path(arguments.output) if arguments.output
                       else repo / ".local/test-ledger/test-catalog.json")
        catalog_doc = catalog_module.build(repo)
        catalog_module.write(catalog_doc, destination)
        print(f"TEST_LEDGER_CATALOG entries={json_counts(catalog_doc)} output={destination}")
        return 0

    if arguments.command == "run":
        ledger_dir = (Path(arguments.ledger_dir) if arguments.ledger_dir
                      else repo / ".local/test-ledger")
        catalog_path = (Path(arguments.catalog) if arguments.catalog
                        else ledger_dir / "test-catalog.json")
        if not catalog_path.is_file():
            catalog_module.write(catalog_module.build(repo), catalog_path)
            print(f"TEST_LEDGER_CATALOG_GENERATED path={catalog_path}")
        catalog_doc = ledger_module.load_catalog(catalog_path)
        summary = ledger_module.run(
            repo, catalog_doc, catalog_path, arguments.scope, arguments.filter, ledger_dir,
            arguments.timeout_seconds, arguments.allow_game_launch,
            arguments.allow_coverage_writes, arguments.log_prefix)
        return 0 if summary["counts"][ledger_module.STATUS_FAILED] == 0 else 1

    if arguments.command == "selftest":
        return selftest_module.run_selftest(
            repo, Path(arguments.work_directory).resolve(), Path(__file__).resolve())
    raise SystemExit(f"test-ledger: unknown command {arguments.command}")


def json_counts(catalog_doc: dict) -> str:
    return ", ".join(f"{kind}={count}" for kind, count in catalog_doc["entryCountsByKind"].items())


if __name__ == "__main__":
    raise SystemExit(main())
