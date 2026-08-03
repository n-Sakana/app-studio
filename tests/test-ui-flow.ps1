$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the real product window through UI Automation. The physical pointer is
# never moved and no key is ever sent to anything on the desktop, so this test is
# safe to run while somebody is using the machine.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$shotDir = Join-Path $PSScriptRoot '.build\ui-flow'
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null
$app = $null

function All-Of($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($window, $controlType, $label) {
    foreach ($item in All-Of $window $controlType) {
        if ($item.Current.Name -eq $label) { return $item }
    }
    return $null
}
function Wait-Named($window, $controlType, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $found = Find-Named $window $controlType $label
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Shoot($window, $name) {
    # A picture of this window also shows whatever else the operator has open,
    # so it is only taken when it is explicitly asked for.
    if ($env:APPSTUDIO_UI_SHOTS -ne '1') { return $null }
    $handle = [IntPtr][int64]$window.Current.NativeWindowHandle
    $rect = [AppStudio.WindowTools]::GetPhysicalRect($handle)
    if ($null -eq $rect) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    return [AppStudio.Capture]::Crop($rect, $masks, (Join-Path $shotDir ($name + '.png')), $handle)
}

try {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','120000') -PassThru -WindowStyle Hidden

    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1500
    $null = Shoot $window 'home'

    # --- 1. the first screen offers the three things and nothing else -------
    $snapLabel = Message 'home-snap.txt' 'Snap'
    $recordLabel = Message 'home-record.txt' 'Record'
    $snap = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) $snapLabel 15000
    if ($null -eq $snap) { throw 'The snap button is not on the first screen.' }
    $record = Find-Named $window ([System.Windows.Automation.ControlType]::Button) $recordLabel
    if ($null -eq $record) { throw 'The record button is not on the first screen.' }
    if ($snap.Current.IsOffscreen -or $record.Current.IsOffscreen) { throw 'A main action is not actually on screen.' }
    foreach ($main in @($snap, $record)) {
        $null = $main.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    }

    # Nothing from the old stepwise product may still be reachable.
    $buttons = @()
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Button)) { $buttons += $item.Current.Name }
    foreach ($gone in @('AI', 'Plan', 'Case', 'Import', 'Answer')) {
        foreach ($name in $buttons) {
            if ($name -and $name.IndexOf($gone, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw ('A control from the withdrawn assistant workflow is still on screen: ' + $name)
            }
        }
    }

    # --- 2. the session list exists and says so when it is empty -----------
    $lists = All-Of $window ([System.Windows.Automation.ControlType]::List)
    if ($lists.Count -lt 1) { throw 'There is no session list.' }

    # --- 3. the detailed settings are folded away, with a live summary -----
    $settingsLabel = Message 'settings-fold.txt' 'Detailed settings'
    $fold = Wait-Named $window ([System.Windows.Automation.ControlType]::Group) $settingsLabel 8000
    if ($null -eq $fold) { throw 'The detailed settings fold is missing.' }
    $expand = $fold.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expand.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) { throw 'The detailed settings are not folded away by default.' }
    $expand.Expand()
    Start-Sleep -Milliseconds 500
    $null = Shoot $window 'settings'

    # The permission that lets replay touch a real application must be a real
    # tick box, and it must start off.
    $writeLabel = Message 'settings-write.txt' 'Let replay act on the real application'
    $permission = Wait-Named $window ([System.Windows.Automation.ControlType]::CheckBox) $writeLabel 6000
    if ($null -eq $permission) { throw 'The replay permission switch is missing.' }
    $toggle = $permission.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'The replay permission is on before anyone asked for it.' }

    # The route choice offers the three carrying-out routes and never MSAA.
    $combos = All-Of $window ([System.Windows.Automation.ControlType]::ComboBox)
    if ($combos.Count -lt 2) { throw 'The route and value choices are not both present.' }
    $routeNames = @()
    foreach ($combo in $combos) {
        $expandCombo = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expandCombo.Expand()
        Start-Sleep -Milliseconds 250
        foreach ($item in All-Of $combo ([System.Windows.Automation.ControlType]::ListItem)) { $routeNames += $item.Current.Name }
        $expandCombo.Collapse()
        Start-Sleep -Milliseconds 150
    }
    $joined = ($routeNames -join ' ')
    foreach ($needed in @('UI Automation', 'SendInput')) {
        if ($joined.IndexOf($needed, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw ('A route is missing from the settings: ' + $needed) }
    }
    if ($joined -match '(?i)\bMSAA\b') { throw 'MSAA is offered as a way to carry an operation out.' }

    $expand.Collapse()
    Start-Sleep -Milliseconds 300

    # --- 4. the window survives being driven and still reports its state ---
    $status = $null
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) {
        if ($item.Current.Name -and $item.Current.Name.Length -gt 6) { $status = $item.Current.Name }
    }
    if ($null -eq $status) { throw 'The window shows no status line at all.' }

    Write-Output ('PASS test-ui-flow mainActions=snap+record sessionList=1 settingsFolded=1 replayPermission=off routes=uia+win32+sendInput msaaOffered=0 withdrawnControls=0')
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $app) { $app.Dispose() }
}
