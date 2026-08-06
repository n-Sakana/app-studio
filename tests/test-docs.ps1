$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# The documents are the product contract. A term listed here is one a reader has
# to be able to find, so removing a guarantee from the code cannot quietly leave
# the promise standing in the documents, and vice versa.
$requirements=@{
 'SPEC.md'=@(
   'スナップ','録画','再生',
   'session.md','screens.pdf','report.html',
   'UIA-EMPTYTREE','NEEDS-B3','RuntimeId','SetWindowsHookEx',
   'secretInput','recordText','lengthOnly',
   'uia.InvokePattern:notSupported -> win32.BM_CLICK:success',
   'notSupported','win32.classIndex','window.relative',
   'MSAA は取得専用','完全性の証明ではない','app-studio/session/2');
 'DEVELOPMENT.md'=@(
   'C# 5.0','run-all.ps1','schema','test-hang-recovery','run-wp-s.ps1',
   'test-privacy','test-outputs','test-replay','test-gui-e2e',
   'APPSTUDIO_ALLOW_REAL_INPUT','dispatcher','Privacy');
 'ONSITE.md'=@(
   'launch.vbs','EDR/DLP','AutomationId','writeTargets',
   'session.md','screens.pdf','secretInput','appSwitch','keyChord')
}
foreach($name in $requirements.Keys){
 $path=Join-Path $root ('docs\'+$name)
 if(-not(Test-Path $path)){throw ($name+' missing')}
 $text=[IO.File]::ReadAllText($path,(New-Object Text.UTF8Encoding($false)))
 foreach($term in $requirements[$name]){
  if(-not$text.Contains($term)){throw ($name+' missing term '+$term)}
 }
}
$readme=[IO.File]::ReadAllText((Join-Path $root 'README.md'),(New-Object Text.UTF8Encoding($false)))
foreach($term in @('スナップ','録画','再生','session.md','screens.pdf','report.html','APPSTUDIO_ALLOW_REAL_INPUT','CC0 1.0 Universal')){
 if(-not$readme.Contains($term)){throw ('README.md missing term '+$term)}
}
# Nothing may still promise the withdrawn assistant workflow. Saying that a file
# is deliberately not produced is the opposite of promising it, so the check is
# on the names that cannot appear as a denial.
#
# Taking an answer back in used to be on this list. It is not withdrawn any
# more: the code screen reads one paste, checks it against the request it was
# asked for and shows the difference before anything is replaced, so the
# documents are supposed to describe it. The artefact names below never came
# back and stay banned.
foreach($doc in @('README.md','docs\SPEC.md','docs\DEVELOPMENT.md','docs\ONSITE.md')){
 $text=[IO.File]::ReadAllText((Join-Path $root $doc),(New-Object Text.UTF8Encoding($false)))
 foreach($gone in @('pui-plan','案件','調査パック','handoff.txt','MANIFEST.json')){
  if($text.Contains($gone)){throw ($doc+' still promises the withdrawn workflow: '+$gone)}
 }
}
# The intake is only worth having if a bad answer is refused with a reason, so
# the documents have to say so where a reader meets the feature.
foreach($pair in @(@('README.md','request id'),@('docs\SPEC.md','request id'))){
 $text=[IO.File]::ReadAllText((Join-Path $root $pair[0]),(New-Object Text.UTF8Encoding($false)))
 if(-not$text.Contains($pair[1])){throw ($pair[0]+' does not state how a stale or foreign answer is refused')}
}
# What the handover folder contains is the whole contract, so it has to be stated
# where a reader will meet it.
#
# It used to be "exactly two files". It is not that any more: the operator picks
# what goes, so the rule that makes "attach what is in ai/" a complete
# instruction is now that the folder holds what was selected and nothing left
# over. The documents have to say the rule that is true.
foreach($pair in @(
 @('README.md','AI へ渡すのは、選んだものだけ'),
 @('README.md','いま渡すものと常に一致'),
 @('docs\SPEC.md','その依頼のために書き出したファイルだけ'),
 @('docs\SPEC.md','全組合せを許可する'))){
 $text=[IO.File]::ReadAllText((Join-Path $root $pair[0]),(New-Object Text.UTF8Encoding($false)))
 if(-not$text.Contains($pair[1])){throw ($pair[0]+' does not state the handover rule: '+$pair[1])}
}
# And they must not go back to promising the rule that was withdrawn.
foreach($doc in @('README.md','docs\SPEC.md')){
 $text=[IO.File]::ReadAllText((Join-Path $root $doc),(New-Object Text.UTF8Encoding($false)))
 foreach($stale in @('ちょうど2ファイル','常にこの2ファイル','10節に')){
  if($text.Contains($stale)){throw ($doc+' still promises the withdrawn fixed handover: '+$stale)}
 }
}
foreach($path in @('launch.vbs','launch.bat','app-studio.ps1','app-studio-worker.ps1','tests\run-all.ps1','tests\wp-s\run-wp-s.ps1','tests\build-fixtures.ps1')){
 if(-not(Test-Path (Join-Path $root $path))){throw ('documented path missing: '+$path)}
}
foreach($path in @('README.md','LICENSE')){if(-not(Test-Path (Join-Path $root $path))){throw ('required file missing: '+$path)}}
$license=[IO.File]::ReadAllText((Join-Path $root 'LICENSE'),(New-Object Text.UTF8Encoding($false)))
if(-not$license.Contains('CC0 1.0 Universal')){throw 'LICENSE is not CC0 1.0'}
# Every source file the map names has to exist, and every source file has to be
# in the map: a module nobody documented is a module nobody maintains.
$development=[IO.File]::ReadAllText((Join-Path $root 'docs\DEVELOPMENT.md'),(New-Object Text.UTF8Encoding($false)))
$sources=@(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs')
foreach($file in $sources){
 $stem=[IO.Path]::GetFileNameWithoutExtension($file.Name)
 if(-not$development.Contains('`'+$stem+'`')){throw ('DEVELOPMENT.md source map is missing '+$stem)}
}
# And the map may not describe a module the product has stopped using. Being
# listed there reads as "this is part of the product", so a file kept alive only
# by a test would be documented as if it shipped. The two entry points are
# reached from the launcher scripts rather than from any other C# file.
$text=@{}
foreach($file in $sources){$text[$file.Name]=[IO.File]::ReadAllText($file.FullName)}
$launcher=(Get-Content (Join-Path $root 'app-studio.ps1') -Raw)+(Get-Content (Join-Path $root 'app-studio-worker.ps1') -Raw)
$typePattern='(?m)^\s*public\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|enum|interface)\s+([A-Za-z0-9_]+)'
$orphans=@()
foreach($file in $sources){
 $types=@([regex]::Matches($text[$file.Name],$typePattern)|ForEach-Object{$_.Groups[1].Value}|Sort-Object -Unique)
 if($types.Count-eq0){continue}
 $reachable=$false
 foreach($name in $types){
  $needle='\b'+[regex]::Escape($name)+'\b'
  if($launcher-match$needle){$reachable=$true;break}
  foreach($other in $sources){
   if($other.Name-eq$file.Name){continue}
   if($text[$other.Name]-match$needle){$reachable=$true;break}
  }
  if($reachable){break}
 }
 if(-not$reachable){$orphans+=$file.Name}
}
if($orphans.Count-gt0){throw ('these modules are in the source map but nothing else in the product uses them: '+($orphans-join', '))}
Write-Output ('PASS test-docs files=4 sourceMap='+(@(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs')).Count+' orphanModules=0 withdrawnPromises=0 license=CC0-1.0')
