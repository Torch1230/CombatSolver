#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][Alias('ReportsRoot')][string]$InputPath,
    [string]$OutputDirectory,
    [ValidateSet('Preflight','RestoreOnly','ReplayRecorded','SearchOnly','DeploySolver')][string]$ReplayMode = 'RestoreOnly',
    [string]$CheckpointSelector = 'latest',
    [string]$ReplayPolicyOverridePath,
    [string]$ManifestPath,
    [string]$Sts2GameRoot,
    [string]$RitsuWorkshopRoot,
    [ValidateRange(10,3600)][int]$TimeoutSeconds = 120,
    [ValidateRange(0,10000)][int]$MaxReports = 0,
    [switch]$Resume,
    [switch]$RetryFailures,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
if ($PreflightOnly) { $ReplayMode = 'Preflight' }
$arguments = @('run','--project',(Join-Path $PSScriptRoot 'CheckpointTool/CheckpointTool.csproj'),'-c','Release','--verbosity','quiet','--','batch',[IO.Path]::GetFullPath($InputPath),'--mode',$ReplayMode,'--selector',$CheckpointSelector,'--timeout',"$TimeoutSeconds",'--max-items',"$MaxReports")
foreach ($pair in @(@('--output',$OutputDirectory), @('--policy',$ReplayPolicyOverridePath), @('--manifest',$ManifestPath), @('--game-root',$Sts2GameRoot), @('--ritsu-root',$RitsuWorkshopRoot))) {
    if ($pair[1]) { $arguments += @($pair[0], [IO.Path]::GetFullPath($pair[1])) }
}
if ($Resume) { $arguments += '--resume' }
if ($RetryFailures) { $arguments += '--retry-failures' }
& dotnet @arguments
exit $LASTEXITCODE
