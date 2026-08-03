$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Messages]::Init($root)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-scan-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$process = $null
$canvas = $null
$runner = $null
$log = $null
try {
    $ready = Join-Path $tempDir 'ready.json'
    $process = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    Start-Sleep -Milliseconds 400

    $windows = [AppStudio.WindowTools]::ListProcessWindows($process.Id, 0)
    if ($windows.Count -lt 1) { throw 'ListProcessWindows returned no window for the fixture.' }

    $limits = New-Object AppStudio.ScanLimits
    $limits.UiaBudgetMs = 15000
    $limits.MsaaBudgetMs = 8000
    $runner = New-Object AppStudio.ScanRunner($root)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = $runner.Run($process.Id, $windows[0].Hwnd, $limits, $null)
    $watch.Stop()

    if ($result.Nodes.Count -lt 20) { throw ('Scan found only ' + $result.Nodes.Count + ' elements in the WinForms fixture.') }
    $names = @($result.Nodes | ForEach-Object { $_.Name })
    foreach ($expected in @('Save', 'No effect', 'Feature', 'Customer code', 'Password')) {
        if ($names -notcontains $expected) { throw ('Scan did not find the element named ' + $expected) }
    }
    $automationIds = @($result.Nodes | Where-Object { $_.AutomationId } | ForEach-Object { $_.AutomationId })
    foreach ($expected in @('CustomerCode', 'FirstSave', 'FeatureToggle', 'CustomerList')) {
        if ($automationIds -notcontains $expected) { throw ('Scan did not record AutomationId ' + $expected) }
    }
    $password = @($result.Nodes | Where-Object { $_.AutomationId -eq 'PasswordField' })
    if ($password.Count -lt 1) { throw 'Password field was not found.' }
    if (-not $password[0].IsPassword) { throw 'Password field was not flagged as a password element.' }
    foreach ($node in $result.Nodes) {
        if ($node.ValueKind -ne 'not-read' -and $null -ne $node.ValueKind) { throw ('A scan node carried a value kind: ' + $node.ValueKind) }
    }
    $withHwnd = @($result.Nodes | Where-Object { $_.Hwnd -ne 0 }).Count
    $withoutHwnd = $result.Nodes.Count - $withHwnd
    if ($withHwnd -lt 1) { throw 'No scanned element reported a window handle.' }
    if ($withoutHwnd -lt 1) { throw 'Every scanned element had a window handle; handle-less elements are not being covered.' }
    $patterned = @($result.Nodes | Where-Object { $_.Patterns -and $_.Patterns.Count -gt 0 }).Count
    if ($patterned -lt 3) { throw 'Operable patterns were not recorded.' }
    $uiaCoverage = $result.CoverageFor('uia')
    if ($uiaCoverage.State -eq 'unavailable') { throw ('UIA coverage was unavailable: ' + ($uiaCoverage.Reasons | ForEach-Object { $_.Code }) -join ',') }
    $win32Coverage = $result.CoverageFor('win32')
    if ($win32Coverage.NodeCount -lt 5) { throw ('Win32 child enumeration returned only ' + $win32Coverage.NodeCount + ' windows.') }
    $paths = @($result.Nodes | Where-Object { $_.Path -and $_.Path.Contains(' > ') }).Count
    if ($paths -lt 5) { throw 'Hierarchy paths were not built.' }
    # Merging renumbers the nodes, so the parent links have to be remapped.
    $linked = 0
    for ($index = 0; $index -lt $result.Nodes.Count; $index++) {
        $node = $result.Nodes[$index]
        if ($node.NodeId -ne $index) { throw ('Node ' + $index + ' kept the identifier ' + $node.NodeId) }
        if ($node.ParentId -lt -1 -or $node.ParentId -ge $result.Nodes.Count) { throw ('Node ' + $index + ' points at parent ' + $node.ParentId) }
        if ($node.ParentId -eq $index) { throw ('Node ' + $index + ' is its own parent.') }
        if ($node.ParentId -ge 0) {
            $linked++
            $parent = $result.Nodes[$node.ParentId]
            if ($parent.Path -and $node.Path -and -not $node.Path.StartsWith($parent.Path)) { throw ('Node ' + $index + ' path does not continue its parent path.') }
        }
    }
    if ($linked -lt 10) { throw ('Only ' + $linked + ' nodes kept a parent after merging.') }

    # Owner drawn window: the tree is empty, so coordinate sampling must engage
    # and the result must say so instead of silently returning nothing.
    $canvasReady = Join-Path $tempDir 'canvas.ready'
    $canvas = Start-Process -FilePath $build.FixtureCanvas -ArgumentList @('--ready', $canvasReady) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $canvasReady) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $canvasReady)) { throw 'FixtureCanvas did not become ready.' }
    Start-Sleep -Milliseconds 300
    $canvasWindows = [AppStudio.WindowTools]::ListProcessWindows($canvas.Id, 0)
    if ($canvasWindows.Count -lt 1) { throw 'FixtureCanvas exposed no window.' }
    $canvasLimits = New-Object AppStudio.ScanLimits
    $canvasLimits.HitTestBudgetMs = 8000
    $canvasResult = $runner.Run($canvas.Id, $canvasWindows[0].Hwnd, $canvasLimits, $null)
    $hitCoverage = $canvasResult.CoverageFor('hit-test')
    if ($hitCoverage.State -eq 'skipped') { throw 'Coordinate sampling was skipped on an owner drawn window.' }
    if ($hitCoverage.Reasons.Count -lt 1) { throw 'Coordinate sampling did not report its grid limits.' }
    if ($canvasResult.Nodes.Count -lt 1) { throw 'Owner drawn window produced no element at all.' }

    # The machine readable log has to be written without any save action.
    $log = New-Object AppStudio.SessionLog((Join-Path $tempDir 'log'))
    if (-not $log.Enabled) { throw ('SessionLog could not be created: ' + $log.Status.DisabledReason) }
    foreach ($node in $result.Nodes) { $null = $log.Append('elements', [AppStudio.ScanJson]::Node($node, $result.ScanId, 0)) }
    $null = $log.Append('events', [AppStudio.ScanJson]::Summary($result))
    $summaryText = [AppStudio.ScanSummary]::Build($result)
    if (-not $log.WriteText('summary.md', $summaryText)) { throw 'Summary could not be written.' }
    $log.FlushDurable()
    $elementsPath = $log.PathFor('elements')
    $lines = @(Get-Content -LiteralPath $elementsPath -Encoding UTF8)
    if ($lines.Count -ne $result.Nodes.Count) { throw ('Element log line count ' + $lines.Count + ' does not match ' + $result.Nodes.Count) }
    foreach ($line in $lines) { $null = ConvertFrom-Json $line }
    $first = ConvertFrom-Json $lines[0]
    foreach ($field in @('seq', 'at', 'kind', 'scanId', 'nodeId', 'sources', 'rect', 'controlType', 'hasHwnd')) {
        if (-not ($first.PSObject.Properties.Name -contains $field)) { throw ('Element record is missing the field ' + $field) }
    }
    $rawSummary = Get-Content -LiteralPath (Join-Path $log.Folder 'summary.md') -Raw -Encoding UTF8
    if ($rawSummary -notmatch 'secret-value-42' -and $rawSummary -notmatch 'P@ssword123') { } else { throw 'The summary leaked a live value.' }
    $joined = ($lines -join "`n")
    if ($joined -match 'secret-value-42' -or $joined -match 'P@ssword123') { throw 'The element log leaked a live value.' }

    Write-Output ('PASS test-scan elements=' + $result.Nodes.Count + ' windows=' + $result.Windows.Count + ' durationMs=' + $result.DurationMs +
        ' uia=' + $uiaCoverage.State + '/' + $uiaCoverage.NodeCount + ' msaa=' + $result.CoverageFor('msaa').State + '/' + $result.CoverageFor('msaa').NodeCount +
        ' win32=' + $win32Coverage.State + '/' + $win32Coverage.NodeCount + ' withHwnd=' + $withHwnd + ' withoutHwnd=' + $withoutHwnd +
        ' patterns=' + $patterned + ' canvasElements=' + $canvasResult.Nodes.Count + ' canvasHitTest=' + $hitCoverage.State + '/' + $hitCoverage.NodeCount +
        ' logLines=' + $lines.Count + ' valueLeak=0')
} finally {
    if ($null -ne $log) { $log.Dispose() }
    if ($null -ne $runner) { $runner.Dispose() }
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($null -ne $canvas -and -not $canvas.HasExited) { $canvas.Kill(); $canvas.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
