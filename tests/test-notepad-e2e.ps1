$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Record a procedure in Windows' own Notepad and carry it out again.
#
# The pass condition is the text in Notepad, read out of Notepad. Not that a
# replay was called, not that a step said "done" - the document has to actually
# say what the recorded procedure would have made it say.
#
# Notepad is the case that used to be lost: it opens with the keyboard already
# in the document, so a recording that only watches for clicks records nothing
# at all of what was typed.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-notepad-e2e (moves the real pointer and types real keys; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-notepad-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

if ($null -eq ('PuiNotepad.Input' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiNotepad
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
        private const uint KEYUP = 0x0002;

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(120);
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].data.mouse.flags = LEFTDOWN;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(70);
            inputs[0].data.mouse.flags = LEFTUP;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(280);
        }

        private static void Key(ushort virtualKey, bool up)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].data.key.vk = virtualKey;
            if (up) inputs[0].data.key.flags = KEYUP;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        // A key at a time, the way a keyboard produces them. Unicode injection
        // carries no virtual key, and a product that never hooks the keyboard
        // cannot see input that has no key in it.
        public static void TypeKeys(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                ushort virtualKey;
                bool shift = false;
                if (character >= 'a' && character <= 'z') virtualKey = (ushort)(0x41 + (character - 'a'));
                else if (character >= 'A' && character <= 'Z') { virtualKey = (ushort)(0x41 + (character - 'A')); shift = true; }
                else if (character >= '0' && character <= '9') virtualKey = (ushort)(0x30 + (character - '0'));
                else if (character == '-') virtualKey = 0xBD;
                else if (character == ' ') virtualKey = 0x20;
                else throw new ArgumentException("this helper types letters, digits, hyphen and space only");
                if (shift) Key(0x10, false);
                Key(virtualKey, false);
                Thread.Sleep(30);
                Key(virtualKey, true);
                if (shift) Key(0x10, true);
                Thread.Sleep(45);
            }
            Thread.Sleep(300);
        }

        public static void Press(ushort virtualKey)
        {
            Key(virtualKey, false);
            Thread.Sleep(90);
            Key(virtualKey, true);
            Thread.Sleep(350);
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
            Thread.Sleep(350);
        }
    }
}
'@
}

# What Notepad's document holds, read out of Notepad itself.
function Read-Document($processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
        $documents = $top.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
        foreach ($document in $documents) {
            $pattern = $null
            try { $pattern = $document.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) } catch { }
            if ($null -ne $pattern) { return $pattern.Current.Value }
            $textPattern = $null
            try { $textPattern = $document.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern) } catch { }
            if ($null -ne $textPattern) { return $textPattern.DocumentRange.GetText(-1) }
        }
    }
    return $null
}
# notepad.exe is a stub that hands the document to another process, so the
# window this test may touch is found by the file name it was asked to open -
# a name nothing else on the machine can have. Anything else that happens to be
# a Notepad window belongs to whoever is using this computer and is never
# touched.
function Notepad-Window($marker) {
    foreach ($window in [AppStudio.WindowTools]::ListStackOrder(@(), 0)) {
        if ($window.ClassName -eq 'Notepad' -and $window.Title -and $window.Title.Contains($marker)) { return $window }
    }
    return $null
}

$stub = $null
$notepadPid = 0
$recorder = $null
$engine = $null
$document = Join-Path $temp ('appstudio-e2e-' + [Guid]::NewGuid().ToString('N') + '.txt')
$marker = [IO.Path]::GetFileName($document)
try {
    [IO.File]::WriteAllText($document, '')
    $stub = Start-Process 'notepad.exe' -ArgumentList $document -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    $window = $null
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        Start-Sleep -Milliseconds 400
        $window = Notepad-Window $marker
    }
    if ($null -eq $window) { throw 'Notepad did not open the document this test created.' }
    $notepadPid = $window.ProcessId
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    [AppStudio.WindowTools]::Move([IntPtr][int64]$window.Hwnd, ($work.Left + 40), ($work.Top + 40), 900, 640) | Out-Null
    Start-Sleep -Milliseconds 600
    [AppStudio.ScreenCapture]::Raise([int64]$window.Hwnd)
    Start-Sleep -Milliseconds 800
    $window = Notepad-Window $marker
    # Notepad puts the keyboard in the document itself. Nothing here clicks on
    # it: that is exactly the case being tested.
    if ([AppStudio.WindowTools]::FocusedControl() -eq 0) { throw 'Notepad has no focused control.' }
    $before = Read-Document $notepadPid
    if ($null -eq $before) { throw 'Notepad exposes no document to read.' }

    # ===== 1. record ========================================================
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'notepad e2e')
    $session.ValuePolicy = 'recordText'
    $recorder = New-Object AppStudio.Recorder($root, $session, $null, $null)
    $recorder.Start()
    Start-Sleep -Milliseconds 2500

    # typed into the document that already had the keyboard
    [PuiNotepad.Input]::TypeKeys('alpha one')
    Start-Sleep -Milliseconds 900
    # a shortcut that acts on what was typed, then more typing after it
    [PuiNotepad.Input]::Chord(0x11, 0x41)
    Start-Sleep -Milliseconds 500
    [PuiNotepad.Input]::TypeKeys('beta two')
    Start-Sleep -Milliseconds 900
    # an intentional wait, then a click back into the document
    Start-Sleep -Milliseconds 1500
    $documentRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64](Notepad-Window $marker).Hwnd)
    [PuiNotepad.Input]::Click(($documentRect.X + 300), ($documentRect.Y + 300))
    Start-Sleep -Milliseconds 700
    [PuiNotepad.Input]::Chord(0x11, 0x41)
    Start-Sleep -Milliseconds 400
    [PuiNotepad.Input]::TypeKeys('gamma three')
    Start-Sleep -Milliseconds 1200

    $recorder.Stop()
    $recorder = $null
    [AppStudio.SessionStore]::WriteMeta($session)

    $recorded = Read-Document $notepadPid
    if ($recorded -notmatch 'gamma three') { throw ('Notepad does not hold what was typed: ' + $recorded) }

    $steps = @($session.Steps)
    $typed = @($steps | Where-Object { $_.Kind -eq 'textInput' })
    if ($typed.Count -lt 2) { throw ('only ' + $typed.Count + ' text entries were recorded from ' + $steps.Count + ' steps') }
    $values = @($typed | ForEach-Object { $_.Value })
    if (($values -join '|') -notmatch 'alpha one') { throw ('the text typed before any click was not recorded: ' + ($values -join '|')) }
    if (($values -join '|') -notmatch 'gamma three') { throw ('the last text entry was not recorded: ' + ($values -join '|')) }
    $chords = @($steps | Where-Object { $_.Kind -eq 'keyChord' -and $_.KeyChord -eq 'Ctrl+A' })
    if ($chords.Count -lt 2) { throw ('the shortcuts were not recorded: ' + $chords.Count) }
    if (@($steps | Where-Object { $_.GapMs -ge 1000 }).Count -lt 1) { throw 'the intentional wait was not recorded as an interval' }
    if ($session.InputEvents.Count -lt 10) { throw ('the timeline is too short to be a record of this: ' + $session.InputEvents.Count) }

    # ===== 2. replay, and read Notepad afterwards ===========================
    # The document is emptied first, so what is in it at the end came from the
    # replay and from nothing else.
    [AppStudio.ScreenCapture]::Raise([int64](Notepad-Window $marker).Hwnd)
    Start-Sleep -Milliseconds 600
    [PuiNotepad.Input]::Chord(0x11, 0x41)
    [PuiNotepad.Input]::Press(0x2E)
    Start-Sleep -Milliseconds 600
    $emptied = Read-Document $notepadPid
    if ($emptied -match 'gamma three') { throw 'the document could not be emptied before the replay' }

    $engine = New-Object AppStudio.ReplayEngine($root, $session)
    $report = $engine.Run('auto', $true)
    $engine.Dispose()
    $engine = $null
    Start-Sleep -Milliseconds 900

    $after = Read-Document $notepadPid
    if ($null -eq $after) { throw 'Notepad could not be read after the replay' }
    # The whole point: the real application changed, and it changed into what
    # the recording said.
    if ($after -notmatch 'gamma three') {
        $trail = @()
        foreach ($step in $session.Steps) {
            if ($null -ne $step.LastReplay) { $trail += ($step.StepId + '=' + $step.LastReplay.State + ':' + $step.LastReplay.Reason) }
        }
        throw ('the replay did not put the recorded text into Notepad. document=[' + $after + '] trail=' + ($trail -join ' | '))
    }
    if ($report.Succeeded -lt 1) { throw 'no step reported that it was carried out' }

    # Every step that ran says what it waited and what route carried it.
    $ran = @($session.Steps | Where-Object { $null -ne $_.LastReplay })
    if ($ran.Count -lt 1) { throw 'the replay left no record on the steps' }
    foreach ($step in $ran) {
        if ([string]::IsNullOrEmpty($step.LastReplay.State)) { throw ('step ' + $step.StepId + ' has no replay state') }
        if ($step.LastReplay.Attempts.Count -lt 1) { throw ('step ' + $step.StepId + ' left no route trail') }
    }
    $paced = @($ran | Where-Object { $_.LastReplay.WaitedMs -gt 0 })
    if ($paced.Count -lt 1) { throw 'the recorded intervals were not honoured on replay' }

    # ===== 3. the outputs read the same as the window =======================
    $outputs = [AppStudio.Outputs]::WriteAll($session, 1048576)
    if (-not $outputs.Report.Written) { throw ('report.html was not written: ' + $outputs.Report.Problem) }
    $verdict = [AppStudio.SessionVerdict]::Of($session)
    $html = [IO.File]::ReadAllText($session.ReportPath)
    if (-not $html.Contains([System.Net.WebUtility]::HtmlEncode($verdict.Headline))) { throw 'the report does not carry the same conclusion as the window' }

    Write-Output ('PASS test-notepad-e2e steps=' + $steps.Count + ' events=' + $session.InputEvents.Count +
        ' textEntries=' + $typed.Count + ' preFocusedText=1 chords=' + $chords.Count +
        ' replayDone=' + $report.Succeeded + '/' + $report.Attempted + ' documentAfterReplay=matched')
} finally {
    if ($null -ne $recorder) { $recorder.Dispose() }
    if ($null -ne $engine) { $engine.Dispose() }
    # Only the process that owns the document this test created is stopped.
    if ($notepadPid -ne 0) {
        try { (Get-Process -Id $notepadPid -ErrorAction Stop).Kill() } catch { }
    }
    if ($null -ne $stub -and -not $stub.HasExited) { try { $stub.Kill() } catch { } }
    [AppStudio.Probe]::Shutdown()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
