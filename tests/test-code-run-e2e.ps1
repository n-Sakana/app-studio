$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The whole point of the code screen, checked against a real window: a recording
# is written out as PowerShell, that PowerShell is run the way the operator would
# run it, and the fixture actually changes.
#
# The pass condition is the fixture's own state, not that the script was called.
# A script that runs to the end without moving the application is a failure here.
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-code-run-' + [Guid]::NewGuid().ToString('N'))
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
# The locators are built by the product's own builder, so this test cannot pass
# with addresses the product would never have written.
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

    $button = By-Id $window 'FirstSave'
    $field = By-Id $window 'CustomerCode'
    if ($null -eq $button -or $null -eq $field) { throw 'the fixture does not have the controls this test drives' }
    $beforeButton = $button.Current.Name
    $beforeField = $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($beforeButton -ne 'Save') { throw ('the fixture button did not start as expected: ' + $beforeButton) }

    $windowRect = New-Object AppStudio.RectValue
    $windowRect.X = 0; $windowRect.Y = 0; $windowRect.Width = 900; $windowRect.Height = 700

    # A two step recording: type into a field, then press a button whose caption
    # says how many times it has been pressed.
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'generated code against a real window')
    $session.ValuePolicy = 'recordText'
    $wanted = 'changed-by-generated-code'
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
    $script = $project.Find('powershell', 'RecordedProcedure').Text
    $parsed = [AppStudio.ScriptRun]::CheckPowerShell($script)
    if (-not $parsed.Ok) { throw ('the generated PowerShell does not parse: ' + (($parsed.Problems) -join ' / ')) }

    $result = [AppStudio.ScriptRun]::RunPowerShell($script, (Join-Path $temp 'run'), 120000)
    if (-not $result.Started) { throw ('the generated script was never started: ' + $result.Problem) }
    if ($null -ne $result.Problem) { throw ('the generated script stopped: ' + $result.Problem + "`n" + $result.Output) }
    if (-not $result.Ok) { throw ('the generated script failed with exit ' + $result.ExitCode + ":`n" + $result.Output) }

    Start-Sleep -Milliseconds 800
    $afterButton = (By-Id $window 'FirstSave').Current.Name
    $afterField = (By-Id $window 'CustomerCode').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($afterField -ne $wanted) {
        throw ('the field was not actually written: expected "' + $wanted + '" but the window holds "' + $afterField + '"')
    }
    if ($afterButton -eq $beforeButton) {
        throw ('the button was not actually pressed: its caption is still "' + $afterButton + '"')
    }
    if ($afterButton -notmatch '^Saved ') {
        throw ('the button changed to something unexpected: ' + $afterButton)
    }

    Write-Output ('PASS test-code-run-e2e field="' + $beforeField.Substring(0, 6) + '..." -> "' + $afterField + '" button="' + $beforeButton + '" -> "' + $afterButton + '" exit=' + $result.ExitCode)
} finally {
    if ($null -ne $fixture) { try { if (-not $fixture.HasExited) { $fixture.Kill() } } catch { } }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
