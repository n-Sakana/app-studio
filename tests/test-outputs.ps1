$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-outputs-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

# A flat rectangle compresses to almost nothing and would never exercise the
# size budget, so the fixture picture is built to compress the way a real
# screenshot does: many small blocks of varying colour, from a fixed seed so
# every run gets the same file.
function New-Png([string]$path,[int]$w,[int]$h,[int]$seed){
 New-Item -ItemType Directory (Split-Path $path) -Force|Out-Null
 $bitmap=New-Object Drawing.Bitmap($w,$h)
 try{
  $g=[Drawing.Graphics]::FromImage($bitmap)
  try{
   $g.Clear([Drawing.Color]::White)
   $state=[int]$seed
   $brush=New-Object Drawing.SolidBrush([Drawing.Color]::Black)
   try{
    for($y=0;$y -lt $h;$y+=6){
     for($x=0;$x -lt $w;$x+=6){
      $state=($state*1103515245+12345)-band 0x3FFFFFFF
      $r=(($state-shr 5)-band 0xFF);$gg=(($state-shr 13)-band 0xFF);$b=(($state-shr 21)-band 0xFF)
      $brush.Color=[Drawing.Color]::FromArgb($r,$gg,$b)
      $g.FillRectangle($brush,$x,$y,6,6)
     }
    }
   }finally{$brush.Dispose()}
  }finally{$g.Dispose()}
  $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
 }finally{$bitmap.Dispose()}
}

function New-Screen($session,[string]$id,[string]$title,[string]$shot,[string]$note){
 $screen=New-Object AppStudio.ScreenRecord
 $screen.ScanId='sc-0001';$screen.ScreenId=$id;$screen.Hwnd=1000+[int]($id.Substring(1));$screen.Title=$title;$screen.ClassName='FixtureWindow';$screen.NodeCount=1;$screen.Note=$note
 $screen.Rect=New-Object AppStudio.RectValue;$screen.Rect.Width=900;$screen.Rect.Height=700
 if($shot){$screen.ShotFile=$shot;$screen.CapturedAt=[DateTimeOffset]::Now;$screen.CaptureMethod='BitBlt';$screen.Sha256=(Get-FileHash $shot -Algorithm SHA256).Hash}
 else{$screen.ShotProblem='SHOT-NORECT: this window had no usable rectangle.'}
 $session.Screens.Screens.Add($screen)
 return $screen
}

$session=[AppStudio.SessionStore]::Create($temp,'record','outputs fixture')
$session.Environment=(New-Object AppStudio.JsonObject).Add('os',(New-Object AppStudio.JsonObject).Add('caption','Fixture OS'))
$session.Register([Diagnostics.Process]::GetCurrentProcess().Id)|Out-Null
$session.AddLimit('[uia] SCAN-TIMEOUT: the walk stopped after its allowance.')

for($i=1;$i -le 4;$i++){
 $shot=Join-Path $session.ShotsFolder ('screen-sc-0001-S'+$i+'.png')
 New-Png $shot 900 700 (7919*$i)
 $screen=New-Screen $session ('S'+$i) ('Fixture window '+$i) $shot ('keyframe '+$i)
 $screen.ComponentIds.Add('E'+($i-1))
}
$null=New-Screen $session 'S5' 'Window with no picture' $null $null

for($i=0;$i -lt 4;$i++){
 $node=New-Object AppStudio.ScanNode
 $node.NodeId=$i;$node.ScreenId=('S'+($i+1));$node.Name=('Field '+$i);$node.AutomationId=('Field'+$i);$node.ControlType='Edit';$node.ClassName='Edit'
 $node.CtrlId=(1000+$i);$node.Path=('Window > Edit "Field '+$i+'"');$node.Patterns=@('Value');$node.Visible=$true;$node.Enabled=$true
 $node.Rect=New-Object AppStudio.RectValue;$node.Rect.X=10;$node.Rect.Y=(20*$i);$node.Rect.Width=100;$node.Rect.Height=24
 $node.AddSource('uia');$node.AddSource('win32')
 $session.Elements.Add($node)
}
# A value that has to survive as text, and a name with characters that would
# break a table or an HTML document if they were not handled.
$session.Elements[1].Name='Awkward | name <b>with markup</b> & a pipe'

$coverage=New-Object AppStudio.ScanCoverage;$coverage.Provider='uia';$coverage.State='partial';$coverage.NodeCount=4;$coverage.DurationMs=120;$coverage.Truncated=$true
$coverage.AddReason('SCAN-TIMEOUT','the walk stopped after its allowance')
$session.Coverage.Add($coverage)

$window=New-Object AppStudio.RectValue;$window.Width=900;$window.Height=700
$siblings=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';foreach($n in $session.Elements){$siblings.Add($n)}
for($i=0;$i -lt 3;$i++){
 $step=New-Object AppStudio.StepRecord
 $step.Index=($i+1);$step.At=[DateTimeOffset]::Now;$step.OffsetMs=(1000*$i);$step.Kind='click';$step.AppKey='P1';$step.AppName='Fixture'
 $step.WindowTitle=('Fixture window '+($i+1));$step.WindowClass='FixtureWindow';$step.ScreenBefore=('S'+($i+1));$step.ScreenAfter=('S'+($i+2))
 $step.ElementLabel=('Edit "Field '+$i+'"');$step.Name=('Field '+$i);$step.AutomationId=('Field'+$i);$step.ControlType='Edit';$step.ClassName='Edit';$step.CtrlId=(1000+$i)
 $step.Rect=$session.Elements[$i].Rect;$step.TreePath=$session.Elements[$i].Path;$step.Sources.Add('uia')
 $step.Locators=[AppStudio.LocatorBuilder]::Build($session.Elements[$i],$window,$siblings)
 $step.Confidence=[AppStudio.LocatorBuilder]::BestConfidence($step.Locators)
 $step.EffectSummary='the window title changed'
 $outcome=New-Object AppStudio.ReplayOutcome
 $outcome.At=[DateTimeOffset]::Now;$outcome.State='done';$outcome.ResolvedBy='uia.automationId';$outcome.MatchCount=1;$outcome.Reason='Carried out through win32.BM_CLICK.'
 $a=New-Object AppStudio.RouteAttempt;$a.Route='uia';$a.Method='uia.InvokePattern';$a.Outcome='notSupported';$a.DurationMs=31
 $b=New-Object AppStudio.RouteAttempt;$b.Route='win32';$b.Method='win32.BM_CLICK';$b.Outcome='success';$b.DurationMs=88;$b.Effect='window title changed'
 $outcome.Attempts.Add($a);$outcome.Attempts.Add($b)
 $step.LastReplay=$outcome
 $session.Steps.Add($step)
}
$secret=New-Object AppStudio.StepRecord
$secret.Index=4;$secret.At=[DateTimeOffset]::Now;$secret.Kind='secretInput';$secret.AppName='Fixture';$secret.WindowTitle='Fixture window 1';$secret.WindowClass='FixtureWindow'
$secret.ElementLabel='Edit "Password"';$secret.Name='Password';$secret.ScreenBefore='S1'
[AppStudio.Privacy]::ApplyValue($secret,'recordText','never-write-me-4c1a',$true,'isPassword')
$session.Steps.Add($secret)
$session.EndedAt=[DateTimeOffset]::Now
[AppStudio.SessionStore]::WriteMeta($session)

# ===== 1. a generous budget: everything fits, nothing is dropped ==========
$outputs=[AppStudio.Outputs]::WriteAll($session,(64*1024*1024))
if(-not$outputs.Complete){throw ('outputs incomplete: '+$outputs.Problems)}
foreach($path in @($session.ReportPath,$session.SessionMdPath,$session.ScreensPdfPath)){
 if(-not(Test-Path $path)){throw ('output missing: '+$path)}
}
# The assistant handover is exactly two files and nothing else.
$aiFiles=@(Get-ChildItem $session.AiFolder -File)
if($aiFiles.Count-ne2){throw ('the assistant folder holds '+$aiFiles.Count+' files instead of 2: '+(($aiFiles|ForEach-Object{$_.Name})-join','))}
if(($aiFiles|ForEach-Object{$_.Name}|Sort-Object)-join','-ne'screens.pdf,session.md'){throw 'the two assistant files are not session.md and screens.pdf'}
if($outputs.Pdf.PageCount-ne4){throw ('page count mismatch: '+$outputs.Pdf.PageCount)}
if($outputs.Pdf.OmittedScreens.Count-ne0){throw 'a screen was dropped although the budget was generous'}

# ===== 2. screens.pdf is a real document =================================
$pdfBytes=[IO.File]::ReadAllBytes($session.ScreensPdfPath)
$latin=[Text.Encoding]::GetEncoding(28591)
$pdfText=$latin.GetString($pdfBytes)
if(-not$pdfText.StartsWith('%PDF-1.4')){throw 'the pdf header is wrong'}
if(-not$pdfText.TrimEnd().EndsWith('%%EOF')){throw 'the pdf is truncated'}
if(($pdfText.Split([string[]]@('/Type /Page'),[StringSplitOptions]::None).Count-1)-lt 4){throw 'the pdf does not carry one page per screen'}
$startxref=[regex]::Match($pdfText,'startxref\s+(\d+)')
if(-not$startxref.Success){throw 'the pdf has no startxref'}
$xrefOffset=[int]$startxref.Groups[1].Value
if($latin.GetString($pdfBytes,$xrefOffset,4)-ne'xref'){throw 'startxref does not point at the table'}
$entries=[regex]::Matches($pdfText.Substring($xrefOffset),'(\d{10}) 00000 n')
if($entries.Count-lt 8){throw ('the cross reference table is too small: '+$entries.Count)}
foreach($entry in $entries){
 $offset=[int]$entry.Groups[1].Value
 if($offset-le 0-or$offset-ge$pdfBytes.Length){throw ('a cross reference offset is outside the file: '+$offset)}
 if($latin.GetString($pdfBytes,$offset,24)-notmatch '^\d+ 0 obj'){throw ('a cross reference offset does not point at an object: '+$offset)}
}
foreach($screenId in @('Screen S1','Screen S2','Screen S3','Screen S4')){
 if(-not$pdfText.Contains($screenId)){throw ('the pdf page does not name its screen: '+$screenId)}
}
if(-not$pdfText.Contains('page 1 of 4')){throw 'the pdf pages are not numbered against the whole set'}
# Lossless at a generous budget, so the picture is stored uncompressed.
if(-not$pdfText.Contains('/FlateDecode')){throw 'the pictures were not stored losslessly at a generous budget'}
if($pdfText.Contains('/DCTDecode')){throw 'the pictures were degraded although the budget allowed lossless'}

# ===== 3. session.md ====================================================
$markdown=[IO.File]::ReadAllText($session.SessionMdPath)
foreach($section in @('## 1. Machine, screens and coordinates','## 2. What was and was not written down','## 3. Applications','## 4. Screens','## 5. Elements','## 6. What the operator did','## 7. Replay results','## 8. Coverage and limits','## 9. Writing an automation from this')){
 if(-not$markdown.Contains($section)){throw ('session.md section missing: '+$section)}
}
if($markdown-match 'data:image|base64,'){throw 'session.md carries an embedded picture'}
if(-not$markdown.Contains('physical screen pixels')){throw 'session.md does not state the coordinate system'}
if(-not$markdown.Contains('uia.InvokePattern:notSupported -> win32.BM_CLICK:success')){throw 'session.md does not carry the route trail'}
if(-not$markdown.Contains('SCAN-TIMEOUT')){throw 'session.md dropped a coverage reason'}
if(-not$markdown.Contains('S5')){throw 'session.md dropped the screen that has no picture'}
if(-not$markdown.Contains('SHOT-NORECT')){throw 'session.md dropped the reason a picture is missing'}
if($markdown.Contains('never-write-me-4c1a')){throw 'a secret reached session.md'}
if(-not$markdown.Contains('\|')){throw 'a pipe inside a value was not escaped, so a table row is broken'}
$tableLines=@($markdown-split "`n"|Where-Object{$_.StartsWith('| E')})
if($tableLines.Count-ne4){throw ('the element table has '+$tableLines.Count+' rows instead of 4')}
foreach($line in $tableLines){ if((($line.ToCharArray()|Where-Object{$_-eq'|'}).Count)-lt 13){throw 'an element row lost a column'} }

# ===== 4. report.html ===================================================
$html=[IO.File]::ReadAllText($session.ReportPath)
if($html-match '(?i)https?://|src="//|href="//|@import|<iframe|<link |fetch\s*\(|XMLHttpRequest'){throw 'the report reaches outside itself'}
if($html-notmatch 'data:image/jpeg;base64,[A-Za-z0-9+/=]{200,}'){throw 'no picture was embedded in the report'}
foreach($id in @('id="summary"','id="screens"','id="steps"','id="elements"','id="replay"','id="limits"')){
 if(-not$html.Contains($id)){throw ('report section missing: '+$id)}
}
if(-not$html.Contains('id="q"')){throw 'the report has no filter box'}
if(-not$html.Contains('<details')){throw 'the report has nothing to fold away'}
if($html.Contains('never-write-me-4c1a')){throw 'a secret reached the report'}
if($html.Contains('Awkward | name <b>with markup</b>')){throw 'markup inside a value was not escaped'}
if(-not$html.Contains('Awkward | name &lt;b&gt;with markup&lt;/b&gt; &amp; a pipe')){throw 'a value with markup was not carried through escaped'}
if(-not$html.Contains('SHOT-NORECT')){throw 'the report dropped the reason a picture is missing'}
if(-not$html.Contains('win32.BM_CLICK')){throw 'the report dropped the route trail'}
if(-not$html.Contains('prefers-color-scheme')){throw 'the report does not follow the reader theme'}
if($outputs.Report.EmbeddedPictures-ne4){throw ('embedded picture count mismatch: '+$outputs.Report.EmbeddedPictures)}

# ===== 5. a budget that forces reduction, then one that forces omission ==
$losslessBytes=(Get-Item $session.ScreensPdfPath).Length
if($losslessBytes-lt(40*1024)){throw ('the fixture pictures are too compressible to test the budget: '+$losslessBytes+' bytes')}

# These fixture screens are flat blocks, which the lossless form stores better
# than a photographic one, so every reduction step makes the file larger. The
# product must notice that, keep the smaller form, and say so - degrading the
# pictures for nothing would be a loss with nothing bought.
$reduced=[AppStudio.Outputs]::WriteAll($session,[int]($losslessBytes*0.95))
if(-not$reduced.Pdf.Written){throw 'no document was written under a reduced budget'}
if($reduced.Pdf.Notes.Count-lt1){throw 'the budget work was not written down'}
if(($reduced.Pdf.Notes-join' ')-notmatch 'produced a larger file'){throw 'a reduction step that made the file bigger was not reported'}
if($reduced.Pdf.Quality-match 'lossy'){throw 'the pictures were degraded even though that made the file larger'}
if($reduced.Pdf.OmittedScreens.Count-lt1){throw 'nothing was left out although reduction could not help'}

# And where a lossy step genuinely helps, it is taken and stated on the page.
$photo=[AppStudio.SessionStore]::Create($temp,'snap','gradient fixture')
$photoShot=Join-Path $photo.ShotsFolder 'screen-sc-0001-S1.png'
New-Item -ItemType Directory $photo.ShotsFolder -Force|Out-Null
$grad=New-Object Drawing.Bitmap(1400,1000)
try{
 $gg=[Drawing.Graphics]::FromImage($grad)
 try{
  $brush=New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.Rectangle(0,0,1400,1000)),[Drawing.Color]::DarkSlateBlue,[Drawing.Color]::Orange,45.0)
  try{$gg.FillRectangle($brush,0,0,1400,1000)}finally{$brush.Dispose()}
 }finally{$gg.Dispose()}
 $grad.Save($photoShot,[Drawing.Imaging.ImageFormat]::Png)
}finally{$grad.Dispose()}
$photoScreen=New-Screen $photo 'S1' 'Gradient window' $photoShot $null
$photoScreen.ComponentIds.Add('E0')
[AppStudio.SessionStore]::WriteMeta($photo)
$photoLossless=[AppStudio.ScreensPdf]::Write($photo,$photo.ScreensPdfPath,(64*1024*1024))
if($photoLossless.Quality-match 'lossy'){throw 'the gradient was degraded at a generous budget'}
$photoLossy=[AppStudio.ScreensPdf]::Write($photo,$photo.ScreensPdfPath,[int]($photoLossless.Bytes/4))
if(-not$photoLossy.Written){throw 'no document was written for the gradient fixture'}
if($photoLossy.Quality-notmatch 'lossy'){throw ('the lossy step was never reached where it helps: '+$photoLossy.Quality)}
if($photoLossy.Bytes-ge$photoLossless.Bytes){throw 'the lossy step did not make the file smaller'}
if($photoLossy.OmittedScreens.Count-ne0){throw 'a page was dropped although reduction was enough'}
$photoText=$latin.GetString([IO.File]::ReadAllBytes($photo.ScreensPdfPath))
if(-not$photoText.Contains('re-encoded at quality')){throw 'the lossy re-encoding was not stated on the page'}
if(-not$photoText.Contains('/DCTDecode')){throw 'the lossy form was not actually used'}

$tight=[AppStudio.Outputs]::WriteAll($session,(16*1024))
if(-not$tight.Pdf.Written){throw 'no document was written under a tight budget'}
if($tight.Pdf.Notes.Count-lt1){throw 'the tight budget was not written down'}
if($tight.Pdf.OmittedScreens.Count-lt1){throw 'nothing was dropped although the budget could not hold four pages'}
$tightMarkdown=[IO.File]::ReadAllText($session.SessionMdPath)
foreach($dropped in $tight.Pdf.OmittedScreens){
 if(-not$tightMarkdown.Contains($dropped)){throw ('session.md does not name the dropped screen '+$dropped)}
}
if($tightMarkdown-notmatch 'Screens left out of'){throw 'session.md does not say that screens were left out'}
$tightHtml=[IO.File]::ReadAllText($session.ReportPath)
if($tightHtml-notmatch 'Left out of'){throw 'the report does not say that screens were left out'}
# What survived is still a valid document with page numbers against the survivors.
$tightText=$latin.GetString([IO.File]::ReadAllBytes($session.ScreensPdfPath))
if(-not$tightText.StartsWith('%PDF-1.4')-or-not$tightText.TrimEnd().EndsWith('%%EOF')){throw 'the reduced document is not a valid pdf'}
if(-not$tightText.Contains('page 1 of ')){throw 'the reduced document lost its page numbering'}

# ===== 6. copying the outputs keeps the two file rule ===================
$copyTarget=Join-Path $temp 'exported'
New-Item -ItemType Directory $copyTarget|Out-Null
$copyProblem=[AppStudio.Outputs]::CopyTo($session,$copyTarget)
if($copyProblem){throw ('the copy failed: '+$copyProblem)}
$copied=Get-ChildItem $copyTarget -Directory
if($copied.Count-ne1){throw 'the copy did not make one folder'}
if(@(Get-ChildItem (Join-Path $copied[0].FullName 'ai') -File).Count-ne2){throw 'the copied assistant folder does not hold exactly two files'}
if(-not(Test-Path (Join-Path $copied[0].FullName 'report.html'))){throw 'the copied report is missing'}

Write-Output ('PASS test-outputs files=report.html+session.md+screens.pdf aiAttachments=2 pdfPages='+$outputs.Pdf.PageCount+' xref=valid mdSections=9 base64InMd=0 htmlExternalRefs=0 escaping=ok budgetLadder=honest(no-worse-step)+lossy-when-it-helps budgetOmissions='+$tight.Pdf.OmittedScreens.Count+' omissionsStated=md+html secretLeak=0')
}finally{Remove-Item $temp -Recurse -Force}
