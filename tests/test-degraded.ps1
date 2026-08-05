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
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-degraded-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$process = $null
try {
    [AppStudio.Probe]::Configure($root, $true)
    $ready = Join-Path $tempDir 'ready.json'
    $process = Start-Process -FilePath $build.FixtureWin32 -ArgumentList @('--mode','healthy','--ready',$ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWin32 did not become ready.' }
    $fixture = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    $x = [int](($fixture.editRect.left + $fixture.editRect.right) / 2)
    $y = [int](($fixture.editRect.top + $fixture.editRect.bottom) / 2)
    # What this asks for is the window at a point on the screen, so the answer
    # depends on what else happens to be over that point. Bringing the fixture
    # to the front first makes the question the one this test means to ask;
    # without it the test reports "degraded acquisition failed" when what
    # actually happened is that somebody left a window open there.
    [AppStudio.ScreenCapture]::Raise([int64]$fixture.window)
    Start-Sleep -Milliseconds 400
    $onTop = [AppStudio.WindowTools]::ProcessIdAt($x, $y)
    if ($onTop -ne $process.Id) {
        throw ('another window is over the fixture at ' + $x + ',' + $y + ': process ' + $onTop +
            ' answered instead of ' + $process.Id + '. This test reads the window at a screen point, so it needs that point.')
    }
    $snapshot = [AppStudio.Probe]::At($x, $y, 500)
    if ($snapshot.Win32Status.State -ne 'ok' -or $snapshot.Win32.CtrlId -ne 1002) { throw 'Win32 degraded acquisition failed.' }
    if ($snapshot.UiaStatus.State -ne 'unavailable' -or $snapshot.UiaStatus.Reasons[0].Code -ne 'UIA-INIT') { throw 'UIA degraded reason missing.' }
    $evidencePath = Join-Path $tempDir 'degraded.json'
    [AppStudio.SnapshotJson]::WriteDegradedEvidence($snapshot, $evidencePath)
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    if (-not $evidence.fixed -or -not $evidence.recorded -or $evidence.snapshot.win32.ctrlId -ne 1002) { throw 'Degraded fixed/recorded/output evidence failed.' }
} finally {
    [AppStudio.Probe]::Shutdown()
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
Write-Output 'PASS test-degraded uia=unavailable win32=ok fixed=1 recorded=1 output=1'

