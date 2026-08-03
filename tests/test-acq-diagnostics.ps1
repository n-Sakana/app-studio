$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;[AppStudio.DpiAwareness]::Enable();$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-acqdiag-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null
try{
 [AppStudio.Probe]::Configure($root,$false);$ready=Join-Path $temp 'ready.json';$process=Start-Process $build.FixtureWin32 -ArgumentList @('--mode','healthy','--ready',$ready) -PassThru;$limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWin32 not ready'};$f=Get-Content $ready -Raw|ConvertFrom-Json
 $rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.list);$snapshot=[AppStudio.Probe]::At(($rect.X+[int]($rect.Width/2)),($rect.Y+[int]($rect.Height/2)),1500)
 if($null-eq$snapshot-or$null-eq$snapshot.Win32){throw 'no snapshot was obtained from the live fixture'}
 # An acquisition code raised against a live window has to reach every place a
 # reader looks, and the live probe itself has to report its own state.
 $session=[AppStudio.SessionStore]::Create($temp,'snap','acquisition diagnostics')
 foreach($code in @('ACQ-RESTART','ACQ-DROPPED','NEEDS-B3')){$session.AddLimit('[acquisition] '+$code+': injected for this fixture.')}
 $session.AddDiagnostic('probe uia state: '+$snapshot.UiaStatus.State)
 $screen=New-Object AppStudio.ScreenRecord
 $screen.ScanId='sc-live';$screen.ScreenId='S1';$screen.Hwnd=$snapshot.Win32.TopHwnd;$screen.Title='FixtureWin32';$screen.ClassName=$snapshot.Win32.ClassName
 $screen.Rect=$rect;$screen.ShotProblem='SHOT-SKIPPED: this fixture does not need a picture.'
 $session.Screens.Screens.Add($screen)
 [AppStudio.SessionStore]::WriteMeta($session)
 $outputs=[AppStudio.Outputs]::WriteAll($session,(2*1024*1024))
 if(-not$outputs.Markdown.Written-or-not$outputs.Report.Written){throw ('outputs missing: '+$outputs.Problems)}
 $markdown=[IO.File]::ReadAllText($session.SessionMdPath);$html=[IO.File]::ReadAllText($session.ReportPath);$meta=[IO.File]::ReadAllText((Join-Path $session.Folder 'meta.json'))
 foreach($code in @('NEEDS-B3','ACQ-RESTART','ACQ-DROPPED')){
  foreach($projection in @(@('meta.json',$meta),@('session.md',$markdown),@('report.html',$html))){
   if(-not$projection[1].Contains($code)){throw ($code+' is missing from '+$projection[0])}
  }
 }
 $health=[AppStudio.Probe]::GetHealth();if($null-eq$health-or[string]::IsNullOrEmpty($health.State)){throw 'the acquisition worker does not report its state'}
 Write-Output ('PASS test-acq-diagnostics codes=NEEDS-B3,ACQ-RESTART,ACQ-DROPPED projections=meta+session.md+report.html workerHealth='+$health.State)
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item $temp -Recurse -Force}
