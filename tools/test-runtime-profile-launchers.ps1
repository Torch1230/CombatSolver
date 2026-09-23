#requires -Version 7.4
# Cross-platform no-game checks; the child is a disposable .NET console fixture.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver runtime profile ' + [Guid]::NewGuid().ToString('N'))
$fixture = $null
$savedEnvironment = @{}
foreach ($key in @('DOTNET_gcServer', 'COMPlus_gcServer', 'COMBATSOLVER_RUNTIME_PROFILE', 'RUNTIME_PROFILE_TEST_OUTPUT', 'RUNTIME_PROFILE_TEST_EXIT')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
function Assert-Profile([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Runtime profile launcher assertion: $Message" }
}
function Invoke-Launcher([string]$Script, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $Script) + $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        Assert-Profile ($child.WaitForExit(15000)) 'mock launcher timed out'
        return @{ ExitCode = $child.ExitCode; Stdout = $stdout.GetAwaiter().GetResult(); Stderr = $stderr.GetAwaiter().GetResult() }
    } finally {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $fixtureName = 'csgc' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>$fixtureName</AssemblyName></PropertyGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $testRoot 'Fixture.csproj')
    @'
using System.Text.Json;
File.WriteAllText(Environment.GetEnvironmentVariable("RUNTIME_PROFILE_TEST_OUTPUT")!, JsonSerializer.Serialize(new {
    dotnet = Environment.GetEnvironmentVariable("DOTNET_gcServer"),
    complus = Environment.GetEnvironmentVariable("COMPlus_gcServer"),
    profile = Environment.GetEnvironmentVariable("COMBATSOLVER_RUNTIME_PROFILE"),
    directory = Environment.CurrentDirectory,
    arguments = args,
}));
if (args.Length > 0 && args[0] == "hold") Thread.Sleep(60000);
return int.TryParse(Environment.GetEnvironmentVariable("RUNTIME_PROFILE_TEST_EXIT"), out int code) ? code : 0;
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Program.cs')
    & dotnet build (Join-Path $testRoot 'Fixture.csproj') -c Release -o (Join-Path $testRoot 'game directory') --nologo --verbosity quiet
    Assert-Profile ($LASTEXITCODE -eq 0) 'fixture build failed'
    $executable = Join-Path $testRoot ('game directory/' + $fixtureName + $(if ($IsWindows) { '.exe' } else { '' }))
    $launcher = Join-Path $PSScriptRoot 'start-server-gc.ps1'
    $env:DOTNET_gcServer = '0'
    $env:COMPlus_gcServer = '0'
    $env:COMBATSOLVER_RUNTIME_PROFILE = 'parent-profile'
    $env:RUNTIME_PROFILE_TEST_OUTPUT = Join-Path $testRoot 'result.json'
    $env:RUNTIME_PROFILE_TEST_EXIT = '0'
    $arguments = @('with spaces', '', 'embedded"quote', 'literal$(not-a-command)', 'semi;colon', '--option=value', 'trailing\')
    $result = Invoke-Launcher $launcher (@('-GameExecutable', $executable) + $arguments)
    Assert-Profile ($result.ExitCode -eq 0) "launch failed: $($result.Stderr)"
    $observed = Get-Content -LiteralPath $env:RUNTIME_PROFILE_TEST_OUTPUT -Raw | ConvertFrom-Json
    Assert-Profile ($observed.dotnet -eq '1' -and $observed.complus -eq '1' -and $observed.profile -eq 'server-generational') 'child runtime environment differs'
    Assert-Profile ($observed.directory -eq [IO.Path]::GetDirectoryName($executable)) 'child working directory differs'
    Assert-Profile (($observed.arguments | ConvertTo-Json -Compress) -ceq ($arguments | ConvertTo-Json -Compress)) 'game argument round-trip differs'
    Assert-Profile ($env:DOTNET_gcServer -eq '0' -and $env:COMPlus_gcServer -eq '0' -and $env:COMBATSOLVER_RUNTIME_PROFILE -eq 'parent-profile') 'child mutated parent environment'
    $env:RUNTIME_PROFILE_TEST_EXIT = '37'
    $result = Invoke-Launcher $launcher @('-GameExecutable', $executable)
    Assert-Profile ($result.ExitCode -eq 37) 'child failure exit code was lost'
    $env:RUNTIME_PROFILE_TEST_EXIT = '0'

    $heldPath = Join-Path $testRoot 'held.json'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.Environment['RUNTIME_PROFILE_TEST_OUTPUT'] = $heldPath
    $start.ArgumentList.Add('hold')
    $fixture = [Diagnostics.Process]::Start($start)
    for ($attempt = 0; $attempt -lt 100 -and -not (Test-Path -LiteralPath $heldPath); $attempt++) { Start-Sleep -Milliseconds 10 }
    Assert-Profile (Test-Path -LiteralPath $heldPath) 'held fixture did not start'
    $result = Invoke-Launcher $launcher @('-GameExecutable', $executable)
    Assert-Profile ($result.ExitCode -ne 0 -and $result.Stderr.Contains('already running')) 'existing process was not rejected'
    $fixture.Refresh()
    Assert-Profile (-not $fixture.HasExited) 'launcher terminated an existing process'

    $result = Invoke-Launcher (Join-Path $PSScriptRoot 'run-unattended-test.ps1') @('-RuntimeProfile', 'invalid-profile')
    Assert-Profile ($result.ExitCode -ne 0 -and $result.Stderr.Contains('RuntimeProfile') -and $result.Stderr.Contains('ValidateSet')) 'unknown profile failed outside parameter validation'

    # Evaluate only the real pure setup and already-owned marker branch. Process
    # discovery, admission, and termination are never invoked by these checks.
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'run-unattended-test.ps1'), [ref]$tokens, [ref]$parseErrors)
    Assert-Profile ($parseErrors.Count -eq 0) 'unattended launcher syntax is invalid'
    $assignments = $ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -in @('$runtimeEnvironment', '$runtimeEnvironmentKey')
    }, $true)
    Assert-Profile ($assignments.Count -eq 2) 'cannot isolate actual runtime environment setup'
    $setup = [scriptblock]::Create(($assignments | ForEach-Object { $_.Extent.Text }) -join "`n")
    foreach ($RuntimeProfile in @('default', 'server-generational')) {
        . $setup
        $expectedGc = if ($RuntimeProfile -eq 'default') { '0' } else { '1' }
        $expectedProfile = if ($RuntimeProfile -eq 'default') { '' } else { 'server-generational' }
        Assert-Profile ($runtimeEnvironment.DOTNET_gcServer -eq $expectedGc -and
            $runtimeEnvironment.COMPlus_gcServer -eq $expectedGc -and
            $runtimeEnvironment.COMBATSOLVER_RUNTIME_PROFILE -eq $expectedProfile) 'unattended child environment differs'
    }
    $clauses = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.IfStatementAst] }, $true) |
        ForEach-Object { $_.Clauses } | Where-Object { $_.Item1.Extent.Text.Contains('$marker.runtimeEnvironmentKey') })
    Assert-Profile ($clauses.Count -eq 1) 'cannot isolate actual owned-marker restart branch'
    $restart = [scriptblock]::Create('if (' + $clauses[0].Item1.Extent.Text + ') ' + $clauses[0].Item2.Extent.Text)
    function Stop-ClaimedProcessAndRemoveDependency($OwnedProcess, $Birth) {
        Assert-Profile ($OwnedProcess.Id -eq 123456 -and $Birth -eq 'fixture-birth') 'restart lost owned process identity'
        $script:stopCalls++
    }
    $runtimeContext = @{ ArtifactId = 'same' }
    foreach ($markerCase in @('same', 'changed', 'legacy')) {
        $marker = @{ artifactId = 'same'; runtimeEnvironmentKey = $runtimeEnvironmentKey }
        if ($markerCase -eq 'changed') { $marker.runtimeEnvironmentKey = 'another-runtime' }
        if ($markerCase -eq 'legacy') { $marker.Remove('runtimeEnvironmentKey') }
        $process = [pscustomobject]@{ Id = 123456 }
        $processIdentityStartTimeUtc = 'fixture-birth'
        $script:stopCalls = 0
        . $restart
        $expectedStops = if ($markerCase -eq 'same') { 0 } else { 1 }
        Assert-Profile ($script:stopCalls -eq $expectedStops) "wrong restart decision for $markerCase"
    }
    Assert-Profile ($env:DOTNET_gcServer -eq '0' -and $env:COMPlus_gcServer -eq '0' -and $env:COMBATSOLVER_RUNTIME_PROFILE -eq 'parent-profile') 'unattended environment setup mutated parent variables'
    Write-Output 'RUNTIME_PROFILE_LAUNCHERS_PS_OK arguments/environment/exit-code/existing-process/invalid-profile/marker-reuse'
} finally {
    if ($null -ne $fixture) {
        if (-not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
        $fixture.Dispose()
    }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
