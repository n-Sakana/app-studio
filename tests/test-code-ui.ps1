$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the real code screen through UI Automation. The physical pointer is
# never moved and no key is ever sent to anything on the desktop, so this test is
# safe to run while somebody is using the machine. It does use the clipboard,
# because that is the thing being tested; whatever was on it is put back at the
# end.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$shotDir = Join-Path $PSScriptRoot '.build\code-ui'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
$app = $null
$seedFolder = $null
$restore = $null
try { if ([Windows.Forms.Clipboard]::ContainsText()) { $restore = [Windows.Forms.Clipboard]::GetText() } } catch { }

function All-Of($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($window, $controlType, $label) {
    foreach ($item in All-Of $window $controlType) {
        if ($item.Current.Name -eq $label) { return $item }
    }
    return $null
}
function Wait-Named($window, $controlType, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $found = Find-Named $window $controlType $label
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Press($window, $label) {
    $button = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) $label 15000
    if ($null -eq $button) { throw ('there is no button called "' + $label + '" on screen') }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    return $button
}
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Shoot($window, $name) {
    # A picture of this window can also catch whatever overlaps it, so it is
    # only taken when it is explicitly asked for.
    if ($env:APPSTUDIO_UI_SHOTS -ne '1') { return $null }
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    return [AppStudio.Capture]::Crop($rect, $masks, (Join-Path $shotDir ($name + '.png')), $handle)
}
function Editor-Text($window) {
    $box = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 15000
    if ($null -eq $box) {
        # Say what was on screen instead. "It is not there" on its own sends the
        # next person looking in the wrong place.
        $seen = @()
        foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Edit)) { $seen += ('"' + $item.Current.Name + '"') }
        throw ('the code editor is not on screen. Edit controls found: ' + (($seen -join ', ')) + '. Screen says: ' + (Screen-Text $window))
    }
    return $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Screen-Text($window) {
    $texts = @()
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) { $texts += $item.Current.Name }
    return ($texts -join ' | ')
}
function Set-Board([string]$text) {
    for ($try = 0; $try -lt 8; $try++) {
        try { [Windows.Forms.Clipboard]::SetText($text); return } catch { Start-Sleep -Milliseconds 200 }
    }
    throw 'the clipboard would not accept the answer this test wanted to paste'
}
function Get-Board() {
    for ($try = 0; $try -lt 8; $try++) {
        try { if ([Windows.Forms.Clipboard]::ContainsText()) { return [Windows.Forms.Clipboard]::GetText() } } catch { }
        Start-Sleep -Milliseconds 200
    }
    return ''
}

# One recording, seeded into the product's own store, with addresses good enough
# for both languages to have something to say about it.
function Seed($root) {
    $session = [AppStudio.SessionStore]::Create($root, 'record', 'code screen fixture')
    $session.ValuePolicy = 'recordText'
    # One of the two files the request tells an assistant to read is the picture
    # document, and there is no picture document without a picture. The fixture
    # has one so the handover this test drives is a real one.
    New-Item -ItemType Directory -Path $session.ShotsFolder -Force | Out-Null
    $shot = Join-Path $session.ShotsFolder 'S1.png'
    $bitmap = New-Object Drawing.Bitmap(320, 240)
    try {
        $g = [Drawing.Graphics]::FromImage($bitmap)
        try { $g.Clear([Drawing.Color]::White); $g.FillRectangle([Drawing.Brushes]::SteelBlue, 20, 20, 140, 70) } finally { $g.Dispose() }
        $bitmap.Save($shot, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $screen = New-Object AppStudio.ScreenRecord
    $screen.ScanId = 'sc-1'; $screen.ScreenId = 'S1'; $screen.Title = 'Fixture Window'; $screen.ClassName = 'FixtureWindow'
    $screen.Rect = New-Object AppStudio.RectValue; $screen.Rect.Width = 900; $screen.Rect.Height = 700
    $screen.ShotFile = $shot; $screen.CapturedAt = [DateTimeOffset]::Now; $screen.CaptureMethod = 'BitBlt'
    $screen.Sha256 = (Get-FileHash $shot -Algorithm SHA256).Hash
    $session.Screens.Screens.Add($screen)
    $null = [AppStudio.SessionStore]::Append($session, 'screens', $screen.ToJson())
    for ($index = 1; $index -le 3; $index++) {
        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.At = [DateTimeOffset]::Now; $step.OffsetMs = $index * 800; $step.GapMs = 300
        $step.AppName = 'fixture'; $step.WindowTitle = 'Fixture Window'; $step.WindowClass = 'FixtureWindow'
        $step.Rect = New-Object AppStudio.RectValue
        $step.Rect.X = 40; $step.Rect.Y = 60 * $index; $step.Rect.Width = 90; $step.Rect.Height = 24
        $step.Point = New-Object AppStudio.PointValue; $step.Point.X = 85; $step.Point.Y = 60 * $index + 12
        $step.Confidence = 'high'
        if ($index -eq 2) {
            $step.Kind = 'textInput'; $step.ElementLabel = 'Edit "Name"'; $step.ControlType = 'Edit'
            $step.ValueKind = 'text'; $step.Value = 'hello'; $step.ValueLength = 5
        } else {
            $step.Kind = 'click'; $step.ElementLabel = 'Button "Go ' + $index + '"'; $step.ControlType = 'Button'
        }
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'uia.automationId'; $locator.AutomationId = 'ctl' + $index; $locator.ControlType = $step.ControlType; $locator.Confidence = 'high'
        $step.Locators.Add($locator)
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'win32.ctrlId'; $locator.CtrlId = 1000 + $index; $locator.ClassName = $step.ControlType; $locator.Confidence = 'medium'
        $step.Locators.Add($locator)
        $session.Steps.Add($step)
        $null = [AppStudio.SessionStore]::Append($session, 'steps', $step.ToJson())
    }
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session.Folder
}

try {
    $seedFolder = Seed $root
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','180000') -PassThru -WindowStyle Hidden

    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1500
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $null = Shoot $window '01-launcher'

    # --- 1. the code screen is reached from a result, not from the launcher ---
    if ($null -ne (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'detail-code.txt' 'Edit as code'))) {
        throw 'the launcher already offers the code screen before anything has been recorded'
    }
    $null = Press $window (Message 'compact-results.txt' 'Results')
    $list = (All-Of $window ([System.Windows.Automation.ControlType]::List))[0]
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -lt 1) { throw 'the seeded recording is not in the list' }
    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1400
    $resultRect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    $null = Shoot $window '02-result'
    $null = Press $window (Message 'detail-code.txt' 'Edit as code')
    $null = Shoot $window '03-code-powershell'

    # --- 2. it opens on the module a person edits, and it is short ------------
    $ps = Editor-Text $window
    if ($ps.Length -lt 400) { throw ('the code screen opened with almost nothing in it: ' + $ps.Length + ' characters') }
    if ($ps.IndexOf('A1', [StringComparison]::Ordinal) -lt 0) { throw 'the PowerShell on screen does not carry the recorded steps' }
    # The workflow is the procedure, not the machinery. Three recorded steps are
    # a handful of lines, and no address or literal is written into it.
    $workflowLines = @(($ps -split "`r`n|`n") | Where-Object { $_ -match "^\s*(FindWindow|InvokeElement|SetElementText|SendKeys|AskSecret|Unsupported)\s+'" })
    if ($workflowLines.Count -lt 3) { throw ('the workflow on screen has ' + $workflowLines.Count + ' step lines') }
    if ($ps.IndexOf('uia.automationId', [StringComparison]::Ordinal) -ge 0) { throw 'the workflow on screen carries an address, which belongs in RecordedFacts' }
    $parsed = [AppStudio.ScriptRun]::CheckPowerShell($ps)
    if (-not $parsed.Ok) { throw ('what the screen opened with does not parse: ' + (($parsed.Problems) -join ' / ')) }

    # --- 3. the module tree names every module of both languages --------------
    $expected = @('Workflow.ps1', 'RecordedFacts.ps1', 'RuntimeCore.ps1', 'RuntimeLocator.ps1', 'RuntimeNative.ps1',
        'Workflow.bas', 'RecordedFacts.bas', 'RuntimeCore.bas', 'RuntimeLocator.bas', 'RuntimeNative.bas')
    foreach ($module in $expected) {
        if ($null -eq (Wait-Named $window ([System.Windows.Automation.ControlType]::TreeItem) $module 8000)) {
            throw ('the module tree does not list ' + $module)
        }
    }
    $null = Shoot $window '03b-module-tree'

    # Choosing the runtime shows the runtime, and that is where the nine
    # operations live now.
    $core = Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'RuntimeCore.ps1'
    $core.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
    $runtime = Editor-Text $window
    if ($runtime -eq $ps) { throw 'choosing another module did not change what is in the editor' }
    foreach ($op in @('FindWindow', 'FocusElement', 'InvokeElement', 'SetElementText', 'ReadElementText', 'SendKeys', 'WaitGap', 'WaitIdle', 'AskSecret')) {
        if ($runtime.IndexOf($op, [StringComparison]::Ordinal) -lt 0) { throw ('the PowerShell runtime on screen is missing ' + $op) }
    }
    $back = Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.ps1'
    $back.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $ps) { throw 'going back to the workflow did not bring it back' }

    # --- 3b. both languages, same place, same buttons -------------------------
    foreach ($label in @((Message 'code-lang-ps.txt' 'PowerShell'), (Message 'code-lang-vba.txt' 'VBA'))) {
        if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::Button) $label)) {
            throw ('the code screen does not offer ' + $label)
        }
    }
    $null = Press $window (Message 'code-lang-vba.txt' 'VBA')
    $null = Shoot $window '04-code-vba'
    $vba = Editor-Text $window
    if ($vba -eq $ps) { throw 'switching the language did not change what is in the editor' }
    if ($vba.IndexOf('Attribute VB_Name', [StringComparison]::Ordinal) -lt 0) { throw 'the VBA on screen is not a module' }
    if ($vba.IndexOf('RunRecordedProcedure', [StringComparison]::Ordinal) -lt 0) { throw 'the VBA on screen has nothing to start from' }
    $null = Press $window (Message 'code-lang-ps.txt' 'PowerShell')
    if ((Editor-Text $window) -ne $ps) { throw 'switching back did not bring the PowerShell back' }

    # --- 3c. full width editing, there and back, losing nothing ---------------
    $editBox = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 15000
    $typed = $ps + "`r`n# typed before going full width"
    $editBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($typed)
    Start-Sleep -Milliseconds 700
    $null = Press $window (Message 'code-full-enter.txt' 'Edit at full width')
    Start-Sleep -Milliseconds 900
    $null = Shoot $window '04b-full-width'
    if ($null -ne (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-ai-copy.txt' 'Copy the request'))) {
        throw 'full width editing did not give the editor the work area'
    }
    if ((Editor-Text $window) -ne $typed) { throw 'going to full width lost what had been typed' }
    if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.ps1')) {
        throw 'full width editing dropped the module tree'
    }
    $null = Press $window (Message 'code-full-exit.txt' 'Leave full width editing')
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $typed) { throw 'coming back from full width lost what had been typed' }
    # Put it back the way it was, so the rest of the run compares against the
    # generated version rather than against something this test typed.
    $null = Press $window (Message 'code-baseline.txt' 'Back to the generated version')
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $ps) { throw 'the generated version could not be brought back after the full width round trip' }

    # --- 4. one copy, and nothing of the code in it ---------------------------
    $null = Press $window (Message 'code-ai-copy.txt' 'Copy the request')
    Start-Sleep -Milliseconds 1500
    $request = Get-Board
    if ($request.Length -lt 2000) { throw ('the clipboard did not get the request: ' + $request.Length + ' characters') }
    if ($request.Length -gt 20000) { throw ('the request is ' + $request.Length + ' characters, which is not one paste') }
    $match = [regex]::Match($request, '#@APPSTUDIO ([0-9a-fA-F-]{36}) SUMMARY BEGIN')
    if (-not $match.Success) { throw 'the copied request does not state the answer format with its own request id' }
    $requestId = $match.Groups[1].Value
    if ($request -match '#@APPSTUDIO [0-9a-fA-F-]{36} REQUEST ') { throw 'the request still numbers itself as one of several copies' }
    foreach ($section in @('## The two files attached with this message', '## What is being asked', '## The modules', '## How to answer')) {
        if ($request.IndexOf($section, [StringComparison]::Ordinal) -lt 0) { throw ('the copied request is missing ' + $section) }
    }
    foreach ($name in @('session.md', 'screens.pdf')) {
        if ($request.IndexOf($name, [StringComparison]::Ordinal) -lt 0) { throw ('the copied request does not name the attachment ' + $name) }
    }
    foreach ($leak in @('Set-StrictMode', 'Attribute VB_Name', 'uia.automationId')) {
        if ($request.IndexOf($leak, [StringComparison]::Ordinal) -ge 0) { throw ('the copied request embeds ' + $leak + ', which belongs in the attachment') }
    }
    # The two files the request tells the assistant to read have to be there.
    $aiFolder = Join-Path (Join-Path $seedFolder 'out') 'ai'
    foreach ($name in @('session.md', 'screens.pdf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $aiFolder $name))) { throw ('the request was copied although ' + $name + ' was not written') }
    }
    $attached = [IO.File]::ReadAllText((Join-Path $aiFolder 'session.md'), (New-Object Text.UTF8Encoding($false)))
    if ($attached.IndexOf('## 10. The automation as it stands', [StringComparison]::Ordinal) -lt 0) {
        throw 'the attached file does not carry the automation the request points at'
    }
    if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-ai-folder.txt' 'Open the folder with the two files to attach'))) {
        throw 'there is no way to get to the two files that go with the request'
    }
    $null = Shoot $window '05-request-copied'

    # --- 5. an answer to somebody else's request is refused -------------------
    $stranger = [Guid]::NewGuid().ToString('D')
    Set-Board (@(
        ('#@APPSTUDIO ' + $stranger + ' BEGIN powershell Workflow'),
        'Write-Output ''this must never be applied''',
        ('#@APPSTUDIO ' + $stranger + ' END powershell Workflow'),
        ('#@APPSTUDIO ' + $stranger + ' COMPLETE 1')) -join "`r`n")
    $null = Press $window (Message 'code-ai-paste.txt' 'Take the answer in from the clipboard')
    Start-Sleep -Milliseconds 700
    if ((Editor-Text $window) -ne $ps) { throw 'an answer to a different request reached the editor' }
    $refusalText = Screen-Text $window
    if ($refusalText.IndexOf((Message 'code-intake-refused.txt' 'not taken in'), [StringComparison]::Ordinal) -lt 0) {
        throw 'a refused answer was not reported on screen'
    }
    if ($refusalText.IndexOf((Message 'intake-otherRequest.txt' 'different request'), [StringComparison]::Ordinal) -lt 0) {
        throw 'the refusal did not say why'
    }
    $null = Shoot $window '06-stale-answer-refused'

    # --- 6. a real answer is shown as a difference before it replaces anything -
    $answerBody = @('# rewritten by the assistant', 'FindWindow -Class ''FixtureWindow'' -Title ''Fixture Window''', 'WaitIdle -BudgetMs 2500')
    Set-Board (@(
        ('#@APPSTUDIO ' + $requestId + ' SUMMARY BEGIN'),
        'replaced the body so the difference is obvious',
        ('#@APPSTUDIO ' + $requestId + ' SUMMARY END'),
        ('#@APPSTUDIO ' + $requestId + ' BEGIN powershell Workflow')) +
        $answerBody +
        @(('#@APPSTUDIO ' + $requestId + ' END powershell Workflow'),
        ('#@APPSTUDIO ' + $requestId + ' COMPLETE 1')) -join "`r`n")
    $null = Press $window (Message 'code-ai-paste.txt' 'Take the answer in from the clipboard')
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $ps) { throw 'the answer was written straight into the editor without being shown first' }
    $readyText = Screen-Text $window
    if ($readyText.IndexOf((Message 'code-intake-ready.txt' 'ready to be looked at'), [StringComparison]::Ordinal) -lt 0) {
        throw 'the screen does not say an answer is waiting to be looked at'
    }
    $apply = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-diff-apply.txt' 'Take this in')
    if ($null -eq $apply) { throw 'there is no way to accept the difference' }
    if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-diff-drop.txt' 'Leave it'))) {
        throw 'there is no way to refuse the difference'
    }
    $null = Shoot $window '07-difference'

    # --- 7. accepted, and then put back --------------------------------------
    $apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    $applied = Editor-Text $window
    if ($applied.IndexOf('rewritten by the assistant', [StringComparison]::Ordinal) -lt 0) { throw 'accepting the difference did not change the editor' }
    $null = Shoot $window '08-applied'
    $null = Press $window (Message 'code-undo.txt' 'Undo the last change taken in')
    Start-Sleep -Milliseconds 900
    $undone = Editor-Text $window
    if ($undone -ne $ps) { throw 'undoing did not bring back what was there before' }
    $null = Shoot $window '09-undone'

    # --- 8. the generated version is always reachable -------------------------
    $null = Press $window (Message 'code-baseline.txt' 'Back to the generated version')
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $ps) { throw 'the generated version could not be brought back' }

    # --- 9. leaving goes back one step, not all the way out -------------------
    $null = Press $window (Message 'topbar-back.txt' 'Back')
    Start-Sleep -Milliseconds 1200
    $backRect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($backRect.Width -ne $resultRect.Width -or $backRect.Height -ne $resultRect.Height) {
        throw ('leaving the code screen did not land on the result screen: ' + $backRect.Width + 'x' + $backRect.Height)
    }
    if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'detail-code.txt' 'Edit as code'))) {
        throw 'the result screen is not what leaving the code screen landed on'
    }
    $null = Shoot $window '10-back-on-result'

    $shots = @(Get-ChildItem -LiteralPath $shotDir -Filter '*.png' -ErrorAction SilentlyContinue)
    Write-Output ('PASS test-code-ui psChars=' + $ps.Length + ' vbaChars=' + $vba.Length + ' requestChars=' + $request.Length +
        ' copies=1 staleRefused=1 diffBeforeApply=1 applied=1 undone=1 baseline=1 back=result shots=' + $shots.Count)
} finally {
    if ($null -ne $app) { try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $app.HasExited) { $app.Kill() } } catch { } }
    if ($null -ne $seedFolder -and (Test-Path -LiteralPath $seedFolder)) { Remove-Item -LiteralPath $seedFolder -Recurse -Force -ErrorAction SilentlyContinue }
    if ($null -ne $restore) { try { [Windows.Forms.Clipboard]::SetText($restore) } catch { } }
}
