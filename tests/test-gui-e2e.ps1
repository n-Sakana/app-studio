$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The whole product against two real applications, with the physical pointer and
# real keystrokes: choose a window and acquire it, record a procedure that
# crosses from one application to the other, then play that recording back.
#
# Only windows this test started itself are ever pointed at or driven. Before
# anything is replayed the test proves that exactly one window matches each
# recorded description, and refuses to go on if a stranger's window could be hit
# instead.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-gui-e2e (moves the real pointer and types real keys; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-e2e-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$sessionRoot = Join-Path $root 'runtime\sessions'
$before = @{}
if (Test-Path -LiteralPath $sessionRoot) { Get-ChildItem -LiteralPath $sessionRoot -Directory | ForEach-Object { $before[$_.Name] = $true } }

if ($null -eq ('PuiTest.RealInput' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiTest
{
    public static class RealInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr GetMessageExtraInfo();
        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        // A pointer that jumps has no path, and a chooser that follows a path
        // has nothing to follow. These are the steps a hand would produce.
        public static void Glide(int fromX, int fromY, int toX, int toY, int steps, int pauseMs)
        {
            for (int index = 1; index <= steps; index++)
            {
                int x = fromX + (int)((toX - fromX) * (index / (double)steps));
                int y = fromY + (int)((toY - fromY) * (index / (double)steps));
                SetCursorPos(x, y);
                Thread.Sleep(pauseMs);
            }
        }

        public static bool Click(int x, int y)
        {
            if (!SetCursorPos(x, y)) return false;
            Thread.Sleep(120);
            INPUT[] down = new INPUT[1];
            down[0].type = INPUT_MOUSE;
            down[0].data.mouse.flags = MOUSEEVENTF_LEFTDOWN;
            down[0].data.mouse.extra = GetMessageExtraInfo();
            if (SendInput(1, down, Marshal.SizeOf(typeof(INPUT))) != 1) return false;
            Thread.Sleep(140);
            INPUT[] up = new INPUT[1];
            up[0].type = INPUT_MOUSE;
            up[0].data.mouse.flags = MOUSEEVENTF_LEFTUP;
            up[0].data.mouse.extra = GetMessageExtraInfo();
            return SendInput(1, up, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static bool Type(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                INPUT[] inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].data.key.scan = text[index];
                inputs[0].data.key.flags = KEYEVENTF_UNICODE;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].data.key.scan = text[index];
                inputs[1].data.key.flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                if (SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) != 2) return false;
                Thread.Sleep(25);
            }
            return true;
        }

        // A hand holds the modifier down while it presses the key and lets go
        // afterwards. Sending all four events in one batch is not something a
        // person can produce, and testing against it would be testing a machine.
        public static bool Chord(ushort modifier, ushort key)
        {
            if (!Send(modifier, false)) return false;
            Thread.Sleep(120);
            if (!Send(key, false)) return false;
            Thread.Sleep(140);
            if (!Send(key, true)) return false;
            Thread.Sleep(120);
            if (!Send(modifier, true)) return false;
            Thread.Sleep(250);
            return true;
        }

        static bool Send(ushort virtualKey, bool up)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].data.key.vk = virtualKey;
            if (up) inputs[0].data.key.flags = KEYEVENTF_KEYUP;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }
    }
}
'@
}

function All-Of($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($scope, $controlType, $label) {
    foreach ($item in All-Of $scope $controlType) {
        if ($item.Current.Name -eq $label -and -not $item.Current.IsOffscreen) { return $item }
    }
    return $null
}
function App-Root($processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
}
function Wait-Product($processId, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        foreach ($top in App-Root $processId) {
            $found = Find-Named $top ([System.Windows.Automation.ControlType]::Button) $label
            if ($null -ne $found) { return $found }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Press($element, $what) {
    if ($null -eq $element) { throw ('control not found: ' + $what) }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
}
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Centre($rect) { return @(($rect.X + [int]($rect.Width / 2)), ($rect.Y + [int]($rect.Height / 2))) }
# The fixtures are not per-monitor DPI aware, so the system places their child
# controls by scaling the sizes the application itself asked for. Changing a
# fixture window's size from here would move its children out from under it, so
# only the position is ever changed and the size is left exactly as it is.
function Place-Window($hwnd, $x, $y) {
    $rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hwnd)
    if ($null -eq $rect) { throw 'a fixture window has no rectangle' }
    [AppStudio.WindowTools]::Move([IntPtr]$hwnd, $x, $y, $rect.Width, $rect.Height) | Out-Null
    Start-Sleep -Milliseconds 300
    return [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hwnd)
}
function Raise-Window($hwnd) {
    [AppStudio.ScreenCapture]::Raise($hwnd)
    Start-Sleep -Milliseconds 450
}
function Safe-Click($x, $y, $expectedProcessId, $hwnd, $what) {
    # Nothing is ever pressed without proving first that the point belongs to a
    # window this test started. If something else is on top, the intended window
    # is raised once and the ownership is checked again; it is never forced.
    $owner = [AppStudio.WindowTools]::ProcessIdAt($x, $y)
    if ($owner -ne $expectedProcessId) {
        Raise-Window $hwnd
        $owner = [AppStudio.WindowTools]::ProcessIdAt($x, $y)
    }
    if ($owner -ne $expectedProcessId) { throw ($what + ': the point at ' + $x + ',' + $y + ' belongs to process ' + $owner + ', not ' + $expectedProcessId) }
    if (-not [PuiTest.RealInput]::Click($x, $y)) { throw ($what + ': the click could not be sent') }
    Start-Sleep -Milliseconds 600
}
function New-Sessions() {
    $found = @()
    if (Test-Path -LiteralPath $sessionRoot) {
        foreach ($folder in Get-ChildItem -LiteralPath $sessionRoot -Directory) {
            if (-not $before.ContainsKey($folder.Name)) { $found += $folder.FullName }
        }
    }
    return @($found | Sort-Object)
}

$forms = $null
$win32 = $null
$app = $null
try {
    # --- two real applications, both started here ---------------------------
    $formsReady = Join-Path $temp 'forms.json'
    $forms = Start-Process $build.FixtureWinForms -ArgumentList @('--ready', $formsReady) -PassThru
    $win32Ready = Join-Path $temp 'win32.json'
    $win32 = Start-Process $build.FixtureWin32 -ArgumentList @('--mode', 'healthy', '--ready', $win32Ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(15)
    while (((-not (Test-Path $formsReady)) -or (-not (Test-Path $win32Ready))) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 40 }
    if (-not (Test-Path $formsReady)) { throw 'FixtureWinForms did not become ready.' }
    if (-not (Test-Path $win32Ready)) { throw 'FixtureWin32 did not become ready.' }
    $f = Get-Content $formsReady -Raw | ConvertFrom-Json
    $w = Get-Content $win32Ready -Raw | ConvertFrom-Json
    $formsWindow = [int64]$f.window
    $win32Window = [int64]$w.window
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $formsRect = Place-Window $formsWindow ($work.Left + 20) ($work.Top + 20)
    $sideX = $formsRect.X + $formsRect.Width + 30
    $win32Rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$win32Window)
    if (($sideX + $win32Rect.Width) -le $work.Right) {
        $win32Rect = Place-Window $win32Window $sideX ($work.Top + 20)
    } else {
        $win32Rect = Place-Window $win32Window ($work.Left + 20) ($formsRect.Y + $formsRect.Height + 20)
    }
    foreach ($pair in @(@('FixtureWinForms', $formsRect), @('FixtureWin32', $win32Rect))) {
        if ($pair[1].X -lt $work.Left -or ($pair[1].X + $pair[1].Width) -gt ($work.Right + 4) -or ($pair[1].Y + $pair[1].Height) -gt ($work.Bottom + 4)) {
            throw ($pair[0] + ' does not fit on this display: ' + $pair[1].X + ',' + $pair[1].Y + ' ' + $pair[1].Width + 'x' + $pair[1].Height)
        }
    }

    # --- the product ---------------------------------------------------------
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','600000') -PassThru -WindowStyle Hidden
    $snapButton = Wait-Product $app.Id (Message 'home-snap.txt' 'Snap') 60000
    if ($null -eq $snapButton) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1200

    # ===== 1. snap ==========================================================
    Press $snapButton 'snap'
    Start-Sleep -Milliseconds 1200
    $formsCentre = Centre $formsRect
    [PuiTest.RealInput]::Glide(($formsRect.X + 20), ($formsRect.Y + 10), $formsCentre[0], $formsCentre[1], 14, 45)
    Start-Sleep -Milliseconds 700
    if (-not [PuiTest.RealInput]::Click($formsCentre[0], $formsCentre[1])) { throw 'the chooser click could not be sent' }
    Start-Sleep -Milliseconds 1000
    $limit = [DateTime]::UtcNow.AddSeconds(150)
    $snapSession = $null
    while ([DateTime]::UtcNow -lt $limit) {
        $created = New-Sessions
        foreach ($folder in $created) {
            if (Test-Path (Join-Path $folder 'out\ai\session.md')) { $snapSession = $folder; break }
        }
        if ($null -ne $snapSession) { break }
        Start-Sleep -Milliseconds 1000
    }
    if ($null -eq $snapSession) { throw 'The snap produced no session with outputs.' }
    foreach ($name in @('out\report.html', 'out\ai\session.md', 'out\ai\screens.pdf')) {
        if (-not (Test-Path (Join-Path $snapSession $name))) { throw ('The snap did not produce ' + $name) }
    }
    $aiCount = @(Get-ChildItem (Join-Path $snapSession 'out\ai') -File).Count
    if ($aiCount -ne 2) { throw ('The assistant folder holds ' + $aiCount + ' files instead of 2.') }
    $snapMd = [IO.File]::ReadAllText((Join-Path $snapSession 'out\ai\session.md'))
    if ($snapMd -notmatch 'FixtureWinForms') { throw 'The snap did not acquire the window that was pointed at.' }
    if ($snapMd -notmatch 'CustomerCode') { throw 'The snap did not obtain the parts of that window.' }
    $snapLoaded = [AppStudio.SessionStore]::Load($snapSession)
    if ($snapLoaded.Elements.Count -lt 5) { throw ('The snap obtained only ' + $snapLoaded.Elements.Count + ' elements.') }

    # The chooser and this product's own windows must not be in the evidence.
    foreach ($node in $snapLoaded.Elements) {
        if ($node.ProcessId -eq $app.Id) { throw 'A window of App Studio itself was acquired as a target.' }
    }
    foreach ($appRef in $snapLoaded.Apps) {
        if ($appRef.ProcessId -eq $app.Id) { throw 'App Studio itself was recorded as a target application.' }
    }
    # The picture is of the target, not of a dimmed desktop with a frame on it.
    $snapShot = @(Get-ChildItem (Join-Path $snapSession 'shots') -Filter '*.png')
    if ($snapShot.Count -lt 1) { throw 'The snap took no picture.' }
    $bitmap = New-Object Drawing.Bitmap($snapShot[0].FullName)
    try {
        if ($bitmap.Width -ne $formsRect.Width -or $bitmap.Height -ne $formsRect.Height) {
            throw ('The picture is ' + $bitmap.Width + 'x' + $bitmap.Height + ' but the window is ' + $formsRect.Width + 'x' + $formsRect.Height + '.')
        }
        # A dimming overlay left on screen would darken every pixel; the fixture
        # form is a light window, so a bright pixel proves it was not captured
        # through the chooser.
        $bright = 0
        for ($y = 4; $y -lt $bitmap.Height; $y += 17) {
            for ($x = 4; $x -lt $bitmap.Width; $x += 17) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) { $bright++ }
            }
        }
        if ($bright -lt 10) { throw ('The picture looks dimmed: only ' + $bright + ' bright samples, so the chooser overlay was probably captured.') }
    } finally { $bitmap.Dispose() }

    # ===== 2. record across two applications ================================
    $recordButton = Wait-Product $app.Id (Message 'home-record.txt' 'Record') 30000
    Press $recordButton 'record'
    Start-Sleep -Milliseconds 4500

    $normalRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.normal)
    $normalCentre = Centre $normalRect
    Safe-Click $normalCentre[0] $normalCentre[1] $forms.Id $formsWindow 'the customer code field'
    [PuiTest.RealInput]::Chord(0x11, 0x41) | Out-Null
    [PuiTest.RealInput]::Type('e2e-typed-value') | Out-Null
    Start-Sleep -Milliseconds 700

    $passwordRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.password)
    $passwordCentre = Centre $passwordRect
    Safe-Click $passwordCentre[0] $passwordCentre[1] $forms.Id $formsWindow 'the password field'
    [PuiTest.RealInput]::Chord(0x11, 0x41) | Out-Null
    [PuiTest.RealInput]::Type('e2e-secret-9x4b') | Out-Null
    Start-Sleep -Milliseconds 700

    $saveRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.first)
    $saveCentre = Centre $saveRect
    Safe-Click $saveCentre[0] $saveCentre[1] $forms.Id $formsWindow 'the save button'

    # Cross into the other application.
    $registerRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$w.button)
    $registerCentre = Centre $registerRect
    Safe-Click $registerCentre[0] $registerCentre[1] $win32.Id $win32Window 'the register button of the second application'
    # Crossing into another application makes the recorder acquire that window,
    # which takes seconds. Stopping during it would cut the recording off in the
    # middle of a step rather than at the end of one.
    Start-Sleep -Seconds 12

    # Stop through the control the product puts on screen while recording.
    $stopLabel = Message 'hud-stop.txt' 'Stop'
    $stop = Wait-Product $app.Id $stopLabel 15000
    if ($null -eq $stop) { throw 'The recording control was not on screen.' }
    Press $stop 'stop'
    Start-Sleep -Milliseconds 1500

    $limit = [DateTime]::UtcNow.AddSeconds(180)
    $recordSession = $null
    while ([DateTime]::UtcNow -lt $limit) {
        foreach ($folder in @(New-Sessions)) {
            if ($folder -eq $snapSession) { continue }
            if (Test-Path (Join-Path $folder (Join-Path 'out' (Join-Path 'ai' 'session.md')))) { $recordSession = $folder; break }
        }
        if ($null -ne $recordSession) { break }
        Start-Sleep -Milliseconds 1000
    }
    if ($null -eq $recordSession) {
        $seen = @()
        foreach ($folder in @(New-Sessions)) { $seen += ((Split-Path $folder -Leaf) + '=' + (Test-Path (Join-Path $folder (Join-Path 'out' (Join-Path 'ai' 'session.md'))))) }
        throw ('The recording produced no session with outputs. snap=' + (Split-Path $snapSession -Leaf) + ' seen=' + ($seen -join ','))
    }
    $recorded = [AppStudio.SessionStore]::Load($recordSession)
    if ($recorded.Steps.Count -lt 4) { throw ('Only ' + $recorded.Steps.Count + ' steps were recorded.') }
    $kinds = @($recorded.Steps | ForEach-Object { $_.Kind })
    foreach ($needed in @('click', 'appSwitch', 'keyChord')) {
        if ($kinds -notcontains $needed) { throw ('The recording has no ' + $needed + ' step; kinds=' + ($kinds -join ',')) }
    }
    $apps = @($recorded.Apps | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
    if ($apps.Count -lt 2) { throw ('The recording stayed inside one application: ' + ($apps -join ',')) }
    foreach ($appRef in $recorded.Apps) {
        if ($appRef.ProcessId -eq $app.Id) { throw 'App Studio itself was recorded as a target application.' }
    }
    foreach ($step in $recorded.Steps) {
        if ($step.ProcessId -eq $app.Id) { throw 'A press on the recording control was recorded as a step.' }
    }
    # The typed text is recorded so the run can be repeated; the secret is not.
    $typed = @($recorded.Steps | Where-Object { $_.Kind -eq 'textInput' })
    $secrets = @($recorded.Steps | Where-Object { $_.Kind -eq 'secretInput' })
    if ($secrets.Count -lt 1) { throw 'Typing into a password box did not produce a secret step.' }
    # Where an ordinary field was recorded, the text has to be there, because
    # that is what makes the recording replayable at all.
    foreach ($step in $typed) {
        if ($step.ValueKind -eq 'text' -and -not $step.Value) { throw 'A text entry was recorded without the text.' }
        if ($step.ValueKind -eq 'unknown' -and $step.Unavailable.Count -lt 1) { throw 'An unreadable text entry did not say so.' }
    }
    foreach ($step in $secrets) {
        if ($step.Value) { throw 'A secret value was written down.' }
        if ($step.Unavailable.Count -lt 1) { throw 'The absence of the secret was not stated.' }
    }
    foreach ($file in Get-ChildItem $recordSession -File -Recurse | Where-Object { $_.Extension -ne '.png' -and $_.Extension -ne '.pdf' }) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text.Contains('e2e-secret-9x4b')) { throw ('The secret reached ' + $file.Name) }
        if ($text.Contains('P@ssword123')) { throw ('The fixture password reached ' + $file.Name) }
    }
    foreach ($name in @('out\report.html', 'out\ai\session.md', 'out\ai\screens.pdf')) {
        if (-not (Test-Path (Join-Path $recordSession $name))) { throw ('The recording did not produce ' + $name) }
    }
    # The frame and the recording control must never appear in a keyframe.
    foreach ($screen in $recorded.Screens.Screens) {
        if ($screen.ClassName -and $screen.ClassName.StartsWith('HwndWrapper')) { throw 'A window of this product became a screen of the recording.' }
    }
    # A keyframe is a picture of somebody's application, so a secret field has to
    # be blacked out in it exactly as in a deliberate acquisition.
    $maskedFrames = 0
    foreach ($screen in $recorded.Screens.Screens) {
        if (-not $screen.HasShot) { continue }
        if ([string]::IsNullOrEmpty($screen.Note)) { throw ('Keyframe ' + $screen.ScreenId + ' does not say whether anything was blacked out.') }
        if ($screen.Note -notmatch 'blacked out') { throw ('Keyframe ' + $screen.ScreenId + ' does not state its masking: ' + $screen.Note) }
        if ($screen.Note -match '\d+ secret field') { $maskedFrames++ }
    }
    if ($maskedFrames -lt 1) { throw 'No keyframe blacked out the password field of the recorded application.' }
    # And it really is black where the password box was.
    $passwordScreen = $null
    foreach ($screen in $recorded.Screens.Screens) {
        if ($screen.HasShot -and $screen.Note -match '\d+ secret field' -and $screen.ClassName -eq 'WindowsForms10.Window.8.app.0.34f5582_r6_ad1') { $passwordScreen = $screen; break }
    }
    if ($null -ne $passwordScreen) {
        $bitmap = New-Object Drawing.Bitmap($passwordScreen.ShotFile)
        try {
            $px = $passwordCentre[0] - $passwordScreen.Rect.X
            $py = $passwordCentre[1] - $passwordScreen.Rect.Y
            if ($px -ge 0 -and $py -ge 0 -and $px -lt $bitmap.Width -and $py -lt $bitmap.Height) {
                $pixel = $bitmap.GetPixel($px, $py)
                if ($pixel.R -ne 0 -or $pixel.G -ne 0 -or $pixel.B -ne 0) { throw ('The password box was not blacked out in keyframe ' + $passwordScreen.ScreenId + ': ' + $pixel.ToString()) }
            }
        } finally { $bitmap.Dispose() }
    }

    # ===== 3. replay ========================================================
    # Refuse to drive anything unless exactly one window matches each recorded
    # description, so a window somebody else opened can never be the target.
    # The same test the engine itself applies - application, window class and
    # title together - so this refuses exactly when the engine would be at risk
    # of picking a window somebody else opened.
    $stack = @([AppStudio.WindowTools]::ListStackOrder((New-Object 'long[]' 0), $app.Id))
    $ambiguous = @()
    foreach ($step in $recorded.Steps) {
        if (-not $step.WindowClass) { continue }
        $matching = @($stack | Where-Object { $_.ClassName -eq $step.WindowClass -and $_.ProcessName -eq $step.AppName -and $_.Title -eq $step.WindowTitle })
        if ($matching.Count -ne 1) { $ambiguous += ($step.AppName + '/' + $step.WindowTitle + '=' + $matching.Count) }
    }
    $replayNote = 'skipped'
    if ($ambiguous.Count -gt 0) {
        Write-Output ('NOTE test-gui-e2e replay was not run: more than one window matches a recorded description (' + (($ambiguous | Sort-Object -Unique) -join ',') + '). Nothing was driven.')
    } else {
        $writeLabel = Message 'settings-write.txt' 'Let replay act on the real application'
        $productWindow = $null
        foreach ($top in App-Root $app.Id) {
            if ($null -ne (Find-Named $top ([System.Windows.Automation.ControlType]::Button) (Message 'home-snap.txt' 'Snap'))) { $productWindow = $top }
        }
        if ($null -eq $productWindow) { throw 'The App Studio window disappeared before replay.' }
        $settingsFold = Find-Named $productWindow ([System.Windows.Automation.ControlType]::Group) (Message 'settings-fold.txt' 'Detailed settings')
        if ($null -eq $settingsFold) { throw 'The detailed settings fold is missing.' }
        $settingsFold.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 600
        $permission = Find-Named $productWindow ([System.Windows.Automation.ControlType]::CheckBox) $writeLabel
        if ($null -eq $permission) { throw 'The replay permission switch is missing.' }
        $permission.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 400
        $replayButton = Find-Named $productWindow ([System.Windows.Automation.ControlType]::Button) (Message 'detail-replay.txt' 'Replay')
        if ($null -eq $replayButton) { throw 'The replay button is missing for a recorded session.' }
        Press $replayButton 'replay'
        $limit = [DateTime]::UtcNow.AddSeconds(300)
        $replayLog = Join-Path $recordSession 'replay.jsonl'
        while ([DateTime]::UtcNow -lt $limit -and -not (Test-Path $replayLog)) { Start-Sleep -Milliseconds 1000 }
        if (-not (Test-Path $replayLog)) { throw 'The replay wrote nothing.' }
        # Every step re-acquires the window it is about to act on, which takes
        # seconds, so the wait is on the run's own end condition rather than on a
        # guessed pause: either every step has a result, or a step did not finish
        # and the run stopped there.
        $secretPromptSeen = $false
        while ([DateTime]::UtcNow -lt $limit) {
            $sofar = @([AppStudio.SessionLog]::ReadAllLines($replayLog))
            if ($sofar.Count -ge $recorded.Steps.Count) { break }
            if ($sofar.Count -gt 0) {
                $last = (ConvertFrom-Json $sofar[$sofar.Count - 1]).result.state
                if ($last -ne 'done') { break }
            }
            # A step that enters a secret has no value to replay, so the product
            # has to stop and ask. Answering "stop here" is the operator's other
            # choice, and the run must record that rather than type something.
            foreach ($top in App-Root $app.Id) {
                $cancel = Find-Named $top ([System.Windows.Automation.ControlType]::Button) (Message 'secret-cancel.txt' 'Stop here')
                if ($null -ne $cancel) {
                    $secretPromptSeen = $true
                    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    Start-Sleep -Milliseconds 800
                }
            }
            Start-Sleep -Milliseconds 1000
        }
        Start-Sleep -Milliseconds 2500
        if (@($recorded.Steps | Where-Object { $_.Kind -eq 'secretInput' }).Count -gt 0) {
            if (-not $secretPromptSeen) { throw 'A step that enters a secret was replayed without asking the operator for the value.' }
        }
        $lines = [AppStudio.SessionLog]::ReadAllLines($replayLog)
        if ($lines.Count -lt 1) { throw 'The replay record is empty.' }
        $routes = @()
        $states = @()
        foreach ($line in $lines) {
            $item = ConvertFrom-Json $line
            $states += $item.result.state
            foreach ($attempt in $item.result.attempts) { if ($attempt.method) { $routes += ($attempt.method + ':' + $attempt.outcome) } }
        }
        if ($routes.Count -lt 1) { throw 'The replay recorded no route attempt at all.' }
        # The secret step must be refused for want of a value, never carried out
        # with something invented.
        foreach ($line in $lines) {
            $item = ConvertFrom-Json $line
            $step = $recorded.Steps | Where-Object { $_.StepId -eq $item.stepId }
            if ($null -ne $step -and $step.Kind -eq 'secretInput' -and $item.result.state -eq 'done') {
                throw 'A secret step was carried out even though no value exists for it.'
            }
        }
        $replayNote = 'steps=' + $lines.Count + ' attempts=' + $routes.Count + ' firstState=' + $states[0] + ' done=' + @($states | Where-Object { $_ -eq 'done' }).Count + ' secretPrompt=' + $secretPromptSeen
        # At least one step has to have been carried out through a real route,
        # and the trail has to name the routes that were tried in order.
        $carried = @($routes | Where-Object { $_ -match ':(success|unknown)$' })
        if ($carried.Count -lt 1) { throw ('No step was carried out by any route: ' + ($routes -join ' / ')) }
        $ladder = @($routes | Where-Object { $_ -match '^uia\.' })
        if ($ladder.Count -lt 1) { throw 'The UI Automation route was never even attempted.' }
        # A recorded step must never be replayed twice from one recording.
        $replayedIds = @()
        foreach ($line in $lines) { $replayedIds += (ConvertFrom-Json $line).stepId }
        $duplicates = @($replayedIds | Group-Object | Where-Object { $_.Count -gt 1 })
        if ($duplicates.Count -gt 0) { throw ('A step was replayed more than once: ' + (($duplicates | ForEach-Object { $_.Name }) -join ',')) }
        # Every step is either carried out or refused with a reason; nothing is
        # left without an answer.
        foreach ($state in $states) {
            if ([string]::IsNullOrWhiteSpace($state)) { throw 'A replayed step has no result state.' }
        }
    }

    $reportBytes = (Get-Item (Join-Path $recordSession 'out\report.html')).Length
    Write-Output ('PASS test-gui-e2e snapElements=' + $snapLoaded.Elements.Count + ' snapPictureUndimmed=1 recordSteps=' + $recorded.Steps.Count +
        ' apps=' + ($apps -join '+') + ' textInput=' + $typed.Count + ' secretSteps=' + $secrets.Count + ' secretLeak=0 keyframesMasked=' + $maskedFrames + ' ownWindowsInEvidence=0 replay=' + $replayNote +
        ' reportBytes=' + $reportBytes)
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $app) { $app.Dispose() }
    foreach ($fixture in @($forms, $win32)) {
        if ($null -ne $fixture -and -not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
        if ($null -ne $fixture) { $fixture.Dispose() }
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
