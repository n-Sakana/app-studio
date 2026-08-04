$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the real product window through UI Automation. The physical pointer is
# never moved and no key is ever sent to anything on the desktop, so this test is
# safe to run while somebody is using the machine.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$shotDir = Join-Path $PSScriptRoot '.build\ui-flow'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
$app = $null

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
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Shoot($window, $name) {
    # A picture of this window also shows whatever else the operator has open,
    # so it is only taken when it is explicitly asked for.
    if ($env:APPSTUDIO_UI_SHOTS -ne '1') { return $null }
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    return [AppStudio.Capture]::Crop($rect, $masks, (Join-Path $shotDir ($name + '.png')), $handle)
}

# One session per state the result screen can be in, written into the product's
# own store so the window shows what it would show for a real recording. They
# are removed again at the end.
#
# A screen is only as good as its worst state, so all five are built and looked
# at: everything worked, some of it did not, most of it did not, nothing was
# recorded, and everything has a very long name.
function New-SeedRect($x, $y, $w, $h) {
    $rect = New-Object AppStudio.RectValue
    $rect.X = $x; $rect.Y = $y; $rect.Width = $w; $rect.Height = $h
    return $rect
}
function New-SeedPicture($session, $id) {
    $folder = $session.ShotsFolder
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $path = Join-Path $folder ($id + '.png')
    $bitmap = New-Object Drawing.Bitmap(160, 120)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([Drawing.Color]::FromArgb(238, 242, 246)) } finally { $graphics.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return $path
}
function Seed-State($root, $title, $state) {
    $session = [AppStudio.SessionStore]::Create($root, 'record', $title)
    $long = ($state -eq 'long')
    $windowTitle = if ($long) { 'A window title that will not stop ' * 10 } else { 'Fixture window' }
    $screenCount = if ($state -eq 'many') { 4 } else { 1 }
    if ($state -ne 'empty') {
        for ($index = 1; $index -le $screenCount; $index++) {
            $screen = New-Object AppStudio.ScreenRecord
            $screen.ScanId = 's'; $screen.ScreenId = ('S' + $index); $screen.Title = $windowTitle; $screen.ClassName = 'FixtureWindow'
            $screen.Rect = New-SeedRect 0 0 800 600
            if ($state -ne 'ok') { $screen.ShotProblem = 'SHOT-FAILED: the window was covered when the shutter fired.' }
            # "has a picture" means the file is on disk, so the complete state
            # has to have one.
            else { $screen.ShotFile = New-SeedPicture $session $screen.ScreenId }
            $session.Screens.Screens.Add($screen)
            $null = [AppStudio.SessionStore]::Append($session, 'screens', $screen.ToJson())
        }
    }
    $node = New-Object AppStudio.ScanNode
    $node.NodeId = 0; $node.ScreenId = 'S1'
    $node.Name = if ($long) { 'An element name that goes on for a very long time ' * 6 } else { 'Save' }
    $node.AutomationId = 'SaveButton'; $node.ControlType = 'Button'; $node.ClassName = 'Button'; $node.CtrlId = 1001
    $node.Rect = New-SeedRect 10 10 60 24
    if ($state -ne 'empty') {
        $session.Elements.Add($node)
        $null = [AppStudio.SessionStore]::Append($session, 'elements', [AppStudio.ScanJson]::Node($node, 's', 0))
        $session.Screens.Screens[0].ComponentIds.Add('E0')
    }
    $siblings = New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'
    $siblings.Add($node)
    $stepCount = 0
    if ($state -eq 'ok') { $stepCount = 3 } elseif ($state -eq 'partial') { $stepCount = 2 } elseif ($state -eq 'many') { $stepCount = 12 } elseif ($state -eq 'long') { $stepCount = 1 }
    for ($index = 1; $index -le $stepCount; $index++) {
        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.At = [DateTimeOffset]::Now; $step.OffsetMs = $index * 900; $step.GapMs = 700
        $step.Kind = 'click'; $step.AppName = 'FixtureApp'; $step.WindowTitle = $windowTitle; $step.WindowClass = 'FixtureWindow'
        $step.Button = 'left'; $step.Dpi = 96
        $step.Point = New-Object AppStudio.PointValue; $step.Point.X = 100; $step.Point.Y = 200
        $step.EffectSummary = if ($long) { 'the application reported something at considerable length ' * 8 } else { 'the button reported that it was pressed' }
        # In the failing states nothing identifies what the step acted on, which
        # is what makes them unreplayable.
        if ($state -eq 'ok' -or $state -eq 'long' -or ($state -eq 'partial' -and $index -eq 1)) {
            $step.ElementLabel = 'Button "' + $node.Name + '"'
            $step.Locators = [AppStudio.LocatorBuilder]::Build($node, $session.Screens.Screens[0].Rect, $siblings)
        } else {
            $step.ElementLabel = '(unidentified element)'
            $step.Unavailable.Add('no-identifying-locator: nothing addresses this element.')
        }
        if ($state -eq 'many') {
            $outcome = New-Object AppStudio.ReplayOutcome
            $outcome.State = 'not-found'; $outcome.Reason = 'No window matching FixtureApp / FixtureWindow is open.'
            $outcome.WaitedMs = 120; $outcome.SettleMs = 240
            $step.LastReplay = $outcome
        }
        $session.Steps.Add($step)
        $null = [AppStudio.SessionStore]::Append($session, 'steps', $step.ToJson())
    }
    if ($state -eq 'partial') { $session.AddLimit('[uia] UIA-TIMEOUT: the tree walk did not finish inside its allowance.') }
    if ($state -eq 'many') {
        for ($index = 1; $index -le 9; $index++) {
            $session.AddLimit('[msaa] MSAA-BUDGET: layer ' + $index + ' stopped after its allowance and left part of the window undescribed.')
        }
    }
    if ($long) { $session.AddLimit(('A limit whose explanation is far longer than any column can hold ' * 8)) }
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session.Folder
}
function Descends-From($child, $ancestors) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $parent = $walker.GetParent($child)
    while ($null -ne $parent) {
        foreach ($candidate in $ancestors) {
            if ([System.Windows.Automation.Automation]::Compare($candidate, $parent)) { return $true }
        }
        $parent = $walker.GetParent($parent)
    }
    return $false
}

# Newest first in the list, so they are seeded worst-last and the partial one
# is what the screen opens on.
$seedStates = @('long', 'empty', 'many', 'ok', 'partial')
# The list is newest first, so it reads back the other way round.
$listStates = @('partial', 'ok', 'many', 'empty', 'long')
$seedFolders = @()
try {
    foreach ($state in $seedStates) { $seedFolders += (Seed-State $root ('ui flow ' + $state) $state) }
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','120000') -PassThru -WindowStyle Hidden

    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1500
    $null = Shoot $window 'home'

    # --- 1. the launcher is small and offers only what starts a job -------
    $snapLabel = Message 'home-snap.txt' 'Snap'
    $recordLabel = Message 'home-record.txt' 'Record'
    $snap = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) $snapLabel 15000
    if ($null -eq $snap) { throw 'The snap button is not on the launcher.' }
    $record = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $recordLabel
    if ($null -eq $record) { throw 'The record button is not on the launcher.' }
    if ($snap.Current.IsOffscreen -or $record.Current.IsOffscreen) { throw 'A main action is not actually on screen.' }
    foreach ($main in @($snap, $record)) { $null = $main.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) }

    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    if ($rect.Width -gt ($work.Width * 0.65) -or $rect.Height -gt ($work.Height * 0.65)) {
        throw ('The launcher is too large for a starting screen: ' + $rect.Width + 'x' + $rect.Height)
    }
    # A launcher carries no result area: no session list, no element table.
    if ((All-Of $window ([System.Windows.Automation.ControlType]::List)).Count -ne 0) {
        throw 'The launcher is carrying a result list before anything has been acquired.'
    }

    # Nothing from the withdrawn assistant workflow may still be reachable.
    $buttons = @()
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Button)) { $buttons += $item.Current.Name }
    foreach ($gone in @('AI', 'Plan', 'Case', 'Import', 'Answer')) {
        foreach ($name in $buttons) {
            if ($name -and $name.IndexOf($gone, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw ('A control from the withdrawn assistant workflow is still on screen: ' + $name)
            }
        }
    }

    # --- 2. the settings are a dialog, not a permanent panel --------------
    $optionsLabel = Message 'compact-options.txt' 'Settings'
    $options = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $optionsLabel
    if ($null -eq $options) { throw 'The launcher has no way to reach the settings.' }
    $options.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    # An owned dialog is not listed among the desktop's children, so it is found
    # by its window handle instead of by walking the automation tree.
    $dialog = $null
    $limit = [DateTime]::UtcNow.AddSeconds(12)
    $wanted = Message 'settings-title.txt' 'Detailed settings'
    while ($null -eq $dialog -and [DateTime]::UtcNow -lt $limit) {
        foreach ($w in [AppStudio.WindowTools]::ListStackOrder((New-Object 'long[]' 0), 0)) {
            if ($w.ProcessId -ne $app.Id) { continue }
            if ($w.Title -ne $wanted) { continue }
            $dialog = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$w.Hwnd)
        }
        if ($null -eq $dialog) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $dialog) { throw 'The settings dialog did not open.' }
    $dialogRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$dialog.Current.NativeWindowHandle)
    if ($dialogRect.Height -gt ($work.Height * 0.9)) { throw ('The settings dialog is taller than the desktop: ' + $dialogRect.Height) }
    $null = Shoot $dialog 'settings'

    $writeLabel = Message 'settings-write.txt' 'Let replay act on the real application'
    $permission = Find-Named $dialog ([System.Windows.Automation.ControlType]::CheckBox) $writeLabel
    if ($null -eq $permission) { throw 'The replay permission switch is missing.' }
    $toggle = $permission.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'The replay permission is on before anyone asked for it.' }

    $routeNames = @()
    foreach ($combo in All-Of $dialog ([System.Windows.Automation.ControlType]::ComboBox)) {
        $expandCombo = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expandCombo.Expand()
        Start-Sleep -Milliseconds 250
        foreach ($item in All-Of $combo ([System.Windows.Automation.ControlType]::ListItem)) { $routeNames += $item.Current.Name }
        $expandCombo.Collapse()
        Start-Sleep -Milliseconds 150
    }
    $joined = ($routeNames -join ' ')
    foreach ($needed in @('UI Automation', 'SendInput')) {
        if ($joined.IndexOf($needed, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw ('A route is missing from the settings: ' + $needed) }
    }
    if ($joined -match '(?i)\bMSAA\b') { throw 'MSAA is offered as a way to carry an operation out.' }
    $close = Find-Named $dialog ([System.Windows.Automation.ControlType]::Button) (Message 'settings-close.txt' 'Close')
    if ($null -eq $close) { throw 'The settings dialog cannot be closed.' }
    $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700

    # --- 3. the result screen is a different, larger shape ----------------
    $results = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'compact-results.txt' 'Results')
    if ($null -eq $results) { throw 'The launcher has no way to reach the records.' }
    $results.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1500
    $resultRect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($resultRect.Width -le $rect.Width -or $resultRect.Height -le $rect.Height) {
        throw ('The result screen did not grow: launcher ' + $rect.Width + 'x' + $rect.Height + ' result ' + $resultRect.Width + 'x' + $resultRect.Height)
    }
    if ((All-Of $window ([System.Windows.Automation.ControlType]::List)).Count -lt 1) { throw 'The result screen has no session list.' }
    $null = Shoot $window 'result'

    # --- 3a. the conclusion is readable without opening anything ----------
    $list = (All-Of $window ([System.Windows.Automation.ControlType]::List))[0]
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -lt 1) { throw 'The seeded session is not in the list.' }
    if ($items.Count -lt $seedStates.Count) { throw ('only ' + $items.Count + ' of the seeded sessions are listed') }
    # Every state the screen can be in, looked at in turn. A screen is only as
    # good as its worst state.
    $seen = @()
    for ($stateIndex = 0; $stateIndex -lt $seedStates.Count; $stateIndex++) {
        $items[$stateIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 1200
        $stateTexts = @()
        foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) { $stateTexts += $item.Current.Name }
        $stateJoined = ($stateTexts -join ' | ')
        $words = @{}
        foreach ($name in @('state-ok.txt', 'state-partial.txt', 'state-failed.txt', 'state-empty.txt')) {
            $words[$name] = Message $name $name
        }
        $found = $null
        foreach ($name in $words.Keys) { if ($stateJoined.IndexOf($words[$name], [StringComparison]::Ordinal) -ge 0) { $found = $name } }
        if ($null -eq $found) { throw ('a session shows no state at all: ' + $items[$stateIndex].Current.Name) }
        $seen += $found
        # The next move is offered whatever the state, and the details are one
        # layer deep in every one of them.
        if ($stateJoined.IndexOf((Message 'detail-next.txt' 'Next'), [StringComparison]::Ordinal) -lt 0) {
            throw ('a session offers no next action: ' + $items[$stateIndex].Current.Name)
        }
        $stateFolds = @()
        foreach ($group in All-Of $window ([System.Windows.Automation.ControlType]::Group)) {
            $pattern = $null
            try { $pattern = $group.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
            if ($null -ne $pattern) { $stateFolds += $group }
        }
        foreach ($fold in $stateFolds) {
            if (Descends-From $fold $stateFolds) { throw ('a fold is inside another fold in state ' + $listStates[$stateIndex]) }
        }
        $null = Shoot $window ('result-' + $listStates[$stateIndex])
    }
    if (@($seen | Sort-Object -Unique).Count -lt 3) { throw ('the seeded sessions all read as the same state: ' + ($seen -join ',')) }

    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1500
    $null = Shoot $window 'result-detail'

    $texts = @()
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) { $texts += $item.Current.Name }
    $joinedText = ($texts -join ' | ')
    foreach ($needed in @((Message 'state-partial.txt' 'partly complete'), (Message 'detail-next.txt' 'Next'), (Message 'detail-more.txt' 'Details'))) {
        if ($joinedText.IndexOf($needed, [StringComparison]::Ordinal) -lt 0) {
            throw ('the result screen does not show "' + $needed + '" before anything is opened')
        }
    }
    # Whatever is wrong has to be on the first screen with its count.
    if ($joinedText.IndexOf((Message 'verdict-warn-limits.txt' 'thing(s) could not be obtained'), [StringComparison]::Ordinal) -lt 0) {
        throw 'the result screen hides what could not be obtained instead of counting it'
    }

    # --- 3b. one layer of folding, never two ------------------------------
    $folds = @()
    foreach ($group in All-Of $window ([System.Windows.Automation.ControlType]::Group)) {
        $pattern = $null
        try { $pattern = $group.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
        if ($null -ne $pattern) { $folds += $group }
    }
    if ($folds.Count -lt 2) { throw ('the result screen has ' + $folds.Count + ' fold(s); the details are not reachable') }
    foreach ($fold in $folds) {
        if (Descends-From $fold $folds) { throw 'a fold is inside another fold on the result screen' }
    }
    # Opening one produces a list, not more things to open.
    $folds[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 900
    $null = Shoot $window 'result-open'
    $after = @()
    foreach ($group in All-Of $window ([System.Windows.Automation.ControlType]::Group)) {
        $pattern = $null
        try { $pattern = $group.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
        if ($null -ne $pattern) { $after += $group }
    }
    foreach ($fold in $after) {
        if (Descends-From $fold $after) { throw 'opening a fold revealed another fold' }
    }

    # --- 4. and it can go back --------------------------------------------
    $back = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'topbar-back.txt' 'Back')
    if ($null -eq $back) { throw 'The result screen offers no way back to the launcher.' }
    $back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200
    $backRect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($backRect.Width -ne $rect.Width -or $backRect.Height -ne $rect.Height) {
        throw ('Going back did not restore the launcher size: ' + $backRect.Width + 'x' + $backRect.Height)
    }

    # --- 5. the window still reports its state ----------------------------
    $status = $null
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) {
        if ($item.Current.Name -and $item.Current.Name.Length -gt 6) { $status = $item.Current.Name }
    }
    if ($null -eq $status) { throw 'The window shows no status line at all.' }

    Write-Output ('PASS test-ui-flow launcher=' + $rect.Width + 'x' + $rect.Height + ' noResultAreaOnLaunch=1 settings=dialog replayPermission=off routes=uia+win32+sendInput msaaOffered=0 result=' + $resultRect.Width + 'x' + $resultRect.Height + ' conclusionFirst=1 folds=' + $folds.Count + ' foldDepth=1 states=' + (($seen | Sort-Object -Unique) -join '+') + ' back=restored withdrawnControls=0')
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $app) { $app.Dispose() }
    foreach ($folder in $seedFolders) {
        if ($null -ne $folder -and (Test-Path -LiteralPath $folder)) { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
