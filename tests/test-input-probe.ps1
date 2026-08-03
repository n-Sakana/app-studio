$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The only test that emits real mouse and keyboard input. It is aimed at a
# window built for the purpose, the window is placed under wherever the pointer
# already is so the pointer barely moves, and the original pointer position is
# put back afterwards whatever happens.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
# Restoring the pointer is a test concern, so the declaration lives here rather
# than widening the product surface with a cursor mover.
Add-Type -Namespace PuiTest -Name Cursor -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool SetCursorPos(int x, int y);
'@
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-input-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$target = $null
$cover = $null
$startCursor = [AppStudio.WindowTools]::CursorPosition()
if ($null -eq $startCursor) { throw 'The pointer position could not be read.' }
try {
    [AppStudio.Probe]::Configure($root, $false)
    $ready = Join-Path $tempDir 'ready.json'
    $events = Join-Path $tempDir 'events.jsonl'
    $target = Start-Process -FilePath $build.FixtureInputTarget -ArgumentList @('--ready', $ready, '--events', $events, '--expect', 'Z') -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureInputTarget did not become ready.' }
    Start-Sleep -Milliseconds 400
    $info = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json

    # The fixture reports its own coordinates, which are scaled ones on a high
    # DPI display. Everything below works from physical rectangles read back
    # from the window handles instead.
    function Center($handle) {
        $bounds = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$handle)
        if ($null -eq $bounds) { throw 'A fixture control has no rectangle.' }
        $reference = New-Object AppStudio.ElementRef
        $reference.X = [int]$bounds.X + [int]([int]$bounds.Width / 2)
        $reference.Y = [int]$bounds.Y + [int]([int]$bounds.Height / 2)
        $reference.Hwnd = [int64]$handle
        return $reference
    }
    function WriteArgs([string]$value) {
        $arguments = New-Object AppStudio.ProbeArgs
        $arguments.WriteEnabled = $true
        $arguments.Value = $value
        $arguments.BudgetMs = 5000
        return $arguments
    }

    # Move the window so its click surface sits under the pointer that is
    # already there; the synthetic click then barely moves the pointer.
    $windowRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$info.window)
    $surfaceRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$info.surface)
    $offsetX = $startCursor.X - ($surfaceRect.X + [int]($surfaceRect.Width / 2))
    $offsetY = $startCursor.Y - ($surfaceRect.Y + [int]($surfaceRect.Height / 2))
    $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$info.window, ($windowRect.X + $offsetX), ($windowRect.Y + $offsetY), $windowRect.Width, $windowRect.Height)
    Start-Sleep -Milliseconds 300

    $surface = Center $info.surface
    $entry = Center $info.entry

    # Nothing is emitted until the points provably belong to the test window.
    foreach ($point in @($surface, $entry)) {
        $owner = [AppStudio.WindowTools]::ProcessIdAt($point.X, $point.Y)
        if ($owner -ne $target.Id) { throw ('The point ' + $point.X + ',' + $point.Y + ' belongs to pid ' + $owner + ', not the test window; no input was sent.') }
    }

    # A bare panel offers no invoke pattern and is not a BUTTON class, so the
    # probe has to fall through to a real SendInput click to reach it.
    $click = [AppStudio.ProbeRunner]::Run($surface, [AppStudio.ProbeKind]::Click, (WriteArgs ''))
    if ($click.Method -ne 'win32.SendInput.click') { throw ('The click did not take the input fallback: ' + $click.Method) }
    if (@('success', 'unknown') -notcontains $click.Outcome) { throw ('Unexpected click outcome: ' + $click.Outcome + ' ' + $(if ($click.Error) { $click.Error.Message })) }

    $keys = [AppStudio.ProbeRunner]::Run($entry, [AppStudio.ProbeKind]::Keys, (WriteArgs 'Z'))
    if ($keys.Method -ne 'win32.SendInput.keys') { throw ('The keys probe did not take the input fallback: ' + $keys.Method) }
    Start-Sleep -Milliseconds 400

    if (-not (Test-Path -LiteralPath $events)) { throw 'The target window recorded nothing at all.' }
    $records = @(Get-Content -LiteralPath $events -Encoding UTF8 | ForEach-Object { ConvertFrom-Json $_ })
    $clicks = @($records | Where-Object { $_.kind -eq 'click' })
    if ($clicks.Count -lt 1) { throw 'The synthetic click never reached the target window.' }
    if ($clicks[0].button -ne 'left') { throw ('The target saw a ' + $clicks[0].button + ' click.') }
    $dx = [Math]::Abs($clicks[0].screenX - $surface.X)
    $dy = [Math]::Abs($clicks[0].screenY - $surface.Y)
    if ($dx -gt 4 -or $dy -gt 4) { throw ('The click landed at ' + $clicks[0].screenX + ',' + $clicks[0].screenY + ' instead of ' + $surface.X + ',' + $surface.Y) }
    $keyRecords = @($records | Where-Object { $_.kind -eq 'key' })
    if ($keyRecords.Count -lt 1) { throw 'The synthetic key never reached the target window.' }
    if (-not $keyRecords[0].matchedExpected) { throw 'The target received a key, but not the one that was sent.' }

    $joined = (Get-Content -LiteralPath $events -Raw -Encoding UTF8)
    if ($joined -match '"char"' -or $joined -match '"text"') { throw 'The target recorded key content.' }

    # A covered point must not be operated: acting there would reach whatever
    # window came to the front instead of the element that was chosen.
    $coverReady = Join-Path $tempDir 'cover.json'
    $coverEvents = Join-Path $tempDir 'cover.jsonl'
    $cover = Start-Process -FilePath $build.FixtureInputTarget -ArgumentList @('--ready', $coverReady, '--events', $coverEvents, '--expect', 'Q') -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $coverReady) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $coverReady)) { throw 'The covering window did not become ready.' }
    $coverInfo = Get-Content -LiteralPath $coverReady -Raw | ConvertFrom-Json
    $coverWindow = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$coverInfo.window)
    $coverSurface = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$coverInfo.surface)
    $shiftX = $entry.X - ($coverSurface.X + [int]($coverSurface.Width / 2))
    $shiftY = $entry.Y - ($coverSurface.Y + [int]($coverSurface.Height / 2))
    $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$coverInfo.window, ($coverWindow.X + $shiftX), ($coverWindow.Y + $shiftY), $coverWindow.Width, $coverWindow.Height)
    Start-Sleep -Milliseconds 400
    if ([AppStudio.WindowTools]::ProcessIdAt($entry.X, $entry.Y) -ne $cover.Id) { throw 'The covering window did not take the point.' }
    $covered = [AppStudio.ProbeRunner]::Run($entry, [AppStudio.ProbeKind]::Click, (WriteArgs ''))
    if ($covered.Outcome -ne 'blocked' -or $covered.Method -ne 'policy.covered') { throw ('A covered point was operated anyway: ' + $covered.Outcome + ' ' + $covered.Method) }
    Start-Sleep -Milliseconds 300
    if (Test-Path -LiteralPath $coverEvents) {
        $coverRecords = @(Get-Content -LiteralPath $coverEvents -Encoding UTF8 | ForEach-Object { ConvertFrom-Json $_ } | Where-Object { $_.kind -eq 'click' })
        if ($coverRecords.Count -gt 0) { throw 'Input reached the covering window.' }
    }

    $endCursor = [AppStudio.WindowTools]::CursorPosition()
    Write-Output ('PASS test-input-probe clickDelivered=1 at=' + $clicks[0].screenX + ',' + $clicks[0].screenY +
        ' method=' + $click.Method + '/' + $click.Outcome + ' keyDelivered=1 matched=true method=' + $keys.Method + '/' + $keys.Outcome +
        ' pointerMoved=' + [Math]::Abs($endCursor.X - $startCursor.X) + ',' + [Math]::Abs($endCursor.Y - $startCursor.Y) +
        ' coveredPoint=blocked/policy.covered wrongWindowReceived=0 keyContentStored=0')
} finally {
    [AppStudio.Probe]::Shutdown()
    if ($null -ne $target -and -not $target.HasExited) { $target.Kill(); $target.WaitForExit() }
    if ($null -ne $cover -and -not $cover.HasExited) { $cover.Kill(); $cover.WaitForExit() }
    # Always give the pointer back to whoever was using it.
    if ($null -ne $startCursor) { $null = [PuiTest.Cursor]::SetCursorPos($startCursor.X, $startCursor.Y) }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
