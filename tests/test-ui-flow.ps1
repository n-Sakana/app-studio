$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the real product window through its own steps. The buttons are pressed
# through UI Automation, so the physical pointer is never moved and no key is
# ever sent to anything on the desktop.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$shotDir = Join-Path $PSScriptRoot '.build\ui-flow'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$fixture = $null
$app = $null
$liveRoot = Join-Path $root 'runtime\live-session'
$before = @{}
if (Test-Path -LiteralPath $liveRoot) { Get-ChildItem -LiteralPath $liveRoot -Directory | ForEach-Object { $before[$_.Name] = $true } }

function Find-Descendants($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Button($window, $label) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Button)) {
        if ($item.Current.Name -eq $label -and -not $item.Current.IsOffscreen) { return $item }
    }
    return $null
}
function Wait-Button($window, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $button = Find-Button $window $label
        if ($null -ne $button) { return $button }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Press($button, $label) {
    if ($null -eq $button) { throw ('Button not found: ' + $label) }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 350
}
function Shoot($window, $name) {
    # Pictures of the window also show the operator's own window list, so they
    # are only taken when they are explicitly asked for.
    if ($env:APPSTUDIO_UI_SHOTS -ne '1') { return $null }
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $path = Join-Path $shotDir ($name + '.png')
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    $shot = [AppStudio.Capture]::Crop($rect, $masks, $path, $handle)
    return $shot
}
function Text-Of($element) {
    try {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $pattern.Current.Value
    } catch {
        return $null
    }
}

try {
    $ready = Join-Path $tempDir 'ready.json'
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', (Join-Path $root 'app-studio.ps1'), '-AutoCloseMs', '180000') -PassThru -WindowStyle Hidden

    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1200
    $shotTarget = Shoot $window 'step1-target'

    # Step 1: choose the fixture window from the list.
    $chosen = $null
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ($null -eq $chosen -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if ($item.Current.Name -like '*FixtureWinForms*') { $chosen = $item; break }
        }
        if ($null -eq $chosen) {
            $refresh = Find-Button $window '一覧を更新'
            if ($null -ne $refresh) { Press $refresh '一覧を更新' }
            Start-Sleep -Milliseconds 400
        }
    }
    if ($null -eq $chosen) { throw 'The fixture window was not offered in the target list.' }
    $selection = $chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 200
    Press (Find-Button $window 'この画面を調べる') 'この画面を調べる'
    $shotMenu = Shoot $window 'step2-menu'

    # Each start button is named after its own purpose, so the one that is meant
    # gets pressed instead of whichever identical button happens to come first.
    $purposes = @('自動でひととおり洗い出す', '自分で操作しながら記録する', '部品をひとつ試しに操作する')
    $buttons = @(Find-Descendants $window ([System.Windows.Automation.ControlType]::Button))
    $starts = @()
    foreach ($purpose in $purposes) {
        $hit = $null
        foreach ($item in $buttons) { if ($item.Current.Name -eq $purpose) { $hit = $item; break } }
        if ($null -eq $hit) { throw ('No start button is named for the purpose: ' + $purpose) }
        $starts += $hit
    }
    if ($starts.Count -lt 3) { throw ('Expected three purposes on the menu, found ' + $starts.Count) }

    # Step 2a: automatic scan.
    Press $starts[0] '自動でひととおり洗い出す'
    $shotScan = Shoot $window 'step3-scanning'
    $again = Wait-Button $window 'もう一度調べる' 120000
    if ($null -eq $again) { throw 'The scan never reached its result step.' }
    Start-Sleep -Milliseconds 800
    $shotResult = Shoot $window 'step4-result'

    $summaryText = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        $value = Text-Of $item
        if ($null -ne $value -and $value.Contains('自動調査の結果')) { $summaryText = $value }
    }
    if ($null -eq $summaryText) { throw 'The result summary was not shown on screen.' }
    foreach ($expected in @('見つけた部品', '種類ごとの内訳', '取得できなかった', 'これで全部だという保証はありません')) {
        if (-not $summaryText.Contains($expected)) { throw ('The summary is missing the section: ' + $expected) }
    }

    # The session must already be on disk without any save action.
    $sessionDir = $null
    foreach ($directory in (Get-ChildItem -LiteralPath $liveRoot -Directory)) {
        if (-not $before.ContainsKey($directory.Name)) { $sessionDir = $directory.FullName }
    }
    if ($null -eq $sessionDir) { throw 'No session folder was created.' }
    $elementsPath = Join-Path $sessionDir 'elements.jsonl'
    # The records are written off the interface thread, so give them a moment.
    $elementLines = @()
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($elementLines.Count -lt 20 -and [DateTime]::UtcNow -lt $limit) {
        Start-Sleep -Milliseconds 400
        if (Test-Path -LiteralPath $elementsPath) { $elementLines = @(Get-Content -LiteralPath $elementsPath -Encoding UTF8) }
    }
    if (-not (Test-Path -LiteralPath $elementsPath)) { throw 'The scan wrote no element log.' }
    if ($elementLines.Count -lt 20) { throw ('Only ' + $elementLines.Count + ' elements were written.') }
    $eventLines = @(Get-Content -LiteralPath (Join-Path $sessionDir 'events.jsonl') -Encoding UTF8)
    $eventKinds = @($eventLines | ForEach-Object { (ConvertFrom-Json $_).kind })
    foreach ($expected in @('session.start', 'target.select', 'scan.start', 'scan.summary')) {
        if ($eventKinds -notcontains $expected) { throw ('The event log is missing ' + $expected) }
    }
    $summaryFiles = @(Get-ChildItem -LiteralPath $sessionDir -Filter 'summary-*.md')
    if ($summaryFiles.Count -lt 1) { throw 'No human readable summary was written.' }

    # A read only operation test, reached from the scan result list.
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Group)) {
        if ($item.Current.Name -like '*見つけた部品*') {
            $expand = $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $expand.Expand()
        }
    }
    Start-Sleep -Milliseconds 400
    $part = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
        if (-not $item.Current.IsOffscreen -and $item.Current.Name -like '*ボタン*') { $part = $item; break }
    }
    if ($null -eq $part) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if (-not $item.Current.IsOffscreen) { $part = $item; break }
        }
    }
    if ($null -eq $part) { throw 'The found parts list offered nothing to operate.' }
    $partName = $part.Current.Name
    ($part.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 200
    Press (Find-Button $window '選んだ部品を試しに操作する') '選んだ部品を試しに操作する'
    $runButton = Wait-Button $window '実行する' 10000
    if ($null -eq $runButton) { throw 'The operation step was not reached from the result list.' }
    Press $runButton '実行する'
    # The read probe re-resolves the element and observes it again, which can
    # take several seconds on a loaded machine.
    $operationEvents = @()
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($operationEvents.Count -lt 1 -and [DateTime]::UtcNow -lt $limit) {
        Start-Sleep -Milliseconds 500
        $operationEvents = @(Get-Content -LiteralPath (Join-Path $sessionDir 'events.jsonl') -Encoding UTF8 |
            ForEach-Object { ConvertFrom-Json $_ } | Where-Object { $_.kind -eq 'operation.result' })
    }
    if ($operationEvents.Count -lt 1) { throw 'The operation test wrote no result.' }
    $operation = $operationEvents[$operationEvents.Count - 1]
    if ($operation.operation -ne 'read') { throw ('The default operation was not a read: ' + $operation.operation) }
    if ($operation.writeEnabled) { throw 'The operation ran with changing operations enabled.' }
    if ([string]::IsNullOrEmpty($operation.method)) { throw 'The operation result kept no route.' }
    if (@('success', 'failed', 'blocked', 'notSupported', 'unknown') -notcontains $operation.outcome) { throw ('Unknown outcome value: ' + $operation.outcome) }
    $shotOperationResult = Shoot $window 'step7-operation-result'
    Press (Find-Button $window '別のことをする') '別のことをする'

    # Step 2b: manual observation start and stop through the same menu.
    Press (Find-Button $window '自分で操作しながら記録する') '自分で操作しながら記録する'
    if ($env:APPSTUDIO_UI_HOVER -eq '1') {
        # The pointer is never moved. The fixture window is moved so that one of
        # its controls comes to rest under wherever the pointer already is.
        $spot = [AppStudio.WindowTools]::CursorPosition()
        $fixtureWindows = [AppStudio.WindowTools]::ListProcessWindows($fixture.Id, 0)
        if ($null -ne $spot -and $fixtureWindows.Count -ge 1) {
            $bounds = $fixtureWindows[0].Rect
            $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$fixtureWindows[0].Hwnd, ($spot.X - [int]($bounds.Width / 2)), ($spot.Y - [int]($bounds.Height / 2)), $bounds.Width, $bounds.Height)
            Start-Sleep -Milliseconds 3000
        }
    }
    $shotObserve = Shoot $window 'step5-observe'
    $stop = Wait-Button $window '終わる' 10000
    if ($null -eq $stop) { throw 'The observation step did not appear.' }
    Press $stop '終わる'
    $observationPath = Join-Path $sessionDir 'observations.jsonl'
    if (-not (Test-Path -LiteralPath $observationPath)) { throw 'Manual observation wrote no log.' }
    $observationKinds = @(Get-Content -LiteralPath $observationPath -Encoding UTF8 | ForEach-Object { (ConvertFrom-Json $_).kind })
    if ($observationKinds -notcontains 'observe.start' -or $observationKinds -notcontains 'observe.stop') { throw 'The observation log has no start and stop.' }
    $hoverNote = 'wiring-only'
    if ($env:APPSTUDIO_UI_HOVER -eq '1' -and $observationKinds -notcontains 'observe.enter') { throw 'The pointer was over the target but nothing was recorded.' }
    if ($observationKinds -contains 'observe.enter') { $hoverNote = 'recorded' }
    if (-not (Test-Path -LiteralPath (Join-Path $sessionDir 'observation-summary.md'))) { throw 'No observation summary was written.' }
    # Stopping now lands on the review of what was just recorded, read back from
    # the file, rather than dropping straight back to the menu.
    $reviewShown = $false
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        $value = Text-Of $item
        if ($null -ne $value -and $value.Contains('手動観察')) { $reviewShown = $true }
    }
    if (-not $reviewShown) { throw 'Stopping the recording showed no review of it.' }
    Press (Find-Button $window '別のことをする') '別のことをする'

    # Step 2c: the operation test must be reachable and start read only.
    Press (Find-Button $window '部品をひとつ試しに操作する') '部品をひとつ試しに操作する'
    $shotOperate = Shoot $window 'step6-operate'
    $run = Wait-Button $window '実行する' 10000
    if ($null -eq $run) { throw 'The operation step did not appear.' }
    $writeToggle = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::CheckBox)) {
        if ($item.Current.Name -like '*対象を変える操作を許可*') { $writeToggle = $item }
    }
    if ($null -eq $writeToggle) { throw 'The write permission switch is not on the operation step.' }
    $toggleState = $writeToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggleState.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Changing operations were allowed without being asked for.' }

    $shots = @($shotTarget, $shotMenu, $shotScan, $shotResult, $shotObserve, $shotOperate, $shotOperationResult)
    $okShots = @($shots | Where-Object { $null -ne $_ -and $_.Status.State -eq 'ok' }).Count
    $shotNote = if ($env:APPSTUDIO_UI_SHOTS -eq '1') { $okShots.ToString() + '/7' } else { 'off' }

    $windowPattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
    Start-Sleep -Milliseconds 800

    Write-Output ('PASS test-ui-flow steps=target,menu,scan,result,operate-read,observe elements=' + $elementLines.Count +
        ' operated="' + $partName + '"->' + $operation.outcome + '/' + $operation.method + ' hover=' + $hoverNote +
        ' events=' + $eventLines.Count + ' summaries=' + $summaryFiles.Count + ' observation=start+stop writeDefault=off shots=' + $shotNote + ' folder=' + $sessionDir)
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $fixture -and -not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
