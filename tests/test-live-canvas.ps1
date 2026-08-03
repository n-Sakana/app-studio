$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;[AppStudio.DpiAwareness]::Enable();$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-canvas-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null
try{
 # An application that paints its own surface exposes nothing to walk. The whole
 # point of this test is that the product says so instead of producing a thin
 # list that reads like a complete one.
 [AppStudio.Probe]::Configure($root,$false)
 $ready=Join-Path $temp 'ready.json'
 $process=Start-Process $build.FixtureCanvas -ArgumentList @('--ready',$ready) -PassThru
 $limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25}
 if(-not(Test-Path $ready)){throw 'FixtureCanvas not ready'}
 Start-Sleep -Milliseconds 300
 $f=Get-Content $ready -Raw|ConvertFrom-Json
 $hwnd=[int64]$f.hwnd
 if([AppStudio.WindowTools]::CountChildren([IntPtr]$hwnd)-ne0){throw 'FixtureCanvas created a child HWND'}
 $physical=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hwnd)

 $reference=New-Object AppStudio.ElementRef;$reference.X=$physical.X+[int]($physical.Width/2);$reference.Y=$physical.Y+[int]($physical.Height/2);$reference.Hwnd=$hwnd
 $snapshot=[AppStudio.Probe]::Deep($reference,3000)
 if($snapshot.Win32.ClassName-ne'FixtureCanvasWindow'){throw ('canvas Win32 class mismatch: '+$snapshot.Win32.ClassName)}
 $empty=@($snapshot.UiaStatus.Reasons|Where-Object{$_.Code-eq'UIA-EMPTYTREE'})
 if($empty.Count-ne1-or$snapshot.UiaStatus.State-ne'partial'){throw ('UIA-EMPTYTREE missing: state='+$snapshot.UiaStatus.State)}

 # Acquire it the way the product does, through the chooser path.
 $target=New-Object AppStudio.TargetWindowInfo
 $target.Hwnd=$hwnd;$target.ProcessId=$process.Id;$target.Title='FixtureCanvas';$target.ClassName='FixtureCanvasWindow';$target.Rect=$physical;$target.ProcessName='FixtureCanvas'
 $session=[AppStudio.SessionStore]::Create($temp,'snap','canvas fixture')
 $runner=New-Object AppStudio.ScanRunner($root)
 try{ $null=[AppStudio.Acquire]::Window($runner,$session,$target,(New-Object AppStudio.ScanLimits),$null) }finally{ $runner.Dispose() }
 if($session.Screens.Screens.Count-ne1){throw ('the canvas produced '+$session.Screens.Screens.Count+' screens instead of 1')}
 [AppStudio.Acquire]::Shoot($session,$session.Screens.Screens[0],(New-Object AppStudio.Acquire+NullGuard),250)
 if(-not$session.Screens.Screens[0].HasShot){throw ('the canvas picture failed: '+$session.Screens.Screens[0].ShotProblem)}

 # Whatever the walk found, the limits have to be stated.
 if($session.Limits.Count-lt1){throw 'an application with no published structure reported no limit at all'}
 $session.EndedAt=[DateTimeOffset]::Now
 [AppStudio.SessionStore]::WriteMeta($session)
 $outputs=[AppStudio.Outputs]::WriteAll($session,(8*1024*1024))
 if(-not$outputs.Complete){throw ('canvas outputs incomplete: '+$outputs.Problems)}
 $markdown=[IO.File]::ReadAllText($session.SessionMdPath)
 $html=[IO.File]::ReadAllText($session.ReportPath)
 # The picture is the only description that exists, and both outputs have to say
 # so rather than let a reader assume the element table is the whole story.
 $inner=@($session.Elements|Where-Object{$_.Hwnd-ne$hwnd})
 if($inner.Count-gt0){throw ('the canvas exposed inner parts it should not have: '+$inner.Count)}
 foreach($projection in @(@('session.md',$markdown),@('report.html',$html))){
  if($projection[1]-notmatch '(?i)coordinat'){throw ($projection[0]+' does not warn that only coordinates are left')}
  if($projection[1]-notmatch 'not a proof'){throw ($projection[0]+' dropped the completeness caveat')}
 }
 if($outputs.Pdf.PageCount-ne1){throw 'the canvas picture did not become a page'}

 # A read against the surface still works and is recorded honestly.
 $read=[AppStudio.ProbeRunner]::Run($reference,[AppStudio.ProbeKind]::Read,(New-Object AppStudio.ProbeArgs))
 if($read.Outcome-ne'success'-or[string]::IsNullOrWhiteSpace($read.Method)){throw ('canvas read probe failed: '+$read.Outcome)}
 if($read.Attempts.Count-lt1){throw 'the read left no route trail'}

 Write-Output ('PASS test-live-canvas childHwnd=0 uia=partial:UIA-EMPTYTREE innerElements='+$inner.Count+' picture=1 pdfPages='+$outputs.Pdf.PageCount+' limitsStated='+$session.Limits.Count+' read='+$read.Outcome)
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item $temp -Recurse -Force}
