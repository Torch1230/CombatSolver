#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'run-unattended-test.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Unattended script has parse errors.' }
$values = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true) | ForEach-Object {
    foreach ($pair in $_.KeyValuePairs) {
        if ($pair.Item1.Extent.Text -eq 'preCombatInterveningMapPoints') { $pair.Item2.Extent.Text }
    }
})
if ($values.Count -ne 1) { throw 'Expected exactly one map-point request field.' }
$makeRequest = [scriptblock]::Create('@{ preCombatInterveningMapPoints = ' + $values[0] + ' }')
$cases = @('', '[]', '[{"actIndex":0,"floor":1,"column":2}]', '[{"actIndex":0,"floor":1,"column":2},{"actIndex":0,"floor":2,"column":3}]')
$counts = @(0, 0, 1, 2)
for ($i = 0; $i -lt $cases.Count; $i++) {
    $PreCombatInterveningMapPointsJson = $cases[$i]
    $request = & $makeRequest
    $json = $request | ConvertTo-Json -Depth 10 -Compress
    $roundTrip = $json | ConvertFrom-Json
    $points = $roundTrip.preCombatInterveningMapPoints
    if ($null -eq $points -or -not $points.GetType().IsArray -or $points.Count -ne $counts[$i]) {
        throw "Map-point array shape mismatch for case ${i}: $json"
    }
    foreach ($point in $points) {
        if ($point -is [array] -or $null -eq $point.column) { throw "Nested or malformed map point: $json" }
    }
}
'UNATTENDED_MAP_POINTS_OK cases=4 source=production_request_expression'
