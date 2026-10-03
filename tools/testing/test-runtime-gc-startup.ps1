#requires -Version 7.4
# Three fresh native processes sharing one owned game snapshot. No Steam launch.
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$RitsuWorkshopRoot,
    [Parameter(Mandatory)][string]$Build,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot -Parent
$game=[IO.Path]::GetFullPath($GameRoot)
$ritsu=[IO.Path]::GetFullPath($RitsuWorkshopRoot)
$launcher=Join-Path $PSScriptRoot 'run-unattended-test.ps1'
$instance='gc-auto-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
if($env:COMBATSOLVER_HEADLESS_ROOT){throw 'Use the repository-owned default instance root.'}
$instanceRoot=Join-Path $source ".local/headless-instances/$instance"
$evidence=[IO.Path]::GetFullPath($Output)
if(Test-Path $evidence){throw 'Evidence already exists'}
New-Item -ItemType Directory $evidence | Out-Null
$originalConfig=Join-Path $game 'data_sts2_windows_x86_64\sts2.runtimeconfig.json'
$originalHash=(Get-FileHash $originalConfig).Hash
$initial=Get-Content $originalConfig -Raw | ConvertFrom-Json
if($initial.runtimeOptions.configProperties.'System.GC.Server' -eq $true){
    throw 'This test needs an unmodified WorkstationGC source configuration.'
}
$settingsPath=Join-Path $instanceRoot 'Roaming\SlayTheSpire2\combat_solver_settings.json'
New-Item -ItemType Directory (Split-Path $settingsPath) -Force | Out-Null
@{performanceMigrationVersion=246;autoConfigureServerGc=$true;enableNoGcRegion=$true;onlineStatisticsEnabled=$false} | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8NoBOM
$environmentKeys=@('COMBATSOLVER_TEST_AUTO_GC_CONFIG','DOTNET_gcServer','COMPlus_gcServer','COMBATSOLVER_RUNTIME_PROFILE')
$savedEnvironment=@{}
foreach($key in $environmentKeys){$savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key)}
$env:COMBATSOLVER_TEST_AUTO_GC_CONFIG='1'
Remove-Item Env:DOTNET_gcServer,Env:COMPlus_gcServer,Env:COMBATSOLVER_RUNTIME_PROFILE -ErrorAction SilentlyContinue
try {
    for($launch=0;$launch -lt 3;$launch++){
        $out=Join-Path $evidence "launch-$launch"
        if($launch -eq 1){
            $settings=Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
            $settings.autoConfigureServerGc=$false
            $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding utf8NoBOM
        }
        & pwsh -NoProfile -File $launcher -Sts2GameRoot $game -RitsuWorkshopRoot $ritsu -CombatSolverBuildDir ([IO.Path]::GetFullPath($Build)) -HeadlessInstance $instance -RuntimeProfile default -EvidenceDirectory $out -GeneratedScenarioPath (Join-Path $source 'coverage\runtime-gc-profile\strict-starter.json') -PerformancePresetForTest Low -SearchBudgetOverrideMilliseconds 3000 -SearchMaxExpandedNodesForTest 300 -SearchMaxDegreeOfParallelismForTest 1 -HeadlessMemoryReservationMiB 12288 -HeadlessCpuReservation 1 -TimeoutSeconds 120 -ExitOnComplete
        if($LASTEXITCODE -ne 0){throw "Native launch $launch failed"}
        $result=Get-Content (Join-Path $out 'result.json') -Raw | ConvertFrom-Json
        $runtime=(Get-Content (Join-Path $out 'search-result.json') -Raw | ConvertFrom-Json).runtime
        $expectedActive=$launch -eq 1
        if($result.status -ne 'Passed' -or $runtime.serverGc -ne $expectedActive -or
            $runtime.effectiveNoGcRegionEnabled -ne (-not $expectedActive) -or
            $runtime.savedNoGcRegionEnabled -ne $true){throw "Actual runtime mismatch at launch $launch"}
        $privateConfig=Join-Path $instanceRoot 'game\data_sts2_windows_x86_64\sts2.runtimeconfig.json'
        Copy-Item $privateConfig (Join-Path $out 'next-launch.runtimeconfig.json')
        $config=Get-Content $privateConfig -Raw | ConvertFrom-Json
        $prepared=$config.runtimeOptions.configProperties.'CombatSolver.RuntimeProfile' -eq 'server-generational'
        if($prepared -ne ($launch -eq 0)){throw 'Next-launch config mismatch'}
        Copy-Item (Join-Path $instanceRoot '*.log') $out
        "AUTO_NATIVE_LAUNCH=$launch runtime=$($runtime|ConvertTo-Json -Compress) nextPrepared=$prepared"
    }
    if((Get-FileHash $originalConfig).Hash -ne $originalHash){throw 'Original game config changed'}
    'AUTO_STARTUP_NATIVE_PASSED original_config_unchanged=true'
 } finally {
    if(Test-Path $instanceRoot){
        Get-ChildItem $instanceRoot -Filter '*.log' -File -Recurse | ForEach-Object {
            $relative=[IO.Path]::GetRelativePath($instanceRoot,$_.FullName)
            $destination=Join-Path $evidence ('diagnostic-'+($relative -replace '[\\/]', '_'))
            Copy-Item $_.FullName $destination
        }
    }
    foreach($key in $environmentKeys){[Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key])}
    & pwsh -NoProfile -File $launcher -Sts2GameRoot $game -RitsuWorkshopRoot $ritsu -HeadlessInstance $instance -StopInstance -CleanupInstanceOnExit
    if($LASTEXITCODE -ne 0){throw 'Owned cleanup failed'}
}
