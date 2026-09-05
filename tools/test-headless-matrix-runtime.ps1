#requires -Version 7.4
# No game. The real matrix invokes this file, copied as its mock launcher.
param(
    [string]$ScenarioId, [string]$Sts2GameRoot, [string]$RitsuWorkshopRoot,
    [string]$HeadlessInstance, [string]$HeadlessExecutionMode,
    [int]$HeadlessMemoryReservationMiB, [int]$HeadlessCpuReservation,
    [int]$HeadlessQueueTimeoutSeconds, [string]$CombatSolverBuildDir,
    [int]$TimeoutSeconds, [switch]$StopAfterCombatRootSnapshotAssertion,
    [switch]$ExitOnComplete, [switch]$StopInstance, [switch]$CancellationOnly, [switch]$StopOnly
)
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($PSCommandPath) -eq 'run-unattended-test.ps1') {
    $root = $env:COMBATSOLVER_HEADLESS_ROOT
    if ($StopInstance) { $ScenarioId = 'STOP-INSTANCE' }
    @{ root=$root; instance=$HeadlessInstance; scenario=$ScenarioId; mode=$HeadlessExecutionMode
        memory=$HeadlessMemoryReservationMiB; cpu=$HeadlessCpuReservation
        queue=$HeadlessQueueTimeoutSeconds; build=$CombatSolverBuildDir } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath $env:MATRIX_TEST_LOG
    if ($ScenarioId -eq 'STOP-INSTANCE') {
        Remove-Item -LiteralPath (Join-Path $root 'process.json')
        exit 0
    }
    $marker = @{ pid=999999999; processStartTimeUtc='2026-01-01T00:00:00.0000000Z'
        instance=$HeadlessInstance; runtimeRoot=$root
        executable=Join-Path $root 'game/SlayTheSpire2.exe'
        appData=Join-Path $root 'Roaming'; dataDir=Join-Path $root 'Roaming/SlayTheSpire2' }
    switch ($env:MATRIX_TEST_MODE) {
        foreign { $marker.instance='another-instance' }
        legacy { $marker=@{pid=999999999} }
        owner {
            $ownerPath=Join-Path $root 'instance.json'
            $owner=Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json -AsHashtable
            $owner.repositoryRoot=Join-Path $root 'foreign'
            $owner | ConvertTo-Json | Set-Content -LiteralPath $ownerPath
        }
    }
    $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'process.json')
    if ($env:MATRIX_TEST_MODE -eq 'hang') {
        Set-Content -LiteralPath (Join-Path $root 'mock-ready') -Value ready
        Start-Sleep -Seconds 20
    }
    exit 0
}

function Assert-Matrix([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Matrix mock assertion failed: $Message" }
}
if ($StopOnly) {
    # Real PowerShell launcher entry; no Windows game is available here.
    # Stale/absent and foreign live-pwsh rejection use actual process lookup.
    $stopRoot=Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-stop-ps-' + [Guid]::NewGuid().ToString('N'))
    $stopRepo=Join-Path $stopRoot 'repo'
    New-Item -ItemType Directory -Path (Join-Path $stopRepo 'tools') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-unattended-test.ps1'),
        (Join-Path $PSScriptRoot 'headless-runtime.ps1') -Destination (Join-Path $stopRepo 'tools')
    $savedRoot=$env:COMBATSOLVER_HEADLESS_ROOT; $savedHost=$env:COMBATSOLVER_HEADLESS_HOST_ROOT
    try {
        $env:COMBATSOLVER_HEADLESS_ROOT=Join-Path $stopRoot 'runtime'
        $env:COMBATSOLVER_HEADLESS_HOST_ROOT=Join-Path $stopRoot 'host'
        . (Join-Path $PSScriptRoot 'headless-runtime.ps1')
        $context=New-HeadlessRuntimeContext $stopRepo (Join-Path $stopRoot 'missing-source') stop exclusive 4096 2 1
        New-Item -ItemType Directory -Path $context.Root, $context.HostRoot -Force | Out-Null
        Initialize-HeadlessRuntimeOwner $context
        $markerPath=Join-Path $context.Root 'process.json'
        $marker=@{pid=999999999;processStartTimeUtc='2026-01-01T00:00:00.0000000Z';instance='stop';runtimeRoot=$context.Root
            executable=Join-Path $context.Root 'game/SlayTheSpire2.exe';appData=Join-Path $context.Root 'Roaming'
            dataDir=Join-Path $context.Root 'Roaming/SlayTheSpire2'}
        $stopArgs=@('-NoProfile','-File',(Join-Path $stopRepo 'tools/run-unattended-test.ps1'),'-StopInstance',
            '-HeadlessInstance','stop','-Sts2GameRoot',(Join-Path $stopRoot 'missing-source'),
            '-RitsuWorkshopRoot',(Join-Path $stopRoot 'missing-dependency'),'-CombatSolverBuildDir',(Join-Path $stopRoot 'missing-build'))
        Set-Content -LiteralPath (Join-Path $context.HostRoot 'peer.json') -Value peer-sentinel
        $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath
        & pwsh @stopArgs *> (Join-Path $stopRoot 'stale.log')
        Assert-Matrix ($LASTEXITCODE -eq 0 -and -not (Test-Path -LiteralPath $markerPath)) 'real stop did not clear stale marker'
        & pwsh @stopArgs *> (Join-Path $stopRoot 'absent.log')
        Assert-Matrix ($LASTEXITCODE -eq 0) 'absent stop is not idempotent'
        Assert-Matrix (-not (Test-Path -LiteralPath (Join-Path $context.Root 'Roaming')) -and
            -not (Test-Path -LiteralPath (Join-Path $stopRoot 'missing-source')) -and
            -not (Test-Path -LiteralPath (Join-Path $stopRoot 'missing-build'))) 'stop created request/profile/source/build state'
        Set-Content -LiteralPath $markerPath -Value '{}'
        & pwsh @stopArgs *> (Join-Path $stopRoot 'no-pid.log')
        Assert-Matrix ($LASTEXITCODE -ne 0 -and (Test-Path -LiteralPath $markerPath)) 'missing PID was not preserved'
        $self=Get-Process -Id $PID
        $marker.pid=$PID; $marker.processStartTimeUtc=$self.StartTime.ToUniversalTime().ToString('O')
        $marker | ConvertTo-Json | Set-Content -LiteralPath $markerPath
        & pwsh @stopArgs *> (Join-Path $stopRoot 'foreign-process.log')
        Assert-Matrix ($LASTEXITCODE -ne 0 -and (Test-Path -LiteralPath $markerPath)) 'live foreign executable was not rejected'
        Assert-Matrix ((Get-Content -LiteralPath (Join-Path $context.HostRoot 'peer.json') -Raw).Trim() -eq 'peer-sentinel') 'peer lease changed'
        Write-Output "STOP_POWERSHELL_PASS real-launcher/stale/absent/no-pid/foreign-pwsh/no-build-no-request/peer-preserved evidence=$stopRoot"
    } finally {
        $env:COMBATSOLVER_HEADLESS_ROOT=$savedRoot; $env:COMBATSOLVER_HEADLESS_HOST_ROOT=$savedHost
    }
    return
}
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-matrix-' + [Guid]::NewGuid().ToString('N'))
$repo=Join-Path $testRoot 'repo with space'
$source=Join-Path $testRoot 'source'
New-Item -ItemType Directory -Path (Join-Path $repo 'tools'), (Join-Path $repo 'docs'),
    (Join-Path $source 'mods/.combatsolver-headless-ritsulib') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-headless-matrix.ps1'),
    (Join-Path $PSScriptRoot 'headless-runtime.ps1') -Destination (Join-Path $repo 'tools')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $repo 'tools/run-unattended-test.ps1')
Set-Content -LiteralPath (Join-Path $source 'mods/.combatsolver-headless-ritsulib/sentinel') -Value source-sentinel
Set-Content -LiteralPath (Join-Path $repo 'docs/TEST_MATRIX.md') -Value @(
    'pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MOCK-A',
    'pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MOCK-B')
$runner=Join-Path $repo 'tools/run-headless-matrix.ps1'
$common=@('-NoProfile','-File',$runner,'-Sts2GameRoot',$source,
    '-ResultsPath',(Join-Path $testRoot 'results.jsonl'),
    '-RitsuWorkshopRoot',(Join-Path $testRoot 'ritsu'),'-HeadlessExecutionMode','parallel',
    '-HeadlessMemoryReservationMiB','2048','-HeadlessCpuReservation','1',
    '-HeadlessQueueTimeoutSeconds','3','-CombatSolverBuildDir',(Join-Path $testRoot 'frozen build'))
$previousRoot=$env:COMBATSOLVER_HEADLESS_ROOT
$previousHost=$env:COMBATSOLVER_HEADLESS_HOST_ROOT
$previousMode=$env:MATRIX_TEST_MODE
$previousLog=$env:MATRIX_TEST_LOG
try {
    $env:COMBATSOLVER_HEADLESS_HOST_ROOT=Join-Path $testRoot 'host'
    if (-not $CancellationOnly) {
    $env:COMBATSOLVER_HEADLESS_ROOT=Join-Path $testRoot 'explicit'
    $env:MATRIX_TEST_MODE='normal'; $env:MATRIX_TEST_LOG=Join-Path $testRoot 'normal.jsonl'
    & pwsh @common -HeadlessInstance explicit *> (Join-Path $testRoot 'normal.log')
    Assert-Matrix ($LASTEXITCODE -eq 0) 'warm matrix failed'
    $rows=@(Get-Content -LiteralPath $env:MATRIX_TEST_LOG | ConvertFrom-Json)
    Assert-Matrix ($rows.Count -eq 3 -and $rows[2].scenario -eq 'STOP-INSTANCE') 'warm tail did not exit'
    foreach ($row in $rows) {
        Assert-Matrix ($row.root -eq $env:COMBATSOLVER_HEADLESS_ROOT -and $row.instance -eq 'explicit' -and
            $row.mode -eq 'parallel' -and $row.memory -eq 2048 -and $row.cpu -eq 1 -and $row.queue -eq 3 -and
            $row.build -eq (Join-Path $testRoot 'frozen build')) 'runtime arguments changed at a boundary'
    }
    Write-Output 'MATRIX_MOCK_PASS explicit-instance/warm-final-exit/parameter-forwarding'
    foreach ($mode in @('foreign','owner','legacy')) {
        $env:COMBATSOLVER_HEADLESS_ROOT=Join-Path $testRoot $mode
        $env:MATRIX_TEST_MODE=$mode; $env:MATRIX_TEST_LOG=Join-Path $testRoot "$mode.jsonl"
        & pwsh @common -HeadlessInstance $mode -MaxCases 1 *> (Join-Path $testRoot "$mode.log")
        Assert-Matrix ($LASTEXITCODE -ne 0) "$mode did not fail closed"
        Assert-Matrix (Test-Path -LiteralPath (Join-Path $env:COMBATSOLVER_HEADLESS_ROOT 'process.json')) 'foreign marker deleted'
        Assert-Matrix (@(Get-Content -LiteralPath $env:MATRIX_TEST_LOG).Count -eq 1) 'cleanup launcher was invoked for foreign ownership'
        Write-Output "MATRIX_MOCK_PASS $mode-rejected-and-preserved"
    }
    }
    # Test cancellation branch with a deterministic console adapter. The child
    # pwsh and its tree are real; this does not claim native Windows Ctrl+C proof.
    $env:COMBATSOLVER_HEADLESS_ROOT=Join-Path $testRoot 'cancel'
    $env:MATRIX_TEST_MODE='hang'; $env:MATRIX_TEST_LOG=Join-Path $testRoot 'cancel.jsonl'
    $cancelDriver=Join-Path $testRoot 'cancel-driver.ps1'
    $driver=@'
param([string]$Runner,[string]$Source)
Add-Type @"
using System;
using System.Diagnostics;
using System.IO;
public static class CombatSolverHeadlessMatrixCancellation {
 public static bool IsCancellationRequested { get; private set; }
 public static void Install() { }
 public static void Uninstall() { }
 public static bool WaitForExit(Process p, bool observe) {
  if (observe) {
   var limit=DateTime.UtcNow.AddSeconds(8);
   var ready=Path.Combine(Environment.GetEnvironmentVariable("COMBATSOLVER_HEADLESS_ROOT"),"mock-ready");
   while (!File.Exists(ready) && !p.WaitForExit(50) && DateTime.UtcNow<limit) { }
   IsCancellationRequested=true; return false;
  }
  p.WaitForExit(); return true;
 }
}
"@
& $Runner -Sts2GameRoot $Source -HeadlessInstance cancel -MaxCases 1 -ResultsPath (Join-Path $Source 'cancel-results.jsonl')
exit $LASTEXITCODE
'@
    Set-Content -LiteralPath $cancelDriver -Value $driver
    & pwsh -NoProfile -File $cancelDriver -Runner $runner -Source $source *> (Join-Path $testRoot 'cancel.log')
    Assert-Matrix ($LASTEXITCODE -eq 130) 'cancellation exit was not preserved'
    Assert-Matrix (-not (Test-Path -LiteralPath (Join-Path $env:COMBATSOLVER_HEADLESS_ROOT 'process.json'))) 'cancelled warm marker was not cleaned'
    $rows=@(Get-Content -LiteralPath $env:MATRIX_TEST_LOG | ConvertFrom-Json)
    Assert-Matrix ($rows.Count -eq 2 -and $rows[1].scenario -eq 'STOP-INSTANCE') 'cancel cleanup did not use same instance'
    Assert-Matrix ((Get-Content -LiteralPath (Join-Path $source 'mods/.combatsolver-headless-ritsulib/sentinel') -Raw).Trim() -eq 'source-sentinel') 'source dependency was touched'
    Write-Output "MATRIX_MOCK_PASS cancel-adapter/source-preserved evidence=$testRoot"
} finally {
    $env:COMBATSOLVER_HEADLESS_ROOT=$previousRoot
    $env:COMBATSOLVER_HEADLESS_HOST_ROOT=$previousHost
    $env:MATRIX_TEST_MODE=$previousMode
    $env:MATRIX_TEST_LOG=$previousLog
}
