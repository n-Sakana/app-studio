$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-handoff-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

$session=[AppStudio.SessionStore]::Create($temp,'record','handoff fixture')
$session.ValuePolicy='recordText'
$session.Environment=(New-Object AppStudio.JsonObject).Add('os',(New-Object AppStudio.JsonObject).Add('caption','Fixture OS'))
$session.AddLimit('[uia] SCAN-TIMEOUT: the walk stopped after its allowance.')
$screen=New-Object AppStudio.ScreenRecord
$screen.ScanId='sc-1';$screen.ScreenId='S1';$screen.Title='Fixture Window';$screen.ClassName='FixtureWindow';$screen.PdfPage=1
$screen.Rect=New-Object AppStudio.RectValue;$screen.Rect.Width=900;$screen.Rect.Height=700
$session.Screens.Screens.Add($screen)
$node=New-Object AppStudio.ScanNode
$node.NodeId=1;$node.ScreenId='S1';$node.Name='Save';$node.ControlType='Button';$node.AutomationId='saveButton';$node.ClassName='Button';$node.CtrlId=1041
$session.Elements.Add($node)
$step=New-Object AppStudio.StepRecord
$step.Index=1;$step.Kind='click';$step.At=[DateTimeOffset]::Now;$step.GapMs=420;$step.AppName='fixture'
$step.WindowTitle='Fixture Window';$step.WindowClass='FixtureWindow';$step.ElementLabel='Button "Save"';$step.Confidence='high'
$step.FocusLabel='Edit "Name"'
$locator=New-Object AppStudio.ElementLocator;$locator.Strategy='uia.automationId';$locator.AutomationId='saveButton';$locator.ControlType='Button';$locator.Confidence='high'
$step.Locators.Add($locator)
$session.Steps.Add($step)

$project=[AppStudio.CodeProject]::Open($session)
$requestId=[AppStudio.Handoff]::NewRequestId()
if(-not[AppStudio.Handoff]::IsRequestId($requestId)){throw 'a freshly made request id is not accepted by its own reader'}
if([AppStudio.Handoff]::IsRequestId('not-an-id')){throw 'anything at all is being accepted as a request id'}
$result=[AppStudio.Handoff]::Build($session,$project,'Make the save reliable.',$requestId)
$text=$result.Text

# One text carries all eight things. None of them is left for the operator to
# fetch and paste themselves.
foreach($section in @('## 1. What is being asked','## 2. How to answer','## 3. The operation vocabulary','## 4. The machine, the screens and the coordinate system','## 5. What the operator did','## 6. Windows and elements','## 7. What could not be obtained','## 8. The code as it stands')){
 if(-not$text.Contains($section)){throw ('the request is missing the section '+$section)}
}
if(-not$text.Contains($requestId)){throw 'the request does not carry its own id'}
if(-not$text.Contains('Make the save reliable.')){throw 'the request dropped what was being asked'}
foreach($op in @('FindWindow','FocusElement','InvokeElement','SetElementText','ReadElementText','SendKeys','WaitGap','WaitIdle','AskSecret')){
 if(-not$text.Contains('`'+$op+'`')){throw ('the request does not describe the operation '+$op)}
}
# The return format is stated, in full, with the id already filled in.
foreach($line in @('SUMMARY BEGIN','SUMMARY END','BEGIN powershell','END powershell','COMPLETE 1','PART 00 OF 03','NOCHANGE UNNECESSARY')){
 if(-not$text.Contains('#@APPSTUDIO '+$requestId+' '+$line)){throw ('the return format does not show '+$line)}
}
foreach($claim in @('Never press a remembered screen coordinate','physical screen pixels','not a proof of completeness')){
 if(-not$text.Contains($claim)){throw ('the request dropped the rule: '+$claim)}
}
if(-not$text.Contains('SCAN-TIMEOUT')){throw 'the request dropped what could not be obtained'}
if(-not$text.Contains('| A1 |')){throw 'the request dropped the action log'}
if(-not$text.Contains('| 420 |')){throw 'the request dropped the recorded interval'}
if(-not$text.Contains('Edit "Name"')){throw 'the request dropped what held the keyboard'}
if(-not$text.Contains('saveButton')){throw 'the request dropped the element ledger'}
if(-not$text.Contains('RecordedProcedure.ps1')){throw 'the request dropped the PowerShell as it stands'}
if(-not$text.Contains('RecordedProcedure.bas')){throw 'the request dropped the VBA as it stands'}

# Short enough to paste in one go stays one copy.
if($result.Split){throw 'a request that fits in one paste was split anyway'}
if($result.Chunks.Count-ne1){throw ('a single request produced '+$result.Chunks.Count+' chunks')}
if(-not$result.Chunks[0].StartsWith('#@APPSTUDIO '+$requestId+' REQUEST 01 OF 01')){throw 'the single chunk is not numbered'}

# Too long to paste in one go is cut on line boundaries and numbered. Nothing is
# dropped to make it fit.
$long=New-Object Text.StringBuilder
for($i=0;$i -lt 2400;$i++){[void]$long.AppendLine('# filler line '+$i+' aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')}
$project.SetText('powershell','RecordedProcedure',$long.ToString())
$big=[AppStudio.Handoff]::Build($session,$project,'Make the save reliable.',$requestId)
if(-not$big.Split){throw 'a request far too long for one paste was not split'}
$total=$big.Chunks.Count
for($i=0;$i -lt $total;$i++){
 $head='#@APPSTUDIO '+$requestId+' REQUEST '+('{0:00}' -f ($i+1))+' OF '+('{0:00}' -f $total)
 if(-not$big.Chunks[$i].StartsWith($head)){throw ('chunk '+($i+1)+' is not numbered as '+$head)}
}
if(-not$big.Chunks[0].Contains('Read them all before answering')){throw 'the first part does not say to wait'}
if(-not$big.Chunks[$total-1].Contains('That is the whole request')){throw 'the last part does not say it is the last'}
$rejoined=New-Object Text.StringBuilder
for($i=0;$i -lt $total;$i++){
 $lines=$big.Chunks[$i] -split "`r`n"
 for($j=0;$j -lt $lines.Count;$j++){
  if($j-eq0){continue}
  if($lines[$j].StartsWith('This request is being sent in ')){continue}
  if($lines[$j].StartsWith('Continued. Part ')){continue}
  if($lines[$j]-eq'That is the whole request. Answer now, in the shape section 2 describes.'){continue}
  [void]$rejoined.AppendLine($lines[$j])
 }
}
$flat={param($s) ($s -replace "`r`n","" -replace "`n","" -replace " ","")}
if((&$flat $rejoined.ToString()).Length -lt (&$flat $big.Text).Length){throw 'putting the parts back together loses text'}
if(-not(&$flat $rejoined.ToString()).Contains((&$flat 'filler line 2399'))){throw 'the last of the text never reached any part'}

# The request is written beside the code, not into the assistant folder: that
# folder is exactly two files and has to stay that way.
$problem=[AppStudio.Handoff]::Write($project,$result)
if($null-ne$problem){throw ('the request could not be written: '+$problem)}
if(-not(Test-Path (Join-Path $project.Folder 'request.md'))){throw 'the request was not written beside the code'}
if(Test-Path (Join-Path $session.AiFolder 'request.md')){throw 'the request was written into the two file assistant folder'}

Write-Output ('PASS test-handoff bytes='+$text.Length+' chunks='+$total+' sections=8')
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
