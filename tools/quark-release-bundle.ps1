Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

function New-QuarkReleaseBundle {
    param(
        [Parameter(Mandatory)]
        [string]$MinimalReleaseZip,

        [Parameter(Mandatory)]
        [string]$RitsuLibZip,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $temporaryPath = "$OutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Copy-Item -LiteralPath $MinimalReleaseZip -Destination $temporaryPath
        $archive = [System.IO.Compression.ZipFile]::Open(
            $temporaryPath,
            [System.IO.Compression.ZipArchiveMode]::Update)
        $ritsuArchive = [System.IO.Compression.ZipFile]::OpenRead($RitsuLibZip)
        try {
            $entryNames = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            foreach ($existingEntry in $archive.Entries) {
                $null = $entryNames.Add($existingEntry.FullName)
            }

            foreach ($ritsuEntry in $ritsuArchive.Entries) {
                $sourceName = $ritsuEntry.FullName.Replace('\', '/')
                $segments = @($sourceName.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ($sourceName.StartsWith('/') -or
                    $segments.Count -eq 0 -or
                    $segments -contains '.' -or
                    $segments -contains '..') {
                    throw "RitsuLib ZIP 包含非法条目：$($ritsuEntry.FullName)"
                }

                $entryName = "RitsuLib/$([string]::Join('/', $segments))"
                if ($ritsuEntry.Name.Length -eq 0) {
                    $entryName += '/'
                }
                if (-not $entryNames.Add($entryName)) {
                    throw "夸克打包版中存在重复条目：$entryName"
                }

                $targetEntry = $archive.CreateEntry(
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $targetEntry.LastWriteTime = $ritsuEntry.LastWriteTime
                if ($ritsuEntry.Name.Length -ne 0) {
                    $sourceStream = $ritsuEntry.Open()
                    $targetStream = $targetEntry.Open()
                    try {
                        $sourceStream.CopyTo($targetStream)
                    }
                    finally {
                        $targetStream.Dispose()
                        $sourceStream.Dispose()
                    }
                }
            }
        }
        finally {
            $ritsuArchive.Dispose()
            $archive.Dispose()
        }

        $bundle = Get-Item -LiteralPath $temporaryPath
        if ($bundle.Length -le 10MB) {
            throw "夸克打包版必须超过 10 MiB，实际为 $($bundle.Length) 字节。"
        }
        Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
        return Get-Item -LiteralPath $OutputPath
    }
    catch {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw
    }
}
