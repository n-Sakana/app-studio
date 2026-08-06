$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The three panes, driven the way a hand drives them.
#
# test-layout-audit photographs the window in a set of states and asks whether
# anything is cut off. It cannot drag a boundary: a boundary is dragged with the
# pointer, and injected pointer input does not reach a window on every machine -
# on a locked or busy desktop SendInput is refused and the test would quietly
# check nothing. So the drag is done here without the pointer: the width one
# neighbour loses and the other gains is moved, and the boundary is then told the
# drag has finished, which is what the window actually reacts to.
#
# What is checked after every move is what went wrong. The three panes have to
# add up to the room there is, so none of them hangs over the right hand edge.
# Moving one boundary may not move the pane at the other end of the window.
# Folding a pane gives its width to the middle and to nothing else. Opening it
# again gives back the widths the operator had set. All four held at the size the
# window opens at and stopped holding as soon as anything was done in a different
# order, which is how the control that folds the right hand pane ended up off the
# side of the screen.
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()

$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-panes-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$problems = New-Object 'System.Collections.Generic.List[string]'
function Note([string]$text) { $problems.Add($text) }

function Seed($into) {
    $session = [AppStudio.SessionStore]::Create($into, 'record', 'pane layout')
    $session.ValuePolicy = 'recordText'
    $rect = New-Object AppStudio.RectValue
    $rect.X = 0; $rect.Y = 0; $rect.Width = 900; $rect.Height = 700
    for ($index = 1; $index -le 9; $index++) {
        $node = New-Object AppStudio.ScanNode
        $node.AutomationId = ('n' + $index); $node.ControlType = 'Button'; $node.Name = ('Key ' + $index); $node.ClassName = 'Fixture'
        $node.Rect = New-Object AppStudio.RectValue
        $node.Rect.X = 10; $node.Rect.Y = 20 * $index; $node.Rect.Width = 60; $node.Rect.Height = 22
        $siblings = New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'
        $siblings.Add($node)
        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 200
        $step.AppName = 'fixture'; $step.WindowTitle = 'Fixture Window'; $step.WindowClass = 'FixtureWindow'
        $step.ElementLabel = ('Button "' + $index + '" with a long enough label to need wrapping in a narrow pane')
        $step.ControlType = 'Button'; $step.Confidence = 'high'
        foreach ($locator in ([AppStudio.LocatorBuilder]::Build($node, $rect, $siblings))) { $step.Locators.Add($locator) }
        $session.Steps.Add($step)
        $null = [AppStudio.SessionStore]::Append($session, 'steps', $step.ToJson())
    }
    $session.AddLimit('a limit long enough to need wrapping when the pane is narrow')
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session
}

try {
    [AppStudio.Messages]::Init($root)
    [AppStudio.Theme]::Init($root)
    $session = Seed $temp
    $project = [AppStudio.CodeProject]::Open($session)

    $window = New-Object System.Windows.Window
    [AppStudio.Theme]::Install($window.Resources)
    $window.Width = 1200
    $window.Height = 820
    $window.Left = 40
    $window.Top = 40
    $window.WindowStartupLocation = 'Manual'
    $window.ShowInTaskbar = $false
    $workspace = New-Object AppStudio.Workspace($window, $null)
    $body = $workspace.Build()
    $window.Content = $body
    $window.Show()
    $window.UpdateLayout()
    $workspace.SetSession($session, $project)
    $window.UpdateLayout()

    $grid = [System.Windows.Controls.Grid]$body
    if ($grid.ColumnDefinitions.Count -ne 5) { throw ('the workspace has ' + $grid.ColumnDefinitions.Count + ' columns, not 5') }
    $left = $grid.ColumnDefinitions[0]
    $leftGap = $grid.ColumnDefinitions[1]
    $centre = $grid.ColumnDefinitions[2]
    $rightGap = $grid.ColumnDefinitions[3]
    $right = $grid.ColumnDefinitions[4]
    $splitters = @()
    foreach ($child in $grid.Children) {
        if ($child -is [System.Windows.Controls.GridSplitter]) { $splitters += $child }
    }
    if ($splitters.Count -ne 2) { throw ('the workspace has ' + $splitters.Count + ' boundaries, not 2') }

    function Settle() {
        $window.UpdateLayout()
        # The workspace re-decides the widths once the arrangement has settled, so
        # the queue is drained before anything is measured.
        $window.Dispatcher.Invoke([Action] {}, [System.Windows.Threading.DispatcherPriority]::ContextIdle) | Out-Null
        $window.UpdateLayout()
    }
    function Widths() {
        Settle
        return @{
            L = $left.ActualWidth; C = $centre.ActualWidth; R = $right.ActualWidth
            Gaps = $leftGap.ActualWidth + $rightGap.ActualWidth
            Room = $grid.ActualWidth
        }
    }
    # Does the arrangement fit in the room it has? A grid whose columns add up to
    # more than its own width is a grid hanging over the right hand edge, and what
    # hangs over is the last pane and everything in it.
    function Fits($label) {
        $w = Widths
        $sum = $w.L + $w.C + $w.R + $w.Gaps
        if ($env:APPSTUDIO_PANE_TRACE -eq '1') {
            Write-Host ('TRACE {0,-52} L={1,7:N1} C={2,7:N1} R={3,7:N1} gaps={4,5:N1} sum={5,7:N1} room={6,7:N1}' -f `
                $label, $w.L, $w.C, $w.R, $w.Gaps, $sum, $w.Room)
        }
        if ($sum -gt ($w.Room + 1)) {
            Note ($label + ': the panes add up to ' + [int]$sum + ' in a space of ' + [int]$w.Room +
                ' (left ' + [int]$w.L + ', centre ' + [int]$w.C + ', right ' + [int]$w.R + ')')
        }
        foreach ($pair in @(@('left', $w.L), @('centre', $w.C), @('right', $w.R))) {
            if ($pair[1] -lt 0) { Note ($label + ': the ' + $pair[0] + ' pane has a negative width') }
        }
        return $w
    }
    # A boundary, dragged.
    #
    # What a GridSplitter does to a grid is move width from one of its two
    # neighbours to the other and then say it has finished; that is done here
    # directly, because the part of a drag that goes wrong is not the splitter -
    # it is what the window does with the new widths afterwards. The pointer is
    # not used: injected pointer input is refused on a locked or busy desktop, and
    # a test that silently does nothing there is worse than no test.
    function DragBy($which, $delta) {
        if ($which -eq 'left') { $a = $left; $b = $centre } else { $a = $centre; $b = $right }
        $wasA = $a.ActualWidth
        $wasB = $b.ActualWidth
        $move = $delta
        if ($wasA + $move -lt 0) { $move = -$wasA }
        if ($wasB - $move -lt 0) { $move = $wasB }
        $a.Width = New-Object System.Windows.GridLength(($wasA + $move))
        $b.Width = New-Object System.Windows.GridLength(($wasB - $move))
        $window.UpdateLayout()
        $splitters[$(if ($which -eq 'left') { 0 } else { 1 })].RaiseEvent(
            (New-Object System.Windows.Controls.Primitives.DragCompletedEventArgs($move, 0, $false)))
        Settle
    }

    $start = Fits 'as it opens'
    if ($start.L -lt 100 -or $start.C -lt 200 -or $start.R -lt 150) {
        Note ('as it opens: a pane is implausibly narrow: ' + [int]$start.L + '/' + [int]$start.C + '/' + [int]$start.R)
    }

    # ---- a boundary moves, in both directions -------------------------------
    #
    # It moved one way and refused the other, because what the drag had just set
    # was read back before the arrangement had caught up and written away again.
    $before = Fits 'before the first drag'
    DragBy 'left' 120
    $after = Fits 'left boundary, moved right'
    if (($after.L - $before.L) -lt 60) {
        Note ('the left boundary was dragged 120 to the right and the pane grew by ' + [int]($after.L - $before.L))
    }
    if ([Math]::Abs($after.R - $before.R) -gt 2) {
        Note ('dragging the left boundary moved the right hand pane by ' + [int]($after.R - $before.R))
    }
    $before = $after
    DragBy 'left' -120
    $after = Fits 'left boundary, moved back'
    if (($before.L - $after.L) -lt 60) {
        Note ('the left boundary was dragged 120 back and the pane shrank by ' + [int]($before.L - $after.L))
    }

    $before = Fits 'before the right boundary'
    DragBy 'right' -140
    $after = Fits 'right boundary, moved left'
    if (($after.R - $before.R) -lt 70) {
        Note ('the right boundary was dragged 140 to the left and the pane grew by ' + [int]($after.R - $before.R))
    }
    if ([Math]::Abs($after.L - $before.L) -gt 2) {
        Note ('dragging the right boundary moved the left hand pane by ' + [int]($after.L - $before.L))
    }
    $before = $after
    DragBy 'right' 140
    $after = Fits 'right boundary, moved back'
    if (($before.R - $after.R) -lt 70) {
        Note ('the right boundary was dragged 140 back and the pane shrank by ' + [int]($before.R - $after.R))
    }

    # ---- folding, in both orders, keeps the widths --------------------------
    $set = Fits 'the widths the operator set'
    foreach ($order in @(@('left', 'right'), @('right', 'left'))) {
        $tag = 'fold ' + $order[0] + ' then ' + $order[1]
        foreach ($which in $order) {
            if ($which -eq 'left') { $workspace.SetLeftOpen($false) } else { $workspace.SetRightOpen($false) }
            $null = Fits ($tag + ': after folding the ' + $which)
        }
        foreach ($which in $order) {
            if ($which -eq 'left') { $workspace.SetLeftOpen($true) } else { $workspace.SetRightOpen($true) }
            $null = Fits ($tag + ': after opening the ' + $which)
        }
        $back = Fits ($tag + ': back again')
        foreach ($pair in @(@('left', $back.L, $set.L), @('centre', $back.C, $set.C), @('right', $back.R, $set.R))) {
            if ([Math]::Abs($pair[1] - $pair[2]) -gt 2) {
                Note ($tag + ': the ' + $pair[0] + ' pane came back at ' + [int]$pair[1] + ' instead of ' + [int]$pair[2])
            }
        }
    }

    # ---- folding one pane does not move the other ---------------------------
    $set = Fits 'before folding one side'
    $workspace.SetRightOpen($false)
    $folded = Fits 'right folded'
    if ([Math]::Abs($folded.L - $set.L) -gt 2) {
        Note ('folding the right hand pane moved the left hand pane by ' + [int]($folded.L - $set.L))
    }
    $workspace.SetRightOpen($true)
    $opened = Fits 'right opened again'
    foreach ($pair in @(@('left', $opened.L, $set.L), @('right', $opened.R, $set.R))) {
        if ([Math]::Abs($pair[1] - $pair[2]) -gt 2) {
            Note ('after folding and opening the right pane, the ' + $pair[0] + ' pane is ' + [int]$pair[1] + ' instead of ' + [int]$pair[2])
        }
    }

    # ---- the editor on its own, and back -----------------------------------
    $set = Fits 'before the editor takes the window'
    $workspace.SetEditorOnly($true)
    $only = Fits 'the editor on its own'
    if ($only.L -gt 1 -or $only.R -gt 1) {
        Note ('the editor has the window and the side panes still take ' + [int]$only.L + ' and ' + [int]$only.R)
    }
    $workspace.SetEditorOnly($false)
    $back = Fits 'back from the editor on its own'
    foreach ($pair in @(@('left', $back.L, $set.L), @('centre', $back.C, $set.C), @('right', $back.R, $set.R))) {
        if ([Math]::Abs($pair[1] - $pair[2]) -gt 2) {
            Note ('after the editor had the window, the ' + $pair[0] + ' pane is ' + [int]$pair[1] + ' instead of ' + [int]$pair[2])
        }
    }

    # ---- narrow, in every arrangement --------------------------------------
    #
    # The minimums add up to more than a narrow window has. What has to give is
    # the minimums, not the window: an arrangement that keeps them and overflows
    # is one whose right hand end is off the screen.
    foreach ($width in @(1100, 980, 900, 820, 760, 700, 640)) {
        $window.Width = $width
        $null = Fits ('narrowed to ' + $width)
        $workspace.SetRightOpen($false)
        $null = Fits ('narrowed to ' + $width + ', right folded')
        $workspace.SetRightOpen($true)
        $workspace.SetLeftOpen($false)
        $null = Fits ('narrowed to ' + $width + ', left folded')
        $workspace.SetLeftOpen($true)
        $null = Fits ('narrowed to ' + $width + ', both open again')
    }
    $window.Width = 1200
    $null = Fits 'wide again'

    # ---- dragging while narrow ---------------------------------------------
    $window.Width = 820
    Settle
    DragBy 'right' 200
    $null = Fits 'narrow, right boundary pushed hard right'
    DragBy 'left' 400
    $null = Fits 'narrow, left boundary pushed hard right'
    DragBy 'left' -400
    $null = Fits 'narrow, left boundary pushed hard left'
    $window.Width = 1200
    $null = Fits 'wide again after being pushed about'

    $window.Close()
    if ($problems.Count -gt 0) {
        foreach ($line in $problems) { Write-Output ('PANES ' + $line) }
        throw ($problems.Count.ToString() + ' pane layout fault(s); see the PANES lines above')
    }
    Write-Output ('PASS test-pane-layout drags=8 foldOrders=2 widths=7 editorOnly=1 overflow=0 unrelatedMovement=0')
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
