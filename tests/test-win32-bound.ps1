$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-win32-bound-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$process = $null
try {
    $ready = Join-Path $tempDir 'ready.json'
    $hung = Join-Path $tempDir 'hung.signal'
    $release = Join-Path $tempDir 'release.signal'
    $process = Start-Process -FilePath $build.FixtureWin32 -ArgumentList @('--mode','permanent','--ready',$ready,'--hung',$hung,'--release',$release) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while ((-not (Test-Path -LiteralPath $ready) -or -not (Test-Path -LiteralPath $hung)) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $hung)) { throw 'FixtureWin32 did not hang.' }
    $fixture = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $info = [AppStudio.Win32Probe]::AtHandle([IntPtr][int64]$fixture.edit, 150)
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -gt 500) { throw ('Win32 bound exceeded: ' + $watch.ElapsedMilliseconds + ' ms') }
    if ($info.ClassName -ne 'Edit' -or $info.CtrlId -ne 1002 -or $null -eq $info.WindowRect) { throw 'Non-message Win32 facts were lost during hang.' }
    if ($info.Status.State -ne 'partial') { throw ('Expected partial, got ' + $info.Status.State) }
    if (@($info.Status.Reasons | Where-Object Code -eq 'WIN32-HUNG').Count -ne 1) { throw 'WIN32-HUNG reason missing.' }
    Write-Output ('PASS test-win32-bound returnMs=' + $watch.ElapsedMilliseconds + ' state=' + $info.Status.State + ' class=' + $info.ClassName + ' ctrlId=' + $info.CtrlId)
} finally {
    Set-Content -LiteralPath (Join-Path $tempDir 'release.signal') -Value 'release' -Encoding Ascii -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 100
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
