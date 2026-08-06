$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell test failed: ' + $LASTEXITCODE) }
    return
}
$tests = @(
    'test-compile.ps1',
    'test-docs.ps1',
    'test-json.ps1',
    'test-locator.ps1',
    'test-privacy.ps1',
    'test-session-store.ps1',
    'test-outputs.ps1',
    'test-report-states.ps1',
    'test-replay.ps1',
    'test-codegen.ps1',
    'test-workflow-edit.ps1',
    'test-code-build.ps1',
    'test-vba-binary.ps1',
    'test-handoff.ps1',
    'test-ai-picks.ps1',
    'test-intake.ps1',
    'test-diagnostics.ps1',
    'test-acq-diagnostics.ps1',
    'test-win32-bound.ps1',
    'test-degraded.ps1',
    'test-live-basic.ps1',
    'test-live-move.ps1',
    'test-live-restart.ps1',
    'test-capture-policy.ps1',
    'test-live-canvas.ps1',
    'test-autosave.ps1',
    'test-scan.ps1',
    'test-ui-shell.ps1',
    'test-pane-layout.ps1',
    'test-layout-audit.ps1',
    'test-hud-fit.ps1',
    'test-hang-recovery.ps1'
)
# These move the real pointer and send real keystrokes, so they disturb whoever
# is using the machine. They only run when the operator says so, and when they
# do not run the fact is printed instead of being passed over in silence.
$realInputTests = @(
    'test-live-probe.ps1',
    'test-input-probe.ps1',
    'test-packaged-target.ps1',
    'test-input-timeline.ps1',
    'test-record-to-workflow.ps1',
    'test-gesture-e2e.ps1',
    'test-ime-e2e.ps1',
    'test-gui-e2e.ps1',
    'test-notepad-e2e.ps1',
    'test-calculator-e2e.ps1',
    'test-code-run-e2e.ps1',
    'test-artefact-e2e.ps1',
    'test-replay-fidelity.ps1',
    'test-vba-host.ps1',
    'test-vba-build.ps1',
    'test-inspector.ps1'
)
if ($env:APPSTUDIO_ALLOW_REAL_INPUT -eq '1') {
    $tests += $realInputTests
} else {
    foreach ($name in $realInputTests) {
        Write-Output ('SKIP ' + $name + ' (emits real mouse and keyboard input; set APPSTUDIO_ALLOW_REAL_INPUT=1 on a machine nobody is using)')
    }
}
$watch = [Diagnostics.Stopwatch]::StartNew()
$passed = 0
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($name in $tests) {
    $path = Join-Path $PSScriptRoot $name
    Write-Output ('RUN ' + $name)
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -STA -File $path
    if ($LASTEXITCODE -ne 0) { throw ($name + ' failed with exit code ' + $LASTEXITCODE) }
    $passed++
}
$watch.Stop()
Write-Output ('PASS run-all tests=' + $passed + ' durationMs=' + $watch.ElapsedMilliseconds)
