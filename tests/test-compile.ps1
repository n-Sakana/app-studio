$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$started = [Diagnostics.Stopwatch]::StartNew()
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name)
if ($sourceFiles.Count -eq 0) { throw 'No C# source files.' }

$badAscii = @()
foreach ($file in $sourceFiles + @(Get-Item (Join-Path $root 'launch.vbs'))) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) {
        $badAscii += $file.FullName
    }
}
if ($badAscii.Count -gt 0) { throw ('Non-ASCII source: ' + ($badAscii -join ', ')) }

$joined = ($sourceFiles | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
$forbidden = @('\$"', '\?\.', '\?\?=', '\bnameof\s*\(', '\bout\s+var\b')
foreach ($pattern in $forbidden) {
    if ($joined -match $pattern) { throw ('C# 5.0 forbidden syntax matched: ' + $pattern) }
}

& (Join-Path $root 'app-studio.ps1') -CompileOnly
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-start-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $diagnosticsPath = Join-Path $tempDir 'diagnostics.json'
    & (Join-Path $root 'app-studio.ps1') -DiagnosticsPath $diagnosticsPath -AutoCloseMs 150
    if (-not (Test-Path -LiteralPath $diagnosticsPath)) { throw 'Startup diagnostics were not written.' }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
$started.Stop()
Write-Output ('PASS test-compile files=' + $sourceFiles.Count + ' startClose=1 durationMs=' + $started.ElapsedMilliseconds)
