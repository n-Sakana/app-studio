param(
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '.build')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) {
    throw 'The .NET Framework C# compiler was not found.'
}

function Find-GacAssembly([string]$Name) {
    $gacKinds = @('GAC_MSIL', 'GAC_64', 'GAC_32')
    $found = @()
    foreach ($gacKind in $gacKinds) {
        $folder = Join-Path (Join-Path $env:WINDIR 'Microsoft.NET\assembly') (Join-Path $gacKind $Name)
        if (Test-Path -LiteralPath $folder) {
            $found += Get-ChildItem -LiteralPath $folder -Recurse -Filter ($Name + '.dll') -ErrorAction Stop
        }
    }
    $candidate = $found | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $candidate) {
        throw ('GAC assembly not found: ' + $Name)
    }
    return $candidate.FullName
}

$uiaClient = Find-GacAssembly 'UIAutomationClient'
$uiaTypes = Find-GacAssembly 'UIAutomationTypes'
$windowsBase = Find-GacAssembly 'WindowsBase'
$presentationCore = Find-GacAssembly 'PresentationCore'
$presentationFramework = Find-GacAssembly 'PresentationFramework'
$systemXaml = Find-GacAssembly 'System.Xaml'
$interop = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'Microsoft.UIAutomationClient.Interop.dll' -ErrorAction Stop |
    Where-Object { $_.FullName -match '\\x64\\UIAVerify\\Microsoft\.UIAutomationClient\.Interop\.dll$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $interop) {
    throw 'Windows SDK UI Automation interop assembly was not found.'
}

New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
Copy-Item -LiteralPath $interop.FullName -Destination (Join-Path $BuildDirectory $interop.Name) -Force

$fixtureSource = Join-Path $repoRoot 'tests\fixtures\FixtureWpf.cs'
$fixtureExe = Join-Path $BuildDirectory 'FixtureApps.exe'
$harnessSource = Join-Path $PSScriptRoot 'WpsHarness.cs'
$harnessExe = Join-Path $BuildDirectory 'WpsHarness.exe'
$interopCopy = Join-Path $BuildDirectory $interop.Name

& $csc /nologo /langversion:5 /target:winexe /platform:anycpu /optimize+ /main:AppStudio.WpsFixtures.WpfProgram `
    /out:$fixtureExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:$windowsBase `
    /reference:$presentationCore `
    /reference:$presentationFramework `
    /reference:$systemXaml `
    $fixtureSource
if ($LASTEXITCODE -ne 0) {
    throw ('Fixture compilation failed with exit code ' + $LASTEXITCODE)
}

& $csc /nologo /langversion:5 /target:exe /platform:anycpu /optimize+ `
    /out:$harnessExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:$windowsBase `
    /reference:$uiaClient `
    /reference:$uiaTypes `
    /reference:$interopCopy `
    $harnessSource
if ($LASTEXITCODE -ne 0) {
    throw ('Harness compilation failed with exit code ' + $LASTEXITCODE)
}

[pscustomobject]@{
    BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
    FixtureExe = $fixtureExe
    HarnessExe = $harnessExe
    InteropAssembly = $interop.FullName
    Csc = $csc
}
