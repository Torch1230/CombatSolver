#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $root
try {
    dotnet run --project tools/CheckpointTool/CheckpointTool.csproj -c Release --no-launch-profile -- session @args
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
