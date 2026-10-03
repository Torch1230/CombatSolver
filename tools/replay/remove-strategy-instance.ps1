#requires -Version 7.4
param([Parameter(Mandatory)][string]$Instance, [Parameter(Mandatory)][string]$SourceGameRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../testing/headless-runtime.ps1')
$repository = Get-HeadlessCanonicalPath (Join-Path $PSScriptRoot '../..')
$root = Join-Path $repository ".local/headless-instances/$Instance"
if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
$owner = Join-Path $root 'instance.json'
if (-not (Test-Path -LiteralPath $owner -PathType Leaf)) {
    throw "Instance ownership marker missing: $root"
}
$marker = Get-Content -LiteralPath $owner -Raw | ConvertFrom-Json -AsHashtable
$context = New-HeadlessRuntimeContext $repository $SourceGameRoot $Instance 'exclusive' 4096 2 120
Remove-HeadlessRuntimeInstance $context
