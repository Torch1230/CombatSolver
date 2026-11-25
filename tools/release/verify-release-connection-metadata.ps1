function Read-AssemblyInspectionInfo {
    param([Parameter(Mandatory)][string]$DllPath)

    $resolvedPath = (Resolve-Path -LiteralPath $DllPath).Path
    [byte[]]$dllBytes = [System.IO.File]::ReadAllBytes($resolvedPath)
    $metadataEntries = [System.Collections.Generic.List[pscustomobject]]::new()
    $typeNames = [System.Collections.Generic.List[string]]::new()

    $stream = [System.IO.MemoryStream]::new($dllBytes, $false)
    $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (-not $peReader.HasMetadata) {
            throw "目标 DLL 不包含有效的 .NET 元数据：$resolvedPath"
        }
        $metadataReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)

        foreach ($typeHandle in $metadataReader.TypeDefinitions) {
            $typeDef = $metadataReader.GetTypeDefinition($typeHandle)
            $name = $metadataReader.GetString($typeDef.Name)
            if (-not [string]::IsNullOrEmpty($name)) {
                $typeNames.Add($name)
            }
        }

        $assemblyDef = $metadataReader.GetAssemblyDefinition()
        foreach ($attrHandle in $assemblyDef.GetCustomAttributes()) {
            $attr = $metadataReader.GetCustomAttribute($attrHandle)
            if ($attr.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) {
                continue
            }
            $memberRef = $metadataReader.GetMemberReference(
                [System.Reflection.Metadata.MemberReferenceHandle]$attr.Constructor)
            if ($memberRef.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) {
                continue
            }
            $typeRef = $metadataReader.GetTypeReference(
                [System.Reflection.Metadata.TypeReferenceHandle]$memberRef.Parent)
            if ($metadataReader.GetString($typeRef.Namespace) -cne 'System.Reflection' -or
                $metadataReader.GetString($typeRef.Name) -cne 'AssemblyMetadataAttribute') {
                continue
            }
            $blobReader = $metadataReader.GetBlobReader($attr.Value)
            if ($blobReader.Length -lt 2 -or $blobReader.ReadUInt16() -ne 1) {
                continue
            }
            $key = $blobReader.ReadSerializedString()
            $value = $blobReader.ReadSerializedString()
            $metadataEntries.Add([pscustomobject]@{
                Key = [string]$key
                Value = [string]$value
            })
        }
    }
    finally {
        $peReader.Dispose()
        $stream.Dispose()
    }

    return [pscustomobject]@{
        ResolvedPath = $resolvedPath
        Bytes = $dllBytes
        MetadataEntries = $metadataEntries.ToArray()
        TypeNames = $typeNames.ToArray()
    }
}

function Assert-ReleaseSecurityInvariants {
    param([Parameter(Mandatory)][string]$DllPath)

    $info = Read-AssemblyInspectionInfo -DllPath $DllPath
    $violations = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in $info.MetadataEntries) {
        if ($entry.Key.StartsWith('Showcase', [System.StringComparison]::OrdinalIgnoreCase)) {
            $violations.Add("禁止存在 Showcase 前缀的程序集元数据：$($entry.Key)")
        }
    }

    $forbiddenTypeNames = @(
        'PerformanceRecording',
        'PerformanceSession',
        'PerformanceLifecycle',
        'CombatShowcaseCollector',
        'CombatShowcaseUploadNode',
        'ShowcaseTransport'
    )
    $forbiddenTypeSet = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$forbiddenTypeNames,
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($typeName in $info.TypeNames) {
        $baseName = $typeName.Split('`')[0]
        if ($forbiddenTypeSet.Contains($baseName)) {
            $violations.Add("发布 DLL 包含禁止类型：$typeName")
        }
    }

    $forbiddenStrings = @(
        'CombatShowcaseCollector',
        'CombatShowcaseUploadNode',
        'ShowcaseTransport',
        'ShowcaseEndpoint',
        'ShowcaseUploadToken',
        'ShowcaseCertificateSha256',
        'PerformanceRecording',
        'PerformanceSession',
        'PerformanceLifecycle',
        'powershell.exe',
        'ExecutionPolicy',
        'performance-recording.json'
    )

    [byte[]]$rawBytes = $info.Bytes
    $latin1Text = [System.Text.Encoding]::Latin1.GetString($rawBytes)
    $evenLength = $rawBytes.Length - ($rawBytes.Length % 2)
    $utf16EvenText = if ($evenLength -gt 0) {
        [System.Text.Encoding]::Unicode.GetString($rawBytes, 0, $evenLength)
    } else {
        ''
    }
    $oddLength = if ($rawBytes.Length -gt 1) {
        ($rawBytes.Length - 1) - (($rawBytes.Length - 1) % 2)
    } else {
        0
    }
    $utf16OddText = if ($oddLength -gt 0) {
        [System.Text.Encoding]::Unicode.GetString($rawBytes, 1, $oddLength)
    } else {
        ''
    }

    foreach ($token in $forbiddenStrings) {
        if ($latin1Text.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $violations.Add("发布 DLL 原始字节（UTF-8/ASCII）包含禁止字符串：$token")
        }
        if ($utf16EvenText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $utf16OddText.IndexOf($token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $violations.Add("发布 DLL 原始字节（UTF-16LE）包含禁止字符串：$token")
        }
    }

    if ($violations.Count -gt 0) {
        throw ("发布 DLL 安全不变量校验失败（$($info.ResolvedPath)）：`n - " + ($violations -join "`n - "))
    }
}

function Assert-ReleaseConnectionMetadata {
    param([Parameter(Mandatory)][string]$DllPath)

    Assert-ReleaseSecurityInvariants -DllPath $DllPath

    $info = Read-AssemblyInspectionInfo -DllPath $DllPath
    $metadata = @{}
    foreach ($entry in $info.MetadataEntries) {
        $metadata[$entry.Key] = $entry.Value
    }

    foreach ($key in @(
        'PresenceEndpoint',
        'PresenceCertificateSha256'
    )) {
        if (-not $metadata.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($metadata[$key])) {
            throw "发布 DLL 缺少连接元数据：$key"
        }
    }

    $address = $null
    $validAddress = [Uri]::TryCreate($metadata['PresenceEndpoint'], [UriKind]::Absolute, [ref]$address)
    if (-not $validAddress -or $address.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "发布 DLL 的 PresenceEndpoint 必须使用 HTTPS。"
    }
    if ($metadata['PresenceCertificateSha256'] -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw "发布 DLL 的 PresenceCertificateSha256 必须是 SHA-256 证书指纹。"
    }
}
