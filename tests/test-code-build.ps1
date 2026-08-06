$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Editing happens in modules; handing the automation over happens in one file.
# This is the seam between them, so what is checked here is that the single file
# is the same automation - it compiles, it runs, it stops for its own reasons,
# and what a step aims at is still legible after the fold.
#
# The recording deliberately names a window that does not exist. A run that
# reaches the end would mean the artefact drove somebody's application, which a
# test may not do; a run that stops with the runtime's own words proves the whole
# path without touching anything.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-code-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    # A title and a label that are not ASCII, on purpose: they are the thing most
    # likely to be quietly destroyed by a file that three different readers parse.
    $title = ([char]0x96FB + [char]0x5353 + ' No Such Window 8d41c2')
    $label = ('Button "' + [char]0x30C6 + [char]0x30B9 + [char]0x30C8 + '"')
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'code build fixture')
    $step = New-Object AppStudio.StepRecord
    $step.Index = 1; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 150
    $step.WindowTitle = $title; $step.WindowClass = 'ApplicationFrameWindow'
    $step.ElementLabel = $label; $step.ControlType = 'Button'
    $locator = New-Object AppStudio.ElementLocator
    $locator.Strategy = 'uia.automationId'; $locator.AutomationId = 'num1Button'; $locator.ControlType = 'Button'
    $step.Locators.Add($locator)
    $session.Steps.Add($step)

    $project = [AppStudio.CodeProject]::Open($session)
    $modules = $project.Files('powershell')
    if ($modules.Count -ne 5) { throw ('the automation is ' + $modules.Count + ' modules instead of 5') }

    $build = [AppStudio.CodeBuild]::BuildPowerShell($modules, (Join-Path $temp 'build'))
    if (-not $build.Ok) { throw ('nothing was built: ' + $build.Problem) }
    if ([IO.Path]::GetFileName($build.Path) -ne 'Workflow.cmd') { throw ('the artefact is ' + [IO.Path]::GetFileName($build.Path)) }
    if ($build.Modules.Count -ne 5) { throw ('the artefact folded ' + $build.Modules.Count + ' modules instead of 5') }

    # cmd reads the first line of this file itself, so a byte order mark in front
    # of it is not a comment - it is part of the command.
    $bytes = [IO.File]::ReadAllBytes($build.Path)
    if ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'the built cmd carries a byte order mark' }
    $text = [IO.File]::ReadAllText($build.Path, (New-Object Text.UTF8Encoding($false)))
    foreach ($part in @('<# :', '@echo off', 'Add-Type -TypeDefinition $engine', 'namespace AppStudioRun')) {
        if ($text.IndexOf($part, [StringComparison]::Ordinal) -lt 0) { throw ('the artefact is missing ' + $part) }
    }
    # What the step aims at has to still be in the file, in the alphabet it was
    # recorded in.
    if ($text.IndexOf($label, [StringComparison]::Ordinal) -lt 0) { throw 'the element label did not survive the build' }
    if ($text.IndexOf($title, [StringComparison]::Ordinal) -lt 0) { throw 'the window title did not survive the build' }
    # Every module has to be inside it, and still be findable as itself.
    foreach ($module in $modules) {
        if ($text.IndexOf('#region ' + $module.FileName, [StringComparison]::Ordinal) -lt 0) {
            throw ('the artefact does not carry ' + $module.FileName + ' as its own region')
        }
    }
    # The engine is one program: a using after a namespace does not compile, so
    # the imports have to have been lifted rather than left where they were.
    $firstNamespace = $text.IndexOf('namespace AppStudioRun', [StringComparison]::Ordinal)
    $lastUsing = $text.LastIndexOf("`nusing ", [StringComparison]::Ordinal)
    if ($lastUsing -gt $firstNamespace) { throw 'a using directive was left after a namespace, so the artefact cannot compile' }

    # A failed run holds its console open so the operator can read why. Nothing
    # is watching this one, so it is told to report the exit code instead of
    # waiting for a key. The log is written either way, and is checked below.
    $env:APPSTUDIO_NO_PAUSE = '1'
    $stdout = Join-Path $temp 'out.txt'
    $stderr = Join-Path $temp 'err.txt'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $run = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $build.Path + '"'), '-SettleMs', '400') `
        -NoNewWindow -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -gt 90000) { throw ('the artefact was not bounded: ' + $watch.ElapsedMilliseconds + ' ms') }
    # A console application writes its error through the console code page, and
    # that is what the redirected file holds. Reading it as anything else would
    # fail this test for the reader's mistake rather than the artefact's.
    $err = [IO.File]::ReadAllText($stderr, [Text.Encoding]::Default)
    if ($run.ExitCode -eq 0) { throw 'the artefact reported success although the window it looks for does not exist' }
    if ($err -notmatch 'no window matches') { throw ('the reason that came back is not the runtime own one: ' + $err) }
    if ($err -notmatch [regex]::Escape($title)) { throw ('the window title did not survive the build: ' + $err) }

    # A run that stopped has to leave something behind. Double-clicked, the
    # console it wrote its reason to closes with it, so the reason has to also be
    # somewhere that outlives the window.
    $log = $build.Path + '.log'
    if (-not (Test-Path -LiteralPath $log)) { throw ('the run left no log beside the artefact: ' + $log) }
    $logText = [IO.File]::ReadAllText($log, (New-Object Text.UTF8Encoding($false)))
    if ($logText -notmatch 'START') { throw ('the log does not record the run starting: ' + $logText) }
    if ($logText -notmatch 'STOPPED') { throw ('the log does not record why the run stopped: ' + $logText) }
    if ($logText -notmatch 'no window matches') { throw ('the log does not carry the runtime own reason: ' + $logText) }
    # The artefact holds the wait, so a person who double-clicks it gets to read
    # the reason instead of watching the window vanish.
    if ($text.IndexOf('pause', [StringComparison]::Ordinal) -lt 0) {
        throw 'the artefact does not hold the console open when a run fails'
    }
    if ($text.IndexOf('APPSTUDIO_NO_PAUSE', [StringComparison]::Ordinal) -lt 0) {
        throw 'the artefact offers no way for an unattended caller to skip the wait'
    }

    # A module holding a line that would end the literal the engine is carried in
    # is refused by name, rather than built into a file that compiles as something
    # nobody wrote.
    $project.SetText('powershell', 'RuntimeNative', "'@`r`nWrite-Output 'not mine'")
    $broken = [AppStudio.CodeBuild]::BuildPowerShell($project.Files('powershell'), (Join-Path $temp 'build2'))
    if ($broken.Ok) { throw 'a module that ends the engine literal early was built anyway' }
    if ($broken.Problem -notmatch 'RuntimeNative') { throw ('the refusal does not name the module: ' + $broken.Problem) }

    Write-Output ('PASS test-code-build artefact=Workflow.cmd bytes=' + $build.Bytes + ' modules=' + $build.Modules.Count +
        ' bom=0 exit=' + $run.ExitCode + ' elapsedMs=' + $watch.ElapsedMilliseconds + ' nonAsciiSurvived=1 literalBreak=refused')
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
