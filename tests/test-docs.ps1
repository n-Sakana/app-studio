$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$requirements=@{
 'SPEC.md'=@('powershell.exe','maskedOnly','UIA-EMPTYTREE','NEEDS-B3','PACK-WRITE','RuntimeId');
 'DEVELOPMENT.md'=@('C# 5.0','run-all.ps1','schemaVersion','test-hang-recovery','run-wp-s.ps1');
 'ONSITE.md'=@('launch.vbs','EDR/DLP','AutomationId','targetRunId','writeTargets','MANIFEST.json')
}
foreach($name in $requirements.Keys){$path=Join-Path $root ('docs\'+$name);if(-not(Test-Path $path)){throw ($name+' missing')};$text=[IO.File]::ReadAllText($path,(New-Object Text.UTF8Encoding($false)));foreach($term in $requirements[$name]){if(-not$text.Contains($term)){throw ($name+' missing term '+$term)}}}
foreach($path in @('launch.vbs','launch.bat','app-studio.ps1','app-studio-worker.ps1','tests\run-all.ps1','tests\wp-s\run-wp-s.ps1')){if(-not(Test-Path (Join-Path $root $path))){throw ('documented path missing: '+$path)}}
foreach($path in @('README.md','LICENSE')){if(-not(Test-Path (Join-Path $root $path))){throw ('required file missing: '+$path)}}
$license=[IO.File]::ReadAllText((Join-Path $root 'LICENSE'),(New-Object Text.UTF8Encoding($false)));if(-not$license.Contains('CC0 1.0 Universal')){throw 'LICENSE is not CC0 1.0'}
Write-Output 'PASS test-docs files=3 documentedCommands=present license=CC0-1.0'
