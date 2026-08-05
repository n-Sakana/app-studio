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
    $workflowLines = @(($ps -split "`r`n|`n") | Where-Object { $_ -match "^\s*(Runtime\.)?(FindWindow|InvokeElement|SetElementText|SendKeys|AskSecret|Unsupported)\s*\(?\s*['`"]" })
    if ($workflowLines.Count -lt 3) { throw ('the workflow on screen has ' + $workflowLines.Count + ' step lines') }
    if ($ps.IndexOf('uia.automationId', [StringComparison]::Ordinal) -ge 0) { throw 'the workflow on screen carries an address, which belongs in RecordedFacts' }
    # The engine is one program in five files, so a single module is not
    # compiled on its own here - what the screen opened with is checked for
    # being the entry point it claims to be. test-codegen compiles the set.
    foreach ($marker in @('namespace AppStudioRun', 'public static class Workflow', 'Runtime.Start(', 'Runtime.Complete()')) {
        if ($ps.IndexOf($marker, [StringComparison]::Ordinal) -lt 0) {
            throw ('what the screen opened with is not the engine entry point: ' + $marker + ' is missing')
        }
    }

    # --- 3. the module tree is a shape, and every module is reachable in it ---
    # The procedure a person edits is first and on its own; the three modules
    # that carry operations out are gathered under one heading that is shut when
    # the screen opens. Nothing is removed by being folded, so every one of the
    # ten is still reachable - after opening the heading that says it is there.
    # The tree is the project tree of the form being edited: its modules, by
    # their file names, and nothing else. The other form is chosen with the
    # button above, so listing its modules here as well - folded or not - would
    # be the same choice offered twice.
    function TreeNames($window) {
        $names = @()
        foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::TreeItem)) { $names += $item.Current.Name }
        return $names
    }
    $psModules = @('Workflow.cs', 'RecordedFacts.cs', 'RuntimeCore.cs', 'RuntimeLocator.cs', 'RuntimeNative.cs')
    $vbaModules = @('Workflow.bas', 'RecordedFacts.bas', 'RuntimeCore.bas', 'RuntimeLocator.bas', 'RuntimeNative.bas')
    $names = TreeNames $window
    foreach ($module in $psModules) {
        if ($names -notcontains $module) { throw ('the module tree does not list ' + $module + ': ' + ($names -join ', ')) }
    }
    foreach ($module in $vbaModules) {
        if ($names -contains $module) { throw ('the tree lists the other form''s module ' + $module + ' while PowerShell is the one being edited') }
    }
    if ($names.Count -ne $psModules.Count) {
        throw ('the tree holds ' + $names.Count + ' rows for ' + $psModules.Count + ' modules: ' + ($names -join ', '))
    }
    if ($names[0] -ne 'Workflow.cs') { throw ('the tree opens on ' + $names[0] + ' rather than on the procedure') }
    # A row is a file name and nothing else. The count above already says there
    # are exactly as many rows as modules; this says each row is the module's
    # name rather than a description of it dressed up as one.
    foreach ($name in $names) {
        if (-not ($name -like '*.cs')) {
            throw ('a module row is not a file name: "' + $name + '"')
        }
    }
    # Every row has to fit the pane it is in. A tree whose text runs out past
    # its own edge is a tree nobody can read the end of.
    $paneRight = 0
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::TreeItem)) {
        $ir = $item.Current.BoundingRectangle
        if (($ir.X + $ir.Width) -gt $paneRight) { $paneRight = $ir.X + $ir.Width }
    }
    $editorBox = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 15000
    if ($null -ne $editorBox -and $paneRight -gt $editorBox.Current.BoundingRectangle.X) {
        throw ('a module row runs past the module pane and under the editor: ' + [int]$paneRight + ' > ' + [int]$editorBox.Current.BoundingRectangle.X)
    }
    $null = Shoot $window '03b-module-tree'

    # Choosing the runtime shows the runtime, and that is where the nine
    # operations live now.
    $core = Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'RuntimeCore.cs'
    $core.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
    $runtime = Editor-Text $window
    if ($runtime -eq $ps) { throw 'choosing another module did not change what is in the editor' }
    foreach ($op in @('FindWindow', 'FocusElement', 'InvokeElement', 'SetElementText', 'ReadElementText', 'SendKeys', 'WaitGap', 'WaitIdle', 'AskSecret')) {
        if ($runtime.IndexOf($op, [StringComparison]::Ordinal) -lt 0) { throw ('the PowerShell runtime on screen is missing ' + $op) }
    }
    $back = Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.cs'
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

    # --- 3b2. the editor is the size of a place to read a file in -------------
    # A control being present says nothing about whether anybody can work in it.
    # These are the numbers: how much of the window the editor actually holds,
    # how many lines of the file that is, and whether the last one can be
    # reached. The window used to be 1400 by 1050 with an editor 203 tall.
    $winRect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    if ($winRect.Y -lt $work.Y) { throw ('the code window starts above the desktop: y=' + $winRect.Y) }
    if (($winRect.Y + $winRect.Height) -gt ($work.Y + $work.Height)) {
        throw ('the code window runs below the desktop: bottom=' + ($winRect.Y + $winRect.Height) + ' work=' + ($work.Y + $work.Height))
    }
    $editBox = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 15000
    $editRect = $editBox.Current.BoundingRectangle
    $share = ($editRect.Width * $editRect.Height) / ($winRect.Width * $winRect.Height)
    if ($share -lt 0.28) { throw ('the editor holds ' + [int]($share * 100) + '% of the code window') }
    if ($editRect.Height -lt 420) { throw ('the editor viewport is ' + [int]$editRect.Height + ' pixels tall') }
    if ($editRect.Width -lt 520) { throw ('the editor viewport is ' + [int]$editRect.Width + ' pixels wide') }
    # Nothing may sit on top of it, and it may not run off the window.
    if (($editRect.X + $editRect.Width) -gt ($winRect.X + $winRect.Width)) { throw 'the editor runs off the right of the window' }
    if (($editRect.Y + $editRect.Height) -gt ($winRect.Y + $winRect.Height)) { throw 'the editor runs off the bottom of the window' }

    # The last line has to be reachable and whole. Scrolling to the end of the
    # text and asking where the caret is tells us the box really goes that far.
    $value = $editBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $lineCount = ($ps -split "`r`n|`n").Count
    $textPattern = $editBox.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $visibleAtTop = $textPattern.GetVisibleRanges()
    if ($visibleAtTop.Count -lt 1) { throw 'the editor reports no visible text at all' }
    $topText = $visibleAtTop[0].GetText(-1)
    $topLines = ($topText -split "`r`n|`n").Count
    if ($topLines -lt 20) { throw ('only ' + $topLines + ' lines of ' + $lineCount + ' are visible in the editor') }

    # --- 3b3. the code is still drawn after scrolling down --------------------
    # The colour and the line numbers are painted on layers behind the text box
    # rather than into it, and a layer that is measured with the viewport's
    # height stops drawing where the viewport ends. Scrolled past the first
    # screenful, that leaves a caret moving over an empty box. No test that asks
    # whether a control exists can see this, so this one looks at the pixels.
    # The measurement is the same band of the same viewport, photographed twice:
    # once at the top of the file and once scrolled to its end. At the top the
    # band is full of code, so it says what "full of code" is worth on this
    # machine at this size; scrolled down it has to still be worth about that.
    # A layer cut off at the viewport leaves it empty.
    function Ink($handle, $rect, $path) {
        $box = New-Object AppStudio.RectValue
        $box.X = [int]$rect.X; $box.Y = [int]$rect.Y
        $box.Width = [int]$rect.Width; $box.Height = [int]$rect.Height
        $null = [AppStudio.Capture]::Crop($box, (New-Object 'AppStudio.MaskRect[]' 0), $path, $handle)
        if (-not (Test-Path -LiteralPath $path)) { throw 'the editor could not be photographed' }
        $bitmap = [Drawing.Bitmap]::FromFile($path)
        try {
            # Past the line numbers, and the last fifth of the viewport: the part
            # that only holds anything when the whole file was drawn.
            $left = [int]($bitmap.Width * 0.12)
            $top = [int]($bitmap.Height * 0.78)
            $background = $bitmap.GetPixel($bitmap.Width - 4, 4)
            $marked = 0
            for ($y = $top; $y -lt ($bitmap.Height - 3); $y++) {
                for ($x = $left; $x -lt ($bitmap.Width - 4); $x += 2) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    $delta = [Math]::Abs([int]$pixel.R - [int]$background.R) +
                        [Math]::Abs([int]$pixel.G - [int]$background.G) +
                        [Math]::Abs([int]$pixel.B - [int]$background.B)
                    if ($delta -gt 40) { $marked++ }
                }
            }
            return $marked
        } finally { $bitmap.Dispose() }
    }
    $inkAtTop = Ink $handle $editRect (Join-Path $shotDir 'editor-at-top.png')
    if ($inkAtTop -lt 100) { throw ('the editor draws almost nothing even at the top of the file: ' + $inkAtTop) }
    # Find is not a permanent bar over every file any more - it appears on
    # Ctrl+F - so the way to the end of the file here is the scroll bar, which
    # is also the way a person gets there.
    $restingFind = Find-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-find.txt' 'Find')
    if ($null -ne $restingFind -and -not $restingFind.Current.IsOffscreen) {
        throw 'the find box is on screen before anybody asked for it'
    }
    $scroll = $editBox.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
    Start-Sleep -Milliseconds 900
    $scrolled = (Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 8000).Current.BoundingRectangle
    $scrolledInk = Ink $handle $scrolled (Join-Path $shotDir 'editor-scrolled.png')
    if ($scrolledInk -lt ($inkAtTop / 4)) {
        throw ('scrolled to its last line, the bottom of the editor holds ' + $scrolledInk +
            ' marked pixels against ' + $inkAtTop + ' at the top of the same file. The colour layer is ' +
            'being cut off at the viewport, so the code stops being drawn where the first screenful ended.')
    }
    $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
    Start-Sleep -Milliseconds 400

    # --- 3c. concentrating, there and back, losing nothing --------------------
    $typed = $ps + "`r`n# typed before going full width"
    $value.SetValue($typed)
    Start-Sleep -Milliseconds 700
    $null = Press $window (Message 'code-full-enter.txt' 'Edit at full width')
    Start-Sleep -Milliseconds 900
    $null = Shoot $window '04b-full-width'
    if ($null -ne (Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-ai-copy.txt' 'Copy the request'))) {
        throw 'full width editing did not give the editor the work area'
    }
    # Concentrating has to take the rest of the window away in fact, not in
    # name: the language switch, the version buttons and the status line go, and
    # the editor is measurably larger for it.
    foreach ($gone in @((Message 'code-lang-ps.txt' 'PowerShell'), (Message 'code-baseline.txt' 'Back to the generated version'))) {
        if ($null -ne (Find-Named $window ([System.Windows.Automation.ControlType]::Button) $gone)) {
            throw ('concentrating left "' + $gone + '" on screen')
        }
    }
    $fullBox = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 15000
    $fullRect = $fullBox.Current.BoundingRectangle
    if ($fullRect.Width -le $editRect.Width) {
        throw ('concentrating did not widen the editor: ' + [int]$editRect.Width + ' -> ' + [int]$fullRect.Width)
    }
    $fullShare = ($fullRect.Width * $fullRect.Height) / ($winRect.Width * $winRect.Height)
    if ($fullShare -lt 0.45) { throw ('the concentrated editor holds ' + [int]($fullShare * 100) + '% of the window') }
    if ((Editor-Text $window) -ne $typed) { throw 'going to full width lost what had been typed' }
    if ($null -eq (Find-Named $window ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.cs')) {
        throw 'full width editing dropped the module tree'
    }
    $null = Press $window (Message 'code-full-exit.txt' 'Leave full width editing')
    Start-Sleep -Milliseconds 900
    if ((Editor-Text $window) -ne $typed) { throw 'coming back from full width lost what had been typed' }

    # --- 3d. it follows the window instead of being sized once ----------------
    foreach ($size in @(@(1400, 900), @(1750, 1010), @(1300, 800))) {
        [AppStudio.WindowTools]::Move($handle, 60, 8, $size[0], $size[1]) | Out-Null
        Start-Sleep -Milliseconds 900
        $sized = (Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 8000).Current.BoundingRectangle
        if (($sized.X + $sized.Width) -gt (60 + $size[0])) { throw ('at ' + $size[0] + 'x' + $size[1] + ' the editor runs off the right') }
        if (($sized.Y + $sized.Height) -gt (8 + $size[1])) { throw ('at ' + $size[0] + 'x' + $size[1] + ' the editor runs off the bottom') }
        if ($sized.Height -lt 300) { throw ('at ' + $size[0] + 'x' + $size[1] + ' the editor collapsed to ' + [int]$sized.Height + ' pixels') }
        if ($sized.Width -lt 380) { throw ('at ' + $size[0] + 'x' + $size[1] + ' the editor narrowed to ' + [int]$sized.Width + ' pixels') }
        # Nothing may be pushed off the bottom by the resize either.
        foreach ($name in @((Message 'code-ai-copy.txt' 'Copy the request'), (Message 'code-run.txt' 'Run'))) {
            $control = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $name
            if ($null -eq $control) { throw ('"' + $name + '" is gone at ' + $size[0] + 'x' + $size[1]) }
            $cr = $control.Current.BoundingRectangle
            if (($cr.Y + $cr.Height) -gt (8 + $size[1])) { throw ('"' + $name + '" is below the window at ' + $size[0] + 'x' + $size[1]) }
            if ($control.Current.IsOffscreen) { throw ('"' + $name + '" is offscreen at ' + $size[0] + 'x' + $size[1]) }
        }
        $null = Shoot $window ('04c-resized-' + $size[0] + 'x' + $size[1])
    }
    [AppStudio.WindowTools]::Move($handle, $winRect.X, $winRect.Y, $winRect.Width, $winRect.Height) | Out-Null
    Start-Sleep -Milliseconds 700
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
    $answerBody = @('// rewritten by the assistant', 'namespace AppStudioRun', '{', '    public static class Workflow', '    {', '        public static void Run(int settleMs)', '        {', '            Runtime.Start(settleMs);', '            Runtime.Complete();', '        }', '    }', '}')
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
        ' copies=1 staleRefused=1 diffBeforeApply=1 applied=1 undone=1 baseline=1 back=result' +
        ' editor=' + [int]$editRect.Width + 'x' + [int]$editRect.Height + ' share=' + [int]($share * 100) + '%' +
        ' concentrated=' + [int]$fullRect.Width + 'x' + [int]$fullRect.Height + ' share=' + [int]($fullShare * 100) + '%' +
        ' visibleLines=' + $topLines + '/' + $lineCount + ' inkAfterScroll=' + $scrolledInk +
        ' runtimeFolded=1 treeInPane=1 resized=3 shots=' + $shots.Count)
} finally {
    if ($null -ne $app) { try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $app.HasExited) { $app.Kill() } } catch { } }
    if ($null -ne $seedFolder -and (Test-Path -LiteralPath $seedFolder)) { Remove-Item -LiteralPath $seedFolder -Recurse -Force -ErrorAction SilentlyContinue }
    if ($null -ne $restore) { try { [Windows.Forms.Clipboard]::SetText($restore) } catch { } }
}
