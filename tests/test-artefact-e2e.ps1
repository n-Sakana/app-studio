$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The thing an operator is actually handed, started the way they would start it.
#
# test-code-run-e2e.ps1 already proves that the generated modules drive a real
# window, but it runs them through ScriptRun - the path the Run button uses.
# Nobody is handed that path. They are handed one .cmd, and they double-click it.
# Between the two sits the whole build: a batch header cmd parses, a PowerShell
# wrapper, and five C# modules folded into a here-string. None of that was
# covered by a test that drove a real application, so "the build succeeded" and
# "what was built works" were two different claims and only the first was
# checked.
#
# Double-clicked, that file opens a window and waits to be told to go. So this
# starts it through cmd with no arguments, exactly as a double-click does,
# presses the one button, and passes only if the fixture window actually
# changed. test-artefact-window covers what the window itself does; what is
# proved here is that pressing it drives a real application.
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
# The window in the built file is written with the product's own wording, so
# the product has to know where its wording lives before anything is built.
[AppStudio.Messages]::Init($root)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-artefact-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$fixture = $null
$run = $null

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
# The artefact's own window, found by the process cmd started rather than by the
# name on it, so a window left open by something else is never the one driven.
function Artefact-Window($cmdId) {
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $limit) {
        $hosted = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $cmdId) |
            Where-Object { $_.Name -eq 'powershell.exe' } | ForEach-Object { [int]$_.ProcessId })
        if ($hosted.Count -gt 0) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $child = $walker.GetFirstChild([System.Windows.Automation.AutomationElement]::RootElement)
            while ($null -ne $child) {
                if ($hosted -contains [int]$child.Current.ProcessId) { return $child }
                $child = $walker.GetNextSibling($child)
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Message([string]$name) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (-not (Test-Path -LiteralPath $path)) { throw ('the artefact window has no wording for ' + $name) }
    return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim()
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

try {
    $ready = Join-Path $temp 'ready.json'
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    $window = Window-Of $fixture.Id
    if ($null -eq $window) { throw 'the fixture window never appeared to UI Automation' }
    Start-Sleep -Milliseconds 600

    $beforeButton = (By-Id $window 'FirstSave').Current.Name
    if ($beforeButton -ne 'Save') { throw ('the fixture button did not start as expected: ' + $beforeButton) }

    $windowRect = New-Object AppStudio.RectValue
    $windowRect.X = 0; $windowRect.Y = 0; $windowRect.Width = 900; $windowRect.Height = 700

    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'the built artefact against a real window')
    $session.ValuePolicy = 'recordText'
    $wanted = 'written-by-the-built-cmd'
    $typing = New-Object AppStudio.StepRecord
    $typing.Index = 1; $typing.Kind = 'textInput'; $typing.At = [DateTimeOffset]::Now; $typing.GapMs = 200
    $typing.AppName = 'FixtureWinForms'; $typing.WindowTitle = 'FixtureWinForms'; $typing.WindowClass = ''
    $typing.ElementLabel = 'Edit "CustomerCode"'; $typing.ControlType = 'Edit'; $typing.Confidence = 'high'
    $typing.ValueKind = 'text'; $typing.Value = $wanted; $typing.ValueLength = $wanted.Length
    foreach ($locator in (Locators-For 'CustomerCode' 'Edit' $windowRect)) { $typing.Locators.Add($locator) }
    $session.Steps.Add($typing)
    $press = New-Object AppStudio.StepRecord
    $press.Index = 2; $press.Kind = 'click'; $press.At = [DateTimeOffset]::Now; $press.GapMs = 200
    $press.AppName = 'FixtureWinForms'; $press.WindowTitle = 'FixtureWinForms'; $press.WindowClass = ''
    $press.ElementLabel = 'Button "FirstSave"'; $press.ControlType = 'Button'; $press.Confidence = 'high'
    foreach ($locator in (Locators-For 'FirstSave' 'Button' $windowRect)) { $press.Locators.Add($locator) }
    $session.Steps.Add($press)
    [AppStudio.SessionStore]::WriteMeta($session)

    $project = [AppStudio.CodeProject]::Open($session)
    $modules = $project.Files('powershell')
    $built = [AppStudio.CodeBuild]::BuildPowerShell($modules, (Join-Path $temp 'build'))
    if (-not $built.Ok) { throw ('nothing was built: ' + $built.Problem) }

    # Started the way a double-click starts it: cmd, the path, and nothing else.
    # It opens its window and waits, so the run is the button being pressed.
    $captionRun = Message 'artefact-run.txt'
    $resultDone = Message 'artefact-result-done.txt'
    $stateDone = Message 'artefact-state-done.txt'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $built.Path + '"')) -PassThru
    $artefactWindow = Artefact-Window $run.Id
    if ($null -eq $artefactWindow) { throw 'the built artefact opened no window' }
    $action = By-Id $artefactWindow 'AppStudioAction'
    $result = By-Id $artefactWindow 'AppStudioResult'
    if ($null -eq $action) { throw 'the artefact window has no button' }
    if ($null -eq $result) { throw 'the artefact window has no result field' }
    $action.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $limit = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $limit -and $action.Current.Name -ne $captionRun) { Start-Sleep -Milliseconds 200 }
    $watch.Stop()
    $said = $result.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
    if ($action.Current.Name -ne $captionRun) {
        throw ('the run never finished: the button says "' + $action.Current.Name + '", the title says "' +
            $artefactWindow.Current.Name + '" and the field says "' + $said + '"')
    }
    if ($artefactWindow.Current.Name -notlike ('*' + $stateDone)) {
        throw ('the artefact says it did not finish: ' + $artefactWindow.Current.Name + '  ' + $said)
    }
    if ($said.IndexOf($resultDone, [StringComparison]::Ordinal) -lt 0) { throw ('the artefact reports ' + $said) }

    # The pass condition is the fixture, not what the window says.
    Start-Sleep -Milliseconds 900
    $afterField = (By-Id $window 'CustomerCode').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $afterButton = (By-Id $window 'FirstSave').Current.Name
    if ($afterField -ne $wanted) {
        throw ('the built artefact did not write the field: expected "' + $wanted + '" but the window holds "' + $afterField + '"')
    }
    if ($afterButton -eq $beforeButton) {
        throw ('the built artefact did not press the button: its caption is still "' + $afterButton + '"')
    }
    if ($afterButton -notmatch '^Saved ') { throw ('the button changed to something unexpected: ' + $afterButton) }

    # A run that worked is accounted for too, not only one that failed.
    $log = $built.Path + '.log'
    if (-not (Test-Path -LiteralPath $log)) { throw 'a successful run left no log' }
    $logText = [IO.File]::ReadAllText($log, (New-Object Text.UTF8Encoding($false)))
    if ($logText -notmatch 'DONE') { throw ('the log does not record the run finishing: ' + $logText) }

    # Closing the window is the end of the artefact. Nothing it started stays
    # behind to keep driving the fixture after the operator has walked away.
    $artefactWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $run.WaitForExit(30000)) { throw 'closing the window left the built artefact running' }

    Write-Output ('PASS test-artefact-e2e artefact=' + [IO.Path]::GetFileName($built.Path) + ' bytes=' + $built.Bytes +
        ' window=pressed exit=' + $run.ExitCode + ' elapsedMs=' + $watch.ElapsedMilliseconds +
        ' field="' + $afterField + '" button="' + $beforeButton + '" -> "' + $afterButton + '" log=DONE')
} finally {
    if ($null -ne $fixture) { try { if (-not $fixture.HasExited) { $fixture.Kill() } } catch { } }
    if ($null -ne $run) {
        # Killing cmd is not killing what cmd started.
        try {
            foreach ($id in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $run.Id) | ForEach-Object { [int]$_.ProcessId })) {
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
        try { if (-not $run.HasExited) { $run.Kill() } } catch { }
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
