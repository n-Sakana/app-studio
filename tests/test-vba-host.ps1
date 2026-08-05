$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Running VBA needs a VBA host, and a machine may not have one, or may not trust
# a caller to touch its VBA project. None of those is allowed to look like a
# pass: whichever of them is true, the product has to say which one by name.
#
# The module deliberately looks for a window that does not exist, so a run that
# reaches the end is proving the host path and not driving anybody's application.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-vba-host-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
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
    $vba = $project.Find('vba', 'RecordedProcedure').Text

    $structure = [AppStudio.ScriptRun]::CheckVba($vba)
    if (-not $structure.Ok) { throw ('the generated module is not structurally sound: ' + (($structure.Problems) -join ' / ')) }
    if ($structure.Method -notmatch 'structural') { throw 'the structural check does not admit that it is only a structural check' }
    foreach ($entry in @('Public Sub RunRecordedProcedure()', 'Public Sub RunRecordedProcedureTo(', 'On Error GoTo Failed')) {
        if ($vba.IndexOf($entry, [StringComparison]::Ordinal) -lt 0) { throw ('the module is missing ' + $entry) }
    }
    # Every Declare has to name the entry point it actually calls. Without the
    # alias the renamed one is looked for in the DLL and is never found.
    foreach ($line in ($vba -split "`r`n")) {
        if ($line -match '^\s*(Private|Public)\s+Declare' -and $line -notmatch '\sAlias\s') {
            throw ('a Declare has no Alias, so its entry point cannot be found: ' + $line.Trim())
        }
    }

    $before = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = [AppStudio.ScriptRun]::RunVba($vba, (Join-Path $temp 'run'), 'RunRecordedProcedure', 120000)
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -gt 130000) { throw ('the host call was not bounded: ' + $watch.ElapsedMilliseconds + ' ms') }

    $outcome = ''
    if (-not $result.Started) {
        if ($result.Problem -notmatch 'No VBA host is installed') { throw ('no host was found but the reason given was: ' + $result.Problem) }
        $outcome = 'noHost'
    } elseif ($result.Problem -match 'Trust access to the VBA project object model') {
        $outcome = 'notTrusted'
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
    }
    Start-Sleep -Milliseconds 1500
    $after = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw ('a VBA host was left running: ' + $before + ' -> ' + $after) }

    Write-Output ('PASS test-vba-host outcome=' + $outcome + ' bounded=1 hostsLeft=0 elapsedMs=' + $watch.ElapsedMilliseconds +
        ' declaresAliased=1 reason="' + ($result.Problem -replace '"', "'") + '"')
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
