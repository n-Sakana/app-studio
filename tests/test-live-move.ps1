$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build=& (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir=Join-Path ([IO.Path]::GetTempPath()) ('pui-live-move-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir|Out-Null
$process=$null;$overlay=$null
function Assert-Rect($Actual,$Expected,[string]$Stage){
    if($null-eq$Actual -or $null-eq$Expected){throw ($Stage+' rectangle missing')}
    if([Math]::Abs($Actual.X-$Expected.X)-gt2 -or [Math]::Abs($Actual.Y-$Expected.Y)-gt2 -or [Math]::Abs($Actual.Width-$Expected.Width)-gt2 -or [Math]::Abs($Actual.Height-$Expected.Height)-gt2){throw ($Stage+' overlay mismatch actual='+$Actual.X+','+$Actual.Y+','+$Actual.Width+','+$Actual.Height+' expected='+$Expected.X+','+$Expected.Y+','+$Expected.Width+','+$Expected.Height)}
}
try{
    $process=Start-Process -FilePath $build.FixtureWpf -ArgumentList @('--kind','healthy','--hang-mode','permanent','--run-dir',$tempDir,'--prefix','move','--left','80') -PassThru
    $ready=Join-Path $tempDir 'move.ready';$limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWpf not ready'}
    $map=@{};Get-Content $ready|ForEach-Object{$p=$_.Split('=',2);$map[$p[0]]=$p[1]}
    [AppStudio.Probe]::Configure($root,$false)
    $first=[AppStudio.Probe]::At([int]$map.x,[int]$map.y,1500);if($first.Uia.AutomationId-ne'TargetText'){throw 'Initial UIA target mismatch'}
    $overlay=New-Object AppStudio.OverlayController
    $overlay.ShowSnapshot($first,[int]$map.x,[int]$map.y)
    Assert-Rect $overlay.GetFrameRect() $first.Uia.BoundingRect 'initial'
    $window=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$map.window)
    $screens=[System.Windows.Forms.Screen]::AllScreens
    if($screens.Count-gt1){$nextX=$screens[1].WorkingArea.Left+100;$nextY=$screens[1].WorkingArea.Top+100}else{$nextX=$window.X+140;$nextY=$window.Y+90}
    $dx=$nextX-$window.X;$dy=$nextY-$window.Y
    if(-not[AppStudio.WindowTools]::Move([IntPtr][int64]$map.window,$nextX,$nextY,$window.Width,$window.Height)){throw 'Fixture move failed'}
    Start-Sleep -Milliseconds 250
    $second=[AppStudio.Probe]::At(([int]$map.x+$dx),([int]$map.y+$dy),1500);if($second.Uia.AutomationId-ne'TargetText'){throw 'Moved UIA target mismatch'}
    if([string]::IsNullOrWhiteSpace($second.Win32.MonitorId)-or$second.Win32.Dpi-lt96){throw ('Win32 monitor/DPI missing: '+$second.Win32.MonitorId+' '+$second.Win32.Dpi)}
    $overlay.ShowSnapshot($second,([int]$map.x+$dx),([int]$map.y+$dy))
    Assert-Rect $overlay.GetFrameRect() $second.Uia.BoundingRect 'moved'
    if([Math]::Abs(($second.Uia.BoundingRect.X-$first.Uia.BoundingRect.X)-$dx)-gt2 -or [Math]::Abs(($second.Uia.BoundingRect.Y-$first.Uia.BoundingRect.Y)-$dy)-gt2){throw 'Moved rectangle delta mismatch'}
    $health=[AppStudio.Probe]::GetHealth();if($health.State-ne'ready' -or -not$health.ActiveWarmupPerformed -or -not$health.SpareWarmupPerformed){throw 'Health display source was not ready'}
    $appliedDpi=(Get-ItemProperty -LiteralPath 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
    Write-Output ('PASS test-live-move dx='+$dx+' dy='+$dy+' frame='+$second.Uia.BoundingRect.Width+'x'+$second.Uia.BoundingRect.Height+' coordinateMode=physical appliedDpi='+$appliedDpi+' elementDpi='+$second.Win32.Dpi+' monitorId='+$second.Win32.MonitorId+' monitors='+$screens.Count+' health='+$health.State)
}finally{
    if($null-ne$overlay){$overlay.Dispose()};[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item -LiteralPath $tempDir -Recurse -Force
}
