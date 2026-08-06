$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Does a replay carry out what was recorded - the same things, aimed at the same
# controls, in the same order?
#
# "It ran and nothing threw" does not answer that. A replay that performed the
# right three operations in the wrong order would report three successes, and so
# would one that typed the right words into the wrong field. So the recording
# here is built so that only one order can produce the end state it checks for:
# a value is typed, a button is pressed, and then a different value is typed over
# the first. Out of order, the field holds the wrong word; with the wrong target,
# it holds nothing at all; and the button caption counts its own presses, so an
# extra or a missing press is visible too.
#
# It then runs the whole thing again at a different speed, because the speed
# control may only shorten waiting - if it changed what is carried out, or the
# order of it, it would be a different automation rather than a faster one.
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-fidelity-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$fixture = $null

function Window-Of($processId) {
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $found = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function By-Id($window, $automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Value-Of($window, $automationId) {
    return (By-Id $window $automationId).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Locators-For($automationId, $controlType, $windowRect) {
    $node = New-Object AppStudio.ScanNode
    $node.AutomationId = $automationId
    $node.ControlType = $controlType
    $node.Name = $automationId
    $node.ClassName = 'Fixture'
    $node.Rect = New-Object AppStudio.RectValue
    $node.Rect.X = 10; $node.Rect.Y = 10; $node.Rect.Width = 80; $node.Rect.Height = 22
    $siblings = New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'
    $siblings.Add($node)
    return [AppStudio.LocatorBuilder]::Build($node, $windowRect, $siblings)
}
# The recording under test. Four steps whose end state only one order produces.
function Seed($into, $windowRect) {
    $session = [AppStudio.SessionStore]::Create($into, 'record', 'replay fidelity')
    $session.ValuePolicy = 'recordText'
    $plan = @(
        @{ Kind = 'textInput'; Id = 'CustomerCode';  Type = 'Edit';   Value = 'first-value' },
        @{ Kind = 'click';     Id = 'FirstSave';     Type = 'Button'; Value = $null },
        @{ Kind = 'textInput'; Id = 'CustomerCode';  Type = 'Edit';   Value = 'second-value' },
        @{ Kind = 'click';     Id = 'FeatureToggle'; Type = 'CheckBox'; Value = $null }
    )
    $index = 1
    foreach ($item in $plan) {
        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.Kind = $item.Kind; $step.At = [DateTimeOffset]::Now; $step.GapMs = 250
        $step.AppName = 'FixtureWinForms'; $step.WindowTitle = 'FixtureWinForms'; $step.WindowClass = ''
        $step.ElementLabel = ($item.Type + ' "' + $item.Id + '"'); $step.ControlType = $item.Type; $step.Confidence = 'high'
        if ($null -ne $item.Value) {
            $step.ValueKind = 'text'; $step.Value = $item.Value; $step.ValueLength = $item.Value.Length
        }
        foreach ($locator in (Locators-For $item.Id $item.Type $windowRect)) { $step.Locators.Add($locator) }
        $session.Steps.Add($step)
        $index++
    }
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session
}

try {
    $ready = Join-Path $temp 'ready.json'
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    $window = Window-Of $fixture.Id
    if ($null -eq $window) { throw 'the fixture window never appeared to UI Automation' }
    Start-Sleep -Milliseconds 600

    $windowRect = New-Object AppStudio.RectValue
    $windowRect.X = 0; $windowRect.Y = 0; $windowRect.Width = 900; $windowRect.Height = 700

    $results = @()
    foreach ($speed in @(1.0, 3.0)) {
        # A clean start, so the end state is produced by this run alone.
        (By-Id $window 'CustomerCode').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
        Start-Sleep -Milliseconds 300
        $beforeButton = (By-Id $window 'FirstSave').Current.Name
        $beforeToggle = (By-Id $window 'FeatureToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState

        $session = Seed $temp $windowRect
        $engine = New-Object AppStudio.ReplayEngine($root, $session)
        $engine.Speed = $speed
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $report = $engine.Run('auto', $true)
        $watch.Stop()
        $engine.Dispose()

        if ($report.Attempted -ne 4) { throw ('speed ' + $speed + ': the replay attempted ' + $report.Attempted + ' of 4 steps') }
        if ($report.Succeeded -ne 4) { throw ('speed ' + $speed + ': only ' + $report.Succeeded + ' of 4 steps were carried out: ' + $report.StopReason) }

        # ---- order: the replay log is in the recorded order, one row per step --
        $order = @()
        foreach ($step in $report.Steps) { $order += $step.Index }
        if (($order -join ',') -ne '1,2,3,4') { throw ('speed ' + $speed + ': the steps were carried out in the order ' + ($order -join ',')) }

        # ---- target and value: only the recorded order produces this state -----
        Start-Sleep -Milliseconds 700
        $code = Value-Of $window 'CustomerCode'
        $afterToggle = (By-Id $window 'FeatureToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
        $afterButton = (By-Id $window 'FirstSave').Current.Name
        if ($code -ne 'second-value') {
            throw ('speed ' + $speed + ': CustomerCode holds "' + $code + '". "first-value" means the two typing steps ran in the wrong order; anything else means they were aimed somewhere else.')
        }
        if ($afterToggle -eq $beforeToggle) {
            throw ('speed ' + $speed + ': the recorded press on the check box did not reach it; it is still ' + $beforeToggle)
        }
        if ($afterButton -eq $beforeButton) { throw ('speed ' + $speed + ': the recorded press did not happen') }
        if ($afterButton -notmatch '^Saved ') { throw ('speed ' + $speed + ': the button says ' + $afterButton) }
        # The caption counts presses, so one recorded press has to be one press.
        $before = 0; $after = 0
        if ($beforeButton -match 'Saved (\d+)') { $before = [int]$Matches[1] }
        if ($afterButton -match 'Saved (\d+)') { $after = [int]$Matches[1] }
        if (($after - $before) -ne 1) {
            throw ('speed ' + $speed + ': one recorded press produced ' + ($after - $before) + ' presses')
        }
        $results += @{ Speed = $speed; Ms = $watch.ElapsedMilliseconds; Code = $code; Button = $afterButton }
    }

    # ---- the speed control shortens waiting and changes nothing else ----------
    $slow = $results[0].Ms
    $fast = $results[1].Ms
    if ($results[0].Code -ne $results[1].Code) { throw 'the two speeds left the window in different states' }
    if ($fast -ge $slow) {
        throw ('raising the replay speed did not shorten the run: 1.0 took ' + $slow + ' ms and 3.0 took ' + $fast + ' ms')
    }

    Write-Output ('PASS test-replay-fidelity steps=4 order=1,2,3,4 code="' + $results[0].Code + '" pressesPerRecordedPress=1' +
        ' slowMs=' + $slow + ' fastMs=' + $fast + ' speedup=' + [Math]::Round($slow / [Math]::Max(1, $fast), 2) + 'x')
} finally {
    if ($null -ne $fixture) { try { if (-not $fixture.HasExited) { $fixture.Kill() } } catch { } }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
