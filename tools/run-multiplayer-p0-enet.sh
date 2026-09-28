#!/usr/bin/env bash
set -euo pipefail

port="${1:-33771}"
player_count="${2:-2}"
controller_mode="${3:-scripted}"
search_dop="${4:-0}"
if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1 || port > 65535)); then
    printf 'Port must be in 1..65535.\n' >&2
    exit 2
fi
if [[ "$player_count" != 2 && "$player_count" != 4 ]]; then
    printf 'Player count must be 2 or 4.\n' >&2
    exit 2
fi
if [[ "$controller_mode" != scripted && "$controller_mode" != full-auto ]]; then
    printf 'Controller mode must be scripted or full-auto.\n' >&2
    exit 2
fi
if [[ ! "$search_dop" =~ ^[0-9]+$ ]] || ((search_dop < 0 || search_dop > 16)); then
    printf 'Search DOP must be in 0..16.\n' >&2
    exit 2
fi

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
session="$repository_root/.local/multiplayer-p0/enet-$player_count-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
mkdir -p -- "$session"
pids=()

for ((seat=0; seat<player_count; seat++)); do
    peer="$session/peer-$seat"
    input="$session/input-$seat.json"
    mkdir -p -- "$peer"
    python3 - "$input" "$session" "$seat" "$port" "$player_count" "$controller_mode" <<'PY'
import json
import sys
path, session, seat, port, player_count, controller_mode = sys.argv[1:]
with open(path, 'w', encoding='utf-8') as output:
    json.dump({
        'mode': 'host' if seat == '0' else 'client',
        'playerCount': int(player_count),
        'seat': int(seat),
        'port': int(port),
        'coordinationDirectory': session,
        'expectedGameVersion': '0.111.0',
        'verifyControllerFullAuto': controller_mode == 'full-auto',
    }, output)
PY
    search_args=()
    if ((search_dop > 0)); then
        search_args+=(--search-max-degree-of-parallelism-for-test "$search_dop")
    fi
    "$repository_root/tools/run-unattended-test.sh" \
        --scenario-id MULTIPLAYER-P0 --multiplayer-probe-path "$input" \
        --evidence-directory "$peer" \
        --headless-instance "mp-p0-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')" \
        --headless-execution-mode parallel --headless-memory-reservation-mib 1536 \
        --timeout-seconds 120 --exit-on-complete --cleanup-instance-on-exit \
        "${search_args[@]}" \
        >"$peer/stdout.txt" 2>"$peer/stderr.txt" &
    pids+=("$!")
done

failed=0
for pid in "${pids[@]}"; do
    if ! wait "$pid"; then failed=1; fi
done
if ((failed)); then
    printf 'ENet peer launcher failed; inspect %s\n' "$session" >&2
    exit 1
fi

python3 - "$session" "$player_count" <<'PY'
import json
from pathlib import Path
import sys
session = Path(sys.argv[1])
player_count = int(sys.argv[2])
for seat in range(player_count):
    path = session / f'peer-{seat}' / 'result.json'
    if not path.exists() or json.loads(path.read_text(encoding='utf-8'))['status'] != 'Passed':
        raise SystemExit(f'ENet peer {seat} did not pass; inspect {session / f"peer-{seat}"}')
print(f'MULTIPLAYER_P0_ENET_OK evidence={session} peers={player_count}')
PY
