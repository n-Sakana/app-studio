$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
function New-Rect([int]$x,[int]$y,[int]$w,[int]$h){$r=New-Object AppStudio.RectValue;$r.X=$x;$r.Y=$y;$r.Width=$w;$r.Height=$h;return $r}
function New-Snapshot([string]$id,[string]$name){$s=New-Object AppStudio.Snapshot;$s.Uia=New-Object AppStudio.UiaInfo;$s.Uia.AutomationId=$id;$s.Uia.Name=$name;$s.Uia.ControlType='Edit';$s.Uia.BoundingRect=New-Rect 10 10 80 20;return $s}
function New-Locator([string]$id,[string]$name,[string]$scopeKind){
 $s=New-Snapshot $id $name;$target=New-Object AppStudio.TargetInfo;$target.TargetRunId='run-a';$target.ProcessName='Fixture';if($scopeKind-eq'topLevelWindow'){$target.TopLevelClass='Form'};$all=[AppStudio.LocatorBuilder]::Build($s,$target);return @($all|Where-Object{$_.Strategy-eq'uia.automationId'})[0]
}
function Verify($locator,$original,$candidates,[string]$context,[int]$durationOverride){
 $ctx=New-Object AppStudio.ResolveContext;$ctx.Context=$context;$ctx.TargetRunId='run-a';$ctx.Original=$original;$ctx.Candidates=$candidates;$v=[AppStudio.Resolver]::Resolve($locator,$ctx);if($durationOverride-ge0){$v.DurationMs=$durationOverride;[AppStudio.Resolver]::ApplyVerification($locator,$v)};return $v
}
$expected=New-Snapshot 'StableId' 'Customer code'
$zero=New-Locator 'StableId' 'Customer code' 'topLevelWindow';$v0=Verify $zero $expected @() 'immediate' -1
$one=New-Locator 'StableId' 'Customer code' 'topLevelWindow';$v1=Verify $one $expected @((New-Snapshot 'StableId' 'Customer code')) 'immediate' -1
$two=New-Locator 'StableId' 'Customer code' 'topLevelWindow';$v2=Verify $two $expected @((New-Snapshot 'StableId' 'Customer code'),(New-Snapshot 'StableId' 'Other')) 'immediate' -1
if($v0.MatchCount-ne0-or$zero.Confidence.Level-ne'low'){throw '0-match confidence mismatch'}
if($v1.MatchCount-ne1-or-not$v1.SameElement-or$one.Confidence.Level-ne'high'){throw ('1-match confidence mismatch: '+$one.Confidence.Level)}
if($v2.MatchCount-ne2-or$two.Confidence.Level-ne'medium'){throw ('2-match confidence mismatch: '+$two.Confidence.Level)}
if(($zero.Confidence.Level-eq$one.Confidence.Level)-or($one.Confidence.Level-eq$two.Confidence.Level)-or($zero.Confidence.Level-eq$two.Confidence.Level)){throw '0/1/2 match levels were not distinct'}

$processScoped=New-Locator 'StableId' 'Customer code' 'process'
$windowScoped=New-Locator 'StableId' 'Customer code' 'topLevelWindow'
if($processScoped.Confidence.Score-ge$windowScoped.Confidence.Score){throw 'process scope penalty missing'}
$slow=New-Locator 'StableId' 'Customer code' 'topLevelWindow';$before=$slow.Confidence.Score;$null=Verify $slow $expected @((New-Snapshot 'StableId' 'Customer code')) 'restart' 1001;if($slow.Confidence.Score-ge100-or-not($slow.Confidence.Reasons-match 'longer than one second')){throw 'slow resolution penalty missing'}

$dynamicSnapshot=New-Snapshot '' 'Updated 2026-08-01 12:34 99%';$stableSnapshot=New-Snapshot '' 'Customer code';$target=New-Object AppStudio.TargetInfo;$target.TopLevelClass='Form'
$dynamic=@([AppStudio.LocatorBuilder]::Build($dynamicSnapshot,$target)|Where-Object{$_.Strategy-eq'uia.nameControlType'})[0]
$stableName=@([AppStudio.LocatorBuilder]::Build($stableSnapshot,$target)|Where-Object{$_.Strategy-eq'uia.nameControlType'})[0]
if($dynamic.Confidence.Score-ge$stableName.Confidence.Score){throw 'dynamic Name penalty missing'}
$screenSnapshot=New-Snapshot '' '';$target.ClientRect=New-Rect 0 0 200 100;$screen=@([AppStudio.LocatorBuilder]::Build($screenSnapshot,$target)|Where-Object{$_.Strategy-eq'screen.relative'})[0];if($screen.Confidence.Level-ne'low'-or$screen.Confidence.Score-gt35){throw 'screen.relative cap missing'}

foreach($locator in @($zero,$one,$two,$processScoped,$windowScoped,$slow,$dynamic,$stableName,$screen)) {
 if($locator.Confidence.Reasons.Count-lt1){throw 'confidence reasons missing'}
 foreach($reason in $locator.Confidence.Reasons){if([string]::IsNullOrWhiteSpace($reason)-or$reason.Contains("`n")-or$reason.Contains("`r")){throw 'reason is not one human-readable line'}}
}
Write-Output ('PASS test-confidence matchLevels=0:low,1:high,2:medium dynamicPenalty=1 processPenalty=1 slowPenalty=1 screenCap=low reasons=all')
