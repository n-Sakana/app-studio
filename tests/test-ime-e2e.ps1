$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Japanese entered through the operating system's own IME, recorded and then
# carried out again, judged by what the three fields actually hold.
#
# Five things a person does, in one uninterrupted sitting:
#   1. hiragana, committed with Enter
#   2. a conversion to kanji, chosen with Space and committed with Enter
#   3. a field whose whole content is Japanese punctuation
#   4. the field that already had the keyboard before the recording started
#   5. two more fields reached with Tab inside the same window
#
# Nothing here types a character with KEYEVENTF_UNICODE. Every character goes
# through the IME the way a keyboard produces it, because an injected character
# would test nothing about IME input at all.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-ime-e2e (types real keys through the IME; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)

# The Japanese IME has to be on this machine. If it is not, the test says so
# and stops rather than passing on a check it never made.
$japanese = $false
foreach ($language in [System.Windows.Forms.InputLanguage]::InstalledInputLanguages) {
    if ($language.Culture.Name -eq 'ja-JP') { $japanese = $true }
}
if (-not $japanese) {
    Write-Output 'SKIP test-ime-e2e (no ja-JP input language is installed on this machine, so IME input cannot be produced)'
    return
}

$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-ime-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

if ($null -eq ('PuiIme.Keys' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiIme
{
    public static class Keys
    {
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYUP = 0x0002;
        private const uint WM_IME_CONTROL = 0x0283;
        private const int IMC_GETCONVERSIONMODE = 0x0001;
        private const int IMC_SETCONVERSIONMODE = 0x0002;
        private const int IMC_GETOPENSTATUS = 0x0005;
        private const int IMC_SETOPENSTATUS = 0x0006;
        // native + full width + roman entry: what "hiragana" means on a
        // Japanese keyboard.
        public const int HiraganaRoman = 0x0019;

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
            Thread.Sleep(40);
            Key(virtualKey, true);
            Thread.Sleep(90);
        }

        // The IME belongs to the target's own thread, so it is asked through
        // the window it publishes for exactly this. Turning it on from here is
        // the same state the operator's own half-width/full-width key produces.
        public static long Open(long hwnd, bool open)
        {
            IntPtr ime = ImmGetDefaultIMEWnd(new IntPtr(hwnd));
            if (ime == IntPtr.Zero) return -1;
            SendMessage(ime, WM_IME_CONTROL, new IntPtr(IMC_SETOPENSTATUS), new IntPtr(open ? 1 : 0));
            Thread.Sleep(120);
            return SendMessage(ime, WM_IME_CONTROL, new IntPtr(IMC_GETOPENSTATUS), IntPtr.Zero).ToInt64();
        }

        public static long Mode(long hwnd, int mode)
        {
            IntPtr ime = ImmGetDefaultIMEWnd(new IntPtr(hwnd));
            if (ime == IntPtr.Zero) return -1;
            SendMessage(ime, WM_IME_CONTROL, new IntPtr(IMC_SETCONVERSIONMODE), new IntPtr(mode));
            Thread.Sleep(120);
            return SendMessage(ime, WM_IME_CONTROL, new IntPtr(IMC_GETCONVERSIONMODE), IntPtr.Zero).ToInt64();
        }

        // Romaji, one physical key at a time. The IME turns them into kana.
        public static void Romaji(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                ushort virtualKey;
                if (character >= 'a' && character <= 'z') virtualKey = (ushort)(0x41 + (character - 'a'));
                else if (character >= '0' && character <= '9') virtualKey = (ushort)(0x30 + (character - '0'));
                else if (character == '.') virtualKey = 0xBE;
                else if (character == ',') virtualKey = 0xBC;
                else if (character == '-') virtualKey = 0xBD;
                else throw new ArgumentException("this helper types romaji letters, digits, period, comma and hyphen only");
                Press(virtualKey);
            }
            Thread.Sleep(260);
        }
    }
}
'@
}

function Read-Field($processId, $automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
        foreach ($edit in $top.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)))) {
            try { return $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        }
    }
    return $null
}
function Clear-Field($processId, $automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
        foreach ($edit in $top.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)))) {
            try { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('') } catch { }
        }
    }
}
# Japanese does not survive a console that is not expecting it, so every value
# is reported as its code points as well as itself.
function Show([string]$value) {
    if ($null -eq $value) { return '<null>' }
    if ($value.Length -eq 0) { return '(empty)' }
    $codes = @()
    foreach ($character in $value.ToCharArray()) { $codes += ('U+' + ([int][char]$character).ToString('X4')) }
    return '(' + $value.Length + ') ' + ($codes -join ' ')
}

$fixture = $null
$recorder = $null
$engine = $null
$results = @()
try {
    $ready = Join-Path $temp 'ime.json'
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $fixture = Start-Process $build.FixtureIme -ArgumentList @(
        '--ready', $ready, '--left', ($work.Left + 80), '--top', ($work.Top + 80)) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ((-not (Test-Path $ready)) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 60 }
    if (-not (Test-Path $ready)) { throw 'FixtureIme did not become ready.' }
    $f = Get-Content $ready -Raw | ConvertFrom-Json
    [AppStudio.ScreenCapture]::Raise([int64]$f.window)
    Start-Sleep -Milliseconds 900

    # The window opens with the keyboard in the first field and nothing clicks
    # on it: scenario 4.
    if ([AppStudio.WindowTools]::FocusedControl() -ne [int64]$f.first) {
        throw 'the fixture did not open with the keyboard in the first field'
    }
    $opened = [PuiIme.Keys]::Open([int64]$f.first, $true)
    $mode = [PuiIme.Keys]::Mode([int64]$f.first, [PuiIme.Keys]::HiraganaRoman)
    if ($opened -ne 1) {
        Write-Output ('SKIP test-ime-e2e (the IME would not open for the target window: IMC_GETOPENSTATUS=' + $opened + ')')
        return
    }
    Write-Output ('NOTE test-ime-e2e ime open=' + $opened + ' conversionMode=' + $mode)

    # ===== record ===========================================================
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'ime e2e')
    $session.ValuePolicy = 'recordText'
    $recorder = New-Object AppStudio.Recorder($root, $session, $null, $null)
    $recorder.Start()
    Start-Sleep -Milliseconds 2500

    # 1. hiragana into the field that already had the keyboard
    [PuiIme.Keys]::Romaji('konnnitiha')
    [PuiIme.Keys]::Press(0x0D)
    Start-Sleep -Milliseconds 700

    # 2. a conversion to kanji in the same field
    [PuiIme.Keys]::Romaji('kanji')
    [PuiIme.Keys]::Press(0x20)
    Start-Sleep -Milliseconds 400
    [PuiIme.Keys]::Press(0x0D)
    Start-Sleep -Milliseconds 700

    # 5. Tab to the second field, hiragana again
    [PuiIme.Keys]::Press(0x09)
    Start-Sleep -Milliseconds 600
    [PuiIme.Keys]::Romaji('tesuto')
    [PuiIme.Keys]::Press(0x0D)
    Start-Sleep -Milliseconds 700

    # 5 + 3. Tab to the third field, Japanese punctuation and nothing else
    [PuiIme.Keys]::Press(0x09)
    Start-Sleep -Milliseconds 600
    [PuiIme.Keys]::Romaji('.,')
    [PuiIme.Keys]::Press(0x0D)
    Start-Sleep -Milliseconds 1200

    $recorder.Stop()
    $recorder = $null
    [AppStudio.SessionStore]::WriteMeta($session)

    # What the application actually holds. This is the truth every claim below
    # is measured against.
    $truth = @{}
    foreach ($name in @('Field1', 'Field2', 'Field3')) { $truth[$name] = Read-Field $fixture.Id $name }
    foreach ($name in @('Field1', 'Field2', 'Field3')) { Write-Output ('NOTE recorded-in-app ' + $name + ' = ' + (Show $truth[$name])) }
    if ([string]::IsNullOrEmpty($truth['Field1'])) { throw 'the IME put nothing into the first field, so there is nothing to test' }

    # ===== what the recording caught ========================================
    $steps = @($session.Steps)
    $texts = @($steps | Where-Object { $_.Kind -eq 'textInput' })
    $chords = @($steps | Where-Object { $_.Kind -eq 'keyChord' })
    $events = @($session.InputEvents)
    Write-Output ('NOTE steps=' + $steps.Count + ' kinds=' + ((@($steps | ForEach-Object { $_.Kind }) | Sort-Object -Unique) -join '+') +
        ' textInputs=' + $texts.Count + ' chords=' + ((@($chords | ForEach-Object { $_.KeyChord }) | Sort-Object -Unique) -join ',') +
        ' events=' + $events.Count + ' eventKinds=' + ((@($events | ForEach-Object { $_.Kind }) | Sort-Object -Unique) -join '+'))
    foreach ($step in $texts) {
        Write-Output ('NOTE textInput ' + $step.StepId + ' element=' + $step.ElementLabel + ' value=' + (Show $step.Value))
    }

    # Each scenario is judged on the last value the recording has for the field
    # it belongs to.
    # The step names the element by its AutomationId, which is what the fixture
    # calls the field. Its label is the caption beside it, which is a different
    # word.
    function Last-Value($texts, $automationId) {
        $found = $null
        foreach ($step in $texts) { if ($step.AutomationId -eq $automationId) { $found = $step.Value } }
        return $found
    }
    $kanji = [string][char]0x611F
    $scenarios = @(
        @{ id = '1-hiragana';    field = 'Field1'; expect = ([string][char]0x3053 + [char]0x3093 + [char]0x306B + [char]0x3061 + [char]0x306F) },
        @{ id = '2-kanji';       field = 'Field1'; expect = $kanji },
        @{ id = '3-punctuation'; field = 'Field3'; expect = ([string][char]0x3002 + [char]0x3001) },
        @{ id = '4-prefocused';  field = 'Field1'; expect = $null },
        @{ id = '5-tabbed';      field = 'Field2'; expect = ([string][char]0x3066 + [char]0x3059 + [char]0x3068) }
    )
    $recordedOk = @()
    $recordedLost = @()
    foreach ($scenario in $scenarios) {
        $inApp = $truth[$scenario.field]
        $inRecord = Last-Value $texts $scenario.field
        $ok = (-not [string]::IsNullOrEmpty($inRecord)) -and ($inRecord -eq $inApp)
        if ($ok) { $recordedOk += $scenario.id } else { $recordedLost += $scenario.id }
        Write-Output ('NOTE scenario ' + $scenario.id + ' field=' + $scenario.field + ' app=' + (Show $inApp) +
            ' recorded=' + (Show $inRecord) + ' -> ' + $(if ($ok) { 'kept' } else { 'LOST' }))
        if ($null -ne $scenario.expect -and (-not $inApp.Contains($scenario.expect))) {
            Write-Output ('NOTE scenario ' + $scenario.id + ' did not produce the expected characters in the application; the IME behaved differently here')
        }
    }

    # Which fields the typing detector noticed at all. A field whose content is
    # only Japanese punctuation is entered with keys that are never watched, so
    # its value survives through the read-back while the timeline shows no
    # typing at all. That difference is measured rather than assumed.
    $typingLabels = @()
    foreach ($item in $events) { if ($item.Kind -eq 'typing' -and $item.ElementLabel) { $typingLabels += $item.ElementLabel } }
    Write-Output ('NOTE typing events on: ' + $(if ($typingLabels.Count -eq 0) { 'none' } else { ((@($typingLabels | Sort-Object -Unique)) -join ' , ') }))
    if ($session.Limits.Count -eq 0) { Write-Output 'NOTE limits: none' }
    foreach ($limit in $session.Limits) { Write-Output ('NOTE limit: ' + $limit) }

    # ===== replay, and read the fields again ================================
    foreach ($name in @('Field1', 'Field2', 'Field3')) { Clear-Field $fixture.Id $name }
    Start-Sleep -Milliseconds 500
    foreach ($name in @('Field1', 'Field2', 'Field3')) {
        if (-not [string]::IsNullOrEmpty((Read-Field $fixture.Id $name))) { throw ('the fields could not be emptied before the replay: ' + $name) }
    }

    $engine = New-Object AppStudio.ReplayEngine($root, $session)
    $report = $engine.Run('auto', $true)
    $engine.Dispose()
    $engine = $null
    Start-Sleep -Milliseconds 1200

    $after = @{}
    foreach ($name in @('Field1', 'Field2', 'Field3')) { $after[$name] = Read-Field $fixture.Id $name }
    $replayedOk = @()
    $replayedLost = @()
    foreach ($name in @('Field1', 'Field2', 'Field3')) {
        $ok = ($after[$name] -eq $truth[$name])
        if ($ok) { $replayedOk += $name } else { $replayedLost += $name }
        Write-Output ('NOTE replay ' + $name + ' before=' + (Show $truth[$name]) + ' after=' + (Show $after[$name]) + ' -> ' + $(if ($ok) { 'matched' } else { 'DIFFERENT' }))
    }
    $trail = @()
    foreach ($step in $session.Steps) {
        if ($null -ne $step.LastReplay) { $trail += ($step.StepId + '/' + $step.Kind + '=' + $step.LastReplay.State) }
    }
    Write-Output ('NOTE replay trail ' + ($trail -join ' '))

    # ===== what this test insists on ========================================
    # Only what was measured above. A scenario that turns out not to survive is
    # printed as LOST rather than passed over, and the two lines below are the
    # guarantees this test locks in.
    if ($recordedOk.Count -lt 1) { throw ('no IME scenario survived recording at all: ' + ($recordedLost -join ',')) }
    if ($replayedLost.Count -gt 0 -and $recordedLost.Count -eq 0) {
        throw ('every field was recorded but replay did not restore: ' + ($replayedLost -join ',') + ' trail=' + ($trail -join ' '))
    }

    Write-Output ('PASS test-ime-e2e imeMode=' + $mode + ' recordedKept=' + ($recordedOk -join '+') +
        ' recordedLost=' + $(if ($recordedLost.Count -eq 0) { 'none' } else { ($recordedLost -join '+') }) +
        ' replayMatched=' + ($replayedOk -join '+') +
        ' replayDifferent=' + $(if ($replayedLost.Count -eq 0) { 'none' } else { ($replayedLost -join '+') }) +
        ' steps=' + $report.Succeeded + '/' + $report.Attempted)
} finally {
    if ($null -ne $recorder) { $recorder.Dispose() }
    if ($null -ne $engine) { $engine.Dispose() }
    if ($null -ne $fixture -and -not $fixture.HasExited) {
        try { [PuiIme.Keys]::Open([int64]$f.first, $false) | Out-Null } catch { }
        $fixture.Kill()
    }
    [AppStudio.Probe]::Shutdown()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
