namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The PowerShell half of the same automation, written as five files.
    //
    // Workflow.ps1 is the one a person edits: one line for one thing the
    // operator did, and nothing else in it. The interval before a step, putting
    // the keyboard back before a chord, and settling afterwards are carried out
    // by the runtime around that line, so deleting the line deletes the step
    // whole. RecordedFacts.ps1 holds what the recording saw. The three Runtime
    // files hold how any of it is carried out and are not meant to be edited.
    //
    // Nothing in here presses a remembered coordinate. An element is found again
    // by the locators the recording produced, in the order it produced them, and
    // a place inside an element is a fraction of that element's rectangle as it
    // is now.
    public static class PowerShellGen
    {
        public static List<CodeFile> BuildFiles(ScriptPlan plan, StudioSession session)
        {
            List<ScriptLine> lines = ScriptModel.Lines(plan);
            List<CodeFile> files = new List<CodeFile>();
            files.Add(Make(CodeModules.Workflow, Workflow(plan, session, lines)));
            files.Add(Make(CodeModules.RecordedFacts, Facts(plan, lines)));
            files.Add(Make(CodeModules.RuntimeCore, Join(CoreLines())));
            files.Add(Make(CodeModules.RuntimeLocator, Join(LocatorLines())));
            files.Add(Make(CodeModules.RuntimeNative, Join(NativeLines())));
            return files;
        }

        private static CodeFile Make(string name, string text)
        {
            CodeFile file = new CodeFile();
            file.Language = ScriptLanguages.PowerShell;
            file.Name = name;
            file.Role = CodeRoles.Of(name);
            file.Text = text;
            return file;
        }

        private static string Join(string[] lines)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < lines.Length; index++) text.AppendLine(lines[index]);
            return text.ToString();
        }

        // ---------- Workflow.ps1 : the recorded procedure ----------

        private static string Workflow(ScriptPlan plan, StudioSession session, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("#requires -Version 5.1");
            text.AppendLine("#");
            text.AppendLine("# " + App.Name + " " + App.Version + " - the recorded procedure (PowerShell)");
            text.AppendLine("# session " + Comment(plan.SessionId) + "  " + Comment(plan.SessionTitle));
            text.AppendLine("#");
            // What this file is, and the one rule for editing it. The rest of
            // that explanation used to be another twelve lines here, which put
            // the recorded steps - the reason anybody opens this file - below
            // the bottom of the editor. It is on the screen beside the editor
            // now, in the reader's own language, where an explanation belongs.
            text.AppendLine("# One line below is one recorded step, in the order it happened. Deleting a");
            text.AppendLine("# line takes that step out and changes nothing else.");
            text.AppendLine("#");
            text.AppendLine("# It drives the real applications on this machine. Read it before you run it.");
            text.AppendLine("#");
            if (session != null && session.ValuePolicy != null)
            {
                text.AppendLine("# Value policy while recording: " + Comment(session.ValuePolicy));
            }
            for (int index = 0; index < plan.Notes.Count; index++) text.AppendLine("# - " + Comment(plan.Notes[index]));
            if (session != null)
            {
                for (int index = 0; index < session.Limits.Count && index < 12; index++)
                {
                    text.AppendLine("# limit: " + Comment(session.Limits[index]));
                }
            }
            text.AppendLine("#");
            text.AppendLine();
            text.AppendLine("[CmdletBinding()]");
            text.AppendLine("param([int]$SettleMs = 2500)");
            text.AppendLine();
            text.AppendLine("Set-StrictMode -Version 2.0");
            text.AppendLine("$ErrorActionPreference = 'Stop'");
            text.AppendLine();
            text.AppendLine("# The runtime. Dot sourced, so everything below runs in this scope.");
            text.AppendLine(". (Join-Path $PSScriptRoot '" + CodeModules.RuntimeNative + ".ps1')");
            text.AppendLine(". (Join-Path $PSScriptRoot '" + CodeModules.RuntimeLocator + ".ps1')");
            text.AppendLine(". (Join-Path $PSScriptRoot '" + CodeModules.RuntimeCore + ".ps1')");
            text.AppendLine(". (Join-Path $PSScriptRoot '" + CodeModules.RecordedFacts + ".ps1')");
            text.AppendLine();
            text.AppendLine("Start-Workflow -SettleMs $SettleMs");
            text.AppendLine();
            text.AppendLine("#region the recorded procedure - one line is one step");
            text.AppendLine();
            if (lines.Count == 0)
            {
                text.AppendLine("# This session recorded nothing that can be carried out, so there is no");
                text.AppendLine("# procedure here. Nothing is invented to fill the gap.");
                text.AppendLine();
            }
            for (int index = 0; index < lines.Count; index++)
            {
                text.AppendLine(WorkflowLine(lines[index]));
            }
            text.AppendLine();
            text.AppendLine("#endregion");
            text.AppendLine();
            text.AppendLine("Complete-Workflow");
            return text.ToString();
        }

        // One call, one comment, one line. The operation names are the nine the
        // VBA workflow uses, spelled the same way.
        private static string WorkflowLine(ScriptLine line)
        {
            StringBuilder text = new StringBuilder();
            text.Append(Pad(line.Op, 15)).Append(" '").Append(Quote(line.Id)).Append("'");
            string note = Comment(line.Comment);
            if (note.Length > 0)
            {
                while (text.Length < 34) text.Append(' ');
                text.Append("  # ").Append(note);
            }
            return text.ToString();
        }

        private static string Pad(string value, int width)
        {
            string text = value == null ? "" : value;
            StringBuilder padded = new StringBuilder(text);
            while (padded.Length < width) padded.Append(' ');
            return padded.ToString();
        }

        // ---------- RecordedFacts.ps1 : what the recording saw ----------

        private static string Facts(ScriptPlan plan, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("#");
            text.AppendLine("# " + App.Name + " " + App.Version + " - what the recording saw");
            text.AppendLine("# session " + Comment(plan.SessionId));
            text.AppendLine("#");
            text.AppendLine("# Generated from the recording. One block per line of Workflow.ps1, holding");
            text.AppendLine("# the addresses that step was reached by, the interval the operator left");
            text.AppendLine("# before it, and whatever the step carried.");
            text.AppendLine("#");
            text.AppendLine("# An address here is never a screen coordinate. A place inside an element is");
            text.AppendLine("# a fraction of that element's own rectangle, so it follows the element when");
            text.AppendLine("# the window moves.");
            text.AppendLine("#");
            text.AppendLine("# Editing this changes what a step aims at. Editing Workflow.ps1 changes");
            text.AppendLine("# which steps happen. A block left here with no line using it is harmless.");
            text.AppendLine("#");
            text.AppendLine();
            text.AppendLine("$script:AppStudioFacts = @{}");
            text.AppendLine();
            for (int index = 0; index < lines.Count; index++)
            {
                Fact(text, lines[index]);
            }
            return text.ToString();
        }

        private static void Fact(StringBuilder text, ScriptLine line)
        {
            string note = Comment(line.Comment);
            if (note.Length > 0) text.AppendLine("# " + Quote(line.Id) + "  " + note);
            text.AppendLine("$script:AppStudioFacts['" + Quote(line.Id) + "'] = @{");
            text.AppendLine("    step  = '" + Quote(line.StepId) + "'");
            text.AppendLine("    op    = '" + Quote(line.Op) + "'");
            text.AppendLine("    note  = '" + Quote(note) + "'");
            text.AppendLine("    gapMs = " + line.GapMs.ToString(CultureInfo.InvariantCulture));
            if (line.Op == ScriptOp.FindWindow)
            {
                text.AppendLine("    windowClass = '" + Quote(line.WindowClass) + "'");
                text.AppendLine("    windowTitle = '" + Quote(line.WindowTitle) + "'");
            }
            if (line.Op == ScriptOp.Unsupported)
            {
                text.AppendLine("    reason = '" + Quote(line.Reason) + "'");
            }
            if (!String.IsNullOrEmpty(line.ElementLabel))
            {
                text.AppendLine("    element = '" + Quote(line.ElementLabel) + "'");
            }
            if (line.Locators != null && line.Locators.Count > 0)
            {
                text.AppendLine("    locators = " + Locators(line.Locators));
            }
            if (line.FocusLocators != null && line.FocusLocators.Count > 0)
            {
                text.AppendLine("    focus = " + Locators(line.FocusLocators));
            }
            if (line.DropLocators != null && line.DropLocators.Count > 0)
            {
                text.AppendLine("    drop = " + Locators(line.DropLocators));
                text.AppendLine("    dropRelX = " + Number(line.DropRelX));
                text.AppendLine("    dropRelY = " + Number(line.DropRelY));
            }
            if (line.Op == ScriptOp.InvokeElement)
            {
                text.AppendLine("    button = '" + Quote(line.Button) + "'");
                text.AppendLine("    times  = " + line.Times.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("    relX   = " + Number(line.RelX));
                text.AppendLine("    relY   = " + Number(line.RelY));
                if (line.WheelDelta != 0) text.AppendLine("    wheelDelta = " + line.WheelDelta.ToString(CultureInfo.InvariantCulture));
            }
            if (line.Op == ScriptOp.SetElementText)
            {
                text.AppendLine("    text = '" + Quote(line.Text) + "'");
            }
            if (line.Op == ScriptOp.SendKeys)
            {
                text.AppendLine("    chord    = '" + Quote(line.Chord) + "'");
                text.AppendLine("    recorded = '" + Quote(line.Keys) + "'");
            }
            if (line.Op == ScriptOp.AskSecret)
            {
                text.AppendLine("    prompt = '" + Quote(line.SecretPrompt) + "'");
            }
            text.AppendLine("}");
            text.AppendLine();
        }

        // ---------- RuntimeCore.ps1 : the nine operations ----------

        private static string[] CoreLines()
        {
            return new string[]
            {
                "#",
                "# The nine operations, and the bookkeeping around them.",
                "#",
                "# These are the same nine names the VBA runtime carries, with the same",
                "# meanings. A line of Workflow.ps1 names one of them and the id of a step;",
                "# everything that step needs is looked up here rather than written out",
                "# beside the call, because the workflow is meant to be read.",
                "#",
                "# Each operation waits the interval the operator left before its step, does",
                "# the one thing, and then waits for the application to stop changing. That",
                "# is why a step is one line and why deleting the line deletes the wait with",
                "# it.",
                "#",
                "# Runtime file. You should not need to change anything here to change what",
                "# the procedure does.",
                "#",
                "",
                "$script:AppStudioWindow = $null",
                "$script:AppStudioStep = '-'",
                "$script:AppStudioSettleMs = 2500",
                "",
                "function Start-Workflow {",
                "    param([int]$SettleMs = 2500)",
                "    $script:AppStudioSettleMs = $SettleMs",
                "    $script:AppStudioWindow = $null",
                "    [void][AppStudioRun.Native]::SetProcessDPIAware()",
                "}",
                "",
                "function Complete-Workflow {",
                "    Write-Output 'The recorded procedure finished.'",
                "}",
                "",
                "function AppStudioStop {",
                "    param([string]$Reason)",
                "    throw ('App Studio step ' + $script:AppStudioStep + ' stopped: ' + $Reason)",
                "}",
                "",
                "# What the recording saw about this step. A line naming a step that is not",
                "# in RecordedFacts.ps1 stops the run: it is a workflow and a recording that",
                "# no longer agree, and guessing which one is right is not this runtime's to",
                "# make.",
                "function AppStudioBegin {",
                "    param([string]$StepId)",
                "    $script:AppStudioStep = $StepId",
                "    if (-not $script:AppStudioFacts.Contains($StepId)) {",
                "        AppStudioStop ('there is nothing recorded under this id. Either the line was ' +",
                "            'renamed in Workflow.ps1 or the block was removed from " + CodeModules.RecordedFacts + ".ps1.')",
                "    }",
                "    $fact = $script:AppStudioFacts[$StepId]",
                "    WaitGap (AppStudioFact $fact 'gapMs' 0)",
                "    return $fact",
                "}",
                "",
                "function AppStudioFact {",
                "    param($Fact, [string]$Name, $Default)",
                "    if ($null -eq $Fact) { return $Default }",
                "    if (-not $Fact.Contains($Name)) { return $Default }",
                "    return $Fact[$Name]",
                "}",
                "",
                "# Waits for the window the recording expects to be in front, then keeps it.",
                "function FindWindow {",
                "    param([string]$StepId, [int]$TimeoutMs = 10000)",
                "    $fact = AppStudioBegin $StepId",
                "    $class = [string](AppStudioFact $fact 'windowClass' '')",
                "    $title = [string](AppStudioFact $fact 'windowTitle' '')",
                "    $root = [System.Windows.Automation.AutomationElement]::RootElement",
                "    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)",
                "    $found = $null",
                "    while ([DateTime]::UtcNow -lt $deadline) {",
                "        $candidates = New-Object System.Collections.ArrayList",
                "        foreach ($child in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {",
                "            $sameClass = [string]::IsNullOrEmpty($class) -or $child.Current.ClassName -eq $class",
                "            $sameTitle = [string]::IsNullOrEmpty($title) -or $child.Current.Name -eq $title",
                "            if ($sameClass -and $sameTitle) { [void]$candidates.Add($child) }",
                "        }",
                "        if ($candidates.Count -eq 1) { $found = $candidates[0]; break }",
                "        if ($candidates.Count -gt 1) {",
                "            AppStudioStop ('more than one window matches class \"' + $class + '\" title \"' + $title + '\" (' + $candidates.Count + '). Nothing was pressed, because there is no way to tell which one the recording meant.')",
                "        }",
                "        Start-Sleep -Milliseconds 150",
                "    }",
                "    if ($null -eq $found) {",
                "        AppStudioStop ('no window matches class \"' + $class + '\" title \"' + $title + '\". The application may not be running, or its title may differ from the recorded run.')",
                "    }",
                "    $script:AppStudioWindow = $found",
                "    $handle = [IntPtr]$found.Current.NativeWindowHandle",
                "    if ($handle -ne [IntPtr]::Zero) { [void][AppStudioRun.Native]::SetForegroundWindow($handle) }",
                "    Start-Sleep -Milliseconds 120",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "function FocusElement {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    AppStudioFocus (AppStudioFact $fact 'locators' @())",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "function AppStudioFocus {",
                "    param([object[]]$Locators)",
                "    if ($null -eq $Locators -or $Locators.Count -eq 0) { return }",
                "    $element = AppStudioResolve $Locators",
                "    try { $element.SetFocus() } catch {",
                "        AppStudioStop ('the keyboard could not be put back on this element: ' + $_.Exception.Message + '. Nothing was typed.')",
                "    }",
                "    Start-Sleep -Milliseconds 80",
                "}",
                "",
                "# Presses the element. A pattern the element publishes is preferred, because",
                "# it acts on the control rather than on the screen; synthetic input is the",
                "# fallback and it needs the window in front.",
                "function InvokeElement {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    $locators = AppStudioFact $fact 'locators' @()",
                "    $button = [string](AppStudioFact $fact 'button' 'left')",
                "    $times = [int](AppStudioFact $fact 'times' 1)",
                "    $relX = [double](AppStudioFact $fact 'relX' (-1))",
                "    $relY = [double](AppStudioFact $fact 'relY' (-1))",
                "    $wheel = [int](AppStudioFact $fact 'wheelDelta' 0)",
                "    $drop = AppStudioFact $fact 'drop' @()",
                "    $element = AppStudioResolve $locators",
                "    if ($wheel -ne 0) {",
                "        $at = AppStudioPoint $element $relX $relY",
                "        AppStudioWheel $at[0] $at[1] $wheel",
                "    } elseif ($null -ne $drop -and @($drop).Count -gt 0) {",
                "        $from = AppStudioPoint $element $relX $relY",
                "        $target = AppStudioResolve $drop",
                "        $to = AppStudioPoint $target ([double](AppStudioFact $fact 'dropRelX' (-1))) ([double](AppStudioFact $fact 'dropRelY' (-1)))",
                "        AppStudioDrag $from[0] $from[1] $to[0] $to[1]",
                "    } else {",
                "        AppStudioPress $element $button $times $relX $relY",
                "    }",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "function AppStudioPress {",
                "    param($Element, [string]$Button, [int]$Times, [double]$RelX, [double]$RelY)",
                "    if ($Times -eq 1) {",
                "        $pattern = $null",
                "        if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {",
                "            $pattern.Invoke()",
                "            return",
                "        }",
                "        $toggle = $null",
                "        if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {",
                "            $toggle.Toggle()",
                "            return",
                "        }",
                "        $select = $null",
                "        if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) {",
                "            $select.Select()",
                "            return",
                "        }",
                "    }",
                "    $at = AppStudioPoint $Element $RelX $RelY",
                "    AppStudioClick $at[0] $at[1] $Button $Times",
                "}",
                "",
                "function SetElementText {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    $locators = AppStudioFact $fact 'locators' @()",
                "    $value = [string](AppStudioFact $fact 'text' '')",
                "    AppStudioFocus (AppStudioFact $fact 'focus' @())",
                "    $element = AppStudioResolve $locators",
                "    if ($element.Current.IsPassword) {",
                "        AppStudioStop 'this element is a password field. Writing into it from a script is refused.'",
                "    }",
                "    $pattern = $null",
                "    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern) -and -not $pattern.Current.IsReadOnly) {",
                "        $pattern.SetValue($value)",
                "        WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "        return",
                "    }",
                "    try { $element.SetFocus() } catch {",
                "        AppStudioStop ('this element publishes no way to set its text and the keyboard could not be put on it either: ' + $_.Exception.Message)",
                "    }",
                "    Start-Sleep -Milliseconds 60",
                "    [System.Windows.Forms.SendKeys]::SendWait('^a')",
                "    [System.Windows.Forms.SendKeys]::SendWait((AppStudioEscapeKeys $value))",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "function ReadElementText {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    $element = AppStudioResolve (AppStudioFact $fact 'locators' @())",
                "    if ($element.Current.IsPassword) { return $null }",
                "    $pattern = $null",
                "    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }",
                "    $textPattern = $null",
                "    if ($element.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$textPattern)) { return $textPattern.DocumentRange.GetText(-1) }",
                "    return $element.Current.Name",
                "}",
                "",
                "function AppStudioEscapeKeys {",
                "    param([string]$Text)",
                "    if ($null -eq $Text) { return '' }",
                "    $out = New-Object System.Text.StringBuilder",
                "    foreach ($character in $Text.ToCharArray()) {",
                "        if ('+^%~(){}[]'.IndexOf($character) -ge 0) { [void]$out.Append('{').Append($character).Append('}') }",
                "        else { [void]$out.Append($character) }",
                "    }",
                "    return $out.ToString()",
                "}",
                "",
                "# One recorded chord, sent after the keyboard has been put back where the",
                "# recording had it.",
                "function SendKeys {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    $chord = [string](AppStudioFact $fact 'chord' '')",
                "    $recorded = [string](AppStudioFact $fact 'recorded' '')",
                "    if ([string]::IsNullOrEmpty($chord)) {",
                "        AppStudioStop ('the recorded key \"' + $recorded + '\" has no equivalent that can be sent from here, so nothing was sent.')",
                "    }",
                "    AppStudioFocus (AppStudioFact $fact 'focus' @())",
                "    [System.Windows.Forms.SendKeys]::SendWait($chord)",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "function WaitGap {",
                "    param([int]$Ms)",
                "    if ($Ms -le 0) { return }",
                "    $wait = $Ms",
                "    if ($wait -lt 120) { $wait = 120 }",
                "    if ($wait -gt 4000) { $wait = 4000 }",
                "    Start-Sleep -Milliseconds $wait",
                "}",
                "",
                "# Waits for the front window to stop changing, up to a stated ceiling.",
                "# Reaching the ceiling is a measured wait, not a failure.",
                "function WaitIdle {",
                "    param([int]$BudgetMs = 2500)",
                "    $watch = [Diagnostics.Stopwatch]::StartNew()",
                "    $lastFront = [IntPtr]::Zero",
                "    $stable = 0",
                "    while ($watch.ElapsedMilliseconds -lt $BudgetMs) {",
                "        $front = [AppStudioRun.Native]::GetForegroundWindow()",
                "        if ($front -eq $lastFront) { $stable = $stable + 1 } else { $stable = 0 }",
                "        $lastFront = $front",
                "        if ($stable -ge 2) { break }",
                "        Start-Sleep -Milliseconds 80",
                "    }",
                "    $watch.Stop()",
                "}",
                "",
                "# A value the recording deliberately never kept. It is asked for here and it",
                "# is never written to a file, a log or the console.",
                "function AskSecret {",
                "    param([string]$StepId)",
                "    $fact = AppStudioBegin $StepId",
                "    $prompt = [string](AppStudioFact $fact 'prompt' 'This step needs a value the recording did not keep. Type it now')",
                "    $secure = Read-Host -Prompt $prompt -AsSecureString",
                "    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)",
                "    try {",
                "        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)",
                "        if ([string]::IsNullOrEmpty($plain)) { AppStudioStop 'no value was supplied for a step that needs one.' }",
                "        AppStudioFocus (AppStudioFact $fact 'locators' @())",
                "        Start-Sleep -Milliseconds 60",
                "        [System.Windows.Forms.SendKeys]::SendWait((AppStudioEscapeKeys $plain))",
                "    } finally {",
                "        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)",
                "    }",
                "    WaitIdle -BudgetMs $script:AppStudioSettleMs",
                "}",
                "",
                "# The recording holds something no address can be built from. The run stops",
                "# here with the reason rather than pressing a remembered coordinate.",
                "function Unsupported {",
                "    param([string]$StepId)",
                "    $script:AppStudioStep = $StepId",
                "    $reason = 'this step has no address that survives a restart.'",
                "    if ($script:AppStudioFacts.Contains($StepId)) {",
                "        $reason = [string](AppStudioFact $script:AppStudioFacts[$StepId] 'reason' $reason)",
                "    }",
                "    AppStudioStop $reason",
                "}",
                ""
            };
        }

        // ---------- RuntimeLocator.ps1 : finding the element again ----------

        private static string[] LocatorLines()
        {
            return new string[]
            {
                "#",
                "# Turning a recorded address back into the element that is on screen now.",
                "#",
                "# The locators are tried in the order the recording produced them: the",
                "# identifier the application chose first, a position among siblings last. A",
                "# locator that matches more than one element decides nothing and is not used",
                "# as if it had; the next one is tried instead, and when all of them are",
                "# spent the run stops rather than pressing something it cannot name.",
                "#",
                "# Runtime file. You should not need to change anything here to change what",
                "# the procedure does.",
                "#",
                "",
                "function AppStudioLabel {",
                "    param($Element)",
                "    $type = $Element.Current.ControlType",
                "    $name = $Element.Current.Name",
                "    $short = if ($null -eq $type) { '?' } else { $type.ProgrammaticName -replace '^ControlType\\.', '' }",
                "    if ([string]::IsNullOrEmpty($name)) { return $short }",
                "    return ($short + ' \"' + $name + '\"')",
                "}",
                "",
                "# The readable hierarchy path, rebuilt the way the recording wrote it. It is",
                "# the weakest of the strategies used here and it moves when the application's",
                "# layout does, which is why it is only ever tried after the identifiers above",
                "# it have failed.",
                "function AppStudioPath {",
                "    param($Element, $Root)",
                "    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker",
                "    $parts = New-Object System.Collections.ArrayList",
                "    $node = $Element",
                "    $guard = 0",
                "    while ($null -ne $node -and $guard -lt 40) {",
                "        $guard = $guard + 1",
                "        [void]$parts.Insert(0, (AppStudioLabel $node))",
                "        if ($node.Current.NativeWindowHandle -eq $Root.Current.NativeWindowHandle -and $guard -gt 1) { break }",
                "        try { $node = $walker.GetParent($node) } catch { $node = $null }",
                "        if ($null -ne $node -and $null -ne $Root -and $node.Current.NativeWindowHandle -eq [System.Windows.Automation.AutomationElement]::RootElement.Current.NativeWindowHandle) { break }",
                "    }",
                "    return ($parts -join ' > ')",
                "}",
                "",
                "# Windows Forms and several toolkits append a per process number to the",
                "# window class. The number changes on every launch, so the volatile tail is",
                "# dropped rather than compared as if it were stable.",
                "function AppStudioStableClass {",
                "    param([string]$Value)",
                "    if ([string]::IsNullOrEmpty($Value)) { return $Value }",
                "    $marker = $Value.IndexOf('.app.', [StringComparison]::OrdinalIgnoreCase)",
                "    if ($marker -gt 0) { return $Value.Substring(0, $marker) }",
                "    if ($Value.StartsWith('WindowsForms10.', [StringComparison]::OrdinalIgnoreCase)) {",
                "        $dot = $Value.IndexOf('.', 'WindowsForms10.'.Length)",
                "        if ($dot -gt 0) { return $Value.Substring(0, $dot) }",
                "    }",
                "    return $Value",
                "}",
                "",
                "# One locator against the window as it is now. Returns every element it",
                "# matched: deciding what to do about none or many is the caller's job, and",
                "# neither answer is turned into a guess here.",
                "function AppStudioMatch {",
                "    param($Locator)",
                "    $window = $script:AppStudioWindow",
                "    if ($null -eq $window) { AppStudioStop 'no window has been found yet, so there is nothing to look inside.' }",
                "    $hits = New-Object System.Collections.ArrayList",
                "    $strategy = [string]$Locator['strategy']",
                "    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)",
                "    $classSeen = 0",
                "    foreach ($element in $all) {",
                "        $hit = $false",
                "        if ($strategy -eq 'uia.automationId') {",
                "            $hit = ($element.Current.AutomationId -eq [string]$Locator['automationId'])",
                "            if ($hit -and $Locator.Contains('controlType') -and -not [string]::IsNullOrEmpty([string]$Locator['controlType'])) {",
                "                $short = $element.Current.ControlType.ProgrammaticName -replace '^ControlType\\.', ''",
                "                $hit = ($short -eq [string]$Locator['controlType'])",
                "            }",
                "        } elseif ($strategy -eq 'uia.nameControlType') {",
                "            $short = $element.Current.ControlType.ProgrammaticName -replace '^ControlType\\.', ''",
                "            $hit = ($element.Current.Name -eq [string]$Locator['name']) -and ($short -eq [string]$Locator['controlType'])",
                "        } elseif ($strategy -eq 'uia.treePath') {",
                "            $hit = ((AppStudioPath $element $window) -eq [string]$Locator['treePath'])",
                "        } elseif ($strategy -eq 'win32.ctrlId') {",
                "            $handle = [IntPtr]$element.Current.NativeWindowHandle",
                "            if ($handle -ne [IntPtr]::Zero -and ((AppStudioStableClass $element.Current.ClassName) -eq [string]$Locator['className'])) {",
                "                $hit = ([AppStudioRun.Native]::GetWindowLong($handle, -12) -eq [int]$Locator['ctrlId'])",
                "            }",
                "        } elseif ($strategy -eq 'win32.classIndex') {",
                "            if ((AppStudioStableClass $element.Current.ClassName) -eq [string]$Locator['className']) {",
                "                if ($classSeen -eq [int]$Locator['classIndex']) { $hit = $true }",
                "                $classSeen = $classSeen + 1",
                "            }",
                "        }",
                "        if ($hit) { [void]$hits.Add($element) }",
                "    }",
                "    # The comma keeps the list a list. PowerShell unrolls a returned",
                "    # collection, and a single match coming back as one element has no Count",
                "    # for the caller to read.",
                "    return ,$hits",
                "}",
                "",
                "function AppStudioResolve {",
                "    param([object[]]$Locators)",
                "    if ($null -eq $Locators -or $Locators.Count -eq 0) {",
                "        AppStudioStop 'this step has no address at all, so there is nothing to look for. Its recorded position is a description, never an address.'",
                "    }",
                "    $ambiguous = @()",
                "    foreach ($locator in $Locators) {",
                "        $hits = @(AppStudioMatch $locator)",
                "        if ($hits.Count -eq 1) { return $hits[0] }",
                "        if ($hits.Count -gt 1) { $ambiguous += ([string]$locator['strategy'] + ' matched ' + $hits.Count) }",
                "    }",
                "    if ($ambiguous.Count -gt 0) {",
                "        AppStudioStop ('the element could not be told apart from others like it (' + ($ambiguous -join '; ') + '). Nothing was sent.')",
                "    }",
                "    AppStudioStop 'the element could not be found again in this window. Nothing was sent.'",
                "}",
                "",
                "# Where inside the element to act, as a fraction of the rectangle it has",
                "# right now. Never a coordinate the recording remembered.",
                "function AppStudioPoint {",
                "    param($Element, [double]$RelX, [double]$RelY)",
                "    $rect = $Element.Current.BoundingRectangle",
                "    if ($rect.Width -le 0 -or $rect.Height -le 0) {",
                "        AppStudioStop 'the element was found but has no usable rectangle right now, so there is nowhere to act.'",
                "    }",
                "    $fx = if ($RelX -lt 0) { 0.5 } else { $RelX }",
                "    $fy = if ($RelY -lt 0) { 0.5 } else { $RelY }",
                "    return @([int]($rect.X + $rect.Width * $fx), [int]($rect.Y + $rect.Height * $fy))",
                "}",
                ""
            };
        }

        // ---------- RuntimeNative.ps1 : the OS plumbing ----------

        private static string[] NativeLines()
        {
            return new string[]
            {
                "#",
                "# The parts that talk to Windows directly: the declarations, the pointer,",
                "# and turning a desktop point into what SendInput wants.",
                "#",
                "# Runtime file. You should not need to change anything here to change what",
                "# the procedure does.",
                "#",
                "",
                "Add-Type -AssemblyName UIAutomationClient",
                "Add-Type -AssemblyName UIAutomationTypes",
                "Add-Type -AssemblyName System.Windows.Forms",
                "",
                "if ($null -eq ('AppStudioRun.Native' -as [type])) {",
                "    Add-Type -TypeDefinition @\"",
                "using System;",
                "using System.Runtime.InteropServices;",
                "namespace AppStudioRun {",
                "  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }",
                "  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; public int pad1; public int pad2; }",
                "  public static class Native {",
                "    [DllImport(\"user32.dll\")] public static extern bool SetProcessDPIAware();",
                "    [DllImport(\"user32.dll\")] public static extern IntPtr GetForegroundWindow();",
                "    [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr h);",
                "    [DllImport(\"user32.dll\")] public static extern uint SendInput(uint n, INPUT[] p, int size);",
                "    [DllImport(\"user32.dll\")] public static extern bool GetCursorPos(out int x, out int y);",
                "    [DllImport(\"user32.dll\")] public static extern int GetSystemMetrics(int i);",
                "    [DllImport(\"user32.dll\", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder b, int n);",
                "    [DllImport(\"user32.dll\", EntryPoint = \"GetWindowLongW\")] public static extern int GetWindowLong(IntPtr h, int index);",
                "    public static void Mouse(uint flags, int x, int y, uint data) {",
                "      INPUT[] one = new INPUT[1];",
                "      one[0].type = 0;",
                "      one[0].mi.dx = x; one[0].mi.dy = y; one[0].mi.mouseData = data; one[0].mi.dwFlags = flags;",
                "      SendInput(1, one, Marshal.SizeOf(typeof(INPUT)));",
                "    }",
                "  }",
                "}",
                "\"@",
                "}",
                "",
                "# A desktop point in the 0..65535 space SendInput uses for an absolute move.",
                "function AppStudioAbsolute {",
                "    param([int]$X, [int]$Y)",
                "    $screenW = [AppStudioRun.Native]::GetSystemMetrics(78)",
                "    $screenH = [AppStudioRun.Native]::GetSystemMetrics(79)",
                "    if ($screenW -le 0 -or $screenH -le 0) { AppStudioStop 'the virtual desktop reported no size, so no pointer position can be expressed.' }",
                "    $originX = [AppStudioRun.Native]::GetSystemMetrics(76)",
                "    $originY = [AppStudioRun.Native]::GetSystemMetrics(77)",
                "    return @([int](($X - $originX) * 65535 / $screenW), [int](($Y - $originY) * 65535 / $screenH))",
                "}",
                "",
                "function AppStudioClick {",
                "    param([int]$X, [int]$Y, [string]$Button, [int]$Times)",
                "    $at = AppStudioAbsolute $X $Y",
                "    $down = 0x0002; $up = 0x0004",
                "    if ($Button -eq 'right') { $down = 0x0008; $up = 0x0010 }",
                "    elseif ($Button -eq 'middle') { $down = 0x0020; $up = 0x0040 }",
                "    [AppStudioRun.Native]::Mouse(0x8001, $at[0], $at[1], 0)",
                "    Start-Sleep -Milliseconds 40",
                "    for ($i = 0; $i -lt $Times; $i++) {",
                "        [AppStudioRun.Native]::Mouse(0x8001 -bor $down, $at[0], $at[1], 0)",
                "        [AppStudioRun.Native]::Mouse(0x8001 -bor $up, $at[0], $at[1], 0)",
                "        if ($i -lt ($Times - 1)) { Start-Sleep -Milliseconds 60 }",
                "    }",
                "}",
                "",
                "function AppStudioWheel {",
                "    param([int]$X, [int]$Y, [int]$Delta)",
                "    $at = AppStudioAbsolute $X $Y",
                "    [AppStudioRun.Native]::Mouse(0x8001, $at[0], $at[1], 0)",
                "    Start-Sleep -Milliseconds 40",
                "    # mouseData carries a signed turn in an unsigned field.",
                "    $data = if ($Delta -lt 0) { [uint32](4294967296 + $Delta) } else { [uint32]$Delta }",
                "    [AppStudioRun.Native]::Mouse(0x0800, 0, 0, $data)",
                "}",
                "",
                "function AppStudioDrag {",
                "    param([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY)",
                "    $from = AppStudioAbsolute $FromX $FromY",
                "    $to = AppStudioAbsolute $ToX $ToY",
                "    [AppStudioRun.Native]::Mouse(0x8001, $from[0], $from[1], 0)",
                "    Start-Sleep -Milliseconds 60",
                "    [AppStudioRun.Native]::Mouse(0x8002, $from[0], $from[1], 0)",
                "    Start-Sleep -Milliseconds 60",
                "    [AppStudioRun.Native]::Mouse(0x8001, $to[0], $to[1], 0)",
                "    Start-Sleep -Milliseconds 60",
                "    [AppStudioRun.Native]::Mouse(0x8004, $to[0], $to[1], 0)",
                "}",
                ""
            };
        }

        // ---------- shared helpers ----------

        public static string SecretPrompt(ScriptOp op)
        {
            string where = String.IsNullOrEmpty(op.ElementLabel) ? "this field" : op.ElementLabel;
            return op.StepId + ": the recording kept no value for " + where + ". Type it now";
        }

        private static string Number(double value)
        {
            if (value < 0) return "-1";
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string Locators(List<ElementLocator> locators)
        {
            StringBuilder text = new StringBuilder();
            text.Append("@(");
            for (int index = 0; index < locators.Count; index++)
            {
                if (index != 0) text.Append(", ");
                ElementLocator locator = locators[index];
                text.Append("@{ strategy = '").Append(Quote(locator.Strategy)).Append("'");
                if (!String.IsNullOrEmpty(locator.AutomationId)) text.Append("; automationId = '").Append(Quote(locator.AutomationId)).Append("'");
                if (!String.IsNullOrEmpty(locator.Name)) text.Append("; name = '").Append(Quote(locator.Name)).Append("'");
                if (!String.IsNullOrEmpty(locator.ControlType)) text.Append("; controlType = '").Append(Quote(locator.ControlType)).Append("'");
                if (!String.IsNullOrEmpty(locator.ClassName)) text.Append("; className = '").Append(Quote(locator.ClassName)).Append("'");
                if (!String.IsNullOrEmpty(locator.TreePath)) text.Append("; treePath = '").Append(Quote(locator.TreePath)).Append("'");
                if (locator.CtrlId != 0) text.Append("; ctrlId = '").Append(locator.CtrlId.ToString(CultureInfo.InvariantCulture)).Append("'");
                if (locator.ClassIndex >= 0) text.Append("; classIndex = ").Append(locator.ClassIndex.ToString(CultureInfo.InvariantCulture));
                text.Append(" }");
            }
            text.Append(")");
            return text.ToString();
        }

        // The recorded chord, written the way SendKeys spells it. A key with no
        // equivalent comes back empty, and the generated line then stops with
        // the recorded name in it rather than sending something else.
        public static string SendKeysChord(string chord)
        {
            if (String.IsNullOrEmpty(chord)) return "";
            string[] pieces = chord.Split('+');
            StringBuilder prefix = new StringBuilder();
            for (int index = 0; index < pieces.Length - 1; index++)
            {
                string modifier = pieces[index].Trim();
                if (String.Equals(modifier, "Ctrl", StringComparison.OrdinalIgnoreCase)) prefix.Append('^');
                else if (String.Equals(modifier, "Alt", StringComparison.OrdinalIgnoreCase)) prefix.Append('%');
                else if (String.Equals(modifier, "Shift", StringComparison.OrdinalIgnoreCase)) prefix.Append('+');
                else return "";
            }
            string key = pieces[pieces.Length - 1].Trim();
            if (key.Length == 0) return "";
            string named = NamedKey(key);
            if (named != null) return prefix.ToString() + named;
            if (key.Length == 1)
            {
                char single = key[0];
                if ((single >= 'A' && single <= 'Z') || (single >= 'a' && single <= 'z') || (single >= '0' && single <= '9'))
                {
                    return prefix.ToString() + Char.ToLowerInvariant(single).ToString();
                }
            }
            return "";
        }

        private static string NamedKey(string key)
        {
            if (String.Equals(key, "Enter", StringComparison.OrdinalIgnoreCase)) return "{ENTER}";
            if (String.Equals(key, "Tab", StringComparison.OrdinalIgnoreCase)) return "{TAB}";
            if (String.Equals(key, "Esc", StringComparison.OrdinalIgnoreCase)) return "{ESC}";
            if (String.Equals(key, "Escape", StringComparison.OrdinalIgnoreCase)) return "{ESC}";
            if (String.Equals(key, "Space", StringComparison.OrdinalIgnoreCase)) return " ";
            if (String.Equals(key, "Back", StringComparison.OrdinalIgnoreCase)) return "{BACKSPACE}";
            if (String.Equals(key, "Delete", StringComparison.OrdinalIgnoreCase)) return "{DEL}";
            if (String.Equals(key, "Home", StringComparison.OrdinalIgnoreCase)) return "{HOME}";
            if (String.Equals(key, "End", StringComparison.OrdinalIgnoreCase)) return "{END}";
            if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f'))
            {
                int number;
                if (Int32.TryParse(key.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= 12)
                {
                    return "{F" + number.ToString(CultureInfo.InvariantCulture) + "}";
                }
            }
            return null;
        }

        // A single quoted PowerShell string ends at the first quote in it, so
        // every quote inside one is doubled.
        public static string Quote(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return Comment(value).Replace("'", "''");
        }

        // Anything written into a comment or a literal has to stay on the line
        // it was put on.
        public static string Comment(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\r') continue;
                if (character == '\n') { text.Append(" / "); continue; }
                text.Append(character);
            }
            return text.ToString();
        }
    }
}
