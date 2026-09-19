#requires -Version 7.4
param(
    [ValidateSet('manual-same', 'manual-different', 'frozen-same', 'frozen-different', 'auto-off-fullauto', 'single-step-execute')]
    [string]$Case
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$fixture = Get-Content -LiteralPath (Join-Path $projectRoot "coverage/unattended/toasty-qol-$Case.json") -Raw | ConvertFrom-Json
$arguments = @{
    ScenarioId = $fixture.scenarioId
    CharacterId = $fixture.characterId
    EncounterId = $fixture.encounterId
    Seed = $fixture.seed
    RelicsJson = ConvertTo-Json -InputObject @($fixture.relics) -Depth 8 -Compress
    EnemyCurrentHp = $fixture.enemyCurrentHp
    ShortSearchBudgetOverrideMilliseconds = $fixture.shortSearchBudgetOverrideMilliseconds
    TimeoutSeconds = $fixture.timeoutSeconds
    EnableNoGcRegionForTest = 0
    ForceShortSearchOnly = $true
    SingleStepAfterInitialSearch = $true
    CleanupInstanceOnExit = $true
}
if ($fixture.singleStepResumeModeForTest) {
    $arguments.SingleStepResumeModeForTest = $fixture.singleStepResumeModeForTest
}
& (Join-Path $PSScriptRoot 'run-unattended-test.ps1') @arguments
if ($LASTEXITCODE -ne 0) { throw "QoL contract $Case failed with exit code $LASTEXITCODE" }
