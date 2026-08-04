$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The product against the calculator Windows ships with, driven by the physical
# pointer, because that is where the faults were seen: presses going missing
# from a recording, and a replay that appeared to do nothing.
#
# Nothing here is satisfied by "the call was made". A recording is checked press
# by press against what was sent, and a replay is only a pass when the
# calculator's own result actually changes to the expected value.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-calculator-e2e (moves the real pointer; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$shotDir = Join-Path $root 'artifacts\calculator-e2e'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null

if ($null -eq ('CalcE2E.In' -as [type])) {
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Threading;
namespace CalcE2E{
public static class In{
 [StructLayout(LayoutKind.Sequential)] struct MI{public int dx;public int dy;public uint d;public uint f;public uint t;public IntPtr e;}
 [StructLayout(LayoutKind.Sequential)] struct KI{public ushort v;public ushort s;public uint f;public uint t;public IntPtr e;}
 [StructLayout(LayoutKind.Explicit)] struct U{[FieldOffset(0)]public MI m;[FieldOffset(0)]public KI k;}
 [StructLayout(LayoutKind.Sequential)] struct INP{public uint type;public U u;}
 [DllImport("user32.dll")] static extern uint SendInput(uint n,INP[] i,int s);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 public static bool Click(int x,int y){
  if(!SetCursorPos(x,y))return false;
  Thread.Sleep(45);
  INP[] d=new INP[1];d[0].type=0;d[0].u.m.f=0x0002;
  if(SendInput(1,d,Marshal.SizeOf(typeof(INP)))!=1)return false;
  Thread.Sleep(55);
  INP[] u=new INP[1];u[0].type=0;u[0].u.m.f=0x0004;
  return SendInput(1,u,Marshal.SizeOf(typeof(INP)))==1;
 }
}
public static class Cursor{
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 public static void Move(int x,int y){ SetCursorPos(x,y); }
}}
'@
}

function Get-Calc {
    foreach ($w in [AppStudio.WindowTools]::ListStackOrder((New-Object 'long[]' 0), 0)) {
        if ($w.ClassName -ne 'ApplicationFrameWindow') { continue }
        foreach ($cp in [AppStudio.WindowTools]::ContentProcessIds([IntPtr]$w.Hwnd, $w.ProcessId)) {
            $p = Get-Process -Id $cp -ErrorAction SilentlyContinue
            if ($null -ne $p -and $p.ProcessName -match 'Calculator') { return $w }
        }
    }
    return $null
}
function All-Of($e,$t){$c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$t);return $e.FindAll([System.Windows.Automation.TreeScope]::Descendants,$c)}
function Find-Named($s,$t,$l){foreach($i in All-Of $s $t){if($i.Current.Name -eq $l -and -not $i.Current.IsOffscreen){return $i}};return $null}
function App-Roots($q){$c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$q);return [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$c)}
function Wait-Ctl($q,$type,$label,$ms){$lim=[DateTime]::UtcNow.AddMilliseconds($ms);while([DateTime]::UtcNow -lt $lim){foreach($t in App-Roots $q){$b=Find-Named $t $type $label;if($null -ne $b){return $b}};Start-Sleep -Milliseconds 250};return $null}
function Msg([string]$n,[string]$f){$p=Join-Path $root ('assets\messages\'+$n);if(Test-Path $p){return ([IO.File]::ReadAllText($p,(New-Object Text.UTF8Encoding($false)))).Trim()};return $f}
function Shoot([long]$hwnd,[string]$name){
    if ($hwnd -eq 0) {
        $vs=[System.Windows.Forms.SystemInformation]::VirtualScreen
        $r=New-Object AppStudio.RectValue; $r.X=$vs.Left; $r.Y=$vs.Top; $r.Width=$vs.Width; $r.Height=$vs.Height
        $null=[AppStudio.Capture]::Crop($r,(New-Object 'AppStudio.MaskRect[]' 0),(Join-Path $shotDir ($name+'.png')),[IntPtr]::Zero)
        return
    }
    $r=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hwnd); if($null -eq $r){return}
    $null=[AppStudio.Capture]::Crop($r,(New-Object 'AppStudio.MaskRect[]' 0),(Join-Path $shotDir ($name+'.png')),[IntPtr]$hwnd)
}
# The calculator's own result, read by acquiring the window. Never a point probe:
# a point probe is part of what is under test.
function Read-Display($calc){
    $t=[AppStudio.SessionStore]::Create($env:TEMP,'snap','read')
    $r=New-Object AppStudio.ScanRunner($root)
    try{ $null=[AppStudio.Acquire]::Window($r,$t,$calc,(New-Object AppStudio.ScanLimits),$null) }finally{$r.Dispose()}
    $d=@($t.Elements | Where-Object { $_.AutomationId -eq 'CalculatorResults' })
    Remove-Item -LiteralPath $t.Folder -Recurse -Force -ErrorAction SilentlyContinue
    if($d.Count -eq 0){return '(no result element)'}
    return $d[0].Name
}
function Digits([string]$text){ return (($text -replace '[^0-9]','')) }

$calc = Get-Calc
if ($null -eq $calc) { Start-Process 'calc.exe' | Out-Null; Start-Sleep -Seconds 5; $calc = Get-Calc }
if ($null -eq $calc) { throw 'The calculator did not open.' }
[AppStudio.WindowTools]::BringToFront($calc.Hwnd) | Out-Null
Start-Sleep -Milliseconds 700
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 600

# Where the buttons are, learned once before anything is recorded.
$pre = [AppStudio.SessionStore]::Create($env:TEMP, 'snap', 'calc map')
$runner = New-Object AppStudio.ScanRunner($root)
try { $null = [AppStudio.Acquire]::Window($runner, $pre, $calc, (New-Object AppStudio.ScanLimits), $null) } finally { $runner.Dispose() }
$map = @{}
foreach ($n in $pre.Elements) { if ($n.AutomationId -and $n.Rect -and $n.Rect.Width -gt 10) { $map[$n.AutomationId] = $n.Rect } }
Remove-Item -LiteralPath $pre.Folder -Recurse -Force -ErrorAction SilentlyContinue

# Several series, not one: digits, an operator, a clear, and a long run.
$series = @(
  @{ name='add';      keys=@('num7Button','plusButton','num8Button','equalButton');                          expect='15' },
  @{ name='clearAdd'; keys=@('clearButton','num1Button','num2Button','plusButton','num3Button','equalButton'); expect='15' },
  @{ name='multiply'; keys=@('clearButton','num9Button','multiplyButton','num6Button','equalButton');          expect='54' }
)
foreach ($s in $series) { foreach ($k in $s.keys) { if (-not $map.ContainsKey($k)) { throw ('button not mapped: ' + $k) } } }

$ps5 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$sessionRoot = Join-Path $root 'runtime\sessions'
$before = @{}
if (Test-Path $sessionRoot) { Get-ChildItem $sessionRoot -Directory | ForEach-Object { $before[$_.Name] = $true } }
$app = Start-Process $ps5 -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','900000') -PassThru -WindowStyle Hidden
$totalSent = 0
$totalRecorded = 0
$replayResults = @()
try {
    $snapBtn = Wait-Ctl $app.Id ([System.Windows.Automation.ControlType]::Button) (Msg 'home-snap.txt' 'Snap') 60000
    if ($null -eq $snapBtn) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1500
    $appWin = $null
    foreach($t in App-Roots $app.Id){ if($null -ne (Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'home-snap.txt' 'Snap'))){$appWin=$t} }
    $appHwnd = [int64]$appWin.Current.NativeWindowHandle
    $launcherRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$appHwnd)
    Shoot $appHwnd '01-launcher'

    # ---- the launcher has to be small ------------------------------------
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    if ($launcherRect.Width -gt ($work.Width * 0.65) -or $launcherRect.Height -gt ($work.Height * 0.65)) {
        throw ('The launcher is too large: ' + $launcherRect.Width + 'x' + $launcherRect.Height + ' on a ' + $work.Width + 'x' + $work.Height + ' desktop.')
    }
    Write-Output ('launcher = ' + $launcherRect.Width + 'x' + $launcherRect.Height + ' physical')

    # ---- snap, then focus has to come back -------------------------------
    $snapBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 4000
    # Move the way a hand does so the chooser follows the pointer, then take the
    # window from the middle of it rather than from the screen edge.
    $cx = $calc.Rect.X + [int]($calc.Rect.Width/2)
    $cy = $calc.Rect.Y + [int]($calc.Rect.Height/2)
    for ($i = 1; $i -le 12; $i++) {
        [CalcE2E.Cursor]::Move(($calc.Rect.X + 30 + [int](($cx - $calc.Rect.X - 30) * $i / 12)), ($calc.Rect.Y + 40 + [int](($cy - $calc.Rect.Y - 40) * $i / 12)))
        Start-Sleep -Milliseconds 45
    }
    Start-Sleep -Milliseconds 900
    Shoot 0 '01b-picker-overlay'
    [CalcE2E.In]::Click($cx, $cy) | Out-Null
    Start-Sleep -Seconds 2
    $lim=[DateTime]::UtcNow.AddSeconds(200); $snapDone=$false
    while(-not $snapDone -and [DateTime]::UtcNow -lt $lim){
        foreach ($d in Get-ChildItem $sessionRoot -Directory -ErrorAction SilentlyContinue) {
            if ($before.ContainsKey($d.Name)) { continue }
            if (Test-Path (Join-Path $d.FullName 'out\ai\session.md')) { $snapDone=$true }
        }
        if(-not $snapDone){ Start-Sleep -Milliseconds 1000 }
    }
    if (-not $snapDone) { throw 'The snap produced no session.' }
    Start-Sleep -Seconds 3
    $front = [AppStudio.WindowTools]::Foreground()
    $focusBack = ($null -ne $front -and $front.ProcessId -eq $app.Id)
    Write-Output ('after snap the front window is ' + $front.ProcessName + ' (App Studio = ' + $focusBack + ')')
    foreach($t in App-Roots $app.Id){ if($null -ne (Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'home-snap.txt' 'Snap'))){ Shoot ([int64]$t.Current.NativeWindowHandle) '02-after-snap-result' } }
    if (-not $focusBack) { throw 'After the snap the front window was not App Studio.' }

    # ---- record each series, at a pace a person actually clicks at -------
    foreach ($s in $series) {
        [AppStudio.WindowTools]::BringToFront($calc.Hwnd) | Out-Null
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 500
        foreach($t in App-Roots $app.Id){ if($null -ne (Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'home-snap.txt' 'Snap'))){ [AppStudio.WindowTools]::BringToFront([int64]$t.Current.NativeWindowHandle) | Out-Null } }
        Start-Sleep -Milliseconds 700
        $rec = Wait-Ctl $app.Id ([System.Windows.Automation.ControlType]::Button) (Msg 'home-record.txt' 'Record') 30000
        if ($null -eq $rec) { throw 'record button not found' }
        $mark = @{}
        Get-ChildItem $sessionRoot -Directory | ForEach-Object { $mark[$_.Name] = $true }
        $rec.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 5200
        [AppStudio.WindowTools]::BringToFront($calc.Hwnd) | Out-Null
        Start-Sleep -Milliseconds 900
        if ($s.name -eq 'add') {
            foreach($t in App-Roots $app.Id){ $sb=Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'hud-stop.txt' 'Stop'); if($null -ne $sb){ Shoot ([int64]$t.Current.NativeWindowHandle) '03-recording-hud' } }
        }
        foreach ($k in $s.keys) {
            $r = $map[$k]
            if (-not [CalcE2E.In]::Click(($r.X+[int]($r.Width/2)),($r.Y+[int]($r.Height/2)))) { throw ('click failed: '+$k) }
            Start-Sleep -Milliseconds 110
        }
        $totalSent += $s.keys.Count
        Start-Sleep -Seconds 5
        $liveResult = Digits (Read-Display $calc)
        $stop = Wait-Ctl $app.Id ([System.Windows.Automation.ControlType]::Button) (Msg 'hud-stop.txt' 'Stop') 25000
        if ($null -eq $stop) { throw 'stop control not found' }
        $stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 6
        $folder = $null
        $lim = [DateTime]::UtcNow.AddSeconds(240)
        while ($null -eq $folder -and [DateTime]::UtcNow -lt $lim) {
            foreach ($d in Get-ChildItem $sessionRoot -Directory) {
                if ($mark.ContainsKey($d.Name)) { continue }
                if (Test-Path (Join-Path $d.FullName 'steps.jsonl')) { $folder = $d.FullName }
            }
            if ($null -eq $folder) { Start-Sleep -Milliseconds 1000 }
        }
        if ($null -eq $folder) { throw ('no session for series ' + $s.name) }
        $sess = [AppStudio.SessionStore]::Load($folder)
        $clicks = @($sess.Steps | Where-Object { $_.Kind -eq 'click' })
        $totalRecorded += $clicks.Count
        Write-Output ('series ' + $s.name + ': sent=' + $s.keys.Count + ' recorded=' + $clicks.Count + ' liveResult=' + $liveResult + ' expect=' + $s.expect)
        if ($clicks.Count -ne $s.keys.Count) {
            $got = @($clicks | ForEach-Object { $_.AutomationId })
            throw ('series ' + $s.name + ' lost presses: sent ' + $s.keys.Count + ' recorded ' + $clicks.Count + ' [' + ($got -join ',') + ']')
        }
        for ($i = 0; $i -lt $s.keys.Count; $i++) {
            if ($clicks[$i].AutomationId -ne $s.keys[$i]) {
                throw ('series ' + $s.name + ' step ' + $i + ' recorded [' + $clicks[$i].AutomationId + '] but [' + $s.keys[$i] + '] was pressed')
            }
        }
        if ($liveResult -ne $s.expect) { throw ('series ' + $s.name + ' did not compute ' + $s.expect + ' during recording; got ' + $liveResult) }
        $s.folder = $folder
    }

    # ---- replay the last series and require the calculator to change -----
    $target = $series[$series.Count - 1]
    [AppStudio.WindowTools]::BringToFront($calc.Hwnd) | Out-Null
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 800
    $beforeReplay = Digits (Read-Display $calc)
    Shoot $calc.Hwnd '04-calc-before-replay'
    if ($beforeReplay -eq $target.expect) { throw 'the calculator already showed the expected value before replay' }

    $win = $null
    foreach($t in App-Roots $app.Id){ if($null -ne (Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'home-snap.txt' 'Snap'))){$win=$t} }
    if ($null -eq $win) { throw 'App Studio window gone' }
    [AppStudio.WindowTools]::BringToFront([int64]$win.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 900
    $play = Find-Named $win ([System.Windows.Automation.ControlType]::Button) (Msg 'detail-replay.txt' 'Replay')
    if ($null -eq $play) { throw 'the replay button is not on the result screen' }
    $play.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1500
    # Pressing replay without permission must ask, not fail quietly.
    $ok = Wait-Ctl $app.Id ([System.Windows.Automation.ControlType]::Button) (Msg 'replay-consent-ok.txt' 'Allow and replay') 15000
    if ($null -eq $ok) { throw 'replay neither ran nor asked for permission' }
    Write-Output 'replay asked for permission before doing anything'
    foreach($t in App-Roots $app.Id){ $b=Find-Named $t ([System.Windows.Automation.ControlType]::Button) (Msg 'replay-consent-ok.txt' 'Allow and replay'); if($null -ne $b){ Shoot ([int64]$t.Current.NativeWindowHandle) '05-replay-consent' } }
    $ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $log = Join-Path $target.folder 'replay.jsonl'
    $sess = [AppStudio.SessionStore]::Load($target.folder)
    $lim = [DateTime]::UtcNow.AddSeconds(420)
    while ([DateTime]::UtcNow -lt $lim) {
        if (Test-Path $log) {
            $lines = @([AppStudio.SessionLog]::ReadAllLines($log))
            if ($lines.Count -ge $sess.Steps.Count) { break }
            if ($lines.Count -gt 0 -and (ConvertFrom-Json $lines[$lines.Count-1]).result.state -ne 'done') { break }
        }
        Start-Sleep -Milliseconds 1000
    }
    Start-Sleep -Seconds 5
    $afterReplay = Digits (Read-Display $calc)
    Shoot $calc.Hwnd '06-calc-after-replay'
    if (Test-Path $log) {
        foreach ($line in [AppStudio.SessionLog]::ReadAllLines($log)) {
            $o = ConvertFrom-Json $line
            $trail = ($o.result.attempts | Where-Object { $_.route -ne 'resolve' } | ForEach-Object { $_.method + ':' + $_.outcome }) -join ' -> '
            $replayResults += ($o.stepId + '=' + $o.result.state)
            Write-Output ('  replay ' + $o.stepId + ' ' + $o.result.state + ' | ' + $trail)
        }
    }
    Write-Output ('calculator before replay = ' + $beforeReplay + ' , after replay = ' + $afterReplay + ' , expected ' + $target.expect)
    if ($afterReplay -ne $target.expect) {
        throw ('REPLAY DID NOT DRIVE THE CALCULATOR: expected ' + $target.expect + ' but the display shows ' + $afterReplay)
    }

    Write-Output ''
    Write-Output ('PASS test-calculator-e2e launcher=' + $launcherRect.Width + 'x' + $launcherRect.Height +
        ' focusReturnedAfterSnap=1 series=' + $series.Count + ' clicksSent=' + $totalSent + ' clicksRecorded=' + $totalRecorded +
        ' dropped=' + ($totalSent - $totalRecorded) + ' replayConsent=asked replayDisplay=' + $beforeReplay + '->' + $afterReplay +
        ' expected=' + $target.expect)
} finally {
    if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    $app.Dispose()
}
