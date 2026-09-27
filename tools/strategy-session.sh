#!/usr/bin/env bash
set -Eeuo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$root"
dotnet run --project tools/CheckpointTool/CheckpointTool.csproj -c Release --no-launch-profile -- session "$@"
