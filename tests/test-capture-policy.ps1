$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;[AppStudio.DpiAwareness]::Enable();$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-capture-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null
try{
 [AppStudio.Probe]::Configure($root,$false)
 $ready=Join-Path $temp 'ready.json'
 $process=Start-Process $build.FixtureWinForms -ArgumentList @('--ready',$ready) -PassThru
 $limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25}
 if(-not(Test-Path $ready)){throw 'FixtureWinForms not ready'}
 Start-Sleep -Milliseconds 400
 $f=Get-Content $ready -Raw|ConvertFrom-Json

 # The fixture really does publish a password field, which is what the masking
 # has to key off.
 $passwordPhysical=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.password)
 $password=[AppStudio.Probe]::At(($passwordPhysical.X+[int]($passwordPhysical.Width/2)),($passwordPhysical.Y+[int]($passwordPhysical.Height/2)),1500)
 if(-not$password.Uia.IsPassword){throw 'IsPassword was false on the fixture password box'}

 # Acquire the window the way the product does, then take its picture.
 $windowHwnd=[int64]$f.window
 $target=New-Object AppStudio.TargetWindowInfo
 $target.Hwnd=$windowHwnd;$target.ProcessId=$process.Id;$target.Title='FixtureWinForms';$target.ClassName='WindowsForms10.Window.8.app';$target.ProcessName='FixtureWinForms'
 $target.Rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$windowHwnd)
 $session=[AppStudio.SessionStore]::Create($temp,'snap','capture policy')
 $runner=New-Object AppStudio.ScanRunner($root)
 try{ $null=[AppStudio.Acquire]::Window($runner,$session,$target,(New-Object AppStudio.ScanLimits),$null) }finally{ $runner.Dispose() }
 $screen=$session.Screens.Screens[0]

 # The password box has to be one of the rectangles that gets blacked out.
 $masks=[AppStudio.Acquire]::SecretMasks($session,$screen)
 if($masks.Count-lt1){throw 'no secret rectangle was found on a window that has a password box'}
 [AppStudio.Acquire]::Shoot($session,$screen,(New-Object AppStudio.Acquire+NullGuard),250)
 if(-not$screen.HasShot){throw ('the window picture failed: '+$screen.ShotProblem)}
 if($screen.Note-notmatch 'blacked out'){throw 'the picture does not say that something was blacked out'}

 # And it really is black in the file, at the middle of the password box.
 $bitmap=New-Object Drawing.Bitmap($screen.ShotFile)
 try{
  $x=($passwordPhysical.X+[int]($passwordPhysical.Width/2))-$screen.Rect.X
  $y=($passwordPhysical.Y+[int]($passwordPhysical.Height/2))-$screen.Rect.Y
  if($x-lt0-or$y-lt0-or$x-ge$bitmap.Width-or$y-ge$bitmap.Height){throw 'the password box is not inside the picture'}
  $pixel=$bitmap.GetPixel($x,$y)
  if($pixel.R-ne0-or$pixel.G-ne0-or$pixel.B-ne0){throw ('the password box was not blacked out: '+$pixel.ToString())}
 }finally{$bitmap.Dispose()}

 # A whole-desktop capture stays behind an explicit action and keeps its warning.
 $blocked=[AppStudio.Capture]::Full((Join-Path $temp 'blocked.png'),$false)
 if($blocked.Status.State-ne'unavailable'-or(Test-Path (Join-Path $temp 'blocked.png'))){throw 'an implicit full-screen capture was not blocked'}
 $full=[AppStudio.Capture]::Full((Join-Path $temp 'full.png'),$true)
 if($full.Status.State-ne'ok'-or-not(Test-Path $full.File)-or[string]::IsNullOrWhiteSpace($full.Warning)){throw 'the explicit full-screen capture failed or lost its warning'}

 # Nothing the fixture holds may reach a written output.
 $session.EndedAt=[DateTimeOffset]::Now
 [AppStudio.SessionStore]::WriteMeta($session)
 $outputs=[AppStudio.Outputs]::WriteAll($session,(8*1024*1024))
 if(-not$outputs.Complete){throw ('outputs incomplete: '+$outputs.Problems)}
 foreach($file in Get-ChildItem $session.Folder -File -Recurse|Where-Object{$_.Extension-ne'.png'-and$_.Extension-ne'.pdf'}){
  $text=[IO.File]::ReadAllText($file.FullName)
  foreach($secretText in @('secret-value-42','P@ssword123')){ if($text.Contains($secretText)){throw ('a fixture value leaked to '+$file.Name)} }
 }

 Write-Output ('PASS test-capture-policy secretRects='+$masks.Count+' passwordPixel=black maskingStated=1 fullImplicit=blocked fullExplicit=ok valueLeak=0')
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item $temp -Recurse -Force}
