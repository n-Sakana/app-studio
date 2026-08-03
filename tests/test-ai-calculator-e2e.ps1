param(
    [ValidateSet('request', 'answer')] [string]$Phase = 'request',
    [string]$State,
    [string]$Goal = '7 に 8 を足した答えを電卓の画面に出す',
    [string]$AnswerFile,
    [string]$Evidence,
    [int]$AliveMs = 3600000
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', $PSCommandPath, '-Phase', $Phase, '-Goal', $Goal, '-AliveMs', $AliveMs)
    if ($State) { $forward += @('-State', $State) }
    if ($AnswerFile) { $forward += @('-AnswerFile', $AnswerFile) }
    if ($Evidence) { $forward += @('-Evidence', $Evidence) }
    & $windowsPowerShell @forward
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# Drives the assistant route end to end against the Windows Calculator through
# App Studio's own window: scan the screen, build the two attachments, and later
# take an answer that was written somewhere else back in and run it.
#
# It runs in two phases because the answer is written by something outside this
# script. Phase "request" leaves App Studio and Calculator running and writes
# down what it started; phase "answer" attaches to those same processes.
#
# Only windows this script started are ever operated, and only those are ever
# closed. App Studio's own buttons are pressed through UI Automation, so the
# physical pointer is not moved; the only real input is what App Studio itself
# sends to Calculator, which is the thing being tested.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-ai-calculator-e2e (App Studio sends real input to Calculator; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
Add-Type -AssemblyName System.Windows.Forms

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
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Press($button, $label) {
    if ($null -eq $button) { throw ('Button not found: ' + $label) }
    ($button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 400
}
function Set-Edit($window, $name, $value) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        if ($item.Current.Name -eq $name) {
            ($item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)
            Start-Sleep -Milliseconds 300
            return $item
        }
    }
    return $null
}
function Read-Edit($window, $contains) {
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::Edit)) {
        try { $value = ($item.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $value = $null }
        if ($null -ne $value -and $value.Contains($contains)) { return $value }
    }
    return $null
}
function Window-ByPid($processId, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $found = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function List-TopWindows($namePattern) {
    $found = @()
    foreach ($item in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.Name -like $namePattern) { $found += $item }
    }
    return $found
}
function Window-ByHandle($handle, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        foreach ($item in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ([int64]$item.Current.NativeWindowHandle -eq [int64]$handle) { return $item }
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
# What the application itself shows. This is the independent check: it does not
# ask App Studio whether it succeeded, it asks Calculator what it displays.
function Calculator-Display($calculatorWindow) {
    if ($null -eq $calculatorWindow) { return $null }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CalculatorResults')
    $display = $calculatorWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $display) { return $null }
    return $display.Current.Name
}
function Shoot($handle, $path) {
    $rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$handle)
    if ($null -eq $rect -or $rect.Width -le 0) { return $null }
    $masks = New-Object 'AppStudio.MaskRect[]' 0
    $shot = [AppStudio.Capture]::Crop($rect, $masks, $path, [IntPtr][int64]$handle)
    if ($null -eq $shot -or $shot.Status.State -ne 'ok') { return $null }
    return $shot.File
}

if (-not $Evidence) { $Evidence = Join-Path $root 'artifacts\ai-e2e\default' }
New-Item -ItemType Directory -Path $Evidence -Force | Out-Null
# Variable names differ only by case in this language, so the path and the
# record it holds get names that cannot collide.
$statePath = $State
if (-not $statePath) { $statePath = Join-Path $Evidence 'state.json' }

# --------------------------------------------------------------- phase: request
if ($Phase -eq 'request') {
    $caseRoot = Join-Path $root 'runtime\cases'
    $casesBefore = @{}
    if (Test-Path -LiteralPath $caseRoot) { Get-ChildItem -LiteralPath $caseRoot -Directory | ForEach-Object { $casesBefore[$_.Name] = $true } }
    # Anything already on screen belongs to whoever is using the machine.
    $calculatorBefore = @()
    foreach ($item in (List-TopWindows '電卓')) { $calculatorBefore += [int64]$item.Current.NativeWindowHandle }
    foreach ($item in (List-TopWindows 'Calculator')) { $calculatorBefore += [int64]$item.Current.NativeWindowHandle }

    $calculator = Start-Process -FilePath 'calc.exe' -PassThru
    $calculatorWindow = $null
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $calculatorWindow -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in (@(List-TopWindows '電卓') + @(List-TopWindows 'Calculator'))) {
            if ($calculatorBefore -notcontains ([int64]$item.Current.NativeWindowHandle)) { $calculatorWindow = $item; break }
        }
        if ($null -eq $calculatorWindow) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $calculatorWindow) { throw 'Calculator did not open a window this script had not seen before, so there is nothing it may operate.' }
    $calculatorHandle = [int64]$calculatorWindow.Current.NativeWindowHandle
    $calculatorPid = $calculatorWindow.Current.ProcessId

    # App Studio parks itself against the right edge, and a point it covers is
    # refused rather than clicked. Calculator is moved to the left so the parts
    # the plan names are actually reachable.
    $bounds = $calculatorWindow.Current.BoundingRectangle
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $null = [AppStudio.WindowTools]::Move([IntPtr][int64]$calculatorHandle, ($work.Left + 24), ($work.Top + 24), [int]$bounds.Width, [int]$bounds.Height)
    Start-Sleep -Milliseconds 700

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-File', (Join-Path $root 'app-studio.ps1'), '-AutoCloseMs', $AliveMs) -PassThru -WindowStyle Hidden
    $window = Window-ByPid $app.Id 60000
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1500

    # 1. choose the Calculator window in the product's own target list
    $chosen = $null
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $chosen -and [DateTime]::UtcNow -lt $limit) {
        foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem)) {
            if ($item.Current.Name -like '*電卓*' -or $item.Current.Name -like '*Calculator*') { $chosen = $item; break }
        }
        if ($null -eq $chosen) {
            $refresh = Find-Button $window '一覧を更新'
            if ($null -ne $refresh) { Press $refresh '一覧を更新' }
            Start-Sleep -Milliseconds 500
        }
    }
    if ($null -eq $chosen) { throw 'The target list never offered Calculator.' }
    ($chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 250
    Press (Find-Button $window 'この画面を調べる') 'この画面を調べる'

    $displayBefore = Calculator-Display $calculatorWindow
    $beforeShot = Shoot $calculatorHandle (Join-Path $Evidence 'calculator-before.png')

    # 2. scan the screen that is showing now
    Press (Find-Button $window '自動でひととおり洗い出す') '自動でひととおり洗い出す'
    if ($null -eq (Wait-Button $window 'もう一度調べる' 180000)) { throw 'The Calculator scan never finished.' }
    Start-Sleep -Milliseconds 1500
    # The pictures of the scanned screens are taken right after the scan, so the
    # step is given time to finish before the ledger is read.
    Start-Sleep -Milliseconds 2500

    # 3. carry the result into the assistant route and write the goal
    Press (Find-Button $window 'この結果をAIに渡す') 'この結果をAIに渡す'
    if ($null -eq (Wait-Button $window '依頼文をコピーする' 60000)) { throw 'The request step did not appear.' }
    Start-Sleep -Milliseconds 2500
    if ($null -eq (Set-Edit $window 'やりたいこと（自由に書く）' $Goal)) { throw 'The goal box was not found.' }
    Press (Find-Button $window '依頼文をコピーする') '依頼文をコピーする'
    Start-Sleep -Milliseconds 2000

    $caseDir = $null
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $caseDir -and [DateTime]::UtcNow -lt $limit) {
        foreach ($directory in (Get-ChildItem -LiteralPath $caseRoot -Directory)) {
            if (-not $casesBefore.ContainsKey($directory.Name)) { $caseDir = $directory.FullName }
        }
        if ($null -eq $caseDir) { Start-Sleep -Milliseconds 400 }
    }
    if ($null -eq $caseDir) { throw 'No case folder was created.' }
    $handoffDir = Join-Path $caseDir 'handoff'
    $handoffText = Join-Path $handoffDir 'handoff.txt'
    $handoffPdf = Join-Path $handoffDir 'screens.pdf'
    foreach ($path in @($handoffText, $handoffPdf)) {
        if (-not (Test-Path -LiteralPath $path)) { throw ('The request did not produce the attachment ' + $path) }
    }
    $attached = @(Get-ChildItem -LiteralPath $handoffDir -File)
    if ($attached.Count -ne 2) { throw ('The attachment folder holds ' + $attached.Count + ' files, expected exactly 2.') }
    $handoffRecord = Get-Content -LiteralPath (Join-Path $caseDir 'handoff.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $screensLedger = Get-Content -LiteralPath (Join-Path $caseDir 'screens.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($screensLedger.shotCount -lt 1) { throw 'The scan recorded no picture of any screen.' }

    $stateData = [ordered]@{
        appPid = $app.Id
        calculatorPid = $calculatorPid
        calculatorHandle = $calculatorHandle
        calculatorStarted = $calculator.Id
        caseDir = $caseDir
        handoffDir = $handoffDir
        handoffText = $handoffText
        handoffPdf = $handoffPdf
        requestPath = (Join-Path $caseDir 'request.txt')
        bundleId = $handoffRecord.bundleId
        premiseHash = $handoffRecord.premiseHash
        textSha256 = $handoffRecord.textSha256
        pdfSha256 = $handoffRecord.pdfSha256
        pageCount = $handoffRecord.pageCount
        screenCount = @($screensLedger.screens).Count
        shotCount = $screensLedger.shotCount
        goal = $Goal
        displayBefore = $displayBefore
        beforeShot = $beforeShot
        evidence = $Evidence
    }
    ($stateData | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $statePath -Encoding UTF8
    Write-Output ('PHASE-REQUEST-OK case=' + (Split-Path -Leaf $caseDir) + ' bundle=' + $handoffRecord.bundleId +
        ' screens=' + @($screensLedger.screens).Count + ' pictures=' + $screensLedger.shotCount +
        ' pages=' + $handoffRecord.pageCount + ' textSha=' + $handoffRecord.textSha256.Substring(0, 16) +
        ' pdfSha=' + $handoffRecord.pdfSha256.Substring(0, 16) + ' display="' + $displayBefore + '"' +
        ' appPid=' + $app.Id + ' state=' + $statePath)
    return
}

# ---------------------------------------------------------------- phase: answer
if (-not (Test-Path -LiteralPath $statePath)) { throw ('No state file from the request phase: ' + $statePath) }
$stateData = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $AnswerFile -or -not (Test-Path -LiteralPath $AnswerFile)) { throw ('The answer file is missing: ' + $AnswerFile) }
# Read the answer as bytes and decode without touching it, so what is typed into
# the product is exactly what was written by whatever answered.
$answerBytes = [IO.File]::ReadAllBytes($AnswerFile)
$answerText = [Text.Encoding]::UTF8.GetString($answerBytes)
if ($answerText.Length -gt 0 -and [int]$answerText[0] -eq 0xFEFF) { $answerText = $answerText.Substring(1) }
$answerSha = (Get-FileHash -LiteralPath $AnswerFile -Algorithm SHA256).Hash

$window = Window-ByPid $stateData.appPid 30000
if ($null -eq $window) { throw ('App Studio (pid ' + $stateData.appPid + ') is no longer showing a window.') }
$calculatorWindow = Window-ByHandle $stateData.calculatorHandle 20000
if ($null -eq $calculatorWindow) { throw 'The Calculator window this run started is gone.' }

$importButton = Find-Button $window '回答を取り込む'
if ($null -ne $importButton) { Press $importButton '回答を取り込む' }
$box = Set-Edit $window '回答をそのまま貼る。前後に説明があってもよい。' $answerText
if ($null -eq $box) { throw 'The answer box was not found.' }
$typed = ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
$typedSha = [BitConverter]::ToString((New-Object Security.Cryptography.SHA256Managed).ComputeHash([Text.Encoding]::UTF8.GetBytes($typed))).Replace('-', '')
Press (Find-Button $window '貼った内容を読み取る') '貼った内容を読み取る'
Start-Sleep -Milliseconds 900

$runButton = Find-Button $window 'この内容で実行する'
if ($null -eq $runButton) { throw 'The run button is missing.' }
$acceptedText = Read-Edit $window '1. '
if (-not $runButton.Current.IsEnabled) {
    # A plan that changes the target needs the permission switch first. Anything
    # else means the answer itself was refused, and that is reported as it is.
    $writeToggle = $null
    foreach ($item in Find-Descendants $window ([System.Windows.Automation.ControlType]::CheckBox)) {
        if ($item.Current.Name -eq 'このセッションで対象を変える操作を許可する') { $writeToggle = $item }
    }
    if ($null -eq $writeToggle) { throw 'The write permission switch is not on the import step.' }
    if ($writeToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        ($writeToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
        Start-Sleep -Milliseconds 400
    }
    $runButton = Find-Button $window 'この内容で実行する'
}
if (-not $runButton.Current.IsEnabled) {
    $reason = Read-Edit $window '読み取れなかった理由'
    if ($null -eq $reason) { $reason = Read-Edit $window '' }
    throw ('The answer was not runnable. What the product said: ' + $reason)
}
Press $runButton 'この内容で実行する'
if ($null -eq (Wait-Button $window '案件フォルダを開く' 180000)) { throw 'The run never reached its result step.' }
Start-Sleep -Milliseconds 1200

$displayAfter = Calculator-Display $calculatorWindow
$afterShot = Shoot $stateData.calculatorHandle (Join-Path $stateData.evidence 'calculator-after.png')
$caseDir = $stateData.caseDir
$runFiles = @(Get-ChildItem -LiteralPath $caseDir -Filter 'run-*.jsonl' | Sort-Object Name)
if ($runFiles.Count -lt 1) { throw 'The run wrote no result file.' }
$runLines = @(Get-Content -LiteralPath $runFiles[$runFiles.Count - 1].FullName -Encoding UTF8 | ForEach-Object { ConvertFrom-Json $_ })
$stepLines = @($runLines | Where-Object { $_.kind -eq 'plan.step' })
$runLine = @($runLines | Where-Object { $_.kind -eq 'plan.run' })[0]
$answerFiles = @(Get-ChildItem -LiteralPath $caseDir -Filter 'answer-*.txt' | Sort-Object Name)
$storedAnswer = $answerFiles[$answerFiles.Count - 1].FullName
$storedSha = (Get-FileHash -LiteralPath $storedAnswer -Algorithm SHA256).Hash

$result = [ordered]@{
    caseDir = $caseDir
    answerFile = $AnswerFile
    answerSha256 = $answerSha
    typedSha256 = $typedSha
    storedAnswerFile = $storedAnswer
    storedAnswerSha256 = $storedSha
    displayBefore = $stateData.displayBefore
    displayAfter = $displayAfter
    beforeShot = $stateData.beforeShot
    afterShot = $afterShot
    steps = @($stepLines | ForEach-Object { [ordered]@{ id = $_.stepId; action = $_.action; target = $_.elementId; outcome = $_.outcome; method = $_.method; identity = $_.usedIdentity; reaction = $_.reaction } })
    summary = [ordered]@{ success = $runLine.success; failed = $runLine.failed; blocked = $runLine.blocked; notSupported = $runLine.notSupported; unknown = $runLine.unknown; skipped = $runLine.skipped; writeEnabled = $runLine.writeEnabled }
    planTitle = $runLine.title
}
$resultPath = Join-Path $stateData.evidence 'result.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Output ('PHASE-ANSWER-OK case=' + (Split-Path -Leaf $caseDir) +
    ' answerSha=' + $answerSha + ' storedSha=' + $storedSha + ' typedSha=' + $typedSha +
    ' display="' + $stateData.displayBefore + '" -> "' + $displayAfter + '"' +
    ' steps=' + $stepLines.Count + ' outcomes=' + (@($stepLines | ForEach-Object { $_.outcome }) -join ',') +
    ' result=' + $resultPath)
