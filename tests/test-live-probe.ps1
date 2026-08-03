$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly;[AppStudio.DpiAwareness]::Enable();$build=&(Join-Path $PSScriptRoot 'build-fixtures.ps1')
# The click and keys kinds fall through to real input, which moves the pointer.
# Whoever is using the machine gets it back at the end.
Add-Type -Namespace PuiTest -Name Cursor -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool SetCursorPos(int x, int y);
'@
$startCursor=[AppStudio.WindowTools]::CursorPosition()
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-probe-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null;$process=$null
function Ref($rect){$r=New-Object AppStudio.ElementRef;$r.X=[int](($rect.left+$rect.right)/2);$r.Y=[int](($rect.top+$rect.bottom)/2);$r.Hwnd=0;return $r}
function RefHwnd($handle){$rect=[AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$handle);$r=New-Object AppStudio.ElementRef;$r.X=$rect.X+[int]($rect.Width/2);$r.Y=$rect.Y+[int]($rect.Height/2);$r.Hwnd=[int64]$handle;return $r}
function WriteArgs([string]$value){$a=New-Object AppStudio.ProbeArgs;$a.WriteEnabled=$true;$a.Value=$value;$a.BudgetMs=3000;return $a}
function Check($result,[string]$label){if(@('success','failed','blocked','notSupported','unknown')-notcontains$result.Outcome){throw ($label+' invalid outcome='+$result.Outcome)};if([string]::IsNullOrWhiteSpace($result.Method)){throw ($label+' method missing')}}
try{
 [AppStudio.Probe]::Configure($root,$false);$ready=Join-Path $temp 'ready.json';$process=Start-Process $build.FixtureWinForms -ArgumentList @('--ready',$ready) -PassThru;$limit=[DateTime]::UtcNow.AddSeconds(10);while(-not(Test-Path $ready)-and[DateTime]::UtcNow-lt$limit){Start-Sleep -Milliseconds 25};if(-not(Test-Path $ready)){throw 'FixtureWinForms not ready'};Start-Sleep -Milliseconds 300;$f=Get-Content $ready -Raw|ConvertFrom-Json
 $normal=RefHwnd $f.normal;$password=RefHwnd $f.password;$first=RefHwnd $f.first;$noEffect=RefHwnd $f.noEffect;$toggle=RefHwnd $f.toggle;$choice=RefHwnd $f.choice;$list=RefHwnd $f.list
 $read=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::Read,(New-Object AppStudio.ProbeArgs));Check $read 'read';if($read.Outcome-ne'success'-or$read.Method-ne'uia.properties'){throw 'read probe did not succeed through UIA properties'}
 $readOnly=[AppStudio.ProbeRunner]::Run($first,[AppStudio.ProbeKind]::Invoke,(New-Object AppStudio.ProbeArgs));Check $readOnly 'readOnly';if($readOnly.Outcome-ne'blocked'-or$readOnly.Method-ne'policy.readOnly'){throw 'read-only default did not block invoke'}
 $passwordWrite=[AppStudio.ProbeRunner]::Run($password,[AppStudio.ProbeKind]::SetValue,(WriteArgs 'forbidden'));Check $passwordWrite 'password';if($passwordWrite.Outcome-ne'blocked'-or$passwordWrite.Method-ne'policy.isPassword'){throw ('password setValue was not blocked: '+$passwordWrite.Outcome+' '+$passwordWrite.Method+' '+$passwordWrite.Error.Code)}
 $set=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::SetValue,(WriteArgs 'changed-value'));Check $set 'setValue';if($set.Outcome-ne'success'-or$set.Method-ne'uia.ValuePattern.SetValue'-or$null-eq$set.Undo-or-not$set.Undo.Available){throw ('setValue/undo mismatch outcome='+$set.Outcome+' method='+$set.Method)}
 $undo=[AppStudio.ProbeRunner]::Undo($set);Check $undo 'undo';if($undo.Outcome-ne'success'-or$null-eq$set.Undo.PerformedAt){throw 'setValue undo failed'}
 $focusPassword=[AppStudio.ProbeRunner]::Run($password,[AppStudio.ProbeKind]::Focus,(WriteArgs ''));Check $focusPassword 'focusPassword';$focus=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::Focus,(WriteArgs ''));Check $focus 'focus';if($focus.Outcome-ne'success'-or$focus.Method-ne'uia.SetFocus'){throw ('focus did not verify: '+$focus.Outcome)}
 $invoke=[AppStudio.ProbeRunner]::Run($first,[AppStudio.ProbeKind]::Invoke,(WriteArgs ''));Check $invoke 'invoke';if($invoke.Outcome-ne'success'-or$invoke.Method-ne'uia.InvokePattern.Invoke'){throw ('invoke did not verify: '+$invoke.Outcome+' '+$invoke.Method)}
 $unknown=[AppStudio.ProbeRunner]::Run($noEffect,[AppStudio.ProbeKind]::Invoke,(WriteArgs ''));Check $unknown 'unknown';if($unknown.Outcome-ne'unknown'-or$unknown.Method-ne'uia.InvokePattern.Invoke'){throw ('no-effect control was not unknown: '+$unknown.Outcome+' '+$unknown.Method)}
 $toggleResult=[AppStudio.ProbeRunner]::Run($toggle,[AppStudio.ProbeKind]::Toggle,(WriteArgs ''));Check $toggleResult 'toggle';if($toggleResult.Outcome-ne'success'){throw ('toggle failed: '+$toggleResult.Outcome+' '+$toggleResult.Method)}
 $expand=[AppStudio.ProbeRunner]::Run($choice,[AppStudio.ProbeKind]::Expand,(WriteArgs ''));Check $expand 'expand'
 $select=[AppStudio.ProbeRunner]::Run($list,[AppStudio.ProbeKind]::Select,(WriteArgs ''));Check $select 'select'
 $scroll=[AppStudio.ProbeRunner]::Run($list,[AppStudio.ProbeKind]::Scroll,(WriteArgs ''));Check $scroll 'scroll'
 $click=[AppStudio.ProbeRunner]::Run($noEffect,[AppStudio.ProbeKind]::Click,(WriteArgs ''));Check $click 'click';if($click.Outcome-ne'unknown'){throw 'click on no-effect control was rounded to success'}
 $null=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::Focus,(WriteArgs ''));$keys=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::Keys,(WriteArgs 'Z'));Check $keys 'keys';if($keys.Method-ne'win32.SendInput.keys'){throw ('keys did not use SendInput: '+$keys.Method)}
 $rateFirst=[AppStudio.ProbeRunner]::Run($choice,[AppStudio.ProbeKind]::Click,(WriteArgs ''));Check $rateFirst 'rateFirst';$rate=[AppStudio.ProbeRunner]::Run($choice,[AppStudio.ProbeKind]::Click,(WriteArgs ''));if($rate.Outcome-ne'blocked'-or$rate.Method-ne'policy.rateLimit'){throw ('one-second rate limit missing: '+$rate.Outcome+' '+$rate.Method)}
 [AppStudio.Probe]::Shutdown();$failed=[AppStudio.ProbeRunner]::Run($normal,[AppStudio.ProbeKind]::Read,(New-Object AppStudio.ProbeArgs));Check $failed 'failed';if($failed.Outcome-ne'failed'){throw ('failed outcome was not retained: '+$failed.Outcome)};[AppStudio.Probe]::Configure($root,$false)
 # Every operation leaves a trail of the routes it tried, and no field value
 # ever reaches it.
 $trail=New-Object 'System.Collections.Generic.List[AppStudio.RouteAttempt]'
 foreach($result in @($read,$readOnly,$passwordWrite,$set,$undo,$focus,$invoke,$unknown,$toggleResult,$expand,$select,$scroll,$click,$keys,$rateFirst,$rate,$failed)){
  if($result.Attempts.Count-lt1){throw ('an operation left no route trail: '+$result.Kind+' '+$result.Outcome)}
  if([string]::IsNullOrWhiteSpace($result.AttemptLine)){throw 'the route trail has no text'}
  foreach($attempt in $result.Attempts){$trail.Add($attempt)}
 }
 $routes=@($trail|ForEach-Object{$_.Route}|Sort-Object -Unique)
 foreach($needed in @('uia','guard')){if($routes-notcontains$needed){throw ('a route never appeared in any trail: '+$needed)}}
 $blockedTrail=@($passwordWrite.Attempts|Where-Object{$_.Route-eq'guard'})
 if($blockedTrail.Count-lt1-or$blockedTrail[0].Effect-notmatch 'nothing was sent'){throw 'the password refusal did not record that nothing was sent'}
 $json=''
 foreach($attempt in $trail){$json=$json+[AppStudio.JsonWriter]::WriteCompact($attempt.ToJson())}
 if($json-notmatch '"method"'-or$json-notmatch '"outcome"'){throw 'the route trail lost its fields'}
 if($json-match 'secret-value-42|P@ssword123|changed-value'){throw 'a field value leaked into the route trail'}
 $results=@($read,$readOnly,$passwordWrite,$set,$undo,$focus,$invoke,$unknown,$toggleResult,$expand,$select,$scroll,$click,$keys,$rateFirst,$rate,$failed);$methods=@($results|ForEach-Object{$_.Method}|Sort-Object -Unique);$outcomes=@($results|ForEach-Object{$_.Outcome}|Sort-Object -Unique);if($outcomes.Count-ne5){throw ('not all five outcomes were exercised: '+($outcomes-join','))};Write-Output ('PASS test-live-probe kinds=10 outcomes='+($outcomes-join',')+' methods='+($methods-join',')+' noEffect=unknown password=blocked readOnly=blocked rateLimit=blocked undo=success valueLeak=0')
}finally{[AppStudio.Probe]::Shutdown();if($null-ne$process-and-not$process.HasExited){$process.Kill();$process.WaitForExit()};if($null-ne$startCursor){$null=[PuiTest.Cursor]::SetCursorPos($startCursor.X,$startCursor.Y)};Remove-Item $temp -Recurse -Force}
