$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The report in every state it can be in: everything worked, some of it did not,
# most of it did not, nothing was recorded, and everything has a very long name.
#
# What is checked is the information design, not the wording. The conclusion has
# to come first, every section has to be one summary line and one fold, and a
# fold must never contain another fold - a reader who opens something has to
# find the answer, not more things to open.
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-states-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
# Written where a person can open them, because whether a page reads well is not
# something an assertion can answer.
$shots = Join-Path $root 'runtime\report-states'
New-Item -ItemType Directory -Path $shots -Force | Out-Null

# A picture has to be on disk, because that is what the product means by
# "this screen has a picture".
function New-Picture($id) {
    $path = Join-Path $temp ($id + '.png')
    $bitmap = New-Object Drawing.Bitmap(120, 90)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([Drawing.Color]::FromArgb(240, 244, 248)) } finally { $graphics.Dispose() }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return $path
}
function New-Rect($x, $y, $w, $h) {
    $rect = New-Object AppStudio.RectValue
    $rect.X = $x; $rect.Y = $y; $rect.Width = $w; $rect.Height = $h
    return $rect
}
function New-Node($id, $name, $automationId, $screen) {
    $node = New-Object AppStudio.ScanNode
    $node.NodeId = $id; $node.ScreenId = $screen; $node.Name = $name; $node.AutomationId = $automationId
    $node.ControlType = 'Button'; $node.ClassName = 'Button'; $node.CtrlId = 1000 + $id
    $node.Path = 'Window > Button "' + $name + '"'
    $node.Rect = New-Rect (10 + $id) 10 60 24
    $node.AddSource('uia')
    return $node
}
function New-Screen($id, $title, $withShot) {
    $screen = New-Object AppStudio.ScreenRecord
    $screen.ScanId = 's'; $screen.ScreenId = $id; $screen.Title = $title; $screen.ClassName = 'FixtureWindow'
    $screen.Rect = New-Rect 0 0 800 600
    if (-not $withShot) { $screen.ShotProblem = 'SHOT-FAILED: the window was covered when the shutter fired.' }
    else { $screen.ShotFile = New-Picture $id }
    return $screen
}
function New-Step($index, $kind, $node, $window) {
    $step = New-Object AppStudio.StepRecord
    $step.Index = $index; $step.At = [DateTimeOffset]::Now; $step.OffsetMs = $index * 1200; $step.GapMs = 900
    $step.Kind = $kind; $step.AppName = 'FixtureApp'; $step.WindowTitle = 'Fixture window'; $step.WindowClass = 'FixtureWindow'
    $step.Button = 'left'
    $step.Point = New-Object AppStudio.PointValue; $step.Point.X = 100 + $index; $step.Point.Y = 200
    $step.Dpi = 96; $step.MonitorId = 'monitor-1'
    if ($null -ne $node) {
        $step.ElementLabel = 'Button "' + $node.Name + '"'
        $siblings = New-Object 'System.Collections.Generic.List[AppStudio.ScanNode]'
        $siblings.Add($node)
        $step.Locators = [AppStudio.LocatorBuilder]::Build($node, $window, $siblings)
        $step.Confidence = [AppStudio.LocatorBuilder]::BestConfidence($step.Locators)
    }
    $step.EffectSummary = 'the button reported that it was pressed'
    return $step
}
function New-Session($name, $kind) {
    $session = [AppStudio.SessionStore]::Create($temp, $kind, $name)
    $session.ValuePolicy = 'recordText'
    return $session
}
function Render($session, $name) {
    $result = New-Object AppStudio.ReportResult
    $html = [AppStudio.Report]::Build($session, $null, $result)
    $path = Join-Path $shots ($name + '.html')
    [IO.File]::WriteAllText($path, $html, (New-Object Text.UTF8Encoding($false)))
    return $html
}

# A fold must never sit inside another fold. Counted by walking the tags, so a
# nested <details> anywhere in the page fails whatever produced it.
function Max-FoldDepth([string]$html) {
    $depth = 0; $max = 0; $index = 0
    while ($true) {
        $open = $html.IndexOf('<details', $index)
        $close = $html.IndexOf('</details>', $index)
        if ($open -lt 0 -and $close -lt 0) { break }
        if ($open -ge 0 -and ($close -lt 0 -or $open -lt $close)) {
            $depth++
            if ($depth -gt $max) { $max = $depth }
            $index = $open + 8
        } else {
            $depth--
            $index = $close + 10
        }
    }
    return $max
}
function Section-Order([string]$html) {
    $order = @()
    foreach ($id in @('summary', 'steps', 'screens', 'elements', 'replay', 'input', 'limits', 'method')) {
        $at = $html.IndexOf('id="' + $id + '"')
        if ($at -ge 0) { $order += ,@($id, $at) }
    }
    return @($order | Sort-Object { $_[1] } | ForEach-Object { $_[0] })
}

try {
    $window = New-Rect 0 0 800 600
    $states = @{}

    # --- 1. everything worked ------------------------------------------------
    $ok = New-Session 'everything worked' 'record'
    $ok.Screens.Screens.Add((New-Screen 'S1' 'Fixture window' $true))
    $ok.Screens.Screens[0].ComponentIds.Add('E0')
    $ok.Screens.Screens[0].ComponentIds.Add('E1')
    for ($index = 0; $index -lt 3; $index++) {
        $node = New-Node $index ('Save ' + $index) ('SaveButton' + $index) 'S1'
        $ok.Elements.Add($node)
        $ok.Steps.Add((New-Step ($index + 1) 'click' $node $window))
    }
    $states['ok'] = Render $ok 'state-ok'

    # --- 2. some of it did not ----------------------------------------------
    $partial = New-Session 'some of it did not' 'record'
    $partial.Screens.Screens.Add((New-Screen 'S1' 'Fixture window' $true))
    $partial.Screens.Screens.Add((New-Screen 'S2' 'Second window' $false))
    $node = New-Node 0 'Save' 'SaveButton' 'S1'
    $partial.Elements.Add($node)
    $partial.Steps.Add((New-Step 1 'click' $node $window))
    # A step with nothing that identifies what it acted on.
    $bare = New-Step 2 'click' $null $window
    $bare.ElementLabel = '(unidentified element)'
    $bare.Unavailable.Add('no-identifying-locator: this element exposed no name, AutomationId, hierarchy path or control id.')
    $partial.Steps.Add($bare)
    $partial.AddLimit('[uia] UIA-TIMEOUT: the tree walk did not finish inside its allowance.')
    $states['partial'] = Render $partial 'state-partial'

    # --- 3. most of it did not ----------------------------------------------
    $many = New-Session 'most of it did not' 'record'
    for ($index = 1; $index -le 4; $index++) {
        $many.Screens.Screens.Add((New-Screen ('S' + $index) ('Window ' + $index) $false))
    }
    for ($index = 1; $index -le 12; $index++) {
        $step = New-Step $index 'click' $null $window
        $step.ElementLabel = '(unidentified element)'
        $step.Unavailable.Add('no-identifying-locator: nothing addresses this element.')
        $outcome = New-Object AppStudio.ReplayOutcome
        $outcome.State = 'not-found'; $outcome.Reason = 'No window matching FixtureApp / FixtureWindow is open.'
        $outcome.WaitedMs = 120; $outcome.SettleMs = 240
        $step.LastReplay = $outcome
        $many.Steps.Add($step)
    }
    for ($index = 1; $index -le 9; $index++) {
        $many.AddLimit('[msaa] MSAA-BUDGET: layer ' + $index + ' stopped after its allowance and left part of the window undescribed.')
    }
    $states['many'] = Render $many 'state-many-failures'

    # --- 4. nothing was recorded --------------------------------------------
    $empty = New-Session 'nothing was recorded' 'record'
    $states['empty'] = Render $empty 'state-empty'

    # --- 5. everything has a very long name ---------------------------------
    $long = New-Session ('a session whose title runs on and on ' * 6) 'snap'
    $longTitle = 'A window title that will not stop ' * 12
    $screen = New-Screen 'S1' $longTitle $true
    $screen.ComponentIds.Add('E0')
    $long.Screens.Screens.Add($screen)
    $longNode = New-Node 0 ('An element name that goes on for a very long time ' * 8) ('AutomationIdThatIsAbsurdlyLong' * 6) 'S1'
    $longNode.Path = ('Window > Pane > Group > ' * 20) + 'Button'
    $long.Elements.Add($longNode)
    $longStep = New-Step 1 'click' $longNode $window
    $longStep.WindowTitle = $longTitle
    $longStep.EffectSummary = ('the application reported something at considerable length ' * 10)
    $long.Steps.Add($longStep)
    $long.AddLimit(('A limit whose explanation is far longer than any column can hold ' * 10))
    $states['long'] = Render $long 'state-long-text'

    # --- what every state has to satisfy ------------------------------------
    foreach ($name in @('ok', 'partial', 'many', 'empty', 'long')) {
        $html = $states[$name]
        $depth = Max-FoldDepth $html
        if ($depth -gt 1) { throw ('state ' + $name + ' nests folds ' + $depth + ' deep') }
        $order = Section-Order $html
        if ($order.Count -lt 1 -or $order[0] -ne 'summary') { throw ('state ' + $name + ' does not put the conclusion first: ' + ($order -join ',')) }
        if ($order[$order.Count - 1] -ne 'method') { throw ('state ' + $name + ' does not put how-it-was-made last: ' + ($order -join ',')) }
        # The conclusion has to be readable without opening anything.
        $summaryStart = $html.IndexOf('id="summary"')
        $summaryEnd = $html.IndexOf('</section>', $summaryStart)
        $summary = $html.Substring($summaryStart, $summaryEnd - $summaryStart)
        if ($summary.Contains('<details')) { throw ('state ' + $name + ' folded away part of its own conclusion') }
        foreach ($needed in @('class="state"', 'class="stats"', 'class="next"')) {
            if (-not $summary.Contains($needed)) { throw ('state ' + $name + ' has no ' + $needed + ' in the conclusion') }
        }
        # Every fold says how much is behind it before it is opened.
        foreach ($match in [regex]::Matches($html, '<details class="fold"><summary>([^<]*)')) {
            if ([string]::IsNullOrWhiteSpace($match.Groups[1].Value)) { throw ('state ' + $name + ' has a fold with no label') }
        }
        # Long text must be allowed to wrap or scroll rather than push the page
        # sideways.
        if (-not $html.Contains('overflow-wrap:anywhere')) { throw 'long values have no way to wrap' }
        if (-not $html.Contains('.panel{max-height')) { throw 'the fold body has no bound on its height' }
        if ($html.Contains('<section id="input"') -and $name -eq 'empty') { throw 'an empty session showed a timeline section' }
    }

    # --- the words are the same words the window uses ------------------------
    $verdictOk = [AppStudio.SessionVerdict]::Of($ok)
    $verdictPartial = [AppStudio.SessionVerdict]::Of($partial)
    $verdictMany = [AppStudio.SessionVerdict]::Of($many)
    $verdictEmpty = [AppStudio.SessionVerdict]::Of($empty)
    if ($verdictOk.State -ne 'ok') { throw ('a complete session was not called complete: ' + $verdictOk.State) }
    if ($verdictPartial.State -ne 'partial') { throw ('a partly complete session was not called partial: ' + $verdictPartial.State) }
    if ($verdictMany.State -ne 'partial') { throw ('a session that failed to replay was not called partial: ' + $verdictMany.State) }
    if ($verdictEmpty.State -ne 'empty') { throw ('an empty session was not called empty: ' + $verdictEmpty.State) }
    if ($verdictPartial.NotReplayable -lt 1) { throw 'a step with no identifying locator was counted as replayable' }
    if ($verdictMany.ReplayStopped -ne 12) { throw ('the stopped steps were miscounted: ' + $verdictMany.ReplayStopped) }
    foreach ($verdict in @($verdictOk, $verdictPartial, $verdictMany, $verdictEmpty)) {
        if ([string]::IsNullOrWhiteSpace($verdict.Headline)) { throw 'a state produced no headline' }
        if ([string]::IsNullOrWhiteSpace($verdict.NextAction)) { throw 'a state produced no next action' }
        if ([string]::IsNullOrWhiteSpace($verdict.StateWord)) { throw 'a state produced no state word' }
    }
    # The conclusion in the report is the conclusion the verdict decided.
    if (-not $states['partial'].Contains([System.Net.WebUtility]::HtmlEncode($verdictPartial.Headline))) {
        throw 'the report headline is not the one the verdict decided'
    }
    # And session.md says the same thing, so three files cannot disagree.
    $md = [AppStudio.SessionMd]::Write($partial, (Join-Path $temp 'session.md'), $null)
    if (-not $md.Written) { throw ('session.md was not written: ' + $md.Problem) }
    $mdText = [IO.File]::ReadAllText((Join-Path $temp 'session.md'))
    if (-not $mdText.Contains($verdictPartial.Headline)) { throw 'session.md does not carry the same conclusion' }
    if (-not $mdText.Contains($verdictPartial.StateWord)) { throw 'session.md does not carry the same state word' }

    Write-Output ('PASS test-report-states states=5 foldDepth=1 conclusionFirst=1 methodLast=1 sharedWording=app+html+md written=' + $shots)
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
