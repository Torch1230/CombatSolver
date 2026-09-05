# Windows headless infrastructure only. Each launcher runs in a separate pwsh.
# The host lease is deliberately independent of the request/process protocol.

function Get-HeadlessCanonicalPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-HeadlessNoReparsePoint([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Headless managed paths must not traverse a reparse point: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Write-HeadlessJson([string]$Path, [object]$Value) {
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Copy-HeadlessProfileTree([string]$Source, [string]$Destination) {
    Assert-HeadlessNoReparsePoint $Source
    Assert-HeadlessNoReparsePoint $Destination
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cannot isolate a profile containing a reparse point: $($item.FullName)"
        }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function New-HeadlessRuntimeContext(
    [string]$RepositoryRoot,
    [string]$SourceGameRoot,
    [string]$Instance,
    [string]$ExecutionMode,
    [int]$MemoryReservationMiB,
    [int]$CpuReservation,
    [int]$QueueTimeoutSeconds
) {
    $repository = Get-HeadlessCanonicalPath $RepositoryRoot
    $source = Get-HeadlessCanonicalPath $SourceGameRoot
    if ([string]::IsNullOrWhiteSpace($Instance)) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($repository.ToUpperInvariant())
        $Instance = "wt-" + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLowerInvariant()
    }
    if ($Instance -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw "HeadlessInstance must be a 1-64 character identifier (letters, digits, dot, underscore, hyphen)."
    }
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $root = if ([string]::IsNullOrWhiteSpace($env:COMBATSOLVER_HEADLESS_ROOT)) {
        Join-Path $localData "CombatSolver\headless-instances\$Instance"
    } else { $env:COMBATSOLVER_HEADLESS_ROOT }
    $root = Get-HeadlessCanonicalPath $root
    $hostRoot = if ([string]::IsNullOrWhiteSpace($env:COMBATSOLVER_HEADLESS_HOST_ROOT)) {
        Join-Path $localData 'CombatSolver\headless-host-v1'
    } else { $env:COMBATSOLVER_HEADLESS_HOST_ROOT }
    $hostRoot = Get-HeadlessCanonicalPath $hostRoot
    foreach ($protected in @($source, $repository, [Environment]::GetFolderPath('UserProfile'), $localData,
            [Environment]::GetFolderPath('ApplicationData'))) {
        $protected = Get-HeadlessCanonicalPath $protected
        if ($protected.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $protected.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Headless runtime cannot own a protected directory or its ancestor: $root"
        }
    }
    if ($root.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $root.Equals($hostRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($hostRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $hostRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The instance runtime must be separate from the source game and host lease directory."
    }
    Assert-HeadlessNoReparsePoint $root
    Assert-HeadlessNoReparsePoint $hostRoot
    foreach ($child in @('game', 'Roaming', 'Local')) {
        Assert-HeadlessNoReparsePoint (Join-Path $root $child)
    }
    $rootBytes = [Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant())
    $leaseKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rootBytes)).ToLowerInvariant()
    return @{
        Instance = $Instance; Root = $root; SourceGameRoot = $source; RepositoryRoot = $repository
        GameRoot = Join-Path $root 'game'; HostRoot = $hostRoot
        LeasePath = Join-Path $hostRoot "$leaseKey.json"
        Mode = $ExecutionMode; MemoryMiB = $MemoryReservationMiB
        Cpu = $CpuReservation; QueueSeconds = $QueueTimeoutSeconds
        LeaseToken = ''; ArtifactId = ''
    }
}

function Initialize-HeadlessRuntimeOwner([hashtable]$Context) {
    $path = Join-Path $Context.Root 'instance.json'
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $owner = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        if ($owner.schemaVersion -ne 1 -or $owner.instance -ne $Context.Instance -or
            -not [string]::Equals($owner.repositoryRoot, $Context.RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($owner.runtimeRoot, $Context.Root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Headless runtime ownership does not match this instance: $path"
        }
    } else {
        # A pre-existing game tree without this ownership marker is never adopted.
        if (Test-Path -LiteralPath $Context.GameRoot) {
            throw "An unowned game tree already exists in the requested runtime: $($Context.GameRoot)"
        }
        Write-HeadlessJson $path @{ schemaVersion = 1; instance = $Context.Instance; runtimeRoot = $Context.Root; repositoryRoot = $Context.RepositoryRoot }
    }
}

function Get-HeadlessSnapshotPlan(
    [hashtable]$Context,
    [string]$CombatSolverDll,
    [string]$CombatSolverManifest,
    [string]$MemoryCleaner,
    [string]$RitsuDll,
    [string]$RitsuManifest
) {
    # Every payload is bound, including other mods and non-DLL mod assets. No
    # hardlinks/junctions: a build in another worktree must not mutate this image.
    $sources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $Context.SourceGameRoot -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cannot freeze a game tree containing a reparse point: $($item.FullName)"
        }
        if (-not $item.PSIsContainer) {
            $sources[[IO.Path]::GetRelativePath($Context.SourceGameRoot, $item.FullName)] = $item.FullName
        }
    }
    $sources['mods\CombatSolver\CombatSolver.dll'] = $CombatSolverDll
    $sources['mods\CombatSolver\CombatSolver.json'] = $CombatSolverManifest
    $sources['mods\CombatSolver\CombatSolver.MemoryCleaner.exe'] = $MemoryCleaner
    $sources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.dll'] = $RitsuDll
    $sources['mods\.combatsolver-headless-ritsulib\STS2-RitsuLib.json'] = $RitsuManifest
    $files = [Collections.Generic.List[object]]::new()
    $identity = [Text.StringBuilder]::new()
    foreach ($relative in @($sources.Keys | Sort-Object -CaseSensitive)) {
        Assert-LauncherNotCancelled
        $source = $sources[$relative]
        Assert-HeadlessNoReparsePoint $source
        $fileHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $files.Add(@{ relative = $relative; source = $source; sha256 = $fileHash })
        [void]$identity.Append($relative).Append([char]0).Append($fileHash).Append([char]0)
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($identity.ToString())
    return @{ id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); files = $files }
}

function Remove-HeadlessOwnedGameTree([hashtable]$Context, [string]$Path) {
    $pathFull = Get-HeadlessCanonicalPath $Path
    $parent = [IO.Path]::GetDirectoryName($pathFull)
    if (-not $parent.Equals($Context.Root, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $pathFull '.combatsolver-frozen-game.json') -PathType Leaf)) {
        throw "Refusing to remove an unowned private game snapshot: $pathFull"
    }
    $owner = Get-Content -LiteralPath (Join-Path $pathFull '.combatsolver-frozen-game.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($owner.schemaVersion -ne 1 -or $owner.runtimeRoot -ne $Context.Root) {
        throw "Private game snapshot ownership does not match this runtime: $pathFull"
    }
    Assert-HeadlessNoReparsePoint $pathFull
    foreach ($entry in Get-ChildItem -LiteralPath $pathFull -Recurse -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing snapshot cleanup through a reparse point: $($entry.FullName)"
        }
    }
    Remove-Item -LiteralPath $pathFull -Recurse -Force
    Write-Host "UNATTENDED_SNAPSHOT_REMOVED path=$pathFull source_game_preserved=true"
}

function Set-HeadlessGameSnapshot([hashtable]$Context, [hashtable]$Plan) {
    Assert-HeadlessNoReparsePoint $Context.GameRoot
    $manifest = Join-Path $Context.GameRoot '.combatsolver-frozen-game.json'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $existing = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
        if ($existing.artifactId -eq $Plan.id) { return }
    }
    # The caller has already stopped its old game and holds both the instance
    # lock and an admitted pending host lease. Never overwrite a loaded image.
    foreach ($candidate in @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)) {
        $candidateHandle = $candidate.SafeHandle
        if (-not $candidate.HasExited -and [string]::Equals($candidate.MainModule.FileName,
                (Join-Path $Context.GameRoot 'SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Cannot replace a private game snapshot while its process is alive."
        }
    }
    $staging = Join-Path $Context.Root ('.game-stage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    Write-HeadlessJson (Join-Path $staging '.combatsolver-frozen-game.json') @{ schemaVersion = 1; artifactId = ''; runtimeRoot = $Context.Root }
    try {
        foreach ($file in $Plan.files) {
            Assert-LauncherNotCancelled
            $destination = Join-Path $staging $file.relative
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath $file.source -Destination $destination -Force
            # This is a source-mutation check at the freeze boundary, not a
            # second deployment verification: builds may run during the copy.
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.sha256) {
                throw "A source payload changed while the private game was being frozen: $($file.source)"
            }
        }
        $dependency = Join-Path $staging 'mods\.combatsolver-headless-ritsulib'
        Set-Content -LiteralPath (Join-Path $dependency '.combatsolver-headless-only') -Value 'CombatSolver private frozen dependency' -Encoding UTF8
        Write-HeadlessJson (Join-Path $staging '.combatsolver-frozen-game.json') @{
            schemaVersion = 1; artifactId = $Plan.id; runtimeRoot = $Context.Root; files = $Plan.files
        }
        if (Test-Path -LiteralPath $Context.GameRoot) {
            Remove-HeadlessOwnedGameTree $Context $Context.GameRoot
        }
        Move-Item -LiteralPath $staging -Destination $Context.GameRoot
    } finally {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            Remove-HeadlessOwnedGameTree $Context $staging
        }
    }
}

function Get-HeadlessProcessIdentity([Diagnostics.Process]$Process) {
    $handle = $Process.SafeHandle
    $Process.Refresh()
    if ($Process.HasExited) { throw "Cannot claim an exited headless process." }
    return @{ pid = $Process.Id; birth = $Process.StartTime.ToUniversalTime().ToString('O'); exe = $Process.MainModule.FileName }
}

function Get-HeadlessIdentityState([object]$Identity) {
    if ($null -eq $Identity) { return @{ alive = $false; workingSetMiB = 0 } }
    if ([int]$Identity.pid -le 0 -or [string]::IsNullOrWhiteSpace([string]$Identity.birth) -or
        [string]::IsNullOrWhiteSpace([string]$Identity.exe)) {
        throw "Malformed headless process identity; refusing to reclaim its lease."
    }
    $candidate = Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return @{ alive = $false; workingSetMiB = 0 } }
    # Access errors propagate: an unreadable process is not a dead process.
    $handle = $candidate.SafeHandle
    $candidate.Refresh()
    if ($candidate.HasExited) { return @{ alive = $false; workingSetMiB = 0 } }
    $birth = ([DateTimeOffset]$Identity.birth).UtcDateTime.ToString('O')
    $matches = $candidate.StartTime.ToUniversalTime().ToString('O') -eq $birth
    if ($matches -and -not [string]::Equals($candidate.MainModule.FileName, [string]$Identity.exe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Live process birth matches but executable changed; preserving its lease.'
    }
    return @{ alive = $matches; workingSetMiB = if ($matches) { [math]::Ceiling($candidate.WorkingSet64 / 1MB) } else { 0 } }
}

function Open-HeadlessHostLock([hashtable]$Context, [switch]$Cleanup) {
    New-Item -ItemType Directory -Path $Context.HostRoot -Force | Out-Null
    Assert-HeadlessNoReparsePoint $Context.HostRoot
    $deadline = [DateTime]::UtcNow.AddSeconds($(if ($Cleanup) { 5 } else { $Context.QueueSeconds }))
    while ($true) {
        if (-not $Cleanup) { Assert-LauncherNotCancelled }
        try {
            return [IO.File]::Open((Join-Path $Context.HostRoot 'admission.lock'),
                [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out acquiring the headless host admission lock." }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-HeadlessHostCapacity {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return @{ cpu = [Environment]::ProcessorCount; totalMiB = [math]::Floor($os.TotalVisibleMemorySize / 1024);
        availableMiB = [math]::Floor($os.FreePhysicalMemory / 1024) }
}

function Get-HeadlessHostLeases([hashtable]$Context) {
    $leases = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Context.HostRoot -Filter '*.json' -File) {
        $lease = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.schemaVersion -ne 1 -or $lease.state -notin @('queued', 'pending', 'running') -or
            $lease.mode -notin @('exclusive', 'parallel') -or [int]$lease.cpu -lt 1 -or [int]$lease.memoryMiB -lt 1 -or
            [string]::IsNullOrWhiteSpace([string]$lease.runtimeRoot) -or
            [string]::IsNullOrWhiteSpace([string]$lease.token)) {
            throw "Unknown or invalid host lease; preserving it: $($file.FullName)"
        }
        $game = Get-HeadlessIdentityState $lease.game
        $launcher = Get-HeadlessIdentityState $lease.launcher
        if (-not $game.alive -and -not $launcher.alive) {
            if ($lease.state -eq 'pending' -and (Test-HeadlessUnboundGame $lease.runtimeRoot)) {
                throw 'An orphan private game may exist between spawn and registration; preserving its pending lease.'
            }
            Remove-Item -LiteralPath $file.FullName -Force
            continue
        }
        if ($game.alive -and -not [string]::Equals([string]$lease.game.exe,
                (Join-Path $lease.runtimeRoot 'game\SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Live host lease executable does not belong to its private runtime: $($file.FullName)"
        }
        $lease.path = $file.FullName
        $lease.gameAlive = $game.alive
        $lease.outstandingMiB = [math]::Max(0, [int]$lease.memoryMiB - $game.workingSetMiB)
        $leases.Add($lease)
    }
    return ,$leases
}

function Test-HeadlessUnboundGame([string]$RuntimeRoot) {
    $executable = Join-Path $RuntimeRoot 'game\SlayTheSpire2.exe'
    foreach ($candidate in @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)) {
        $handle = $candidate.SafeHandle
        $candidate.Refresh()
        if (-not $candidate.HasExited -and [string]::Equals($candidate.MainModule.FileName, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Enter-HeadlessHostLease([hashtable]$Context, [Diagnostics.Process]$ExistingGame) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Context.QueueSeconds)
    $launcher = Get-HeadlessProcessIdentity ([Diagnostics.Process]::GetCurrentProcess())
    $game = if ($null -ne $ExistingGame) { Get-HeadlessProcessIdentity $ExistingGame } else { $null }
    $Context.LeaseToken = [Guid]::NewGuid().ToString('N')
    $queuedAt = [DateTime]::UtcNow.ToString('O')
    $reportedWaiting = $false
    while ($true) {
        Assert-LauncherNotCancelled
        $hostLock = Open-HeadlessHostLock $Context
        try {
            $leases = Get-HeadlessHostLeases $Context
            $own = @($leases | Where-Object { $_.path -eq $Context.LeasePath })
            if ($own.Count -gt 0 -and -not [string]::Equals($own[0].runtimeRoot, $Context.Root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The host lease path belongs to another runtime."
            }
            if ($own.Count -gt 0 -and $own[0].gameAlive -and
                ($null -eq $game -or [int]$own[0].game.pid -ne $game.pid -or
                    ([DateTimeOffset]$own[0].game.birth) -ne ([DateTimeOffset]$game.birth))) {
                throw "A different live process owns this instance's host lease."
            }
            $others = @($leases | Where-Object { $_.path -ne $Context.LeasePath })
            $active = @($others | Where-Object { $_.state -ne 'queued' })
            $knownGames = @($leases | Where-Object { $_.gameAlive } | ForEach-Object { [int]$_.game.pid })
            if ($null -ne $game) { $knownGames += [int]$game.pid }
            $unknownGames = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -notin $knownGames })
            $capacity = Get-HeadlessHostCapacity
            $otherMemory = 0L; $otherOutstanding = 0L; $otherCpu = 0
            foreach ($lease in $active) {
                $otherMemory += [int]$lease.memoryMiB
                $otherOutstanding += [int]$lease.outstandingMiB
                $otherCpu += [int]$lease.cpu
            }
            $ownWorkingSet = if ($null -eq $game) { 0 } else { (Get-HeadlessIdentityState $game).workingSetMiB }
            $ownOutstanding = [math]::Max(0, $Context.MemoryMiB - $ownWorkingSet)
            $earlierQueued = @($others | Where-Object { $_.state -eq 'queued' -and
                    [DateTimeOffset]$_.queuedAt -lt [DateTimeOffset]$queuedAt })
            $canEnter = $unknownGames.Count -eq 0 -and
                ($null -ne $game -or $earlierQueued.Count -eq 0) -and
                ($Context.Mode -ne 'exclusive' -or $active.Count -eq 0) -and
                @($active | Where-Object { $_.mode -eq 'exclusive' }).Count -eq 0 -and $active.Count -lt 2 -and
                ($otherCpu + $Context.Cpu) -le $capacity.cpu -and
                ($otherMemory + $Context.MemoryMiB) -le ($capacity.totalMiB - 2048) -and
                ($otherOutstanding + $ownOutstanding + 2048) -le $capacity.availableMiB
            $record = @{ schemaVersion = 1; instance = $Context.Instance; runtimeRoot = $Context.Root;
                token = $Context.LeaseToken; launcher = $launcher; game = $game; artifactId = $Context.ArtifactId;
                mode = $Context.Mode; memoryMiB = $Context.MemoryMiB; cpu = $Context.Cpu; queuedAt = $queuedAt;
                state = if ($canEnter) { if ($null -eq $game) { 'pending' } else { 'running' } } else {
                    if ($null -eq $game) { 'queued' } else { 'running' }
                }
            }
            # A warm game continues owning its old reservation/mode until an
            # upgrade can be admitted; queuing must never make it invisible.
            if (-not $canEnter -and $null -ne $game -and $own.Count -gt 0) {
                $record.mode = $own[0].mode; $record.memoryMiB = $own[0].memoryMiB; $record.cpu = $own[0].cpu
            }
            Write-HeadlessJson $Context.LeasePath $record
            if ($canEnter) {
                Write-Host "UNATTENDED_ADMITTED instance=$($Context.Instance) mode=$($Context.Mode) memory_mib=$($Context.MemoryMiB) cpu=$($Context.Cpu)"
                return
            }
        } finally { $hostLock.Dispose() }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Headless host admission timed out; no other instance was stopped." }
        if (-not $reportedWaiting) {
            Write-Host "UNATTENDED_QUEUED instance=$($Context.Instance) mode=$($Context.Mode)"
            $reportedWaiting = $true
        }
        Start-Sleep -Milliseconds 250
    }
}

function Set-HeadlessHostGame([hashtable]$Context, [Diagnostics.Process]$Game) {
    $identity = Get-HeadlessProcessIdentity $Game
    if (-not [string]::Equals($identity.exe, (Join-Path $Context.GameRoot 'SlayTheSpire2.exe'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Started headless executable is not the instance's private game."
    }
    $hostLock = Open-HeadlessHostLock $Context
    try {
        $lease = Get-Content -LiteralPath $Context.LeasePath -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.token -ne $Context.LeaseToken -or $lease.runtimeRoot -ne $Context.Root) {
            throw "Headless host lease changed before process registration."
        }
        $lease.game = $identity; $lease.state = 'running'
        Write-HeadlessJson $Context.LeasePath $lease
    } finally { $hostLock.Dispose() }
}

function Exit-HeadlessHostLease([hashtable]$Context, [int]$GameProcessId = 0, [string]$GameBirth = '') {
    $hostLock = Open-HeadlessHostLock $Context -Cleanup
    try {
        if (-not (Test-Path -LiteralPath $Context.LeasePath -PathType Leaf)) { return }
        $lease = Get-Content -LiteralPath $Context.LeasePath -Raw | ConvertFrom-Json -AsHashtable
        if ($lease.runtimeRoot -ne $Context.Root) { throw "Cannot release another runtime's host lease." }
        $matchesToken = -not [string]::IsNullOrEmpty($Context.LeaseToken) -and $lease.token -eq $Context.LeaseToken
        $matchesGame = $null -ne $lease.game -and $GameProcessId -gt 0 -and
            [int]$lease.game.pid -eq $GameProcessId -and
            ([DateTimeOffset]$lease.game.birth) -eq ([DateTimeOffset]$GameBirth)
        if (-not $matchesToken -and -not $matchesGame) { return }
        if ((Get-HeadlessIdentityState $lease.game).alive) {
            # Passed/ready may retain a warm game. Its host reservation lives
            # until this exact process exits, not until its launcher returns.
            return
        }
        if ($lease.state -eq 'pending' -and (Test-HeadlessUnboundGame $Context.Root)) { return }
        Remove-Item -LiteralPath $Context.LeasePath -Force
    } finally { $hostLock.Dispose() }
}
