$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The three things a pointer does that no accessibility pattern expresses:
# pressing twice, dragging, and turning the wheel. They are recorded on a window
# that says what reached it, and then replayed - and the pass condition is that
# the window says the same things arrived a second time.
#
# The window is a fixture this test starts and stops itself, so nothing on the
# operator's desktop is ever dragged or scrolled.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-gesture-e2e (moves the real pointer; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-gesture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

if ($null -eq ('PuiGesture.Input' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiGesture
{
    public static class Input
    {
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        private const uint INPUT_MOUSE = 0;
        private const uint LEFTDOWN = 0x0002;
        private const uint LEFTUP = 0x0004;
        private const uint WHEEL = 0x0800;

        private static void Mouse(uint flags, uint data)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].data.mouse.flags = flags;
            inputs[0].data.mouse.mouseData = data;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(110);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(70);
            Mouse(LEFTUP, 0);
            Thread.Sleep(300);
        }

        public static void DoubleClick(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(110);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(40);
            Mouse(LEFTUP, 0);
            Thread.Sleep(70);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(40);
            Mouse(LEFTUP, 0);
            Thread.Sleep(400);
        }

        public static void Drag(int fromX, int fromY, int toX, int toY)
        {
            SetCursorPos(fromX, fromY);
            Thread.Sleep(110);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(90);
            for (int index = 1; index <= 12; index++)
            {
                SetCursorPos(fromX + (toX - fromX) * index / 12, fromY + (toY - fromY) * index / 12);
                Thread.Sleep(28);
            }
            Thread.Sleep(90);
            Mouse(LEFTUP, 0);
            Thread.Sleep(400);
        }

        public static void Wheel(int x, int y, int notches)
        {
            SetCursorPos(x, y);
            Thread.Sleep(110);
            for (int index = 0; index < Math.Abs(notches); index++)
            {
                Mouse(WHEEL, unchecked((uint)(notches < 0 ? -120 : 120)));
                Thread.Sleep(70);
            }
            Thread.Sleep(400);
        }
    }
}
'@
}

function Events($path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $lines = @()
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $lines += (ConvertFrom-Json $line) }
    }
    return $lines
}
function Count-Of($events, $kind) { return @($events | Where-Object { $_.kind -eq $kind }).Count }

$fixture = $null
$recorder = $null
$engine = $null
try {
    $ready = Join-Path $temp 'target.json'
    $events = Join-Path $temp 'events.jsonl'
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $fixture = Start-Process $build.FixtureInputTarget -ArgumentList @(
        '--ready', $ready, '--events', $events, '--left', ($work.Left + 60), '--top', ($work.Top + 60)) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ((-not (Test-Path $ready)) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 60 }
    if (-not (Test-Path $ready)) { throw 'FixtureInputTarget did not become ready.' }
    $f = Get-Content $ready -Raw | ConvertFrom-Json
    [AppStudio.ScreenCapture]::Raise([int64]$f.window)
    Start-Sleep -Milliseconds 700

    $surface = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.surface)
    $left = @(($surface.X + 40), ($surface.Y + 20))
    $right = @(($surface.X + $surface.Width - 40), ($surface.Y + $surface.Height - 20))
    $middle = @(($surface.X + [int]($surface.Width / 2)), ($surface.Y + [int]($surface.Height / 2)))

    # ===== 1. record the three gestures =====================================
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'gesture fixture')
    $recorder = New-Object AppStudio.Recorder($root, $session, $null, $null)
    $recorder.Start()
    Start-Sleep -Milliseconds 2500

    [PuiGesture.Input]::DoubleClick($middle[0], $middle[1])
    Start-Sleep -Milliseconds 900
    [PuiGesture.Input]::Drag($left[0], $left[1], $right[0], $right[1])
    Start-Sleep -Milliseconds 900
    [PuiGesture.Input]::Wheel($middle[0], $middle[1], -3)
    Start-Sleep -Milliseconds 1200

    $recorder.Stop()
    $recorder = $null
    [AppStudio.SessionStore]::WriteMeta($session)

    $recorded = Events $events
    if ((Count-Of $recorded 'doubleClick') -lt 1) { throw 'the fixture never saw a double click while recording' }
    if ((Count-Of $recorded 'drag') -lt 1) { throw 'the fixture never saw a drag while recording' }
    if ((Count-Of $recorded 'wheel') -lt 1) { throw 'the fixture never saw a wheel turn while recording' }

    $steps = @($session.Steps)
    foreach ($kind in @('doubleClick', 'drag', 'wheel')) {
        if (@($steps | Where-Object { $_.Kind -eq $kind }).Count -lt 1) {
            throw ('no ' + $kind + ' step was recorded; kinds were ' + ((@($steps | ForEach-Object { $_.Kind }) | Sort-Object -Unique) -join ','))
        }
    }
    $dragStep = @($steps | Where-Object { $_.Kind -eq 'drag' })[0]
    if ($dragStep.DropLocators.Count -lt 1) { throw 'the drag step does not say where it was released' }
    # The drop has to be addressed by identity, not by where the pointer was.
    $identifying = @($dragStep.DropLocators | Where-Object { [AppStudio.LocatorResolver]::Identifies($_.Strategy) })
    if ($identifying.Count -lt 1) { throw 'the drop is described only by position, so it could never be replayed' }

    # ===== 2. replay, and ask the fixture what arrived ======================
    $baseline = @{}
    foreach ($kind in @('doubleClick', 'drag', 'wheel', 'click')) { $baseline[$kind] = Count-Of $recorded $kind }

    $engine = New-Object AppStudio.ReplayEngine($root, $session)
    $report = $engine.Run('auto', $true)
    $engine.Dispose()
    $engine = $null
    Start-Sleep -Milliseconds 1200

    $afterReplay = Events $events
    $delta = @{}
    foreach ($kind in @('doubleClick', 'drag', 'wheel')) { $delta[$kind] = (Count-Of $afterReplay $kind) - $baseline[$kind] }
    $trail = @()
    foreach ($step in $session.Steps) {
        if ($null -ne $step.LastReplay) { $trail += ($step.StepId + '/' + $step.Kind + '=' + $step.LastReplay.State + ':' + $step.LastReplay.Reason) }
    }
    foreach ($kind in @('doubleClick', 'drag', 'wheel')) {
        if ($delta[$kind] -lt 1) {
            throw ('the replay did not produce a ' + $kind + ' in the target window. deltas=' +
                (($delta.Keys | Sort-Object | ForEach-Object { $_ + '=' + $delta[$_] }) -join ',') + ' trail=' + ($trail -join ' | '))
        }
    }
    # A drag has to travel; a replay that presses and releases in one place is
    # not a drag however it is labelled.
    $replayedDrags = @($afterReplay | Where-Object { $_.kind -eq 'drag' })
    $lastDrag = $replayedDrags[$replayedDrags.Count - 1]
    if ([Math]::Abs([int]$lastDrag.dx) -lt 20) { throw ('the replayed drag travelled only ' + $lastDrag.dx + ' px') }

    # The route trail has to say plainly that only synthetic input carries these.
    $gestureSteps = @($session.Steps | Where-Object { $_.Kind -eq 'doubleClick' -or $_.Kind -eq 'drag' -or $_.Kind -eq 'wheel' })
    foreach ($step in $gestureSteps) {
        if ($null -eq $step.LastReplay) { throw ('step ' + $step.StepId + ' was never replayed') }
        $line = $step.LastReplay.AttemptLine
        if ($line -notmatch 'notSupported') { throw ('step ' + $step.StepId + ' does not say that the pattern routes do not carry it: ' + $line) }
        if ($line -notmatch 'SendInput') { throw ('step ' + $step.StepId + ' does not name the route that carried it: ' + $line) }
    }

    Write-Output ('PASS test-gesture-e2e recordedKinds=doubleClick+drag+wheel replayed=doubleClick+' + $delta['doubleClick'] +
        '/drag+' + $delta['drag'] + '/wheel+' + $delta['wheel'] + ' dragTravel=' + $lastDrag.dx +
        ' steps=' + $report.Succeeded + '/' + $report.Attempted)
} finally {
    if ($null -ne $recorder) { $recorder.Dispose() }
    if ($null -ne $engine) { $engine.Dispose() }
    if ($null -ne $fixture -and -not $fixture.HasExited) { $fixture.Kill() }
    [AppStudio.Probe]::Shutdown()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
