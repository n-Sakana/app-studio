namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The PowerShell half of the same automation. It is a whole script, not a
    // sketch: the operation library at the top is what makes the recorded part
    // underneath readable, and the recorded part names the same nine operations
    // the VBA module names.
    //
    // Nothing in here presses a remembered coordinate. An element is found
    // again by the locators the recording produced, in the order it produced
    // them, and a place inside an element is a fraction of that element's
    // rectangle as it is now.
    public static class PowerShellGen
    {
        public static string Build(ScriptPlan plan, StudioSession session)
        {
            StringBuilder text = new StringBuilder();
            Header(text, plan, session);
            Library(text);
            Procedure(text, plan);
            return text.ToString();
        }

        private static void Header(StringBuilder text, ScriptPlan plan, StudioSession session)
        {
            text.AppendLine("#requires -Version 5.1");
            text.AppendLine("#");
            text.AppendLine("# " + App.Name + " " + App.Version + " - generated automation (PowerShell)");
            text.AppendLine("# session " + Comment(plan.SessionId) + "  " + Comment(plan.SessionTitle));
            text.AppendLine("#");
            text.AppendLine("# This drives the real applications on this machine. Read it before you run it.");
            text.AppendLine("# It was written from a recording; it is a starting point that is meant to be edited.");
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
        }

        // The nine operations. Their names and their meaning are the ones in
        // ScriptModel, so the VBA module below says the same words about the
        // same recording.
        private static void Library(StringBuilder text)
        {
            string[] lines = new string[]
            {
                "#region operation library - the same nine operations the VBA module has",
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
                "[void][AppStudioRun.Native]::SetProcessDPIAware()",
                "",
                "$script:AppStudioWindow = $null",
                "$script:AppStudioStep = '-'",
                "",
                "function AppStudioStop {",
                "    param([string]$Reason)",
                "    throw ('App Studio step ' + $script:AppStudioStep + ' stopped: ' + $Reason)",
                "}",
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
                "# The readable hierarchy path, rebuilt the way the recording wrote it.",
                "# It is the weakest of the strategies used here and it moves when the",
                "# application's layout does, which is why it is only ever tried after the",
                "# identifiers above it have failed.",
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
                "# Waits for the window the recording expects to be in front, then keeps it.",
                "function FindWindow {",
                "    param([string]$Class, [string]$Title, [int]$TimeoutMs = 10000)",
                "    $root = [System.Windows.Automation.AutomationElement]::RootElement",
                "    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)",
                "    $found = $null",
                "    while ([DateTime]::UtcNow -lt $deadline) {",
                "        $candidates = New-Object System.Collections.ArrayList",
                "        foreach ($child in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {",
                "            $sameClass = [string]::IsNullOrEmpty($Class) -or $child.Current.ClassName -eq $Class",
                "            $sameTitle = [string]::IsNullOrEmpty($Title) -or $child.Current.Name -eq $Title",
                "            if ($sameClass -and $sameTitle) { [void]$candidates.Add($child) }",
                "        }",
                "        if ($candidates.Count -eq 1) { $found = $candidates[0]; break }",
                "        if ($candidates.Count -gt 1) {",
                "            AppStudioStop ('more than one window matches class \"' + $Class + '\" title \"' + $Title + '\" (' + $candidates.Count + '). Nothing was pressed, because there is no way to tell which one the recording meant.')",
                "        }",
                "        Start-Sleep -Milliseconds 150",
                "    }",
                "    if ($null -eq $found) {",
                "        AppStudioStop ('no window matches class \"' + $Class + '\" title \"' + $Title + '\". The application may not be running, or its title may differ from the recorded run.')",
                "    }",
                "    $script:AppStudioWindow = $found",
                "    $handle = [IntPtr]$found.Current.NativeWindowHandle",
                "    if ($handle -ne [IntPtr]::Zero) { [void][AppStudioRun.Native]::SetForegroundWindow($handle) }",
                "    Start-Sleep -Milliseconds 120",
                "    return $found",
                "}",
                "",
                "# One locator against the window as it is now. Returns every element it",
                "# matched: deciding what to do about none or many is the caller's job,",
                "# and neither answer is turned into a guess here.",
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
                "    # collection, and a single match coming back as one element has no",
                "    # Count for the caller to read.",
                "    return ,$hits",
                "}",
                "",
                "# The locators in the order the recording produced them: the identifier the",
                "# application chose first, a position among siblings last. A locator that",
                "# matches more than one element decides nothing and is not used as if it",
                "# had; the next one is tried instead, and when all of them are spent the",
                "# run stops rather than pressing something it cannot name.",
                "function AppStudioResolve {",
                "    param([object[]]$Locators)",
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
                "",
                "function AppStudioClick {",
                "    param([int]$X, [int]$Y, [string]$Button, [int]$Times)",
                "    $screenW = [AppStudioRun.Native]::GetSystemMetrics(78)",
                "    $screenH = [AppStudioRun.Native]::GetSystemMetrics(79)",
                "    $originX = [AppStudioRun.Native]::GetSystemMetrics(76)",
                "    $originY = [AppStudioRun.Native]::GetSystemMetrics(77)",
                "    if ($screenW -le 0 -or $screenH -le 0) { AppStudioStop 'the virtual desktop reported no size, so no pointer position can be expressed.' }",
                "    $ax = [int](($X - $originX) * 65535 / $screenW)",
                "    $ay = [int](($Y - $originY) * 65535 / $screenH)",
                "    $down = 0x0002; $up = 0x0004",
                "    if ($Button -eq 'right') { $down = 0x0008; $up = 0x0010 }",
                "    elseif ($Button -eq 'middle') { $down = 0x0020; $up = 0x0040 }",
                "    [AppStudioRun.Native]::Mouse(0x8001, $ax, $ay, 0)",
                "    Start-Sleep -Milliseconds 40",
                "    for ($i = 0; $i -lt $Times; $i++) {",
                "        [AppStudioRun.Native]::Mouse(0x8001 -bor $down, $ax, $ay, 0)",
                "        [AppStudioRun.Native]::Mouse(0x8001 -bor $up, $ax, $ay, 0)",
                "        if ($i -lt ($Times - 1)) { Start-Sleep -Milliseconds 60 }",
                "    }",
                "}",
                "",
                "# Presses the element. A pattern the element publishes is preferred,",
                "# because it acts on the control rather than on the screen; synthetic",
                "# input is the fallback and it needs the window in front.",
                "function InvokeElement {",
                "    param([object[]]$Locators, [string]$Button = 'left', [int]$Times = 1, [double]$RelX = -1, [double]$RelY = -1, [int]$WheelDelta = 0, [object[]]$DropLocators = $null, [double]$DropRelX = -1, [double]$DropRelY = -1)",
                "    $element = AppStudioResolve $Locators",
                "    if ($WheelDelta -ne 0) {",
                "        $at = AppStudioPoint $element $RelX $RelY",
                "        $screenW = [AppStudioRun.Native]::GetSystemMetrics(78)",
                "        $screenH = [AppStudioRun.Native]::GetSystemMetrics(79)",
                "        $ax = [int]((($at[0]) - [AppStudioRun.Native]::GetSystemMetrics(76)) * 65535 / $screenW)",
                "        $ay = [int]((($at[1]) - [AppStudioRun.Native]::GetSystemMetrics(77)) * 65535 / $screenH)",
                "        [AppStudioRun.Native]::Mouse(0x8001, $ax, $ay, 0)",
                "        Start-Sleep -Milliseconds 40",
                "        # mouseData carries a signed turn in an unsigned field.",
                "        $data = if ($WheelDelta -lt 0) { [uint32](4294967296 + $WheelDelta) } else { [uint32]$WheelDelta }",
                "        [AppStudioRun.Native]::Mouse(0x0800, 0, 0, $data)",
                "        return",
                "    }",
                "    if ($null -ne $DropLocators) {",
                "        $from = AppStudioPoint $element $RelX $RelY",
                "        $target = AppStudioResolve $DropLocators",
                "        $to = AppStudioPoint $target $DropRelX $DropRelY",
                "        $screenW = [AppStudioRun.Native]::GetSystemMetrics(78)",
                "        $screenH = [AppStudioRun.Native]::GetSystemMetrics(79)",
                "        $ox = [AppStudioRun.Native]::GetSystemMetrics(76)",
                "        $oy = [AppStudioRun.Native]::GetSystemMetrics(77)",
                "        $fx = [int]((($from[0]) - $ox) * 65535 / $screenW); $fy = [int]((($from[1]) - $oy) * 65535 / $screenH)",
                "        $tx = [int]((($to[0]) - $ox) * 65535 / $screenW); $ty = [int]((($to[1]) - $oy) * 65535 / $screenH)",
                "        [AppStudioRun.Native]::Mouse(0x8001, $fx, $fy, 0)",
                "        Start-Sleep -Milliseconds 60",
                "        [AppStudioRun.Native]::Mouse(0x8002, $fx, $fy, 0)",
                "        Start-Sleep -Milliseconds 60",
                "        [AppStudioRun.Native]::Mouse(0x8001, $tx, $ty, 0)",
                "        Start-Sleep -Milliseconds 60",
                "        [AppStudioRun.Native]::Mouse(0x8004, $tx, $ty, 0)",
                "        return",
                "    }",
                "    if ($Times -eq 1) {",
                "        $pattern = $null",
                "        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {",
                "            $pattern.Invoke()",
                "            return",
                "        }",
                "        $toggle = $null",
                "        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {",
                "            $toggle.Toggle()",
                "            return",
                "        }",
                "        $select = $null",
                "        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$select)) {",
                "            $select.Select()",
                "            return",
                "        }",
                "    }",
                "    $at = AppStudioPoint $element $RelX $RelY",
                "    AppStudioClick $at[0] $at[1] $Button $Times",
                "}",
                "",
                "function FocusElement {",
                "    param([object[]]$Locators)",
                "    $element = AppStudioResolve $Locators",
                "    try { $element.SetFocus() } catch {",
                "        AppStudioStop ('the keyboard could not be put back on this element: ' + $_.Exception.Message + '. Nothing was typed.')",
                "    }",
                "    Start-Sleep -Milliseconds 80",
                "}",
                "",
                "function SetElementText {",
                "    param([object[]]$Locators, [string]$Text)",
                "    $element = AppStudioResolve $Locators",
                "    if ($element.Current.IsPassword) {",
                "        AppStudioStop 'this element is a password field. Writing into it from a script is refused.'",
                "    }",
                "    $value = $null",
                "    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value) -and -not $value.Current.IsReadOnly) {",
                "        $value.SetValue($Text)",
                "        return",
                "    }",
                "    try { $element.SetFocus() } catch {",
                "        AppStudioStop ('this element publishes no way to set its text and the keyboard could not be put on it either: ' + $_.Exception.Message)",
                "    }",
                "    Start-Sleep -Milliseconds 60",
                "    [System.Windows.Forms.SendKeys]::SendWait('^a')",
                "    [System.Windows.Forms.SendKeys]::SendWait((AppStudioEscapeKeys $Text))",
                "}",
                "",
                "function ReadElementText {",
                "    param([object[]]$Locators)",
                "    $element = AppStudioResolve $Locators",
                "    if ($element.Current.IsPassword) { return $null }",
                "    $value = $null",
                "    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value)) { return $value.Current.Value }",
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
                "function SendKeys {",
                "    param([string]$Chord, [string]$Recorded)",
                "    if ([string]::IsNullOrEmpty($Chord)) {",
                "        AppStudioStop ('the recorded key \"' + $Recorded + '\" has no equivalent that can be sent from here, so nothing was sent.')",
                "    }",
                "    [System.Windows.Forms.SendKeys]::SendWait($Chord)",
                "}",
                "",
                "function WaitGap {",
                "    param([int]$Ms)",
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
                "# A value the recording deliberately never kept. It is asked for here and",
                "# it is never written to a file, a log or the console.",
                "function AskSecret {",
                "    param([object[]]$Locators, [string]$Prompt)",
                "    $secure = Read-Host -Prompt $Prompt -AsSecureString",
                "    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)",
                "    try {",
                "        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)",
                "        if ([string]::IsNullOrEmpty($plain)) { AppStudioStop 'no value was supplied for a step that needs one.' }",
                "        $element = AppStudioResolve $Locators",
                "        try { $element.SetFocus() } catch { }",
                "        Start-Sleep -Milliseconds 60",
                "        [System.Windows.Forms.SendKeys]::SendWait((AppStudioEscapeKeys $plain))",
                "    } finally {",
                "        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)",
                "    }",
                "}",
                "",
                "function Unsupported {",
                "    param([string]$Reason)",
                "    AppStudioStop $Reason",
                "}",
                "",
                "#endregion",
                ""
            };
            for (int index = 0; index < lines.Length; index++) text.AppendLine(lines[index]);
        }

        private static void Procedure(StringBuilder text, ScriptPlan plan)
        {
            text.AppendLine("#region the recorded procedure");
            text.AppendLine();
            for (int index = 0; index < plan.Ops.Count; index++)
            {
                ScriptOp op = plan.Ops[index];
                text.AppendLine("# " + op.StepId + "  " + Comment(op.Headline));
                text.AppendLine("$script:AppStudioStep = '" + Quote(op.StepId) + "'");
                text.AppendLine(Line(op));
                text.AppendLine();
            }
            text.AppendLine("#endregion");
            text.AppendLine();
            text.AppendLine("Write-Output 'The recorded procedure finished.'");
        }

        private static string Line(ScriptOp op)
        {
            if (op.Op == ScriptOp.FindWindow)
            {
                return "FindWindow -Class '" + Quote(op.WindowClass) + "' -Title '" + Quote(op.WindowTitle) + "'";
            }
            if (op.Op == ScriptOp.WaitGap)
            {
                return "WaitGap -Ms " + op.GapMs.ToString(CultureInfo.InvariantCulture);
            }
            if (op.Op == ScriptOp.WaitIdle)
            {
                return "WaitIdle -BudgetMs $SettleMs";
            }
            if (op.Op == ScriptOp.Unsupported)
            {
                return "Unsupported -Reason '" + Quote(op.Reason) + "'";
            }
            if (op.Op == ScriptOp.SendKeys)
            {
                return "SendKeys -Chord '" + Quote(SendKeysChord(op.Keys)) + "' -Recorded '" + Quote(op.Keys) + "'";
            }
            if (op.Op == ScriptOp.FocusElement)
            {
                return "FocusElement -Locators " + Locators(op.Locators);
            }
            if (op.Op == ScriptOp.SetElementText)
            {
                return "SetElementText -Locators " + Locators(op.Locators) + " -Text '" + Quote(op.Text) + "'";
            }
            if (op.Op == ScriptOp.AskSecret)
            {
                return "AskSecret -Locators " + Locators(op.Locators) + " -Prompt '" + Quote(SecretPrompt(op)) + "'";
            }
            if (op.Op == ScriptOp.ReadElementText)
            {
                return "ReadElementText -Locators " + Locators(op.Locators);
            }
            StringBuilder line = new StringBuilder();
            line.Append("InvokeElement -Locators ").Append(Locators(op.Locators));
            line.Append(" -Button '").Append(Quote(op.Button)).Append("'");
            line.Append(" -Times ").Append(op.Times.ToString(CultureInfo.InvariantCulture));
            line.Append(" -RelX ").Append(Number(op.RelX)).Append(" -RelY ").Append(Number(op.RelY));
            if (op.WheelDelta != 0) line.Append(" -WheelDelta ").Append(op.WheelDelta.ToString(CultureInfo.InvariantCulture));
            if (op.DropLocators != null && op.DropLocators.Count > 0)
            {
                line.Append(" -DropLocators ").Append(Locators(op.DropLocators));
                line.Append(" -DropRelX ").Append(Number(op.DropRelX)).Append(" -DropRelY ").Append(Number(op.DropRelY));
            }
            return line.ToString();
        }

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
