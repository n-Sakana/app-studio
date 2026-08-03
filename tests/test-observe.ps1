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

# The product must have no path at all by which typed text could be captured.
$sourceText = ((Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name) | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
foreach ($forbidden in @('SetWindowsHookEx', 'WH_KEYBOARD', 'GetKeyboardState', 'ToUnicode', 'GetKeyNameText', 'WM_CHAR', 'WM_KEYDOWN', 'GetClipboardData')) {
    if ($sourceText -match [regex]::Escape($forbidden)) { throw ('Forbidden input capture API present in the sources: ' + $forbidden) }
}
$asyncKeyUses = [regex]::Matches($sourceText, 'GetAsyncKeyState\(([^)]*)\)')
foreach ($use in $asyncKeyUses) {
    $argument = $use.Groups[1].Value.Trim()
    if ($argument -ne 'key' -and $argument -ne 'int virtualKey' -and $argument -notmatch 'VK_(L|R|M)BUTTON') { throw ('GetAsyncKeyState is used with something other than a mouse button: ' + $argument) }
}
$vkConstants = [regex]::Matches($sourceText, 'internal const int VK_[A-Z]+')
foreach ($constant in $vkConstants) {
    if ($constant.Value -notmatch 'VK_(L|R|M)BUTTON') { throw ('A non mouse virtual key constant is declared: ' + $constant.Value) }
}

$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-observe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$process = $null
$other = $null
$log = $null
try {
    [AppStudio.Probe]::Configure($root, $false)
    $ready = Join-Path $tempDir 'ready.json'
    $process = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    Start-Sleep -Milliseconds 400
    $fixture = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json

    $log = New-Object AppStudio.SessionLog((Join-Path $tempDir 'log'))
    if (-not $log.Enabled) { throw ('SessionLog could not be created: ' + $log.Status.DisabledReason) }
    $observer = New-Object AppStudio.ObservationRecorder($log)
    $own = [Diagnostics.Process]::GetCurrentProcess().Id
    $observer.Start($process.Id, 'FixtureWinForms')

    function Point-At($rect) { return @([int](($rect.left + $rect.right) / 2), [int](($rect.top + $rect.bottom) / 2)) }
    function Get-CenterOf($controlHandle) {
        $bounds = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$controlHandle)
        if ($null -eq $bounds) { throw 'A fixture control has no rectangle.' }
        $centreX = [int]$bounds.X + [int]([int]$bounds.Width / 2)
        $centreY = [int]$bounds.Y + [int]([int]$bounds.Height / 2)
        return ,@($centreX, $centreY)
    }
    function Observe($x, $y) {
        $snapshot = [AppStudio.Probe]::At($x, $y, 1500)
        $view = [AppStudio.SnapshotAnalysis]::Analyze($snapshot, $x, $y, $own)
        $observer.OnAcquisition($snapshot, $view, $x, $y)
        return @{ Snapshot = $snapshot; View = $view }
    }

    $normal = Get-CenterOf $fixture.normal
    $first = Get-CenterOf $fixture.first
    $toggle = Get-CenterOf $fixture.toggle

    # Pointer resting on one element: one enter, no repeats.
    $null = Observe $normal[0] $normal[1]
    Start-Sleep -Milliseconds 60
    $null = Observe ($normal[0] + 2) ($normal[1] + 1)
    $null = Observe ($normal[0] + 4) ($normal[1] + 2)
    Start-Sleep -Milliseconds 60
    # Moving to another element must produce a leave and a new enter.
    $null = Observe $first[0] $first[1]
    $null = Observe $toggle[0] $toggle[1]

    # A click on the target, with the after state observed from the same point.
    $ownerAtToggle = [AppStudio.WindowTools]::ProcessIdAt($toggle[0], $toggle[1])
    if ($ownerAtToggle -ne $process.Id) {
        # Something else is covering the fixture. Raise it without activating it
        # so no focus is taken from whoever is using the desktop.
        $fixtureWindows = [AppStudio.WindowTools]::ListProcessWindows($process.Id, 0)
        if ($fixtureWindows.Count -lt 1) { throw 'The fixture has no visible window.' }
        $rect = $fixtureWindows[0].Rect
        $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$fixtureWindows[0].Hwnd, $rect.X, $rect.Y, $rect.Width, $rect.Height)
        Start-Sleep -Milliseconds 200
        $ownerAtToggle = [AppStudio.WindowTools]::ProcessIdAt($toggle[0], $toggle[1])
    }
    if ($ownerAtToggle -ne $process.Id) {
        $coveringName = '?'
        try { $coveringName = (Get-Process -Id $ownerAtToggle).ProcessName } catch { }
        throw ('The toggle point belongs to ' + $coveringName + ' (pid ' + $ownerAtToggle + '), not the fixture.')
    }
    $before = $observer.OnMouseDown($toggle[0], $toggle[1], 'left')
    if ($null -eq $before) { throw 'A click inside the target application was not attributed to an element.' }
    Start-Sleep -Milliseconds 120
    $after = Observe $toggle[0] $toggle[1]
    $observer.OnClickOutcome($before, $after.Snapshot, $after.View, $toggle[0], $toggle[1], @('focus.change'), 320)

    # A click outside the target must not be described.
    $otherReady = Join-Path $tempDir 'other.ready'
    $other = Start-Process -FilePath $build.FixtureWin32 -ArgumentList @('--mode', 'healthy', '--ready', $otherReady) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $otherReady) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $otherReady)) { throw 'FixtureWin32 did not become ready.' }
    $otherInfo = Get-Content -LiteralPath $otherReady -Raw | ConvertFrom-Json
    Start-Sleep -Milliseconds 250
    $outside = Point-At $otherInfo.editRect
    $null = $observer.OnMouseDown($outside[0], $outside[1], 'left')
    # Hovering outside the target must not add an element record either.
    $null = Observe $outside[0] $outside[1]

    $observer.SetPaused($true)
    $null = Observe $first[0] $first[1]
    $observer.SetPaused($false)
    $observer.Stop()
    $log.FlushDurable()

    $lines = @(Get-Content -LiteralPath $log.PathFor('observations') -Encoding UTF8)
    $records = @($lines | ForEach-Object { ConvertFrom-Json $_ })
    $kinds = @($records | ForEach-Object { $_.kind })
    foreach ($expected in @('observe.start', 'observe.enter', 'observe.leave', 'observe.click', 'observe.click.result', 'observe.click.outside', 'observe.pause', 'observe.resume', 'observe.stop')) {
        if ($kinds -notcontains $expected) { throw ('Observation log is missing ' + $expected) }
    }
    $enters = @($records | Where-Object { $_.kind -eq 'observe.enter' })
    if ($enters.Count -lt 3) { throw ('Expected at least three element entries, found ' + $enters.Count) }
    if ($enters.Count -gt 5) { throw ('Pointer movement inside one element produced repeated entries: ' + $enters.Count) }
    foreach ($enter in $enters) {
        if ($enter.element.processId -ne $process.Id) { throw 'An element outside the target process was recorded.' }
        if ($null -eq $enter.element.rect) { throw 'An element was recorded without a rectangle.' }
        if ([string]::IsNullOrEmpty($enter.element.key)) { throw 'An element was recorded without an identity key.' }
        if ($null -eq $enter.x -or $null -eq $enter.y) { throw 'An element entry was recorded without coordinates.' }
        if ([string]::IsNullOrEmpty($enter.at)) { throw 'An element entry was recorded without a time.' }
        if ([string]::IsNullOrEmpty($enter.element.route)) { throw 'An element entry was recorded without its acquisition route.' }
    }
    $leaves = @($records | Where-Object { $_.kind -eq 'observe.leave' })
    if (@($leaves | Where-Object { $_.dwellMs -ge 0 }).Count -ne $leaves.Count) { throw 'Dwell time was not recorded.' }
    if (@($leaves | Where-Object { $_.moveSamples -gt 0 }).Count -lt 1) { throw 'Movement inside an element was not counted.' }
    $result = @($records | Where-Object { $_.kind -eq 'observe.click.result' })[0]
    if ($null -eq $result.before -or $null -eq $result.after) { throw 'The click result did not keep both states.' }
    if ($null -eq $result.applicationEvents -or $result.applicationEvents.Count -lt 1) { throw 'Application events around the click were not kept.' }
    $paused = @($records | Where-Object { $_.kind -eq 'observe.enter' -and $_.seq -gt (@($records | Where-Object { $_.kind -eq 'observe.pause' })[0].seq) })
    if ($paused.Count -gt 0) { throw 'Elements were still recorded while observation was paused.' }
    $outsideRecords = @($records | Where-Object { $_.kind -eq 'observe.click.outside' })
    if ($outsideRecords.Count -lt 1) { throw 'A click outside the target was not noted at all.' }
    if ($null -ne $outsideRecords[0].element) { throw 'A click outside the target described an element.' }

    $joined = ($lines -join "`n")
    if ($joined -match 'secret-value-42' -or $joined -match 'P@ssword123' -or $joined -match 'ABC123') { throw 'The observation log leaked a value from a target application.' }

    $rawPath = $log.PathFor('pointer-raw')
    if (-not (Test-Path -LiteralPath $rawPath)) { throw 'The raw pointer trail was not written to its own stream.' }
    $rawLines = @(Get-Content -LiteralPath $rawPath -Encoding UTF8)
    if ($rawLines.Count -lt 1) { throw 'The raw pointer trail is empty.' }
    if ($rawLines.Count -ge $lines.Count) { throw 'The raw trail was not kept separate from the readable log.' }
    foreach ($rawLine in $rawLines) { $null = ConvertFrom-Json $rawLine }

    $status = $observer.Status
    Write-Output ('PASS test-observe records=' + $lines.Count + ' enters=' + $enters.Count + ' leaves=' + $leaves.Count +
        ' clicks=' + $status.ClickCount + ' outsideIgnored=' + $outsideRecords.Count + ' rawTrail=' + $rawLines.Count +
        ' pausedRecorded=0 keyboardCapture=none valueLeak=0')
} finally {
    if ($null -ne $log) { $log.Dispose() }
    [AppStudio.Probe]::Shutdown()
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($null -ne $other -and -not $other.HasExited) { $other.Kill(); $other.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
