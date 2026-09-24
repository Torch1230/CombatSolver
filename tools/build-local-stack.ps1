param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ForPublication,
    [string]$PrivateConfigDirectory = ""
)

$ErrorActionPreference = "Stop"
$solverRoot = Split-Path -Parent $PSScriptRoot
$solverProject = Join-Path $solverRoot "CombatSolver.csproj"

$buildArguments = @('build', $solverProject, '-c', $Configuration, '--nologo')
if ($ForPublication) {
    $buildArguments += '-p:PublicationBuild=true'
    $configDirectory = if ([string]::IsNullOrWhiteSpace($PrivateConfigDirectory)) {
        Join-Path $solverRoot '.local'
    } else {
        $PrivateConfigDirectory
    }
    $presenceProps = Join-Path $configDirectory 'presence.props'
    $showcaseProps = Join-Path $configDirectory 'showcase.props'
    foreach ($path in @($presenceProps, $showcaseProps)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "发布构建缺少私有连接配置：$path"
        }
    }
    $buildArguments += "-p:PresencePropsPath=$((Resolve-Path -LiteralPath $presenceProps).Path)"
    $buildArguments += "-p:ShowcasePropsPath=$((Resolve-Path -LiteralPath $showcaseProps).Path)"
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "CombatSolver build failed with exit code $LASTEXITCODE."
}

if ($ForPublication) {
    . (Join-Path $PSScriptRoot 'verify-release-connection-metadata.ps1')
    Assert-ReleaseConnectionMetadata (Join-Path $solverRoot '.godot\mono\temp\bin\Release\CombatSolver.dll')
}
