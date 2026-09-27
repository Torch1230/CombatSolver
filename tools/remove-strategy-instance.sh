#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
source "$script_dir/headless-runtime.sh"
instance="${1:?instance required}"
HR_WORKTREE="$repo_root"
root="${COMBATSOLVER_HEADLESS_ROOT:-$repo_root/.local/headless-instances/$instance}"
[[ -f "$root/runtime-owner.json" ]] || { echo "strategy instance ownership marker missing: $root" >&2; exit 1; }
executable="$root/game/SlayTheSpire2"
hr_init "$root" "$instance" "$executable" "$root/data" exclusive 4096 2 120
hr_remove_instance
