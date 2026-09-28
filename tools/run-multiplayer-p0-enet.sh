#!/usr/bin/env bash
set -euo pipefail

port="${1:-33771}"
if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1 || port > 65535)); then
    printf 'Port must be in 1..65535.\n' >&2
    exit 2
fi

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
session="$repository_root/.local/multiplayer-p0/enet-2-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
mkdir -p -- "$session"
pids=()

for seat in 0 1; do
    peer="$session/peer-$seat"
    input="$session/input-$seat.json"
    mkdir -p -- "$peer"
    python3 - "$input" "$session" "$seat" "$port" <<'PY'
import json
import sys
path, session, seat, port = sys.argv[1:]
with open(path, 'w', encoding='utf-8') as output:
    json.dump({
        'mode': 'host' if seat == '0' else 'client',
        'playerCount': 2,
        'seat': int(seat),
        'port': int(port),
        'coordinationDirectory': session,
        'expectedGameVersion': '0.111.0',
    }, output)
PY
    "$repository_root/tools/run-unattended-test.sh" \
        --scenario-id MULTIPLAYER-P0 --multiplayer-probe-path "$input" \
        --evidence-directory "$peer" \
        --headless-instance "mp-p0-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')" \
        --headless-execution-mode parallel --headless-memory-reservation-mib 1536 \
        --timeout-seconds 120 --exit-on-complete --cleanup-instance-on-exit \
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

python3 - "$session" <<'PY'
import json
from pathlib import Path
import sys
session = Path(sys.argv[1])
for seat in (0, 1):
    path = session / f'peer-{seat}' / 'result.json'
    if not path.exists() or json.loads(path.read_text(encoding='utf-8'))['status'] != 'Passed':
        raise SystemExit(f'ENet peer {seat} did not pass; inspect {session / f"peer-{seat}"}')
print(f'MULTIPLAYER_P0_ENET_OK evidence={session} peers=2')
PY
