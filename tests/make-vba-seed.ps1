$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell script failed: ' + $LASTEXITCODE) }
    return
}
# Rebuilds assets/vba/seed.xlsm, the workbook every built automation starts from.
#
# This is the one place in the repository that needs Excel and needs trusted
# access to the VBA project object model, and it is not part of building or
# running anything: it makes the bundled asset. A workbook is a binary container
# only Excel can create from nothing, so the seed is created once, committed, and
# from then on the product adds modules to a copy of it byte by byte.
#
# The seed deliberately holds no standard module. Excel writes xl/vbaProject.bin
# only for a workbook whose VBA project holds code, so the document module every
# workbook already has carries one private procedure and nothing else. The five
# modules of an automation are then all additions, which keeps a built workbook
# to exactly the five it was made from.
#
# The output is not byte reproducible: Excel stamps a fresh project id and
# timestamps into every workbook it writes. What is reproducible is the shape,
# and that is what is checked below and by tests/test-vba-binary.ps1.
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File tests\make-vba-seed.ps1

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$folder = Join-Path (Join-Path $root 'assets') 'vba'
$seed = Join-Path $folder 'seed.xlsm'

$type = [Type]::GetTypeFromProgID('Excel.Application')
if ($null -eq $type) {
    throw 'Excel is not installed on this machine, so the seed workbook cannot be created here. It is a committed asset: a clone already has one, and only a change to the seed itself needs this script.'
}
if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }

$pending = Join-Path ([IO.Path]::GetTempPath()) ('app-studio-seed-' + [Guid]::NewGuid().ToString('N') + '.xlsm')
$excel = New-Object -ComObject Excel.Application
try {
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $book = $excel.Workbooks.Add()
    try {
        $project = $book.VBProject
    } catch {
        throw 'Excel refused access to the VBA project. Turn on "Trust access to the VBA project object model" in Trust Center > Macro Settings. Only creating this asset needs that setting; building and running an automation do not.'
    }
    # One private procedure in the document module, so the project is not empty
    # and Excel writes it into the workbook. Nothing ever calls it.
    $project.VBComponents.Item('ThisWorkbook').CodeModule.AddFromString(
        "Private Sub AppStudioSeed()`r`nEnd Sub`r`n")
    $book.SaveAs($pending, 52)
    $book.Close($false)
} finally {
    try { $excel.Quit() } catch { }
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($excel) } catch { }
}

# What was written has to be a workbook the build can actually read, or the
# seed would fail on somebody else's machine instead of here. It is read by the
# product's own reader, which is the one that will have to read it later.
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$read = [AppStudio.VbaWorkbook]::Read($pending)
if (-not $read.Ok) { throw ('the workbook Excel wrote is not usable as a seed: ' + $read.Problem) }
$bytes = $read.VbaProjectBytes
$standard = 0
foreach ($module in $read.Project.Modules) {
    if ($module.Kind -eq [AppStudio.VbaModuleKind]::Standard) { $standard++ }
}
if ($standard -ne 0) { throw ('the seed carries ' + $standard + ' standard module(s); it is meant to carry none') }

[IO.File]::Copy($pending, $seed, $true)
Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
Write-Output ('WROTE ' + $seed + ' bookBytes=' + (Get-Item -LiteralPath $seed).Length +
    ' vbaProjectBytes=' + $bytes + ' documentModules=' + $read.Project.Modules.Count + ' standardModules=0')
