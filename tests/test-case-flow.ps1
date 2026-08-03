$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the whole case flow through the product window: investigate, build the
# request, take an answer back in, run it on a fixture and check what was
# written down. The buttons and the two text boxes are driven through UI
# Automation, so the physical pointer is never moved and no key is ever sent to
# anything on the desktop. The only application operated is the fixture.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-case-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$fixture = $null
$app = $null
$caseRoot = Join-Path $root 'runtime\cases'
$before = @{}
if (Test-Path -LiteralPath $caseRoot) { Get-ChildItem -LiteralPath $caseRoot -Directory | ForEach-Object { $before[$_.Name] = $true } }

function Find-Descendants($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Button($window, $label) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Button)) {
        if ($item.Current.Name -eq $label -and -not $item.Current.IsOffscreen) { return $item }
    }
    return $null
}
function Wait-Button($window, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $button = Find-Button $window $label
        if ($null -ne $button) { return $button }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Press($button, $label) {
    if ($null -eq $button) { throw ('Button not found: ' + $label) }
    ($button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 350
}
function Set-Edit($window, $name, $value) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        if ($item.Current.Name -eq $name) {
            ($item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)
            Start-Sleep -Milliseconds 250
            return $true
        }
    }
    return $false
}
function Text-Of($element) {
    try { return ($element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { return $null }
}
# The fixture is checked through its own window handles rather than through the
# same accessibility route the tool used, so a step that only claims to have
# worked cannot make this test pass.
if ($null -eq ('CaseFlowNative' -as [type])) {
    Add-Type -Namespace 'PuiTest' -Name 'CaseFlowNative' -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wparam, System.Text.StringBuilder lparam, uint flags, uint timeout, out IntPtr result);
'@
}
function Get-ControlText($handle) {
    $builder = New-Object System.Text.StringBuilder 1024
    $answer = [IntPtr]::Zero
    $call = [PuiTest.CaseFlowNative]::SendMessageTimeoutW([IntPtr][int64]$handle, 0x000D, [IntPtr]1024, $builder, 0x0002, 2000, [ref]$answer)
    if ($call -eq [IntPtr]::Zero) { throw ('WM_GETTEXT did not return for handle ' + $handle) }
    return $builder.ToString()
}
function Read-Edit($window, $contains) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        $value = Text-Of $item
        if ($null -ne $value -and $value.Contains($contains)) { return $value }
    }
    return $null
}

try {
    $ready = Join-Path $tempDir 'ready.json'
    $fixture = Start-Process -FilePath $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 25 }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'FixtureWinForms did not become ready.' }
    # App Studio parks itself against the right edge. The fixture is moved to
    # the left before anything is scanned so that the recorded coordinates are
    # never under the tool's own window, which would make every operation on
    # them refused as covered rather than actually tried.
    $fixtureWindows = [AppStudio.WindowTools]::ListProcessWindows($fixture.Id, 0)
    if ($fixtureWindows.Count -lt 1) { throw 'The fixture window was not found.' }
    $bounds = $fixtureWindows[0].Rect
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$fixtureWindows[0].Hwnd, ($work.Left + 20), ($work.Top + 20), $bounds.Width, $bounds.Height)
    Start-Sleep -Milliseconds 400

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', (Join-Path $root 'app-studio.ps1'), '-AutoCloseMs', '300000') -PassThru -WindowStyle Hidden
    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1200

    # 1. choose the fixture, 2. scan it.
    $chosen = $null
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ($null -eq $chosen -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if ($item.Current.Name -like '*FixtureWinForms*') { $chosen = $item; break }
        }
        if ($null -eq $chosen) {
            $refresh = Find-Button $window '一覧を更新'
            if ($null -ne $refresh) { Press $refresh '一覧を更新' }
            Start-Sleep -Milliseconds 400
        }
    }
    if ($null -eq $chosen) { throw 'The fixture window was not offered in the target list.' }
    ($chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 200
    Press (Find-Button $window 'この画面を調べる') 'この画面を調べる'
    # Each start button carries the name of its own purpose, so the menu is checked
    # by asking for all four purposes by name rather than counting identical buttons.
    foreach ($purpose in @('自動でひととおり洗い出す', '自分で操作しながら記録する', '部品をひとつ試しに操作する', 'AIに操作を考えてもらう')) {
        if ($null -eq (Find-Button $window $purpose)) { throw ('The menu is missing the purpose: ' + $purpose) }
    }
    Press (Find-Button $window '自動でひととおり洗い出す') '自動でひととおり洗い出す'
    if ($null -eq (Wait-Button $window 'もう一度調べる' 120000)) { throw 'The scan never reached its result step.' }
    Start-Sleep -Milliseconds 800

    # 3. hand the result to the assistant flow. The screenshot is taken here.
    Press (Find-Button $window 'この結果をAIに渡す') 'この結果をAIに渡す'
    if ($null -eq (Wait-Button $window '依頼文をコピーする' 20000)) { throw 'The request step did not appear.' }
    Start-Sleep -Milliseconds 1500

    $caseDir = $null
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ($null -eq $caseDir -and [DateTime]::UtcNow -lt $limit) {
        if (Test-Path -LiteralPath $caseRoot) {
            foreach ($directory in (Get-ChildItem -LiteralPath $caseRoot -Directory)) {
                if (-not $before.ContainsKey($directory.Name)) { $caseDir = $directory.FullName }
            }
        }
        if ($null -eq $caseDir) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $caseDir) { throw 'No case folder was created.' }
    $shots = @(Get-ChildItem -LiteralPath (Join-Path $caseDir 'shots') -Filter '*.png' -ErrorAction SilentlyContinue)
    if ($shots.Count -lt 1) { throw 'The case took no screenshot of the target.' }
    if ($shots[0].Length -lt 2000) { throw ('The screenshot is suspiciously small: ' + $shots[0].Length) }

    # 4. the free text goal, then 5. the request text.
    if (-not (Set-Edit $window 'やりたいこと（自由に書く）' '顧客コード欄にTEST-777と入れて保存ボタンを押す')) { throw 'The goal box was not found.' }
    Press (Find-Button $window '依頼文をコピーする') '依頼文をコピーする'
    Start-Sleep -Milliseconds 900
    foreach ($name in @('investigation.md', 'request.txt', 'elements.json', 'case.md', 'case.json', 'screens.json', 'handoff.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $caseDir $name))) { throw ('The request did not write ' + $name) }
    }
    # The two files that are actually attached to the chat message, in a folder
    # of their own so "attach these" has one answer.
    $handoffDir = Join-Path $caseDir 'handoff'
    $handoffText = Join-Path $handoffDir 'handoff.txt'
    $handoffPdf = Join-Path $handoffDir 'screens.pdf'
    foreach ($path in @($handoffText, $handoffPdf)) {
        if (-not (Test-Path -LiteralPath $path)) { throw ('The request did not write the attachment ' + $path) }
    }
    $attached = @(Get-ChildItem -LiteralPath $handoffDir -File)
    if ($attached.Count -ne 2) { throw ('The attachment folder holds ' + $attached.Count + ' files, expected exactly 2.') }
    $handoffRecord = Get-Content -LiteralPath (Join-Path $caseDir 'handoff.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]::IsNullOrEmpty($handoffRecord.bundleId)) { throw 'The handoff kept no bundle id.' }
    if ([string]::IsNullOrEmpty($handoffRecord.premiseHash)) { throw 'The handoff kept no premise for the answer to match.' }
    if ($handoffRecord.pageCount -lt 1) { throw 'The picture document has no pages.' }
    if ((Get-FileHash -LiteralPath $handoffPdf -Algorithm SHA256).Hash -ne $handoffRecord.pdfSha256) { throw 'The recorded picture hash does not match the file.' }
    if ((Get-FileHash -LiteralPath $handoffText -Algorithm SHA256).Hash -ne $handoffRecord.textSha256) { throw 'The recorded text hash does not match the file.' }
    $pdfHead = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($handoffPdf))
    if (-not $pdfHead.StartsWith('%PDF-')) { throw 'The picture attachment is not a document.' }
    if (-not $pdfHead.Contains('Screen S1')) { throw 'The picture attachment does not key its pages to a screen id.' }
    $handoffBody = [IO.File]::ReadAllText($handoffText, [Text.Encoding]::UTF8)
    if ($handoffBody -notmatch '\|\s*S1\s*\|\s*1\s*\|') { throw 'The text attachment does not say which page shows S1.' }
    if ($handoffBody -notmatch '\|\s*E\d+\s*\|\s*S1\s*\|') { throw 'The text attachment does not put components on their screen.' }
    $screensLedger = Get-Content -LiteralPath (Join-Path $caseDir 'screens.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($screensLedger.screens).Count -lt 1) { throw 'The screen ledger is empty.' }
    if ($screensLedger.shotCount -lt 1) { throw 'No picture was taken of any scanned screen.' }
    $requestText = [IO.File]::ReadAllText((Join-Path $caseDir 'request.txt'), [Text.Encoding]::UTF8)
    foreach ($term in @('pui-plan', 'setValue', '"element"', '"point"', 'TEST-777')) {
        if (-not $requestText.Contains($term)) { throw ('The request text is missing: ' + $term) }
    }
    $investigation = [IO.File]::ReadAllText((Join-Path $caseDir 'investigation.md'), [Text.Encoding]::UTF8)
    if ($investigation -notmatch '\|\s*E\d+\s*\|') { throw 'The investigation has no quotable element ids.' }
    if (-not $investigation.Contains('取得できなかったもの')) { throw 'The investigation does not say what was missed.' }

    # Pick real ids out of the table the assistant would have been given.
    $elements = (Get-Content -LiteralPath (Join-Path $caseDir 'elements.json') -Raw -Encoding UTF8 | ConvertFrom-Json).elements
    $codeBox = $elements | Where-Object { $_.listed -and $_.automationId -eq 'CustomerCode' } | Select-Object -First 1
    $saveButton = $elements | Where-Object { $_.listed -and $_.automationId -eq 'FirstSave' } | Select-Object -First 1
    $passwordBox = $elements | Where-Object { $_.automationId -eq 'PasswordField' } | Select-Object -First 1
    if ($null -eq $codeBox -or $null -eq $saveButton) { throw 'The scan did not offer the fixture edit and button as addressable parts.' }

    # 6/7. a bad answer must be refused outright, and must run nothing.
    Press (Find-Button $window '回答を取り込む') '回答を取り込む'
    if (-not (Set-Edit $window '回答をそのまま貼る。前後に説明があってもよい。' '{"format":"pui-plan","version":1,"steps":[{"id":1,"action":"teleport","target":{"element":"E999999"}}]}')) { throw 'The answer box was not found.' }
    Press (Find-Button $window '貼った内容を読み取る') '貼った内容を読み取る'
    Start-Sleep -Milliseconds 500
    $rejected = Read-Edit $window '読み取れなかった理由'
    if ($null -eq $rejected) { throw 'A bad answer was not refused.' }
    if (-not $rejected.Contains('知らない action')) { throw ('The refusal does not name the problem: ' + $rejected) }
    $runButton = Find-Button $window 'この内容で実行する'
    if ($null -eq $runButton) { throw 'The run button vanished instead of staying disabled.' }
    if ($runButton.Current.IsEnabled) { throw 'A refused answer left the run button enabled.' }
    if (Test-Path -LiteralPath (Join-Path $caseDir 'run-01.jsonl')) { throw 'A refused answer still ran something.' }

    # A good answer: type into the fixture edit, then press its Save button.
    $answer = '説明の文がここにあってもよい。' + "`n" + '```json' + "`n" +
        '{ "format": "pui-plan", "version": 1, "title": "顧客コードを入れて保存する", "notes": "n",' +
        ' "steps": [' +
        ' { "id": 1, "action": "setValue", "target": { "element": "' + $codeBox.id + '" }, "value": "TEST-777", "expect": "顧客コード欄が変わる", "why": "ValuePatternあり" },' +
        ' { "id": 2, "action": "invoke", "target": { "element": "' + $saveButton.id + '" }, "expect": "保存ボタンの表示が変わる" },' +
        ' { "id": 3, "action": "read", "target": { "element": "' + $saveButton.id + '" }, "expect": "変化後の表示を読む", "extraField": "ignored" } ] }' + "`n" +
        '```' + "`n" + 'この手順でどうでしょうか。'
    if (-not (Set-Edit $window '回答をそのまま貼る。前後に説明があってもよい。' $answer)) { throw 'The answer box was not found on the retry.' }
    Press (Find-Button $window '貼った内容を読み取る') '貼った内容を読み取る'
    Start-Sleep -Milliseconds 500
    # Matched on the rendered wording, not on anything that is also sitting in
    # the paste box, so this cannot pass by reading the answer back to itself.
    $planText = Read-Edit $window '期待: 顧客コード欄が変わる'
    if ($null -eq $planText) { throw 'The accepted plan was not shown for checking.' }
    if (-not $planText.Contains('顧客コードを入れて保存する')) { throw 'The shown plan lost the title.' }
    foreach ($term in @('1. setValue', '2. invoke', '3. read', 'TEST-777')) {
        if (-not $planText.Contains($term)) { throw ('The shown plan is missing: ' + $term) }
    }
    if (-not $planText.Contains('extraField')) { throw 'An unused field in the answer was dropped without saying so.' }

    # The run must stay refused until the operator allows changing operations.
    $runButton = Find-Button $window 'この内容で実行する'
    if ($runButton.Current.IsEnabled) { throw 'A changing plan could be run without permission being given.' }
    $writeToggle = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::CheckBox)) {
        if ($item.Current.Name -eq 'このセッションで対象を変える操作を許可する') { $writeToggle = $item }
    }
    if ($null -eq $writeToggle) { throw 'The write permission switch is not on the import step.' }
    ($writeToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Start-Sleep -Milliseconds 300
    $runButton = Find-Button $window 'この内容で実行する'
    if (-not $runButton.Current.IsEnabled) { throw 'Permission was given but the run stayed refused.' }

    # 8. run it on the fixture and check the fixture really reacted.
    Press $runButton 'この内容で実行する'
    if ($null -eq (Wait-Button $window '案件フォルダを開く' 90000)) { throw 'The run never reached its result step.' }
    Start-Sleep -Milliseconds 600
    $runPath = Join-Path $caseDir 'run-01.jsonl'
    if (-not (Test-Path -LiteralPath $runPath)) { throw 'The run wrote no record.' }
    $runLines = @(Get-Content -LiteralPath $runPath -Encoding UTF8 | ForEach-Object { ConvertFrom-Json $_ })
    $stepRecords = @($runLines | Where-Object { $_.kind -eq 'plan.step' })
    $summary = @($runLines | Where-Object { $_.kind -eq 'plan.run' })
    if ($stepRecords.Count -ne 3) { throw ('Expected three step records, got ' + $stepRecords.Count) }
    if ($summary.Count -ne 1) { throw 'The run wrote no summary.' }
    if (-not $summary[0].writeEnabled) { throw 'The run recorded the wrong permission.' }
    foreach ($record in $stepRecords) {
        if (@('success', 'failed', 'blocked', 'notSupported', 'unknown') -notcontains $record.outcome) { throw ('Unknown outcome value: ' + $record.outcome) }
        if ([string]::IsNullOrEmpty($record.method)) { throw ('A step kept no route: ' + $record.stepId) }
        if ([string]::IsNullOrEmpty($record.usedIdentity)) { throw ('A step kept no identifying material: ' + $record.stepId) }
    }
    $setValueStep = $stepRecords | Where-Object { $_.action -eq 'setValue' } | Select-Object -First 1
    if ($setValueStep.outcome -ne 'success') { throw ('setValue on the fixture did not succeed: ' + $setValueStep.outcome + ' via ' + $setValueStep.method) }

    # The fixture itself is the proof, not the tool's own report.
    $handles = Get-Content -LiteralPath $ready -Raw -Encoding UTF8 | ConvertFrom-Json
    $codeValue = Get-ControlText $handles.normal
    if ($codeValue -ne 'TEST-777') { throw ('The fixture edit was not actually changed: "' + $codeValue + '"') }
    $savedLabel = Get-ControlText $handles.first
    if ($savedLabel -notlike 'Saved*') { throw ('The fixture button was not actually pressed: "' + $savedLabel + '"') }
    $passwordValue = Get-ControlText $handles.password
    if ($passwordValue -ne 'P@ssword123') { throw ('The password field was touched: "' + $passwordValue + '"') }

    # 9. the written record must tie it all together.
    $caseText = [IO.File]::ReadAllText((Join-Path $caseDir 'case.md'), [Text.Encoding]::UTF8)
    foreach ($term in @('スクリーンショット', 'やりたいこと', 'TEST-777', '試す操作', '操作試験', '使えた識別情報', 'AutomationId=CustomerCode')) {
        if (-not $caseText.Contains($term)) { throw ('The case record is missing: ' + $term) }
    }
    $index = Get-Content -LiteralPath (Join-Path $caseDir 'case.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($index.status -ne 'ran') { throw ('The case status was not updated: ' + $index.status) }
    if ($index.runCount -ne 1) { throw ('The case run count is wrong: ' + $index.runCount) }
    if ($index.stepCount -ne 3) { throw ('The case step count is wrong: ' + $index.stepCount) }

    # The history screen has to find it again.
    Press (Find-Button $window 'これまでの記録を見る') 'これまでの記録を見る'
    Start-Sleep -Milliseconds 700
    $historyRow = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
        if ($item.Current.Name -like ('*' + (Split-Path -Leaf $caseDir) + '*')) { $historyRow = $item }
    }
    if ($null -eq $historyRow) { throw 'The new case is not in the history list.' }
    ($historyRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 600
    if ($null -eq (Read-Edit $window '操作試験')) { throw 'Selecting a case in the history showed no record.' }

    $passwordNote = if ($null -eq $passwordBox) { 'absent' } else { 'present' }
    ($window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    Start-Sleep -Milliseconds 800

    Write-Output ('PASS test-case-flow steps=target,scan,request,reject,import,run,history case=' + (Split-Path -Leaf $caseDir) +
        ' shot=' + $shots[0].Length + 'B elements=' + @($elements).Count +
        ' setValue=' + $setValueStep.outcome + '/' + $setValueStep.method +
        ' fixtureEdit="' + $codeValue + '" fixtureButton="' + $savedLabel + '"' +
        ' refusedBadAnswer=yes writeGate=enforced passwordField=' + $passwordNote)
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit() }
    if ($null -ne $fixture -and -not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
    Remove-Item -LiteralPath $tempDir -Recurse -Force
}
