$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-store-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{
$session=[AppStudio.SessionStore]::Create($temp,'record','store fixture')
if(-not(Test-Path $session.Folder)){throw 'the session folder was not created'}
if($session.Id.Length-lt10){throw 'the session id is not a usable name'}

# --- elements -------------------------------------------------------------
$node=New-Object AppStudio.ScanNode
$node.NodeId=0;$node.ScreenId='S1';$node.Name='Customer code';$node.AutomationId='CustomerCode';$node.ControlType='Edit'
$node.ClassName='Edit';$node.CtrlId=1002;$node.Path='Window > Edit "Customer code"';$node.Patterns=@('Value','LegacyIAccessible')
$node.Visible=$true;$node.Enabled=$true;$node.IsPassword=$false;$node.ProcessId=4321
$node.Rect=New-Object AppStudio.RectValue;$node.Rect.X=10;$node.Rect.Y=20;$node.Rect.Width=100;$node.Rect.Height=24
$node.AddSource('uia');$node.AddSource('win32')
$session.Elements.Add($node)
if(-not [AppStudio.SessionStore]::Append($session,'elements',[AppStudio.ScanJson]::Node($node,'sc-0001',0))){throw 'an element line could not be written'}

# --- screen ---------------------------------------------------------------
$screen=New-Object AppStudio.ScreenRecord
$screen.ScanId='sc-0001';$screen.ScreenId='S1';$screen.Hwnd=12345;$screen.Title='Fixture window';$screen.ClassName='FixtureWindow';$screen.NodeCount=1
$screen.Rect=New-Object AppStudio.RectValue;$screen.Rect.Width=640;$screen.Rect.Height=480
$screen.ComponentIds.Add('E0')
$screen.ShotProblem='SHOT-NORECT: fixture screen has no picture on purpose.'
$session.Screens.Screens.Add($screen)
$null=[AppStudio.SessionStore]::Append($session,'screens',$screen.ToJson())

# --- step with locators and a replay result -------------------------------
$step=New-Object AppStudio.StepRecord
$step.Index=1;$step.At=[DateTimeOffset]::Now;$step.OffsetMs=1500;$step.Kind='click';$step.AppKey='P1';$step.AppName='Fixture'
$step.ProcessId=4321;$step.Hwnd=12345;$step.WindowTitle='Fixture window';$step.WindowClass='FixtureWindow'
$step.ScreenBefore='S1';$step.ElementLabel='Edit "Customer code"';$step.Name='Customer code';$step.AutomationId='CustomerCode'
$step.ControlType='Edit';$step.ClassName='Edit';$step.CtrlId=1002;$step.TreePath=$node.Path;$step.Rect=$node.Rect
$step.Sources.Add('uia');$step.EffectSummary='no window or title change was observed after this'
$step.Diagnostics.Add('uia: partial')
$step.Unavailable.Add('tree-path-unknown: fixture')
$window=New-Object AppStudio.RectValue;$window.Width=640;$window.Height=480
$siblings=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';$siblings.Add($node)
$step.Locators=[AppStudio.LocatorBuilder]::Build($node,$window,$siblings)
$step.Confidence=[AppStudio.LocatorBuilder]::BestConfidence($step.Locators)
$outcome=New-Object AppStudio.ReplayOutcome
$outcome.At=[DateTimeOffset]::Now;$outcome.State='done';$outcome.Reason='Carried out through win32.BM_CLICK.';$outcome.ResolvedBy='uia.automationId';$outcome.MatchCount=1;$outcome.DurationMs=210
$first=New-Object AppStudio.RouteAttempt;$first.Route='uia';$first.Method='uia.InvokePattern';$first.Outcome='notSupported';$first.DurationMs=40
$second=New-Object AppStudio.RouteAttempt;$second.Route='win32';$second.Method='win32.BM_CLICK';$second.Outcome='success';$second.DurationMs=90;$second.Effect='value changed'
$outcome.Attempts.Add($first);$outcome.Attempts.Add($second)
if($outcome.AttemptLine-ne'uia.InvokePattern:notSupported -> win32.BM_CLICK:success'){throw ('the attempt trail does not read in order: '+$outcome.AttemptLine)}
$step.LastReplay=$outcome
$session.Steps.Add($step)
$null=[AppStudio.SessionStore]::Append($session,'steps',$step.ToJson())
$session.Register(4321)|Out-Null
$session.AddLimit('fixture limit')
$session.EndedAt=[DateTimeOffset]::Now
[AppStudio.SessionStore]::WriteMeta($session)

# --- the log is readable while it is still open ---------------------------
$open=[IO.File]::Open((Join-Path $session.Folder 'steps.jsonl'),'Open','Write','ReadWrite')
try{ if(([AppStudio.SessionLog]::ReadAllLines((Join-Path $session.Folder 'steps.jsonl'))).Count-lt1){throw 'a live log could not be read back'} }finally{$open.Dispose()}

# --- round trip -----------------------------------------------------------
$loaded=[AppStudio.SessionStore]::Load($session.Folder)
if($null-eq$loaded){throw 'the session could not be loaded back'}
if($loaded.Kind-ne'record'-or$loaded.Title-ne'store fixture'){throw 'session identity did not survive the round trip'}
if($loaded.Elements.Count-ne1-or$loaded.Screens.Screens.Count-ne1-or$loaded.Steps.Count-ne1){throw 'record counts did not survive the round trip'}
if($loaded.Apps.Count-ne1){throw 'the application list did not survive the round trip'}
$re=$loaded.Elements[0]
foreach($pair in @(@('AutomationId','CustomerCode'),@('ControlType','Edit'),@('ClassName','Edit'),@('Path','Window > Edit "Customer code"'))){
 if($re.($pair[0])-ne$pair[1]){throw ('element field lost: '+$pair[0]+' = '+$re.($pair[0]))}
}
if($re.CtrlId-ne1002-or$re.Rect.Width-ne100-or$re.Patterns.Count-ne2){throw 'element detail lost in the round trip'}
if($re.Visible-ne$true-or$re.Enabled-ne$true){throw 'element state lost in the round trip'}
if($re.Sources.Count-lt2){throw 'acquisition sources lost in the round trip'}
$rs=$loaded.Steps[0]
if($rs.StepId-ne'A1'-or$rs.Kind-ne'click'-or$rs.AutomationId-ne'CustomerCode'){throw 'step identity lost in the round trip'}
if($rs.Locators.Count-ne$step.Locators.Count){throw 'locators lost in the round trip'}
if($null-eq$rs.LastReplay-or$rs.LastReplay.Attempts.Count-ne2){throw 'the route trail was lost in the round trip'}
if($rs.LastReplay.AttemptLine-ne$outcome.AttemptLine){throw 'the route trail changed in the round trip'}
if($rs.Diagnostics.Count-ne1-or$rs.Unavailable.Count-ne1){throw 'step diagnostics lost in the round trip'}
$rscreen=$loaded.Screens.Screens[0]
if($rscreen.ScreenId-ne'S1'-or$rscreen.ComponentIds.Count-ne1){throw 'screen ledger lost in the round trip'}
if([string]::IsNullOrEmpty($rscreen.ShotProblem)){throw 'a screen without a picture lost its reason'}

# --- the listing finds it, newest first ----------------------------------
$second=[AppStudio.SessionStore]::Create($temp,'snap','second fixture')
[AppStudio.SessionStore]::WriteMeta($second)
$list=[AppStudio.SessionStore]::List($temp)
if($list.Count-ne2){throw ('the listing did not find both sessions: '+$list.Count)}
if((Split-Path $list[0] -Leaf)-ne$second.Id){throw 'the listing is not newest first'}

# --- a broken folder is reported, not swallowed --------------------------
$broken=Join-Path ([AppStudio.SessionStore]::Root($temp)) 'broken-session'
New-Item -ItemType Directory $broken|Out-Null
[IO.File]::WriteAllText((Join-Path $broken 'meta.json'),'{ this is not json')
$brokenLoad=[AppStudio.SessionStore]::Load($broken)
if($null-eq$brokenLoad-or$brokenLoad.Limits.Count-lt1){throw 'an unreadable session folder was swallowed instead of reported'}

Write-Output 'PASS test-session-store append=durable liveRead=1 roundTrip=elements+screens+steps+locators+routes listing=newest-first unreadable=reported'
}finally{Remove-Item $temp -Recurse -Force}
