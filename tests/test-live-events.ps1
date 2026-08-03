$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-events-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null;$monitor=$null
function Pump([int]$Milliseconds){$frame=New-Object Windows.Threading.DispatcherFrame;$timer=New-Object Windows.Threading.DispatcherTimer;$timer.Interval=[TimeSpan]::FromMilliseconds($Milliseconds);$timer.Add_Tick({$timer.Stop();$frame.Continue=$false});$timer.Start();[Windows.Threading.Dispatcher]::PushFrame($frame)}
try{
 $ready=Join-Path $temp 'ready.json';$command=Join-Path $temp 'command.txt';$done=Join-Path $temp 'done.txt';$process=Start-Process $build.FixtureWinForms -ArgumentList @('--ready',$ready,'--command',$command,'--done',$done) -PassThru;$limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWinForms not ready'}
 $monitor=New-Object AppStudio.WinEventMonitor;$monitor.Start($process.Id);[IO.File]::WriteAllText($command,'one',(New-Object Text.UTF8Encoding($false)));Pump 1200;[IO.File]::WriteAllText($command,'two',(New-Object Text.UTF8Encoding($false)));Pump 1200
 $events=@($monitor.Drain());if($events.Count-lt4){throw ('Too few WinEvents: '+$events.Count)}
 for($i=1;$i-lt$events.Count;$i++){if($events[$i].Sequence-le$events[$i-1].Sequence){throw 'WinEvent sequence was not increasing'}}
 $types=@($events|ForEach-Object Type);foreach($required in @('menu.open','menu.close','focus.change','window.show')){if($types-notcontains$required){throw ($required+' missing; types='+($types-join','))}}
 $session=New-Object AppStudio.SessionRecorder((Join-Path $temp 'shots'));foreach($event in $events){$session.AddEvent($event.Type,'winEvent',('hwnd='+$event.Hwnd))};if(@($session.Data.Events|Where-Object Source -eq 'winEvent').Count-ne$events.Count){throw 'events[].source missing'}
 Write-Output ('PASS test-live-events events='+$events.Count+' menuOpen='+@($types|Where-Object{$_-eq'menu.open'}).Count+' menuClose='+@($types|Where-Object{$_-eq'menu.close'}).Count+' focus='+@($types|Where-Object{$_-eq'focus.change'}).Count+' reentry=2')
}finally{if($null-ne$monitor){$monitor.Dispose()};if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};Remove-Item $temp -Recurse -Force}
