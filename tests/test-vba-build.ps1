$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The VBA artefact is a workbook, so the only proof worth having is the workbook
# itself: opened again from disk, holding the five modules with the entry point
# in the one a person edits.
#
# A machine with no VBA host cannot produce one. That is a result, not a failure,
# and it has to be said by name rather than passed over - so this test reports
# which of the two happened instead of quietly succeeding either way.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-vba-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$before = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
try {
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'vba build fixture')
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
    if ($modules.Count -ne 5) { throw ('the automation is ' + $modules.Count + ' VBA modules instead of 5') }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $build = [AppStudio.CodeBuild]::BuildVba($modules, (Join-Path $temp 'build'))
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -gt 120000) { throw ('the build was not bounded: ' + $watch.ElapsedMilliseconds + ' ms') }

    $outcome = ''
    if (-not $build.Ok) {
        # Only the two stated reasons are allowed to come back. Anything else is
        # a fault being reported as an environment.
        if ($build.Problem -match 'No VBA host is installed') { $outcome = 'noHost' }
        elseif ($build.Problem -match 'Trust access to the VBA project object model') { $outcome = 'notTrusted' }
        else { throw ('the build failed for a reason it does not own: ' + $build.Problem) }
        # The modules still have to be on disk, because that is the whole of what
        # was promised when no workbook can be written here.
        $staged = Join-Path (Join-Path $temp 'build') 'modules'
        foreach ($name in @('Workflow.bas','RecordedFacts.bas','RuntimeCore.bas','RuntimeLocator.bas','RuntimeNative.bas')) {
            if (-not (Test-Path -LiteralPath (Join-Path $staged $name))) {
                throw ('no workbook was written and ' + $name + ' was not left behind either')
            }
        }
    } else {
        $outcome = 'built'
        if ([IO.Path]::GetFileName($build.Path) -ne 'Workflow.xlsm') { throw ('the artefact is ' + [IO.Path]::GetFileName($build.Path)) }
        if ($build.Modules.Count -ne 5) { throw ('the workbook was given ' + $build.Modules.Count + ' modules instead of 5') }

        # An xlsm is a zip. The macro part has to actually be inside it rather
        # than merely reported as imported.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($build.Path)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq 'xl/vbaProject.bin' }
            if ($null -eq $entry) { throw 'the workbook carries no vbaProject.bin, so the modules are not in it' }
            if ($entry.Length -lt 1000) { throw ('vbaProject.bin is ' + $entry.Length + ' bytes, which cannot hold five modules') }
        } finally {
            $zip.Dispose()
        }

        # Read it back through a host of this test's own, so what is checked is
        # what is in the file rather than what the builder said it did.
        $readerBefore = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
        $excel = New-Object -ComObject Excel.Application
        try {
            $excel.Visible = $false
            $excel.DisplayAlerts = $false
            $book = $excel.Workbooks.Open($build.Path)
            $found = @()
            foreach ($component in $book.VBProject.VBComponents) { $found += $component.Name }
            foreach ($wanted in @('Workflow','RecordedFacts','RuntimeCore','RuntimeLocator','RuntimeNative')) {
                if ($found -notcontains $wanted) { throw ('the built workbook has no ' + $wanted + ' module') }
            }
            $code = $book.VBProject.VBComponents.Item('Workflow').CodeModule
            $inside = $code.Lines(1, $code.CountOfLines)
            if ($inside -notmatch 'RunRecordedProcedure') { throw 'the workflow module in the workbook has no entry point' }
            if ($inside -notmatch 'InvokeElement') { throw 'the workflow module in the workbook lost the recorded step' }
            $book.Close($false)
        } finally {
            $excel.Quit()
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel)
        }
        # This test opened a host of its own and closes that one itself, so the
        # count below is only what the builder left behind.
        Start-Sleep -Milliseconds 1500
        foreach ($process in @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue)) {
            if ($readerBefore -notcontains $process.Id) { $process.Kill() }
        }
    }

    Start-Sleep -Milliseconds 2000
    $after = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw ('a VBA host was left running: ' + $before + ' -> ' + $after) }

    Write-Output ('PASS test-vba-build outcome=' + $outcome + ' bytes=' + $build.Bytes + ' modules=' + $build.Modules.Count +
        ' bounded=1 hostsLeft=0 elapsedMs=' + $watch.ElapsedMilliseconds)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
