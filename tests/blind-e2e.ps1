param(
    [ValidateSet('request', 'intake')]
    [string]$Phase = 'request',
    # One answer, or several when the assistant sent one module per message.
    # They are pasted into the same run of the application, one after another,
    # because that is what the operator does: the parts are collected while the
    # screen is open, and leaving the screen starts the intake again.
    [string[]]$Answer,
    [string]$Run,
    [string]$Request,
    [switch]$ExpectParts
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $forward = @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',$PSCommandPath,'-Phase',$Phase)
    if ($Answer) { $forward += @('-Answer', ($Answer -join ',')) }
    if ($Run) { $forward += @('-Run',$Run) }
    if ($Request) { $forward += @('-Request',$Request) }
    if ($ExpectParts) { $forward += '-ExpectParts' }
    & $windowsPowerShell @forward
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell phase failed: ' + $LASTEXITCODE) }
    return
}

# The independent-assistant end to end run.
#
# The point of this harness is that nothing here writes the answer. Phase one
# drives the real product until the operator would have a request on the
# clipboard, and puts that request and the two files it tells the assistant to
# read into one folder and nothing else. Somebody who has never seen this
# repository answers from that folder alone. Phase two drives the real product
# again and takes that answer in, exactly as pasting it would.
#
# The request id is not passed between the phases by this script. It is saved by
# the product into code.json when the request is made and read back when the
# session is opened again, which is also why a stale answer can still be caught
# after a restart.
#
#   powershell -File tests/blind-e2e.ps1 -Phase request
#   ...an independent assistant answers, its reply is saved verbatim...
#   powershell -File tests/blind-e2e.ps1 -Phase intake -Run <run> -Answer <file>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
$artifacts = Join-Path $root 'artifacts\blind-e2e'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

function All-Of($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Named($window, $controlType, $label) {
    foreach ($item in All-Of $window $controlType) { if ($item.Current.Name -eq $label) { return $item } }
    return $null
}
function Wait-Named($window, $controlType, $label, $timeoutMs) {
    $limit = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $limit) {
        $found = Find-Named $window $controlType $label
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
function Press($window, $label) {
    $button = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) $label 20000
    if ($null -eq $button) { throw ('there is no button called "' + $label + '" on screen') }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1000
    return $button
}
function Message([string]$name, [string]$fallback) {
    $path = Join-Path $root ('assets\messages\' + $name)
    if (Test-Path -LiteralPath $path) { return ([IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))).Trim() }
    return $fallback
}
function Editor-Text($window) {
    $box = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-editor-name.txt' 'The automation, as code') 20000
    if ($null -eq $box) { throw 'the code editor is not on screen' }
    return $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Screen-Text($window) {
    $texts = @()
    foreach ($item in All-Of $window ([System.Windows.Automation.ControlType]::Text)) { $texts += $item.Current.Name }
    return ($texts -join ' | ')
}
function Set-Board([string]$text) {
    for ($try = 0; $try -lt 10; $try++) {
        try { [Windows.Forms.Clipboard]::SetText($text); return } catch { Start-Sleep -Milliseconds 250 }
    }
    throw 'the clipboard would not take the answer'
}
function Get-Board() {
    for ($try = 0; $try -lt 10; $try++) {
        try { if ([Windows.Forms.Clipboard]::ContainsText()) { return [Windows.Forms.Clipboard]::GetText() } } catch { }
        Start-Sleep -Milliseconds 250
    }
    return ''
}
function Hash([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# A recording of nine numbered presses, so the answer has something ordinary to
# work on and the workflow has nine lines a person could ask about.
function Seed($root) {
    $session = [AppStudio.SessionStore]::Create($root, 'record', 'blind end to end fixture')
    $session.ValuePolicy = 'recordText'
    New-Item -ItemType Directory -Path $session.ShotsFolder -Force | Out-Null
    $shot = Join-Path $session.ShotsFolder 'S1.png'
    $bitmap = New-Object Drawing.Bitmap(480, 360)
    try {
        $g = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $g.Clear([Drawing.Color]::White)
            $font = New-Object Drawing.Font('Segoe UI', 14)
            for ($i = 1; $i -le 9; $i++) {
                $x = 30 + (($i - 1) % 3) * 130
                $y = 60 + [int](($i - 1) / 3) * 90
                $g.FillRectangle([Drawing.Brushes]::WhiteSmoke, $x, $y, 110, 70)
                $g.DrawRectangle([Drawing.Pens]::SteelBlue, $x, $y, 110, 70)
                $g.DrawString([string]$i, $font, [Drawing.Brushes]::Black, ($x + 46), ($y + 20))
            }
            $font.Dispose()
        } finally { $g.Dispose() }
        $bitmap.Save($shot, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }

    $screen = New-Object AppStudio.ScreenRecord
    $screen.ScanId = 'sc-1'; $screen.ScreenId = 'S1'; $screen.Title = 'Keypad'; $screen.ClassName = 'KeypadWindow'
    $screen.Rect = New-Object AppStudio.RectValue; $screen.Rect.Width = 480; $screen.Rect.Height = 360
    $screen.ShotFile = $shot; $screen.CapturedAt = [DateTimeOffset]::Now; $screen.CaptureMethod = 'BitBlt'
    $screen.Sha256 = Hash $shot
    $session.Screens.Screens.Add($screen)
    $null = [AppStudio.SessionStore]::Append($session, 'screens', $screen.ToJson())

    for ($index = 1; $index -le 9; $index++) {
        $node = New-Object AppStudio.ScanNode
        $node.NodeId = $index; $node.ScreenId = 'S1'; $node.Name = [string]$index; $node.ControlType = 'Button'
        $node.AutomationId = 'key' + $index; $node.ClassName = 'Button'; $node.CtrlId = 1000 + $index
        $node.Rect = New-Object AppStudio.RectValue
        $node.Rect.X = 30 + (($index - 1) % 3) * 130; $node.Rect.Y = 60 + [int](($index - 1) / 3) * 90
        $node.Rect.Width = 110; $node.Rect.Height = 70
        $session.Elements.Add($node)
        $null = [AppStudio.SessionStore]::Append($session, 'elements', [AppStudio.ScanJson]::Node($node, 'sc-1', 0))

        $step = New-Object AppStudio.StepRecord
        $step.Index = $index; $step.At = [DateTimeOffset]::Now; $step.OffsetMs = $index * 700; $step.GapMs = 250
        $step.Kind = 'click'; $step.AppName = 'keypad'; $step.WindowTitle = 'Keypad'; $step.WindowClass = 'KeypadWindow'
        $step.ElementLabel = 'Button "' + $index + '"'; $step.ControlType = 'Button'; $step.Confidence = 'high'
        $step.Rect = New-Object AppStudio.RectValue
        $step.Rect.X = $node.Rect.X; $step.Rect.Y = $node.Rect.Y; $step.Rect.Width = 110; $step.Rect.Height = 70
        $step.Point = New-Object AppStudio.PointValue
        $step.Point.X = $node.Rect.X + 55; $step.Point.Y = $node.Rect.Y + 35
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'uia.automationId'; $locator.AutomationId = 'key' + $index; $locator.ControlType = 'Button'; $locator.Confidence = 'high'
        $step.Locators.Add($locator)
        $locator = New-Object AppStudio.ElementLocator
        $locator.Strategy = 'win32.ctrlId'; $locator.CtrlId = 1000 + $index; $locator.ClassName = 'Button'; $locator.Confidence = 'medium'
        $step.Locators.Add($locator)
        $session.Steps.Add($step)
        $null = [AppStudio.SessionStore]::Append($session, 'steps', $step.ToJson())
    }
    [AppStudio.SessionStore]::WriteMeta($session)
    return $session
}

function Open-App() {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $app = Start-Process -FilePath $windowsPowerShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',(Join-Path $root 'app-studio.ps1'),'-AutoCloseMs','300000') -PassThru -WindowStyle Hidden
    $window = $null
    $limit = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -eq $window -and [DateTime]::UtcNow -lt $limit) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 300 }
    }
    if ($null -eq $window) { throw 'The App Studio window never appeared.' }
    Start-Sleep -Milliseconds 1800
    return @($app, $window)
}

# Opens the code screen of one named session. Which session matters: two runs of
# this harness leave two recordings behind, and taking an answer in against the
# wrong one is exactly the mistake the request id exists to catch - so the
# harness must not make it by accident and then report it as a finding.
function Enter-Code($window, [string]$sessionId) {
    $null = Press $window (Message 'compact-results.txt' 'Results')
    $list = (All-Of $window ([System.Windows.Automation.ControlType]::List))[0]
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    if ($items.Count -lt 1) { throw 'no session is in the list' }
    $chosen = $null
    if ($sessionId) {
        for ($index = 0; $index -lt $items.Count; $index++) {
            if ($items[$index].Current.Name.IndexOf($sessionId, [StringComparison]::Ordinal) -ge 0) { $chosen = $items[$index]; break }
        }
        if ($null -eq $chosen) { throw ('the session ' + $sessionId + ' is not in the list') }
    } else {
        $chosen = $items[0]
    }
    $chosen.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1600
    $null = Press $window (Message 'detail-code.txt' 'Edit as code')
    Start-Sleep -Milliseconds 1200
}

$app = $null
try {
    if ($Phase -eq 'request') {
        $session = Seed $root
        $runId = Split-Path $session.Folder -Leaf
        $runDir = Join-Path $artifacts $runId
        $inbox = Join-Path $runDir 'inbox'
        New-Item -ItemType Directory -Path $inbox -Force | Out-Null

        $opened = Open-App
        $app = $opened[0]; $window = $opened[1]
        Enter-Code $window $runId
        $before = Editor-Text $window
        if ($Request) {
            # What the operator typed into the box, exactly as they would have.
            $box = Wait-Named $window ([System.Windows.Automation.ControlType]::Edit) (Message 'code-ai-request-name.txt' 'What to ask for') 20000
            if ($null -eq $box) { throw 'the request box is not on screen' }
            $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Request)
            Start-Sleep -Milliseconds 600
        }
        $null = Press $window (Message 'code-ai-copy.txt' 'Copy the request')
        Start-Sleep -Milliseconds 2500
        $request = Get-Board
        if ($request.Length -lt 1000) { throw ('the request never reached the clipboard: ' + $request.Length) }
        if ($request -match '#@APPSTUDIO [0-9a-fA-F-]{36} REQUEST ') { throw 'the request is still numbered as one of several' }

        # Exactly what the operator would hand over, and nothing else.
        [IO.File]::WriteAllText((Join-Path $inbox 'PASTED-INTO-THE-CHAT.md'), $request, (New-Object Text.UTF8Encoding($false)))
        Copy-Item -LiteralPath $session.SessionMdPath -Destination (Join-Path $inbox 'session.md')
        Copy-Item -LiteralPath $session.ScreensPdfPath -Destination (Join-Path $inbox 'screens.pdf')
        [IO.File]::WriteAllText((Join-Path $runDir 'workflow-before.ps1'), $before, (New-Object Text.UTF8Encoding($false)))

        $manifest = @()
        $manifest += 'run=' + $runId
        $manifest += 'sessionFolder=' + $session.Folder
        foreach ($name in @('PASTED-INTO-THE-CHAT.md', 'session.md', 'screens.pdf')) {
            $path = Join-Path $inbox $name
            $manifest += ('sha256 ' + (Hash $path) + '  ' + $name + '  ' + (Get-Item $path).Length + ' bytes')
        }
        $manifest += 'workflowBeforeSha256=' + (Hash (Join-Path $runDir 'workflow-before.ps1'))
        [IO.File]::WriteAllLines((Join-Path $runDir 'inbox-manifest.txt'), $manifest, (New-Object Text.UTF8Encoding($false)))
        foreach ($line in $manifest) { Write-Output $line }
        Write-Output ('PASS blind-e2e phase=request run=' + $runId + ' inbox=' + $inbox + ' requestChars=' + $request.Length)
    }
    else {
        if (-not $Run) { throw 'which run this answer belongs to has to be said with -Run' }
        if (-not $Answer -or $Answer.Count -lt 1) { throw 'no raw answer file was given' }
        # -File hands array arguments across as one comma joined string, so a
        # list that arrived that way is put back into a list here.
        if ($Answer.Count -eq 1 -and $Answer[0].IndexOf(',') -ge 0) {
            $Answer = @($Answer[0].Split(',') | ForEach-Object { $_.Trim().Trim('"') })
        }
        foreach ($one in $Answer) { if (-not (Test-Path -LiteralPath $one)) { throw ('the raw answer file was not found: ' + $one) } }
        $runDir = Join-Path $artifacts $Run
        if (-not (Test-Path -LiteralPath $runDir)) { throw ('there is no run called ' + $Run) }

        $opened = Open-App
        $app = $opened[0]; $window = $opened[1]
        Enter-Code $window $Run
        $before = Editor-Text $window

        $result = @()
        $result += 'run=' + $Run
        $result += 'pastes=' + $Answer.Count
        $screen = ''
        $refused = $false
        $partial = $false
        $ready = $false
        $noChange = $false
        $lastHash = ''
        $resultPath = Join-Path $runDir ('intake-' + [IO.Path]::GetFileNameWithoutExtension($Answer[$Answer.Count - 1]) + '.txt')

        # Each message the assistant sent, pasted in turn into the same open
        # screen. The answer goes in exactly as it came back; nothing here edits
        # it.
        for ($which = 0; $which -lt $Answer.Count; $which++) {
            $one = $Answer[$which]
            $raw = [IO.File]::ReadAllText($one, (New-Object Text.UTF8Encoding($false)))
            $lastHash = Hash $one
            $result += '--- paste ' + ($which + 1) + ' of ' + $Answer.Count + ' ---'
            $result += 'answerFile=' + $one
            $result += 'answerSha256=' + $lastHash
            $result += 'answerChars=' + $raw.Length
            Set-Board $raw
            $null = Press $window (Message 'code-ai-paste.txt' 'Take the answer in from the clipboard')
            Start-Sleep -Milliseconds 1600
            $afterPaste = Editor-Text $window
            $screen = Screen-Text $window
            $result += 'editorUnchangedAtPaste=' + ($afterPaste -eq $before)
            $result += 'screenAfterPaste=' + $screen

            $refused = $screen.IndexOf((Message 'code-intake-refused.txt' 'not taken in'), [StringComparison]::Ordinal) -ge 0
            $partial = $screen.IndexOf((Message 'code-intake-partial.txt' 'Still needed'), [StringComparison]::Ordinal) -ge 0
            $ready = $screen.IndexOf((Message 'code-intake-ready.txt' 'ready to be looked at'), [StringComparison]::Ordinal) -ge 0
            $noChange = $false
            foreach ($name in @('code-nochange-unnecessary.txt', 'code-nochange-impossible.txt', 'code-nochange-unclear.txt')) {
                if ($screen.IndexOf((Message $name '@@none@@'), [StringComparison]::Ordinal) -ge 0) { $noChange = $true }
            }
            $result += 'refused=' + $refused
            $result += 'partAccepted=' + $partial
            $result += 'diffShown=' + $ready
            $result += 'answeredNoChange=' + $noChange
            # The evidence is written before anything is judged, so a run that
            # could not be classified still leaves what it saw behind.
            [IO.File]::WriteAllLines($resultPath, $result, (New-Object Text.UTF8Encoding($false)))
            if ($afterPaste -ne $before) { throw 'the answer reached the editor without being shown as a difference first' }
            if ($refused -or $noChange -or $ready) { break }
        }
        $rawHash = $lastHash

        # A refusal from the assistant is a result, not a failure: it says which
        # of the three it is and why, and nothing on screen changes.
        if ($noChange) {
            $result += 'outcome=assistantRefused'
            [IO.File]::WriteAllLines($resultPath, $result, (New-Object Text.UTF8Encoding($false)))
            foreach ($line in $result) { Write-Output $line }
            Write-Output ('PASS blind-e2e phase=intake run=' + $Run + ' outcome=assistantRefused answerSha256=' + $rawHash)
            return
        }

        if ($refused) {
            $result += 'outcome=notTakenIn'
            [IO.File]::WriteAllLines($resultPath, $result, (New-Object Text.UTF8Encoding($false)))
            foreach ($line in $result) { Write-Output $line }
            Write-Output ('FAIL blind-e2e phase=intake run=' + $Run + ' outcome=notTakenIn')
            exit 2
        }
        if ($partial -and -not $ready) {
            $result += 'outcome=partAccepted'
            [IO.File]::WriteAllLines($resultPath, $result, (New-Object Text.UTF8Encoding($false)))
            foreach ($line in $result) { Write-Output $line }
            Write-Output ('PASS blind-e2e phase=intake run=' + $Run + ' outcome=partAccepted (more parts needed)')
            return
        }
        if (-not $ready) { throw ('the paste produced neither a refusal nor a difference: ' + $screen) }

        $apply = Wait-Named $window ([System.Windows.Automation.ControlType]::Button) (Message 'code-diff-apply.txt' 'Take this in') 15000
        if ($null -eq $apply) { throw 'there is no way to accept the difference' }
        $apply.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1600
        $applied = Editor-Text $window
        $result += 'appliedChangedEditor=' + ($applied -ne $before)
        [IO.File]::WriteAllText((Join-Path $runDir 'workflow-after.ps1'), $applied, (New-Object Text.UTF8Encoding($false)))

        # What the product now holds, checked with the product's own checker.
        $null = Press $window (Message 'code-check.txt' 'Check')
        Start-Sleep -Milliseconds 4000
        $checkText = Screen-Text $window
        $result += 'checkSaid=' + $checkText

        $result += 'outcome=applied'
        [IO.File]::WriteAllLines((Join-Path $runDir ('intake-' + [IO.Path]::GetFileNameWithoutExtension($Answer) + '.txt')), $result, (New-Object Text.UTF8Encoding($false)))
        foreach ($line in $result) { Write-Output $line }
        Write-Output ('PASS blind-e2e phase=intake run=' + $Run + ' outcome=applied answerSha256=' + $rawHash)
    }
} finally {
    if ($null -ne $app) { try { $app.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 900; if (-not $app.HasExited) { $app.Kill() } } catch { } }
}
