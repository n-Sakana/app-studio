$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the product against two ordinary Windows applications - Notepad and
# Calculator - with the physical pointer and real keystrokes, and checks the
# three answers the assistant flow has to tell apart: a correct plan, a plan
# that is malformed, and a plan whose steps name parts that do not exist.
#
# Only windows this test started itself are ever operated. Every click is
# checked to land on the intended process first, and both applications are
# closed without saving anything.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-gui-e2e (moves the real pointer and types real keys; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$shotDir = Join-Path $PSScriptRoot '.build\gui-e2e'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
Get-ChildItem -LiteralPath $shotDir -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
$caseRoot = Join-Path $root 'runtime\cases'
$liveRoot = Join-Path $root 'runtime\live-session'
$casesBefore = @{}
if (Test-Path -LiteralPath $caseRoot) { Get-ChildItem -LiteralPath $caseRoot -Directory | ForEach-Object { $casesBefore[$_.Name] = $true } }
$sessionsBefore = @{}
if (Test-Path -LiteralPath $liveRoot) { Get-ChildItem -LiteralPath $liveRoot -Directory | ForEach-Object { $sessionsBefore[$_.Name] = $true } }

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

        // A pointer that jumps has no path, and a recorder that watches a path
        // has nothing to record. The steps are what a hand would produce.
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

        // A press with no duration is not what a hand produces, and a recorder
        // that samples the button state can miss it. The hold is the length of
        // an ordinary click.
        public static bool Click(int x, int y)
        {
            if (!SetCursorPos(x, y)) return false;
            Thread.Sleep(80);
            return PressAndRelease(120);
        }

        static bool PressAndRelease(int holdMs)
        {
            INPUT[] down = new INPUT[1];
            down[0].type = INPUT_MOUSE;
            down[0].data.mouse.flags = MOUSEEVENTF_LEFTDOWN;
            down[0].data.mouse.extra = GetMessageExtraInfo();
            if (SendInput(1, down, Marshal.SizeOf(typeof(INPUT))) != 1) return false;
            Thread.Sleep(holdMs);
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
                Thread.Sleep(12);
            }
            return true;
        }

        public static bool Chord(ushort modifier, ushort key)
        {
            INPUT[] inputs = new INPUT[4];
            inputs[0].type = INPUT_KEYBOARD; inputs[0].data.key.vk = modifier;
            inputs[1].type = INPUT_KEYBOARD; inputs[1].data.key.vk = key;
            inputs[2].type = INPUT_KEYBOARD; inputs[2].data.key.vk = key; inputs[2].data.key.flags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].data.key.vk = modifier; inputs[3].data.key.flags = KEYEVENTF_KEYUP;
            return SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT))) == 4;
        }
    }
}
'@
}

function Find-Descendants($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($window, $controlType, $label) {
    foreach ($item in Find-Descendants $window $controlType) {
        if ($item.Current.Name -eq $label -and -not $item.Current.IsOffscreen) { return $item }
    }
    return $null
}
function Wait-Named($window, $controlType, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $found = Find-Named $window $controlType $label
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Scroll-Into-View($element) {
    if ($null -eq $element) { return }
    try {
        ($element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView()
        Start-Sleep -Milliseconds 400
    } catch {
    }
}
$script:clicks = 0
function Click-Element($element, $label, $expectedPid) {
    if ($null -eq $element) { throw ('Not on screen: ' + $label) }
    if ($element.Current.IsOffscreen) {
        try { $element.SetFocus() } catch { }
        Start-Sleep -Milliseconds 500
    }
    $rect = $element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw ('No rectangle: ' + $label) }
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    $owner = [AppStudio.WindowTools]::ProcessIdAt($x, $y)
    if ($owner -ne $expectedPid) { throw ('The point for "' + $label + '" belongs to process ' + $owner + ', not ' + $expectedPid + '; nothing was clicked.') }
    if (-not [PuiTest.RealInput]::Click($x, $y)) { throw ('SendInput refused the click for ' + $label) }
    $script:clicks++
    Start-Sleep -Milliseconds 450
    return "$x,$y"
}
function Shoot($window, $name) {
    Start-Sleep -Milliseconds 300
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    return [AppStudio.Capture]::Crop($rect, $masks, (Join-Path $shotDir ($name + '.png')), $handle)
}
function Wait-Window($processId, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $found = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
# Notepad and Calculator both run as packaged applications on current Windows,
# so the window that appears may belong to a different process than the one
# that was started, and a second launch may become a tab in a window that was
# already open. So the window is matched by handle, not by process, and a window
# that was already there before this test ran is never treated as ours.
function List-TopWindows($namePattern) {
    $found = @()
    foreach ($item in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name -like $namePattern) { $found += $item }
    }
    return $found
}
function Wait-NewTopWindow($namePattern, $timeoutMs, $knownHandles) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        foreach ($item in List-TopWindows $namePattern) {
            if ($knownHandles -notcontains ([int64]$item.Current.NativeWindowHandle)) { return $item }
        }
        Start-Sleep -Milliseconds 400
    }
    return $null
}
# The Calculator display, read straight off the application. This is what says
# whether the plan really moved the target, independently of what the tool
# reports about its own run.
function Calculator-Display($calculatorWindow) {
    if ($null -eq $calculatorWindow) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CalculatorResults')
    $display = $calculatorWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $display) { return $null }
    return $display.Current.Name
}
function Choose-Target($window, $tool, $rowPattern) {
    $row = $null
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $row -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if ($item.Current.Name -like $rowPattern) { $row = $item; break }
        }
        if ($null -eq $row) {
            $refresh = Find-Named $window ([System.Windows.Automation.ControlType]::Button) '一覧を更新'
            if ($null -ne $refresh) { $null = Click-Element $refresh '一覧を更新' $tool }
            Start-Sleep -Milliseconds 500
        }
    }
    if ($null -eq $row) { throw ('The target list never offered ' + $rowPattern) }
    Scroll-Into-View $row
    $null = Click-Element $row ('row ' + $rowPattern) $tool
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この画面を調べる') 'この画面を調べる' $tool
}
function Menu-Start($window, $tool, $index, $label) {
    # Each start button is named after the purpose it starts, so the menu is
    # checked by asking for all four by name and the wanted one is pressed by
    # name too, instead of trusting the order identical buttons come back in.
    $purposes = @('自動でひととおり洗い出す', '自分で操作しながら記録する', '部品をひとつ試しに操作する', 'AIに操作を考えてもらう')
    $starts = @()
    foreach ($purpose in $purposes) {
        $hit = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $purpose
        if ($null -eq $hit) { throw ('The menu is missing the purpose: ' + $purpose) }
        $starts += $hit
    }
    if ($starts.Count -lt 4) { throw ('Expected four purposes on the menu, found ' + $starts.Count) }
    $null = Click-Element $starts[$index] $label $tool
}

$app = $null
$notepad = $null
$calculator = $null
$notepadPid = 0
$calculatorPid = 0
$notepadIsOurs = $false
$calculatorIsOurs = $false
$notes = @()
try {
    # Everything already on screen belongs to whoever is using the machine.
    # This test only ever closes, and only ever types into, a window that was
    # not there before it started.
    $notepadBefore = @()
    foreach ($item in (List-TopWindows '*メモ帳*')) { $notepadBefore += [int64]$item.Current.NativeWindowHandle }
    foreach ($item in (List-TopWindows '*Notepad*')) { $notepadBefore += [int64]$item.Current.NativeWindowHandle }
    $calculatorBefore = @()
    foreach ($item in (List-TopWindows '電卓')) { $calculatorBefore += [int64]$item.Current.NativeWindowHandle }
    foreach ($item in (List-TopWindows 'Calculator')) { $calculatorBefore += [int64]$item.Current.NativeWindowHandle }

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', (Join-Path $root 'app-studio.ps1'), '-AutoCloseMs', '900000') -PassThru -WindowStyle Hidden
    $window = Wait-Window $app.Id 60000
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    $tool = $app.Id
    Start-Sleep -Milliseconds 1500

    # ---------------------------------------------------------------- Notepad
    $notepad = Start-Process -FilePath 'notepad.exe' -PassThru
    $notepadWindow = Wait-NewTopWindow '*メモ帳*' 20000 $notepadBefore
    if ($null -eq $notepadWindow) { $notepadWindow = Wait-NewTopWindow '*Notepad*' 10000 $notepadBefore }
    $notepadIsOurs = $null -ne $notepadWindow
    if (-not $notepadIsOurs) {
        # Current Notepad opens a tab in the window that is already there
        # instead of a second window. That window is somebody's work, so it is
        # used as a read only target and never typed into or closed.
        $existing = @(List-TopWindows '*メモ帳*') + @(List-TopWindows '*Notepad*')
        if ($existing.Count -eq 0) { throw 'Notepad never opened a window.' }
        $notepadWindow = $existing[0]
    }
    $notepadPid = $notepadWindow.Current.ProcessId
    $notes += ('notepad-window=' + (@{ $true = 'started-by-test'; $false = 'reused-existing-readonly' }[$notepadIsOurs]))
    Start-Sleep -Milliseconds 1500

    Choose-Target $window $tool '*Notepad*'
    $null = Shoot $window '01-notepad-menu'

    Menu-Start $window $tool 0 'はじめる (scan notepad)'
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) 'もう一度調べる' 120000)) { throw 'The Notepad scan never finished.' }
    Start-Sleep -Milliseconds 900
    $null = Shoot $window '02-notepad-scan'
    $notepadSummary = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        try { $value = ($item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $value = $null }
        if ($null -ne $value -and $value.Contains('自動調査の結果')) { $notepadSummary = $value }
    }
    if ($null -eq $notepadSummary) { throw 'The Notepad scan showed no summary.' }
    $notes += 'notepad-scan=ok'

    # Manual recording with the real pointer gliding across Notepad's text area.
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '別のことをする') '別のことをする' $tool
    Menu-Start $window $tool 1 'はじめる (observe notepad)'
    $notepadRect = $notepadWindow.Current.BoundingRectangle
    $centreX = [int]($notepadRect.X + $notepadRect.Width / 2)
    $centreY = [int]($notepadRect.Y + $notepadRect.Height / 2)
    [PuiTest.RealInput]::Glide($centreX, ([int]$notepadRect.Y + 60), $centreX, $centreY, 26, 90)
    Start-Sleep -Milliseconds 1200
    $owner = [AppStudio.WindowTools]::ProcessIdAt($centreX, $centreY)
    if ($owner -ne $notepadPid) { throw ('The Notepad centre point belongs to process ' + $owner + ', not ' + $notepadPid) }
    if (-not [PuiTest.RealInput]::Click($centreX, $centreY)) { throw 'SendInput refused the Notepad click.' }
    $script:clicks++
    Start-Sleep -Milliseconds 600
    if ($notepadIsOurs) {
        # Only a document this test created is ever typed into.
        if (-not [PuiTest.RealInput]::Type('AppStudio GUI check')) { throw 'SendInput refused the Notepad keystrokes.' }
        Start-Sleep -Milliseconds 700
        $notes += 'notepad-typed=yes'
    } else {
        $notes += 'notepad-typed=skipped-not-ours'
    }
    $null = Shoot $window '03-notepad-observe'
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '終わる') '終わる' $tool
    Start-Sleep -Milliseconds 1200
    $null = Shoot $window '04-notepad-observe-result'

    $sessionDir = $null
    foreach ($directory in (Get-ChildItem -LiteralPath $liveRoot -Directory)) {
        if (-not $sessionsBefore.ContainsKey($directory.Name)) { $sessionDir = $directory.FullName }
    }
    if ($null -eq $sessionDir) { throw 'No session folder was written.' }
    $observationPath = Join-Path $sessionDir 'observations.jsonl'
    if (-not (Test-Path -LiteralPath $observationPath)) { throw 'The manual recording wrote no observation log.' }
    $observationKinds = @(Get-Content -LiteralPath $observationPath -Encoding UTF8 | ForEach-Object { (ConvertFrom-Json $_).kind })
    if ($observationKinds -notcontains 'observe.start') { throw 'The observation log has no start.' }
    if ($observationKinds -notcontains 'observe.stop') { throw 'The observation log has no stop.' }
    $enters = @($observationKinds | Where-Object { $_ -eq 'observe.enter' }).Count
    if ($enters -lt 1) { throw 'The pointer crossed Notepad but nothing was recorded.' }
    $recordedClicks = @($observationKinds | Where-Object { $_ -eq 'observe.click' }).Count
    $notes += ('notepad-observe=enter:' + $enters + ',click:' + $recordedClicks)

    # ------------------------------------------------------------- Calculator
    $calculator = Start-Process -FilePath 'calc.exe' -PassThru
    $calculatorWindow = Wait-NewTopWindow '電卓' 25000 $calculatorBefore
    if ($null -eq $calculatorWindow) { $calculatorWindow = Wait-NewTopWindow 'Calculator' 10000 $calculatorBefore }
    $calculatorIsOurs = $null -ne $calculatorWindow
    if (-not $calculatorIsOurs) {
        $existing = @(List-TopWindows '電卓') + @(List-TopWindows 'Calculator')
        if ($existing.Count -eq 0) { throw 'Calculator never opened a window.' }
        $calculatorWindow = $existing[0]
    }
    $calculatorPid = $calculatorWindow.Current.ProcessId
    $notes += ('calculator-window=' + (@{ $true = 'started-by-test'; $false = 'reused-existing' }[$calculatorIsOurs]))
    Start-Sleep -Milliseconds 1500
    $displayBefore = Calculator-Display $calculatorWindow

    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '別のことをする') '別のことをする' $tool
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '対象を変える') '対象を変える' $tool
    Choose-Target $window $tool '*電卓*'
    Menu-Start $window $tool 0 'はじめる (scan calculator)'
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) 'もう一度調べる' 120000)) { throw 'The Calculator scan never finished.' }
    Start-Sleep -Milliseconds 900
    $null = Shoot $window '05-calculator-scan'

    # ------------------------------------- assistant flow, three kinds of answer
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この結果をAIに渡す') 'この結果をAIに渡す' $tool
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) '依頼文をコピーする' 30000)) { throw 'The request step did not appear.' }
    Start-Sleep -Milliseconds 2000
    $goalBox = Find-Named $window ([System.Windows.Automation.ControlType]::Edit) 'やりたいこと（自由に書く）'
    $null = Click-Element $goalBox 'goal box' $tool
    if (-not [PuiTest.RealInput]::Type('7 と 8 を押して足し算の画面を出す')) { throw 'SendInput refused the goal keystrokes.' }
    Start-Sleep -Milliseconds 700
    $typed = ($goalBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($typed -notlike '*7 と 8*') { throw ('The typed goal did not arrive intact: "' + $typed + '"') }
    $null = Shoot $window '06-goal-typed'
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '依頼文をコピーする') '依頼文をコピーする' $tool
    Start-Sleep -Milliseconds 1000
    $clipboard = [System.Windows.Forms.Clipboard]::GetText()
    if (-not $clipboard.Contains('pui-plan')) { throw 'The request text does not state the answer format.' }
    $null = Shoot $window '07-request-copied'

    $caseDir = $null
    foreach ($directory in (Get-ChildItem -LiteralPath $caseRoot -Directory)) {
        if (-not $casesBefore.ContainsKey($directory.Name)) { $caseDir = $directory.FullName }
    }
    if ($null -eq $caseDir) { throw 'No case folder was created.' }
    $elements = (Get-Content -LiteralPath (Join-Path $caseDir 'elements.json') -Raw -Encoding UTF8 | ConvertFrom-Json).elements
    $seven = $elements | Where-Object { $_.listed -and ($_.name -eq '7' -or $_.automationId -eq 'num7Button') } | Select-Object -First 1
    $eight = $elements | Where-Object { $_.listed -and ($_.name -eq '8' -or $_.automationId -eq 'num8Button') } | Select-Object -First 1
    if ($null -eq $seven -or $null -eq $eight) { throw 'The Calculator scan did not offer the digit buttons.' }

    $answerBoxName = '回答をそのまま貼る。前後に説明があってもよい。'
    function Paste-Answer($text) {
        [System.Windows.Forms.Clipboard]::SetText($text)
        $box = Find-Named $window ([System.Windows.Automation.ControlType]::Edit) $answerBoxName
        $null = Click-Element $box 'answer box' $tool
        $null = [PuiTest.RealInput]::Chord(0x11, 0x41)
        Start-Sleep -Milliseconds 200
        if (-not [PuiTest.RealInput]::Chord(0x11, 0x56)) { throw 'SendInput refused Ctrl+V.' }
        Start-Sleep -Milliseconds 800
        $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '貼った内容を読み取る') '貼った内容を読み取る' $tool
        Start-Sleep -Milliseconds 900
    }
    function Run-Enabled() {
        $button = Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この内容で実行する'
        if ($null -eq $button) { throw 'The run button is missing.' }
        return $button.Current.IsEnabled
    }

    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '回答を取り込む') '回答を取り込む' $tool

    # (a) an answer that is not a plan at all
    Paste-Answer 'すみません、その操作はこちらでは分かりませんでした。手作業でお願いします。'
    if (Run-Enabled) { throw 'A prose answer with no plan left the run enabled.' }
    $null = Shoot $window '08-answer-not-a-plan'
    $notes += 'reject-prose=blocked'

    # (b) a plan that is the right shape but names a part that does not exist
    $ghost = '{ "format": "pui-plan", "version": 1, "title": "ありえない部品",' +
        ' "steps": [ { "id": 1, "action": "invoke", "target": { "element": "el-does-not-exist" }, "expect": "何かが起きる" } ] }'
    Paste-Answer ('```json' + "`r`n" + $ghost + "`r`n" + '```')
    $ghostRunnable = Run-Enabled
    $null = Shoot $window '09-answer-unknown-element'
    $notes += ('reject-unknown-element=' + (@{ $true = 'accepted-then-fails'; $false = 'blocked' }[$ghostRunnable]))

    # (c) a plan that is truncated part way through, the way a chat cuts off
    $truncated = '{ "format": "pui-plan", "version": 1, "title": "途中で切れた回答", "steps": [ { "id": 1, "action": "invoke"'
    Paste-Answer ('```json' + "`r`n" + $truncated)
    if (Run-Enabled) { throw 'A truncated answer left the run enabled.' }
    $null = Shoot $window '10-answer-truncated'
    $notes += 'reject-truncated=blocked'

    # (d) the correct answer, which has to be accepted and run
    $good = '{ "format": "pui-plan", "version": 1, "title": "7 と 8 を押す",' +
        ' "steps": [' +
        ' { "id": 1, "action": "invoke", "target": { "element": "' + $seven.id + '" }, "expect": "7 が入る" },' +
        ' { "id": 2, "action": "invoke", "target": { "element": "' + $eight.id + '" }, "expect": "78 になる" } ] }'
    Paste-Answer ('```json' + "`r`n" + $good + "`r`n" + '```')
    $null = Shoot $window '11-answer-accepted'
    if (Run-Enabled) { throw 'A changing plan could be run before permission was given.' }
    $writeToggle = Find-Named $window ([System.Windows.Automation.ControlType]::CheckBox) 'このセッションで対象を変える操作を許可する'
    $null = Click-Element $writeToggle 'write permission' $tool
    if (-not (Run-Enabled)) { throw 'Permission was given but the run stayed refused.' }
    $null = Shoot $window '12-permission-given'
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この内容で実行する') 'この内容で実行する' $tool
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) '案件フォルダを開く' 120000)) { throw 'The run never reached its result step.' }
    Start-Sleep -Milliseconds 1000
    $null = Shoot $window '13-run-result'

    # The record has to hold the outcome of every step, whatever it was, and a
    # closing summary line that agrees with those steps.
    $runFiles = @(Get-ChildItem -LiteralPath $caseDir -Filter 'run-*.jsonl')
    if ($runFiles.Count -lt 1) { throw 'The run wrote no result file.' }
    $runLines = @(Get-Content -LiteralPath $runFiles[$runFiles.Count - 1].FullName -Encoding UTF8 | ForEach-Object { ConvertFrom-Json $_ })
    $stepLines = @($runLines | Where-Object { $_.kind -eq 'plan.step' })
    $runLine = @($runLines | Where-Object { $_.kind -eq 'plan.run' })[0]
    if ($stepLines.Count -lt 2) { throw ('The run recorded ' + $stepLines.Count + ' steps, expected 2.') }
    if ($null -eq $runLine) { throw 'The run wrote no closing summary line.' }
    $outcomes = @($stepLines | ForEach-Object { $_.outcome })
    foreach ($outcome in $outcomes) {
        if (@('success', 'failed', 'blocked', 'notSupported', 'unknown') -notcontains $outcome) { throw ('Unknown outcome: ' + $outcome) }
    }
    # An outcome the tool could not confirm has to be reported as unconfirmed,
    # not quietly counted as a success.
    $countedSuccess = @($outcomes | Where-Object { $_ -eq 'success' }).Count
    if ($runLine.success -ne $countedSuccess) { throw ('The summary claims ' + $runLine.success + ' successes but the steps show ' + $countedSuccess + '.') }
    if (-not $runLine.writeEnabled) { throw 'The run recorded itself as read only although permission had been given.' }
    foreach ($step in $stepLines) {
        if ([string]::IsNullOrEmpty($step.method)) { throw ('Step ' + $step.stepId + ' kept no route.') }
        if ([string]::IsNullOrEmpty($step.usedIdentity)) { throw ('Step ' + $step.stepId + ' kept no identifying material.') }
    }
    $notes += ('run-outcomes=' + ($outcomes -join ',') + ' run-summary=s' + $runLine.success + '/f' + $runLine.failed + '/u' + $runLine.unknown)

    # Whatever the tool concluded, the application itself has to show that the
    # two buttons were really pressed.
    $displayAfter = Calculator-Display $calculatorWindow
    if ($null -eq $displayAfter) {
        $notes += 'calculator-display=unreadable'
    } else {
        $notes += ('calculator-display="' + $displayBefore + '" -> "' + $displayAfter + '"')
        if ($displayAfter -notmatch '78') { throw ('The plan ran but the Calculator display never showed 78: "' + $displayAfter + '"') }
    }
    $caseText = [IO.File]::ReadAllText((Join-Path $caseDir 'case.md'), [Text.Encoding]::UTF8)
    if (-not $caseText.Contains('7 と 8')) { throw 'The case record does not carry the goal.' }

    # history, reached with the real mouse
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'これまでの記録を見る') 'これまでの記録を見る' $tool
    Start-Sleep -Milliseconds 900
    $historyRow = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
        if ($item.Current.Name -like ('*' + (Split-Path -Leaf $caseDir) + '*')) { $historyRow = $item }
    }
    if ($null -eq $historyRow) { throw 'The new case is not in the history list.' }
    Scroll-Into-View $historyRow
    $null = Click-Element $historyRow 'history row' $tool
    Start-Sleep -Milliseconds 800
    $null = Shoot $window '14-history'

    # the theme switch is part of the product, so it is pressed too
    $themeButton = Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'ライトとダークを切り替える'
    if ($null -eq $themeButton) { throw 'The theme switch is not on the window.' }
    $null = Click-Element $themeButton 'theme switch' $tool
    Start-Sleep -Milliseconds 700
    $null = Shoot $window '15-theme-switched'
    $themeFile = Join-Path $root 'runtime\settings\theme.txt'
    if (-not (Test-Path -LiteralPath $themeFile)) { throw 'The theme choice was not written.' }
    $storedTheme = (Get-Content -LiteralPath $themeFile -Raw).Trim()
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'ライトとダークを切り替える') 'theme switch back' $tool
    Start-Sleep -Milliseconds 600
    $null = Shoot $window '16-theme-restored'

    $shots = @(Get-ChildItem -LiteralPath $shotDir -Filter '*.png')
    ($window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Start-Sleep -Milliseconds 900

    Write-Output ('PASS test-gui-e2e realClicks=' + $script:clicks + ' targets=notepad,calculator ' + ($notes -join ' ') +
        ' theme=' + $storedTheme + ' case=' + (Split-Path -Leaf $caseDir) + ' shots=' + $shots.Count + ' shotDir=' + $shotDir)
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    # A window that was already on screen when this test started is somebody
    # else's work. Only what this test created is closed.
    if ($notepadIsOurs -and $notepadPid -ne 0) {
        $target = Get-Process -Id $notepadPid -ErrorAction SilentlyContinue
        if ($null -ne $target) { $target.CloseMainWindow() | Out-Null; if (-not $target.WaitForExit(4000)) { $target.Kill() } }
    }
    if ($notepadIsOurs -and $null -ne $notepad -and -not $notepad.HasExited) { $notepad.Kill(); $notepad.WaitForExit() }
    if ($calculatorIsOurs -and $calculatorPid -ne 0) {
        $target = Get-Process -Id $calculatorPid -ErrorAction SilentlyContinue
        if ($null -ne $target) { $target.CloseMainWindow() | Out-Null; if (-not $target.WaitForExit(4000)) { $target.Kill() } }
    }
    if ($calculatorIsOurs -and $null -ne $calculator -and -not $calculator.HasExited) { $calculator.Kill(); $calculator.WaitForExit() }
}
