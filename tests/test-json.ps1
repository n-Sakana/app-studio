$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$started = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $root 'app-studio.ps1') -CompileOnly

$object = New-Object AppStudio.JsonObject
[void]$object.Add('first', 'quote" slash\ line' + "`n")
[void]$object.Add('unicode', ([string][char]0x65E5) + [char]0x672C + [char]0x8A9E + [char]0xD83D + [char]0xDE80)
[void]$object.Add('control', [string][char]1)
[void]$object.Add('last', 42)
$json1 = [AppStudio.JsonWriter]::Write($object)
$json2 = [AppStudio.JsonWriter]::Write($object)
if ($json1 -ne $json2) { throw 'JSON output is not stable.' }
if ($json1.IndexOf('"first"') -ge $json1.IndexOf('"last"')) { throw 'JSON key order changed.' }
if (-not $json1.Contains('\"')) { throw 'Quote escaping missing.' }
if (-not $json1.Contains('\u0001')) { throw 'Control escaping missing.' }

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-json-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $jsonPath = Join-Path $tempDir 'sample.json'
    [AppStudio.JsonWriter]::WriteFile($jsonPath, $object)
    $bytes = [IO.File]::ReadAllBytes($jsonPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'JSON has a UTF-8 BOM.' }
    $parsed = $json1 | ConvertFrom-Json
    if ($parsed.last -ne 42) { throw 'JSON parse round-trip failed.' }

    $diagnosticsPath = Join-Path $tempDir 'diagnostics.json'
    [AppStudio.App]::RunHeadless($root, $diagnosticsPath)
    $diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw | ConvertFrom-Json
    if ($null -ne $diagnostics.appLockerPolicyPresent.value) { throw 'Unknown value was not null.' }
    if ([string]::IsNullOrWhiteSpace($diagnostics.appLockerPolicyPresent.reason)) { throw 'Unknown reason missing.' }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
$started.Stop()
Write-Output ('PASS test-json assertions=9 durationMs=' + $started.ElapsedMilliseconds)
