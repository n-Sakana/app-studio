$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The window the built .cmd opens, driven the way a person drives it.
#
# test-code-build proves the fold: one file, and the automation inside it still
# the automation. It says nothing about what somebody meets when they start that
# file, and "it runs in a console" is not something an operator can use - there
# is nothing to press, nothing to stop, and the outcome leaves with the console.
#
# So this starts the artefact exactly as a double-click does, finds its window,
# and presses the one button. Three runs go through it: one that finishes, one
# stopped by the operator, one that fails. What is checked is what the operator
# can see - the caption on the button, the state in the title bar, the outcome
# and the time in the field - and what they cannot: that stopping actually
# stopped the run rather than letting it finish quietly, and that closing the
# window left nothing running.
#
# It drives nothing but its own window. One recording has no steps and the other
# names a window that does not exist, so no pointer moves and no key is sent,
# which is why this belongs in the set that runs anywhere.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
# The artefact is built with the product's own wording, so the product has to
# know where its wording lives - and this test then reads the same files rather
# than keeping a second copy of the words to compare against.
[AppStudio.Messages]::Init($root)

function Word([string]$name) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (-not (Test-Path -LiteralPath $path)) { throw ('the artefact window has no wording for ' + $name) }
    return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim()
}
$captionRun = Word 'artefact-run.txt'
$captionStop = Word 'artefact-stop.txt'
$stateIdle = Word 'artefact-state-idle.txt'
$stateBusy = Word 'artefact-state-busy.txt'
$stateDone = Word 'artefact-state-done.txt'
$stateFailed = Word 'artefact-state-failed.txt'
$stateAborted = Word 'artefact-state-aborted.txt'
$resultDone = Word 'artefact-result-done.txt'
$resultFailed = Word 'artefact-result-failed.txt'
$resultAborted = Word 'artefact-result-aborted.txt'
$seconds = Word 'artefact-seconds.txt'
$logNote = Word 'artefact-log-note.txt'

$shotDir = Join-Path $PSScriptRoot '.build\artefact'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-artefact-window-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

# ---------- driving one window ----------

# Which process the file cmd started actually is. The window is then looked for
# by that process and not by its title: a window left over from somewhere else
# with the same name on it would otherwise be driven instead, and everything
# after that would be about the wrong window.
function Host-Of([int]$cmdId, [int]$waitSeconds) {
    $limit = [DateTime]::UtcNow.AddSeconds($waitSeconds)
    while ([DateTime]::UtcNow -lt $limit) {
        # cmd's own console host is a child of it too. What is wanted is the one
        # that carries the automation, which is the PowerShell the header starts.
        $kids = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $cmdId) |
            Where-Object { $_.Name -eq 'powershell.exe' } | ForEach-Object { [int]$_.ProcessId })
        if ($kids.Count -gt 0) { return $kids }
        Start-Sleep -Milliseconds 200
    }
    throw 'the file cmd started never became a PowerShell process'
}
# A process id on its own is not an answer: they are handed out again. What is
# asked is whether the PowerShell this run started is still there.
function Still-Running([int]$processId) {
    $found = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $found) { return $false }
    return ($found.ProcessName -eq 'powershell')
}
function Window-Of([int[]]$processIds, [int]$waitSeconds) {
    $limit = [DateTime]::UtcNow.AddSeconds($waitSeconds)
    while ([DateTime]::UtcNow -lt $limit) {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $child = $walker.GetFirstChild([System.Windows.Automation.AutomationElement]::RootElement)
        while ($null -ne $child) {
            if ($processIds -contains [int]$child.Current.ProcessId) { return $child }
            $child = $walker.GetNextSibling($child)
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function By-Id($window, [string]$id, [int]$waitSeconds) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $limit = [DateTime]::UtcNow.AddSeconds($waitSeconds)
    while ([DateTime]::UtcNow -lt $limit) {
        $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
# A read-only multi-line field publishes its text rather than a value, so both
# are asked for and the one it has is used.
function Text-Of($element) {
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.Value
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
        return $pattern.DocumentRange.GetText(-1)
    }
    throw 'the result field publishes neither its value nor its text'
}
function Press($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Caption($button, [string]$caption, [int]$waitSeconds, [string]$what) {
    $limit = [DateTime]::UtcNow.AddSeconds($waitSeconds)
    while ([DateTime]::UtcNow -lt $limit) {
        if ($button.Current.Name -eq $caption) { return }
        Start-Sleep -Milliseconds 150
    }
    throw ($what + ': the button still says "' + $button.Current.Name + '" instead of "' + $caption + '"')
}
function State-Of($window) {
    $title = $window.Current.Name
    $mark = $title.IndexOf(' - ', [StringComparison]::Ordinal)
    if ($mark -lt 0) { throw ('the title bar says nothing about the state: ' + $title) }
    return $title.Substring($mark + 3)
}
function Shot($window, [string]$path) {
    $rect = $window.Current.BoundingRectangle
    $bitmap = New-Object Drawing.Bitmap ([int]$rect.Width), ([int]$rect.Height)
    $canvas = [Drawing.Graphics]::FromImage($bitmap)
    $canvas.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object Drawing.Size ([int]$rect.Width), ([int]$rect.Height)))
    $canvas.Dispose()
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ([int]$rect.Width).ToString() + 'x' + ([int]$rect.Height).ToString()
}
function Seconds-In([string]$text, [string]$what) {
    $match = [regex]::Match($text, '([0-9]+\.[0-9])\s*' + [regex]::Escape($seconds))
    if (-not $match.Success) { throw ($what + ': the field does not say how long it took: ' + $text) }
    return [double]$match.Groups[1].Value
}

# One recording, built, and the single file it became.
function Build-Artefact([string]$name, [bool]$withMissingWindow) {
    $folder = Join-Path $temp $name
    New-Item -ItemType Directory -Path $folder | Out-Null
    $session = [AppStudio.SessionStore]::Create($folder, 'record', ('artefact window - ' + $name))
    if ($withMissingWindow) {
        $step = New-Object AppStudio.StepRecord
        $step.Index = 1; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 150
        $step.WindowTitle = 'No Such Window 5f31ab'; $step.WindowClass = 'ApplicationFrameWindow'
        $step.ElementLabel = 'Button "Nothing"'; $step.ControlType = 'Button'
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'uia.automationId'; $locator.AutomationId = 'nothingButton'; $locator.ControlType = 'Button'
        $step.Locators.Add($locator)
        $session.Steps.Add($step)
    }
    $project = [AppStudio.CodeProject]::Open($session)
    $build = [AppStudio.CodeBuild]::BuildPowerShell($project.Files('powershell'), (Join-Path $folder 'build'))
    if (-not $build.Ok) { throw ('nothing was built: ' + $build.Problem) }
    # One file is the whole promise of this mode. A window that needed something
    # beside it would be a second file to hand over.
    $written = @(Get-ChildItem -LiteralPath (Join-Path $folder 'build') -Force)
    if ($written.Count -ne 1) {
        throw ('the build left ' + $written.Count + ' files beside each other instead of one: ' + (($written | ForEach-Object { $_.Name }) -join ', '))
    }
    return $build
}

$started = [Diagnostics.Stopwatch]::StartNew()
$runs = @()
try {
    $done = Build-Artefact 'done' $false
    $stops = Build-Artefact 'stops' $true

    # The window has to be in the file, and the way past it has to be in the file
    # too: a caller that cannot press a button needs the console it used to get.
    $text = [IO.File]::ReadAllText($done.Path, (New-Object Text.UTF8Encoding($false)))
    foreach ($part in @('#region RunWindow.cs', 'class RunWindow : Form', '[switch]$Console', 'Shell]::Show(', $captionRun, $captionStop)) {
        if ($text.IndexOf($part, [StringComparison]::Ordinal) -lt 0) { throw ('the artefact is missing ' + $part) }
    }
    if ($done.Modules -notcontains 'RunWindow.cs') { throw 'the build does not say it folded the window in' }

    # ---------- a run that finishes, twice ----------

    $cmd = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $done.Path + '" -SettleMs 400')) -PassThru
    $runs += $cmd
    $hosted1 = Host-Of $cmd.Id 30
    $window = Window-Of $hosted1 60
    if ($null -eq $window) { throw 'starting the built file opened no window' }
    if ($window.Current.Name -notlike ([IO.Path]::GetFileName($done.Path) + ' - *')) {
        throw ('the window does not say which file it came from: ' + $window.Current.Name)
    }
    $button = By-Id $window 'AppStudioAction' 20
    $field = By-Id $window 'AppStudioResult' 20
    if ($null -eq $button) { throw 'the window has no button' }
    if ($null -eq $field) { throw 'the window has no result field' }

    # Two things somebody can look at, and the title bar the desktop draws.
    # Anything else on this window is something nobody asked for. The title bar
    # is left out on purpose: its minimise and close buttons belong to Windows,
    # not to this window, and counting them would be counting the frame.
    $parts = @()
    $children = $window.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $children) {
        if ($child.Current.ControlType.ProgrammaticName -eq 'ControlType.TitleBar') { continue }
        $parts += ($child.Current.ControlType.ProgrammaticName + ' ' + $child.Current.AutomationId)
    }
    if ($parts.Count -ne 2) { throw ('the window carries ' + $parts.Count + ' parts instead of two: ' + ($parts -join ', ')) }
    if (@($parts | Where-Object { $_ -eq 'ControlType.Button AppStudioAction' }).Count -ne 1) {
        throw ('the button is not the one part that acts: ' + ($parts -join ', '))
    }
    if (@($parts | Where-Object { $_ -like 'ControlType.* AppStudioResult' }).Count -ne 1) {
        throw ('the result field is not the other part: ' + ($parts -join ', '))
    }

    if ((State-Of $window) -ne $stateIdle) { throw ('a window that has run nothing says ' + (State-Of $window)) }
    if ($button.Current.Name -ne $captionRun) { throw ('the button starts as ' + $button.Current.Name) }
    if ((Text-Of $field).Trim().Length -ne 0) { throw 'the field claims a result before anything has run' }
    $sizeIdle = Shot $window (Join-Path $shotDir 'idle.png')

    Press $button
    Wait-Caption $button $captionRun 60 'a run that finishes'
    if ((State-Of $window) -ne $stateDone) { throw ('a finished run says ' + (State-Of $window)) }
    $doneText = Text-Of $field
    if ($doneText.IndexOf($resultDone, [StringComparison]::Ordinal) -lt 0) { throw ('a finished run reports ' + $doneText) }
    Seconds-In $doneText 'a run that finishes' | Out-Null
    $sizeDone = Shot $window (Join-Path $shotDir 'done.png')

    # Once is not "it can be run again".
    Press $button
    Wait-Caption $button $captionRun 60 'the same window a second time'
    if ((State-Of $window) -ne $stateDone) { throw ('the second run says ' + (State-Of $window)) }
    if ((Text-Of $field).IndexOf($resultDone, [StringComparison]::Ordinal) -lt 0) { throw 'the second run reports something else' }

    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $cmd.WaitForExit(30000)) { throw 'closing the window left the file it was started from running' }
    foreach ($id in $hosted1) {
        if (Still-Running $id) { throw ('closing the window left process ' + $id + ' behind') }
    }

    # ---------- a run stopped by the operator, then one that fails ----------

    $cmd = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $stops.Path + '" -SettleMs 400')) -PassThru
    $runs += $cmd
    $hosted2 = Host-Of $cmd.Id 30
    $window = Window-Of $hosted2 60
    if ($null -eq $window) { throw 'starting the built file opened no window' }
    $button = By-Id $window 'AppStudioAction' 20
    $field = By-Id $window 'AppStudioResult' 20
    if ($null -eq $button) { throw 'the window has no button' }
    if ($null -eq $field) { throw 'the window has no result field' }

    Press $button
    # While it runs the window is still a window: it answers, and the one button
    # now offers the only thing worth offering.
    Wait-Caption $button $captionStop 30 'a run in progress'
    if ((State-Of $window) -ne $stateBusy) { throw ('a running window says ' + (State-Of $window)) }
    $sizeBusy = Shot $window (Join-Path $shotDir 'busy.png')

    Start-Sleep -Milliseconds 800
    Press $button
    Wait-Caption $button $captionRun 30 'stopping a run'
    if ((State-Of $window) -ne $stateAborted) { throw ('a stopped run says ' + (State-Of $window)) }
    $stopText = Text-Of $field
    if ($stopText.IndexOf($resultAborted, [StringComparison]::Ordinal) -lt 0) { throw ('a stopped run reports ' + $stopText) }
    # This recording looks for a window that is not there and gives up after ten
    # seconds. A stop that took that long would be the run ending on its own with
    # the word "stopped" written over it.
    $stopSeconds = Seconds-In $stopText 'stopping a run'
    if ($stopSeconds -gt 8) { throw ('stopping took ' + $stopSeconds + ' seconds, which is the run finishing rather than being stopped') }
    $sizeStopped = Shot $window (Join-Path $shotDir 'stopped.png')

    # And it can still be run after being stopped.
    Press $button
    Wait-Caption $button $captionRun 60 'a run that fails'
    if ((State-Of $window) -ne $stateFailed) { throw ('a failed run says ' + (State-Of $window)) }
    $failText = Text-Of $field
    if ($failText.IndexOf($resultFailed, [StringComparison]::Ordinal) -lt 0) { throw ('a failed run reports ' + $failText) }
    if ($failText -notmatch 'no window matches') { throw ('a failed run does not say why: ' + $failText) }
    if ($failText.IndexOf($logNote, [StringComparison]::Ordinal) -lt 0) { throw 'a failed run does not say where the rest of it is' }
    # A summary, not a log poured into a window.
    if ($failText.Length -gt 700) { throw ('the field was given ' + $failText.Length + ' characters of failure') }
    Seconds-In $failText 'a run that fails' | Out-Null
    $sizeFailed = Shot $window (Join-Path $shotDir 'failed.png')

    # Closed in the middle of a run, which is the other way an operator stops
    # one. The run has to be asked to stop and waited for, or what is left is a
    # closed window and an automation still driving somebody's application with
    # nothing on screen to say so.
    Press $button
    Wait-Caption $button $captionStop 30 'a run about to be closed'
    Start-Sleep -Milliseconds 600
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $cmd.WaitForExit(30000)) { throw 'closing the window mid-run left the file it was started from running' }
    foreach ($id in $hosted2) {
        if (Still-Running $id) { throw ('closing the window left process ' + $id + ' behind') }
    }

    # Every run is accounted for beside the artefact, whether anybody watched the
    # window or not.
    $log = [IO.File]::ReadAllText(($stops.Path + '.log'), (New-Object Text.UTF8Encoding($false)))
    foreach ($mark in @('START  settleMs=400 mode=window', 'ABORTED', 'STOPPED')) {
        if ($log.IndexOf($mark, [StringComparison]::Ordinal) -lt 0) { throw ('the log does not record ' + $mark + ': ' + $log) }
    }
    # Three presses, and the one the window was closed on is accounted for the
    # same way as the two somebody watched.
    if (@([regex]::Matches($log, 'ABORTED')).Count -ne 2) {
        throw ('the run the window was closed on was not recorded as stopped: ' + $log)
    }
    $doneLog = [IO.File]::ReadAllText(($done.Path + '.log'), (New-Object Text.UTF8Encoding($false)))
    if (@([regex]::Matches($doneLog, 'DONE')).Count -ne 2) { throw ('two runs left ' + @([regex]::Matches($doneLog, 'DONE')).Count + ' finished runs in the log') }

    $started.Stop()
    Write-Output ('PASS test-artefact-window parts=2 idle=' + $sizeIdle + ' done=' + $sizeDone + ' busy=' + $sizeBusy +
        ' stopped=' + $sizeStopped + ' failed=' + $sizeFailed + ' stopSeconds=' + $stopSeconds +
        ' reran=1 orphans=0 files=1 shots=' + $shotDir + ' durationMs=' + $started.ElapsedMilliseconds)
} finally {
    # Killing cmd is not killing what cmd started. A window left open here would
    # still be open when this test is run again, and the next run would drive it
    # instead of its own.
    foreach ($run in $runs) {
        try {
            foreach ($id in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $run.Id) | ForEach-Object { [int]$_.ProcessId })) {
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
        try { if (-not $run.HasExited) { $run.Kill() } } catch { }
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
