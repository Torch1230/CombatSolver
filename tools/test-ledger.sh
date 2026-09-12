#!/usr/bin/env bash
# Independent test ledger entry point (Linux).
#
#   ./tools/test-ledger.sh catalog
#   ./tools/test-ledger.sh run --scope pure-contract --ledger-dir /tmp/test-ledger
#   ./tools/test-ledger.sh selftest
#
# The repository root defaults to the checkout that contains this script, so the
# tool never follows the current working directory into another worktree.
#
# Only Linux is provided: the Windows matrix block and the game launcher are
# catalogued but never executed here, and tools/test-ledger.ps1 is intentionally
# not shipped until a Windows host can verify it.
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

python_bin="${TEST_LEDGER_PYTHON:-python3}"
command -v "$python_bin" >/dev/null 2>&1 || {
    echo "test-ledger.sh: $python_bin is required" >&2
    exit 2
}

exec "$python_bin" "$script_dir/TestLedger/test_ledger.py" "$@"
