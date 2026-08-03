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
function Snapshot-At($info){$rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$info.normal);return [AppStudio.Probe]::Deep([AppStudio.ElementRef]@{X=$rect.X+[int]($rect.Width/2);Y=$rect.Y+[int]($rect.Height/2);Hwnd=[int64]$info.normal},3000)}
function Target-For($info,[string]$run){$rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$info.window);$target=New-Object AppStudio.TargetInfo;$target.TargetRunId=$run;$target.ProcessName='FixtureWinForms';$target.TopLevelClass='WindowsForms10.Window.8.app';$target.TopLevelCaption='FixtureWinForms';$target.ClientRect=$rect;return $target}
try{
 [AppStudio.Probe]::Configure($root,$false);$ready=Join-Path $temp 'ready.json';$firstInfo=Start-Fixture $ready;$firstHwnd=[int64]$firstInfo.window;$first=Snapshot-At $firstInfo;$firstTarget=Target-For $firstInfo 'run-1';$locators=[AppStudio.LocatorBuilder]::Build($first,$firstTarget);if($locators.Count-lt3){throw ('too few live locator candidates: '+$locators.Count)}
 foreach($locator in $locators){if([AppStudio.LocatorBuilder]::ContainsForbiddenPersistentMaterial($locator)){throw ('ephemeral material in '+$locator.Strategy)}}
 $process.Kill();$process.WaitForExit();$process.Dispose();$process=$null;Start-Sleep -Milliseconds 250
 $secondInfo=Start-Fixture $ready;$secondHwnd=[int64]$secondInfo.window;if($secondHwnd-eq$firstHwnd){throw 'fixture restart reused the same HWND'};$second=Snapshot-At $secondInfo;$secondTarget=Target-For $secondInfo 'run-2'
 $passed=0;$failed=0;$failedStrategies=@();foreach($locator in $locators){$ctx=New-Object AppStudio.ResolveContext;$ctx.Context='restart';$ctx.TargetRunId='run-2';$ctx.Original=$first;$ctx.Candidates=@($second);$ctx.TargetClientRect=$secondTarget.ClientRect;$verification=[AppStudio.Resolver]::Resolve($locator,$ctx);if($verification.TargetRunId-ne'run-2'-or$locator.Verifications.Count-ne1){throw 'restart verification history missing'};if($verification.MatchCount-eq1-and$verification.SameElement){$passed++}else{$failed++;$failedStrategies+=$locator.Strategy;if($locator.Confidence.Level-ne'low'){throw ($locator.Strategy+' restart failure was not reflected as low confidence')}}}
 if($passed-lt1-or-not(@($locators|Where-Object{$_.Strategy-eq'uia.automationId' -and $_.Verifications[0].SameElement}).Count-eq1)){throw 'no stable candidate survived fixture restart'}
 Write-Output ('PASS test-live-restart hwndChanged=1 candidates='+$locators.Count+' verified='+$locators.Count+' survived='+$passed+' failed='+$failed+' failedStrategies='+($failedStrategies-join',')+' context=restart targetRunId=run-2 forbiddenMaterial=0')
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};if($null-ne$process){$process.Dispose()};Remove-Item -LiteralPath $temp -Recurse -Force}
