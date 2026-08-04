$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The recorder against a real window, with real input, for the kinds of thing a
# person actually does: press, press twice, drag, turn the wheel, type into a
# field nobody clicked on first, move with Tab and use a shortcut.
#
# Every one of those has to appear in the timeline and, where it means
# something, as a step. This is the test that fails when the recording drops
# what the operator did.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-input-timeline (moves the real pointer and types real keys; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-timeline-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

if ($null -eq ('PuiTimeline.Input' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiTimeline
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
        private const uint INPUT_KEYBOARD = 1;
        private const uint LEFTDOWN = 0x0002;
        private const uint LEFTUP = 0x0004;
        private const uint WHEEL = 0x0800;
        private const uint KEYUP = 0x0002;
        private const uint UNICODE = 0x0004;

        private static bool Mouse(uint flags, uint data)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].data.mouse.flags = flags;
            inputs[0].data.mouse.mouseData = data;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(90);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(70);
            Mouse(LEFTUP, 0);
            Thread.Sleep(220);
        }

        public static void DoubleClick(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(90);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(45);
            Mouse(LEFTUP, 0);
            Thread.Sleep(70);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(45);
            Mouse(LEFTUP, 0);
            Thread.Sleep(300);
        }

        public static void Drag(int fromX, int fromY, int toX, int toY)
        {
            SetCursorPos(fromX, fromY);
            Thread.Sleep(90);
            Mouse(LEFTDOWN, 0);
            Thread.Sleep(90);
            for (int index = 1; index <= 10; index++)
            {
                SetCursorPos(fromX + (toX - fromX) * index / 10, fromY + (toY - fromY) * index / 10);
                Thread.Sleep(30);
            }
            Thread.Sleep(90);
            Mouse(LEFTUP, 0);
            Thread.Sleep(280);
        }

        public static void Wheel(int x, int y, int notches)
        {
            SetCursorPos(x, y);
            Thread.Sleep(90);
            for (int index = 0; index < Math.Abs(notches); index++)
            {
                Mouse(WHEEL, unchecked((uint)(notches < 0 ? -120 : 120)));
                Thread.Sleep(60);
            }
            Thread.Sleep(280);
        }

        // Typed the way a keyboard types: a virtual key goes down and comes up.
        // Unicode injection carries no virtual key at all, so a product that
        // watches key state - as this one does, without ever recording which
        // key it was - cannot see it, and neither could a person's screen
        // reader. Only lower case letters, digits and the hyphen are needed
        // here.
        public static void TypeKeys(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                ushort virtualKey;
                if (character >= 'a' && character <= 'z') virtualKey = (ushort)(0x41 + (character - 'a'));
                else if (character >= '0' && character <= '9') virtualKey = (ushort)(0x30 + (character - '0'));
                else if (character == '-') virtualKey = 0xBD;
                else if (character == ' ') virtualKey = 0x20;
                else throw new ArgumentException("this helper types only lower case letters, digits, hyphen and space");
                Key(virtualKey, false);
                Thread.Sleep(35);
                Key(virtualKey, true);
                Thread.Sleep(45);
            }
            Thread.Sleep(320);
        }

        public static void Type(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                INPUT[] inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].data.key.scan = text[index];
                inputs[0].data.key.flags = UNICODE;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].data.key.scan = text[index];
                inputs[1].data.key.flags = UNICODE | KEYUP;
                SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
                Thread.Sleep(45);
            }
            Thread.Sleep(260);
        }

        private static void Key(ushort virtualKey, bool up)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].data.key.vk = virtualKey;
            if (up) inputs[0].data.key.flags = KEYUP;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Press(ushort virtualKey)
        {
            Key(virtualKey, false);
            Thread.Sleep(90);
            Key(virtualKey, true);
            Thread.Sleep(320);
        }

        public static void Chord(ushort modifier, ushort key)
        {
            Key(modifier, false);
            Thread.Sleep(90);
            Key(key, false);
            Thread.Sleep(90);
            Key(key, true);
            Thread.Sleep(90);
            Key(modifier, true);
            Thread.Sleep(320);
        }
    }
}
'@
}

function Centre($rect) { return @(($rect.X + [int]($rect.Width / 2)), ($rect.Y + [int]($rect.Height / 2))) }
function Kinds($events, $kind) { return @($events | Where-Object { $_.Kind -eq $kind }) }

$forms = $null
$recorder = $null
try {
    $ready = Join-Path $temp 'forms.json'
    $forms = Start-Process $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ((-not (Test-Path $ready)) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 60 }
    if (-not (Test-Path $ready)) { throw 'FixtureWinForms did not become ready.' }
    $f = Get-Content $ready -Raw | ConvertFrom-Json
    $window = [int64]$f.window
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$window)
    [AppStudio.WindowTools]::Move([IntPtr]$window, ($work.Left + 30), ($work.Top + 30), $rect.Width, $rect.Height) | Out-Null
    Start-Sleep -Milliseconds 400
    [AppStudio.ScreenCapture]::Raise($window)
    Start-Sleep -Milliseconds 500

    # The rectangles in the ready file are from where the window first opened;
    # it has been moved since, so every position is read from the control's own
    # handle now.
    $normal = Centre ([AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.normal))
    $first = Centre ([AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.first))
    $list = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.list)
    # The parentheses matter: the comma binds tighter than the plus.
    $listTop = @(($list.X + 40), ($list.Y + 12))
    $listLower = @(($list.X + 40), ($list.Y + 70))

    # The keyboard is put in the customer code field before the recording
    # starts, and nothing clicks on it afterwards. This is the case a person
    # hits every day: the window opens with the cursor already in a field, they
    # start recording and type. Everything typed has to survive that.
    [PuiTimeline.Input]::Click($normal[0], $normal[1])
    Start-Sleep -Milliseconds 500
    if ([AppStudio.WindowTools]::FocusedControl() -ne [int64]$f.normal) { throw 'the fixture field did not take the keyboard before the recording started' }

    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'timeline fixture')
    $recorder = New-Object AppStudio.Recorder($root, $session, $null, $null)
    $recorder.Start()
    Start-Sleep -Milliseconds 2500

    # 1. typing into a field that was already focused
    [PuiTimeline.Input]::Chord(0x11, 0x41)
    [PuiTimeline.Input]::TypeKeys('already-focused')
    Start-Sleep -Milliseconds 700

    # 2. an ordinary click on a button
    [PuiTimeline.Input]::Click($first[0], $first[1])
    Start-Sleep -Milliseconds 700

    # 3. a double click in the list
    [PuiTimeline.Input]::DoubleClick($listTop[0], $listTop[1])
    Start-Sleep -Milliseconds 700

    # 4. a drag inside the list
    [PuiTimeline.Input]::Drag($listTop[0], $listTop[1], $listLower[0], $listLower[1])
    Start-Sleep -Milliseconds 700

    # 5. the wheel over the list
    [PuiTimeline.Input]::Wheel($listLower[0], $listLower[1], -3)
    Start-Sleep -Milliseconds 700

    # 6. Tab to move the keyboard, then a shortcut
    [PuiTimeline.Input]::Click($normal[0], $normal[1])
    Start-Sleep -Milliseconds 500
    [PuiTimeline.Input]::Press(0x09)
    [PuiTimeline.Input]::Chord(0x11, 0x41)
    Start-Sleep -Milliseconds 900

    $recorder.Stop()
    $recorder = $null
    [AppStudio.SessionStore]::WriteMeta($session)

    # --- what the timeline has to contain ---------------------------------
    $events = @($session.InputEvents)
    if ($events.Count -lt 20) { throw ('the timeline has only ' + $events.Count + ' events') }
    if ($session.InputWatchState -ne 'hook') { throw ('the pointer was not watched at the event level: ' + $session.InputWatchState) }
    foreach ($needed in @('mouseDown', 'mouseUp', 'click', 'doubleClick', 'drag', 'wheel', 'keyDown', 'keyUp', 'typing', 'focus')) {
        if ((Kinds $events $needed).Count -lt 1) { throw ('the timeline has no ' + $needed + ' event') }
    }
    for ($index = 1; $index -lt $events.Count; $index++) {
        if ($events[$index].OffsetMs -lt $events[$index - 1].OffsetMs) { throw 'the timeline is out of order' }
        if ($events[$index].GapMs -lt 0) { throw 'a negative interval reached the timeline' }
    }
    $wheelEvents = Kinds $events 'wheel'
    foreach ($item in $wheelEvents) { if ($item.WheelDelta -eq 0) { throw 'a wheel event carries no amount' } }
    $dragEvents = Kinds $events 'drag'
    foreach ($item in $dragEvents) {
        if ($item.ToX -eq 0 -and $item.ToY -eq 0) { throw 'a drag event has no release point' }
        if ([Math]::Abs($item.ToY - $item.Y) -lt 20) { throw 'the drag release point is not where the pointer was let go' }
    }
    foreach ($item in $events) {
        if ($item.Kind -eq 'keyDown' -or $item.Kind -eq 'keyUp') { continue }
        if ($item.X -eq 0 -and $item.Y -eq 0) { continue }
        if ($item.Dpi -le 0) { throw ('a pointer event has no DPI: ' + $item.Kind) }
        if ([string]::IsNullOrEmpty($item.MonitorId)) { throw ('a pointer event has no monitor: ' + $item.Kind) }
    }
    # No key that was not asked for reaches the timeline.
    foreach ($item in $events) {
        if ($item.Kind -ne 'keyDown' -and $item.Kind -ne 'keyUp') { continue }
        if ([string]::IsNullOrEmpty($item.Key)) { throw 'a key event has no name' }
        if ($item.Key -notmatch '^(Ctrl\+|Alt\+|Shift\+|Win\+)*(Tab|Enter|Escape|A|F[0-9]+)$') { throw ('an unexpected key reached the timeline: ' + $item.Key) }
    }

    # --- and what the steps have to contain -------------------------------
    $steps = @($session.Steps)
    $byKind = @{}
    foreach ($step in $steps) { $byKind[$step.Kind] = 1 + [int]$byKind[$step.Kind] }
    foreach ($needed in @('click', 'doubleClick', 'drag', 'wheel', 'keyChord', 'textInput')) {
        if (-not $byKind.ContainsKey($needed)) { throw ('no ' + $needed + ' step was recorded: ' + (($byKind.Keys | Sort-Object) -join ',')) }
    }
    $typed = @($steps | Where-Object { $_.Kind -eq 'textInput' -and $_.Value -eq 'already-focused' })
    if ($typed.Count -lt 1) { throw 'text typed into the field that was already focused was not recorded' }
    $dragStep = @($steps | Where-Object { $_.Kind -eq 'drag' })[0]
    if ($null -eq $dragStep.ToPoint) { throw 'the drag step has no release point' }
    if ($dragStep.DropLocators.Count -lt 1) { throw 'the drag step does not say where it was released' }
    $wheelStep = @($steps | Where-Object { $_.Kind -eq 'wheel' })[0]
    if ($wheelStep.WheelDelta -eq 0) { throw 'the wheel step recorded no amount' }
    $doubleStep = @($steps | Where-Object { $_.Kind -eq 'doubleClick' })[0]
    if ($doubleStep.Point.X -eq 0) { throw 'the double click step has no position' }
    $chords = @($steps | Where-Object { $_.Kind -eq 'keyChord' })
    if (@($chords | Where-Object { $_.KeyChord -eq 'Tab' }).Count -lt 1) { throw 'Tab was not recorded' }
    if (@($chords | Where-Object { $_.KeyChord -eq 'Ctrl+A' }).Count -lt 1) { throw 'Ctrl+A was not recorded' }
    if (@($chords | Where-Object { $_.HoldMs -gt 0 }).Count -lt 1) { throw 'no key recorded how long it was held' }
    if (@($steps | Where-Object { $_.GapMs -gt 0 }).Count -lt 3) { throw 'the intervals between steps were not recorded' }
    foreach ($step in $steps) {
        if ($step.Kind -eq 'appSwitch' -or $step.Kind -eq 'keyChord' -or $step.Kind -eq 'textInput' -or $step.Kind -eq 'secretInput') { continue }
        if ($step.Dpi -le 0) { throw ('a pointer step has no DPI: ' + $step.Kind) }
    }
    # Every step that a pointer event produced points back at the event.
    $linked = @($events | Where-Object { -not [string]::IsNullOrEmpty($_.StepId) })
    if ($linked.Count -lt 4) { throw 'the timeline does not say which events became steps' }

    # The same is on disk, not only in memory.
    $reloaded = [AppStudio.SessionStore]::Load($session.Folder)
    if ($reloaded.InputEvents.Count -ne $events.Count) { throw ('the timeline on disk has ' + $reloaded.InputEvents.Count + ' of ' + $events.Count + ' events') }
    $reloadedDrag = @($reloaded.Steps | Where-Object { $_.Kind -eq 'drag' })[0]
    if ($null -eq $reloadedDrag -or $null -eq $reloadedDrag.ToPoint) { throw 'the drag did not survive a reload' }
    if ($reloadedDrag.DropLocators.Count -lt 1) { throw 'the release point did not survive a reload' }

    Write-Output ('PASS test-input-timeline watch=' + $session.InputWatchState + ' events=' + $events.Count +
        ' steps=' + $steps.Count + ' kinds=' + (($byKind.Keys | Sort-Object) -join '+') +
        ' preFocusedText=1 drag=' + $dragEvents.Count + ' wheel=' + $wheelEvents.Count + ' reloaded=' + $reloaded.InputEvents.Count)
} finally {
    if ($null -ne $recorder) { $recorder.Dispose() }
    if ($null -ne $forms -and -not $forms.HasExited) { $forms.Kill() }
    [AppStudio.Probe]::Shutdown()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
