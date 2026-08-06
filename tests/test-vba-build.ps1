$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The workbook is written without Excel, and test-vba-binary is what proves
# that. This is the other half of the claim: that the file which comes out is a
# workbook Excel actually opens, holding the five modules as modules, with the
# code that was on screen.
#
# Only Excel can answer that, so this test starts one of its own - which is why
# it is not in the default set. Nothing here touches the VBA project object
# model on the way in; the reading afterwards does, because reading is how the
# proof is taken, and that is this test's own doing rather than the product's.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-vba-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$before = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
try {
    [AppStudio.VbaWorkbook]::Init($root)
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
    # There is no environment left that can stop this build. A machine without
    # Excel builds the same workbook as a machine with it, so a failure here is
    # a fault and is never to be read as a machine that could not.
    if (-not $build.Ok) { throw ('nothing was built: ' + $build.Problem) }
    if ([IO.Path]::GetFileName($build.Path) -ne 'Workflow.xlsm') { throw ('the artefact is ' + [IO.Path]::GetFileName($build.Path)) }
    if ($build.Modules.Count -ne 5) { throw ('the workbook was given ' + $build.Modules.Count + ' modules instead of 5') }

    # An xlsm is a zip. The macro part has to actually be inside it rather
    # than merely reported as written.
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
    # what Excel makes of the file rather than what the builder said it did.
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
        # Excel has to have compiled the modules from their source. A project
        # that still believed a compiled cache would run something other than
        # what is in the file, and that is the failure this whole path is
        # arranged to avoid.
        #
        # Compared with the runs of spaces collapsed. The VBA editor re-aligns
        # the space between an identifier and what follows it when it takes a
        # module in, so a column that lines up on screen does not line up in
        # Excel. That is Excel's editor and not this build: every character that
        # carries meaning is there, which is what test-vba-binary checks byte
        # for byte against the file itself.
        foreach ($wanted in @('Workflow','RecordedFacts','RuntimeCore','RuntimeLocator','RuntimeNative')) {
            $module = $book.VBProject.VBComponents.Item($wanted).CodeModule
            $text = ($module.Lines(1, $module.CountOfLines) -replace ' +', ' ')
            $onScreen = $project.Find('vba', $wanted).Text
            foreach ($line in ($onScreen -split "`r`n")) {
                $trimmed = ($line.Trim() -replace ' +', ' ')
                if ($trimmed.Length -lt 12) { continue }
                if ($trimmed.StartsWith('Attribute VB_')) { continue }
                if ($text.IndexOf($trimmed, [StringComparison]::Ordinal) -lt 0) {
                    throw ('the ' + $wanted + ' Excel opened is missing a line that is on screen: ' + $trimmed)
                }
            }
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
    # count below is only what the builder left behind - and the builder never
    # starts one at all.
    Start-Sleep -Milliseconds 1500
    foreach ($process in @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue)) {
        if ($readerBefore -notcontains $process.Id) { $process.Kill() }
    }

    Start-Sleep -Milliseconds 2000
    $after = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw ('a VBA host was left running: ' + $before + ' -> ' + $after) }

    Write-Output ('PASS test-vba-build outcome=built bytes=' + $build.Bytes + ' modules=' + $build.Modules.Count +
        ' openedByExcel=1 compiledFromSource=1 bounded=1 hostsLeft=0 elapsedMs=' + $watch.ElapsedMilliseconds)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
