$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$buildDir = Join-Path $PSScriptRoot '.build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'The .NET Framework x64 compiler was not found.' }

$fixtureWin32 = Join-Path $buildDir 'FixtureWin32.exe'
$fixtureWpf = Join-Path $buildDir 'FixtureWpf.exe'
$fixtureWinForms = Join-Path $buildDir 'FixtureWinForms.exe'
$fixtureCanvas = Join-Path $buildDir 'FixtureCanvas.exe'
$fixtureInputTarget = Join-Path $buildDir 'FixtureInputTarget.exe'
$fixtureIme = Join-Path $buildDir 'FixtureIme.exe'
$mutex = New-Object Threading.Mutex($false, 'Local\AppStudioFixtureBuild')
$acquired = $false
try {
    $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(30))
    if (-not $acquired) { throw 'Timed out waiting for the fixture build lock.' }
    $source = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureWin32.cs')
    if (-not (Test-Path -LiteralPath $fixtureWin32) -or (Get-Item $fixtureWin32).LastWriteTimeUtc -lt $source.LastWriteTimeUtc) {
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureWin32 $source.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureWin32 compile failed: ' + $LASTEXITCODE) }
    }
    $wpfSource = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureWpf.cs')
    if (-not (Test-Path -LiteralPath $fixtureWpf) -or (Get-Item $fixtureWpf).LastWriteTimeUtc -lt $wpfSource.LastWriteTimeUtc) {
        $referenceNames = @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml','UIAutomationClient','UIAutomationTypes')
        $references = @()
        foreach ($referenceName in $referenceNames) {
            $assembly = [Reflection.Assembly]::LoadWithPartialName($referenceName)
            if ($null -eq $assembly -or [string]::IsNullOrWhiteSpace($assembly.Location)) { throw ('Fixture reference missing: ' + $referenceName) }
            $references += ('/reference:' + $assembly.Location)
        }
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureWpf $references $wpfSource.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureWpf compile failed: ' + $LASTEXITCODE) }
    }
    $winFormsSource = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureWinForms.cs')
    if (-not (Test-Path -LiteralPath $fixtureWinForms) -or (Get-Item $fixtureWinForms).LastWriteTimeUtc -lt $winFormsSource.LastWriteTimeUtc) {
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureWinForms $winFormsSource.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureWinForms compile failed: ' + $LASTEXITCODE) }
    }
    $inputTargetSource = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureInputTarget.cs')
    if (-not (Test-Path -LiteralPath $fixtureInputTarget) -or (Get-Item $fixtureInputTarget).LastWriteTimeUtc -lt $inputTargetSource.LastWriteTimeUtc) {
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureInputTarget $inputTargetSource.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureInputTarget compile failed: ' + $LASTEXITCODE) }
    }
    $imeSource = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureIme.cs')
    if (-not (Test-Path -LiteralPath $fixtureIme) -or (Get-Item $fixtureIme).LastWriteTimeUtc -lt $imeSource.LastWriteTimeUtc) {
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureIme $imeSource.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureIme compile failed: ' + $LASTEXITCODE) }
    }
    $canvasSource = Get-Item (Join-Path $PSScriptRoot 'fixtures\FixtureCanvas.cs')
    if (-not (Test-Path -LiteralPath $fixtureCanvas) -or (Get-Item $fixtureCanvas).LastWriteTimeUtc -lt $canvasSource.LastWriteTimeUtc) {
        & $csc /nologo /target:winexe /platform:anycpu /out:$fixtureCanvas $canvasSource.FullName
        if ($LASTEXITCODE -ne 0) { throw ('FixtureCanvas compile failed: ' + $LASTEXITCODE) }
    }
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

[pscustomobject]@{
    BuildDirectory = $buildDir
    FixtureWin32 = $fixtureWin32
    FixtureWpf = $fixtureWpf
    FixtureWinForms = $fixtureWinForms
    FixtureCanvas = $fixtureCanvas
    FixtureInputTarget = $fixtureInputTarget
    FixtureIme = $fixtureIme
}
