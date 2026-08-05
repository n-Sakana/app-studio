$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-wfedit-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

# The change the split exists for.
#
# A recording that presses 1 to 9 in order, and the operator wants 5 left out.
# That has to be the deletion of one line of Workflow, and it has to leave every
# other module byte for byte the way it was. If taking one step out means
# editing the runtime, the split bought nothing.

function New-Step($session,[int]$index){
 $s=New-Object AppStudio.StepRecord
 $s.Index=$index;$s.Kind='click';$s.At=[DateTimeOffset]::Now;$s.OffsetMs=$index*700;$s.GapMs=200
 $s.AppName='calc';$s.WindowTitle='Fixture Window';$s.WindowClass='FixtureWindow'
 $s.ElementLabel=('Button "'+$index+'"');$s.ControlType='Button';$s.Confidence='high'
 $s.Rect=New-Object AppStudio.RectValue;$s.Rect.X=10;$s.Rect.Y=10;$s.Rect.Width=40;$s.Rect.Height=20
 $s.Point=New-Object AppStudio.PointValue;$s.Point.X=30;$s.Point.Y=20
 $l=New-Object AppStudio.ElementLocator;$l.Strategy='uia.automationId';$l.AutomationId=('num'+$index);$l.ControlType='Button';$l.Confidence='high'
 $s.Locators.Add($l)
 $l2=New-Object AppStudio.ElementLocator;$l2.Strategy='win32.ctrlId';$l2.CtrlId=(1000+$index);$l2.ClassName='Button';$l2.Confidence='medium'
 $s.Locators.Add($l2)
 $session.Steps.Add($s);return $s
}

$session=[AppStudio.SessionStore]::Create($temp,'record','one to nine')
$session.ValuePolicy='recordText'
for($i=1;$i -le 9;$i++){ $null=New-Step $session $i }

$project=[AppStudio.CodeProject]::Open($session)

# What the recording became, before anything is edited.
$before=@{}
foreach($f in $project.All()){ $before[$f.Language+'/'+$f.Name]=$f.Text }
if($before.Count-ne10){throw ('the project holds '+$before.Count+' modules instead of 10')}

function Step-Lines([string]$text){
 $found=New-Object System.Collections.ArrayList
 foreach($line in ($text -split "`r`n|`n")){
  if($line -match "^\s*(FindWindow|FocusElement|InvokeElement|SetElementText|ReadElementText|SendKeys|AskSecret|Unsupported)\s+['`"]"){[void]$found.Add($line)}
 }
 return ,$found
}
function Press-Lines([string]$text){
 $found=New-Object System.Collections.ArrayList
 foreach($line in ($text -split "`r`n|`n")){
  if($line -match "^\s*InvokeElement\s+['`"]"){[void]$found.Add($line)}
 }
 return ,$found
}

# The recording never switched to the window, so the plan states the window the
# first step expects before it. That is one line on top of the nine presses.
foreach($language in @('powershell','vba')){
 $quote=if($language -eq 'vba'){'"'}else{"'"}
 $workflow=$project.Find($language,'Workflow').Text
 $steps=Press-Lines $workflow
 if($steps.Count-ne9){throw ($language+' workflow has '+$steps.Count+' press lines instead of 9')}
 if((Step-Lines $workflow).Count-ne10){throw ($language+' workflow is not nine presses behind one window line')}

 # Exactly one line mentions step 5, and it is a whole line.
 $target=($quote+'A5'+$quote)
 $hits=@($steps | Where-Object { $_.Contains($target) })
 if($hits.Count-ne1){throw ($language+' has '+$hits.Count+' lines for step A5 instead of 1')}

 # Delete that one line. Nothing else is touched.
 $kept=New-Object System.Collections.ArrayList
 $removed=0
 foreach($line in ($workflow -split "`r`n|`n")){
  if($line.Contains($target)){$removed++;continue}
  [void]$kept.Add($line)
 }
 if($removed-ne1){throw ($language+': deleting step 5 removed '+$removed+' lines')}
 $edited=($kept -join "`r`n")
 $project.SetText($language,'Workflow',$edited)
}

# Eight steps left, in order, with 5 gone and nothing else disturbed.
foreach($language in @('powershell','vba')){
 $quote=if($language -eq 'vba'){'"'}else{"'"}
 $workflow=$project.Find($language,'Workflow').Text
 $steps=Press-Lines $workflow
 if($steps.Count-ne8){throw ($language+' workflow has '+$steps.Count+' press lines after the deletion instead of 8')}
 if($workflow.Contains($quote+'A5'+$quote)){throw ($language+' workflow still carries step A5')}
 foreach($id in @(1,2,3,4,6,7,8,9)){
  if(-not$workflow.Contains($quote+'A'+$id+$quote)){throw ($language+' workflow lost step A'+$id+', which was not the one being removed')}
 }
}

# The whole point: everything that is not the workflow is byte for byte what it
# was. The runtime did not have to be understood, opened or changed.
$untouched=0
foreach($f in $project.All()){
 $key=$f.Language+'/'+$f.Name
 if($f.Name-eq'Workflow'){
  if($f.Text-eq$before[$key]){throw ($key+' did not change, so the deletion never happened')}
  continue
 }
 if($f.Text-ne$before[$key]){throw ($key+' changed while one step was being taken out of the workflow')}
 $untouched++
}
if($untouched-ne8){throw ('only '+$untouched+' modules were checked for being untouched')}

# The address of step 5 is still on file. Leaving it there is what makes the
# deletion one line: nothing has to be renumbered and nothing has to be found.
if(-not$project.Find('powershell','RecordedFacts').Text.Contains("'A5'")){throw 'the PowerShell RecordedFacts lost the block for the deleted step'}
if(-not$project.Find('vba','RecordedFacts').Text.Contains('Case "A5"')){throw 'the VBA RecordedFacts lost the Case for the deleted step'}

# What is left still has to be code.
$check=[AppStudio.ScriptRun]::CheckPowerShell($project.Find('powershell','Workflow').Text)
if(-not$check.Ok){throw ('the edited PowerShell workflow does not parse: '+(($check.Problems)-join' / '))}
$vbaCheck=[AppStudio.ScriptRun]::CheckVba($project.Find('vba','Workflow').Text,'Workflow')
if(-not$vbaCheck.Ok){throw ('the edited VBA workflow is not structurally sound: '+(($vbaCheck.Problems)-join' / '))}

# And it still runs as a set: every module goes to disk together and the one a
# run starts from is the workflow.
$runDir=Join-Path $temp 'run'
$entry=[AppStudio.ScriptRun]::Write($project.Files('powershell'),$runDir,'powershell')
if($null-eq$entry){throw 'the module set has nothing to start from'}
if([IO.Path]::GetFileName($entry)-ne'Workflow.ps1'){throw ('the run starts from '+[IO.Path]::GetFileName($entry))}
foreach($name in @('Workflow.ps1','RecordedFacts.ps1','RuntimeCore.ps1','RuntimeLocator.ps1','RuntimeNative.ps1')){
 if(-not(Test-Path (Join-Path $runDir $name))){throw ('the run folder is missing '+$name)}
}

Write-Output ('PASS test-workflow-edit stepsBefore=9 stepsAfter=8 deletedLines=1 modulesUntouched='+$untouched+' languages=2')
}finally{Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue}
