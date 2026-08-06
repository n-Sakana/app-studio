$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The panel that is on screen while a recording or a replay runs, measured as its
# own text grows.
#
# The fault this exists for: the panel is only as wide as its words, and it was
# sized once, when it was first shown. During a replay the clock says "Replaying
# 1/40" and then "Replaying 12/40", which is wider - and everything after the
# clock moved right while the window did not. The pause control is last on the
# row, so the pause control was the thing that ended up outside the window. It
# was reported as "the pause button was cut off", and a test that only asks
# whether the control exists would never have found it: it existed, it was just
# not in the window any more.
#
# So this grows the text the way a real replay does and measures, after each
# change, whether every control is still inside the panel. It creates the panel
# directly rather than through a replay, because that is the only way to reach
# the wide states quickly and without driving anybody's applications.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Messages]::Init($root)
[AppStudio.Theme]::Init($root)
$shotDir = Join-Path $PSScriptRoot '.build\hud'
$hud = $null

function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Panel-Of($hud) {
    # The control panel is the one of this product's windows that is not
    # click-through; it is the second handle it reports.
    foreach ($handle in $hud.OwnHandles) {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][int64]$handle)
        if ($null -eq $element) { continue }
        $buttons = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
        if ($buttons.Count -gt 0) { return @{ Element = $element; Handle = [IntPtr][int64]$handle } }
    }
    return $null
}
function Check($hud, $what, $problems) {
    $panel = Panel-Of $hud
    if ($null -eq $panel) { $problems.Add($what + ': the panel has no controls on it at all'); return 0 }
    $bounds = [AppStudio.WindowTools]::GetPhysicalRect($panel.Handle)
    if ($null -eq $bounds) { $problems.Add($what + ': the panel has no rectangle'); return 0 }
    $seen = 0
    foreach ($kind in @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::Text)) {
        $items = $panel.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $kind)))
        foreach ($item in $items) {
            $rect = $item.Current.BoundingRectangle
            if ($rect.IsEmpty -or [double]::IsInfinity($rect.Width) -or $rect.Width -le 0) { continue }
            $seen++
            $name = $item.Current.Name
            if ([string]::IsNullOrEmpty($name)) { $name = '(unnamed)' }
            $overRight = ($rect.X + $rect.Width) - ($bounds.X + $bounds.Width)
            $overBottom = ($rect.Y + $rect.Height) - ($bounds.Y + $bounds.Height)
            $overLeft = $bounds.X - $rect.X
            $overTop = $bounds.Y - $rect.Y
            if ($overRight -gt 1 -or $overBottom -gt 1 -or $overLeft -gt 1 -or $overTop -gt 1) {
                $how = @()
                if ($overRight -gt 1) { $how += ([int]$overRight).ToString() + 'px past the right edge' }
                if ($overBottom -gt 1) { $how += ([int]$overBottom).ToString() + 'px below it' }
                if ($overLeft -gt 1) { $how += ([int]$overLeft).ToString() + 'px left of it' }
                if ($overTop -gt 1) { $how += ([int]$overTop).ToString() + 'px above it' }
                $problems.Add($what + ': "' + $name + '" is outside the panel: ' + ($how -join ', '))
            }
        }
    }
    if ($env:APPSTUDIO_UI_SHOTS -eq '1') {
        New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
        [AppStudio.Capture]::Crop($bounds, (New-Object 'AppStudio.MaskRect[]' 0),
            (Join-Path $shotDir ($what.Replace('/', '-').Replace(' ', '-') + '.png')), $panel.Handle) | Out-Null
    }
    return $seen
}

$problems = New-Object 'System.Collections.Generic.List[string]'
$measured = 0
try {
    # ---- while recording -------------------------------------------------
    $hud = New-Object AppStudio.RecordHud((Message 'hud-recording.txt' 'Recording'), (Message 'hud-stop.txt' 'Stop'))
    $hud.ShowControl()
    Start-Sleep -Milliseconds 900
    $hud.SetClock((Message 'hud-recording.txt' 'Recording'), [TimeSpan]::FromSeconds(5))
    Start-Sleep -Milliseconds 500
    $measured += Check $hud 'recording' $problems
    $hud.Dispose(); $hud = $null
    Start-Sleep -Milliseconds 400

    # ---- while replaying, as the step count grows -------------------------
    $hud = New-Object AppStudio.RecordHud((Message 'hud-replaying.txt' 'Replaying'), (Message 'hud-stop.txt' 'Stop'))
    $hud.ShowControl()
    Start-Sleep -Milliseconds 900
    $replaying = Message 'hud-replaying.txt' 'Replaying'
    # 1/40 through 128/1024: the widths a real replay actually walks through,
    # and then some, because the panel must fit its words rather than the words
    # it happened to have when it was first placed.
    # The panel's own width is what has to be watched, not the rectangles of the
    # controls in it. WPF arranges a row inside the window it is given: told to
    # be 1250 wide, it squeezes and clips the row to 1250 and reports every
    # child as being inside. So a control that the eye sees cut in half is
    # reported by UI Automation as perfectly placed, and a test that measures
    # only rectangles passes while the fault is on screen. What is measurable is
    # the window: a panel that is as wide as its words gets wider when its words
    # do, and the panel that shipped never did.
    $widths = @{}
    foreach ($count in @('1/40', '9/40', '12/40', '128/1024')) {
        $hud.SetClock(($replaying + ' ' + $count), [TimeSpan]::FromSeconds(72))
        Start-Sleep -Milliseconds 500
        $measured += Check $hud ('replaying ' + $count) $problems
        $panel = Panel-Of $hud
        $widths[$count] = [AppStudio.WindowTools]::GetPhysicalRect($panel.Handle).Width
    }
    if ($widths['128/1024'] -le $widths['1/40']) {
        $problems.Add('the panel did not get wider when its words did: "' + $replaying + ' 1/40" gave ' +
            $widths['1/40'] + 'px and "' + $replaying + ' 128/1024" gave ' + $widths['128/1024'] +
            'px. Everything after the clock is being squeezed, and the pause control is last on the row.')
    }
    if ($widths['12/40'] -le $widths['1/40']) {
        $problems.Add('the panel did not grow between "1/40" and "12/40" (' + $widths['1/40'] + 'px -> ' +
            $widths['12/40'] + 'px), which is the exact step a replay of more than nine steps takes')
    }

    # ---- paused ----------------------------------------------------------
    $panel = Panel-Of $hud
    $pause = $null
    foreach ($item in $panel.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
        if ($item.Current.Name -eq (Message 'hud-pause.txt' 'Pause')) { $pause = $item }
    }
    if ($null -eq $pause) { throw 'the panel has no pause control' }
    $pause.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 700
    $measured += Check $hud 'paused' $problems
    # Paused says so, rather than going on saying it is replaying.
    $words = @()
    foreach ($item in $panel.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
        $words += $item.Current.Name
    }
    $said = ($words -join ' ')
    if ($said.IndexOf((Message 'hud-paused.txt' 'Paused'), [StringComparison]::Ordinal) -lt 0) {
        throw ('the panel does not say it is paused. It says: ' + $said)
    }

    # The pointer reader has one switch, and it is not on this panel: the same
    # setting in two places is two places to disagree.
    $names = @()
    foreach ($item in $panel.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
        $names += $item.Current.Name
    }
    foreach ($name in $names) {
        if ($name -like ('*' + (Message 'hud-inspect.txt' 'Pointer info') + '*')) {
            throw ('the recording panel still carries the pointer reader switch: ' + $name)
        }
    }

    if ($problems.Count -gt 0) {
        foreach ($problem in $problems) { Write-Output ('HUD ' + $problem) }
        throw ($problems.Count.ToString() + ' control(s) outside the panel; see the HUD lines above')
    }
    Write-Output ('PASS test-hud-fit states=6 measured=' + $measured + ' clipped=0 grewWithText=' + $widths['1/40'] + '->' + $widths['128/1024'] + 'px pausedStated=1 oneInspectSwitch=1')
} finally {
    if ($null -ne $hud) { try { $hud.Dispose() } catch { } }
}
