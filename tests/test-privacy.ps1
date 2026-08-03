$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-privacy-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{

# --- 1. the product has no way to read a keystroke ------------------------
# These are the APIs that would let typed text reach a file. Their absence is
# the guarantee, so it is fixed here rather than described in a document.
$sources=Get-ChildItem (Join-Path $root 'src') -Filter '*.cs'
$joined=($sources|ForEach-Object{[IO.File]::ReadAllText($_.FullName)})-join "`n"
foreach($api in @('SetWindowsHookEx','WH_KEYBOARD','WH_KEYBOARD_LL','GetKeyboardState','ToUnicode','ToAscii','GetClipboardData','SetClipboardViewer','AddClipboardFormatListener','RegisterRawInputDevices')){
 if($joined-match ('\b'+[regex]::Escape($api)+'\b')){throw ('a keystroke or clipboard capture API is present in the sources: '+$api)}
}
# GetAsyncKeyState is allowed, but only against the fixed watch list.
if($joined-notmatch 'GetAsyncKeyState'){throw 'the mouse button watch disappeared'}

# --- 2. the watch list itself --------------------------------------------
$watched=[AppStudio.KeyTable]::Watched
foreach($vk in @(0x41,0x5A,0x30,0x39)){ if($watched-notcontains$vk){throw ('a letter or digit is missing from the watch list: '+$vk)} }
foreach($vk in @(0x41,0x5A,0x30,0x39)){ if([AppStudio.KeyTable]::IsAlwaysSemantic($vk)){throw ('an unmodified letter or digit was treated as a command: '+$vk)} }
foreach($vk in @(0x0D,0x09,0x1B,0x70,0x7B)){ if(-not [AppStudio.KeyTable]::IsAlwaysSemantic($vk)){throw ('a command key was not recognised: '+$vk)} }
# Punctuation is never even looked at.
foreach($vk in @(0xBA,0xBC,0xBE,0xDE,0xC0)){ if($watched-contains$vk){throw ('a punctuation key is on the watch list: '+$vk)} }
if([AppStudio.KeyTable]::Chord($true,$false,$false,$false,0x53)-ne'Ctrl+S'){throw 'chord text mismatch'}
$modifiers=$null;$key=0
if(-not [AppStudio.KeyTable]::TryParse('Ctrl+Shift+S',[ref]$modifiers,[ref]$key)){throw 'a written chord could not be read back'}
if($key-ne0x53-or$modifiers.Count-ne2){throw 'chord round trip mismatch'}
if([AppStudio.KeyTable]::TryParse('Ctrl+Nonsense',[ref]$modifiers,[ref]$key)){throw 'an unknown key was accepted instead of refused'}

# --- 3. what counts as a secret ------------------------------------------
function New-Node([string]$name,[string]$automationId,[string]$class,$isPassword){
 $n=New-Object AppStudio.ScanNode;$n.Name=$name;$n.AutomationId=$automationId;$n.ClassName=$class;if($null-ne$isPassword){$n.IsPassword=$isPassword};return $n
}
if(-not [AppStudio.Privacy]::IsSecretElement((New-Node 'Anything' 'x' 'Edit' $true))){throw 'IsPassword was not treated as a secret'}
foreach($word in @('Password','pass word','PIN','Secret','API key','token','CVV','one-time code','MFA code')){
 if(-not [AppStudio.Privacy]::IsSecretElement((New-Node $word 'x' 'Edit' $null))){throw ('a secret-looking name was not caught: '+$word)}
}
if([AppStudio.Privacy]::IsSecretElement((New-Node 'Customer code' 'CustomerCode' 'Edit' $null))){throw 'an ordinary field was treated as a secret'}
if(-not [AppStudio.Privacy]::HasPasswordStyle(0x0020,'Edit')){throw 'the password window style was not recognised'}
if([AppStudio.Privacy]::HasPasswordStyle(0x0020,'Button')){throw 'the password style was read on a control that cannot have it'}
if([AppStudio.Privacy]::SecretRuleFor((New-Node 'Password' 'x' 'Edit' $null))-ne'secretName'){throw 'the secret rule was not named'}

# --- 4. what gets written down -------------------------------------------
$secretStep=New-Object AppStudio.StepRecord
[AppStudio.Privacy]::ApplyValue($secretStep,'recordText','hunter2-should-never-appear',$true,'isPassword')
if($secretStep.Value-or$secretStep.ValueLength-ne-1){throw 'a secret value or its length was written down'}
if($secretStep.Kind-ne'secretInput'){throw 'a secret entry was not marked as one'}
if($secretStep.Unavailable.Count-lt1){throw 'the absence of the value was not stated'}

$textStep=New-Object AppStudio.StepRecord
[AppStudio.Privacy]::ApplyValue($textStep,'recordText','ABC-123',$false,$null)
if($textStep.Value-ne'ABC-123'-or$textStep.ValueLength-ne7){throw 'an ordinary value was not recorded for replay'}

$lengthStep=New-Object AppStudio.StepRecord
[AppStudio.Privacy]::ApplyValue($lengthStep,'lengthOnly','ABC-123',$false,$null)
if($lengthStep.Value-or$lengthStep.ValueLength-ne7){throw 'the length-only policy did not keep the length and drop the text'}
if($lengthStep.Unavailable.Count-lt1){throw 'the length-only policy did not say the text is absent'}

$unknownStep=New-Object AppStudio.StepRecord
# PowerShell turns a bare $null into an empty string when it binds a string
# parameter, and an empty field is a different fact from an unreadable one.
[AppStudio.Privacy]::ApplyValue($unknownStep,'recordText',([NullString]::Value),$false,([NullString]::Value))
if($unknownStep.ValueKind-ne'unknown'-or$unknownStep.Unavailable.Count-lt1){throw 'an unreadable value was not stated as unreadable'}

foreach($policy in @('recordText','lengthOnly')){
 $statement=[AppStudio.Privacy]::PolicyStatement($policy)
 foreach($claim in @('No keyboard stream','Clipboard contents are never read')){
  if($statement-notmatch $claim){throw ('the policy statement dropped a claim under '+$policy)}
 }
}

# --- 5. a secret never reaches an output ---------------------------------
$session=[AppStudio.SessionStore]::Create($temp,'record','privacy fixture')
$session.ValuePolicy='recordText'
$secret='do-not-persist-2f7a9c'
$step=New-Object AppStudio.StepRecord
$step.Index=1;$step.At=[DateTimeOffset]::Now;$step.Kind='click';$step.AppName='Fixture';$step.WindowTitle='Fixture';$step.WindowClass='FixtureWindow'
[AppStudio.Privacy]::ApplyValue($step,'recordText',$secret,$true,'isPassword')
$session.Steps.Add($step)
$null=[AppStudio.SessionStore]::Append($session,'steps',$step.ToJson())
[AppStudio.SessionStore]::WriteMeta($session)
$null=[AppStudio.Outputs]::WriteAll($session,1048576)
foreach($file in Get-ChildItem $session.Folder -File -Recurse|Where-Object{$_.Extension-ne'.png'}){
 $text=[IO.File]::ReadAllText($file.FullName)
 if($text.Contains($secret)){throw ('a secret reached '+$file.Name)}
}
$markdown=[IO.File]::ReadAllText($session.SessionMdPath)
if($markdown-notmatch 'not recorded'){throw 'session.md did not state that the value is absent'}
if($markdown-notmatch 'ask the operator'){throw 'session.md did not say what a script has to do instead'}

Write-Output 'PASS test-privacy keyCaptureApis=0 watchList=fixed bareLetters=nonSemantic secretRules=isPassword+name+style valuePolicies=recordText/lengthOnly/unknown secretLeak=0'
}finally{Remove-Item $temp -Recurse -Force}
