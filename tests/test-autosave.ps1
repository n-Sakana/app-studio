$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-autosave-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    # A session that is killed without any shutdown must still leave everything
    # it recorded on disk: there is no explicit save action in the product.
    $victimDir = Join-Path $tempDir 'killed'
    $victimScript = Join-Path $tempDir 'victim.ps1'
    $victimSource = @'
$ErrorActionPreference = 'Stop'
& (Join-Path $args[0] 'app-studio.ps1') -CompileOnly
$log = New-Object AppStudio.SessionLog($args[1])
if (-not $log.Enabled) { throw 'log disabled' }
for ($index = 1; $index -le 50; $index++) {
    $record = New-Object AppStudio.JsonObject
    $null = $record.Add('kind', 'test.record')
    $null = $record.Add('index', $index)
    $null = $log.Append('events', $record)
}
$null = $log.WriteText('summary.md', 'written while running')
Set-Content -LiteralPath (Join-Path $args[1] 'marker.txt') -Value 'ready' -Encoding UTF8
Stop-Process -Id $PID -Force
Start-Sleep -Seconds 30
'@
    Set-Content -LiteralPath $victimScript -Value $victimSource -Encoding UTF8
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $victim = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', $victimScript, $root, $victimDir) -PassThru -WindowStyle Hidden
    if (-not $victim.WaitForExit(120000)) { $victim.Kill(); throw 'The victim session did not exit.' }
    $marker = Join-Path $victimDir 'marker.txt'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'The victim session never reached the kill point.' }
    $killedLines = @(Get-Content -LiteralPath (Join-Path $victimDir 'events.jsonl') -Encoding UTF8)
    if ($killedLines.Count -ne 50) { throw ('A killed session kept only ' + $killedLines.Count + ' of 50 records.') }
    $indexes = @()
    foreach ($line in $killedLines) {
        $record = ConvertFrom-Json $line
        if ([string]::IsNullOrEmpty($record.at)) { throw 'A record was written without a timestamp.' }
        $indexes += $record.index
        if ($record.seq -ne $record.index) { throw 'Record sequence numbers are not contiguous.' }
    }
    if ($indexes[0] -ne 1 -or $indexes[49] -ne 50) { throw 'Records are out of order.' }
    if (-not (Test-Path -LiteralPath (Join-Path $victimDir 'summary.md'))) { throw 'The human readable summary was lost.' }

    # A log that cannot be written says so rather than quietly dropping records.
    $blockedPath = Join-Path $tempDir 'blocked.txt'
    Set-Content -LiteralPath $blockedPath -Value 'not a directory' -Encoding UTF8
    $blocked = New-Object AppStudio.SessionLog((Join-Path $blockedPath 'inside'))
    if ($blocked.Enabled) { throw 'A log inside a file path reported itself as usable.' }
    if ([string]::IsNullOrEmpty($blocked.Status.DisabledReason)) { throw 'A disabled log gave no reason.' }
    $record = New-Object AppStudio.JsonObject
    $null = $record.Add('kind', 'test.record')
    if ($blocked.Append('events', $record) -ne 0) { throw 'A disabled log claimed to have written a record.' }
    $blocked.Dispose()

    # The growing log must stay readable while the session is still running.
    $liveDir = Join-Path $tempDir 'live'
    $live = New-Object AppStudio.SessionLog($liveDir)
    try {
        for ($index = 1; $index -le 5; $index++) {
            $item = New-Object AppStudio.JsonObject
            $null = $item.Add('kind', 'test.live')
            $null = $item.Add('index', $index)
            if ($live.Append('events', $item) -eq 0) { throw 'A record was refused by an enabled log.' }
        }
        $whileOpen = @(Get-Content -LiteralPath (Join-Path $liveDir 'events.jsonl') -Encoding UTF8)
        if ($whileOpen.Count -ne 5) { throw ('The open log showed ' + $whileOpen.Count + ' of 5 records.') }
        $status = $live.Status
        if ($status.RecordCount -ne 5 -or $status.WriteFailures -ne 0) { throw 'The log status does not match what was written.' }
        if (-not $live.WriteText('summary.md', 'first')) { throw 'Summary write failed.' }
        if (-not $live.WriteText('summary.md', 'second')) { throw 'Summary rewrite failed.' }
        $summary = Get-Content -LiteralPath (Join-Path $liveDir 'summary.md') -Raw -Encoding UTF8
        if ($summary.Trim() -ne 'second') { throw 'The summary was not replaced in place.' }
        if (Test-Path -LiteralPath (Join-Path $liveDir 'summary.md.tmp')) { throw 'A temporary file was left behind.' }
    } finally {
        $live.Dispose()
    }

    Write-Output ('PASS test-autosave killedRecords=' + $killedLines.Count + ' order=1..50 disabledReported=1 refusedRecords=0 readableWhileOpen=5 summaryReplace=ok')
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
