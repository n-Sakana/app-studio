param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = 'Stop'
$startup = [Diagnostics.Stopwatch]::StartNew()
[Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)

$uiaClient = [Reflection.Assembly]::LoadWithPartialName('UIAutomationClient')
$uiaTypes = [Reflection.Assembly]::LoadWithPartialName('UIAutomationTypes')
$windowsBase = [Reflection.Assembly]::LoadWithPartialName('WindowsBase')

if ($null -eq $uiaClient -or $null -eq $uiaTypes -or $null -eq $windowsBase) {
    throw 'Required UI Automation assemblies are unavailable.'
}

$sourceText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Source))
Add-Type -TypeDefinition $sourceText -ReferencedAssemblies @(
    $uiaClient.Location,
    $uiaTypes.Location,
    $windowsBase.Location
)

$startup.Stop()
[Console]::Out.WriteLine('{"type":"ready","startupMs":' + $startup.ElapsedMilliseconds + '}')
[Console]::Out.Flush()

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) {
        break
    }
    if ($line -eq '{"type":"exit"}') {
        break
    }

    $request = $line | ConvertFrom-Json
    if ($request.type -ne 'probe') {
        [Console]::Out.WriteLine('{"type":"error","id":"","message":"Malformed request"}')
        [Console]::Out.Flush()
        continue
    }

    $requestId = [string]$request.id
    $x = [Int32]$request.x
    $y = [Int32]$request.y
    $result = [AppStudio.WpsWorker.ProbeWorker]::Probe($requestId, $x, $y)
    [Console]::Out.WriteLine($result)
    [Console]::Out.Flush()
}
