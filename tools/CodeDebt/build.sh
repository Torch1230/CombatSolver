#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
if pgrep -f '[d]otnet .*OfflineSearchHarness.dll --request' >/dev/null; then
    echo 'Refusing to build while offline requests are running.' >&2
    exit 1
fi
if [[ -e "$root/.editorconfig" ]]; then
    echo 'Finish the diagnostic build and remove its temporary configuration first.' >&2
    exit 1
fi
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/Cellar/dotnet/9.0.8/libexec}"
export DOTNET_ROLL_FORWARD=Major
dotnet build "$root/CombatSolver.csproj" -c Release -p:CopyModOnBuild=false
