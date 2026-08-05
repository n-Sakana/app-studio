$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-codegen-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

function New-Locator([string]$strategy){
 $l=New-Object AppStudio.ElementLocator;$l.Strategy=$strategy;$l.Confidence='high';return $l
}
function New-Step($session,[int]$index,[string]$kind,[string]$element){
 $s=New-Object AppStudio.StepRecord
 $s.Index=$index;$s.Kind=$kind;$s.At=[DateTimeOffset]::Now;$s.OffsetMs=$index*900;$s.GapMs=350
 $s.AppName='fixture';$s.WindowTitle='Fixture Window';$s.WindowClass='FixtureWindow'
 $s.ElementLabel=$element;$s.ControlType='Button';$s.Confidence='high'
 $s.Rect=New-Object AppStudio.RectValue;$s.Rect.X=100;$s.Rect.Y=200;$s.Rect.Width=80;$s.Rect.Height=24
 $s.Point=New-Object AppStudio.PointValue;$s.Point.X=140;$s.Point.Y=212
 $session.Steps.Add($s);return $s
}

$session=[AppStudio.SessionStore]::Create($temp,'record','codegen fixture')
$session.ValuePolicy='recordText'
$session.AddLimit('[uia] SCAN-TIMEOUT: the walk stopped after its allowance.')

# A1 switch, A2 press with both a UIA and a Win32 address, A3 type, A4 secret,
# A5 a shortcut, A6 an element only UI Automation ever saw, A7 an element with
# nothing but a position.
$a1=New-Step $session 1 'appSwitch' $null
$a2=New-Step $session 2 'click' 'Button "Save"'
$l=New-Locator 'uia.automationId';$l.AutomationId='saveButton';$l.ControlType='Button';$a2.Locators.Add($l)
$l=New-Locator 'win32.ctrlId';$l.CtrlId=1041;$l.ClassName='Button';$a2.Locators.Add($l)
$a3=New-Step $session 3 'textInput' 'Edit "Name"'
$a3.ValueKind='text';$a3.Value='hello world';$a3.ValueLength=11
$l=New-Locator 'win32.ctrlId';$l.CtrlId=1002;$l.ClassName='Edit';$a3.Locators.Add($l)
$a4=New-Step $session 4 'secretInput' 'Edit "Password"'
$a4.ValueKind='secret';$a4.MaskRule='isPassword'
$l=New-Locator 'win32.ctrlId';$l.CtrlId=1003;$l.ClassName='Edit';$a4.Locators.Add($l)
$a5=New-Step $session 5 'keyChord' $null
$a5.KeyChord='Ctrl+S'
$a6=New-Step $session 6 'click' 'Button "Only in UIA"'
$l=New-Locator 'uia.nameControlType';$l.Name='Only in UIA';$l.ControlType='Button';$a6.Locators.Add($l)
$a7=New-Step $session 7 'click' 'Button "Nameless"'
$l=New-Locator 'window.relative';$l.RelativeX=0.5;$l.RelativeY=0.5;$l.Confidence='low';$a7.Locators.Add($l)

$plan=[AppStudio.ScriptModel]::Build($session)
if($plan.Ops.Count-lt 14){throw ('the plan is too short: '+$plan.Ops.Count)}
if($plan.SecretCount-ne1){throw ('secret steps counted wrongly: '+$plan.SecretCount)}
if($plan.Unsupported-ne1){throw ('the step with only a position should be the one refused, got '+$plan.Unsupported)}
if($plan.UnreachableFromVba-lt1){throw 'the UIA only element was not counted as out of reach for VBA'}

$ps=[AppStudio.PowerShellGen]::Build($plan,$session)
$vba=[AppStudio.VbaGen]::Build($plan,$session)

# The nine operations are the contract. Both languages carry all of them, so
# neither is a reduced version of the other.
foreach($op in @('FindWindow','FocusElement','InvokeElement','SetElementText','ReadElementText','SendKeys','WaitGap','WaitIdle','AskSecret')){
 if(-not$ps.Contains($op)){throw ('the PowerShell is missing the operation '+$op)}
 if(-not$vba.Contains($op)){throw ('the VBA is missing the operation '+$op)}
}
foreach($id in @('A1','A2','A3','A4','A5','A6','A7')){
 if(-not$ps.Contains($id)){throw ('the PowerShell dropped step '+$id)}
 if(-not$vba.Contains($id)){throw ('the VBA dropped step '+$id)}
}

# A position inside a window is a description of where something was. Neither
# generator may write one into a script as if it were an address.
foreach($pair in @(@('powershell',$ps),@('vba',$vba))){
 if($pair[1].Contains('window.relative')){throw ($pair[0]+' wrote a position into the script as an address')}
}

# What the recording refused to keep is asked for, not invented.
if(-not$ps.Contains('AskSecret -Locators')){throw 'the PowerShell does not ask the operator for the secret'}
if($ps.Contains('isPassword')-and$ps.Contains('SetElementText -Locators @{ strategy = ''win32.ctrlId''; className = ''Edit''; ctrlId = ''1003''')){throw 'the secret step was turned into a write'}
if(-not$vba.Contains('AskSecret ')){throw 'the VBA does not stop for the secret'}

# The element that only ever existed in the accessibility tree is reachable from
# PowerShell and is refused, by name, in VBA.
if(-not$ps.Contains('uia.nameControlType')){throw 'the PowerShell did not use the UI Automation address it had'}
if(-not$vba.Contains('addressed only through UI Automation')){throw 'the VBA did not say why it cannot reach the UIA only element'}

# The step with no address at all stops both, with a reason.
if(-not$ps.Contains('Unsupported -Reason')){throw 'the PowerShell does not stop at the step it cannot address'}
if(-not$vba.Contains('Unsupported "')){throw 'the VBA does not stop at the step it cannot address'}

# A recorded chord reaches the script in a form that can actually be sent.
if(-not$ps.Contains("SendKeys -Chord '^s'")){throw 'the recorded chord was not translated'}
if(-not$vba.Contains('SendKeys "^s"')){throw 'the recorded chord was not translated into the VBA'}
if([AppStudio.PowerShellGen]::SendKeysChord('Ctrl+Shift+F5')-ne'^+{F5}'){throw 'a modified function key was translated wrongly'}
if([AppStudio.PowerShellGen]::SendKeysChord('Win+D')-ne''){throw 'a key with no equivalent was not reported as having none'}

# The generated PowerShell has to be PowerShell, not something that looks like it.
$check=[AppStudio.ScriptRun]::CheckPowerShell($ps)
if(-not$check.Ok){throw ('the generated PowerShell does not parse: '+(($check.Problems)-join' / '))}
$vbaCheck=[AppStudio.ScriptRun]::CheckVba($vba)
if(-not$vbaCheck.Ok){throw ('the generated VBA is not structurally sound: '+(($vbaCheck.Problems)-join' / '))}
if(-not$vbaCheck.Method.Contains('structural')){throw 'the VBA check does not say that it is only a structural check'}

# Three versions, always. Editing, going back to the generated one, and undoing
# a change that was taken in.
$project=[AppStudio.CodeProject]::Open($session)
if($project.Files('powershell').Count-ne1){throw 'the project did not open with one PowerShell file'}
if($project.Files('vba').Count-ne1){throw 'the project did not open with one VBA file'}
if($project.DiffersFromBaseline('powershell')){throw 'a freshly opened project already differs from the generated version'}
$project.SetText('powershell','RecordedProcedure',$ps+"`r`n# edited by hand")
if(-not$project.DiffersFromBaseline('powershell')){throw 'an edit was not noticed'}
$incoming=New-Object 'System.Collections.Generic.List[AppStudio.CodeFile]'
$file=New-Object AppStudio.CodeFile;$file.Language='powershell';$file.Name='RecordedProcedure';$file.Text="# from an assistant`r`nWrite-Output 'x'"
$incoming.Add($file)
$project.Apply($incoming)
if($project.Find('powershell','RecordedProcedure').Text-notmatch'from an assistant'){throw 'the answer was not applied'}
if(-not$project.UndoApply()){throw 'there was nothing to undo after applying'}
if($project.Find('powershell','RecordedProcedure').Text-notmatch'edited by hand'){throw 'undo did not bring the edited version back'}
$project.RestoreBaseline('powershell')
if($project.DiffersFromBaseline('powershell')){throw 'restoring the generated version left a difference'}
$saveProblem=$project.Save()
if($null-ne$saveProblem){throw ('the code folder could not be written: '+$saveProblem)}
foreach($name in @('current\RecordedProcedure.ps1','current\RecordedProcedure.bas','baseline\RecordedProcedure.ps1','code.json')){
 if(-not(Test-Path (Join-Path $project.Folder $name))){throw ('the code folder is missing '+$name)}
}

Write-Output ('PASS test-codegen ops='+$plan.Ops.Count+' secrets='+$plan.SecretCount+' unsupported='+$plan.Unsupported+' vbaUnreachable='+$plan.UnreachableFromVba+' psBytes='+$ps.Length+' vbaBytes='+$vba.Length)
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
