$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-replay-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

# --- 1. which routes exist, and what a narrowed session means -------------
foreach($mode in @('auto','uia','win32','sendInput')){ if(-not [AppStudio.ProbeRoutes]::IsKnown($mode)){throw ('route mode missing: '+$mode)} }
if([AppStudio.ProbeRoutes]::IsKnown('msaa')){throw 'MSAA was offered as a way to carry an operation out'}
foreach($route in @('uia','win32','sendInput')){
 if(-not [AppStudio.ProbeRoutes]::Allows('auto',$route)){throw ('auto refused '+$route)}
 if(-not [AppStudio.ProbeRoutes]::Allows($route,$route)){throw ($route+' refused itself')}
}
if([AppStudio.ProbeRoutes]::Allows('uia','win32')){throw 'a narrowed session widened back to another route'}
if([AppStudio.ProbeRoutes]::Allows('win32','sendInput')){throw 'a narrowed session widened back to another route'}

# --- 2. a refusal is still an attempt record -----------------------------
$reference=New-Object AppStudio.ElementRef;$reference.X=-30000;$reference.Y=-30000;$reference.Hwnd=0
$readOnly=[AppStudio.ProbeRunner]::Run($reference,[AppStudio.ProbeKind]::Invoke,(New-Object AppStudio.ProbeArgs))
if($readOnly.Outcome-ne'blocked'){throw ('a write was allowed without permission: '+$readOnly.Outcome)}
if($readOnly.Attempts.Count-lt1){throw 'a refusal left no trail'}
if($readOnly.Attempts[0].Route-ne'guard'-or$readOnly.Attempts[0].Effect-notmatch 'nothing was sent'){throw 'the refusal trail does not say that nothing was sent'}
if($readOnly.AttemptLine-notmatch 'policy.readOnly'){throw ('the refusal reason is not in the trail: '+$readOnly.AttemptLine)}
$noElement=[AppStudio.ProbeRunner]::Run($null,[AppStudio.ProbeKind]::Read,(New-Object AppStudio.ProbeArgs))
if($noElement.Outcome-ne'blocked'-or$noElement.Attempts.Count-lt1){throw 'a missing element produced no stated refusal'}

# --- 3. the trail reads in the order the routes were tried ---------------
$outcome=New-Object AppStudio.ReplayOutcome
foreach($pair in @(@('uia','uia.InvokePattern','notSupported'),@('win32','win32.BM_CLICK','success'))){
 $attempt=New-Object AppStudio.RouteAttempt;$attempt.Route=$pair[0];$attempt.Method=$pair[1];$attempt.Outcome=$pair[2];$outcome.Attempts.Add($attempt)
}
if($outcome.AttemptLine-ne'uia.InvokePattern:notSupported -> win32.BM_CLICK:success'){throw ('trail text mismatch: '+$outcome.AttemptLine)}

# --- 4. a recording whose application is not running ---------------------
$session=[AppStudio.SessionStore]::Create($temp,'record','replay fixture')
$node=New-Object AppStudio.ScanNode
$node.NodeId=0;$node.ScreenId='S1';$node.Name='Save';$node.AutomationId='SaveButton';$node.ControlType='Button';$node.ClassName='Button';$node.CtrlId=1
$node.Path='Window > Button "Save"';$node.Rect=New-Object AppStudio.RectValue;$node.Rect.X=10;$node.Rect.Y=10;$node.Rect.Width=60;$node.Rect.Height=24
$window=New-Object AppStudio.RectValue;$window.Width=800;$window.Height=600
$siblings=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';$siblings.Add($node)

$step=New-Object AppStudio.StepRecord
$step.Index=1;$step.At=[DateTimeOffset]::Now;$step.Kind='click';$step.AppName='NoSuchApplication9f3c'
$step.WindowTitle='No such window 9f3c';$step.WindowClass='NoSuchWindowClass9f3c';$step.ElementLabel='Button "Save"'
$step.Locators=[AppStudio.LocatorBuilder]::Build($node,$window,$siblings)
$session.Steps.Add($step)
[AppStudio.SessionStore]::WriteMeta($session)

# Without permission nothing is attempted at all.
$engine=New-Object AppStudio.ReplayEngine($root,$session)
try{
 $blockedRun=$engine.Run('auto',$false)
 if($blockedRun.Succeeded-ne0){throw 'a step ran without the write permission'}
 $blocked=$session.Steps[0].LastReplay
 if($blocked.State-ne'blocked'){throw ('a step was not blocked without permission: '+$blocked.State)}
 if($blocked.Reason-notmatch 'Nothing was sent'){throw 'the refusal did not say that nothing was sent'}
}finally{$engine.Dispose()}

# With permission, a window that does not exist stops the run with a reason
# instead of acting somewhere plausible.
$engine2=New-Object AppStudio.ReplayEngine($root,$session)
try{
 $run=$engine2.Run('auto',$true)
 if($run.Succeeded-ne0){throw 'a step succeeded although its application is not running'}
 if($run.Stopped-lt1-or[string]::IsNullOrEmpty($run.StopReason)){throw 'the run stopped without saying why'}
 $result=$session.Steps[0].LastReplay
 if($result.State-ne'not-found'){throw ('a missing window was not reported as such: '+$result.State)}
 if($result.Reason-notmatch 'NoSuchWindowClass9f3c'){throw 'the stop reason does not name what was expected'}
 if($result.Attempts.Count-lt1-or$result.Attempts[0].Effect-notmatch 'nothing was sent'){throw 'a missing window left no trail'}
}finally{$engine2.Dispose()}

# --- 5. a step nothing can address is refused, not guessed ---------------
$bare=New-Object AppStudio.ScanNode
$bare.Rect=New-Object AppStudio.RectValue;$bare.Rect.X=5;$bare.Rect.Y=5;$bare.Rect.Width=10;$bare.Rect.Height=10
$bareLocators=[AppStudio.LocatorBuilder]::Build($bare,$window,(New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'))
$fresh=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';$fresh.Add($node)
$bareResolve=[AppStudio.LocatorResolver]::Resolve($bareLocators,$fresh)
if($bareResolve.Resolved-or$bareResolve.State-ne'no-locator'){throw ('an unaddressable element was acted on: '+$bareResolve.State)}

# --- 6. the run is written to the session as it happens ------------------
$replayLog=Join-Path $session.Folder 'replay.jsonl'
if(-not(Test-Path $replayLog)){throw 'the replay left no record in the session'}
$lines=[AppStudio.SessionLog]::ReadAllLines($replayLog)
if($lines.Count-lt2){throw ('the replay record is incomplete: '+$lines.Count+' line(s)')}
foreach($line in $lines){ if($line-notmatch '"routeMode"'){throw 'a replay line does not say which routes were allowed'} }

Write-Output 'PASS test-replay routeModes=auto/uia/win32/sendInput msaaOffered=0 narrowingNeverWidens=1 refusalTrail=stated missingWindow=not-found noPermission=blocked unaddressable=refused replayLog=written'
}finally{Remove-Item $temp -Recurse -Force}
