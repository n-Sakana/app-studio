$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-diagnostics-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{
# A code that was raised anywhere has to survive into every place a reader
# looks. Losing one in a projection is how a degraded acquisition starts
# looking like a clean one.
$session=[AppStudio.SessionStore]::Create($temp,'snap','diagnostics fixture')
$codes=@('UIA-TIMEOUT','WIN32-HUNG','CAP-BLACK','SCAN-MAXNODES')
foreach($code in $codes){
 $coverage=New-Object AppStudio.ScanCoverage;$coverage.Provider='uia';$coverage.State='partial';$coverage.NodeCount=1;$coverage.Truncated=$true
 $coverage.AddReason($code,('Injected '+$code+' detail.'))
 $session.Coverage.Add($coverage)
 $session.AddLimit('['+$coverage.Provider+'] '+$code+': Injected '+$code+' detail.')
}
$session.AddDiagnostic('TOOL-NOTE: an injected tool diagnostic.')

$screen=New-Object AppStudio.ScreenRecord
$screen.ScanId='sc-0001';$screen.ScreenId='S1';$screen.Title='Diagnostics fixture';$screen.ClassName='FixtureWindow';$screen.Hwnd=42
$screen.Rect=New-Object AppStudio.RectValue;$screen.Rect.Width=100;$screen.Rect.Height=100
$screen.ShotProblem='SHOT-NORECT: no usable rectangle.'
$session.Screens.Screens.Add($screen)
$session.AddLimit('Screen S1 has no picture: '+$screen.ShotProblem)
[AppStudio.SessionStore]::WriteMeta($session)

$outputs=[AppStudio.Outputs]::WriteAll($session,(4*1024*1024))
if(-not$outputs.Markdown.Written-or-not$outputs.Report.Written){throw ('a text output was not written: '+$outputs.Problems)}
# There is no picture at all, so the picture document cannot exist. That has to
# be stated rather than silently produce an empty attachment.
if($outputs.Pdf.Written){throw 'a picture document was written although no screen has a picture'}
if($outputs.Pdf.Problem-notmatch 'PDF-NOSCREEN'){throw 'the missing picture document had no stated reason'}

$markdown=[IO.File]::ReadAllText($session.SessionMdPath)
$html=[IO.File]::ReadAllText($session.ReportPath)
$meta=[IO.File]::ReadAllText((Join-Path $session.Folder 'meta.json'))
foreach($code in $codes){
 foreach($projection in @(@('session.md',$markdown),@('report.html',$html),@('meta.json',$meta))){
  if(-not$projection[1].Contains($code)){throw ($code+' is missing from '+$projection[0])}
 }
}
foreach($projection in @(@('session.md',$markdown),@('report.html',$html))){
 if(-not$projection[1].Contains('SHOT-NORECT')){throw ('the missing picture reason is absent from '+$projection[0])}
 if(-not$projection[1].Contains('PDF-NOSCREEN')){throw ('the missing picture document reason is absent from '+$projection[0])}
}
if(-not$html.Contains('TOOL-NOTE')){throw 'a tool diagnostic is absent from the report'}
if(-not$markdown.Contains('TOOL-NOTE')){throw 'a tool diagnostic is absent from session.md'}
# The honesty line is printed whether or not the list above is empty.
foreach($projection in @(@('session.md',$markdown),@('report.html',$html))){
 if($projection[1]-notmatch 'not a proof'){throw ($projection[0]+' dropped the completeness caveat')}
}

Write-Output ('PASS test-diagnostics injected='+$codes.Count+' projections=meta+session.md+report.html loss=0 emptyPdf=stated')
}finally{Remove-Item $temp -Recurse -Force}
