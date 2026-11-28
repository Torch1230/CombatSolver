param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ForPublication,
    [switch]$SkipRustKernel,
    [switch]$SkipCopyMod,
    [string]$PrivateConfigDirectory = ""
)

$ErrorActionPreference = "Stop"
$solverRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solverProject = Join-Path $solverRoot "CombatSolver.csproj"

$buildArguments = @('build', $solverProject, '-c', $Configuration, '--nologo')
if ($SkipCopyMod) {
    $buildArguments += '-p:CopyModOnBuild=false'
}
if ($SkipRustKernel) {
    $buildArguments += '-p:StageRustCombatKernelEnabled=false'
}
elseif ($ForPublication) {
    $buildArguments += '-p:RequireRustCombatKernel=true'
}
if ($ForPublication) {
    $buildArguments += '-p:PublicationBuild=true'
    $configDirectory = if ([string]::IsNullOrWhiteSpace($PrivateConfigDirectory)) {
        Join-Path $solverRoot '.local'
    } else {
        $PrivateConfigDirectory
    }
    $presenceProps = Join-Path $configDirectory 'presence.props'
    if (-not (Test-Path -LiteralPath $presenceProps -PathType Leaf)) {
        throw "发布构建缺少私有连接配置：$presenceProps"
    }
    $buildArguments += "-p:PresencePropsPath=$((Resolve-Path -LiteralPath $presenceProps).Path)"
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "CombatSolver build failed with exit code $LASTEXITCODE."
}

if ($ForPublication) {
    . (Join-Path $PSScriptRoot '../release/verify-release-connection-metadata.ps1')
    Assert-ReleaseConnectionMetadata (Join-Path $solverRoot '.godot\mono\temp\bin\Release\CombatSolver.dll')
}
