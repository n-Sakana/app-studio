$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-intake-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
[AppStudio.Messages]::Init($root)
try{

$id=[AppStudio.Handoff]::NewRequestId()
$other=[AppStudio.Handoff]::NewRequestId()
$M='#@APPSTUDIO'
function J([string[]]$lines){ $lines -join "`r`n" }
function Sent([string]$requestId,[string]$rest){ $M+' '+$requestId+' '+$rest }

# ---- the answer that is accepted ----
$good=J @(
 (Sent $id 'SUMMARY BEGIN'),'made the wait explicit',(Sent $id 'SUMMARY END'),
 (Sent $id 'BEGIN powershell RecordedProcedure'),"Write-Output 'hi'","WaitIdle -BudgetMs 2500",(Sent $id 'END powershell RecordedProcedure'),
 (Sent $id 'COMPLETE 1'))
$r=[AppStudio.Intake]::Parse($good,$id)
if(-not$r.Ok){throw ('a well formed answer was refused: '+$r.Reason)}
if($r.Files.Count-ne1){throw 'the well formed answer did not yield one file'}
if($r.Files[0].Language-ne'powershell'){throw 'the language was read wrongly'}
if($r.Files[0].Name-ne'RecordedProcedure'){throw 'the name was read wrongly'}
if($r.Files[0].Text-notmatch"Write-Output 'hi'"){throw 'the body was not taken verbatim'}
if($r.Files[0].Text.Contains($M)){throw 'a protocol line ended up inside the code'}
if($r.Summary-ne'made the wait explicit'){throw 'the summary was lost'}

# Both languages in one paste, sorted out into two files.
$two=J @(
 (Sent $id 'BEGIN powershell RecordedProcedure'),'Write-Output 1',(Sent $id 'END powershell RecordedProcedure'),
 (Sent $id 'BEGIN vba RecordedProcedure'),'Public Sub RunRecordedProcedure()','End Sub',(Sent $id 'END vba RecordedProcedure'),
 (Sent $id 'COMPLETE 2'))
$r=[AppStudio.Intake]::Parse($two,$id)
if(-not$r.Ok){throw ('two files in one answer were refused: '+$r.Reason)}
if($r.Files.Count-ne2){throw 'the two files were not separated'}
if($r.Files[0].Language-eq$r.Files[1].Language){throw 'the two files were not told apart by language'}

# ---- decoration a chat client added ----
$decorated=J @(
 ('> `'+(Sent $id 'BEGIN powershell RecordedProcedure')+'`'),
 "Write-Output 'kept &amp; verbatim'",
 ('<code>'+(Sent $id 'END powershell RecordedProcedure')+'</code>'),
 ('- '+(Sent $id 'COMPLETE 1')))
$r=[AppStudio.Intake]::Parse($decorated,$id)
if(-not$r.Ok){throw ('an answer wrapped in chat decoration was refused: '+$r.Reason)}
if($r.Files[0].Text-ne"Write-Output 'kept &amp; verbatim'"){throw 'the body was altered while stripping decoration from the protocol lines'}

# ---- every way an answer can fail to be one ----
$cases=@(
 @('empty',''),
 @('empty',"   `r`n  "),
 @('noSentinel',"here is your code:`r`nWrite-Output 1"),
 @('otherRequest',(J @((Sent $other 'BEGIN powershell RecordedProcedure'),'x',(Sent $other 'END powershell RecordedProcedure'),(Sent $other 'COMPLETE 1')))),
 @('truncated',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'x'))),
 @('truncated',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure')))),
 @('truncated',(J @((Sent $id 'SUMMARY BEGIN'),'why'))),
 @('mismatch',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 2')))),
 @('mismatch',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END vba RecordedProcedure'),(Sent $id 'COMPLETE 1')))),
 @('duplicate',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'BEGIN powershell RecordedProcedure'),'y',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 2')))),
 @('unknownLanguage',(J @((Sent $id 'BEGIN python RecordedProcedure'),'x',(Sent $id 'END python RecordedProcedure'),(Sent $id 'COMPLETE 1')))),
 @('badName',(J @((Sent $id 'BEGIN powershell 9bad-name'),'x',(Sent $id 'END powershell 9bad-name'),(Sent $id 'COMPLETE 1')))),
 @('emptyFile',(J @((Sent $id 'BEGIN powershell RecordedProcedure'),'   ',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 1')))),
 @('noChangeVerdict',(J @((Sent $id 'SUMMARY BEGIN'),'why',(Sent $id 'SUMMARY END'),(Sent $id 'NOCHANGE'),(Sent $id 'COMPLETE 0')))),
 @('noChangeVerdict',(J @((Sent $id 'SUMMARY BEGIN'),'why',(Sent $id 'SUMMARY END'),(Sent $id 'NOCHANGE MAYBE'),(Sent $id 'COMPLETE 0')))),
 @('noChangeReason',(J @((Sent $id 'SUMMARY BEGIN'),'  ',(Sent $id 'SUMMARY END'),(Sent $id 'NOCHANGE UNCLEAR'),(Sent $id 'COMPLETE 0')))),
 @('noChangeContradiction',(J @((Sent $id 'SUMMARY BEGIN'),'why',(Sent $id 'SUMMARY END'),(Sent $id 'NOCHANGE UNNECESSARY'),(Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 1')))),
 @('questionNotAllowed',(J @((Sent $id 'DECISION which one'),(Sent $id 'COMPLETE 0')))),
 @('partShape',(J @((Sent $id 'PART 03 OF 03'),(Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 1')))),
 @('partShape',(J @((Sent $id 'PART 00 03'),(Sent $id 'BEGIN powershell RecordedProcedure'),'x',(Sent $id 'END powershell RecordedProcedure'),(Sent $id 'COMPLETE 1'))))
)
foreach($case in $cases){
 $result=[AppStudio.Intake]::Parse($case[1],$id)
 if($result.Ok){throw ('this should have been refused as '+$case[0]+' but was accepted')}
 if($result.Reason-ne$case[0]){throw ('expected '+$case[0]+' but got '+$result.Reason)}
 if([string]::IsNullOrEmpty($result.Message)){throw ($case[0]+' was refused without saying anything the operator can act on')}
 if($result.Message-eq$result.Reason){throw ($case[0]+' only repeated its own code back')}
}
# The refusal wording is the Japanese one from assets, not the built in fallback.
$check=[AppStudio.Intake]::Parse('',$id)
if($check.Message-notmatch'[^\x00-\x7F]'){throw 'the refusal message did not come from assets/messages'}

# A refusal is a result, not a failure, and it is read as one.
$refusal=J @((Sent $id 'SUMMARY BEGIN'),'the code already waits for the window',(Sent $id 'SUMMARY END'),(Sent $id 'NOCHANGE UNNECESSARY'),(Sent $id 'COMPLETE 0'))
$r=[AppStudio.Intake]::Parse($refusal,$id)
if(-not$r.Ok){throw ('a well formed refusal was refused: '+$r.Reason)}
if($r.NoChange-ne'UNNECESSARY'){throw 'the refusal verdict was lost'}
if($r.Files.Count-ne0){throw 'a refusal produced files'}

# ---- one file per answer ----
$parts=New-Object AppStudio.IntakeParts
function Part([int]$index,[string]$name,[string]$body){
 J @((Sent $id ('PART '+('{0:00}' -f $index)+' OF 03')),(Sent $id ('BEGIN powershell '+$name)),$body,(Sent $id ('END powershell '+$name)),(Sent $id 'COMPLETE 1'))
}
$added=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 0 'FileA' 'a'),$id))
if(-not$added.Ok){throw ('the first part was refused: '+$added.Reason)}
if($parts.Complete){throw 'one part of three was treated as the whole answer'}
if($parts.MissingText()-ne'2, 3'){throw ('the missing parts were listed as '+$parts.MissingText())}
$added=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 2 'FileC' 'c'),$id))
if(-not$added.Ok){throw 'a part arriving out of order was refused'}
if($parts.MissingText()-ne'2'){throw ('out of order arrival confused the missing list: '+$parts.MissingText())}
# The same part again, saying exactly the same thing, is not a contradiction.
$again=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 2 'FileC' 'c'),$id))
if(-not$again.Ok){throw 'the same part sent twice was treated as a conflict'}
if($again.Added){throw 'the same part was counted twice'}
$conflict=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 2 'FileC' 'different'),$id))
if($conflict.Ok-or$conflict.Reason-ne'partConflict'){throw ('a changed part under a used number was not caught: '+$conflict.Reason)}
$dup=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 1 'FileA' 'a'),$id))
if($dup.Ok-or$dup.Reason-ne'partDuplicateFile'){throw ('the same file under two numbers was not caught: '+$dup.Reason)}
$totals=J @((Sent $id 'PART 01 OF 04'),(Sent $id 'BEGIN powershell FileB'),'b',(Sent $id 'END powershell FileB'),(Sent $id 'COMPLETE 1'))
$bad=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse($totals,$id))
if($bad.Ok-or$bad.Reason-ne'partTotalMismatch'){throw ('a part claiming a different total was not caught: '+$bad.Reason)}
$missing=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse($good,$id))
if($missing.Ok-or$missing.Reason-ne'partMissing'){throw ('an answer with no part line was not caught: '+$missing.Reason)}
$added=[AppStudio.Intake]::AddPart($parts,[AppStudio.Intake]::Parse((Part 1 'FileB' 'b'),$id))
if(-not$parts.Complete){throw 'the three parts did not add up to a whole answer'}
$merged=[AppStudio.Intake]::Merge($parts)
if(-not$merged.Ok){throw 'the completed parts did not merge'}
if($merged.Files.Count-ne3){throw ('the merge produced '+$merged.Files.Count+' files instead of 3')}
if($merged.Files[0].Name-ne'FileA'-or$merged.Files[1].Name-ne'FileB'-or$merged.Files[2].Name-ne'FileC'){throw 'the merged files are not in part order'}
$early=[AppStudio.Intake]::Merge((New-Object AppStudio.IntakeParts))
if($early.Ok){throw 'an empty collection merged into an answer'}

# ---- what would change, before anything changes ----
$session=[AppStudio.SessionStore]::Create($temp,'record','intake fixture')
$project=[AppStudio.CodeProject]::Open($session)
$project.SetText('powershell','RecordedProcedure',(J @('one','two','three')))
$incoming=New-Object 'System.Collections.Generic.List[AppStudio.IntakeFile]'
$f=New-Object AppStudio.IntakeFile;$f.Language='powershell';$f.Name='RecordedProcedure';$f.Text=(J @('one','changed','three'));$incoming.Add($f)
$f=New-Object AppStudio.IntakeFile;$f.Language='powershell';$f.Name='Helper';$f.Text='new';$incoming.Add($f)
$diffs=[AppStudio.Diff]::Compare($project,$incoming)
if($diffs.Count-ne2){throw 'the comparison did not cover both files'}
if($diffs[0].AddedCount-ne1-or$diffs[0].RemovedCount-ne1){throw ('one changed line reported as +'+$diffs[0].AddedCount+' -'+$diffs[0].RemovedCount)}
if(-not$diffs[1].IsNew){throw 'a file that does not exist yet was not reported as new'}
if($diffs[1].RemovedCount-ne0){throw 'a new file reported lines as removed'}
$same=[AppStudio.Diff]::Of('a','a')
if($same.Changed){throw 'identical text was reported as changed'}

Write-Output ('PASS test-intake refusals='+$cases.Count+' parts=3 diffFiles='+$diffs.Count)
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
