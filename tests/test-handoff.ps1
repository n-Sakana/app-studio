$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# The two files a chat window is given: one text file with every fact and one
# document with every picture. This checks that the screen ids and the component
# ids really do join up across both, that the document is a well formed one that
# still holds the exact pixels that went in, and that an answer written against
# one investigation cannot be run against a different one.
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
# The same wording the window uses, so the headings checked below are the ones a
# reader would actually be handed.
[AppStudio.Messages]::Init($root)
Add-Type -AssemblyName System.Drawing
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('pui-handoff-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null

function New-Rect($x, $y, $width, $height) {
    $rect = New-Object AppStudio.RectValue
    $rect.X = $x; $rect.Y = $y; $rect.Width = $width; $rect.Height = $height
    return $rect
}
function New-Node($name, $automationId, $type, $rect) {
    $node = New-Object AppStudio.ScanNode
    $node.Name = $name
    $node.AutomationId = $automationId
    $node.ControlType = $type
    $node.LocalizedControlType = $type
    $node.ClassName = 'TestClass'
    $node.Rect = $rect
    $node.Visible = $true
    $node.Enabled = $true
    $node.Offscreen = $false
    $node.KeyboardFocusable = $true
    $node.AddSource('uia')
    return $node
}
# Two pictures with content that is not flat, so a document that dropped or
# reordered the pixels cannot pass by accident.
function New-Png($path, $width, $height, $seed) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    for ($y = 0; $y -lt $height; $y++) {
        for ($x = 0; $x -lt $width; $x++) {
            $colour = [System.Drawing.Color]::FromArgb(255, (($x * 7 + $seed) % 256), (($y * 5 + $seed) % 256), ((($x + $y) * 3) % 256))
            $bitmap.SetPixel($x, $y, $colour)
        }
    }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

try {
    # ---------------------------------------------------------------- a scan
    $scan = New-Object AppStudio.ScanResult
    $scan.ScanId = 'sc-0001'
    $scan.ProcessId = 4321
    $scan.ProcessName = 'Fixture'
    $names = @(@('Main window', 'MainWin', 'Window'), @('7', 'num7Button', 'Button'), @('8', 'num8Button', 'Button'))
    $windowOne = New-Object AppStudio.ScanWindowResult
    $windowOne.Hwnd = 0x1234
    $windowOne.Title = '電卓'
    $windowOne.ClassName = 'ApplicationFrameWindow'
    $windowOne.Rect = New-Rect 100 120 420 640
    foreach ($entry in $names) { $windowOne.Nodes.Add((New-Node $entry[0] $entry[1] $entry[2] (New-Rect 130 200 60 60))) }
    $windowOne.ScreenId = 'S1'
    $windowTwo = New-Object AppStudio.ScanWindowResult
    $windowTwo.Hwnd = 0x5678
    $windowTwo.Title = 'History'
    $windowTwo.ClassName = 'Popup'
    $windowTwo.Rect = New-Rect 600 120 260 400
    $windowTwo.Nodes.Add((New-Node 'Clear' 'clearHistory' 'Button' (New-Rect 620 300 80 30)))
    $windowTwo.ScreenId = 'S2'
    foreach ($window in @($windowOne, $windowTwo)) {
        $scan.Windows.Add($window)
        foreach ($node in $window.Nodes) { $node.ScreenId = $window.ScreenId; $scan.Nodes.Add($node) }
    }
    for ($index = 0; $index -lt $scan.Nodes.Count; $index++) { $scan.Nodes[$index].NodeId = $index }

    $ledger = [AppStudio.ScreenLedger]::FromScan($scan)
    if ($ledger.Screens.Count -ne 2) { throw ('The ledger did not carry both screens: ' + $ledger.Screens.Count) }
    if ($ledger.Screens[0].ScreenId -ne 'S1' -or $ledger.Screens[1].ScreenId -ne 'S2') { throw 'The screen ids are not the ones the scan handed out.' }
    if ($ledger.Screens[0].ComponentIds.Count -ne 3) { throw ('S1 lost components: ' + $ledger.Screens[0].ComponentIds.Count) }
    if ($ledger.Screens[1].ComponentIds -notcontains 'E3') { throw 'S2 does not name the component the scan put on it.' }
    if ($ledger.Screens[0].ComponentIds -contains 'E3') { throw 'A component was claimed by two screens.' }

    # Pictures, as the capture step would have left them.
    $shotDir = Join-Path $tempDir 'shots'
    New-Item -ItemType Directory -Path $shotDir | Out-Null
    $shotOne = Join-Path $shotDir ([AppStudio.ScreenCapture]::FileNameFor('sc-0001', 'S1'))
    New-Png $shotOne 210 160 11
    $ledger.Screens[0].ShotFile = $shotOne
    $ledger.Screens[0].CaptureMethod = 'BitBlt'
    $ledger.Screens[0].CapturedAt = [DateTimeOffset]::Now
    # The second screen deliberately has no picture, so the row that says why has
    # to survive all the way into both attachments.
    $ledger.Screens[1].ShotProblem = 'SHOT-NORECT: this window has no usable rectangle right now.'
    if ($ledger.ShotCount -ne 1) { throw ('The ledger miscounted its pictures: ' + $ledger.ShotCount) }

    # ------------------------------------------------------------- the bundle
    $record = New-Object AppStudio.CaseRecord
    $record.CaseId = 'case-test'
    $record.Folder = $tempDir
    $record.TargetProcess = 'Calculator'
    $record.TargetTitle = '電卓'
    $record.TargetProcessId = 4321
    $record.SessionFolder = $tempDir
    $request = [AppStudio.RequestBuilder]::Build($record, $scan, 'summary line', $tempDir, '7 と 8 を足す', $ledger)
    if ($request.TemplateProblems.Count -ne 0) { throw ('The request wording is incomplete: ' + ($request.TemplateProblems -join '; ')) }
    foreach ($term in @('handoff.txt', 'screens.pdf', 'pui-plan', '7 と 8 を足す')) {
        if (-not $request.Request.Contains($term)) { throw ('The request text is missing: ' + $term) }
    }
    if ($request.Investigation -notmatch '\|\s*E0\s*\|\s*S1\s*\|') { throw 'The parts table does not put each component on its screen.' }

    $handoffDir = Join-Path $tempDir 'handoff'
    $bundle = [AppStudio.HandoffBuilder]::Build($record, $request, $ledger, $handoffDir, '7 と 8 を足す')
    if (-not $bundle.Complete) { throw ('The bundle is not complete: ' + ($bundle.Problems -join '; ')) }
    if ($bundle.PageCount -ne 2) { throw ('The document should have one page per screen, got ' + $bundle.PageCount) }
    $textPath = Join-Path $handoffDir 'handoff.txt'
    $pdfPath = Join-Path $handoffDir 'screens.pdf'
    foreach ($path in @($textPath, $pdfPath)) { if (-not (Test-Path -LiteralPath $path)) { throw ('The bundle did not write ' + $path) } }

    # --------------------------------------------------- the single text file
    $text = [IO.File]::ReadAllText($textPath, [Text.Encoding]::UTF8)
    if (-not $text.Contains($bundle.BundleId)) { throw 'The text attachment does not name its own bundle.' }
    if (-not $text.Contains('電卓')) { throw 'The text attachment lost the window title it was told to keep intact.' }
    if ($text -notmatch '\|\s*S1\s*\|\s*1\s*\|') { throw 'The text attachment does not say which page shows S1.' }
    if ($text -notmatch '\|\s*S2\s*\|\s*2\s*\|') { throw 'The text attachment does not say which page shows S2.' }
    if (-not $text.Contains('SHOT-NORECT')) { throw 'The screen with no picture was dropped instead of explained.' }
    if ($text -notmatch '\|\s*E3\s*\|\s*S2\s*\|') { throw 'The text attachment does not tie every component to a screen.' }
    if (-not $text.Contains('num7Button')) { throw 'The text attachment carries no identifying material.' }
    # Everything the operator would otherwise have had to attach separately is
    # in this one file.
    foreach ($term in @('取得できなかったもの', '画面台帳')) {
        if (-not $text.Contains($term)) { throw ('The text attachment is missing the section: ' + $term) }
    }

    # ------------------------------------------------------- the one document
    $bytes = [IO.File]::ReadAllBytes($pdfPath)
    $latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    if (-not $latin.StartsWith('%PDF-1.4')) { throw 'The document does not start with a document header.' }
    if (-not $latin.TrimEnd().EndsWith('%%EOF')) { throw 'The document has no end marker.' }
    if (-not $latin.Contains('/Type /Catalog')) { throw 'The document has no catalogue.' }
    if (-not $latin.Contains('/Count 2')) { throw 'The document does not declare two pages.' }
    if (-not $latin.Contains('Screen S1')) { throw 'Page one does not name its screen.' }
    if (-not $latin.Contains('Screen S2')) { throw 'Page two does not name its screen.' }
    if (-not $latin.Contains('page 1 of 2') -or -not $latin.Contains('page 2 of 2')) { throw 'The pages do not state their own numbers.' }
    if (-not $latin.Contains('no picture for this screen')) { throw 'The page for the missing picture does not say it is missing.' }
    $imageCount = ([regex]::Matches($latin, '/Subtype /Image')).Count
    if ($imageCount -ne 1) { throw ('Expected exactly one stored picture, found ' + $imageCount) }

    # The cross reference table has to point at the objects it claims to.
    $startIndex = $latin.LastIndexOf('startxref')
    if ($startIndex -lt 0) { throw 'The document has no cross reference pointer.' }
    $startValue = [int](($latin.Substring($startIndex + 9)).Trim() -split '\s+')[0]
    if ($latin.Substring($startValue, 4) -ne 'xref') { throw 'The cross reference pointer does not point at the table.' }
    $tableText = $latin.Substring($startValue)
    $offsets = @([regex]::Matches($tableText, '(?m)^(\d{10}) 00000 n') | ForEach-Object { [int]$_.Groups[1].Value })
    if ($offsets.Count -lt 5) { throw ('The cross reference table is too short: ' + $offsets.Count) }
    for ($index = 0; $index -lt $offsets.Count; $index++) {
        $expected = ($index + 1).ToString() + ' 0 obj'
        if ($latin.Substring($offsets[$index], $expected.Length) -ne $expected) {
            throw ('Cross reference entry ' + ($index + 1) + ' points at "' + $latin.Substring($offsets[$index], 12) + '"')
        }
    }

    # The stored picture must inflate back to exactly the pixels that went in.
    $marker = $latin.IndexOf('/Subtype /Image')
    $streamStart = $latin.IndexOf("stream`n", $marker) + 7
    $streamEnd = $latin.IndexOf("`nendstream", $streamStart)
    if ($streamStart -le 6 -or $streamEnd -lt $streamStart) { throw 'The stored picture has no readable stream.' }
    $header = $latin.Substring($marker, $streamStart - $marker)
    if ($header -notmatch '/Width (\d+)') { throw 'The stored picture states no width.' }
    $pdfWidth = [int]$Matches[1]
    if ($header -notmatch '/Height (\d+)') { throw 'The stored picture states no height.' }
    $pdfHeight = [int]$Matches[1]
    if ($header -notmatch '/Filter /FlateDecode') { throw 'The stored picture is not in the declared form.' }
    if ($bytes[$streamStart] -ne 0x78) { throw 'The compressed picture has no wrapper the format asks for.' }
    $payload = New-Object byte[] ($streamEnd - $streamStart - 6)
    [Array]::Copy($bytes, $streamStart + 2, $payload, 0, $payload.Length)
    $input = New-Object IO.MemoryStream(,$payload)
    $inflate = New-Object IO.Compression.DeflateStream($input, [IO.Compression.CompressionMode]::Decompress)
    $output = New-Object IO.MemoryStream
    $inflate.CopyTo($output)
    $inflate.Dispose()
    $raw = $output.ToArray()
    $output.Dispose()
    if ($raw.Length -ne ($pdfWidth * $pdfHeight * 3)) {
        throw ('The stored picture inflates to ' + $raw.Length + ' bytes, expected ' + ($pdfWidth * $pdfHeight * 3))
    }
    $source = New-Object System.Drawing.Bitmap($shotOne)
    if ($source.Width -ne $pdfWidth -or $source.Height -ne $pdfHeight) { throw 'The stored picture is not the size of the one that went in.' }
    $mismatch = 0
    for ($y = 0; $y -lt $pdfHeight; $y += 17) {
        for ($x = 0; $x -lt $pdfWidth; $x += 13) {
            $pixel = $source.GetPixel($x, $y)
            $at = ($y * $pdfWidth + $x) * 3
            if ($raw[$at] -ne $pixel.R -or $raw[$at + 1] -ne $pixel.G -or $raw[$at + 2] -ne $pixel.B) { $mismatch++ }
        }
    }
    $source.Dispose()
    if ($mismatch -ne 0) { throw ('The stored picture differs from the original in ' + $mismatch + ' sampled places.') }

    # ------------------------------------ entering the route without a scan
    # Skipping the automatic scan is a documented way to work: the assistant
    # gets no part list and aims at points instead. It must still get the
    # picture that was taken of the target, and the request must still be
    # something the operator can send.
    $bare = New-Object AppStudio.CaseRecord
    $bare.CaseId = 'case-noscan'
    $bare.Folder = $tempDir
    $bare.TargetProcess = 'Calculator'
    $bare.TargetTitle = '電卓'
    $bare.ShotFile = $shotOne
    $bareRequest = [AppStudio.RequestBuilder]::Build($bare, $null, $null, $tempDir, '画面のどこかを押す', $null)
    $bareDir = Join-Path $tempDir 'handoff-noscan'
    $bareBundle = [AppStudio.HandoffBuilder]::Build($bare, $bareRequest, $null, $bareDir, '画面のどこかを押す')
    if (-not $bareBundle.Complete) { throw ('A case with no scan could not build a request: ' + ($bareBundle.Problems -join '; ')) }
    if ($bareBundle.PageCount -ne 1) { throw ('The screenshot taken for the case was not carried into the document: pages=' + $bareBundle.PageCount) }
    $bareText = [IO.File]::ReadAllText((Join-Path $bareDir 'handoff.txt'), [Text.Encoding]::UTF8)
    if ($bareText -notmatch '\|\s*S1\s*\|\s*1\s*\|') { throw 'The unscanned screen has no row of its own.' }
    if (-not $bareText.Contains('自動調査をしていない')) { throw 'The text attachment does not say the screen was never scanned.' }
    $null = [AppStudio.RequestBuilder]::Recompose($bare, $bareRequest, '画面のどこかを押す', $tempDir)
    $bareRequest.Handoff = $bareBundle
    $recomposed = [AppStudio.RequestBuilder]::Recompose($bare, $bareRequest, '画面のどこかを押す', $tempDir)
    if (-not $recomposed.Contains('screens.pdf')) { throw 'The request does not name the picture attachment that was made.' }

    # With nothing to picture at all the request still has to be sendable, and
    # it must not name a document that was never written.
    $blind = New-Object AppStudio.CaseRecord
    $blind.CaseId = 'case-blind'
    $blind.Folder = $tempDir
    $blind.TargetTitle = '電卓'
    $blindRequest = [AppStudio.RequestBuilder]::Build($blind, $null, $null, $tempDir, '何かする', $null)
    $blindDir = Join-Path $tempDir 'handoff-blind'
    $blindBundle = [AppStudio.HandoffBuilder]::Build($blind, $blindRequest, $null, $blindDir, '何かする')
    if (-not $blindBundle.Complete) { throw 'A case with no picture at all blocked the operator instead of stating the fact.' }
    if ($null -ne $blindBundle.PdfPath) { throw 'A picture document was written with nothing to put in it.' }
    if ([string]::IsNullOrEmpty($blindBundle.NoPictureReason)) { throw 'The missing picture was passed over in silence.' }
    $blindRequest.Handoff = $blindBundle
    $blindText = [AppStudio.RequestBuilder]::Recompose($blind, $blindRequest, '何かする', $tempDir)
    if ($blindText -match '(?m)^- screens\.pdf') { throw 'The request names a picture attachment that was never written.' }
    if (-not $blindText.Contains($blindBundle.NoPictureReason)) { throw 'The request does not say the picture is missing.' }
    $blindFiles = @(Get-ChildItem -LiteralPath $blindDir -File)
    if ($blindFiles.Count -ne 1) { throw ('The attachment folder holds ' + $blindFiles.Count + ' files, expected only the text.') }

    # ------------------------------------------------ what the answer may assume
    $table = $request.Elements
    $premise = [AppStudio.HandoffBuilder]::PremiseHash($ledger, $table)
    if ($premise -ne $bundle.PremiseHash) { throw 'The recorded premise does not match the material it was taken from.' }
    $moved = [AppStudio.ScreenLedger]::FromScan($scan)
    $moved.Screens[0].Rect = New-Rect 999 999 420 640
    if ([AppStudio.HandoffBuilder]::PremiseHash($moved, $table) -eq $premise) { throw 'A screen that moved did not change the premise.' }
    $scan.Nodes[1].Rect = New-Rect 700 700 60 60
    $rebuilt = [AppStudio.CaseElementTable]::Build($scan, 250)
    if ([AppStudio.HandoffBuilder]::PremiseHash($ledger, $rebuilt) -eq $premise) { throw 'A part that moved did not change the premise.' }

    Write-Output ('PASS test-handoff screens=' + $ledger.Screens.Count + ' pages=' + $bundle.PageCount +
        ' pictures=' + $ledger.ShotCount + '/' + $ledger.Screens.Count +
        ' text=' + $bundle.TextBytes + 'B pdf=' + $bundle.PdfBytes + 'B image=' + $pdfWidth + 'x' + $pdfHeight +
        ' lossless=yes xrefEntries=' + $offsets.Count + ' premiseHash=' + $premise.Substring(0, 16))
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
