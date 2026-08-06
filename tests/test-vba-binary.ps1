$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The VBA artefact is a workbook, and it is written here without Excel and
# without "Trust access to the VBA project object model". That claim is the
# whole point of this path, so it is what this test is about: the workbook comes
# out of the seed by rebuilding one binary part, no VBA host is started while it
# happens, and what is inside the file afterwards is read back out of the file
# rather than taken from what the builder said it did.
#
# It runs on every machine, including one with no Office on it. Opening the
# result in a real Excel is a different proof and lives in test-vba-build.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-vba-binary-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$names = @('Workflow','RecordedFacts','RuntimeCore','RuntimeLocator','RuntimeNative')
# "VBProject" appears in these files as prose about the setting that is no
# longer needed, so what is checked is the code rather than the comments.
function Assert-NoProjectModel {
    param([string]$Text, [string]$File)
    foreach ($line in ($Text -split "`r`n")) {
        if ($line.Trim().StartsWith('//')) { continue }
        if ($line -match '\bVBProject\b') { throw ($File + ' still reaches for the VBA project object model: ' + $line.Trim()) }
    }
}
try {
    [AppStudio.VbaWorkbook]::Init($root)
    $seed = [AppStudio.VbaWorkbook]::SeedPath()
    if (-not (Test-Path -LiteralPath $seed)) { throw ('the seed workbook is not bundled: ' + $seed) }

    # A window title and an element label that are not ASCII, on purpose. A VBA
    # project stores its source in one code page, so this is the material most
    # likely to be quietly turned into question marks on the way in.
    $title = ([char]0x96FB + [char]0x5353 + ' No Such Window 8d41c2')
    $label = ('Button "' + [char]0x30C6 + [char]0x30B9 + [char]0x30C8 + '"')
    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'vba binary fixture')
    $step = New-Object AppStudio.StepRecord
    $step.Index = 1; $step.Kind = 'click'; $step.At = [DateTimeOffset]::Now; $step.GapMs = 150
    $step.WindowTitle = $title; $step.WindowClass = 'Notepad'
    $step.ElementLabel = $label; $step.ControlType = 'Button'
    $locator = New-Object AppStudio.ElementLocator
    $locator.Strategy = 'win32.ctrlId'; $locator.CtrlId = 1001; $locator.ClassName = 'Button'
    $step.Locators.Add($locator)
    $session.Steps.Add($step)

    $project = [AppStudio.CodeProject]::Open($session)
    $modules = $project.Files('vba')
    if ($modules.Count -ne 5) { throw ('the automation is ' + $modules.Count + ' VBA modules instead of 5') }

    # ---- the build starts no VBA host ----
    $hostsBefore = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $build = [AppStudio.CodeBuild]::BuildVba($modules, (Join-Path $temp 'build'))
    $watch.Stop()
    if (-not $build.Ok) { throw ('nothing was built: ' + $build.Problem) }
    $hostsAfter = @(Get-Process -Name EXCEL -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    foreach ($id in $hostsAfter) {
        if ($hostsBefore -notcontains $id) { throw ('the build started a VBA host, which this path may not need: pid ' + $id) }
    }
    if ($build.Method -notmatch 'no Excel') { throw ('the build does not say how it was made: ' + $build.Method) }

    # ---- one artefact, named the way a handover is named ----
    if ([IO.Path]::GetFileName($build.Path) -ne 'Workflow.xlsm') { throw ('the artefact is ' + [IO.Path]::GetFileName($build.Path)) }
    $produced = @(Get-ChildItem -LiteralPath (Join-Path $temp 'build') -Force | ForEach-Object { $_.Name })
    if (($produced -join ',') -ne 'Workflow.xlsm') { throw ('the build folder holds ' + ($produced -join ', ') + ' instead of the one file somebody is given') }
    if ($build.Modules.Count -ne 5) { throw ('the workbook was given ' + $build.Modules.Count + ' modules instead of 5') }
    foreach ($name in $names) {
        if ($build.Modules -notcontains $name) { throw ('the build does not report putting ' + $name + ' in') }
    }

    # ---- the macro part is actually inside the zip ----
    $zip = [IO.Compression.ZipFile]::OpenRead($build.Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'xl/vbaProject.bin' }
        if ($null -eq $entry) { throw 'the workbook carries no vbaProject.bin, so the modules are not in it' }
        if ($entry.Length -lt 1000) { throw ('vbaProject.bin is ' + $entry.Length + ' bytes, which cannot hold five modules') }
        $partBytes = $entry.Length
    } finally {
        $zip.Dispose()
    }

    # ---- read the artefact back out of the file, binary only ----
    $read = [AppStudio.VbaWorkbook]::Read($build.Path)
    if (-not $read.Ok) { throw ('the workbook that was just written cannot be read back: ' + $read.Problem) }
    foreach ($name in $names) {
        $found = $read.Project.Find($name)
        if ($null -eq $found) { throw ('the built workbook has no ' + $name + ' module') }
        if ($found.Kind -ne [AppStudio.VbaModuleKind]::Standard) { throw ($name + ' is in the workbook as ' + $found.Kind + ' rather than a standard module') }
        if ($found.SourceOffset -ne 0) { throw ($name + ' still carries a compiled form in front of its source') }
        $onScreen = $project.Find('vba', $name).Text
        if ($found.FullCode -ne $onScreen) { throw ('the ' + $name + ' in the workbook is not the ' + $name + ' on screen') }
    }
    # The seed's own document modules are still there and are still documents:
    # a workbook without them is not a workbook.
    if ($read.Project.Modules.Count -ne ($names.Count + 2)) {
        throw ('the workbook holds ' + $read.Project.Modules.Count + ' modules instead of the five plus the two a workbook always has')
    }
    $workflow = $read.Project.Find('Workflow')
    if ($workflow.FullCode.IndexOf('RunRecordedProcedure', [StringComparison]::Ordinal) -lt 0) { throw 'the workflow module in the workbook has no entry point' }
    if ($workflow.FullCode.IndexOf('InvokeElement', [StringComparison]::Ordinal) -lt 0) { throw 'the workflow module in the workbook lost the recorded step' }
    # What the step aims at has to still be in the workbook, in the alphabet it
    # was recorded in. Which of the five modules carries it is the generator's
    # business, so all five are read.
    $inside = ''
    foreach ($name in $names) { $inside += $read.Project.Find($name).FullCode }
    if ($inside.IndexOf($title, [StringComparison]::Ordinal) -lt 0) { throw 'the window title did not survive the build' }
    if ($inside.IndexOf($label, [StringComparison]::Ordinal) -lt 0) { throw 'the element label did not survive the build' }

    # ---- a seed that is not there is said by name ----
    $missing = [AppStudio.VbaWorkbook]::Build((Join-Path $temp 'no-such-seed.xlsm'), [AppStudio.VbaWorkbook]::ModulesOf($modules), (Join-Path $temp 'b1\Workflow.xlsm'))
    if ($missing.Ok) { throw 'a workbook was built from a seed that is not there' }
    if ($missing.Problem -notmatch 'seed workbook is missing') { throw ('a missing seed is reported as something else: ' + $missing.Problem) }

    # ---- a seed whose VBA project is damaged is said by name, not repaired ----
    $broken = Join-Path $temp 'broken-seed.xlsm'
    [IO.File]::Copy($seed, $broken)
    $stream = New-Object IO.FileStream($broken, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Update, $false)
        try {
            $part = $archive.Entries | Where-Object { $_.FullName -eq 'xl/vbaProject.bin' }
            $writing = $part.Open()
            try {
                $writing.SetLength(0)
                $rubbish = New-Object byte[] 2048
                $writing.Write($rubbish, 0, $rubbish.Length)
            } finally { $writing.Dispose() }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    $damaged = [AppStudio.VbaWorkbook]::Build($broken, [AppStudio.VbaWorkbook]::ModulesOf($modules), (Join-Path $temp 'b2\Workflow.xlsm'))
    if ($damaged.Ok) { throw 'a workbook was built from a seed whose VBA project is rubbish' }
    if ($damaged.Problem -notmatch 'OLE2|container') { throw ('a damaged seed is reported as something else: ' + $damaged.Problem) }
    if (Test-Path -LiteralPath (Join-Path $temp 'b2\Workflow.xlsm')) { throw 'a failed build left a workbook behind' }
    if (Test-Path -LiteralPath (Join-Path $temp 'b2\Workflow.xlsm.building')) { throw 'a failed build left its workpiece behind' }

    # ---- a module VBA cannot take is refused, and named ----
    $project.SetText('vba', '9NotAName', "Attribute VB_Name = ""9NotAName""`r`nOption Explicit`r`n")
    $badName = [AppStudio.CodeBuild]::BuildVba($project.Files('vba'), (Join-Path $temp 'b3'))
    if ($badName.Ok) { throw 'a module whose name VBA cannot take was built in anyway' }
    if ($badName.Problem -notmatch '9NotAName') { throw ('the refusal does not name the module: ' + $badName.Problem) }

    # ---- a module whose header claims another name is refused, and named ----
    # Going back to the generated version also drops the module that only ever
    # came from the line above, because the recording never made it.
    $project.RestoreBaseline('vba')
    $project.SetText('vba', 'RuntimeCore', "Attribute VB_Name = ""SomethingElse""`r`nOption Explicit`r`n")
    $mislabelled = [AppStudio.CodeBuild]::BuildVba($project.Files('vba'), (Join-Path $temp 'b4'))
    if ($mislabelled.Ok) { throw 'a module that declares itself as another module was built in anyway' }
    if ($mislabelled.Problem -notmatch 'SomethingElse') { throw ('the refusal does not name what the module claims to be: ' + $mislabelled.Problem) }

    # ---- a character the project's code page cannot carry stops the build ----
    $project.RestoreBaseline('vba')
    $project.SetText('vba', 'RuntimeCore', ("Attribute VB_Name = ""RuntimeCore""`r`nOption Explicit`r`n' " + [char]0xAC00 + "`r`n"))
    $unencodable = [AppStudio.CodeBuild]::BuildVba($project.Files('vba'), (Join-Path $temp 'b5'))
    if ($unencodable.Ok) { throw 'a character the project cannot carry was written into a workbook anyway' }
    if ($unencodable.Problem -notmatch 'U\+AC00') { throw ('the refusal does not name the character: ' + $unencodable.Problem) }
    if ($unencodable.Problem -notmatch 'code page') { throw ('the refusal does not say why the character cannot go in: ' + $unencodable.Problem) }

    # ---- nothing in the build path reaches for a VBA host any more ----
    $buildPath = @('47_CodeBuild.cs','48_Ole2.cs','49_VbaCompress.cs','50_VbaProject.cs','51_VbaWorkbook.cs')
    foreach ($file in $buildPath) {
        $text = [IO.File]::ReadAllText((Join-Path $root ('src\' + $file)))
        foreach ($banned in @('VBComponents', 'GetTypeFromProgID', 'Excel.Application')) {
            if ($text.IndexOf($banned, [StringComparison]::Ordinal) -ge 0) {
                throw ($file + ' still reaches for a VBA host: ' + $banned)
            }
        }
        Assert-NoProjectModel $text $file
    }
    # And the run path reaches for a host - running VBA needs one - but never
    # for its VBA project. That is what makes the trust setting unnecessary on
    # both sides, so it is checked rather than asserted in a document.
    $runPath = [IO.File]::ReadAllText((Join-Path $root 'src\44_ScriptRun.cs'))
    Assert-NoProjectModel $runPath '44_ScriptRun.cs'
    if ($runPath.IndexOf('VBComponents', [StringComparison]::Ordinal) -ge 0) { throw 'the run path still imports modules into a VBA project' }
    if ($runPath -match '"Import"') { throw 'the run path still imports modules into a VBA project' }

    Write-Output ('PASS test-vba-binary artefact=Workflow.xlsm bytes=' + $build.Bytes + ' vbaProjectBytes=' + $partBytes +
        ' modules=' + $build.Modules.Count + ' hostsStarted=0 nonAsciiSurvived=1 readBack=binary' +
        ' refusals=missingSeed,damagedSeed,badName,mislabelled,unencodable elapsedMs=' + $watch.ElapsedMilliseconds)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
