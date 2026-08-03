$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build=& (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-restart-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
$process=$null
function Start-Fixture([string]$ready){
 if(Test-Path $ready){Remove-Item -LiteralPath $ready -Force}
 $script:process=Start-Process $build.FixtureWinForms -ArgumentList @('--ready',$ready) -PassThru
 $limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWinForms not ready'};Start-Sleep -Milliseconds 250;return Get-Content $ready -Raw|ConvertFrom-Json
}
function Acquire-Window($info,[string]$title){
 $hwnd=[int64]$info.window
 $target=New-Object AppStudio.TargetWindowInfo
 $target.Hwnd=$hwnd;$target.ProcessId=$script:process.Id;$target.Title='FixtureWinForms';$target.ClassName='WindowsForms10.Window.8.app';$target.ProcessName='FixtureWinForms'
 $target.Rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$hwnd)
 $session=[AppStudio.SessionStore]::Create($temp,'snap',$title)
 $runner=New-Object AppStudio.ScanRunner($root)
 try{ $null=[AppStudio.Acquire]::Window($runner,$session,$target,(New-Object AppStudio.ScanLimits),$null) }finally{ $runner.Dispose() }
 return $session
}
try{
 # An address is only worth anything if it still finds the element after the
 # application has been restarted, when every handle it had is gone.
 [AppStudio.Probe]::Configure($root,$false)
 $ready=Join-Path $temp 'ready.json'
 $firstInfo=Start-Fixture $ready
 $firstHwnd=[int64]$firstInfo.window
 $first=Acquire-Window $firstInfo 'before restart'
 if($first.Elements.Count-lt3){throw ('too few elements acquired from the fixture: '+$first.Elements.Count)}

 $named=@($first.Elements|Where-Object{-not [string]::IsNullOrEmpty($_.AutomationId)-or-not [string]::IsNullOrEmpty($_.Name)})
 if($named.Count-lt1){throw 'the fixture exposed nothing that could be addressed'}
 $subject=$named[0]
 $siblings=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';foreach($n in $first.Elements){$siblings.Add($n)}
 $locators=[AppStudio.LocatorBuilder]::Build($subject,$first.Screens.Screens[0].Rect,$siblings)
 if($locators.Count-lt2){throw ('too few locator candidates: '+$locators.Count)}
 # Nothing that dies with the process may appear in an address.
 foreach($locator in $locators){
  $json=[AppStudio.JsonWriter]::Write($locator.ToJson())
  foreach($forbidden in @('"hwnd"','"runtimeId"','"liveValue"')){ if($json-match $forbidden){throw ($forbidden+' reached a locator')} }
 }

 $process.Kill();$process.WaitForExit();$process.Dispose();$process=$null;Start-Sleep -Milliseconds 300
 $secondInfo=Start-Fixture $ready
 $secondHwnd=[int64]$secondInfo.window
 if($secondHwnd-eq$firstHwnd){throw 'the fixture restart reused the same window handle'}
 $second=Acquire-Window $secondInfo 'after restart'
 $fresh=New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]';foreach($n in $second.Elements){$fresh.Add($n)}

 $resolved=[AppStudio.LocatorResolver]::Resolve($locators,$fresh)
 if(-not$resolved.Resolved){throw ('no address survived the restart: '+$resolved.State+' - '+$resolved.Reason)}
 if(-not [AppStudio.LocatorResolver]::Identifies($resolved.UsedLocator.Strategy)){throw ('the restart was resolved by something that is not an identification: '+$resolved.UsedLocator.Strategy)}
 if($resolved.Node.Hwnd-eq$subject.Hwnd-and$subject.Hwnd-ne0){throw 'the fixture did not really restart'}
 # What was found has to be the same thing, by the material that identified it.
 if(-not [string]::IsNullOrEmpty($subject.AutomationId)-and$resolved.Node.AutomationId-ne$subject.AutomationId){throw 'the restart resolved to a different element'}
 if($resolved.Trace.Count-lt1){throw 'the resolution left no trace of what was tried'}

 # An element that is gone is reported as gone, not approximated.
 $ghost=New-Object AppStudio.ScanNode
 $ghost.AutomationId='ThisControlNeverExisted7b2';$ghost.Name='Ghost';$ghost.ControlType='Button';$ghost.ClassName='Button';$ghost.CtrlId=31337
 $ghost.Path='Nowhere > Button "Ghost"';$ghost.Rect=New-Object AppStudio.RectValue;$ghost.Rect.Width=10;$ghost.Rect.Height=10
 $ghostLocators=[AppStudio.LocatorBuilder]::Build($ghost,$second.Screens.Screens[0].Rect,$fresh)
 $ghostResult=[AppStudio.LocatorResolver]::Resolve($ghostLocators,$fresh)
 if($ghostResult.Resolved){throw 'an element that no longer exists was resolved anyway'}
 if($ghostResult.State-ne'not-found'){throw ('a vanished element was reported as '+$ghostResult.State)}

 Write-Output ('PASS test-live-restart hwndChanged=1 candidates='+$locators.Count+' survivedBy='+$resolved.UsedLocator.Strategy+' ephemeralMaterial=0 vanished=not-found')
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};if($null-ne$process){$process.Dispose()};Remove-Item -LiteralPath $temp -Recurse -Force}
