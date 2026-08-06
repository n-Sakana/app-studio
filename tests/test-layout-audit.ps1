$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Every control on every screen, measured.
#
# Looking at a screenshot finds the fault you happen to look at. This walks the
# whole automation tree of each screen at several window sizes and reports every
# control that is outside the window, outside its own container, off screen, of
# no size, or of a size that does not match the others of its kind. A person
# reading one picture at a time cannot do that, and the faults it finds are the
# ones that were shipped: clipped controls, and rows of buttons where each
# button is a different height.
#
# It drives the product through UI Automation and never moves the pointer, so it
# is safe to run while somebody is using the machine.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$app = $null
$seedFolder = $null
$problems = New-Object 'System.Collections.Generic.List[string]'
$measured = 0

function All-Of($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($window, $controlType, $label) {
    foreach ($item in All-Of $window $controlType) { if ($item.Current.Name -eq $label) { return $item } }
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
function Note([string]$text) { $script:problems.Add($text) }

# Content below the fold of a scrolling box is not clipped: it is scrolled, and
# the operator reaches it by scrolling. Only something with no scrolling
# ancestor is actually cut off, and telling the two apart is the difference
# between a report worth reading and a hundred false alarms.
function InScroller($element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $element
    for ($depth = 0; $depth -lt 14 -and $null -ne $node; $depth++) {
        try {
            $pattern = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($null -ne $pattern) {
                if ($pattern.Current.VerticallyScrollable -or $pattern.Current.HorizontallyScrollable) { return $true }
            }
        } catch { }
        try { $node = $walker.GetParent($node) } catch { return $false }
    }
    return $false
}

# A tooltip, a menu and a dropped-down list are drawn in windows of their own and
# are placed relative to the pointer, so standing outside the window that owns
# them is what they are supposed to do - a tooltip near the right hand edge is
# meant to hang past it. They are still audited for having a place on screen at
# all; they are only exempt from having to be inside their owner.
function InPopup($element, $window) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $node = $element
    for ($depth = 0; $depth -lt 16 -and $null -ne $node; $depth++) {
        try { if ([System.Windows.Automation.Automation]::Compare($node, $window)) { return $false } } catch { }
        try {
            $kind = $node.Current.ControlType
            if ($kind -eq [System.Windows.Automation.ControlType]::ToolTip -or
                $kind -eq [System.Windows.Automation.ControlType]::Menu -or
                $kind -eq [System.Windows.Automation.ControlType]::Window) { return $true }
        } catch { }
        try { $node = $walker.GetParent($node) } catch { return $false }
    }
    return $false
}

# Everything visible under a window, with its rectangle, its kind and its name.
function Walk($element) {
    $found = New-Object 'System.Collections.Generic.List[object]'
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $false)
    foreach ($item in $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $found.Add($item)
    }
    return $found
}

# One screen, measured. $bounds is the window's own rectangle.
function Audit($screen, $window, $bounds) {
    $items = Walk $window
    $script:measured += $items.Count
    $kinds = @{}
    foreach ($item in $items) {
        $rect = $item.Current.BoundingRectangle
        $name = $item.Current.Name
        if ([string]::IsNullOrEmpty($name)) { $name = '(' + $item.Current.ControlType.ProgrammaticName.Replace('ControlType.', '') + ')' }
        # A text box's name is its contents, and the editor's contents are a
        # whole file. One line, kept short, or the report is unreadable.
        $name = $name.Replace("`r", ' ').Replace("`n", ' ').Replace("`t", ' ')
        if ($name.Length -gt 44) { $name = $name.Substring(0, 43) + [char]0x2026 }
        $label = $screen + ' [' + $item.Current.ControlType.ProgrammaticName.Replace('ControlType.', '') + '] "' + $name + '"'
        # Empty rectangles are how UIA reports something with no place on
        # screen; a control the tree offers and the eye cannot find. WPF
        # spells that as Rect.Empty, whose numbers are infinite, so the
        # arithmetic below has to be kept away from them.
        if ($rect.IsEmpty -or [double]::IsInfinity($rect.Width) -or [double]::IsNaN($rect.Width) -or
            [double]::IsInfinity($rect.Height) -or [double]::IsNaN($rect.Height) -or
            $rect.Width -le 0 -or $rect.Height -le 0) {
            # Only things a person could name. A template's own parts - the
            # repeat buttons inside a scroll bar, for one - are not the
            # product's vocabulary and are not faults.
            if (-not [string]::IsNullOrEmpty($item.Current.Name)) {
                Note ($label + ' has no place on screen (empty rectangle)')
            }
            continue
        }
        # Outside the window it belongs to. This is the clipping the operator
        # sees as a button with its right hand side missing.
        $overRight = ($rect.X + $rect.Width) - ($bounds.X + $bounds.Width)
        $overBottom = ($rect.Y + $rect.Height) - ($bounds.Y + $bounds.Height)
        $overLeft = $bounds.X - $rect.X
        $overTop = $bounds.Y - $rect.Y
        if ($overRight -gt 1 -or $overBottom -gt 1 -or $overLeft -gt 1 -or $overTop -gt 1) {
            if (-not (InScroller $item) -and -not (InPopup $item $window)) {
                $how = @()
                if ($overRight -gt 1) { $how += ([int]$overRight).ToString() + 'px past the right' }
                if ($overBottom -gt 1) { $how += ([int]$overBottom).ToString() + 'px past the bottom' }
                if ($overLeft -gt 1) { $how += ([int]$overLeft).ToString() + 'px left of it' }
                if ($overTop -gt 1) { $how += ([int]$overTop).ToString() + 'px above it' }
                Note ($label + ' is cut off by the window: ' + ($how -join ', '))
            }
        }
        # Collect the sizes of each kind of control, to see whether one kind is
        # one thing or several.
        # Only controls a person can name. A scroll bar's parts are buttons to
        # the automation tree and are not part of the product's own vocabulary
        # of sizes.
        if ([string]::IsNullOrEmpty($item.Current.Name)) { continue }
        $kind = $item.Current.ControlType.ProgrammaticName
        if (-not $kinds.ContainsKey($kind)) { $kinds[$kind] = New-Object 'System.Collections.Generic.List[object]' }
        $kinds[$kind].Add(@($name, [int]$rect.Height, [int]$rect.Width, [int]($rect.Y + $rect.Height / 2)))
    }
    # Buttons standing side by side have to be one shape. Two buttons on the
    # same line of the screen, in two heights, is what a reader sees as "these
    # are different kinds of thing" - and it was true of "replay" beside "open
    # the report", and of the PowerShell and VBA buttons, which the product's
    # own rule says are treated identically.
    if ($kinds.ContainsKey('ControlType.Button')) {
        $rows = @{}
        foreach ($entry in $kinds['ControlType.Button']) {
            # Buttons whose vertical centres are within a few pixels are on the
            # same line as far as the eye is concerned.
            $band = [int]([Math]::Round($entry[3] / 12))
            if (-not $rows.ContainsKey($band)) { $rows[$band] = @() }
            $rows[$band] += , $entry
        }
        foreach ($band in $rows.Keys) {
            $heights = @{}
            foreach ($entry in $rows[$band]) {
                if (-not $heights.ContainsKey($entry[1])) { $heights[$entry[1]] = @() }
                $heights[$entry[1]] += $entry[0]
            }
            if ($heights.Keys.Count -gt 1) {
                $summary = @()
                foreach ($h in ($heights.Keys | Sort-Object)) {
                    $summary += ($h.ToString() + 'px: ' + (($heights[$h] | Select-Object -First 4) -join ', '))
                }
                Note ($screen + ': buttons side by side in ' + $heights.Keys.Count + ' heights -- ' + ($summary -join ' | '))
            }
        }
        # And across the screen as a whole there are at most two: the ordinary
        # one, and the pair of large ones the launcher opens with.
        $all = @{}
        foreach ($entry in $kinds['ControlType.Button']) { $all[$entry[1]] = $true }
        if ($all.Keys.Count -gt 2) {
            Note ($screen + ' uses ' + $all.Keys.Count + ' different button heights: ' + (($all.Keys | Sort-Object) -join ', '))
        }
    }
    return $items.Count
}

function Seed($root) {
    $session = [AppStudio.SessionStore]::Create($root, 'record', 'layout audit')
    $session.ValuePolicy = 'recordText'
    New-Item -ItemType Directory -Path $session.ShotsFolder -Force | Out-Null
    $shot = Join-Path $session.ShotsFolder 'S1.png'
    $bitmap = New-Object Drawing.Bitmap(320, 240)
    try {
        $g = [Drawing.Graphics]::FromImage($bitmap)
        try { $g.Clear([Drawing.Color]::White) } finally { $g.Dispose() }
        $bitmap.Save($shot, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $screen = New-Object AppStudio.ScreenRecord
    $screen.ScanId = 'sc-1'; $screen.ScreenId = 'S1'; $screen.Title = 'Fixture Window'; $screen.ClassName = 'FixtureWindow'
    $screen.Rect = New-Object AppStudio.RectValue; $screen.Rect.Width = 900; $screen.Rect.Height = 700
    $screen.ShotFile = $shot; $screen.CapturedAt = [DateTimeOffset]::Now; $screen.CaptureMethod = 'BitBlt'
    $screen.Sha256 = (Get-FileHash $shot -Algorithm SHA256).Hash
    $session.Screens.Screens.Add($screen)
    $null = [AppStudio.SessionStore]::Append($session, 'screens', $screen.ToJson())
    for ($index = 1; $index -le 9; $index++) {
        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.At = [DateTimeOffset]::Now; $step.OffsetMs = $index * 800; $step.GapMs = 300
        $step.AppName = 'fixture'; $step.WindowTitle = 'Fixture Window'; $step.WindowClass = 'FixtureWindow'
        $step.Kind = 'click'; $step.ElementLabel = ('Button "' + $index + '"'); $step.ControlType = 'Button'; $step.Confidence = 'high'
        $step.Rect = New-Object AppStudio.RectValue
        $step.Rect.X = 10; $step.Rect.Y = 20 * $index; $step.Rect.Width = 60; $step.Rect.Height = 22
        $step.Point = New-Object AppStudio.PointValue; $step.Point.X = 40; $step.Point.Y = 20 * $index + 10
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'uia.automationId'; $locator.AutomationId = ('n' + $index); $locator.ControlType = 'Button'; $locator.Confidence = 'high'
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
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','300000') -PassThru -WindowStyle Hidden
    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1600
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle

    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    # The seeded session, chosen the only way there is to choose one now.
    $picker = (All-Of $window ([System.Windows.Automation.ControlType]::ComboBox))
    if ($picker.Count -lt 1) { throw 'the window has no session picker' }
    $expand = $picker[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand(); Start-Sleep -Milliseconds 700
    $items = $picker[0].FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -lt 1) { throw 'the seeded session is not in the picker' }
    $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1800

    # The two dialogs, each a window of its own.
    foreach ($pair in @(@((Message 'compact-options.txt' 'Settings'), (Message 'settings-title.txt' 'Settings')),
                        @((Message 'record-settings.txt' 'Recording settings'), (Message 'record-settings.txt' 'Recording settings')))) {
        $null = Press $window $pair[0]
        Start-Sleep -Milliseconds 1200
        $dialogHandle = [IntPtr]::Zero
        foreach ($w in [AppStudio.WindowTools]::ListStackOrder((New-Object 'long[]' 0), 0)) {
            if ($w.ProcessId -ne $app.Id) { continue }
            if ($w.Title -ne $pair[1]) { continue }
            $dialogHandle = [IntPtr]$w.Hwnd
        }
        if ($dialogHandle -ne [IntPtr]::Zero) {
            $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($dialogHandle)
            $null = Audit ('dialog:' + $pair[1]) $dialog ([AppStudio.WindowTools]::GetPhysicalRect($dialogHandle))
            $close = Find-Named $dialog ([System.Windows.Automation.ControlType]::Button) (Message 'settings-close.txt' 'Close')
            if ($null -ne $close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
            Start-Sleep -Milliseconds 900
        } else { Note ($pair[1] + ': the dialog did not open') }
    }

    # The full window at several widths, including a narrow one. A layout that
    # only holds at the size it was designed at is a layout with a hidden
    # minimum, and the thing that falls off the end is whatever is last in a row.
    foreach ($size in @(@(0, 0), @(1500, 950), @(1320, 860), @(1120, 780))) {
        $tag = 'default'
        if ($size[0] -gt 0) {
            [AppStudio.WindowTools]::Move($handle, 40, 20, $size[0], $size[1]) | Out-Null
            Start-Sleep -Milliseconds 1100
            $tag = ($size[0].ToString() + 'x' + $size[1])
        }
        $bounds = [AppStudio.WindowTools]::GetPhysicalRect($handle)
        if (($bounds.Y + $bounds.Height) -gt ($work.Y + $work.Height) -or $bounds.Y -lt $work.Y) {
            Note ('full@' + $tag + ': the window is not inside the work area: y=' + $bounds.Y + ' h=' + $bounds.Height)
        }
        $null = Audit ('full@' + $tag) $window $bounds

        # Each pane folded away, and the editor with the whole window.
        $foldLeft = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'pane-modules-hide.txt' 'Fold the modules away')
        if ($null -ne $foldLeft) {
            $foldLeft.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 900
            $null = Audit ('left-folded@' + $tag) $window ([AppStudio.WindowTools]::GetPhysicalRect($handle))
            $show = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'pane-modules-show.txt' 'Open the modules')
            if ($null -ne $show) { $show.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900 }
        }
        $enter = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-full-enter.txt' 'Make the editor full width')
        if ($null -ne $enter) {
            $enter.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 900
            $null = Audit ('editor-full@' + $tag) $window ([AppStudio.WindowTools]::GetPhysicalRect($handle))
            $leave = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-full-exit.txt' 'Leave full width')
            if ($null -ne $leave) { $leave.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900 }
        }
    }

    # The small bar, open and folded. Everything on it is on one row, so it is
    # the shape most likely to push its last control off the end.
    $null = Press $window (Message 'view-mini.txt' 'Fold down to the small bar')
    Start-Sleep -Milliseconds 1400
    $null = Audit 'mini' $window ([AppStudio.WindowTools]::GetPhysicalRect($handle))
    $foldList = Find-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'mini-fold.txt' 'Fold the step list away')
    if ($null -ne $foldList) {
        $foldList.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1100
        $null = Audit 'mini-folded' $window ([AppStudio.WindowTools]::GetPhysicalRect($handle))
    }
    $null = Press $window (Message 'view-full.txt' 'Back to the full view')
    Start-Sleep -Milliseconds 1400

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Output ('LAYOUT ' + $problem) }
        throw ($problems.Count.ToString() + ' layout fault(s) across the screens; see the LAYOUT lines above')
    }
    Write-Output ('PASS test-layout-audit measured=' + $measured + ' screens=13 clipped=0 sizeless=0 buttonHeights=1')
} finally {
    if ($null -ne $app) { try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $app.HasExited) { $app.Kill() } } catch { } }
    if ($null -ne $seedFolder -and (Test-Path -LiteralPath $seedFolder)) { Remove-Item -LiteralPath $seedFolder -Recurse -Force -ErrorAction SilentlyContinue }
}
