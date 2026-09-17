[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$QuarkSessionInput,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+-[A-Za-z0-9]{6}$')]
    [string]$QuarkSessionId,

    [string]$ModUploaderPath = 'D:\Desktop\sts2mod\ModUploader-win-x64\ModUploader.exe',
    [string]$WorkshopDirectory = 'D:\Desktop\sts2mod\ModUploader-win-x64\CombatSolverWorkshop',
    [string]$QuarkSkillDirectory = (Join-Path $env:USERPROFILE '.codex\skills\quarkclouddrive'),
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'quark-release-bundle.ps1')

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot

function Assert-ExitCode {
    param([int]$ExitCode, [string]$Operation)
    if ($ExitCode -ne 0) {
        throw "$Operation 失败，退出码 $ExitCode。"
    }
}

function Resolve-RequiredFile {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 不存在：$Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredDirectory {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label 不存在：$Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

foreach ($command in @('git', 'gh', 'node')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "缺少发布命令：$command"
    }
}

$manifestPath = Resolve-RequiredFile (Join-Path $repoRoot 'CombatSolver.json') 'manifest'
$releaseZipPath = Resolve-RequiredFile (Join-Path $repoRoot "releases\CombatSolver-$Version.zip") '最小发布包'
$quarkReleaseZipPath = Join-Path $repoRoot "releases\CombatSolver-$Version-Quark.zip"
$releaseNotesPath = Resolve-RequiredFile (Join-Path $repoRoot "docs\releases\$Version-RELEASE_NOTES.md") '玩家更新日志'
$solverDllPath = Resolve-RequiredFile (Join-Path $repoRoot '.godot\mono\temp\bin\Release\CombatSolver.dll') 'Release DLL'
$memoryCleanerPath = Resolve-RequiredFile (Join-Path $repoRoot 'tools\CombatSolver.MemoryCleaner\bin\Release\net48\CombatSolver.MemoryCleaner.exe') 'MemoryCleaner'
$licensePath = Resolve-RequiredFile (Join-Path $repoRoot 'LICENSE') 'MIT 许可证'
$noticesPath = Resolve-RequiredFile (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') '第三方许可'
$resolvedModUploaderPath = Resolve-RequiredFile $ModUploaderPath 'ModUploader'
$resolvedWorkshopDirectory = Resolve-RequiredDirectory $WorkshopDirectory '创意工坊暂存目录'
$workshopContentDirectory = Resolve-RequiredDirectory (Join-Path $resolvedWorkshopDirectory 'content') '创意工坊 content'
$workshopJsonPath = Resolve-RequiredFile (Join-Path $resolvedWorkshopDirectory 'workshop.json') 'workshop.json'
$resolvedQuarkSkillDirectory = Resolve-RequiredDirectory $QuarkSkillDirectory '夸克网盘 Skill'
$quarkCliPath = Resolve-RequiredFile (Join-Path $resolvedQuarkSkillDirectory 'scripts\quark-drive.cjs') '夸克网盘 CLI'

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -cne $Version) {
    throw "manifest 版本 $($manifest.version) 与发布版本 $Version 不一致。"
}

$branch = (& git branch --show-current).Trim()
Assert-ExitCode $LASTEXITCODE '读取当前分支'
if ($branch -cne 'main') {
    throw "统一发布脚本只接受 main，当前分支为 $branch。"
}

$sourceCommit = (& git rev-parse HEAD).Trim()
Assert-ExitCode $LASTEXITCODE '读取 release source commit'
$tagName = "v$Version"
$tagCommit = (& git rev-list -n 1 $tagName 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) {
    throw "发布标签 $tagName 不存在。"
}
$tagCommit = $tagCommit.Trim()
if ($tagCommit -cne $sourceCommit) {
    throw "发布标签 $tagName 指向 $tagCommit，当前 release source 为 $sourceCommit。"
}

$trackedChanges = @(& git status --porcelain --untracked-files=no)
Assert-ExitCode $LASTEXITCODE '检查已跟踪文件'
if ($trackedChanges.Count -ne 0) {
    throw "存在未提交的已跟踪文件，停止发布。"
}

$workshop = Get-Content -LiteralPath $workshopJsonPath -Raw | ConvertFrom-Json
if ([string]$workshop.changeNote -notmatch [regex]::Escape("CombatSolver $Version")) {
    throw "workshop.json 的 changeNote 尚未更新到 CombatSolver $Version。"
}

$statePath = Join-Path $repoRoot "releases\CombatSolver-$Version.publish-state.json"
$state = [ordered]@{
    schemaVersion = 1
    version = $Version
    sourceCommit = $sourceCommit
    workshop = $false
    github = $false
    quarkRelease = $false
    quarkNotes = $false
}
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    $savedState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ([string]$savedState.version -cne $Version -or [string]$savedState.sourceCommit -cne $sourceCommit) {
        throw "现有发布状态不属于版本 $Version 的 release source $sourceCommit。"
    }
    foreach ($key in @('workshop', 'github', 'quarkRelease', 'quarkNotes')) {
        $state[$key] = [bool]$savedState.$key
    }
}

function Save-PublishState {
    $temporaryPath = "$statePath.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporaryPath -Encoding utf8
    Move-Item -LiteralPath $temporaryPath -Destination $statePath -Force
}

function Invoke-Quark {
    param([string[]]$Arguments)

    Push-Location -LiteralPath $resolvedQuarkSkillDirectory
    try {
        $commandArguments = @($quarkCliPath) + $Arguments + @(
            '--session-input', $QuarkSessionInput,
            '--session-id', $QuarkSessionId
        )
        $lines = @(& node @commandArguments)
        $exitCode = $LASTEXITCODE
        $lines | ForEach-Object { Write-Host $_ }
        $records = @(
            $lines |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object { $_ | ConvertFrom-Json }
        )
        $results = @($records | Where-Object { $_.type -eq 'result' })
        $result = if ($results.Count -gt 0) { $results[-1] } else { $null }
        if ($exitCode -ne 0 -or $null -eq $result -or [int]$result.code -ne 0) {
            $message = if ($null -ne $result) { [string]$result.msg } else { "退出码 $exitCode" }
            throw "夸克网盘命令失败：$message"
        }
        return [pscustomobject]@{
            Records = $records
            Result = $result
        }
    }
    finally {
        Pop-Location
    }
}

function Get-QuarkItems {
    param([string[]]$Arguments)

    $response = Invoke-Quark -Arguments $Arguments
    $artifacts = @($response.Records | Where-Object { $_.type -eq 'artifact' })
    if ($artifacts.Count -ne 1) {
        throw '夸克网盘目录查询没有生成唯一完整结果。'
    }
    $artifactPath = [string]$artifacts[0].data.file_path
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "夸克网盘目录查询结果不存在：$artifactPath"
    }
    return @(
        Get-Content -LiteralPath $artifactPath |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json }
    )
}

function Select-UniqueQuarkFolder {
    param([object[]]$Items, [string]$Name)

    $matches = @($Items | Where-Object {
        [string]$_.filename -ceq $Name -and [string]$_.file_type -eq '0'
    })
    if ($matches.Count -ne 1) {
        throw "夸克网盘目录 $Name 必须唯一，实际匹配 $($matches.Count) 项。"
    }
    return $matches[0]
}

if ($ValidateOnly) {
    [pscustomobject]@{
        version = $Version
        sourceCommit = $sourceCommit
        releaseZip = $releaseZipPath
        quarkReleaseZip = $quarkReleaseZipPath
        quarkPaddingEntry = 'QUARK_UPLOAD_PADDING.bin'
        quarkMinimumBytesExclusive = 15MB
        releaseNotes = $releaseNotesPath
        license = $licensePath
        workshop = $resolvedWorkshopDirectory
        githubTag = $tagName
        quarkRoot = '战斗路线求解器'
        quarkLatest = '最新版'
        quarkArchive = '老版本'
        quarkNotes = '更新日志'
        monitoringBackend = '由用户维护'
    } | ConvertTo-Json
    return
}

if (-not $state.workshop) {
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $workshopContentDirectory 'CombatSolver.json') -Force
    Copy-Item -LiteralPath $solverDllPath -Destination (Join-Path $workshopContentDirectory 'CombatSolver.dll') -Force
    Copy-Item -LiteralPath $memoryCleanerPath -Destination (Join-Path $workshopContentDirectory 'CombatSolver.MemoryCleaner.exe') -Force
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $workshopContentDirectory 'LICENSE') -Force
    Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $workshopContentDirectory 'THIRD_PARTY_NOTICES.md') -Force
    & $resolvedModUploaderPath upload -w $resolvedWorkshopDirectory
    Assert-ExitCode $LASTEXITCODE '上传 Steam 创意工坊'
    $state.workshop = $true
    Save-PublishState
}

if (-not $state.github) {
    & git -c http.sslBackend=schannel push origin main "refs/tags/$tagName"
    Assert-ExitCode $LASTEXITCODE '推送 main 与发布标签'

    $existingReleaseJson = & gh release view $tagName --json tagName,assets 2>$null
    $releaseExists = $LASTEXITCODE -eq 0
    if ($releaseExists) {
        $existingRelease = ($existingReleaseJson -join "`n") | ConvertFrom-Json
        & gh release edit $tagName --notes-file $releaseNotesPath
        Assert-ExitCode $LASTEXITCODE '同步 GitHub Release 更新日志'
        $assetExists = @($existingRelease.assets | Where-Object { [string]$_.name -ceq (Split-Path -Leaf $releaseZipPath) }).Count -eq 1
        if (-not $assetExists) {
            & gh release upload $tagName $releaseZipPath
            Assert-ExitCode $LASTEXITCODE '上传 GitHub Release 最小 ZIP'
        }
    }
    else {
        & gh release create $tagName $releaseZipPath --verify-tag --notes-file $releaseNotesPath
        Assert-ExitCode $LASTEXITCODE '创建 GitHub Release'
    }
    $state.github = $true
    Save-PublishState
}

if (-not $state.quarkRelease -or -not $state.quarkNotes) {
    $quarkBundle = if (-not $state.quarkRelease) {
        New-QuarkReleaseBundle `
            -MinimalReleaseZip $releaseZipPath `
            -OutputPath $quarkReleaseZipPath
    }
    else {
        $null
    }
    $rootItems = @(Get-QuarkItems -Arguments @(
        'search', '--keyword', '战斗路线求解器', '--search-type', 'dir', '--stdout-only'
    ))
    $rootFolder = Select-UniqueQuarkFolder -Items $rootItems -Name '战斗路线求解器'
    $rootChildren = @(Get-QuarkItems -Arguments @('browse', '--parent-fid', [string]$rootFolder.fid, '--all'))
    $latestFolder = Select-UniqueQuarkFolder -Items $rootChildren -Name '最新版'
    $archiveFolder = Select-UniqueQuarkFolder -Items $rootChildren -Name '老版本'
    $notesFolder = Select-UniqueQuarkFolder -Items $rootChildren -Name '更新日志'

    if (-not $state.quarkRelease) {
        $latestItems = @(Get-QuarkItems -Arguments @('browse', '--parent-fid', [string]$latestFolder.fid, '--all'))
        $releaseName = $quarkBundle.Name
        $currentRelease = @($latestItems | Where-Object {
            [string]$_.filename -ceq $releaseName -and [string]$_.file_type -eq '1'
        })
        if ($currentRelease.Count -gt 1) {
            throw "夸克网盘最新版中存在多个 $releaseName，停止发布。"
        }
        $archiveItems = @($latestItems | Where-Object {
            $currentRelease.Count -eq 0 -or [string]$_.fid -cne [string]$currentRelease[0].fid
        })
        if ($archiveItems.Count -gt 100) {
            throw "夸克网盘最新版中有 $($archiveItems.Count) 个旧文件，单次移动上限为 100。"
        }
        if ($archiveItems.Count -gt 0) {
            $moveArguments = @('move') + @($archiveItems | ForEach-Object { [string]$_.fid }) +
                @('--target-fid', [string]$archiveFolder.fid)
            $null = Invoke-Quark -Arguments $moveArguments
        }
        if ($currentRelease.Count -eq 0) {
            $null = Invoke-Quark -Arguments @(
                'upload', $quarkBundle.FullName, '--parent-fid', [string]$latestFolder.fid
            )
        }
        $state.quarkRelease = $true
        Save-PublishState
    }

    if (-not $state.quarkNotes) {
        $notesItems = @(Get-QuarkItems -Arguments @('browse', '--parent-fid', [string]$notesFolder.fid, '--all'))
        $notesName = Split-Path -Leaf $releaseNotesPath
        $existingNotes = @($notesItems | Where-Object {
            [string]$_.filename -ceq $notesName -and [string]$_.file_type -eq '1'
        })
        if ($existingNotes.Count -gt 1) {
            throw "夸克网盘更新日志中存在多个 $notesName，停止发布。"
        }
        if ($existingNotes.Count -eq 0) {
            $null = Invoke-Quark -Arguments @(
                'upload', $releaseNotesPath, '--parent-fid', [string]$notesFolder.fid
            )
        }
        $state.quarkNotes = $true
        Save-PublishState
    }
}

[pscustomobject]@{
    version = $Version
    sourceCommit = $sourceCommit
    workshop = [bool]$state.workshop
    github = [bool]$state.github
    quarkRelease = [bool]$state.quarkRelease
    quarkReleaseZip = $quarkReleaseZipPath
    quarkPaddingEntry = 'QUARK_UPLOAD_PADDING.bin'
    quarkNotes = [bool]$state.quarkNotes
    monitoringBackend = '由用户维护'
    stateFile = $statePath
} | ConvertTo-Json
