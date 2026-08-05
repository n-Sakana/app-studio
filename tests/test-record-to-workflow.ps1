$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
# From a real recording to a procedure somebody can edit.
#
# Every other test that looks at the workflow starts from a session written into
# the store by the test itself, which proves the generator but not the road that
# leads to it. This one presses real buttons on a real window with the real
# recorder running, and then asks the question a person asks when they open the
# code screen: is what I just did in here, one line at a time, where I can see
# it and take one of them out?
#
# An empty workflow is a failure. So is a workflow that only holds machinery.
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -ne '1') {
    Write-Output 'SKIP test-record-to-workflow (moves the real pointer; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)'
    return
}
Add-Type -AssemblyName System.Windows.Forms
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'app-studio.ps1') -CompileOnly
[AppStudio.DpiAwareness]::Enable()
[AppStudio.Probe]::Configure($root, $false)
$build = & (Join-Path $PSScriptRoot 'build-fixtures.ps1')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pui-r2w-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

if ($null -eq ('PuiR2W.Input' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace PuiR2W
{
    public static class Input
    {
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] private struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public UNION data; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        private const uint INPUT_MOUSE = 0;
        private const uint LEFTDOWN = 0x0002;
        private const uint LEFTUP = 0x0004;

        private static void Mouse(uint flags)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].data.mouse.flags = flags;
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(110);
            Mouse(LEFTDOWN);
            Thread.Sleep(70);
            Mouse(LEFTUP);
            Thread.Sleep(280);
        }
    }
}
'@
}

function Centre($rect) { return @(($rect.X + [int]($rect.Width / 2)), ($rect.Y + [int]($rect.Height / 2))) }

$forms = $null
$recorder = $null
try {
    $ready = Join-Path $temp 'forms.json'
    $forms = Start-Process $build.FixtureWinForms -ArgumentList @('--ready', $ready) -PassThru
    $limit = [DateTime]::UtcNow.AddSeconds(20)
    while ((-not (Test-Path $ready)) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 60 }
    if (-not (Test-Path $ready)) { throw 'FixtureWinForms did not become ready.' }
    $f = Get-Content $ready -Raw | ConvertFrom-Json
    $window = [int64]$f.window
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $rect = [AppStudio.WindowTools]::GetPhysicalRect([IntPtr]$window)
    [AppStudio.WindowTools]::Move([IntPtr]$window, ($work.Left + 30), ($work.Top + 30), $rect.Width, $rect.Height) | Out-Null
    Start-Sleep -Milliseconds 400
    [AppStudio.ScreenCapture]::Raise($window)
    Start-Sleep -Milliseconds 600

    $first = Centre ([AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.first))
    $normal = Centre ([AppStudio.WindowTools]::GetPhysicalRect([IntPtr][int64]$f.normal))

    $session = [AppStudio.SessionStore]::Create($temp, 'record', 'record to workflow')
    $session.ValuePolicy = 'recordText'
    $recorder = New-Object AppStudio.Recorder($root, $session, $null, $null)
    $recorder.Start()
    Start-Sleep -Milliseconds 2500

    # Four presses a person would recognise afterwards, with pauses between them
    # so the intervals are real intervals.
    $pressed = 4
    for ($index = 0; $index -lt $pressed; $index++) {
        if (($index % 2) -eq 0) { [PuiR2W.Input]::Click($first[0], $first[1]) }
        else { [PuiR2W.Input]::Click($normal[0], $normal[1]) }
        Start-Sleep -Milliseconds 800
    }

    # A pause with nothing watched, and then more of the same. The pause has to
    # leave a stated hole rather than looking like a quiet moment.
    $recorder.SetPaused($true)
    Start-Sleep -Milliseconds 400
    [PuiR2W.Input]::Click($first[0], $first[1])
    Start-Sleep -Milliseconds 900
    $recorder.SetPaused($false)
    Start-Sleep -Milliseconds 400
    [PuiR2W.Input]::Click($first[0], $first[1])
    $pressed++
    Start-Sleep -Milliseconds 1400

    $recorder.Stop()
    $recorder.Dispose()
    $recorder = $null
    [AppStudio.SessionStore]::WriteMeta($session)

    # --- what the recording holds ------------------------------------------
    if ($session.Steps.Count -lt $pressed) {
        throw ('the recording kept ' + $session.Steps.Count + ' of ' + $pressed + ' presses')
    }
    $paused = @($session.Limits | Where-Object { $_ -match 'RECORD-PAUSED' })
    if ($paused.Count -lt 1) { throw 'the recording was paused and did not say so in its limits' }

    # --- the procedure it becomes ------------------------------------------
    $project = [AppStudio.CodeProject]::Open($session)
    foreach ($language in @('powershell', 'vba')) {
        $workflow = $project.Find($language, 'Workflow')
        if ($null -eq $workflow) { throw ('there is no procedure for ' + $language) }
        $lines = @($workflow.Text -split "`r`n|`n")
        # The step lines: an operation name, then the id of a recorded step in
        # quotes. Nothing else in the file looks like this.
        # PowerShell quotes the step id with an apostrophe and VBA with a double
        # quote. Both are "an operation name, then the id of a recorded step".
        $steps = @($lines | Where-Object { $_ -match "^\s*(Runtime\.)?(FindWindow|FocusElement|InvokeElement|SetElementText|ReadElementText|SendKeys|WaitGap|WaitIdle|AskSecret|Unsupported)\s*\(?\s*['`"]" })
        if ($steps.Count -lt $pressed) {
            throw ('the ' + $language + ' procedure has ' + $steps.Count + ' step lines for ' + $pressed + ' recorded presses')
        }
        # It has to be the procedure, not the machinery: a workflow a person can
        # read is short, and the recorded steps are near the top of it.
        $firstStep = 0
        for ($index = 0; $index -lt $lines.Count; $index++) {
            if ($lines[$index] -match "^\s*(Runtime\.)?(FindWindow|InvokeElement|SetElementText|SendKeys|Unsupported)\s*\(?\s*['`"]") { $firstStep = $index + 1; break }
        }
        if ($firstStep -eq 0) { throw ('the ' + $language + ' procedure has no recorded step in it at all') }
        if ($firstStep -gt 40) {
            throw ('the first recorded step of the ' + $language + ' procedure is on line ' + $firstStep + ', below the fold of any editor')
        }
        if ($lines.Count -gt 120) {
            throw ('the ' + $language + ' procedure is ' + $lines.Count + ' lines; that is machinery, not a procedure')
        }
        # No address, no interval and no literal in the procedure: those belong
        # to the module beside it, and their absence is what makes one line
        # safe to delete.
        foreach ($leak in @('uia.automationId', 'win32.ctrlId', 'GapMs')) {
            if ($workflow.Text.IndexOf($leak, [StringComparison]::Ordinal) -ge 0) {
                throw ('the ' + $language + ' procedure carries ' + $leak)
            }
        }
    }

    # --- one line out, and only that operation goes ------------------------
    $before = $project.Find('powershell', 'Workflow').Text
    $beforeLines = @($before -split "`r`n|`n")
    $target = -1
    for ($index = 0; $index -lt $beforeLines.Count; $index++) {
        if ($beforeLines[$index] -match "^\s*(Runtime\.)?InvokeElement\s*\(?\s*['`"]") { $target = $index; break }
    }
    if ($target -lt 0) { throw 'the recorded procedure has no press to take out' }
    $others = @()
    foreach ($name in @('RecordedFacts', 'RuntimeCore', 'RuntimeLocator', 'RuntimeNative')) {
        $others += , @($name, $project.Find('powershell', $name).Text)
    }
    $kept = New-Object 'System.Collections.Generic.List[string]'
    for ($index = 0; $index -lt $beforeLines.Count; $index++) {
        if ($index -ne $target) { $kept.Add($beforeLines[$index]) }
    }
    $project.SetText('powershell', 'Workflow', ($kept -join "`r`n"))
    $after = $project.Find('powershell', 'Workflow').Text
    $afterSteps = @(($after -split "`r`n|`n") | Where-Object { $_ -match "^\s*(Runtime\.)?InvokeElement\s*\(?\s*['`"]" })
    $beforeSteps = @($beforeLines | Where-Object { $_ -match "^\s*(Runtime\.)?InvokeElement\s*\(?\s*['`"]" })
    if ($afterSteps.Count -ne ($beforeSteps.Count - 1)) {
        throw ('deleting one line changed the press count by ' + ($beforeSteps.Count - $afterSteps.Count))
    }
    foreach ($pair in $others) {
        if ($project.Find('powershell', $pair[0]).Text -ne $pair[1]) {
            throw ('editing the procedure changed ' + $pair[0])
        }
    }
    $parsed = [AppStudio.ScriptRun]::CheckEngine($project.Files('powershell'))
    if (-not $parsed.Ok) { throw ('the edited procedure does not parse: ' + (($parsed.Problems) -join ' / ')) }

    Write-Output ('PASS test-record-to-workflow pressed=' + $pressed + ' recorded=' + $session.Steps.Count +
        ' pausedStated=' + $paused.Count + ' psSteps=' + $beforeSteps.Count + ' afterDelete=' + $afterSteps.Count +
        ' untouchedModules=' + $others.Count + ' parses=1')
} finally {
    if ($null -ne $recorder) { try { $recorder.Stop(); $recorder.Dispose() } catch { } }
    if ($null -ne $forms -and -not $forms.HasExited) { $forms.Kill(); $forms.WaitForExit() }
    if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
