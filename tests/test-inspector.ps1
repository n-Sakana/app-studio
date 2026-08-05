$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The optional inspector, against a real window on the real desktop.
#
# The three things that matter are not "does a box appear". They are: that the
# box cannot be clicked, that it is this application's own window and therefore
# never reaches a picture, and that nothing is left on the desktop afterwards.
# All three are checked here against a fixture that is started and closed by
# this test.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Messages]::Init($root)
[AppStudio.Theme]::Init($root)
$shotDir = Join-Path $PSScriptRoot '.build\inspector'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null

$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
$fixture = $null
$hud = $null
try {
    $ready = Join-Path $shotDir ('ready-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $wait = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $wait) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    # The window is taken from the process itself rather than from the
    # accessibility tree: what is being tested here is a Win32 point probe, and
    # a Win32 handle is the honest way to say where to point it.
    $handle = [IntPtr]::Zero
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($handle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $limit) {
        $fixture.Refresh()
        $handle = $fixture.MainWindowHandle
        if ($handle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'the fixture window never appeared' }
    [AppStudio.WindowTools]::BringToFront($handle.ToInt64()) | Out-Null
    Start-Sleep -Milliseconds 700
    $box = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $box -or $box.Width -le 0) { throw 'the fixture window has no rectangle' }
    $atX = [int]($box.X + $box.Width / 2)
    $atY = [int]($box.Y + $box.Height / 2)

    # ---- 1. what the probe reads, and what it refuses to read ---------------
    $fact = [AppStudio.Inspector]::At($atX, $atY, (New-Object 'long[]' 0))
    if ($null -eq $fact) { throw 'the probe returned nothing over a real control' }
    if (-not $fact.Known) { throw ('the probe found no rectangle: ' + $fact.Problem) }
    if ($fact.ProcessId -ne $fixture.Id) { throw ('the probe named process ' + $fact.ProcessId + ' instead of the fixture') }
    $chip = $fact.Chip()
    if ($chip.Length -lt 4) { throw 'the chip says nothing' }
    # It has to carry enough for a person to tell this component from the one
    # beside it: what kind of thing it is, what it is made of, and whose it is.
    if ($chip.IndexOf('FixtureWinForms', [StringComparison]::Ordinal) -lt 0) {
        throw ('the chip does not name the application: ' + $chip)
    }
    if ([string]::IsNullOrEmpty($fact.ClassName)) { throw ('the chip has no class to identify by: ' + $chip) }
    if ($chip.IndexOf($fact.ClassName.Substring(0, [Math]::Min(12, $fact.ClassName.Length)), [StringComparison]::Ordinal) -lt 0) {
        throw ('the chip does not show the class: ' + $chip)
    }
    # ---- 2. the overlay is this application's own, and click-through --------
    $hud = New-Object AppStudio.RecordHud('Recording', 'Stop')
    if ($hud.OwnHandles.Count -ne 4) { throw ('the recording overlay is ' + $hud.OwnHandles.Count + ' windows instead of 4') }

    # This product never describes itself as if it were the application being
    # recorded. The stop control is a real window of ours; over it, the probe
    # answers with nothing rather than with a chip about App Studio.
    $hud.ShowControl()
    Start-Sleep -Milliseconds 700
    $controlHandle = $hud.OwnHandles[1]
    $controlRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$controlHandle)
    if ($null -ne $controlRect -and $controlRect.Width -gt 0) {
        $ours = [AppStudio.Inspector]::At([int]($controlRect.X + $controlRect.Width / 2), [int]($controlRect.Y + $controlRect.Height / 2), $hud.OwnHandles)
        if ($null -ne $ours) { throw 'the inspector described one of this product own windows' }
    }
    # The panel has to be big enough for what is written on it, at whatever this
    # display is scaled to. WPF lays out in one unit and SetWindowPos places in
    # another; when the panel was placed with a constant, a 125 per cent display
    # cut the right hand edge and the lower half off the stop control. This is
    # the check that holds whatever the scale is: every control the panel offers
    # is wholly inside the panel.
    $panel = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$controlHandle)
    $panelControls = $panel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    if ($panelControls.Count -lt 4) {
        throw ('the recording panel offers ' + $panelControls.Count + ' controls; stop, pause, the pointer reader and its details are all meant to be on it')
    }
    $panelNames = @()
    foreach ($item in $panelControls) {
        $panelNames += $item.Current.Name
        $cr = $item.Current.BoundingRectangle
        if ($cr.X -lt $controlRect.X -or $cr.Y -lt $controlRect.Y) {
            throw ('"' + $item.Current.Name + '" starts outside the recording panel')
        }
        if (($cr.X + $cr.Width) -gt ($controlRect.X + $controlRect.Width)) {
            throw ('"' + $item.Current.Name + '" is cut off at the right of the recording panel: ' +
                [int]($cr.X + $cr.Width) + ' > ' + [int]($controlRect.X + $controlRect.Width))
        }
        if (($cr.Y + $cr.Height) -gt ($controlRect.Y + $controlRect.Height)) {
            throw ('"' + $item.Current.Name + '" is cut off at the bottom of the recording panel: ' +
                [int]($cr.Y + $cr.Height) + ' > ' + [int]($controlRect.Y + $controlRect.Height))
        }
    }
    # The pointer reader says its own name and which of the two states it is in,
    # on the panel, without anything being opened.
    $joinedPanel = ($panelNames -join ' | ')
    foreach ($word in @((Message 'hud-stop.txt' 'Stop'), (Message 'hud-pause.txt' 'Pause'), (Message 'hud-inspect.txt' 'Pointer info'))) {
        if ($joinedPanel.IndexOf($word, [StringComparison]::Ordinal) -lt 0) {
            throw ('the recording panel does not offer "' + $word + '": ' + $joinedPanel)
        }
    }
    if ($joinedPanel.IndexOf((Message 'hud-off.txt' 'off'), [StringComparison]::Ordinal) -lt 0) {
        throw ('the recording panel does not say whether the pointer reader is on or off: ' + $joinedPanel)
    }

    $hud.ShowInspect($fact, $atX, $atY)
    Start-Sleep -Milliseconds 900

    # The chip is sized the same way, and its first line is what a person reads.
    $chipRect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hud.OwnHandles[3])
    if ($null -eq $chipRect) { throw 'the chip was not placed at all' }
    if ($chipRect.Width -lt 80 -or $chipRect.Height -lt 30) {
        throw ('the chip is ' + $chipRect.Width + 'x' + $chipRect.Height + ', which is too small to hold a line of text')
    }

    # The pointer test the recorder uses must still find the fixture under the
    # glow. If it found App Studio, the press would be thrown away as this
    # product's own and the recording would lose it.
    $owner = [AppStudio.WindowTools]::ProcessIdAt($atX, $atY)
    if ($owner -ne $fixture.Id) {
        throw ('the overlay took ownership of the point: process ' + $owner + ' answered instead of the fixture')
    }
    $glowVisible = $false
    foreach ($handle in $hud.OwnHandles) {
        if ([AppStudio.WindowTools]::IsVisible($handle)) { $glowVisible = $true }
    }
    if (-not $glowVisible) { throw 'nothing was drawn' }
    # A picture of the desktop where the fixture is, so the outline and the chip
    # can be looked at rather than only asserted about. It is a fixture window
    # this test started, so nothing of the operator's is in it.
    $frame = New-Object AppStudio.RectValue
    $frame.X = $box.X - 30; $frame.Y = $box.Y - 30; $frame.Width = $box.Width + 60; $frame.Height = $box.Height + 120
    $shot = [AppStudio.Capture]::Crop($frame, (New-Object 'AppStudio.MaskRect[]' 0), (Join-Path $shotDir 'inspector.png'), [IntPtr]::Zero)

    # ---- 3. it goes away for a picture, and comes back --------------------
    $hud.Suppress($true)
    Start-Sleep -Milliseconds 500
    if (-not $hud.Hidden) { throw 'the overlay was still on screen while a picture was being taken' }
    $hud.Suppress($false)
    Start-Sleep -Milliseconds 400

    # ---- 4. off means off ---------------------------------------------------
    $hud.HideInspect()
    Start-Sleep -Milliseconds 600
    $stillDrawn = 0
    foreach ($handle in $hud.OwnHandles) {
        if ([AppStudio.WindowTools]::IsVisible($handle)) { $stillDrawn++ }
    }
    # The stop control stays up while a recording runs; the inspector must not.
    if ($stillDrawn -gt 1) { throw ('turning the inspector off left ' + $stillDrawn + ' windows drawn') }

    # ---- 5. nothing is left behind -----------------------------------------
    $handles = $hud.OwnHandles
    $hud.Dispose()
    $hud = $null
    Start-Sleep -Milliseconds 800
    $left = 0
    foreach ($handle in $handles) {
        if ([AppStudio.WindowTools]::IsVisible($handle)) { $left++ }
    }
    if ($left -gt 0) { throw ($left + ' overlay windows were left on the desktop after the recording ended') }

    Write-Output ('PASS test-inspector windows=4 clickThrough=1 ownProcess=' + $fixture.Id + ' chip="' + $chip + '" suppressed=1 offIsOff=1 leftBehind=0')
} finally {
    if ($null -ne $hud) { try { $hud.Dispose() } catch { } }
    if ($null -ne $fixture) { try { if (-not $fixture.HasExited) { $fixture.Kill() } } catch { } }
}
