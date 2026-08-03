param([int]$Iterations = 20)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath -Iterations $Iterations
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-hang-recovery-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$healthyProcess = $null
$hangProcess = $null
$heartbeat = $null
function Wait-File([string]$Path, [int]$Seconds) {
    $limit=[DateTime]::UtcNow.AddSeconds($Seconds)
    while(-not (Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $limit){Start-Sleep -Milliseconds 20}
    if(-not (Test-Path -LiteralPath $Path)){throw ('Timed out waiting for ' + $Path)}
}
function Read-Map([string]$Path) {
    $map=@{}; Get-Content -LiteralPath $Path|ForEach-Object{$p=$_.Split('=',2);$map[$p[0]]=$p[1]}; return $map
}
function Is-StrictIncrease($Values) {
    for($i=1;$i -lt $Values.Count;$i++){if([int64]$Values[$i] -le [int64]$Values[$i-1]){return $false}}
    return $Values.Count -gt 1
}
try {
    $healthyProcess = Start-Process -FilePath $build.FixtureWpf -ArgumentList @('--kind','healthy','--hang-mode','permanent','--run-dir',$tempDir,'--prefix','healthy','--left','80') -PassThru
    $hangProcess = Start-Process -FilePath $build.FixtureWpf -ArgumentList @('--kind','hang','--hang-mode','permanent','--run-dir',$tempDir,'--prefix','hang','--left','520') -PassThru
    Wait-File (Join-Path $tempDir 'healthy.ready') 10
    Wait-File (Join-Path $tempDir 'hang.ready') 10
    $healthy=Read-Map (Join-Path $tempDir 'healthy.ready')
    $hang=Read-Map (Join-Path $tempDir 'hang.ready')
    [AppStudio.Probe]::Configure($root,$false)
    $initialHealth=[AppStudio.Probe]::GetHealth()
    if(-not $initialHealth.ActiveWarmupPerformed -or -not $initialHealth.SpareWarmupPerformed){throw 'ready-before-warmup regression'}
    $heartbeat=New-Object AppStudio.UiResponsivenessProbe(50)

    $baseline=@()
    1..5|ForEach-Object{$s=[AppStudio.Probe]::At([int]$healthy.x,[int]$healthy.y,1500);if($s.Uia.AutomationId-ne'TargetText'){throw 'Healthy baseline failed'};$baseline += $s.DurationMs}
    $threads=@();$handles=@();$memory=@();$workers=@();$backToBack=0;$maxReturn=0;$maxHealthy=0
    for($i=1;$i -le $Iterations;$i++){
        $release=Join-Path $tempDir 'hang.release';$hung=Join-Path $tempDir 'hang.hung';$recovered=Join-Path $tempDir 'hang.recovered'
        Remove-Item -LiteralPath $release,$hung,$recovered -Force -ErrorAction SilentlyContinue
        $token='iteration-'+$i
        [IO.File]::WriteAllText((Join-Path $tempDir 'hang.trigger'),$token,(New-Object Text.UTF8Encoding($false)))
        Wait-File $hung 5
        $watch=[Diagnostics.Stopwatch]::StartNew();$failed=[AppStudio.Probe]::At([int]$hang.x,[int]$hang.y,1500);$watch.Stop()
        if($watch.ElapsedMilliseconds-gt1800){throw ('timeout bound exceeded iteration '+$i+': '+$watch.ElapsedMilliseconds)}
        if($failed.UiaStatus.State-ne'unavailable' -or $failed.UiaStatus.Reasons[0].Code-ne'UIA-TIMEOUT'){throw ('timeout outcome mismatch iteration '+$i)}
        if($failed.Win32Status.State-eq'unavailable'){throw ('Win32 disappeared iteration '+$i)}
        $healthyWatch=[Diagnostics.Stopwatch]::StartNew();$good=[AppStudio.Probe]::At([int]$healthy.x,[int]$healthy.y,1500);$healthyWatch.Stop()
        if($good.Uia.AutomationId-ne'TargetText'){throw ('immediate healthy failed iteration '+$i)}
        if($watch.ElapsedMilliseconds-gt$maxReturn){$maxReturn=$watch.ElapsedMilliseconds};if($healthyWatch.ElapsedMilliseconds-gt$maxHealthy){$maxHealthy=$healthyWatch.ElapsedMilliseconds}
        $health=[AppStudio.Probe]::GetHealth();if($health.QueueDepth-ne0 -or $health.OrphanProcessCount-ne0){throw ('queue/orphan iteration '+$i)}
        if(-not $health.ActiveWarmupPerformed){throw ('promoted worker was not UIA-warm iteration '+$i)}
        if($health.State-eq'warming-spare'){$backToBack++}
        $current=[Diagnostics.Process]::GetCurrentProcess();$current.Refresh();$threads += $current.Threads.Count;$handles += $current.HandleCount;$memory += $current.WorkingSet64;$workers += (@($health.ActiveProcessId,$health.SpareProcessId|Where-Object{$_-gt0}).Count)
        [IO.File]::WriteAllText($release,$token,[Text.Encoding]::ASCII);$limit=[DateTime]::UtcNow.AddSeconds(5);while(([string](Get-Content $recovered -ErrorAction SilentlyContinue))-ne$token -and [DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 20}
        Write-Output ('ITER '+$i+' returnMs='+$watch.ElapsedMilliseconds+' healthyMs='+$healthyWatch.ElapsedMilliseconds+' state='+$health.State+' restart='+$health.RestartCount+' orphan='+$health.OrphanProcessCount)
    }
    if($Iterations-ge20){if(Is-StrictIncrease $threads){throw 'Thread count increased at every sample'};if(Is-StrictIncrease $handles){throw 'Handle count increased at every sample'};if(Is-StrictIncrease $memory){throw 'Working set increased at every sample'};if(Is-StrictIncrease $workers){throw 'Worker count increased at every sample'}}
    if($backToBack-lt1){throw 'Back-to-back hang before spare completion was not exercised'}
    $final=[AppStudio.Probe]::GetHealth();if($final.OrphanProcessCount-ne0){throw 'Final orphan process count was nonzero'}
    if($heartbeat.MaxUnresponsiveMs-gt250){throw ('UI heartbeat exceeded threshold: '+$heartbeat.MaxUnresponsiveMs)}
    Write-Output ('PASS test-hang-recovery iterations='+$Iterations+' baselineMedianMs='+(@($baseline|Sort-Object)[2])+' maxReturnMs='+$maxReturn+' maxHealthyMs='+$maxHealthy+' backToBack='+$backToBack+' uiMaxMs='+$heartbeat.MaxUnresponsiveMs+' restarts='+$final.RestartCount)
} finally {
    if($null-ne$heartbeat){$heartbeat.Dispose()}
    [AppStudio.Probe]::Shutdown()
    $release=Join-Path $tempDir 'hang.release';Set-Content -LiteralPath $release -Value release -Encoding Ascii -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 100
    foreach($p in @($healthyProcess,$hangProcess)){if($null-ne$p -and -not$p.HasExited){$p.Kill();$p.WaitForExit()}}
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
