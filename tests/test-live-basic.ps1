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
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-live-basic-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$process = $null
$wpfProcess = $null
try {
    [AppStudio.Probe]::Configure($root, $false)
    $health = [AppStudio.Probe]::GetHealth()
    if (-not $health.ActiveWarmupPerformed -or -not $health.SpareWarmupPerformed) { throw 'A worker reported ready before UIA warm-up.' }
    $ready = Join-Path $tempDir 'ready.json'
    $process = Start-Process -FilePath $build.FixtureWin32 -ArgumentList @('--mode','healthy','--ready',$ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWin32 did not become ready.' }
    $info = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    $x = [int](($info.editRect.left + $info.editRect.right) / 2)
    $y = [int](($info.editRect.top + $info.editRect.bottom) / 2)
    $snapshot = [AppStudio.Probe]::At($x, $y, 500)
    if ($snapshot.Win32.ClassName -ne 'Edit') { throw ('Expected Edit, got ' + $snapshot.Win32.ClassName) }
    if ($snapshot.Win32.CtrlId -ne 1002) { throw ('Expected ctrlId 1002, got ' + $snapshot.Win32.CtrlId) }
    if ($snapshot.Win32.Caption -ne 'ABC123') { throw ('Expected ABC123, got ' + $snapshot.Win32.Caption) }
    if ($snapshot.Win32.WindowRect.Width -ne 180 -or $snapshot.Win32.WindowRect.Height -ne 28) { throw 'Edit rectangle mismatch.' }
    if ($snapshot.Win32.Ancestors.Count -lt 1 -or $snapshot.Win32.Ancestors[0].ClassName -ne '#32770') { throw 'Dialog ancestor missing.' }
    if ($snapshot.Win32Status.State -ne 'ok') { throw ('Win32 status was ' + $snapshot.Win32Status.State) }
    if ($snapshot.UiaStatus.State -eq 'unavailable') { throw 'Native Edit UIA acquisition was unavailable.' }

    $wpfProcess = Start-Process -FilePath $build.FixtureWpf -ArgumentList @('--kind','healthy','--hang-mode','permanent','--run-dir',$tempDir,'--prefix','wpf','--left','700') -PassThru
    $wpfReady = Join-Path $tempDir 'wpf.ready'
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $wpfReady) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $wpfReady)) { throw 'FixtureWpf did not become ready.' }
    $wpfMap = @{}
    Get-Content -LiteralPath $wpfReady | ForEach-Object { $parts=$_.Split('=',2); $wpfMap[$parts[0]]=$parts[1] }
    $wpfSnapshot = [AppStudio.Probe]::At([int]$wpfMap.x, [int]$wpfMap.y, 1500)
    if ($wpfSnapshot.Uia.AutomationId -ne 'TargetText') { throw ('Expected TargetText, got ' + $wpfSnapshot.Uia.AutomationId) }
    if ($wpfSnapshot.UiaStatus.State -ne 'ok') { throw ('WPF UIA status was ' + $wpfSnapshot.UiaStatus.State) }
    $session = New-Object AppStudio.SessionRecorder((Join-Path $tempDir 'shots'))
    $record = $session.Pin($wpfSnapshot, 'WPF target', 'fixture note')
    if ($record.Notes.Count -ne 1 -or $record.Shots.Count -ne 1 -or -not (Test-Path -LiteralPath $record.Shots[0].File)) { throw 'Pin record/crop/note failed.' }
    if ($null -eq $record.Uia.TreePath -or $record.Uia.TreePath.Count -lt 1) { throw 'Deep UIA tree path missing.' }
} finally {
    [AppStudio.Probe]::Shutdown()
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($null -ne $wpfProcess -and -not $wpfProcess.HasExited) { $wpfProcess.Kill(); $wpfProcess.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
Write-Output 'PASS test-live-basic fixtures=FixtureWin32,FixtureWpf win32=ok uia=ok deepTree=ok pin=1 crop=1 note=1 workerWarmup=active+spare'
