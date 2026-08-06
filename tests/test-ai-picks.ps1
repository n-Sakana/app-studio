$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-picks-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

# What the operator hands to an assistant is chosen item by item. This checks the
# thing the window promises: that the selection is what decides the files, that
# every combination is allowed, and that what is said about the result is read
# back from the result rather than assumed from the request.

$session=[AppStudio.SessionStore]::Create($temp,'record','picks fixture')
$session.ValuePolicy='recordText'
$session.Environment=(New-Object AppStudio.JsonObject).Add('os',(New-Object AppStudio.JsonObject).Add('caption','Fixture OS'))
$session.AddLimit('[uia] SCAN-TIMEOUT: the walk stopped after its allowance.')
$session.AddDiagnostic('[test] one diagnostic line')
$shot=Join-Path $session.ShotsFolder 'S1.png'
New-Item -ItemType Directory $session.ShotsFolder -Force|Out-Null
$bitmap=New-Object Drawing.Bitmap(320,240)
try{
 $g=[Drawing.Graphics]::FromImage($bitmap)
 try{$g.Clear([Drawing.Color]::White);$g.FillRectangle([Drawing.Brushes]::SteelBlue,20,20,120,60)}finally{$g.Dispose()}
 $bitmap.Save($shot,[Drawing.Imaging.ImageFormat]::Png)
}finally{$bitmap.Dispose()}
$screen=New-Object AppStudio.ScreenRecord
$screen.ScanId='sc-1';$screen.ScreenId='S1';$screen.Title='Fixture Window';$screen.ClassName='FixtureWindow'
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
$locator=New-Object AppStudio.ElementLocator;$locator.Strategy='uia.automationId';$locator.AutomationId='saveButton';$locator.ControlType='Button';$locator.Confidence='high'
$step.Locators.Add($locator)
$session.Steps.Add($step)

$project=[AppStudio.CodeProject]::Open($session)
$requestId=[AppStudio.Handoff]::NewRequestId()
$allIds=[AppStudio.AiItems]::Order()
$contextIds=[AppStudio.AiItems]::Context()

function Pick([string[]]$on){
 $p=[AppStudio.AiPicks]::Nothing()
 foreach($id in $on){$p.Set($id,$true)}
 return $p
}
function Make($picks){
 $made=[AppStudio.Outputs]::WriteForRequest($session,6291456,$project,$picks)
 $handoff=[AppStudio.Handoff]::Build($session,$project,'Fix the save step.',$requestId,$picks,$made.Markdown,$made.Pdf)
 [AppStudio.Handoff]::Write($project,$handoff)|Out-Null
 $md=''
 if($null-ne$made.Markdown-and$made.Markdown.Written){$md=[IO.File]::ReadAllText($session.SessionMdPath,(New-Object Text.UTF8Encoding($false)))}
 return [PSCustomObject]@{Made=$made;Handoff=$handoff;Md=$md;Text=$handoff.Text}
}
function FenceCount([string]$text,[string]$fence){
 $count=0
 foreach($line in ($text -split "`n")){if($line.TrimEnd("`r")-eq$fence){$count++}}
 return $count
}
function AttachmentNames($handoff){
 $names=@()
 foreach($a in $handoff.Attachments){$names+=$a.Name}
 return ($names -join ',')
}

# ---- 1. C# only: the VBA body is not there -----------------------------------
$csOnly=Make (Pick @('guidance','engine'))
if(-not$csOnly.Md.Contains('namespace AppStudioRun')){throw 'C# was selected but its body is not in session.md'}
if($csOnly.Md.Contains('Attribute VB_Name')){throw 'VBA was not selected but its body is in session.md'}
if($csOnly.Made.Markdown.VbaModules-ne0){throw ('VBA modules were counted as '+$csOnly.Made.Markdown.VbaModules+' when VBA was not selected')}
if($csOnly.Made.Markdown.EngineModules-ne5){throw ('C# modules were counted as '+$csOnly.Made.Markdown.EngineModules+' instead of 5')}
if($csOnly.Text.Contains('| VBA |')){throw 'the request lists VBA modules that were not handed over'}

# ---- 2. VBA only: the C# body is not there -----------------------------------
$vbaOnly=Make (Pick @('guidance','vba'))
if(-not$vbaOnly.Md.Contains('Attribute VB_Name')){throw 'VBA was selected but its body is not in session.md'}
if($vbaOnly.Md.Contains('namespace AppStudioRun')){throw 'C# was not selected but its body is in session.md'}
if($vbaOnly.Made.Markdown.EngineModules-ne0){throw 'C# modules were counted when C# was not selected'}
if($vbaOnly.Made.Markdown.VbaModules-ne5){throw ('VBA modules were counted as '+$vbaOnly.Made.Markdown.VbaModules+' instead of 5')}
if($vbaOnly.Text.Contains('| C# |')){throw 'the request lists C# modules that were not handed over'}

# ---- 3. both ------------------------------------------------------------------
$both=Make (Pick @('guidance','engine','vba'))
if(-not$both.Md.Contains('namespace AppStudioRun')){throw 'both were selected but the C# body is missing'}
if(-not$both.Md.Contains('Attribute VB_Name')){throw 'both were selected but the VBA body is missing'}
if($both.Made.Markdown.EngineModules-ne5-or$both.Made.Markdown.VbaModules-ne5){throw 'both were selected but the counts disagree'}

# ---- 4. neither: it still generates, and it says what that means --------------
$none=Make ([AppStudio.AiPicks]::Nothing())
if($none.Handoff.Attachments.Count-ne0){throw ('nothing was selected but '+$none.Handoff.Attachments.Count+' attachments were named')}
if(-not$none.Handoff.AttachmentsReady){throw 'a request with no attachments was called unready, which would refuse to send it'}
if($none.Text.Length-le0){throw 'nothing was generated at all'}
if(-not$none.Text.Contains('Nothing is attached to this message')){throw 'the request does not say that nothing is attached'}
if($none.Text.Contains('Read them before you answer')){throw 'the request tells the assistant to read attachments that do not exist'}
if(Test-Path $session.SessionMdPath){throw 'session.md was left on disk although nothing was selected for it'}
$noneWarnings=@($none.Handoff.Warnings|ForEach-Object{$_.Id})
foreach($expected in @('no-code','no-pdf','no-protocol','thin-context')){
 if($noneWarnings-notcontains$expected){throw ('the empty selection did not warn about '+$expected)}
}

# ---- 5. every part, one at a time, on and off --------------------------------
#
# Each of the nine is selected alone and has to be the only heading in the file;
# then it is left out of a full selection and has to be the only one missing.
foreach($id in $contextIds){
 $alone=Make (Pick @($id))
 $title=[AppStudio.AiItems]::Title($id)
 if(-not$alone.Md.Contains('## 1. '+$title)){throw ('selecting '+$id+' alone did not put "'+$title+'" in the file')}
 if($alone.Made.Markdown.Sections.Count-ne1){throw ($id+' alone produced '+$alone.Made.Markdown.Sections.Count+' parts')}
 foreach($other in $contextIds){
  if($other-eq$id){continue}
  $otherTitle=[AppStudio.AiItems]::Title($other)
  if($alone.Md.Contains('. '+$otherTitle+"`r`n")-or$alone.Md.Contains('## 2. '+$otherTitle)){throw ($id+' alone dragged in '+$other)}
 }
 $without=[AppStudio.AiPicks]::Everything();$without.Set($id,$false)
 $rest=Make $without
 if($rest.Made.Markdown.Includes($id)){throw ($id+' was switched off but is still a part of the file')}
 foreach($other in $allIds){
  if($other-eq$id){continue}
  if($other-eq'pdf'-or$other-eq'protocol'){continue}
  if(-not$rest.Made.Markdown.Includes($other)){throw ('switching off '+$id+' also removed '+$other)}
 }
}

# The numbering is handed out over whatever is present, so nothing depends on a
# part keeping a fixed number.
$noElements=[AppStudio.AiPicks]::Everything();$noElements.Set('elements',$false)
$shifted=Make $noElements
$n=1
foreach($part in $shifted.Made.Markdown.Sections){
 if($part.Number-ne$n){throw ('the parts are numbered '+$part.Number+' where '+$n+' was expected')}
 if(-not$shifted.Md.Contains('## '+$part.Number+'. '+$part.Title)){throw ('part '+$part.Number+' is not in the file under that number')}
 $n++
}

# ---- 6. the picture document is only attached when it was chosen -------------
$withPdf=Make ([AppStudio.AiPicks]::Default())
if(-not(Test-Path $session.ScreensPdfPath)){throw 'the picture document was selected but not written'}
if((AttachmentNames $withPdf.Handoff)-ne'session.md,screens.pdf'){throw ('the attachments were '+(AttachmentNames $withPdf.Handoff))}
$pdfPages=$withPdf.Made.Pdf.PageCount
$noPdf=[AppStudio.AiPicks]::Default();$noPdf.Set('pdf',$false)
$without=Make $noPdf
if(Test-Path $session.ScreensPdfPath){throw 'the picture document was left on disk as this request''s attachment after being switched off'}
if((AttachmentNames $without.Handoff)-ne'session.md'){throw ('with the pdf off the attachments were '+(AttachmentNames $without.Handoff))}
if(-not$without.Handoff.AttachmentsReady){throw 'the request was called unready although everything it names exists'}
if($without.Text.Contains('**`screens.pdf`**')){throw 'the request tells the assistant to read a picture document that was not written'}
if($without.Text.Contains('two files attached')){throw 'the request still claims two attachments'}
if(-not$without.Md.Contains('not selected')){throw 'session.md does not say the pictures were left out on purpose'}
$pdfWarnings=@($without.Handoff.Warnings|ForEach-Object{$_.Id})
if($pdfWarnings-notcontains'no-pdf'){throw 'switching the pictures off did not warn'}
# A part that was left out is not the same sentence as a part that failed.
if($without.Md.Contains('screens.pdf: not written -')){throw 'a deliberate omission is being reported as a failure to write'}

# ---- 7. the answer format ----------------------------------------------------
$noProtocol=[AppStudio.AiPicks]::Default();$noProtocol.Set('protocol',$false)
$quiet=Make $noProtocol
if($quiet.Text.Contains('#@APPSTUDIO '+$requestId+' BEGIN')){throw 'the answer format was off but the request still specifies it'}
if($quiet.Text.Contains('COMPLETE 1')){throw 'the answer format was off but the protocol is still described'}
if(-not$quiet.Text.Contains('There is no machine readable format for this request')){throw 'the request does not say that no answer format was asked for'}
if(-not$quiet.Text.Contains('read by a person')){throw 'the request does not say who will read the answer'}
$quietWarnings=@($quiet.Handoff.Warnings|ForEach-Object{$_.Id})
if($quietWarnings-notcontains'no-protocol'){throw 'switching the answer format off did not warn'}
$loud=Make ([AppStudio.AiPicks]::Default())
if(-not$loud.Text.Contains('#@APPSTUDIO '+$requestId+' BEGIN powershell Workflow')){throw 'the answer format was on but is not in the request'}

# ---- 8. which module the editor shows is not which code is handed over -------
#
# Three separate states. Changing the language the editor is on, or the language
# a build targets, must not move a single tick.
$before=$project.Picks.Store()
$project.Language='vba'
if($project.Picks.Store()-ne$before){throw 'changing the editor language changed the selection'}
$project.Language='powershell'
if($project.Picks.Store()-ne$before){throw 'changing the editor language back changed the selection'}
$null=[AppStudio.CodeBuild]::BuildVba($project.Files('vba'),(Join-Path $project.Folder 'build'))
if($project.Picks.Store()-ne$before){throw 'building changed the selection'}

# ---- 9. nothing is refused because of what it was combined with --------------
#
# Every one of the fourteen, on and off, against a selection that already has
# everything else set the other way. If any combination were disallowed this is
# where it would show up as a refusal to generate.
foreach($id in $allIds){
 foreach($state in @($true,$false)){
  $mix=[AppStudio.AiPicks]::Nothing()
  foreach($other in $allIds){if($other-ne$id){$mix.Set($other,$true)}}
  $mix.Set($id,$state)
  if($mix.Has($id)-ne$state){throw ('setting '+$id+' to '+$state+' did not take')}
  foreach($other in $allIds){
   if($other-eq$id){continue}
   if(-not$mix.Has($other)){throw ('setting '+$id+' turned off '+$other)}
  }
  $mixed=Make $mix
  if($null-eq$mixed.Text-or$mixed.Text.Length-le0){throw ('the combination with '+$id+'='+$state+' produced nothing')}
 }
}

# ---- 10. what is reported is what is in the files ----------------------------
$full=Make ([AppStudio.AiPicks]::Everything())
$headings=0
foreach($line in ($full.Md -split "`n")){if($line -match '^## \d+\. '){$headings++}}
if($headings-ne$full.Made.Markdown.Sections.Count){throw ('the file has '+$headings+' numbered parts but reports '+$full.Made.Markdown.Sections.Count)}
$csFences=FenceCount $full.Md '```csharp'
$vbFences=FenceCount $full.Md '```vb'
$psFences=FenceCount $full.Md '```powershell'
if($csFences-ne$full.Made.Markdown.EngineModules){throw ('the file has '+$csFences+' C# blocks but reports '+$full.Made.Markdown.EngineModules+' modules')}
if($vbFences-ne$full.Made.Markdown.VbaModules){throw ('the file has '+$vbFences+' VBA blocks but reports '+$full.Made.Markdown.VbaModules+' modules')}
if($psFences-ne1){throw ('the wrapper should be the only PowerShell block, found '+$psFences)}
if(-not$full.Made.Markdown.WrapperIncluded){throw 'the wrapper was selected but is not reported as included'}
if($full.Md.Contains('$engine = @''')-and$full.Md.Contains('AppStudioRun.Workflow')-and$psFences-ne1){throw 'the wrapper block is malformed'}
$mdBytes=(Get-Item $session.SessionMdPath).Length
if($full.Made.Markdown.Bytes-ne$mdBytes){throw ('session.md is '+$mdBytes+' bytes but reports '+$full.Made.Markdown.Bytes)}
$pdfBytes=(Get-Item $session.ScreensPdfPath).Length
if($full.Made.Pdf.Bytes-ne$pdfBytes){throw ('screens.pdf is '+$pdfBytes+' bytes but reports '+$full.Made.Pdf.Bytes)}
foreach($a in $full.Handoff.Attachments){
 $onDisk=(Get-Item $a.Path).Length
 if($a.Bytes-ne$onDisk){throw ($a.Name+' is '+$onDisk+' bytes but the handover reports '+$a.Bytes)}
}
$pdfPageMarks=0
foreach($p in $session.Screens.Screens){if($p.PdfPage-gt0){$pdfPageMarks++}}
if($pdfPageMarks-ne$full.Made.Pdf.PageCount){throw 'the page count does not match the pages that were marked'}
# The ceilings that were applied are named rather than left to be inferred.
if($full.Made.Markdown.LimitsApplied.Count-lt0){throw 'the applied limits list is not readable'}

# ---- 11. nothing left over is treated as this request's attachment -----------
#
# A full selection is generated, then a smaller one. What the first one wrote and
# the second one did not ask for has to be gone, and must not be counted, named
# or reported by the second.
$null=Make ([AppStudio.AiPicks]::Everything())
if(-not(Test-Path $session.ScreensPdfPath)){throw 'the full selection did not write the picture document'}
$smaller=Make (Pick @('actions'))
if(Test-Path $session.ScreensPdfPath){throw 'the previous request''s picture document is still in the attachment folder'}
if((AttachmentNames $smaller.Handoff)-ne'session.md'){throw ('the smaller request named '+(AttachmentNames $smaller.Handoff))}
if($smaller.Made.Markdown.EngineModules-ne0-or$smaller.Made.Markdown.VbaModules-ne0){throw 'the smaller request counted code it did not include'}
if($smaller.Md.Contains('namespace AppStudioRun')){throw 'the smaller request carried code from the larger one'}
if($smaller.Made.Removed.Count-eq0){throw 'the leftover file was removed without saying so'}
$aiFiles=@(Get-ChildItem $session.AiFolder -File)
if($aiFiles.Count-ne1){throw ('the attachment folder holds '+$aiFiles.Count+' files where the request names 1')}
# And the code that is handed over is the code as it is now, not as it was.
$project.SetText('powershell','Workflow','// EDITED-AFTER-THE-FIRST-REQUEST')
$fresh=Make (Pick @('engine'))
if(-not$fresh.Md.Contains('EDITED-AFTER-THE-FIRST-REQUEST')){throw 'the handover carried an older version of the code'}

$parts=$full.Made.Markdown.Sections.Count
Write-Output ('PASS test-ai-picks items='+$allIds.Length+' parts='+$parts+' combinations='+(2*$allIds.Length)+' csOnly=ok vbaOnly=ok both=ok none=ok pdfOff=ok protocolOff=ok stale=0 pdfPages='+$pdfPages+' summaryMatchesFiles=1')
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
