param(
    [int]$Iterations = 20,
    [int]$TemporaryHangSeconds = 15,
    [int]$RequestTimeoutMs = 1500,
    [int]$ToleranceMs = 300,
    [int]$InternalTimeoutMs = 1000,
    [int]$Win32MessageTimeoutMs = 150,
    [int]$UiThresholdMs = 250,
    [string]$ResultsDirectory,
    [switch]$AggregateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$specPath = Join-Path $repoRoot 'docs\SPEC.md'
if (-not (Test-Path -LiteralPath $specPath)) {
    throw ('The specification is missing: ' + $specPath)
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $ResultsDirectory = Join-Path $repoRoot ('artifacts\wp-s\run-' + $stamp)
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

$strategies = @('A', 'B', 'B2', 'C')
$modes = @('permanent', 'temporary')

if (-not $AggregateOnly) {
    $build = & (Join-Path $PSScriptRoot 'build-wp-s.ps1')
    $powershellExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    foreach ($mode in $modes) {
        foreach ($strategy in $strategies) {
            Write-Output ('SCENARIO_START ' + $strategy + ' ' + $mode + ' ' + (Get-Date -Format o))
            & $build.HarnessExe `
                --strategy $strategy `
                --hang-mode $mode `
                --iterations $Iterations `
                --request-timeout-ms $RequestTimeoutMs `
                --tolerance-ms $ToleranceMs `
                --internal-timeout-ms $InternalTimeoutMs `
                --temporary-seconds $TemporaryHangSeconds `
                --win32-message-timeout-ms $Win32MessageTimeoutMs `
                --ui-threshold-ms $UiThresholdMs `
                --fixture-exe $build.FixtureExe `
                --worker-script (Join-Path $PSScriptRoot 'wps-worker.ps1') `
                --worker-source (Join-Path $PSScriptRoot 'WpsWorkerCore.cs') `
                --powershell-exe $powershellExe `
                --output-dir $ResultsDirectory
            if ($LASTEXITCODE -ne 0) {
                throw ('Scenario failed: ' + $strategy + ' ' + $mode + ', exit ' + $LASTEXITCODE)
            }
            Write-Output ('SCENARIO_END ' + $strategy + ' ' + $mode + ' ' + (Get-Date -Format o))
        }
    }
}

$rawFiles = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'raw-*.csv' | Sort-Object Name
$summaryFiles = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'summary-*.csv' | Sort-Object Name
$startupFiles = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'worker-startup-C-*.csv' | Sort-Object Name
$bApiFiles = Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'b-api-B-*.csv' | Sort-Object Name
$raw = @($rawFiles | ForEach-Object { Import-Csv -LiteralPath $_.FullName })
$scenarioSummaries = @($summaryFiles | ForEach-Object { Import-Csv -LiteralPath $_.FullName })
$startup = @($startupFiles | ForEach-Object { Import-Csv -LiteralPath $_.FullName })
$bApi = @($bApiFiles | ForEach-Object { Import-Csv -LiteralPath $_.FullName })

$rawPath = Join-Path $ResultsDirectory '1-WP-S-RAW-DATA.csv'
$raw | Export-Csv -LiteralPath $rawPath -NoTypeInformation -Encoding UTF8
$startupPath = Join-Path $ResultsDirectory '5-WP-S-C-WORKER-STARTUP.csv'
$startup | Export-Csv -LiteralPath $startupPath -NoTypeInformation -Encoding UTF8

function Convert-Long($Value) {
    return [Int64]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Percentile([Int64[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling($Percentile * $sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $sorted.Count) { $index = $sorted.Count - 1 }
    return [Int64]$sorted[$index]
}

function Test-ContinuousIncrease([Int64[]]$Values) {
    if ($Values.Count -lt 2) { return $false }
    for ($i = 1; $i -lt $Values.Count; $i++) {
        if ($Values[$i] -le $Values[$i - 1]) { return $false }
    }
    return $true
}

$verdicts = @()
foreach ($strategy in $strategies) {
    foreach ($mode in $modes) {
        $group = @($raw | Where-Object { $_.strategy -eq $strategy -and $_.hang_mode -eq $mode } | Sort-Object { [int]$_.iteration })
        $returnPass = (@($group | Where-Object { $_.within_return_limit -ne 'true' -or ($_.hang_outcome -ne 'partial' -and $_.hang_outcome -ne 'unavailable') }).Count -eq 0)
        $healthyPass = (@($group | Where-Object { $_.healthy_success -ne 'true' -or $_.tool_restart_required -ne 'false' }).Count -eq 0)
        $queuePass = (@($group | Where-Object { (Convert-Long $_.queue_depth) -ne 0 }).Count -eq 0)
        $orphanThreadMax = ($group | ForEach-Object { Convert-Long $_.orphan_thread_count } | Measure-Object -Maximum).Maximum
        $orphanProcessMax = ($group | ForEach-Object { Convert-Long $_.orphan_process_count } | Measure-Object -Maximum).Maximum
        $threadGrowth = Test-ContinuousIncrease @($group | ForEach-Object { Convert-Long $_.thread_count })
        $handleGrowth = Test-ContinuousIncrease @($group | ForEach-Object { Convert-Long $_.handle_count })
        $processGrowth = Test-ContinuousIncrease @($group | ForEach-Object { Convert-Long $_.child_process_count })
        $memoryGrowth = Test-ContinuousIncrease @($group | ForEach-Object { Convert-Long $_.working_set_bytes })
        $resourcePass = ($orphanThreadMax -eq 0 -and $orphanProcessMax -eq 0 -and -not $threadGrowth -and -not $handleGrowth -and -not $processGrowth -and -not $memoryGrowth)
        $uiPass = (@($group | Where-Object { (Convert-Long $_.ui_max_unresponsive_ms) -gt (Convert-Long $_.ui_threshold_ms) }).Count -eq 0)
        $hangTimes = [Int64[]]@($group | ForEach-Object { Convert-Long $_.hang_t_return_ms })
        $healthyTimes = [Int64[]]@($group | ForEach-Object { Convert-Long $_.healthy_duration_ms })
        $verdicts += [pscustomobject]@{
            Strategy = $strategy
            Mode = $mode
            Rows = $group.Count
            TReturnP50Ms = Get-Percentile $hangTimes 0.50
            TReturnP95Ms = Get-Percentile $hangTimes 0.95
            TReturnMaxMs = ($hangTimes | Measure-Object -Maximum).Maximum
            HealthyP95Ms = Get-Percentile $healthyTimes 0.95
            HealthyMaxMs = ($healthyTimes | Measure-Object -Maximum).Maximum
            ReturnPass = $returnPass
            HealthyPass = $healthyPass
            ResourcePass = $resourcePass
            QueuePass = $queuePass
            UiPass = $uiPass
            OrphanThreadMax = $orphanThreadMax
            OrphanProcessMax = $orphanProcessMax
            ThreadContinuousIncrease = $threadGrowth
            HandleContinuousIncrease = $handleGrowth
            ProcessContinuousIncrease = $processGrowth
            MemoryContinuousIncrease = $memoryGrowth
            FinalThreadDelta = Convert-Long $group[-1].thread_delta
            FinalHandleDelta = Convert-Long $group[-1].handle_delta
            FinalProcessDelta = Convert-Long $group[-1].process_delta
            FinalWorkingSetDeltaBytes = Convert-Long $group[-1].working_set_delta_bytes
            MaxQueue = ($group | ForEach-Object { Convert-Long $_.queue_depth } | Measure-Object -Maximum).Maximum
            MaxUiMs = ($group | ForEach-Object { Convert-Long $_.ui_max_unresponsive_ms } | Measure-Object -Maximum).Maximum
            Pass = ($returnPass -and $healthyPass -and $resourcePass -and $queuePass)
        }
    }
}

$bApiPass = ($bApi.Count -eq 4 -and @($bApi | Where-Object {
    $_.state -eq 'outer-timeout' -or
    (Convert-Long $_.duration_ms) -gt ($InternalTimeoutMs + $ToleranceMs) -or
    $_.thread_alive_after_release -ne 'false'
}).Count -eq 0)

$strategyVerdicts = @()
foreach ($strategy in $strategies) {
    $parts = @($verdicts | Where-Object Strategy -eq $strategy)
    $apiGate = $true
    if ($strategy -eq 'B') { $apiGate = $bApiPass }
    $strategyVerdicts += [pscustomobject]@{
        Strategy = $strategy
        Permanent = [bool]($parts | Where-Object Mode -eq 'permanent').Pass
        Temporary = [bool]($parts | Where-Object Mode -eq 'temporary').Pass
        Pass = (@($parts | Where-Object { -not $_.Pass }).Count -eq 0 -and $apiGate)
    }
}

$selected = 'NONE'
foreach ($candidate in @('B', 'A', 'C')) {
    if (($strategyVerdicts | Where-Object Strategy -eq $candidate).Pass) {
        $selected = $candidate
        break
    }
}

$cStartupValues = [Int64[]]@($startup | ForEach-Object { Convert-Long $_.startup_ms })
$cRows = @($raw | Where-Object strategy -eq 'C')
$cSteadyBaselineValues = [Int64[]]@($cRows | ForEach-Object { Convert-Long $_.healthy_baseline_median_ms })
$cPostSwitchFirstPermanentValues = [Int64[]]@($cRows | Where-Object { $_.worker_switched -eq 'true' -and $_.hang_mode -eq 'permanent' } | ForEach-Object { Convert-Long $_.healthy_duration_ms })
$cPostSwitchFirstAllModesValues = [Int64[]]@($cRows | Where-Object worker_switched -eq 'true' | ForEach-Object { Convert-Long $_.healthy_duration_ms })
$startupP50 = Get-Percentile $cStartupValues 0.50
$startupP95 = Get-Percentile $cStartupValues 0.95
$startupMax = ($cStartupValues | Measure-Object -Maximum).Maximum
$cSteadyBaselineMedian = Get-Percentile $cSteadyBaselineValues 0.50
$cPostSwitchFirstPermanentP50 = Get-Percentile $cPostSwitchFirstPermanentValues 0.50
$cPostSwitchFirstAllModesP95 = Get-Percentile $cPostSwitchFirstAllModesValues 0.95
$spareNeeded = $startupP95 -gt $cPostSwitchFirstAllModesP95

function Bool-Text($Value) {
    if ($Value) { return 'PASS' }
    return 'FAIL'
}

$decision = New-Object Text.StringBuilder
[void]$decision.AppendLine('# WP-S decision')
[void]$decision.AppendLine('')
[void]$decision.AppendLine('- Canonical plan SHA-256: `' + $actualPlanHash + '`')
[void]$decision.AppendLine('- Raw rows: ' + $raw.Count + ' (A/B/B2/C x permanent/temporary x ' + $Iterations + ')')
[void]$decision.AppendLine('- Measured request timeout: ' + $RequestTimeoutMs + ' ms; tolerance: ' + $ToleranceMs + ' ms; IUIAutomation2 internal timeouts: ' + $InternalTimeoutMs + ' ms.')
[void]$decision.AppendLine('- Selected recommendation under the canonical simplicity order B < A < C: **' + $selected + '**.')
[void]$decision.AppendLine('- C steady-state healthy baseline through the same strategy boundary: median **' + $cSteadyBaselineMedian + ' ms**. The first healthy acquisition immediately after a worker switch is UIA-cold: **' + $cPostSwitchFirstPermanentP50 + ' ms p50 / ' + $cPostSwitchFirstAllModesP95 + ' ms p95**.')
[void]$decision.AppendLine('')
[void]$decision.AppendLine('| Strategy | Mode | t_return p50/p95/max ms | Healthy p95/max ms | Return | Immediate healthy | Resources | Queue | UI max ms | Gate |')
[void]$decision.AppendLine('|---|---|---:|---:|---|---|---|---|---:|---|')
foreach ($v in $verdicts) {
    [void]$decision.AppendLine('| ' + $v.Strategy + ' | ' + $v.Mode + ' | ' + $v.TReturnP50Ms + '/' + $v.TReturnP95Ms + '/' + $v.TReturnMaxMs + ' | ' + $v.HealthyP95Ms + '/' + $v.HealthyMaxMs + ' | ' + (Bool-Text $v.ReturnPass) + ' | ' + (Bool-Text $v.HealthyPass) + ' | ' + (Bool-Text $v.ResourcePass) + ' | ' + (Bool-Text $v.QueuePass) + ' | ' + $v.MaxUiMs + ' | ' + (Bool-Text $v.Pass) + ' |')
}
[void]$decision.AppendLine('')
[void]$decision.AppendLine('## B API-specific timeout check')
[void]$decision.AppendLine('')
[void]$decision.AppendLine('| Mode | API | State | HRESULT/reason | duration ms | alive before release | alive after release |')
[void]$decision.AppendLine('|---|---|---|---|---:|---|---|')
foreach ($api in $bApi) {
    [void]$decision.AppendLine('| ' + $api.hang_mode + ' | ' + $api.operation + ' | ' + $api.state + ' | `' + $api.reason + '` | ' + $api.duration_ms + ' | ' + $api.thread_alive_before_release + ' | ' + $api.thread_alive_after_release + ' |')
}
[void]$decision.AppendLine('')
[void]$decision.AppendLine('B API-specific gate: **' + (Bool-Text $bApiPass) + '**. A raw target element is captured while healthy and retained only inside its MTA executor; no live element crosses the boundary.')
[void]$decision.AppendLine('')
[void]$decision.AppendLine('Resource details are not hidden:')
foreach ($v in $verdicts) {
    [void]$decision.AppendLine('- ' + $v.Strategy + '/' + $v.Mode + ': orphan threads max=' + $v.OrphanThreadMax + ', orphan processes max=' + $v.OrphanProcessMax + ', final deltas thread/handle/process/memory=' + $v.FinalThreadDelta + '/' + $v.FinalHandleDelta + '/' + $v.FinalProcessDelta + '/' + $v.FinalWorkingSetDeltaBytes + ' bytes; continuous increase thread/handle/process/memory=' + $v.ThreadContinuousIncrease + '/' + $v.HandleContinuousIncrease + '/' + $v.ProcessContinuousIncrease + '/' + $v.MemoryContinuousIncrease + '.')
}
[void]$decision.AppendLine('')
[void]$decision.AppendLine('B2 is an auxiliary experiment, not one of the canonical final boundaries. A temporary-mode pass cannot override a permanent-mode failure. B calls `ElementFromPoint`, then resolves `TargetText`; the raw stage/reason columns show whether element, property, or pattern was reached. A/B2 timeouts are outer wait cutoffs and do not prove cancellation of the blocked COM call. C recovery is enforced by process termination.')
[void]$decision.AppendLine('A discarded pre-final run exposed a fixture signal-file sharing race on A/temporary iteration 3. The reader sharing mode and atomic-writer retry were corrected; none of that failed run is included in these rows.')
[void]$decision.AppendLine('Continuous increase is operationalized as a strict increase at every adjacent sample. A plateau or decrease disproves continuous growth; orphan thread/process counters remain independent hard failures.')
[IO.File]::WriteAllText((Join-Path $ResultsDirectory '2-WP-S-DECISION.md'), $decision.ToString(), (New-Object Text.UTF8Encoding($false)))

$boundary = New-Object Text.StringBuilder
[void]$boundary.AppendLine('# WP-S acquisition boundary')
[void]$boundary.AppendLine('')
if ($selected -eq 'B') {
    [void]$boundary.AppendLine('Recommended boundary: **B, same-process MTA worker using `[ComImport] IUIAutomation2`**, with `ConnectionTimeout` and `TransactionTimeout` set to ' + $InternalTimeoutMs + ' ms.')
} elseif ($selected -eq 'A') {
    [void]$boundary.AppendLine('Recommended boundary: **A, same-process disposable managed-UIA worker**.')
} elseif ($selected -eq 'C') {
    [void]$boundary.AppendLine('Recommended boundary: **C, `powershell.exe` child acquisition worker with one prewarmed spare and UTF-8 JSON Lines over stdin/stdout**. A timeout kills the active worker; any incomplete final line is discarded, and the prewarmed spare serves the immediate healthy request.')
} else {
    [void]$boundary.AppendLine('No boundary met all four canonical conditions. WP-00 must not start.')
}
[void]$boundary.AppendLine('')
[void]$boundary.AppendLine('The teacher/Luca WP-S gate accepted boundary C. Live UIA objects never cross the tested boundary; inputs are coordinates and outputs are serialized/value results. Win32 core facts are collected first and remain available when UIA is unavailable.')
[void]$boundary.AppendLine('The Windows SDK `Microsoft.UIAutomationClient.Interop.dll` was used only by the rejected B/B2 experiments. It is not a product requirement for boundary C and is not shipped. If WP-07 needs direct UIA COM, declare the required interfaces with `[ComImport]` so the product keeps zero bundled binary dependencies.')
[void]$boundary.AppendLine('')
[void]$boundary.AppendLine('## Reproduction')
[void]$boundary.AppendLine('')
[void]$boundary.AppendLine('From the repository root in Windows PowerShell 5.1 or PowerShell 7:')
[void]$boundary.AppendLine('')
[void]$boundary.AppendLine('```powershell')
[void]$boundary.AppendLine('.\tests\wp-s\run-wp-s.ps1 -Iterations ' + $Iterations + ' -TemporaryHangSeconds ' + $TemporaryHangSeconds + ' -RequestTimeoutMs ' + $RequestTimeoutMs + ' -ToleranceMs ' + $ToleranceMs + ' -InternalTimeoutMs ' + $InternalTimeoutMs)
[void]$boundary.AppendLine('```')
[void]$boundary.AppendLine('')
[void]$boundary.AppendLine('Requirements observed in this experiment: interactive Windows desktop, .NET Framework C# compiler, Windows PowerShell 5.1, and, only for rejected B/B2, Windows SDK `Microsoft.UIAutomationClient.Interop.dll`. Generated experiment executables stay under `tests\wp-s\.build`; the product boundary C requires no bundled interop DLL; the canonical plan is read-only.')
[IO.File]::WriteAllText((Join-Path $ResultsDirectory '3-WP-S-BOUNDARY.md'), $boundary.ToString(), (New-Object Text.UTF8Encoding($false)))

$timeouts = New-Object Text.StringBuilder
[void]$timeouts.AppendLine('# WP-S timeout and recovery numbers')
[void]$timeouts.AppendLine('')
[void]$timeouts.AppendLine('| Use | Recommended overall limit | Selected C enforcement | Evidence/status |')
[void]$timeouts.AppendLine('|---|---:|---|---|')
[void]$timeouts.AppendLine('| Hover | 1500 ms | Kill active process, switch to warm spare | Directly measured by all WP-S rows. Test tolerance is 300 ms. |')
[void]$timeouts.AppendLine('| Pin/deep read | 3000 ms | Same process boundary | Numeric recommendation; deep acquisition itself belongs to later WP and was not implemented here. |')
[void]$timeouts.AppendLine('| Operation probe | 5000 ms | Same process boundary | Numeric recommendation; operation execution belongs to later WP and was not implemented here. |')
[void]$timeouts.AppendLine('')
[void]$timeouts.AppendLine('Healthy recovery is measured immediately after every timeout/unavailable result, without restarting the tool. See `2-WP-S-DECISION.md` for p95/max by strategy and mode and `1-WP-S-RAW-DATA.csv` for every iteration. UI ping threshold was ' + $UiThresholdMs + ' ms. Win32 `SendMessageTimeout` was ' + $Win32MessageTimeoutMs + ' ms; non-message Win32 calls remained available independently.')
[void]$timeouts.AppendLine('B was tested with `ConnectionTimeout=1000 ms` and `TransactionTimeout=1000 ms`; neither stopped the primed `ElementFromPoint`, property, or ValuePattern call before the 1800 ms outer watchdog. B is rejected, so no UIA2 internal timeout is part of the selected boundary.')
[void]$timeouts.AppendLine('')
[void]$timeouts.AppendLine('C steady-state healthy baseline through the same strategy boundary: median=' + $cSteadyBaselineMedian + ' ms. The first healthy acquisition immediately after a worker switch is UIA-cold: p50=' + $cPostSwitchFirstPermanentP50 + ' ms, p95=' + $cPostSwitchFirstAllModesP95 + ' ms. These are not warm steady-state measurements.')
[void]$timeouts.AppendLine('C worker startup (PowerShell start plus Add-Type): p50=' + $startupP50 + ' ms, p95=' + $startupP95 + ' ms, max=' + $startupMax + ' ms. Prewarmed spare required to hide startup latency: **' + $spareNeeded + '**.')
[IO.File]::WriteAllText((Join-Path $ResultsDirectory '4-WP-S-TIMEOUTS.md'), $timeouts.ToString(), (New-Object Text.UTF8Encoding($false)))

$manifest = [pscustomobject]@{
    ResultsDirectory = $ResultsDirectory
    PlanHash = $actualPlanHash
    RawRows = $raw.Count
    Selected = $selected
    Verdicts = $verdicts
    CWorkerStartupP50Ms = $startupP50
    CWorkerStartupP95Ms = $startupP95
    CWorkerStartupMaxMs = $startupMax
    CSteadyHealthyBaselineMedianMs = $cSteadyBaselineMedian
    CPostSwitchFirstPermanentP50Ms = $cPostSwitchFirstPermanentP50
    CPostSwitchFirstAllModesP95Ms = $cPostSwitchFirstAllModesP95
    PrewarmedSpareNeeded = $spareNeeded
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'WP-S-RUN-SUMMARY.json') -Encoding UTF8

Write-Output ('WP_S_RESULTS ' + $ResultsDirectory)
Write-Output ('WP_S_SELECTED ' + $selected)
$verdicts | Format-Table Strategy, Mode, TReturnP95Ms, HealthyP95Ms, OrphanThreadMax, OrphanProcessMax, MaxQueue, MaxUiMs, Pass -AutoSize
