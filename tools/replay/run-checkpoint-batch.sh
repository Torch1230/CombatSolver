#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$script_dir/CheckpointTool/CheckpointTool.csproj" -c Release --verbosity quiet -- batch "$@"
