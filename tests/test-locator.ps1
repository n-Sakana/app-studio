$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly

function New-Rect([int]$x,[int]$y,[int]$w,[int]$h) {
    $r=New-Object AppStudio.RectValue;$r.X=$x;$r.Y=$y;$r.Width=$w;$r.Height=$h;return $r
}
function New-Base([string]$automationId,[string]$name,[int]$ctrlId) {
    $snapshot=New-Object AppStudio.Snapshot
    $snapshot.Uia=New-Object AppStudio.UiaInfo
    $snapshot.Uia.AutomationId=$automationId;$snapshot.Uia.Name=$name;$snapshot.Uia.ControlType='Edit';$snapshot.Uia.BoundingRect=New-Rect 120 130 100 20
    $snapshot.Win32=New-Object AppStudio.Win32Info
    $snapshot.Win32.ClassName='Edit';$snapshot.Win32.CtrlId=$ctrlId;$snapshot.Win32.ZIndex=2;$snapshot.Win32.WindowRect=New-Rect 120 130 100 20;$snapshot.Win32.ClientRect=New-Rect 120 130 100 20
    $ancestor=New-Object AppStudio.Win32Ancestor;$ancestor.ClassName='#32770';$ancestor.Caption='Order entry';$snapshot.Win32.Ancestors.Add($ancestor)
    return $snapshot
}
function New-Target {
    $target=New-Object AppStudio.TargetInfo;$target.TargetRunId='run-1';$target.ProcessName='Fixture';$target.TopLevelClass='#32770';$target.TopLevelCaption='Order entry';$target.ClientRect=New-Rect 100 100 500 400;return $target
}
function Find-Strategy($items,[string]$strategy) { return @($items|Where-Object{$_.Strategy-eq$strategy}) }

$stable=New-Base 'CustomerCode' 'Customer code' 1002
$stable.Uia.TreePath=@()
$stableLocators=[AppStudio.LocatorBuilder]::Build($stable,(New-Target))
if ((Find-Strategy $stableLocators 'uia.automationId').Count-ne1) { throw 'stable AutomationId candidate missing' }
if ((Find-Strategy $stableLocators 'uia.nameControlType').Count-ne1) { throw 'Name+ControlType candidate missing' }
if ((Find-Strategy $stableLocators 'win32.ctrlId').Count-ne1) { throw 'valid ctrlId candidate missing' }
if ((Find-Strategy $stableLocators 'win32.classPath').Count-ne1) { throw 'Win32 class path candidate missing' }
if ((Find-Strategy $stableLocators 'screen.relative').Count-ne1) { throw 'relative coordinate candidate missing' }

$numeric=New-Base '12345' 'Updated 2026-08-01 12:34 99%' 0
$numeric.Uia.TreePath=@()
$numericLocators=[AppStudio.LocatorBuilder]::Build($numeric,(New-Target))
if ((Find-Strategy $numericLocators 'uia.automationId').Count-ne0) { throw 'numeric-only AutomationId was used' }
if ((Find-Strategy $numericLocators 'win32.ctrlId').Count-ne0) { throw 'zero ctrlId was used' }
$numeric.Win32.CtrlId=-1
if ((Find-Strategy ([AppStudio.LocatorBuilder]::Build($numeric,(New-Target))) 'win32.ctrlId').Count-ne0) { throw '-1 ctrlId was used' }

$indexOnly=New-Base '' '' 0
$pathNode=New-Object AppStudio.UiaNode;$pathNode.ControlType='Edit';$pathNode.IndexAmongSameType=2;$pathNode.SiblingCount=4;$indexOnly.Uia.TreePath=@($pathNode)
$indexLocators=[AppStudio.LocatorBuilder]::Build($indexOnly,(New-Target))
if ((Find-Strategy $indexLocators 'uia.path').Count-ne1) { throw 'captured index path candidate missing' }
if ((Find-Strategy $indexLocators 'uia.nameControlType').Count-ne0) { throw 'Name candidate was invented from empty material' }

$coordinateOnly=New-Object AppStudio.Snapshot;$coordinateOnly.Win32=New-Object AppStudio.Win32Info;$coordinateOnly.Win32.WindowRect=New-Rect 140 160 30 30;$coordinateOnly.Win32.ClientRect=$coordinateOnly.Win32.WindowRect
$coordinateLocators=[AppStudio.LocatorBuilder]::Build($coordinateOnly,(New-Target))
if ($coordinateLocators.Count-ne1-or$coordinateLocators[0].Strategy-ne'screen.relative') { throw 'UIA-unavailable coordinate-only boundary produced invented material' }

foreach($locator in @($stableLocators)+@($numericLocators)+@($indexLocators)+@($coordinateLocators)) {
    if ([AppStudio.LocatorBuilder]::ContainsForbiddenPersistentMaterial($locator)) { throw ('forbidden persistent material in '+$locator.Strategy) }
    $json=[AppStudio.JsonWriter]::Write([AppStudio.LocatorJson]::Build($locator))
    if ($json-match 'secret-value|runtimeId|hwnd|liveValue|recordedValue') { throw ('forbidden value or ephemeral identity in '+$locator.Strategy) }
}
Write-Output ('PASS test-locator stableCandidates='+$stableLocators.Count+' numericAutoId=excluded invalidCtrlId=excluded indexPath=kept coordinateOnly=1 forbiddenMaterial=0')
