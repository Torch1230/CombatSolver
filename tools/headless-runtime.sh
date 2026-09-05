#!/usr/bin/env bash
# Source-only Linux headless ownership/snapshot/admission boundary. No game rules.
hr_error() { echo "headless-runtime: $*" >&2; return 1; }
hr_start_time() {
    local line; local -a fields
    IFS= read -r line <"/proc/$1/stat" || return 1
    read -r -a fields <<<"${line##*) }"
    ((${#fields[@]} >= 20)) || return 1
    printf '%s\n' "${fields[19]}"
}
hr_identity_state() {
    local line; local -a fields
    [[ $1 =~ ^[1-9][0-9]*$ && $2 =~ ^[0-9]+$ ]] || return 2
    [[ -d /proc/$1 ]] || return 1
    [[ -r /proc/$1/stat ]] || return 2
    IFS= read -r line <"/proc/$1/stat" || return 2
    read -r -a fields <<<"${line##*) }"
    ((${#fields[@]} >= 20)) || return 2
    [[ ${fields[0]} != Z && ${fields[19]} == "$2" ]] || return 1
}
hr_init() {
    HR_ROOT="$(realpath -m -- "$1")"; HR_INSTANCE="$2"
    HR_EXECUTABLE="$(realpath -m -- "$3")"; HR_DATA="$4"
    HR_MODE="$5"; HR_MEMORY="$6"; HR_CPU="$7"; HR_QUEUE_SECONDS="$8"
    [[ $HR_INSTANCE =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$ ]] || { hr_error 'invalid instance id'; return 1; }
    [[ $HR_MODE == parallel || $HR_MODE == exclusive ]] || return 1
    local value
    for value in "$HR_MEMORY" "$HR_CPU" "$HR_QUEUE_SECONDS"; do
        [[ $value =~ ^[1-9][0-9]*$ ]] || { hr_error 'reservations and queue timeout must be positive integers'; return 1; }
    done
    HR_HOST="$(realpath -m -- "${COMBATSOLVER_HEADLESS_HOST_ROOT:-${XDG_STATE_HOME:-$HOME/.local/state}/CombatSolver/headless-host-v1}")"
    local protected child
    for protected in / "$HOME" "${HR_WORKTREE:-$PWD}" "$HR_HOST"; do
        protected="$(realpath -m -- "$protected")"
        [[ $HR_ROOT != "$protected" && $protected != "$HR_ROOT/"* ]] || { hr_error 'runtime cannot own a protected directory or its ancestor'; return 1; }
    done
    [[ $HR_ROOT != "$HR_HOST/"* ]] || return 1
    for child in game data config cache runtime-owner.json launcher.lock; do
        [[ ! -L $HR_ROOT/$child ]] || { hr_error "managed path cannot be a symlink: $child"; return 1; }
    done
    mkdir -p -- "$HR_ROOT" "$HR_HOST/leases" || return 1
    exec {HR_INSTANCE_FD}>"$HR_ROOT/launcher.lock" || return 1
    flock -n "$HR_INSTANCE_FD" || { hr_error "instance already has a producer: $HR_ROOT"; return 1; }
    local owner="$HR_ROOT/runtime-owner.json" worktree="${HR_WORKTREE:-$PWD}"
    worktree="$(realpath -m -- "$worktree")"
    if [[ -e $owner ]]; then
        jq -e --arg root "$HR_ROOT" --arg id "$HR_INSTANCE" --arg worktree "$worktree" \
            '.schemaVersion == 1 and .root == $root and .instance == $id and .worktree == $worktree' "$owner" >/dev/null || {
            hr_error 'runtime is owned by another worktree/instance'; return 1;
        }
    else
        [[ ! -e $HR_ROOT/game ]] || { hr_error 'refusing to claim an unowned game tree'; return 1; }
        jq -n --arg root "$HR_ROOT" --arg id "$HR_INSTANCE" --arg worktree "$worktree" \
            '{schemaVersion:1,root:$root,instance:$id,worktree:$worktree}' >"$owner" || return 1
    fi
    local key; key="$(printf '%s' "$HR_ROOT" | sha256sum)"; key="${key%% *}"
    HR_LEASE_PATH="$HR_HOST/leases/$key.json"
    HR_TOKEN=""; HR_LAUNCHER_PID=$BASHPID
    HR_LAUNCHER_START="$(hr_start_time "$HR_LAUNCHER_PID")"
}
hr_host_lock() { exec {HR_HOST_FD}>"$HR_HOST/coordinator.lock"; flock -w 5 "$HR_HOST_FD"; }
hr_host_unlock() { flock -u "$HR_HOST_FD"; exec {HR_HOST_FD}>&-; unset HR_HOST_FD; }
hr_write_lease() {
    local json="$1" temp
    temp="$(mktemp --tmpdir="$HR_HOST/leases" .lease.XXXXXX)" || return 1
    printf '%s\n' "$json" >"$temp" || return 1
    mv -f -- "$temp" "$HR_LEASE_PATH"
}
# Must hold host lock. Unknown/malformed identities are retained, never killed.
hr_collect_leases() {
    HR_RECORDS=(); HR_POOL_UNCERTAIN=0
    local path record state pid birth identity exe candidate orphan
    local -a paths games=()
    shopt -s nullglob; paths=("$HR_HOST/leases/"*.json); shopt -u nullglob
    for path in "${paths[@]}"; do
        if ! record="$(jq -ce 'select(.schemaVersion == 1 and (.state == "queued" or .state == "pending" or .state == "running")
            and (.mode == "exclusive" or .mode == "parallel") and (.token | type == "string" and length > 0)
            and (.ticket | type == "string") and (.executable | type == "string" and startswith("/"))
            and (.memoryMiB | type == "number" and . > 0 and floor == .) and (.cpu | type == "number" and . > 0 and floor == .)
            and (.launcherPid | type == "number" and . > 0 and floor == .) and (.launcherStart | type == "string" and test("^[0-9]+$"))
            and (if .state == "running" then (.gamePid | type == "number" and . > 0 and floor == .) and (.gameStart | type == "string" and test("^[0-9]+$")) else true end))' "$path")"; then
            HR_POOL_UNCERTAIN=1; continue
        fi
        state="$(jq -r .state <<<"$record")"
        if [[ $state == running ]]; then
            pid="$(jq -r .gamePid <<<"$record")"; birth="$(jq -r .gameStart <<<"$record")"
        else
            pid="$(jq -r .launcherPid <<<"$record")"; birth="$(jq -r .launcherStart <<<"$record")"
        fi
        identity=0; hr_identity_state "$pid" "$birth" || identity=$?
        if ((identity == 1)); then
            orphan=0
            if [[ $state == pending ]]; then
                exe="$(jq -r .executable <<<"$record")"
                # A launcher may die between spawn and bind. Do not reap its
                # reservation if its private executable might still be running.
                for candidate in /proc/[0-9]*/exe; do
                    [[ $(readlink -f -- "$candidate" 2>/dev/null) != "$exe" ]] || orphan=1
                done
            fi
            if ((orphan == 0)); then rm -f -- "$path"; continue; fi
            HR_POOL_UNCERTAIN=1
        elif ((identity == 2)); then
            HR_POOL_UNCERTAIN=1
        elif [[ $state == running ]]; then
            exe="$(jq -r .executable <<<"$record")"
            [[ $(readlink -f -- "/proc/$pid/exe" 2>/dev/null) == "$exe" ]] || HR_POOL_UNCERTAIN=1
        fi
        HR_RECORDS+=("$record")
    done
}
hr_unknown_games() {
    local output status=0 pid record known exe state
    output="$(pgrep -x SlayTheSpire2 2>/dev/null)" || status=$?
    ((status <= 1)) || return 0
    for pid in $output; do
        [[ -e /proc/$pid/exe ]] || continue
        known=0; exe="$(readlink -f -- "/proc/$pid/exe" 2>/dev/null)"
        for record in "${HR_RECORDS[@]}"; do
            state="$(jq -r .state <<<"$record")"
            if [[ $state == running ]] && jq -e --argjson pid "$pid" --arg exe "$exe" \
                '.gamePid == $pid and .executable == $exe' <<<"$record" >/dev/null; then known=1; break; fi
            if [[ $state == pending ]] && jq -e --arg exe "$exe" '.executable == $exe' <<<"$record" >/dev/null; then known=1; break; fi
        done
        ((known == 1)) || return 0
    done
    return 1
}
hr_acquire() {
    local game_pid="${1:-}" game_start="${2:-}" deadline=$((SECONDS + HR_QUEUE_SECONDS))
    local existing record used_memory used_cpu count reserved_remaining rss pid capacity_cpu total available reason ticket state
    local first=1 reserved warm=0 own_rss own_remaining
    capacity_cpu="$(getconf _NPROCESSORS_ONLN)"
    total="$(awk '/^MemTotal:/ {print int($2/1024)}' /proc/meminfo)"
    ((HR_CPU <= capacity_cpu && HR_MEMORY <= total - 2048)) || { hr_error 'reservation exceeds host capacity (2 GiB headroom)'; return 1; }
    while :; do
        hr_host_lock || return 1
        hr_collect_leases
        if [[ -z $HR_TOKEN && -e $HR_LEASE_PATH ]]; then
            existing="$(jq -c . "$HR_LEASE_PATH")"
            if [[ -n $game_pid ]] && jq -e --argjson pid "$game_pid" --arg birth "$game_start" \
                --arg mode "$HR_MODE" --argjson memory "$HR_MEMORY" --argjson cpu "$HR_CPU" \
                '.state == "running" and .gamePid == $pid and .gameStart == $birth and .mode == $mode and .memoryMiB == $memory and .cpu == $cpu' <<<"$existing" >/dev/null \
                && hr_identity_state "$game_pid" "$game_start"; then
                HR_TOKEN="$(jq -r .token <<<"$existing")"
                warm=1
            else
                hr_host_unlock; hr_error 'existing instance lease cannot be safely adopted'; return 1
            fi
        fi
        if [[ -z $HR_TOKEN ]]; then
            if [[ -n $game_pid ]]; then hr_host_unlock; hr_error 'live process has no host lease; cannot adopt'; return 1; fi
            HR_TOKEN="$(cat /proc/sys/kernel/random/uuid)"
            ticket="$(date +%s%N)-$HR_TOKEN"
            record="$(jq -cn --arg root "$HR_ROOT" --arg id "$HR_INSTANCE" --arg exe "$HR_EXECUTABLE" --arg data "$HR_DATA" \
                --arg token "$HR_TOKEN" --arg ticket "$ticket" --arg mode "$HR_MODE" --arg birth "$HR_LAUNCHER_START" \
                --argjson pid "$HR_LAUNCHER_PID" --argjson memory "$HR_MEMORY" --argjson cpu "$HR_CPU" \
                '{schemaVersion:1,instanceRoot:$root,instanceId:$id,executable:$exe,dataHome:$data,token:$token,ticket:$ticket,mode:$mode,memoryMiB:$memory,cpu:$cpu,launcherPid:$pid,launcherStart:$birth,state:"queued"}')"
            hr_write_lease "$record"
        fi
        if ! existing="$(jq -ce --arg token "$HR_TOKEN" 'select(.token == $token)' "$HR_LEASE_PATH")"; then
            hr_host_unlock; hr_error 'own lease disappeared or changed while queued'; return 1
        fi
        ticket="$(jq -r .ticket <<<"$existing")"
        used_memory=0; used_cpu=0; count=0; reserved_remaining=0; reason=""
        for record in "${HR_RECORDS[@]}"; do
            [[ $(jq -r .token <<<"$record") != "$HR_TOKEN" ]] || continue
            state="$(jq -r .state <<<"$record")"
            if [[ $state == queued ]]; then
                [[ $(jq -r .ticket <<<"$record") > "$ticket" ]] || reason='earlier queued request'
                continue
            fi
            count=$((count + 1)); used_cpu=$((used_cpu + $(jq -r .cpu <<<"$record")))
            reserved="$(jq -r .memoryMiB <<<"$record")"; used_memory=$((used_memory + reserved)); rss=0
            if [[ $state == running ]]; then
                pid="$(jq -r .gamePid <<<"$record")"
                rss="$(awk '/^VmRSS:/ {print int($2/1024)}' "/proc/$pid/status" 2>/dev/null || true)"; rss="${rss:-0}"
            fi
            ((rss >= reserved)) || reserved_remaining=$((reserved_remaining + reserved - rss))
            [[ $HR_MODE != exclusive && $(jq -r .mode <<<"$record") != exclusive ]] || reason='exclusive lease'
        done
        own_remaining=$HR_MEMORY
        if ((warm == 1)); then
            if ! jq -e --argjson pid "$game_pid" --arg birth "$game_start" --arg exe "$HR_EXECUTABLE" \
                '.state == "running" and .gamePid == $pid and .gameStart == $birth and .executable == $exe' <<<"$existing" >/dev/null \
                || ! hr_identity_state "$game_pid" "$game_start" \
                || [[ $(readlink -f -- "/proc/$game_pid/exe" 2>/dev/null) != "$HR_EXECUTABLE" ]]; then
                hr_host_unlock; hr_error 'warm game identity changed while queued'; return 1
            fi
            own_rss="$(awk '/^VmRSS:/ {print int($2/1024)}' "/proc/$game_pid/status" 2>/dev/null || true)"
            own_rss="${own_rss:-0}"
            if ! hr_identity_state "$game_pid" "$game_start" \
                || [[ $(readlink -f -- "/proc/$game_pid/exe" 2>/dev/null) != "$HR_EXECUTABLE" ]]; then
                hr_host_unlock; hr_error 'warm game identity changed while reading RSS'; return 1
            fi
            # MemAvailable already excludes this live process's resident pages.
            # Reuse reserves only its remaining growth; keep full reservation
            # totals above, and never turn excess RSS into negative headroom.
            if ((own_rss >= HR_MEMORY)); then own_remaining=0
            else own_remaining=$((HR_MEMORY - own_rss)); fi
        fi
        available="$(awk '/^MemAvailable:/ {print int($2/1024)}' /proc/meminfo)"
        ((count < 2 && used_cpu + HR_CPU <= capacity_cpu && used_memory + HR_MEMORY <= total - 2048 && available - reserved_remaining >= own_remaining + 2048)) || reason='host reservations full'
        ((HR_POOL_UNCERTAIN == 0)) || reason='indeterminate lease identity'
        if hr_unknown_games; then reason='unregistered game process'; fi
        if [[ -z $reason ]]; then
            ((warm == 1)) || hr_write_lease "$(jq -c '.state = "pending"' <<<"$existing")"
            hr_host_unlock
            echo "HEADLESS_ADMITTED instance=$HR_INSTANCE mode=$HR_MODE memory_mib=$HR_MEMORY cpu=$HR_CPU" >&2
            return 0
        fi
        hr_host_unlock
        if ((first == 1)); then echo "HEADLESS_QUEUED instance=$HR_INSTANCE reason=$reason" >&2; first=0; fi
        if ((SECONDS >= deadline)); then hr_release; hr_error "queue timeout: $reason"; return 1; fi
        sleep 0.25
    done
}
hr_bind() {
    local pid="$1" birth="$2" record
    hr_identity_state "$pid" "$birth" && [[ $(readlink -f -- "/proc/$pid/exe") == "$HR_EXECUTABLE" ]] || return 1
    hr_host_lock || return 1
    if ! record="$(jq -ce --arg token "$HR_TOKEN" 'select(.token == $token and .state == "pending")' "$HR_LEASE_PATH")"; then hr_host_unlock; return 1; fi
    hr_write_lease "$(jq -c --argjson pid "$pid" --arg birth "$birth" '.state="running" | .gamePid=$pid | .gameStart=$birth' <<<"$record")"
    hr_host_unlock
}
hr_release() {
    [[ -n ${HR_TOKEN:-} ]] || return 0
    hr_host_lock || return 1
    local record identity=0 candidate
    if record="$(jq -ce --arg token "$HR_TOKEN" 'select(.token == $token)' "$HR_LEASE_PATH" 2>/dev/null)"; then
        if [[ $(jq -r .state <<<"$record") == running ]]; then
            hr_identity_state "$(jq -r .gamePid <<<"$record")" "$(jq -r .gameStart <<<"$record")" || identity=$?
            if ((identity != 1)); then hr_host_unlock; return 0; fi
        elif [[ $(jq -r .state <<<"$record") == pending ]]; then
            for candidate in /proc/[0-9]*/exe; do
                if [[ $(readlink -f -- "$candidate" 2>/dev/null) == "$HR_EXECUTABLE" ]]; then
                    hr_host_unlock; return 0
                fi
            done
        fi
        rm -f -- "$HR_LEASE_PATH"
    fi
    HR_TOKEN=""; hr_host_unlock
}

# Full source-content provenance: no version-string-only reuse. Caller holds
# instance lock and must stop its old game before publishing a changed snapshot.
hr_snapshot_id() {
    local source="$1" file
    {
        (cd -- "$source" && find -L . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum --) || return 1
        shift
        for file in "$@"; do sha256sum -- "$file" || return 1; done
    } | sha256sum | cut -d ' ' -f 1
}
hr_prepare_snapshot() {
    local source="$1" dll="$2" manifest="$3" ritsu="$4" ritsu_manifest="$5" expected="$6" staging old directory actual id_temp
    [[ $HR_ROOT != "$source" && $HR_ROOT != "$source/"* && $source != "$HR_ROOT/"* ]] || { hr_error 'source and runtime must be disjoint'; return 1; }
    local candidate
    for candidate in /proc/[0-9]*/exe; do
        [[ $(readlink -f -- "$candidate" 2>/dev/null) != "$HR_EXECUTABLE" ]] || { hr_error 'cannot publish snapshot while its executable is alive'; return 1; }
    done
    if [[ -f $HR_ROOT/snapshot-id && $(<"$HR_ROOT/snapshot-id") == "$expected" && -d $HR_ROOT/game ]]; then return 0; fi
    staging="$(mktemp -d --tmpdir="$HR_ROOT" snapshot.XXXXXX)" || return 1
    cp -aL --reflink=auto -- "$source/." "$staging/" || return 1
    for directory in "$staging/mods/CombatSolver" "$staging/mods/CombatSolverHeadlessRitsuLib"; do
        [[ ! -e $directory ]] || rm -rf -- "$directory" || return 1
        mkdir -p -- "$directory" || return 1
    done
    cp -- "$dll" "$staging/mods/CombatSolver/CombatSolver.dll" || return 1
    cp -- "$manifest" "$staging/mods/CombatSolver/CombatSolver.json" || return 1
    cp -- "$ritsu" "$staging/mods/CombatSolverHeadlessRitsuLib/STS2-RitsuLib.dll" || return 1
    cp -- "$ritsu_manifest" "$staging/mods/CombatSolverHeadlessRitsuLib/STS2-RitsuLib.json" || return 1
    printf '%s\n' 'CombatSolver isolated headless dependency' >"$staging/mods/CombatSolverHeadlessRitsuLib/.combatsolver-headless-only" || return 1
    actual="$(hr_snapshot_id "$source" "$dll" "$manifest" "$ritsu" "$ritsu_manifest")" || return 1
    [[ $actual == "$expected" ]] || {
        hr_error "snapshot source changed while copying; unpublished snapshot retained at $staging"; return 1;
    }
    id_temp="$(mktemp --tmpdir="$HR_ROOT" .snapshot-id.XXXXXX)" || return 1
    printf '%s\n' "$expected" >"$id_temp" || return 1
    if [[ -e $HR_ROOT/game ]]; then
        old="$(mktemp -d --tmpdir="$HR_ROOT" retired.XXXXXX)" || return 1
        # Invalidate before changing the tree. Even a crash or final ID rename
        # failure must not leave a new game falsely cached under the old ID.
        rm -f -- "$HR_ROOT/snapshot-id" || return 1
        mv -- "$HR_ROOT/game" "$old/game" || return 1
        echo "HEADLESS_RETIRED snapshot=$old/game" >&2
    else
        rm -f -- "$HR_ROOT/snapshot-id" || return 1
    fi
    mv -- "$staging" "$HR_ROOT/game" || return 1
    mv -f -- "$id_temp" "$HR_ROOT/snapshot-id" || return 1
}
