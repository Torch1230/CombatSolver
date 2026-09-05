#requires -Version 7.4
# No game is launched. Host capacity and the game-name census are controlled
# fixtures; launcher identity validation still uses the real pwsh process.
param([switch]$ProfileOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'headless-runtime.ps1')

function Assert-LauncherNotCancelled { }
function Get-HeadlessHostCapacity { return @{ cpu = 8; totalMiB = 32768; availableMiB = 24576 } }
function Get-Process {
    [CmdletBinding()]
    param([string]$Name, [int]$Id)
    if ($PSBoundParameters.ContainsKey('Name')) {
        if ($script:UnknownGame) { return [pscustomobject]@{ Id = 987654 } }
        return
    }
    return Microsoft.PowerShell.Management\Get-Process -Id $Id -ErrorAction SilentlyContinue
}
function Assert-HostFixture([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Headless infrastructure assertion failed: $Message" }
}
function Assert-AdmissionRejected([hashtable]$Context) {
    $rejected = $false
    try { Enter-HeadlessHostLease $Context $null } catch {
        if ($_.Exception.Message -notlike 'Headless host admission timed out*') { throw }
        $rejected = $true
    } finally { Exit-HeadlessHostLease $Context }
    Assert-HostFixture $rejected 'expected bounded admission timeout'
    Assert-HostFixture (-not (Test-Path -LiteralPath $Context.LeasePath)) 'failed request left a queued lease'
}

if ($ProfileOnly) {
    $profileFixture = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-profile-test-' + [Guid]::NewGuid().ToString('N'))
    $sourceProfile = Join-Path $profileFixture 'source'
    $copiedProfile = Join-Path $profileFixture 'copied'
    New-Item -ItemType Directory -Path $sourceProfile, $copiedProfile -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceProfile 'settings.save') -Value 'fixture-original'
    Copy-HeadlessProfileTree $sourceProfile $copiedProfile
    Set-Content -LiteralPath (Join-Path $copiedProfile 'source/settings.save') -Value 'fixture-private'
    Assert-HostFixture ((Get-Content -LiteralPath (Join-Path $sourceProfile 'settings.save') -Raw).Trim() -eq 'fixture-original') 'copied profile mutated source'
    New-Item -ItemType SymbolicLink -Path (Join-Path $sourceProfile 'linked.save') -Target (Join-Path $sourceProfile 'settings.save') | Out-Null
    $rejected = $false
    try { Copy-HeadlessProfileTree $sourceProfile $copiedProfile } catch {
        if ($_.Exception.Message -notlike 'Cannot isolate a profile containing a reparse point:*') { throw }
        $rejected = $true
    }
    Assert-HostFixture $rejected 'profile symlink was not rejected'
    Write-Output "HEADLESS_PROFILE_SELFTEST_PASS copy/source-preservation/reparse-rejection evidence=$profileFixture"
    return
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('combatsolver-headless-host-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:UnknownGame = $false
$contexts = @()
foreach ($name in @('a', 'b', 'c')) {
    $contexts += @{
        Instance = $name; Root = Join-Path $testRoot $name; GameRoot = Join-Path $testRoot "$name/game"
        HostRoot = $testRoot; LeasePath = Join-Path $testRoot "$name.json"
        Mode = 'parallel'; MemoryMiB = 4096; Cpu = 2; QueueSeconds = 1
        LeaseToken = ''; ArtifactId = 'fixture-build'
    }
}
try {
    $a, $b, $c = $contexts
    Enter-HeadlessHostLease $a $null
    Enter-HeadlessHostLease $b $null
    Assert-HostFixture ((Get-HeadlessHostLeases $a).Count -eq 2) 'two independent parallel instances were not admitted'
    Assert-AdmissionRejected $c

    # A release is token/identity scoped, even when someone presents the path
    # of another live lease. It must not evict either admitted instance.
    $impostor = $a.Clone(); $impostor.LeaseToken = 'wrong-token'
    Exit-HeadlessHostLease $impostor
    Assert-HostFixture (Test-Path -LiteralPath $a.LeasePath) 'wrong token released another launcher'
    $c.Mode = 'exclusive'
    Assert-AdmissionRejected $c
    Exit-HeadlessHostLease $a
    Assert-HostFixture (Test-Path -LiteralPath $b.LeasePath) 'releasing one instance affected its peer'
    Exit-HeadlessHostLease $b
    Enter-HeadlessHostLease $c $null
    Assert-AdmissionRejected $a
    Exit-HeadlessHostLease $c

    $a.Cpu = 9
    Assert-AdmissionRejected $a
    $a.Cpu = 2; $a.MemoryMiB = 24576
    Assert-AdmissionRejected $a
    $a.MemoryMiB = 4096; $script:UnknownGame = $true
    Assert-AdmissionRejected $a
    $script:UnknownGame = $false

    # A reused PID with a different birth is stale, not permission to stop it.
    $identity = Get-HeadlessProcessIdentity ([Diagnostics.Process]::GetCurrentProcess())
    $staleIdentity = $identity.Clone()
    $staleIdentity.birth = ([DateTimeOffset]$identity.birth).AddSeconds(-1).ToString('O')
    Assert-HostFixture (-not (Get-HeadlessIdentityState $staleIdentity).alive) 'PID birth was ignored'
    Write-HeadlessJson $a.LeasePath @{ schemaVersion = 1; state = 'pending'; mode = 'parallel'; cpu = 2;
        memoryMiB = 4096; runtimeRoot = $a.Root; token = 'stale'; game = $null; launcher = $staleIdentity }
    Assert-HostFixture ((Get-HeadlessHostLeases $a).Count -eq 0) 'dead identity did not release stale admission'
    Assert-HostFixture (Get-HeadlessIdentityState $identity).alive 'stale lease cleanup disturbed its unrelated PID'

    # A warm-game record owns its reservation after the request launcher exits.
    # Only this fake game's liveness is stubbed; no executable is spawned.
    $originalIdentityState = ${function:Get-HeadlessIdentityState}
    $script:FakeGameAlive = $true
    function Get-HeadlessIdentityState([object]$Identity) {
        if ($null -ne $Identity -and [int]$Identity.pid -eq 987655) {
            return @{ alive = $script:FakeGameAlive; workingSetMiB = 1024 }
        }
        & $originalIdentityState $Identity
    }
    $a.LeaseToken = 'warm-token'
    Write-HeadlessJson $a.LeasePath @{ schemaVersion = 1; state = 'running'; mode = 'parallel'; cpu = 2;
        memoryMiB = 4096; runtimeRoot = $a.Root; token = $a.LeaseToken; launcher = $null;
        game = @{ pid = 987655; birth = $identity.birth; exe = Join-Path $a.Root 'game\SlayTheSpire2.exe' } }
    Exit-HeadlessHostLease $a
    Assert-HostFixture (Test-Path -LiteralPath $a.LeasePath) 'warm game lost its reservation'
    $script:FakeGameAlive = $false
    Exit-HeadlessHostLease $a
    Assert-HostFixture (-not (Test-Path -LiteralPath $a.LeasePath)) 'exited warm game retained its reservation'
    Write-Output 'HEADLESS_RUNTIME_SELFTEST_PASS parallel2/exclusive/resource/unknown/ownership/stale/warm'
} finally {
    # This fresh directory contains only this fixture's leases and lock. Never
    # invoke the game snapshot cleaner or touch a production host pool here.
    foreach ($context in $contexts) { Exit-HeadlessHostLease $context }
    foreach ($file in Get-ChildItem -LiteralPath $testRoot -File) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
    Remove-Item -LiteralPath $testRoot -Force
}
