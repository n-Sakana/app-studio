$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The restructured window, driven through UI Automation.
#
# The product is one working state seen two ways now, so the things that can go
# wrong are different from the things that could go wrong when it was four
# screens. This checks the ones that would be silent:
#
#   - what the window says when there is nothing in it yet, which is the first
#     thing anybody ever sees and the easiest state to leave blank;
#   - that folding to the small bar and back does not lose the session, the
#     chosen module or an unsaved edit;
#   - that an answer from an assistant becomes a difference across the middle of
#     the window with both answers on it, rather than being applied;
#   - that the replay speed is on both shapes;
#   - that a build enables the control that starts what it built.
#
# It runs against a scratch store, so it neither reads nor writes the operator's
# own recordings. The pointer is never moved and no key is sent to anything.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-shell-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
# A whole scratch copy of the product, so the window under test reads its own
# wording and writes its own sessions. Anything less is a test that either reads
# the operator's recordings - so "there is no session yet" can never be checked -
# or writes into them, which a test may not do. The acquisition workers are
# started from this folder too, so the launcher and the sources come with it.
foreach ($part in @('assets', 'src')) {
    Copy-Item -LiteralPath (Join-Path $root $part) -Destination (Join-Path $temp $part) -Recurse
}
foreach ($part in @('app-studio.ps1', 'app-studio-worker.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root $part) -Destination (Join-Path $temp $part)
}
$shotDir = Join-Path $PSScriptRoot '.build\ui-shell'
$app = $null
$restore = $null
try { if ([Windows.Forms.Clipboard]::ContainsText()) { $restore = [Windows.Forms.Clipboard]::GetText() } } catch { }

function M([string]$name, [string]$fallback) {
    $path = Join-Path $temp ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function All-Of($element, $controlType) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-Named($window, $controlType, $label) {
    foreach ($i in All-Of $window $controlType) { if ($i.Current.Name -eq $label) { return $i } }
    return $null
}
function Wait-Named($window, $controlType, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $f = Find-Named $window $controlType $label
        if ($null -ne $f) { return $f }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Screen-Text($window) {
    $t = @()
    foreach ($i in All-Of $window ([System.Windows.Automation.ControlType]::Text)) { $t += $i.Current.Name }
    return ($t -join ' | ')
}
function Press($window, $label) {
    $b = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) $label 15000
    if ($null -eq $b) { throw ('there is no control called "' + $label + '" on screen. Screen says: ' + (Screen-Text $window)) }
    $p = $null
    try { $p = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) } catch { }
    if ($null -ne $p) { $p.Invoke() } else { $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 900
    return $b
}
function Shoot($window, $name) {
    if ($env:APPSTUDIO_UI_SHOTS -ne '1') { return }
    New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
    $h = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($h)
    if ($null -eq $rect) { return }
    [AppStudio.Capture]::Crop($rect, (New-Object 'AppStudio.MaskRect[]' 0), (Join-Path $shotDir ($name + '.png')), $h) | Out-Null
}
function Start-App($baseDir) {
    $a = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $baseDir 'app-studio.ps1')) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $limit) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $a.Id)
        $f = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
        if ($null -ne $f) { Start-Sleep -Milliseconds 2500; return @{ App = $a; Win = $f } }
        Start-Sleep -Milliseconds 250
    }
    try { $a.Kill() } catch { }
    throw 'the window never appeared'
}
# A past session is reached from the picker and from nowhere else. Opening the
# product no longer puts one on screen by itself, so every check below that needs
# a session in the panes says so by choosing one here.
function Choose-Session($window, $which) {
    $pickers = @(All-Of $window ([System.Windows.Automation.ControlType]::ComboBox))
    if ($pickers.Count -lt 1) { throw 'the window has no session picker' }
    $expand = $pickers[0].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand(); Start-Sleep -Milliseconds 800
    $items = $pickers[0].FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -le $which) { throw ('the picker holds ' + $items.Count + ' sessions, not ' + ($which + 1)) }
    $items[$which].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 2200
}
function Editor-Text($window) {
    $b = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (M 'code-editor-name.txt' 'The automation, as code') 15000
    if ($null -eq $b) { throw ('the code editor is not on screen. Screen says: ' + (Screen-Text $window)) }
    return $b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
# One recording, written straight into the scratch store.
function Seed($into) {
    $session = [AppStudio.SessionStore]::Create($into, 'record', 'ui shell fixture')
    $session.ValuePolicy = 'recordText'
    $rect = New-Object AppStudio.RectValue
    $rect.X = 0; $rect.Y = 0; $rect.Width = 900; $rect.Height = 700
    $node = New-Object AppStudio.ScanNode
    $node.AutomationId = 'num1Button'; $node.ControlType = 'Button'; $node.Name = 'One'; $node.ClassName = 'Fixture'
    $node.Rect = New-Object AppStudio.RectValue
    $node.Rect.X = 10; $node.Rect.Y = 10; $node.Rect.Width = 80; $node.Rect.Height = 22
    $siblings = New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'
    $siblings.Add($node)
    $step = New-Object AppStudio.StepRecord
    $step.Index = 1; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 150
    $step.AppName = 'FixtureApp'; $step.WindowTitle = 'FixtureApp'; $step.WindowClass = ''
    $step.ElementLabel = 'Button "One"'; $step.ControlType = 'Button'; $step.Confidence = 'high'
    foreach ($l in ([AppStudio.LocatorBuilder]::Build($node, $rect, $siblings))) { $step.Locators.Add($l) }
    $session.Steps.Add($step)
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session
}

try {
    # ---------- 1. the empty state ---------------------------------------
    # Nothing has been recorded, so both the modules and the editor have to say
    # so in words. A blank pane here reads as a product that failed to start.
    $h = Start-App $temp
    $w = $h.Win
    $text = Screen-Text $w
    $emptyCode = M 'empty-code.txt' 'No code has been generated yet.'
    $emptyNote = M 'empty-code-note.txt' 'Record or snap, and the code written from that session appears here.'
    $emptyWork = M 'empty-workflow.txt' 'There is no session yet.'
    $emptyMods = M 'empty-modules.txt' 'There is no code yet.'
    foreach ($wanted in @($emptyCode, $emptyNote, $emptyWork, $emptyMods)) {
        if ($text.IndexOf($wanted, [StringComparison]::Ordinal) -lt 0) {
            throw ('the empty window does not say "' + $wanted + '". It says: ' + $text)
        }
    }
    Shoot $w 'empty'
    # The controls that act on the whole window are there even with no session,
    # and they are in the same place they will be once there is one.
    foreach ($label in @((M 'home-record.txt' 'Record'), (M 'home-snap.txt' 'Snap'), (M 'compact-options.txt' 'Settings'))) {
        if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Button) $label)) {
            throw ('the empty window has no "' + $label + '" control')
        }
    }
    # Nothing that cannot do anything yet is offered.
    $dead = Find-Named $w ([System.Windows.Automation.ControlType]::Button) (M 'code-baseline.txt' 'Back to the generated version')
    if ($null -ne $dead -and -not $dead.Current.IsOffscreen) {
        throw 'the empty window offers a control that has nothing to act on'
    }
    $h.App.Kill(); Start-Sleep -Milliseconds 900

    # ---------- 2. a session, the three panes, and folding down and back ----
    #
    # There is a recording on disk and the window still opens on the empty state:
    # what somebody finished yesterday is not what they came here to look at. It
    # is put on screen the way a person puts it there, from the picker.
    $session = Seed $temp
    $h = Start-App $temp
    $w = $h.Win
    $opening = Screen-Text $w
    if ($opening.IndexOf((M 'empty-code.txt' 'No code has been generated yet.'), [StringComparison]::Ordinal) -lt 0) {
        throw ('a session on disk was put on screen at startup. The window says: ' + $opening)
    }
    Shoot $w 'opened-with-a-session-on-disk'
    Choose-Session $w 0
    $before = Editor-Text $w
    if ($before.Length -lt 40) { throw ('the editor opened with ' + $before.Length + ' characters in it') }
    if ($before.IndexOf('Workflow', [StringComparison]::Ordinal) -lt 0) { throw 'the editor did not open on the procedure' }
    # Everything the build is made of is in the list: the C# the automation is
    # written in, open because it is the thing to edit, and behind their own
    # headings the PowerShell wrapper the build writes and the VBA spelling of
    # the same procedure. A folded heading is closed, not absent, so the two are
    # opened here before they are looked for.
    function Expand-All($window) {
        for ($round = 0; $round -lt 4; $round++) {
            foreach ($i in All-Of $window ([System.Windows.Automation.ControlType]::TreeItem)) {
                try {
                    $p = $i.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    if ($p.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $p.Expand() }
                } catch { }
            }
            Start-Sleep -Milliseconds 400
        }
    }
    function Tree-Names($window) {
        $out = @()
        foreach ($i in All-Of $window ([System.Windows.Automation.ControlType]::TreeItem)) { $out += $i.Current.Name }
        return $out
    }
    # The two languages are two positions of one control rather than two headings
    # in one list, so each is looked for where it now lives. Everything that was
    # reachable before still is: the five C# modules and the wrapper the build
    # writes under PowerShell, the five VBA modules under VBA.
    foreach ($segment in @((M 'lang-powershell.txt' 'PowerShell'), (M 'lang-vba.txt' 'VBA'))) {
        if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Button) $segment)) {
            throw ('the module pane has no "' + $segment + '" position')
        }
    }
    Expand-All $w
    $names = Tree-Names $w
    foreach ($wanted in @('Workflow.cs', 'RecordedFacts.cs', 'RuntimeCore.cs', 'RuntimeLocator.cs', 'RuntimeNative.cs', 'Wrapper.ps1')) {
        if ($names -notcontains $wanted) { throw ('the PowerShell side has no ' + $wanted + '. It has: ' + ($names -join ', ')) }
    }
    if ($names -contains 'Workflow.bas') { throw 'the PowerShell side is listing the VBA modules beside the C# ones again' }
    Shoot $w 'modules-powershell'
    Press $w (M 'lang-vba.txt' 'VBA') | Out-Null
    Expand-All $w
    $names = Tree-Names $w
    foreach ($wanted in @('Workflow.bas', 'RecordedFacts.bas', 'RuntimeCore.bas', 'RuntimeLocator.bas', 'RuntimeNative.bas')) {
        if ($names -notcontains $wanted) { throw ('the VBA side has no ' + $wanted + '. It has: ' + ($names -join ', ')) }
    }
    Shoot $w 'modules-vba'
    Press $w (M 'lang-powershell.txt' 'PowerShell') | Out-Null
    Expand-All $w
    Shoot $w 'three-panes'

    # ---------- the editor actually draws the file ------------------------
    #
    # The characters are painted on a layer behind the box being typed into, so
    # the box can be right while the screen is blank - and it has been: a layer
    # arranged at the viewport's height silently stopped drawing below it, and
    # scrolling down left the caret moving over nothing. Wrapping changes how
    # that layer is measured, so this is checked by looking at the pixels rather
    # than by asking the box what it holds.
    function Ink($handle, $rect, $path) {
        $box = New-Object AppStudio.RectValue
        $box.X = [int]$rect.X; $box.Y = [int]$rect.Y
        $box.Width = [int]$rect.Width; $box.Height = [int]$rect.Height
        $null = [AppStudio.Capture]::Crop($box, (New-Object 'AppStudio.MaskRect[]' 0), $path, $handle)
        if (-not (Test-Path -LiteralPath $path)) { throw 'the editor could not be photographed' }
        $bitmap = [Drawing.Bitmap]::FromFile($path)
        try {
            $left = [int]($bitmap.Width * 0.12)
            $top = [int]($bitmap.Height * 0.72)
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
    New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
    $handle = [IntPtr][int64]$w.Current.NativeWindowHandle
    $editBox = Wait-Named $w ([System.Windows.Automation.ControlType]::Edit) (M 'code-editor-name.txt' 'The automation, as code') 15000
    $editRect = $editBox.Current.BoundingRectangle
    $inkAtTop = Ink $handle $editRect (Join-Path $shotDir 'editor-at-top.png')
    if ($inkAtTop -lt 100) { throw ('the editor draws almost nothing at the top of the file: ' + $inkAtTop) }
    # Scrolled to the end, the lower part of the viewport still has code in it.
    $editBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) | Out-Null
    $scroll = $null
    try { $scroll = $editBox.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
    if ($null -ne $scroll -and $scroll.Current.VerticallyScrollable) {
        $scroll.SetScrollPercent(-1, 100)
        Start-Sleep -Milliseconds 900
        $inkAtEnd = Ink $handle $editRect (Join-Path $shotDir 'editor-scrolled.png')
        if ($inkAtEnd -lt 100) {
            throw ('scrolled to its last line, the editor draws almost nothing: ' + $inkAtEnd)
        }
        $scroll.SetScrollPercent(-1, 0)
        Start-Sleep -Milliseconds 500
    }

    # The replay speed is on the workflow pane.
    if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Slider) (M 'replay-speed.txt' 'Replay speed'))) {
        throw 'the workflow pane has no replay speed control'
    }

    # Folding down to the bar and back keeps the session and the editor.
    Press $w (M 'view-mini.txt' 'Fold down to the small bar') | Out-Null
    Start-Sleep -Milliseconds 1200
    $miniButtons = @()
    foreach ($i in All-Of $w ([System.Windows.Automation.ControlType]::Button)) { $miniButtons += $i.Current.Name }
    foreach ($wanted in @((M 'home-record.txt' 'Record'), (M 'home-snap.txt' 'Snap'),
                          (M 'record-settings.txt' 'Recording settings'), (M 'detail-replay.txt' 'Replay'),
                          (M 'view-full.txt' 'Back to the full view'))) {
        if ($miniButtons -notcontains $wanted) { throw ('the small bar has no "' + $wanted + '". It has: ' + ($miniButtons -join ', ')) }
    }
    # The modules, the code and the assistant are not on the small bar.
    foreach ($gone in @((M 'code-build.txt' 'Build'), (M 'tab-assistant.txt' 'Ask an assistant'))) {
        if ($miniButtons -contains $gone) { throw ('the small bar still carries "' + $gone + '"') }
    }
    # Which session is on screen is still reachable, because it is the only way
    # to a past recording.
    if (@(All-Of $w ([System.Windows.Automation.ControlType]::ComboBox)).Count -lt 1) {
        throw 'the small bar has no session picker'
    }
    if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Slider) (M 'replay-speed.txt' 'Replay speed'))) {
        throw 'the small bar has no replay speed control'
    }
    Shoot $w 'mini'
    Press $w (M 'mini-fold.txt' 'Fold the step list away') | Out-Null
    Shoot $w 'mini-folded'
    Press $w (M 'mini-unfold.txt' 'Open the step list') | Out-Null
    Press $w (M 'view-full.txt' 'Back to the full view') | Out-Null
    Start-Sleep -Milliseconds 1400
    $after = Editor-Text $w
    if ($after -ne $before) { throw 'folding down to the bar and back changed what was in the editor' }
    Shoot $w 'back-to-full'

    # ---------- 3. folding a pane away ------------------------------------
    # A folded pane is off the screen, not deleted: WPF keeps an automation peer
    # for a collapsed element alive, so what is checked is whether the row is
    # actually being shown rather than whether it still has a peer.
    $row = Find-Named $w ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.cs'
    if ($null -eq $row -or $row.Current.IsOffscreen) { throw 'the module list was not on screen to begin with' }
    Press $w (M 'pane-modules-hide.txt' 'Fold the modules away') | Out-Null
    $row = Find-Named $w ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.cs'
    if ($null -ne $row -and -not $row.Current.IsOffscreen) {
        throw 'the module list is still on screen after being folded away'
    }
    if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Button) (M 'pane-modules-show.txt' 'Open the modules'))) {
        throw 'a folded pane left no way to bring it back'
    }
    Shoot $w 'left-folded'
    Press $w (M 'pane-modules-show.txt' 'Open the modules') | Out-Null
    $row = Find-Named $w ([System.Windows.Automation.ControlType]::TreeItem) 'Workflow.cs'
    if ($null -eq $row -or $row.Current.IsOffscreen) { throw 'the module list did not come back' }

    # ---------- 3b. what goes to an assistant is picked, item by item -------
    #
    # The window has to let every combination be asked for. That means the boxes
    # are all really there, none of them is greyed out because of what it was
    # combined with, and nothing else on the window moves them: which module the
    # editor is showing and which language a build targets are separate states
    # from this one, and the point of separating them is that they do not touch.
    Press $w (M 'tab-assistant.txt' 'Ask an assistant') | Out-Null
    Start-Sleep -Milliseconds 600
    function Picks($window) {
        $found = @{}
        foreach ($i in All-Of $window ([System.Windows.Automation.ControlType]::CheckBox)) {
            $found[$i.Current.Name] = $i
        }
        return $found
    }
    function Pick-State($window) {
        $state = @{}
        foreach ($pair in (Picks $window).GetEnumerator()) {
            $state[$pair.Key] = $pair.Value.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
        }
        return $state
    }
    $pickLabels = @(
        (M 'ai-pick-environment.txt' 'Machine, screens and coordinates'),
        (M 'ai-pick-privacy.txt' 'What was and was not written down'),
        (M 'ai-pick-apps.txt' 'The applications'),
        (M 'ai-pick-screens.txt' 'The screen and window ledger'),
        (M 'ai-pick-elements.txt' 'The element ledger'),
        (M 'ai-pick-actions.txt' 'What the operator did'),
        (M 'ai-pick-replay.txt' 'The replay results'),
        (M 'ai-pick-coverage.txt' 'Coverage, limits and diagnostics'),
        (M 'ai-pick-guidance.txt' 'Guidance and the safety rules'),
        (M 'ai-pick-engine.txt' 'The C# automation code'),
        (M 'ai-pick-vba.txt' 'The VBA code'),
        (M 'ai-pick-wrapper.txt' 'The PowerShell launch wrapper'),
        (M 'ai-pick-pdf.txt' 'The screen picture document'),
        (M 'ai-pick-protocol.txt' 'The answer format and the take-back protocol'))
    $boxes = Picks $w
    foreach ($label in $pickLabels) {
        if (-not $boxes.ContainsKey($label)) {
            throw ('the assistant tab has no box for "' + $label + '". It has: ' + (($boxes.Keys | Sort-Object) -join ' / '))
        }
        if (-not $boxes[$label].Current.IsEnabled) {
            throw ('"' + $label + '" is greyed out, so some combination cannot be asked for')
        }
    }
    Shoot $w 'ai-picks'
    # Every box, toggled, with everything else left alone. Nothing may be
    # disabled by the combination it lands in, and nothing else may move.
    foreach ($label in $pickLabels) {
        $pickBefore = Pick-State $w
        $boxes[$label].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 220
        $pickAfter = Pick-State $w
        foreach ($other in $pickLabels) {
            if (-not (Picks $w)[$other].Current.IsEnabled) {
                throw ('toggling "' + $label + '" greyed out "' + $other + '"')
            }
            if ($other -eq $label) {
                if ($pickAfter[$other] -eq $pickBefore[$other]) { throw ('"' + $label + '" did not change when toggled') }
                continue
            }
            if ($pickAfter[$other] -ne $pickBefore[$other]) {
                throw ('toggling "' + $label + '" also moved "' + $other + '"')
            }
        }
        $boxes[$label].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Milliseconds 220
    }
    # Three separate states. The editor language and the build target are moved
    # here, and not one tick may follow them.
    $settled = Pick-State $w
    Press $w (M 'lang-vba.txt' 'VBA') | Out-Null
    Press $w (M 'tab-assistant.txt' 'Ask an assistant') | Out-Null
    $afterVba = Pick-State $w
    foreach ($label in $pickLabels) {
        if ($afterVba[$label] -ne $settled[$label]) { throw ('switching the editor to VBA moved "' + $label + '"') }
    }
    Press $w (M 'lang-powershell.txt' 'PowerShell') | Out-Null
    Press $w (M 'tab-assistant.txt' 'Ask an assistant') | Out-Null
    $afterBack = Pick-State $w
    foreach ($label in $pickLabels) {
        if ($afterBack[$label] -ne $settled[$label]) { throw ('switching the editor back moved "' + $label + '"') }
    }
    # Switching the pictures off says what that costs, and still generates.
    $pdfLabel = M 'ai-pick-pdf.txt' 'The screen picture document'
    (Picks $w)[$pdfLabel].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 500
    $warned = Screen-Text $w
    $pdfWarning = (M 'ai-warn-no-pdf.txt' 'The picture document is off').Substring(0, 12)
    if ($warned.IndexOf($pdfWarning, [StringComparison]::Ordinal) -lt 0) {
        throw ('switching the pictures off did not warn. Screen says: ' + $warned)
    }
    Shoot $w 'ai-picks-warning'
    Press $w (M 'code-ai-copy.txt' 'Copy the request') | Out-Null
    Start-Sleep -Milliseconds 3500
    $aiFolder = Join-Path $session.Folder 'out\ai'
    $attached = @(Get-ChildItem -LiteralPath $aiFolder -File | ForEach-Object { $_.Name })
    if ($attached -contains 'screens.pdf') {
        throw 'the picture document was switched off but is still sitting in the attachment folder'
    }
    if ($attached -notcontains 'session.md') { throw 'nothing was written for a request that selected the text' }
    $made = Screen-Text $w
    if ($made.IndexOf((M 'ai-made-title.txt' 'What was generated'), [StringComparison]::Ordinal) -lt 0) {
        throw ('the window does not say what it generated. Screen says: ' + $made)
    }
    Shoot $w 'ai-generated'
    # Back on, so the section below runs against the whole handover.
    (Picks $w)[$pdfLabel].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 500

    # ---------- 4. an answer from an assistant becomes a difference ---------
    #
    # Nothing an assistant returns may reach the editor without being read
    # first, and it has to be readable: the difference takes the middle of the
    # window - the widest thing on it - rather than a column at the bottom
    # right. Both answers are on it, and both are checked here, because a
    # difference you can only accept is not a choice.
    Press $w (M 'tab-assistant.txt' 'Ask an assistant') | Out-Null
    Press $w (M 'code-ai-copy.txt' 'Copy the request') | Out-Null
    Start-Sleep -Milliseconds 3500
    $codeJson = Join-Path $session.Folder 'code\code.json'
    if (-not (Test-Path -LiteralPath $codeJson)) { throw 'copying the request wrote no request id' }
    $requestId = ([IO.File]::ReadAllText($codeJson) | ConvertFrom-Json).requestId
    if ([string]::IsNullOrWhiteSpace($requestId)) { throw 'the request was copied without an id to answer against' }

    $marked = 'MARKED-BY-THE-ASSISTANT'
    $sentinel = '#@APPSTUDIO ' + $requestId + ' '
    $answer = @(
        ($sentinel + 'SUMMARY BEGIN'), 'named the step', ($sentinel + 'SUMMARY END'),
        ($sentinel + 'BEGIN powershell Workflow'),
        'namespace AppStudioRun',
        '{',
        '    public static class Workflow',
        '    {',
        '        // ' + $marked,
        '        public static void Run(int settleMs) { }',
        '    }',
        '}',
        ($sentinel + 'END powershell Workflow'),
        ($sentinel + 'COMPLETE 1')) -join "`r`n"
    for ($try = 0; $try -lt 8; $try++) {
        try { [Windows.Forms.Clipboard]::SetText($answer); break } catch { Start-Sleep -Milliseconds 250 }
    }
    Press $w (M 'code-ai-paste.txt' 'Paste the answer and see the difference') | Out-Null
    Start-Sleep -Milliseconds 1200
    $said = Screen-Text $w
    if ($said.IndexOf((M 'diff-title.txt' 'The answer from the assistant, not yet applied'), [StringComparison]::Ordinal) -lt 0) {
        throw ('the answer was not shown as a difference. Screen says: ' + $said)
    }
    foreach ($choice in @((M 'diff-apply.txt' 'Take this in'), (M 'diff-reject.txt' 'Reject'))) {
        if ($null -eq (Find-Named $w ([System.Windows.Automation.ControlType]::Button) $choice)) {
            throw ('the difference does not offer "' + $choice + '"')
        }
    }
    # The difference is where the editor was, so it has the room the editor had.
    $diffApply = Find-Named $w ([System.Windows.Automation.ControlType]::Button) (M 'diff-apply.txt' 'Take this in')
    $windowRect = $w.Current.BoundingRectangle
    $diffRect = $diffApply.Current.BoundingRectangle
    if ($diffRect.Left -gt ($windowRect.Left + $windowRect.Width * 0.72)) {
        throw 'the difference is still off in the right hand column rather than across the middle of the window'
    }
    Shoot $w 'diff'

    # Rejecting leaves the code exactly as it was.
    Press $w (M 'diff-reject.txt' 'Reject') | Out-Null
    Start-Sleep -Milliseconds 900
    $afterReject = Editor-Text $w
    if ($afterReject.IndexOf($marked, [StringComparison]::Ordinal) -ge 0) {
        throw 'rejecting the answer applied it anyway'
    }
    if ($afterReject -ne $before) { throw 'rejecting the answer changed the code on screen' }

    # Taking it in replaces the code, and only then.
    for ($try = 0; $try -lt 8; $try++) {
        try { [Windows.Forms.Clipboard]::SetText($answer); break } catch { Start-Sleep -Milliseconds 250 }
    }
    Press $w (M 'code-ai-paste.txt' 'Paste the answer and see the difference') | Out-Null
    Start-Sleep -Milliseconds 1000
    Press $w (M 'diff-apply.txt' 'Take this in') | Out-Null
    Start-Sleep -Milliseconds 1400
    $afterApply = Editor-Text $w
    if ($afterApply.IndexOf($marked, [StringComparison]::Ordinal) -lt 0) {
        throw 'taking the answer in did not put it in the editor'
    }
    Shoot $w 'diff-applied'
    Press $w (M 'tab-workflow.txt' 'Workflow') | Out-Null

    # ---------- 5. build, and the control that starts what was built -------
    $launch = Find-Named $w ([System.Windows.Automation.ControlType]::Button) (M 'code-launch.txt' 'Start the built file')
    if ($null -eq $launch) { throw 'there is no control to start the built file' }
    if ($launch.Current.IsEnabled) { throw 'the built file can be started before anything has been built' }
    Press $w (M 'code-build.txt' 'Build') | Out-Null
    Start-Sleep -Milliseconds 2500
    $launch = Find-Named $w ([System.Windows.Automation.ControlType]::Button) (M 'code-launch.txt' 'Start the built file')
    if (-not $launch.Current.IsEnabled) {
        throw ('a build did not enable the control that starts it. Screen says: ' + (Screen-Text $w))
    }
    # What the build did is said beside the code, not in the assistant column.
    $said = Screen-Text $w
    if ($said.IndexOf((M 'code-build-done.txt' 'Built one file.'), [StringComparison]::Ordinal) -lt 0) {
        throw ('the build result is not stated beside the code. Screen says: ' + $said)
    }
    Shoot $w 'build-done'
    # A build is the third of the three separate states, so it is checked against
    # the second here rather than earlier: it must not have moved a single tick.
    Press $w (M 'tab-assistant.txt' 'Ask an assistant') | Out-Null
    $afterBuild = Pick-State $w
    foreach ($label in $pickLabels) {
        if ($afterBuild[$label] -ne $settled[$label]) { throw ('building moved "' + $label + '"') }
    }
    Press $w (M 'tab-workflow.txt' 'Workflow') | Out-Null

    Write-Output ('PASS test-ui-shell empty=stated modules=' + $names.Count +
        ' inkTop=' + $inkAtTop + ' miniKeepsEditor=1 paneFold=1 diff=centre,reject-kept,apply-replaced buildEnablesLaunch=1' +
        ' aiPicks=' + $pickLabels.Count + ' disabled=0 crossTalk=0 langIndependent=1 buildIndependent=1 pdfOffWarned=1 generatedSummary=1')
} finally {
    if ($null -ne $app) { try { $app.Kill() } catch { } }
    if ($null -ne $h -and $null -ne $h.App) { try { if (-not $h.App.HasExited) { $h.App.Kill() } } catch { } }
    if ($null -ne $restore) { try { [Windows.Forms.Clipboard]::SetText($restore) } catch { } }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
