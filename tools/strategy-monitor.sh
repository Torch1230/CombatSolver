#!/usr/bin/env bash
set -Eeuo pipefail
session="${1:?session directory required}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
view="$script_dir/strategy-monitor-view.sh"
if command -v xterm >/dev/null 2>&1; then
    xterm -title "CombatSolver strategy monitor" -e bash "$view" "$session" &
elif command -v x-terminal-emulator >/dev/null 2>&1; then
    x-terminal-emulator -e bash "$view" "$session" &
elif command -v gnome-terminal >/dev/null 2>&1; then
    gnome-terminal --wait -- bash "$view" "$session" &
elif command -v konsole >/dev/null 2>&1; then
    konsole --nofork -e bash "$view" "$session" &
else
    echo 'No supported terminal emulator found for the strategy monitor.' >"$session/monitor-error.log"
    exit 1
fi
child=$!
printf '{"pid":%s,"readyUtc":"%s"}\n' "$BASHPID" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$session/monitor-ready.json"
wait "$child"
