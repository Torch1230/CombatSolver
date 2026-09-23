#!/usr/bin/env bash
set -Eeuo pipefail
if (($# < 1)) || [[ $1 == --help ]]; then
    echo 'Usage: start-server-gc.sh /path/to/SlayTheSpire2 [game arguments...]'
    exit 0
fi
executable=$(realpath -e -- "$1")
shift
[[ -f $executable && -x $executable ]] || { echo 'Expected the game executable.' >&2; exit 2; }
for candidate in /proc/[0-9]*/exe; do
    if [[ $(readlink -f -- "$candidate" 2>/dev/null) == "$executable" ]]; then
        echo 'This game is already running. Close it before selecting a different runtime profile.' >&2
        exit 2
    fi
done
echo 'Starting with server-generational GC for this process; saved solver settings are unchanged.'
cd -- "$(dirname -- "$executable")"
exec env DOTNET_gcServer=1 COMPlus_gcServer=1 \
    COMBATSOLVER_RUNTIME_PROFILE=server-generational "$executable" "$@"
