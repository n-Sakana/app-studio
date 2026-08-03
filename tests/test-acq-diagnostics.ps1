$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;[AppStudio.DpiAwareness]::Enable();$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-acqdiag-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null
try{
 [AppStudio.Probe]::Configure($root,$false);$ready=Join-Path $temp 'ready.json';$process=Start-Process $build.FixtureWin32 -ArgumentList @('--mode','healthy','--ready',$ready) -PassThru;$limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWin32 not ready'};$f=Get-Content $ready -Raw|ConvertFrom-Json
 $rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.list);$snapshot=[AppStudio.Probe]::At(($rect.X+[int]($rect.Width/2)),($rect.Y+[int]($rect.Height/2)),1500)
 $session=New-Object AppStudio.SessionRecorder((Join-Path $temp 'shots'));$record=$session.Pin($snapshot,'List','B3 diagnosis');$session.AddFailure('acquisition','ACQ-RESTART','worker restarted',$null);$session.AddFailure('acquisition','ACQ-DROPPED','stale hover dropped',$null)
 $codes=@($session.Data.AcquisitionFailures|ForEach-Object Code);foreach($code in @('NEEDS-B3','ACQ-RESTART','ACQ-DROPPED')){if($codes-notcontains$code){throw ($code+' missing')}}
 $json=[AppStudio.JsonWriter]::Write($session.ToJson());$html=[AppStudio.DiagnosticProjection]::Html($session.Data);$screen=[AppStudio.DiagnosticProjection]::Screen($session.Data);foreach($code in @('NEEDS-B3','ACQ-RESTART','ACQ-DROPPED')){if(-not$json.Contains($code)-or-not$html.Contains($code)-or-not$screen.Contains($code)){throw ($code+' not projected to JSON/HTML/screen')}}
 Write-Output 'PASS test-acq-diagnostics codes=NEEDS-B3,ACQ-RESTART,ACQ-DROPPED projections=JSON+HTML+screen'
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item $temp -Recurse -Force}
