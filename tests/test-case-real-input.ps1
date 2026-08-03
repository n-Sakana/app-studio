$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Walks the case flow the way the operator does it: the physical pointer is
# moved and clicked, the goal is typed on the keyboard, and the answer is pasted
# with Ctrl+V. Every click is checked to be over the intended process first, so
# a stray press cannot land on whatever else is on the desktop. The only
# application operated is the fixture. Pictures of each step are written to
# tests/.build/real-input so the result can be looked at afterwards.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-case-real-input (moves the real pointer and types real keys; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$shotDir = Join-Path $PSScriptRoot '.build\real-input'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
Get-ChildItem -LiteralPath $shotDir -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-real-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$fixture = $null
$app = $null
$caseRoot = Join-Path $root 'runtime\cases'
$before = @{}
if (Test-Path -LiteralPath $caseRoot) { Get-ChildItem -LiteralPath $caseRoot -Directory | ForEach-Object { $before[$_.Name] = $true } }

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

        public static bool Click(int x, int y)
        {
            if (!SetCursorPos(x, y)) return false;
            Thread.Sleep(60);
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].data.mouse.flags = MOUSEEVENTF_LEFTDOWN;
            inputs[0].data.mouse.extra = GetMessageExtraInfo();
            inputs[1].type = INPUT_MOUSE;
            inputs[1].data.mouse.flags = MOUSEEVENTF_LEFTUP;
            inputs[1].data.mouse.extra = GetMessageExtraInfo();
            return SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) == 2;
        }

        // Unicode key events carry the character itself, so the same text is
        // produced whatever keyboard layout the machine is set to.
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
if ($null -eq ('PuiTest.RealNative' -as [type])) {
    Add-Type -Namespace 'PuiTest' -Name 'RealNative' -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wparam, System.Text.StringBuilder lparam, uint flags, uint timeout, out IntPtr result);
'@
}

function Get-ControlText($handle) {
    $builder = New-Object System.Text.StringBuilder 1024
    $answer = [IntPtr]::Zero
    $call = [PuiTest.RealNative]::SendMessageTimeoutW([IntPtr][int64]$handle, 0x000D, [IntPtr]1024, $builder, 0x0002, 2000, [ref]$answer)
    if ($call -eq [IntPtr]::Zero) { throw ('WM_GETTEXT did not return for handle ' + $handle) }
    return $builder.ToString()
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
# Nothing is clicked until the point is confirmed to belong to the process that
# is meant to receive it.
function Click-Element($element, $label, $expectedPid) {
    if ($null -eq $element) { throw ('Not on screen: ' + $label) }
    # A control scrolled below the fold has no point to aim at. Focusing it makes
    # the panel bring it into view, which is what a keyboard user would do.
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
    Start-Sleep -Milliseconds 450
    return "$x,$y"
}
# A row further down a scrolling list has no point on screen to aim at, so it is
# brought into view before the pointer is sent anywhere.
function Scroll-Into-View($element) {
    if ($null -eq $element) { return }
    try {
        ($element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView()
        Start-Sleep -Milliseconds 400
    } catch {
    }
}
function Shoot($window, $name) {
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    return [AppStudio.Capture]::Crop($rect, $masks, (Join-Path $shotDir ($name + '.png')), $handle)
}

$clicks = 0
try {
    $ready = Join-Path $tempDir 'ready.json'
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    $handles = Get-Content -LiteralPath $ready -Raw -Encoding UTF8 | ConvertFrom-Json
    $fixtureWindows = [AppStudio.WindowTools]::ListProcessWindows($fixture.Id, 0)
    $bounds = $fixtureWindows[0].Rect
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$fixtureWindows[0].Hwnd, ($work.Left + 20), ($work.Top + 20), $bounds.Width, $bounds.Height)
    Start-Sleep -Milliseconds 500

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', (Join-Path $root 'app-studio.ps1'), '-AutoCloseMs', '420000') -PassThru -WindowStyle Hidden
    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    $tool = $app.Id
    Start-Sleep -Milliseconds 1500
    $null = Shoot $window '01-target'

    # 1. point at the fixture row and press the button, with the real mouse.
    $row = $null
    $limit = [DateTime]::UtcNow.AddSeconds(25)
    while ($null -eq $row -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if ($item.Current.Name -like '*FixtureWinForms*') { $row = $item; break }
        }
        if ($null -eq $row) {
            $refresh = Find-Named $window ([System.Windows.Automation.ControlType]::Button) '一覧を更新'
            if ($null -ne $refresh) { $null = Click-Element $refresh '一覧を更新' $tool; $clicks++ }
            Start-Sleep -Milliseconds 400
        }
    }
    if ($null -eq $row) { throw 'The fixture window was not offered in the target list.' }
    Scroll-Into-View $row
    $null = Click-Element $row 'fixture row' $tool; $clicks++
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この画面を調べる') 'この画面を調べる' $tool; $clicks++
    $null = Shoot $window '02-menu'

    # 2. every purpose has to be on the menu, and each start button is named
    # after its own purpose, so they are asked for by name rather than counted.
    $purposes = @('自動でひととおり洗い出す', '自分で操作しながら記録する', '部品をひとつ試しに操作する', 'AIに操作を考えてもらう')
    $starts = @()
    foreach ($purpose in $purposes) {
        $hit = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $purpose
        if ($null -eq $hit) { throw ('The menu is missing the purpose: ' + $purpose) }
        $starts += $hit
    }
    if ($starts.Count -lt 4) { throw ('Expected four purposes, found ' + $starts.Count) }
    # Investigate first, then hand that result over.
    $null = Click-Element $starts[0] $purposes[0] $tool; $clicks++
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) 'もう一度調べる' 120000)) { throw 'The scan never reached its result step.' }
    Start-Sleep -Milliseconds 1000
    $null = Shoot $window '02b-scan-result'
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この結果をAIに渡す') 'この結果をAIに渡す' $tool; $clicks++
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) '依頼文をコピーする' 30000)) { throw 'The request step did not appear.' }
    Start-Sleep -Milliseconds 2000
    $null = Shoot $window '03-request'

    # 3. type the goal on the keyboard after clicking into the box.
    $goalBox = Find-Named $window ([System.Windows.Automation.ControlType]::Edit) 'やりたいこと（自由に書く）'
    $null = Click-Element $goalBox 'goal box' $tool; $clicks++
    if (-not [PuiTest.RealInput]::Type('顧客コード欄にREAL-INPUTと入れて保存する')) { throw 'SendInput refused the keystrokes.' }
    Start-Sleep -Milliseconds 600
    $typed = ($goalBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($typed -ne '顧客コード欄にREAL-INPUTと入れて保存する') { throw ('The typed goal did not arrive intact: "' + $typed + '"') }
    $null = Shoot $window '04-goal-typed'

    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '依頼文をコピーする') '依頼文をコピーする' $tool; $clicks++
    Start-Sleep -Milliseconds 900
    # What the operator would paste into the chat has to actually be on the clipboard.
    $clipboard = [System.Windows.Forms.Clipboard]::GetText()
    if (-not $clipboard.Contains('REAL-INPUT')) { throw 'The request text on the clipboard does not carry the goal.' }
    if (-not $clipboard.Contains('pui-plan')) { throw 'The request text on the clipboard does not state the answer format.' }
    $null = Shoot $window '05-request-copied'

    $caseDir = $null
    foreach ($directory in (Get-ChildItem -LiteralPath $caseRoot -Directory)) {
        if (-not $before.ContainsKey($directory.Name)) { $caseDir = $directory.FullName }
    }
    if ($null -eq $caseDir) { throw 'No case folder was created.' }
    $elements = (Get-Content -LiteralPath (Join-Path $caseDir 'elements.json') -Raw -Encoding UTF8 | ConvertFrom-Json).elements
    $codeBox = $elements | Where-Object { $_.listed -and $_.automationId -eq 'CustomerCode' } | Select-Object -First 1
    $saveButton = $elements | Where-Object { $_.listed -and $_.automationId -eq 'FirstSave' } | Select-Object -First 1
    if ($null -eq $codeBox -or $null -eq $saveButton) { throw 'The scan did not offer the fixture parts.' }

    # 4. paste the answer with a real Ctrl+V, the way it comes back from a chat.
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '回答を取り込む') '回答を取り込む' $tool; $clicks++
    $answer = 'こういう手順でどうでしょうか。' + "`r`n" + '```json' + "`r`n" +
        '{ "format": "pui-plan", "version": 1, "title": "実キー入力の確認",' +
        ' "steps": [' +
        ' { "id": 1, "action": "setValue", "target": { "element": "' + $codeBox.id + '" }, "value": "REAL-INPUT", "expect": "顧客コード欄が変わる" },' +
        ' { "id": 2, "action": "invoke", "target": { "element": "' + $saveButton.id + '" }, "expect": "保存ボタンの表示が変わる" } ] }' + "`r`n" + '```'
    [System.Windows.Forms.Clipboard]::SetText($answer)
    $answerBox = Find-Named $window ([System.Windows.Automation.ControlType]::Edit) '回答をそのまま貼る。前後に説明があってもよい。'
    $null = Click-Element $answerBox 'answer box' $tool; $clicks++
    if (-not [PuiTest.RealInput]::Chord(0x11, 0x56)) { throw 'SendInput refused Ctrl+V.' }
    Start-Sleep -Milliseconds 700
    $pasted = ($answerBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if (-not $pasted.Contains('pui-plan')) { throw 'Ctrl+V did not put the answer into the box.' }
    $null = Shoot $window '06-answer-pasted'

    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) '貼った内容を読み取る') '貼った内容を読み取る' $tool; $clicks++
    Start-Sleep -Milliseconds 600
    $null = Shoot $window '07-plan-shown'
    $runButton = Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この内容で実行する'
    if ($null -eq $runButton) { throw 'The run button is missing.' }
    if ($runButton.Current.IsEnabled) { throw 'A changing plan could be run without permission.' }

    $writeToggle = Find-Named $window ([System.Windows.Automation.ControlType]::CheckBox) 'このセッションで対象を変える操作を許可する'
    $null = Click-Element $writeToggle 'write permission' $tool; $clicks++
    $runButton = Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'この内容で実行する'
    if (-not $runButton.Current.IsEnabled) { throw 'Permission was given but the run stayed refused.' }
    $null = Shoot $window '08-permission-given'

    # 5. run it, and check the fixture through its own handles.
    $null = Click-Element $runButton 'この内容で実行する' $tool; $clicks++
    if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::Button) '案件フォルダを開く' 90000)) { throw 'The run never reached its result step.' }
    Start-Sleep -Milliseconds 800
    $null = Shoot $window '09-result'

    $codeValue = Get-ControlText $handles.normal
    if ($codeValue -ne 'REAL-INPUT') { throw ('The fixture edit was not actually changed: "' + $codeValue + '"') }
    $savedLabel = Get-ControlText $handles.first
    if ($savedLabel -notlike 'Saved*') { throw ('The fixture button was not actually pressed: "' + $savedLabel + '"') }
    $passwordValue = Get-ControlText $handles.password
    if ($passwordValue -ne 'P@ssword123') { throw ('The password field was touched: "' + $passwordValue + '"') }

    # 6. the history screen, reached with the real mouse as well.
    $null = Click-Element (Find-Named $window ([System.Windows.Automation.ControlType]::Button) 'これまでの記録を見る') 'これまでの記録を見る' $tool; $clicks++
    Start-Sleep -Milliseconds 800
    $historyRow = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
        if ($item.Current.Name -like ('*' + (Split-Path -Leaf $caseDir) + '*')) { $historyRow = $item }
    }
    if ($null -eq $historyRow) { throw 'The new case is not in the history list.' }
    Scroll-Into-View $historyRow
    $null = Click-Element $historyRow 'history row' $tool; $clicks++
    Start-Sleep -Milliseconds 700
    $null = Shoot $window '10-history'

    $caseText = [IO.File]::ReadAllText((Join-Path $caseDir 'case.md'), [Text.Encoding]::UTF8)
    foreach ($term in @('REAL-INPUT', '使えた識別情報', 'AutomationId=CustomerCode')) {
        if (-not $caseText.Contains($term)) { throw ('The case record is missing: ' + $term) }
    }
    $shots = @(Get-ChildItem -LiteralPath $shotDir -Filter '*.png')
    ($window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Start-Sleep -Milliseconds 800

    Write-Output ('PASS test-case-real-input realClicks=' + $clicks + ' typedGoal=ok pasteCtrlV=ok clipboardRequest=ok' +
        ' case=' + (Split-Path -Leaf $caseDir) + ' fixtureEdit="' + $codeValue + '" fixtureButton="' + $savedLabel + '"' +
        ' passwordUntouched=yes writeGate=enforced shots=' + $shots.Count + ' shotDir=' + $shotDir)
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $fixture -and -not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
