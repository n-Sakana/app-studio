$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# There is no save action anywhere in the product, so everything a session
# records has to already be on disk when the process dies. This exercises the
# path the product actually writes through - SessionStore.Append - rather than
# any helper beside it, because a durability guarantee proven on a class the
# product does not use is not a guarantee at all.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-autosave-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    # ---- 1. a session killed without any shutdown keeps what it wrote --------
    $victimBase = Join-Path $tempDir 'killed'
    $victimScript = Join-Path $tempDir 'victim.ps1'
    $victimSource = @'
$ErrorActionPreference = 'Stop'
& (Join-Path $args[0] 'app-studio.ps1') -CompileOnly
$session = [AppStudio.SessionStore]::Create($args[1], 'record', 'autosave victim')
[AppStudio.SessionStore]::WriteMeta($session)
Set-Content -LiteralPath (Join-Path $args[1] 'folder.txt') -Value $session.Folder -Encoding UTF8
for ($index = 1; $index -le 50; $index++) {
    $step = New-Object AppStudio.StepRecord
    $step.Index = $index
    $step.At = [DateTimeOffset]::Now
    $step.Kind = 'click'
    $step.AppName = 'victim'
    $step.ElementLabel = ('element ' + $index)
    if (-not [AppStudio.SessionStore]::Append($session, 'steps', $step.ToJson())) { throw ('record ' + $index + ' was refused') }
}
Set-Content -LiteralPath (Join-Path $args[1] 'marker.txt') -Value 'ready' -Encoding UTF8
Stop-Process -Id $PID -Force
Start-Sleep -Seconds 30
'@
    Set-Content -LiteralPath $victimScript -Value $victimSource -Encoding UTF8
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $victim = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',$victimScript,$root,$victimBase) -PassThru -WindowStyle Hidden
    if (-not $victim.WaitForExit(180000)) { $victim.Kill(); throw 'The victim session did not exit.' }
    if (-not (Test-Path -LiteralPath (Join-Path $victimBase 'marker.txt'))) { throw 'The victim session never reached the kill point.' }
    $victimFolder = (Get-Content -LiteralPath (Join-Path $victimBase 'folder.txt') -Raw).Trim()
    $killedLines = @([AppStudio.SessionLog]::ReadAllLines((Join-Path $victimFolder 'steps.jsonl')))
    if ($killedLines.Count -ne 50) { throw ('A killed session kept only ' + $killedLines.Count + ' of 50 records.') }
    $indexes = @()
    foreach ($line in $killedLines) {
        $record = ConvertFrom-Json $line
        if ([string]::IsNullOrEmpty($record.at)) { throw 'A record was written without a timestamp.' }
        if ([string]::IsNullOrEmpty($record.stepId)) { throw 'A record was written without an identity.' }
        $indexes += $record.index
    }
    if ($indexes[0] -ne 1 -or $indexes[49] -ne 50) { throw 'Records are out of order.' }
    # The index the session list reads has to have survived too.
    if (-not (Test-Path -LiteralPath (Join-Path $victimFolder 'meta.json'))) { throw 'The session index was lost.' }
    $reloaded = [AppStudio.SessionStore]::Load($victimFolder)
    if ($reloaded.Steps.Count -ne 50) { throw ('A killed session reloaded ' + $reloaded.Steps.Count + ' of 50 steps.') }

    # ---- 2. a session that cannot be written says so ------------------------
    $blockedPath = Join-Path $tempDir 'blocked.txt'
    Set-Content -LiteralPath $blockedPath -Value 'not a directory' -Encoding UTF8
    $blocked = New-Object AppStudio.StudioSession
    $blocked.Id = 'blocked'
    $blocked.Folder = Join-Path $blockedPath 'inside'
    $record = New-Object AppStudio.JsonObject
    $null = $record.Add('kind', 'test.record')
    if ([AppStudio.SessionStore]::Append($blocked, 'events', $record)) { throw 'A record was claimed as written into a path that is a file.' }
    if ($blocked.Diagnostics.Count -lt 1) { throw 'A refused write gave no reason.' }
    if ($blocked.Diagnostics[0] -notmatch 'STORE-WRITE') { throw ('The refusal was not stated as a store failure: ' + $blocked.Diagnostics[0]) }
    if (Test-Path -LiteralPath ($blockedPath + '_2')) { throw 'The store silently fell back to another path.' }

    # ---- 3. the growing record stays readable while the session runs --------
    $live = [AppStudio.SessionStore]::Create($tempDir, 'record', 'autosave live')
    for ($index = 1; $index -le 5; $index++) {
        $item = New-Object AppStudio.JsonObject
        $null = $item.Add('kind', 'test.live')
        $null = $item.Add('index', $index)
        if (-not [AppStudio.SessionStore]::Append($live, 'events', $item)) { throw 'A record was refused by a usable session.' }
    }
    $eventsPath = Join-Path $live.Folder 'events.jsonl'
    # Held open by a writer, exactly as it is while a recording runs.
    $holder = [IO.File]::Open($eventsPath, 'Open', 'Write', 'ReadWrite')
    try {
        $whileOpen = @([AppStudio.SessionLog]::ReadAllLines($eventsPath))
        if ($whileOpen.Count -ne 5) { throw ('The open record showed ' + $whileOpen.Count + ' of 5 lines.') }
    } finally { $holder.Dispose() }

    # ---- 4. rewriting the index in place leaves no debris -------------------
    [AppStudio.SessionStore]::WriteMeta($live)
    $live.Title = 'autosave live, renamed'
    [AppStudio.SessionStore]::WriteMeta($live)
    $meta = [AppStudio.SessionStore]::Load($live.Folder)
    if ($meta.Title -ne 'autosave live, renamed') { throw 'The session index was not replaced in place.' }
    if (@(Get-ChildItem -LiteralPath $live.Folder -Filter '*.tmp').Count -ne 0) { throw 'A temporary file was left behind.' }

    Write-Output ('PASS test-autosave path=SessionStore.Append killedRecords=' + $killedLines.Count + ' order=1..50 reloaded=' + $reloaded.Steps.Count + ' refusalStated=STORE-WRITE noFallback=1 readableWhileOpen=5 indexReplace=ok')
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
