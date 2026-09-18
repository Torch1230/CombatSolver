#!/usr/bin/env bash
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/.local/debt"
mkdir -p "$out"
if pgrep -f '[d]otnet .*OfflineSearchHarness.dll --request' >/dev/null; then
    echo 'Refusing to build while offline requests are running.' >&2
    exit 1
fi
if [[ -e "$root/.editorconfig" ]]; then
    echo 'Existing .editorconfig must not be overwritten.' >&2
    exit 1
fi
cp "$root/tools/CodeDebt/analysis.editorconfig" "$root/.editorconfig"
trap 'rm -f "$root/.editorconfig"' EXIT
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/Cellar/dotnet/9.0.8/libexec}"
export DOTNET_ROLL_FORWARD=Major
dotnet build "$root/CombatSolver.csproj" -c Release -t:Rebuild \
    -p:CopyModOnBuild=false -p:TreatWarningsAsErrors=false \
    -p:AnalysisLevel=latest-all -p:EnforceCodeStyleInBuild=true \
    -p:ReportSuppressedDiagnostics=true -p:ErrorLog="$out/analyzer.sarif" \
    > "$out/analyzer.log" 2>&1
status=$?
printf '%s\n' "$status" > "$out/analyzer.exit"
exit "$status"
