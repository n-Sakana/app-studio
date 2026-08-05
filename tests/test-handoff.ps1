$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-handoff-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

$session=[AppStudio.SessionStore]::Create($temp,'record','handoff fixture')
$session.ValuePolicy='recordText'
$session.Environment=(New-Object AppStudio.JsonObject).Add('os',(New-Object AppStudio.JsonObject).Add('caption','Fixture OS'))
$session.AddLimit('[uia] SCAN-TIMEOUT: the walk stopped after its allowance.')
# The picture document is one of the two attachments, so the fixture has a real
# picture in it. Without one there is no PDF, and the attachment check would be
# passing on a session that could never be handed over.
$shot=Join-Path $session.ShotsFolder 'S1.png'
New-Item -ItemType Directory $session.ShotsFolder -Force|Out-Null
$bitmap=New-Object Drawing.Bitmap(320,240)
try{
 $g=[Drawing.Graphics]::FromImage($bitmap)
 try{$g.Clear([Drawing.Color]::White);$g.FillRectangle([Drawing.Brushes]::SteelBlue,20,20,120,60)}finally{$g.Dispose()}
 $bitmap.Save($shot,[Drawing.Imaging.ImageFormat]::Png)
}finally{$bitmap.Dispose()}
$screen=New-Object AppStudio.ScreenRecord
$screen.ScanId='sc-1';$screen.ScreenId='S1';$screen.Title='Fixture Window';$screen.ClassName='FixtureWindow';$screen.PdfPage=1
$screen.Rect=New-Object AppStudio.RectValue;$screen.Rect.Width=900;$screen.Rect.Height=700
$screen.ShotFile=$shot;$screen.CapturedAt=[DateTimeOffset]::Now;$screen.CaptureMethod='BitBlt'
$screen.Sha256=(Get-FileHash $shot -Algorithm SHA256).Hash
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

# The two files the request tells the assistant to read have to exist before it
# is worth pasting anything, so they are written the way the product writes
# them.
$null=[AppStudio.Outputs]::WriteAll($session,6291456,$project)

$result=[AppStudio.Handoff]::Build($session,$project,'Make the save reliable.',$requestId)
$text=$result.Text

# ---- one copy, one paste -----------------------------------------------------
#
# There is no such thing as a chunk here any more. The request is short because
# what the assistant has to read is attached, so nothing has to be cut up and
# nothing has to be pasted twice.
if($result.PSObject.Properties.Name-contains'Chunks'){throw 'the request can still be cut into chunks'}
if($result.PSObject.Properties.Name-contains'Split'){throw 'the request still knows how to split itself'}
if($text.Contains('REQUEST 01 OF')-or$text.Contains(' REQUEST ')){throw 'the request still numbers itself as one of several'}
if($text.Length-gt20000){throw ('the request is '+$text.Length+' characters, which is not a one paste request any more')}

# ---- what is in it -----------------------------------------------------------
foreach($section in @('## The two files attached with this message','## What is being asked','## The modules','## How to answer')){
 if(-not$text.Contains($section)){throw ('the request is missing the section '+$section)}
}
if(-not$text.Contains($requestId)){throw 'the request does not carry its own id'}
if(-not$text.Contains('Make the save reliable.')){throw 'the request dropped what was being asked'}
foreach($name in @('session.md','screens.pdf')){
 if(-not$text.Contains($name)){throw ('the request does not name the attachment '+$name)}
}
if(-not$text.Contains('section 10')){throw 'the request does not say where in the attachment the code is'}

# ---- what is NOT in it -------------------------------------------------------
#
# The code, the log, the ledger and the limits belong to the attachment. Putting
# them here as well is what made the request too long to paste in one go.
if($text.Contains('SCAN-TIMEOUT')){throw 'the request embeds what could not be obtained instead of attaching it'}
if($text.Contains('| A1 |')){throw 'the request embeds the action log'}
if($text.Contains('| 420 |')){throw 'the request embeds the recorded intervals'}
if($text.Contains('saveButton')){throw 'the request embeds the element ledger'}
if($text.Contains('namespace AppStudioRun')){throw 'the request embeds the generated engine'}
if($text.Contains('Attribute VB_Name')){throw 'the request embeds the generated VBA'}
if($text.Contains('```powershell')-or$text.Contains('```vb')){throw 'the request carries a code block of the automation'}

# ---- the modules are named, so an answer can say which one it is --------------
foreach($module in @('Workflow','RecordedFacts','RuntimeCore','RuntimeLocator','RuntimeNative')){
 if(-not$text.Contains('`'+$module+'`')-and-not$text.Contains($module+'.cs')){throw ('the request does not name the module '+$module)}
}

# ---- the return format, in full, with the id already filled in ---------------
foreach($line in @('SUMMARY BEGIN','SUMMARY END','BEGIN powershell','END powershell','COMPLETE 1','PART 00 OF 03','NOCHANGE UNNECESSARY')){
 if(-not$text.Contains('#@APPSTUDIO '+$requestId+' '+$line)){throw ('the return format does not show '+$line)}
}
foreach($rule in @('one module per message','UNCLEAR','Do not offer choices and do not ask a question')){
 if(-not$text.Contains($rule)){throw ('the return format dropped: '+$rule)}
}

# ---- the attachments are looked at, not assumed ------------------------------
if($result.Attachments.Count-ne2){throw ('the request names '+$result.Attachments.Count+' attachments instead of 2')}
if(-not$result.AttachmentsReady){throw ('the two files were written but the request says they are missing: '+$result.MissingText())}
foreach($attachment in $result.Attachments){
 if(-not$attachment.Exists){throw ($attachment.Name+' is not on disk')}
 if($attachment.Bytes-le0){throw ($attachment.Name+' is empty')}
}
Remove-Item (Join-Path $session.AiFolder 'screens.pdf') -Force
$gone=[AppStudio.Handoff]::Build($session,$project,'Make the save reliable.',$requestId)
if($gone.AttachmentsReady){throw 'a missing attachment was reported as ready'}
if($gone.MissingText()-ne'screens.pdf'){throw ('the missing attachment was named as '+$gone.MissingText())}

# ---- the code really is in the attachment ------------------------------------
$markdown=[IO.File]::ReadAllText($session.SessionMdPath,(New-Object Text.UTF8Encoding($false)))
if(-not$markdown.Contains('## 10. The automation as it stands')){throw 'session.md does not carry the automation'}
foreach($module in @('Workflow.cs','RecordedFacts.cs','RuntimeCore.cs','Workflow.bas','RuntimeCore.bas')){
 if(-not$markdown.Contains($module)){throw ('session.md does not carry '+$module)}
}
foreach($op in @('FindWindow','FocusElement','InvokeElement','SetElementText','ReadElementText','SendKeys','WaitGap','WaitIdle','AskSecret')){
 if(-not$markdown.Contains('`'+$op+'`')){throw ('session.md does not describe the operation '+$op)}
}
foreach($claim in @('Never press a remembered screen coordinate','physical screen pixels','not a proof of completeness')){
 if(-not$markdown.Contains($claim)){throw ('session.md dropped the rule: '+$claim)}
}
if(-not$markdown.Contains('namespace AppStudioRun')){throw 'session.md does not carry the engine as it stands'}
if(-not$markdown.Contains('Attribute VB_Name')){throw 'session.md does not carry the VBA as it stands'}

# ---- the handover is still exactly two files ---------------------------------
$aiFiles=@(Get-ChildItem $session.AiFolder -File)
if($aiFiles.Count-ne1){throw 'the assistant folder changed shape while one file was deleted for the test'}
$null=[AppStudio.Outputs]::WriteAll($session,6291456,$project)
$aiFiles=@(Get-ChildItem $session.AiFolder -File)
if($aiFiles.Count-ne2){throw ('the assistant folder holds '+$aiFiles.Count+' files instead of 2')}

# The request is written beside the code, not into the assistant folder: that
# folder is exactly two files and has to stay that way.
$problem=[AppStudio.Handoff]::Write($project,$result)
if($null-ne$problem){throw ('the request could not be written: '+$problem)}
if(-not(Test-Path (Join-Path $project.Folder 'request.md'))){throw 'the request was not written beside the code'}
if(Test-Path (Join-Path $session.AiFolder 'request.md')){throw 'the request was written into the two file assistant folder'}

Write-Output ('PASS test-handoff requestChars='+$text.Length+' copies=1 attachments=2 embeddedCode=0 sessionMdChars='+$markdown.Length)
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
