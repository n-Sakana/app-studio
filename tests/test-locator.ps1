$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly

function New-Rect([int]$x,[int]$y,[int]$w,[int]$h){$r=New-Object AppStudio.RectValue;$r.X=$x;$r.Y=$y;$r.Width=$w;$r.Height=$h;return $r}
function New-Node([string]$automationId,[string]$name,[string]$type,[string]$class,[int]$ctrlId,[string]$path,$rect){
 $n=New-Object AppStudio.ScanNode;$n.AutomationId=$automationId;$n.Name=$name;$n.ControlType=$type;$n.ClassName=$class;$n.CtrlId=$ctrlId;$n.Path=$path;$n.Rect=$rect;return $n
}
function New-List($items){$l=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';foreach($i in $items){$l.Add($i)};return $l}
function Find([object]$items,[string]$strategy){return @($items|Where-Object{$_.Strategy-eq$strategy})}

$window=New-Rect 100 100 500 400
$ok=New-Node 'CustomerCode' 'Customer code' 'Edit' 'Edit' 1002 'Window > Pane > Edit "Customer code"' (New-Rect 120 130 100 20)
$siblings=New-List @($ok,(New-Node 'Other' 'Other' 'Edit' 'Edit' 1003 'Window > Pane > Edit "Other"' (New-Rect 120 160 100 20)))
$all=[AppStudio.LocatorBuilder]::Build($ok,$window,$siblings)
foreach($strategy in @('uia.automationId','uia.nameControlType','uia.treePath','win32.ctrlId','win32.classIndex','window.relative')){
 if((Find $all $strategy).Count-ne1){throw ('locator missing: '+$strategy)}
}
if((Find $all 'uia.automationId')[0].Confidence-ne'high'){throw 'stable AutomationId was not high'}
if((Find $all 'window.relative')[0].Confidence-ne'low'){throw 'a position was not capped at low'}
if([AppStudio.LocatorBuilder]::BestConfidence($all)-ne'high'){throw 'best confidence mismatch'}

# Numeric-only ids and 0 / -1 control ids carry no meaning and are not offered.
$numeric=New-Node '12345' '' 'Edit' 'Edit' 0 $null (New-Rect 10 10 20 20)
$numericAll=[AppStudio.LocatorBuilder]::Build($numeric,$window,(New-List @($numeric)))
if((Find $numericAll 'uia.automationId').Count-ne0){throw 'numeric-only AutomationId was used'}
if((Find $numericAll 'win32.ctrlId').Count-ne0){throw 'zero ctrlId was used'}
if((Find $numericAll 'uia.nameControlType').Count-ne0){throw 'a name locator was invented from an empty name'}
$numeric.CtrlId=-1
if((Find ([AppStudio.LocatorBuilder]::Build($numeric,$window,(New-List @($numeric)))) 'win32.ctrlId').Count-ne0){throw '-1 ctrlId was used'}

# Nothing but a rectangle: exactly one locator, and it is a description.
$bare=New-Node $null $null $null $null 0 $null (New-Rect 140 160 30 30)
$bareAll=[AppStudio.LocatorBuilder]::Build($bare,$window,(New-List @($bare)))
if($bareAll.Count-ne1-or$bareAll[0].Strategy-ne'window.relative'){throw ('bare element invented material: '+(($bareAll|ForEach-Object{$_.Strategy})-join','))}
if([AppStudio.LocatorBuilder]::BestConfidence($bareAll)-ne'low'){throw 'bare element was not low confidence'}

# A duplicated name is stated as medium, not sold as unique.
$twinA=New-Node $null 'OK' 'Button' 'Button' 0 'W > Button "OK"' (New-Rect 10 10 40 20)
$twinB=New-Node $null 'OK' 'Button' 'Button' 0 'W > Button "OK" 2' (New-Rect 60 10 40 20)
$twinAll=[AppStudio.LocatorBuilder]::Build($twinA,$window,(New-List @($twinA,$twinB)))
if((Find $twinAll 'uia.nameControlType')[0].Confidence-ne'medium'){throw 'a duplicated name was not reduced to medium'}

# No locator may ever carry a handle, a RuntimeId or a field value.
foreach($locator in @($all)+@($numericAll)+@($bareAll)+@($twinAll)){
 if($locator.Reasons.Count-lt1){throw ('no reason given for '+$locator.Strategy)}
 $json=[AppStudio.JsonWriter]::Write($locator.ToJson())
 foreach($forbidden in @('hwnd','runtimeId','liveValue','value')){
  if($json-match ('"'+$forbidden+'"')){throw ($forbidden+' reached a locator: '+$locator.Strategy)}
 }
}

# Resolution: unique wins, ambiguous refuses, missing refuses, and a position is
# never used to break a tie.
$fresh=New-List @((New-Node 'CustomerCode' 'Customer code' 'Edit' 'Edit' 1002 'Window > Pane > Edit "Customer code"' (New-Rect 120 130 100 20)))
$resolved=[AppStudio.LocatorResolver]::Resolve($all,$fresh)
if(-not$resolved.Resolved-or$resolved.UsedLocator.Strategy-ne'uia.automationId'){throw ('unique resolution failed: '+$resolved.State)}

$twinLocators=[AppStudio.LocatorBuilder]::Build($twinA,$window,(New-List @($twinA,$twinB)))
$twinFresh=New-List @((New-Node $null 'OK' 'Button' 'Button' 0 'x' (New-Rect 10 10 40 20)),(New-Node $null 'OK' 'Button' 'Button' 0 'y' (New-Rect 60 10 40 20)))
$ambiguous=[AppStudio.LocatorResolver]::Resolve($twinLocators,$twinFresh)
if($ambiguous.Resolved-or$ambiguous.State-ne'ambiguous'){throw ('ambiguity was not refused: '+$ambiguous.State)}
if($ambiguous.Reason-notmatch 'guess'){throw 'the ambiguity refusal did not say why'}

$missing=[AppStudio.LocatorResolver]::Resolve($all,(New-List @((New-Node 'Somethingelse' 'x' 'Edit' 'Edit' 9 'z' (New-Rect 0 0 5 5)))))
if($missing.Resolved-or$missing.State-ne'not-found'){throw ('a missing element was not refused: '+$missing.State)}

$positionOnly=[AppStudio.LocatorResolver]::Resolve($bareAll,$fresh)
if($positionOnly.Resolved-or$positionOnly.State-ne'no-locator'){throw ('a position was used as an address: '+$positionOnly.State)}
if(($positionOnly.Trace-join' ')-notmatch 'never used to decide'){throw 'the position locator was not reported as unusable'}
# The same holds for the structural index: it describes, it does not identify.
$indexOnly=New-Node $null $null $null 'Edit' 0 $null (New-Rect 120 130 100 20)
$indexLocators=[AppStudio.LocatorBuilder]::Build($indexOnly,$window,(New-List @($indexOnly)))
if((Find $indexLocators 'win32.classIndex').Count-ne1){throw 'the class index locator was not recorded'}
$indexResolve=[AppStudio.LocatorResolver]::Resolve($indexLocators,$fresh)
if($indexResolve.Resolved-or$indexResolve.State-ne'no-locator'){throw ('a structural index was used as an address: '+$indexResolve.State)}
foreach($strategy in @('uia.automationId','uia.nameControlType','uia.treePath','win32.ctrlId')){
 if(-not [AppStudio.LocatorResolver]::Identifies($strategy)){throw ($strategy+' stopped counting as an identification')}
}
foreach($strategy in @('win32.classIndex','window.relative')){
 if([AppStudio.LocatorResolver]::Identifies($strategy)){throw ($strategy+' was promoted to an identification')}
}

$empty=[AppStudio.LocatorResolver]::Resolve($all,(New-List @()))
if($empty.State-ne'no-elements'){throw ('an empty candidate list was not reported: '+$empty.State)}

Write-Output ('PASS test-locator strategies=6 numericAutoId=excluded invalidCtrlId=excluded bareElement=position-only duplicateName=medium forbiddenMaterial=0 resolve=unique/ambiguous/not-found/no-elements positionAndIndexNeverResolve=1')
