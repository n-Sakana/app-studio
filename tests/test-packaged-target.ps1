$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# A packaged application shows itself inside a frame window owned by
# ApplicationFrameHost while its contents are drawn by its own process. Every
# point inside such a window therefore reports a different process than the
# window does, which looks exactly like another window covering the target.
# Refusing on that reading refuses every operation aimed at the window of any
# packaged application, so the refusal has to tell the two apart.
#
# Calculator is used because it is the packaged application every Windows
# install has. Only a window this test opened itself is ever operated or closed,
# and the operation used is focus, which brings a window forward and changes
# nothing inside it.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-packaged-target (operates a real application window; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()

function List-TopWindows($namePattern) {
    $found = @()
    foreach ($item in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name -like $namePattern) { $found += $item }
    }
    return $found
}

$calculator = $null
$calculatorPid = 0
try {
    [AppStudio.Probe]::Configure($root, $false)
    # Anything already on screen belongs to whoever is using the machine.
    $before = @()
    foreach ($item in (@(List-TopWindows '電卓') + @(List-TopWindows 'Calculator'))) { $before += [int64]$item.Current.NativeWindowHandle }
    $calculator = Start-Process -FilePath 'calc.exe' -PassThru
    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in (@(List-TopWindows '電卓') + @(List-TopWindows 'Calculator'))) {
            if ($before -notcontains ([int64]$item.Current.NativeWindowHandle)) { $window = $item; break }
        }
        if ($null -eq $window) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $window) { throw 'Calculator did not open a window this test had not seen before, so there is nothing it may operate.' }
    $handle = [int64]$window.Current.NativeWindowHandle
    $calculatorPid = $window.Current.ProcessId
    Start-Sleep -Milliseconds 800

    $rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$handle)
    if ($null -eq $rect -or $rect.Width -le 0) { throw 'The Calculator window has no rectangle.' }
    $x = [int]$rect.X + [int]([int]$rect.Width / 2)
    $y = [int]$rect.Y + [int]([int]$rect.Height / 2)
    $ownerAtPoint = [AppStudio.WindowTools]::ProcessIdAt($x, $y)
    $framePid = $calculatorPid
    # The whole point of this test is the split between the frame and its
    # contents. If this build of Windows does not split them, the test says so
    # instead of passing on a condition it never met.
    if ($ownerAtPoint -eq $framePid) {
        Write-Output ('SKIP test-packaged-target (this Calculator draws in its own frame process ' + $framePid + ', so there is no frame/content split to tell apart)')
        return
    }
    $content = [AppStudio.WindowTools]::ContentProcessIds([IntPtr][int64]$handle, $framePid)
    if (@($content) -notcontains $ownerAtPoint) { throw ('The process at the window centre (' + $ownerAtPoint + ') is not one of the processes drawing inside it: ' + ($content -join ',')) }

    $reference = New-Object AppStudio.ElementRef
    $reference.X = $x
    $reference.Y = $y
    $reference.Hwnd = $handle
    $arguments = New-Object AppStudio.ProbeArgs
    $arguments.WriteEnabled = $true
    $arguments.BudgetMs = 8000
    $result = [AppStudio.ProbeRunner]::Run($reference, [AppStudio.ProbeKind]::Focus, $arguments)
    if ($result.Method -eq 'policy.covered') {
        throw ('A packaged application was refused as covered by itself: frame process ' + $framePid + ', point owned by ' + $ownerAtPoint)
    }
    if (@('success', 'failed', 'blocked', 'notSupported', 'unknown') -notcontains $result.Outcome) { throw ('Unknown outcome: ' + $result.Outcome) }
    if ($result.Outcome -eq 'blocked') { throw ('The operation was refused for another reason: ' + $result.Method + ' ' + $result.ErrorMessage) }
    # A whole window is not a focusable element, so UI Automation cannot focus it
    # and the ordinary Win32 route has to carry it. Reporting "failed" here would
    # stop any plan that sensibly brings its target forward before using it.
    if ($result.Outcome -eq 'failed') { throw ('Focusing a whole window failed with no route left: ' + $result.Method + ' ' + $result.ErrorMessage) }

    # The refusal still has to fire for a point this window does not draw. A
    # point outside the window is owned by something else entirely, which is the
    # case the guard exists for.
    $outside = New-Object AppStudio.ElementRef
    $outside.X = [int]$rect.X - 40
    $outside.Y = [int]$rect.Y + [int]([int]$rect.Height / 2)
    $outside.Hwnd = $handle
    $outsideOwner = [AppStudio.WindowTools]::ProcessIdAt($outside.X, $outside.Y)
    $guardNote = 'not-testable'
    if ($outsideOwner -ne 0 -and $outsideOwner -ne $framePid -and (@($content) -notcontains $outsideOwner)) {
        $refused = [AppStudio.ProbeRunner]::Run($outside, [AppStudio.ProbeKind]::Click, $arguments)
        if ($refused.Method -ne 'policy.covered') { throw ('A point owned by process ' + $outsideOwner + ' was not refused: ' + $refused.Outcome + ' ' + $refused.Method) }
        $guardNote = 'blocked/policy.covered'
    }

    Write-Output ('PASS test-packaged-target framePid=' + $framePid + ' contentPid=' + $ownerAtPoint +
        ' contentProcesses=' + (@($content) -join ',') + ' focus=' + $result.Outcome + '/' + $result.Method +
        ' foreignPoint=' + $guardNote)
} finally {
    [AppStudio.Probe]::Shutdown()
    if ($calculatorPid -ne 0) {
        $target = Get-Process -Id $calculatorPid -ErrorAction SilentlyContinue
        if ($null -ne $target) { $target.CloseMainWindow() | Out-Null; if (-not $target.WaitForExit(4000)) { $target.Kill() } }
    }
    if ($null -ne $calculator -and -not $calculator.HasExited) { $calculator.Kill(); $calculator.WaitForExit() }
}
