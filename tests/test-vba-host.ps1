$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Running VBA needs a VBA host, and a machine may not have one. That is the only
# environment left that can stop a run: the workbook is built first and then
# opened, so nothing here asks Excel for its VBA project and the trust setting
# that used to be required is not. A machine with no host has to be said by
# name rather than passed over.
#
# The module deliberately looks for a window that does not exist, so a run that
# reaches the end is proving the host path and not driving anybody's application.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-vba-host-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    [AppStudio.VbaWorkbook]::Init($root)
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'vba host')
    $step = New-Object AppStudio.StepRecord
    $step.Index = 1; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 150
    $step.WindowTitle = 'No Such Window Exists 8d41c2'; $step.WindowClass = ''
    $step.ElementLabel = 'Button "X"'; $step.ControlType = 'Button'
    $locator = New-Object AppStudio.ElementLocator
    $locator.Strategy = 'win32.ctrlId'; $locator.CtrlId = 1001; $locator.ClassName = 'Button'
    $step.Locators.Add($locator)
    $session.Steps.Add($step)
    $project = [AppStudio.CodeProject]::Open($session)
    $modules = $project.Files('vba')
    $vba = $project.Find('vba', 'Workflow').Text

    if ($modules.Count -ne 5) { throw ('the automation is ' + $modules.Count + ' VBA modules instead of 5') }
    foreach ($module in $modules) {
        $structure = [AppStudio.ScriptRun]::CheckVba($module.Text, $module.Name)
        if (-not $structure.Ok) { throw ('the generated ' + $module.FileName + ' is not structurally sound: ' + (($structure.Problems) -join ' / ')) }
        if ($structure.Method -notmatch 'structural') { throw 'the structural check does not admit that it is only a structural check' }
    }
    foreach ($entry in @('Public Sub RunRecordedProcedure()', 'Public Sub RunRecordedProcedureTo(', 'On Error GoTo Failed')) {
        if ($vba.IndexOf($entry, [StringComparison]::Ordinal) -lt 0) { throw ('the workflow module is missing ' + $entry) }
    }
    # Every Declare has to name the entry point it actually calls. Without the
    # alias the renamed one is looked for in the DLL and is never found. They
    # live in the runtime module now, so every module is read rather than only
    # the one that is started.
    $declares = 0
    foreach ($module in $modules) {
        foreach ($line in ($module.Text -split "`r`n")) {
            if ($line -match '^\s*(Private|Public)\s+Declare') {
                $declares++
                if ($line -notmatch '\sAlias\s') {
                    throw ('a Declare has no Alias, so its entry point cannot be found: ' + $line.Trim())
                }
            }
        }
    }
    if ($declares -lt 10) { throw ('only ' + $declares + ' Declare lines were found across the modules') }

    $before = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = [AppStudio.ScriptRun]::RunVba($modules, (Join-Path $temp 'run'), 'RunRecordedProcedure', 120000)
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -gt 130000) { throw ('the host call was not bounded: ' + $watch.ElapsedMilliseconds + ' ms') }

    $outcome = ''
    if (-not $result.Started) {
        if ($result.Problem -notmatch 'No VBA host is installed') { throw ('no host was found but the reason given was: ' + $result.Problem) }
        $outcome = 'noHost'
    } elseif ($result.Problem -match 'Trust access to the VBA project object model') {
        throw 'the run still asks for trusted access to the VBA project, which this path does not need'
    } elseif ($result.Problem -match 'did not answer within') {
        throw ('the host stopped answering, which this test cannot accept as a result: ' + $result.Problem)
    } elseif ($result.Ok) {
        throw 'the module reported success although the window it looks for does not exist'
    } else {
        # The host ran it and the module refused, in its own words. That is the
        # whole path working.
        if ($result.Problem -notmatch 'no window matches') {
            throw ('the module ran but the reason that came back is not its own: ' + $result.Problem)
        }
        if ($result.Problem -notmatch 'A1') { throw 'the reason does not say which step stopped' }
        $outcome = 'ranAndRefused'
        # What was run is the workbook a handover is, built by the same builder
        # into the run folder. A run that assembled the automation some other
        # way would be proving something adjacent to the artefact.
        $ran = Join-Path (Join-Path $temp 'run') 'Workflow.xlsm'
        if (-not (Test-Path -LiteralPath $ran)) { throw 'the run did not go through a built workbook' }
        foreach ($stray in @('Workflow.bas','RecordedFacts.bas','RuntimeCore.bas','RuntimeLocator.bas','RuntimeNative.bas')) {
            if (Test-Path -LiteralPath (Join-Path (Join-Path $temp 'run') $stray)) {
                throw ('the run still writes modules out to be imported: ' + $stray)
            }
        }
    }
    Start-Sleep -Milliseconds 1500
    $after = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw ('a VBA host was left running: ' + $before + ' -> ' + $after) }

    Write-Output ('PASS test-vba-host outcome=' + $outcome + ' bounded=1 hostsLeft=0 elapsedMs=' + $watch.ElapsedMilliseconds +
        ' declaresAliased=1 trustNotRequired=1 reason="' + ($result.Problem -replace '"', "'") + '"')
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
