$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition-eq'Core'){$ps5=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';&$ps5 -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath;if($LASTEXITCODE-ne0){throw ('Windows PowerShell test failed: '+$LASTEXITCODE)};return}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path;&(Join-Path $root 'app-studio.ps1') -CompileOnly
$temp=Join-Path ([IO.Path]::GetTempPath()) ('pui-schema-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
try{
 $recorder=New-Object AppStudio.SessionRecorder((Join-Path $temp 'shots'));$recorder.Data.Label='Schema fixture';$record=New-Object AppStudio.ElementRecord;$record.ElementId='el-0001';$record.PinnedAt=[DateTimeOffset]::Now;$record.Label='Customer code';$record.RecordedValue=New-Object AppStudio.RecordedValue;$record.RecordedValue.Length=12;$record.RecordedValue.Kind='string';$record.RecordedValue.Masked=$true;$record.RecordedValue.MaskRule='policy.maskedOnly'
 $record.Win32=New-Object AppStudio.Win32Info;$record.Win32.Status=[AppStudio.ProbeStatus]::Ok();$record.Win32.ClassName='Edit';$record.Win32.Caption='Customer';$record.Win32.CaptionSource='WM_GETTEXT';$record.Win32.CtrlId=1002;$record.Win32.WindowRect=New-Object AppStudio.RectValue
 $record.Uia=New-Object AppStudio.UiaInfo;$record.Uia.Status=[AppStudio.ProbeStatus]::Ok();$record.Uia.Name='Customer code';$record.Uia.AutomationId='CustomerCode';$record.Uia.ControlType='Edit';$record.Uia.SupportedPatterns=@('Value');$record.Uia.TreePath=@();$record.Uia.Children=@()
 $recorder.Data.Elements.Add($record);$recorder.AddEvent('custom.future','tool','unknown event types pass through')
 $json=[AppStudio.JsonWriter]::Write($recorder.ToJson());$path=Join-Path $temp 'session.json';[IO.File]::WriteAllText($path,$json,(New-Object Text.UTF8Encoding($false)));$data=$json|ConvertFrom-Json
 $required=@('schemaVersion','tool','session','environment','policy','targets','elements','events','shots','acquisitionFailures');$actualKeys=@($data.PSObject.Properties|ForEach-Object{$_.Name});if(($actualKeys-join',')-ne($required-join',')){throw ('top-level key order mismatch: '+($actualKeys-join','))}
 if($data.schemaVersion-ne1){throw 'schemaVersion changed without an explicit migration'};if($data.policy.valueCapture-ne'maskedOnly'){throw 'maskedOnly is not the default'}
 foreach($key in @('os','user','process','dotnet','uia','monitors','webview2','powershell','appLockerPolicyPresent','hotkeys','writeTargets')){if($null-eq$data.environment.$key){throw ('environment field missing: '+$key)}}
 if($data.environment.os.caption.PSObject.Properties.Name-notcontains'value'-or$data.environment.os.caption.PSObject.Properties.Name-notcontains'reason'){throw 'unknown environment value does not use value/reason'}
 if($data.session.startedAt-notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}$'){throw ('timestamp format mismatch: '+$data.session.startedAt)}
 $element=$data.elements[0];foreach($key in @('elementId','pinnedAt','targetId','targetRunId','label','notes','win32','uia','locators','probes','shots','acquisitionSummary')){if($element.PSObject.Properties.Name-notcontains$key){throw ('element field missing: '+$key)}}
 $recorded=$element.uia.patterns.values.value;foreach($key in @('length','kind','masked','maskRule')){if($recorded.PSObject.Properties.Name-notcontains$key){throw ('RecordedValue field missing: '+$key)}};if($recorded.content){throw 'maskedOnly content persisted'}
 if(@($data.events|Where-Object{$_.type-eq'custom.future' -and $_.source-eq'tool'}).Count-ne1){throw 'open event type/source was not retained'}
 Write-Output ('PASS test-schema topLevelKeys='+$required.Count+' environmentKeys=11 elementKeys=13 timestamp=offset-ms schemaVersion=1 valueCapture=maskedOnly eventSource=present')
}finally{Remove-Item $temp -Recurse -Force}
