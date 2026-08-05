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
function Join-Files($project,[string]$language){
 $text=New-Object Text.StringBuilder
 foreach($f in $project.Files($language)){[void]$text.AppendLine($f.Text)}
 return $text.ToString()
}
function Module-Text($files,[string]$name){
 foreach($f in $files){if($f.Name-eq$name){return $f.Text}}
 throw ('there is no module called '+$name)
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

$psFiles=[AppStudio.EngineGen]::BuildFiles($plan,$session)
$vbaFiles=[AppStudio.VbaGen]::BuildFiles($plan,$session)

# One line of the plan is one line of the workflow. The waits and the focus
# restores are the runtime's job and do not show up as steps.
$lines=[AppStudio.ScriptModel]::Lines($plan)
if($lines.Count-ne7){throw ('the recording of 7 steps became '+$lines.Count+' workflow lines')}

# Five modules per language, the same five names, in the same reading order.
foreach($pair in @(@('powershell',$psFiles),@('vba',$vbaFiles))){
 if($pair[1].Count-ne5){throw ($pair[0]+' was generated as '+$pair[1].Count+' modules instead of 5')}
 foreach($name in @('Workflow','RecordedFacts','RuntimeCore','RuntimeLocator','RuntimeNative')){
  $null=Module-Text $pair[1] $name
 }
}

$ps=New-Object Text.StringBuilder
foreach($f in $psFiles){[void]$ps.AppendLine($f.Text)}
$ps=$ps.ToString()
$vba=New-Object Text.StringBuilder
foreach($f in $vbaFiles){[void]$vba.AppendLine($f.Text)}
$vba=$vba.ToString()

$psWorkflow=Module-Text $psFiles 'Workflow'
$vbaWorkflow=Module-Text $vbaFiles 'Workflow'
$psFacts=Module-Text $psFiles 'RecordedFacts'
$vbaFacts=Module-Text $vbaFiles 'RecordedFacts'
$psCore=Module-Text $psFiles 'RuntimeCore'
$vbaCore=Module-Text $vbaFiles 'RuntimeCore'

# The nine operations are the contract. Both languages carry all of them, so
# neither is a reduced version of the other. They live in the runtime now, and
# the workflow calls them by the same names.
foreach($op in @('FindWindow','FocusElement','InvokeElement','SetElementText','ReadElementText','SendKeys','WaitGap','WaitIdle','AskSecret')){
 if(-not$psCore.Contains($op)){throw ('the PowerShell runtime is missing the operation '+$op)}
 if(-not$vbaCore.Contains($op)){throw ('the VBA runtime is missing the operation '+$op)}
}
foreach($id in @('A1','A2','A3','A4','A5','A6','A7')){
 if(-not$psWorkflow.Contains($id)){throw ('the PowerShell workflow dropped step '+$id)}
 if(-not$vbaWorkflow.Contains($id)){throw ('the VBA workflow dropped step '+$id)}
}

# The workflow is the file a person edits, so it stays short: seven steps are
# seven calls, and no address, interval or literal is written into it.
foreach($pair in @(@('powershell',$psWorkflow),@('vba',$vbaWorkflow))){
 foreach($leak in @('uia.automationId','win32.ctrlId','classIndex','hello world','saveButton')){
  if($pair[1].Contains($leak)){throw ($pair[0]+' workflow carries '+$leak+', which belongs in RecordedFacts')}
 }
}
if(-not$psFacts.Contains('saveButton')){throw 'the PowerShell RecordedFacts lost the address'}
if(-not$psFacts.Contains('hello world')){throw 'the PowerShell RecordedFacts lost the recorded text'}
if(-not$vbaFacts.Contains('ctrlId|Edit|1002')){throw 'the VBA RecordedFacts lost the Win32 address'}

# A position inside a window is a description of where something was. Neither
# generator may write one into a script as if it were an address.
foreach($pair in @(@('powershell',$ps),@('vba',$vba))){
 if($pair[1].Contains('window.relative')){throw ($pair[0]+' wrote a position into the script as an address')}
}

# What the recording refused to keep is asked for, not invented.
if(-not$psWorkflow.Contains("AskSecret")){throw 'the PowerShell does not ask the operator for the secret'}
if(-not$psFacts.Contains('.Asks("')){throw 'the engine kept no prompt for the secret step'}
if($psFacts.Contains('.Types("A4")')){throw 'the secret step was turned into a write'}
if(-not$vbaWorkflow.Contains('AskSecret ')){throw 'the VBA does not stop for the secret'}

# The element that only ever existed in the accessibility tree is reachable from
# PowerShell and is refused, by name, in VBA.
if(-not$psFacts.Contains('uia.nameControlType')){throw 'the PowerShell did not use the UI Automation address it had'}
if(-not$vbaFacts.Contains('addressed only through UI Automation')){throw 'the VBA did not say why it cannot reach the UIA only element'}
if(-not$vbaWorkflow.Contains('Unsupported     "A6"')){throw 'the VBA workflow does not refuse the UIA only step where it happens'}

# The step with no address at all stops both, with a reason.
if($psWorkflow-notmatch'Runtime\.Unsupported\(\s*"A7"\);'){throw 'the engine does not stop at the step it cannot address'}
if(-not$vbaWorkflow.Contains('Unsupported     "A7"')){throw 'the VBA does not stop at the step it cannot address'}
if(-not$psFacts.Contains('.Refuses("')){throw 'the engine kept no reason for the step it refuses'}

# A recorded chord reaches the script in a form that can actually be sent.
if(-not$psFacts.Contains('.Sends("^s"')){throw 'the recorded chord was not translated'}
if(-not$vbaFacts.Contains('gChord = "^s"')){throw 'the recorded chord was not translated into the VBA'}
if([AppStudio.EngineGen]::SendKeysChord('Ctrl+Shift+F5')-ne'^+{F5}'){throw 'a modified function key was translated wrongly'}
if([AppStudio.EngineGen]::SendKeysChord('Win+D')-ne''){throw 'a key with no equivalent was not reported as having none'}

# Every generated module has to be what it claims to be, not something that
# looks like it. A runtime module is not expected to carry the entry point.
# The engine is one program in five files, so it is compiled as one. A module
# on its own is a set of calls into the other four and says nothing by itself.
$check=[AppStudio.ScriptRun]::CheckEngine($psFiles)
if(-not$check.Ok){throw ('the generated engine does not compile: '+(($check.Problems)-join' / '))}
if(-not$check.Method.Contains('compil')){throw 'the engine check does not say that it compiled anything'}
foreach($f in $vbaFiles){
 $vbaCheck=[AppStudio.ScriptRun]::CheckVba($f.Text,$f.Name)
 if(-not$vbaCheck.Ok){throw ('the generated '+$f.FileName+' is not structurally sound: '+(($vbaCheck.Problems)-join' / '))}
 if(-not$vbaCheck.Method.Contains('structural')){throw 'the VBA check does not say that it is only a structural check'}
}
# A runtime module has no entry point and must not be reported as if it should.
$runtimeCheck=[AppStudio.ScriptRun]::CheckVba($vbaCore,'RuntimeCore')
if(-not$runtimeCheck.Ok){throw 'a VBA runtime module was failed for not carrying the entry point'}
$workflowCheck=[AppStudio.ScriptRun]::CheckVba(($vbaWorkflow -replace 'RunRecordedProcedure','SomethingElse'),'Workflow')
if($workflowCheck.Ok){throw 'a VBA workflow with no entry point was accepted'}

# Three versions, always. Editing, going back to the generated one, and undoing
# a change that was taken in.
$project=[AppStudio.CodeProject]::Open($session)
if($project.Files('powershell').Count-ne5){throw ('the project did not open with five PowerShell modules, got '+$project.Files('powershell').Count)}
if($project.Files('vba').Count-ne5){throw ('the project did not open with five VBA modules, got '+$project.Files('vba').Count)}
if($project.Files('powershell')[0].Name-ne'Workflow'){throw 'the module a person edits is not listed first'}
if(-not$project.Files('powershell')[0].IsWorkflow){throw 'the workflow module does not say it is the workflow'}
if($project.Entry('vba').Name-ne'Workflow'){throw 'the VBA entry point module is not Workflow'}
if($project.DiffersFromBaseline('powershell')){throw 'a freshly opened project already differs from the generated version'}
$project.SetText('powershell','Workflow',$psWorkflow+"`r`n// edited by hand")
if(-not$project.DiffersFromBaseline('powershell')){throw 'an edit was not noticed'}
$incoming=New-Object 'System.Collections.Generic.List[AppStudio.CodeFile]'
$file=New-Object AppStudio.CodeFile;$file.Language='powershell';$file.Name='Workflow';$file.Text="// from an assistant`r`nnamespace AppStudioRun { internal static class Spare { } }"
$incoming.Add($file)
$project.Apply($incoming)
if($project.Find('powershell','Workflow').Text-notmatch'from an assistant'){throw 'the answer was not applied'}
# Taking one module in must not disturb the modules it did not mention.
if($project.Find('powershell','RuntimeCore').Text-ne$psCore){throw 'applying one module changed a module the answer never mentioned'}
if(-not$project.UndoApply()){throw 'there was nothing to undo after applying'}
if($project.Find('powershell','Workflow').Text-notmatch'edited by hand'){throw 'undo did not bring the edited version back'}
$project.RestoreBaseline('powershell')
if($project.DiffersFromBaseline('powershell')){throw 'restoring the generated version left a difference'}
$saveProblem=$project.Save()
if($null-ne$saveProblem){throw ('the code folder could not be written: '+$saveProblem)}
foreach($name in @('current\Workflow.cs','current\RecordedFacts.cs','current\RuntimeCore.cs','current\RuntimeLocator.cs','current\RuntimeNative.cs','current\Workflow.bas','current\RuntimeCore.bas','baseline\Workflow.cs','code.json')){
 if(-not(Test-Path (Join-Path $project.Folder $name))){throw ('the code folder is missing '+$name)}
}

Write-Output ('PASS test-codegen ops='+$plan.Ops.Count+' workflowLines='+$lines.Count+' modulesPerLanguage=5 secrets='+$plan.SecretCount+' unsupported='+$plan.Unsupported+' vbaUnreachable='+$plan.UnreachableFromVba+' psBytes='+$ps.Length+' vbaBytes='+$vba.Length)
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
