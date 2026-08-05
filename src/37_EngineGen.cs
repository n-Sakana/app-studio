namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The engine half of the same automation, written as five C# modules.
    //
    // Nobody writes several hundred lines of PowerShell by hand, so nothing here
    // asks anybody to. The flow and the machinery are C#; PowerShell is left with
    // the one job it is good at - naming the assemblies, compiling this, and
    // calling the entry point - and the build puts the batch header, that wrapper
    // and these modules into a single .cmd file that runs by itself.
    //
    // Workflow.cs is the one a person edits: one line for one thing the operator
    // did, and nothing else in it. The interval before a step, putting the
    // keyboard back before a chord, and settling afterwards are carried out by the
    // runtime around that line, so deleting the line deletes the step whole.
    // RecordedFacts.cs holds what the recording saw. The three Runtime files hold
    // how any of it is carried out and are not meant to be edited.
    //
    // Nothing in here presses a remembered coordinate. An element is found again
    // by the locators the recording produced, in the order it produced them, and
    // a place inside an element is a fraction of that element's rectangle as it
    // is now.
    //
    // A window title or an element label is kept exactly as the recording saw it,
    // whatever alphabet it is in. Only the ASCII header at the top of the built
    // .cmd is parsed by cmd itself; the header hands the rest to PowerShell naming
    // UTF-8, so nothing has to be flattened to survive the trip.
    public static class EngineGen
    {
        // The namespace the generated modules share, and the entry point the
        // wrapper calls. Kept as names of their own because the builder and the
        // runner need them before any file exists.
        public const string Namespace = "AppStudioRun";
        public const string EntryPoint = "Run";

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

        // ---------- Workflow.cs : the recorded procedure ----------

        private static string Workflow(ScriptPlan plan, StudioSession session, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("//");
            text.AppendLine("// " + App.Name + " " + App.Version + " - the recorded procedure");
            text.AppendLine("// session " + Comment(plan.SessionId) + "  " + Comment(plan.SessionTitle));
            text.AppendLine("//");
            text.AppendLine("// One line below is one recorded step, in the order it happened. Deleting a");
            text.AppendLine("// line takes that step out and changes nothing else.");
            text.AppendLine("//");
            text.AppendLine("// It drives the real applications on this machine. Read it before you run it.");
            text.AppendLine("//");
            if (session != null && session.ValuePolicy != null)
            {
                text.AppendLine("// Value policy while recording: " + Comment(session.ValuePolicy));
            }
            for (int index = 0; index < plan.Notes.Count; index++) text.AppendLine("// - " + Comment(plan.Notes[index]));
            if (session != null)
            {
                for (int index = 0; index < session.Limits.Count && index < 12; index++)
                {
                    text.AppendLine("// limit: " + Comment(session.Limits[index]));
                }
            }
            text.AppendLine("//");
            text.AppendLine();
            text.AppendLine("namespace " + Namespace);
            text.AppendLine("{");
            text.AppendLine("    public static class " + CodeModules.Workflow);
            text.AppendLine("    {");
            text.AppendLine("        public static void " + EntryPoint + "(int settleMs)");
            text.AppendLine("        {");
            text.AppendLine("            Runtime.Start(settleMs);");
            text.AppendLine("            " + CodeModules.RecordedFacts + ".Load();");
            text.AppendLine();
            text.AppendLine("            #region the recorded procedure - one line is one step");
            text.AppendLine();
            if (lines.Count == 0)
            {
                text.AppendLine("            // This session recorded nothing that can be carried out, so there is");
                text.AppendLine("            // no procedure here. Nothing is invented to fill the gap.");
                text.AppendLine();
            }
            for (int index = 0; index < lines.Count; index++)
            {
                text.AppendLine("            " + WorkflowLine(lines[index]));
            }
            text.AppendLine();
            text.AppendLine("            #endregion");
            text.AppendLine();
            text.AppendLine("            Runtime.Complete();");
            text.AppendLine("        }");
            text.AppendLine("    }");
            text.AppendLine("}");
            return text.ToString();
        }

        // One call, one comment, one line. The operation names are the nine the
        // VBA workflow uses, spelled the same way.
        private static string WorkflowLine(ScriptLine line)
        {
            StringBuilder text = new StringBuilder();
            text.Append("Runtime.").Append(Pad(line.Op + "(", 20)).Append("\"").Append(Escape(line.Id)).Append("\");");
            string note = Comment(line.Comment);
            if (note.Length > 0)
            {
                while (text.Length < 44) text.Append(' ');
                text.Append("  // ").Append(note);
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

        // ---------- RecordedFacts.cs : what the recording saw ----------

        private static string Facts(ScriptPlan plan, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("//");
            text.AppendLine("// " + App.Name + " " + App.Version + " - what the recording saw");
            text.AppendLine("// session " + Comment(plan.SessionId));
            text.AppendLine("//");
            text.AppendLine("// Generated from the recording. One block per line of " + CodeModules.Workflow + ".cs,");
            text.AppendLine("// holding the addresses that step was reached by, the interval the operator");
            text.AppendLine("// left before it, and whatever the step carried.");
            text.AppendLine("//");
            text.AppendLine("// An address here is never a screen coordinate. A place inside an element is");
            text.AppendLine("// a fraction of that element's own rectangle, so it follows the element when");
            text.AppendLine("// the window moves.");
            text.AppendLine("//");
            text.AppendLine("// Editing this changes what a step aims at. Editing " + CodeModules.Workflow + ".cs changes");
            text.AppendLine("// which steps happen. A block left here with no line using it is harmless.");
            text.AppendLine("//");
            text.AppendLine();
            text.AppendLine("namespace " + Namespace);
            text.AppendLine("{");
            text.AppendLine("    internal static class " + CodeModules.RecordedFacts);
            text.AppendLine("    {");
            text.AppendLine("        internal static void Load()");
            text.AppendLine("        {");
            text.AppendLine("            Runtime.Facts.Clear();");
            if (lines.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("            // Nothing was recorded that can be carried out.");
            }
            for (int index = 0; index < lines.Count; index++)
            {
                text.AppendLine();
                Fact(text, lines[index]);
            }
            text.AppendLine("        }");
            text.AppendLine();
            text.AppendLine("        private static Fact Add(string id, string step, string op, int gapMs, string note)");
            text.AppendLine("        {");
            text.AppendLine("            Fact fact = new Fact();");
            text.AppendLine("            fact.Id = id;");
            text.AppendLine("            fact.Step = step;");
            text.AppendLine("            fact.Op = op;");
            text.AppendLine("            fact.GapMs = gapMs;");
            text.AppendLine("            fact.Note = note;");
            text.AppendLine("            Runtime.Facts[id] = fact;");
            text.AppendLine("            return fact;");
            text.AppendLine("        }");
            text.AppendLine("    }");
            text.AppendLine("}");
            return text.ToString();
        }

        private static void Fact(StringBuilder text, ScriptLine line)
        {
            string note = Comment(line.Comment);
            if (note.Length > 0) text.AppendLine("            // " + Comment(line.Id) + "  " + note);
            text.Append("            Add(\"").Append(Escape(line.Id)).Append("\", \"").Append(Escape(line.StepId))
                .Append("\", \"").Append(Escape(line.Op)).Append("\", ")
                .Append(line.GapMs.ToString(CultureInfo.InvariantCulture)).Append(", \"").Append(Escape(note)).Append("\")");
            if (line.Op == ScriptOp.FindWindow)
            {
                text.AppendLine();
                text.Append("                .Window(\"").Append(Escape(line.WindowClass)).Append("\", \"").Append(Escape(line.WindowTitle)).Append("\")");
            }
            if (!String.IsNullOrEmpty(line.ElementLabel))
            {
                text.AppendLine();
                text.Append("                .Named(\"").Append(Escape(line.ElementLabel)).Append("\")");
            }
            if (line.Op == ScriptOp.InvokeElement)
            {
                text.AppendLine();
                text.Append("                .Press(\"").Append(Escape(line.Button)).Append("\", ")
                    .Append(line.Times.ToString(CultureInfo.InvariantCulture)).Append(", ")
                    .Append(Number(line.RelX)).Append(", ").Append(Number(line.RelY)).Append(")");
                if (line.WheelDelta != 0)
                {
                    text.AppendLine();
                    text.Append("                .Wheel(").Append(line.WheelDelta.ToString(CultureInfo.InvariantCulture)).Append(")");
                }
            }
            if (line.Op == ScriptOp.SetElementText)
            {
                text.AppendLine();
                text.Append("                .Types(\"").Append(Escape(line.Text)).Append("\")");
            }
            if (line.Op == ScriptOp.SendKeys)
            {
                text.AppendLine();
                text.Append("                .Sends(\"").Append(Escape(line.Chord)).Append("\", \"").Append(Escape(line.Keys)).Append("\")");
            }
            if (line.Op == ScriptOp.AskSecret)
            {
                text.AppendLine();
                text.Append("                .Asks(\"").Append(Escape(line.SecretPrompt)).Append("\")");
            }
            if (line.Op == ScriptOp.Unsupported && !String.IsNullOrEmpty(line.Reason))
            {
                text.AppendLine();
                text.Append("                .Refuses(\"").Append(Escape(line.Reason)).Append("\")");
            }
            if (line.Locators != null && line.Locators.Count > 0)
            {
                text.AppendLine();
                text.Append("                .On(").Append(Locators(line.Locators, "                    ")).Append(")");
            }
            if (line.FocusLocators != null && line.FocusLocators.Count > 0)
            {
                text.AppendLine();
                text.Append("                .Restoring(").Append(Locators(line.FocusLocators, "                          ")).Append(")");
            }
            if (line.DropLocators != null && line.DropLocators.Count > 0)
            {
                text.AppendLine();
                text.Append("                .Onto(").Append(Number(line.DropRelX)).Append(", ").Append(Number(line.DropRelY))
                    .Append(", ").Append(Locators(line.DropLocators, "                      ")).Append(")");
            }
            text.AppendLine(";");
        }

        // A locator as the object it is. Written out in full rather than encoded,
        // because the point of this file is that a person can read what a step
        // aims at and change it.
        private static string Locators(List<ElementLocator> locators, string indent)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < locators.Count; index++)
            {
                if (index != 0)
                {
                    text.AppendLine(",");
                    text.Append(indent);
                }
                ElementLocator locator = locators[index];
                text.Append("new StepLocator { Strategy = \"").Append(Escape(locator.Strategy)).Append("\"");
                if (!String.IsNullOrEmpty(locator.AutomationId)) text.Append(", AutomationId = \"").Append(Escape(locator.AutomationId)).Append("\"");
                if (!String.IsNullOrEmpty(locator.Name)) text.Append(", Name = \"").Append(Escape(locator.Name)).Append("\"");
                if (!String.IsNullOrEmpty(locator.ControlType)) text.Append(", ControlType = \"").Append(Escape(locator.ControlType)).Append("\"");
                if (!String.IsNullOrEmpty(locator.ClassName)) text.Append(", ClassName = \"").Append(Escape(locator.ClassName)).Append("\"");
                if (!String.IsNullOrEmpty(locator.TreePath)) text.Append(", TreePath = \"").Append(Escape(locator.TreePath)).Append("\"");
                if (locator.CtrlId != 0) text.Append(", CtrlId = ").Append(locator.CtrlId.ToString(CultureInfo.InvariantCulture));
                if (locator.ClassIndex >= 0) text.Append(", ClassIndex = ").Append(locator.ClassIndex.ToString(CultureInfo.InvariantCulture));
                text.Append(" }");
            }
            return text.ToString();
        }

        // ---------- RuntimeCore.cs : the nine operations ----------

        private static string[] CoreLines()
        {
            return new string[]
            {
                "//",
                "// The nine operations, and the bookkeeping around them.",
                "//",
                "// These are the same nine names the VBA runtime carries, with the same",
                "// meanings. A line of " + CodeModules.Workflow + ".cs names one of them and the id of a",
                "// step; everything that step needs is looked up here rather than written out",
                "// beside the call, because the workflow is meant to be read.",
                "//",
                "// Each operation waits the interval the operator left before its step, does",
                "// the one thing, and then waits for the application to stop changing. That",
                "// is why a step is one line and why deleting the line deletes the wait with",
                "// it.",
                "//",
                "// Runtime file. You should not need to change anything here to change what",
                "// the procedure does.",
                "//",
                "",
                "using System;",
                "using System.Collections.Generic;",
                "using System.Diagnostics;",
                "using System.Text;",
                "using System.Threading;",
                "using System.Windows.Automation;",
                "",
                "// A recording that never dragged anything fills no drop fields, and one that",
                "// never touched a dialog control fills no control id. The table still has",
                "// those columns, because the table is the same shape for every recording -",
                "// so a column this recording left empty is not a mistake to be reported.",
                "#pragma warning disable 649",
                "",
                "namespace " + Namespace,
                "{",
                "    // One recorded address. The strategies are the ones the recording",
                "    // produced, and they are tried in the order they are written.",
                "    internal sealed class StepLocator",
                "    {",
                "        internal string Strategy = \"\";",
                "        internal string AutomationId = \"\";",
                "        internal string Name = \"\";",
                "        internal string ControlType = \"\";",
                "        internal string ClassName = \"\";",
                "        internal string TreePath = \"\";",
                "        internal int CtrlId;",
                "        internal int ClassIndex = -1;",
                "    }",
                "",
                "    // What the recording saw about one step. The named methods are how",
                "    // " + CodeModules.RecordedFacts + ".cs states it, so that file reads as a table of steps",
                "    // rather than as a wall of assignments.",
                "    internal sealed class Fact",
                "    {",
                "        internal string Id = \"\";",
                "        internal string Step = \"\";",
                "        internal string Op = \"\";",
                "        internal string Note = \"\";",
                "        internal int GapMs;",
                "        internal string WindowClass = \"\";",
                "        internal string WindowTitle = \"\";",
                "        internal string Element = \"\";",
                "        internal string Text = \"\";",
                "        internal string Chord = \"\";",
                "        internal string Recorded = \"\";",
                "        internal string Prompt = \"\";",
                "        internal string Reason = \"\";",
                "        internal string Button = \"left\";",
                "        internal int Times = 1;",
                "        internal int WheelDelta;",
                "        internal double RelX = -1;",
                "        internal double RelY = -1;",
                "        internal double DropRelX = -1;",
                "        internal double DropRelY = -1;",
                "        internal List<StepLocator> Locators = new List<StepLocator>();",
                "        internal List<StepLocator> Focus = new List<StepLocator>();",
                "        internal List<StepLocator> Drop = new List<StepLocator>();",
                "",
                "        internal Fact Window(string windowClass, string windowTitle)",
                "        {",
                "            WindowClass = windowClass;",
                "            WindowTitle = windowTitle;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Named(string label)",
                "        {",
                "            Element = label;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Press(string button, int times, double relX, double relY)",
                "        {",
                "            Button = button;",
                "            Times = times;",
                "            RelX = relX;",
                "            RelY = relY;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Wheel(int delta)",
                "        {",
                "            WheelDelta = delta;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Types(string value)",
                "        {",
                "            Text = value;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Sends(string chord, string recorded)",
                "        {",
                "            Chord = chord;",
                "            Recorded = recorded;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Asks(string prompt)",
                "        {",
                "            Prompt = prompt;",
                "            return this;",
                "        }",
                "",
                "        internal Fact Refuses(string reason)",
                "        {",
                "            Reason = reason;",
                "            return this;",
                "        }",
                "",
                "        internal Fact On(params StepLocator[] locators)",
                "        {",
                "            Locators.AddRange(locators);",
                "            return this;",
                "        }",
                "",
                "        internal Fact Restoring(params StepLocator[] locators)",
                "        {",
                "            Focus.AddRange(locators);",
                "            return this;",
                "        }",
                "",
                "        internal Fact Onto(double relX, double relY, params StepLocator[] locators)",
                "        {",
                "            DropRelX = relX;",
                "            DropRelY = relY;",
                "            Drop.AddRange(locators);",
                "            return this;",
                "        }",
                "    }",
                "",
                "    // A run that stopped on purpose, with the reason a person can act on.",
                "    // It is a type of its own so the wrapper can tell it apart from a fault",
                "    // in the runtime itself.",
                "    internal sealed class WorkflowStopped : Exception",
                "    {",
                "        internal WorkflowStopped(string message) : base(message)",
                "        {",
                "        }",
                "    }",
                "",
                "    internal static class Runtime",
                "    {",
                "        internal static readonly Dictionary<string, Fact> Facts = new Dictionary<string, Fact>(StringComparer.Ordinal);",
                "        internal static AutomationElement Window;",
                "        internal static string Step = \"-\";",
                "        internal static int SettleMs = 2500;",
                "",
                "        internal static void Start(int settleMs)",
                "        {",
                "            SettleMs = settleMs;",
                "            Window = null;",
                "            Native.SetProcessDPIAware();",
                "        }",
                "",
                "        internal static void Complete()",
                "        {",
                "            Console.WriteLine(\"The recorded procedure finished.\");",
                "        }",
                "",
                "        internal static void Stop(string reason)",
                "        {",
                "            throw new WorkflowStopped(\"App Studio step \" + Step + \" stopped: \" + reason);",
                "        }",
                "",
                "        // What the recording saw about this step. A line naming a step that is",
                "        // not in " + CodeModules.RecordedFacts + ".cs stops the run: it is a workflow and a",
                "        // recording that no longer agree, and guessing which one is right is not",
                "        // this runtime's to make.",
                "        private static Fact Begin(string stepId)",
                "        {",
                "            Step = stepId;",
                "            if (!Facts.ContainsKey(stepId))",
                "            {",
                "                Stop(\"there is nothing recorded under this id. Either the line was renamed in \" +",
                "                    \"" + CodeModules.Workflow + ".cs or the block was removed from " + CodeModules.RecordedFacts + ".cs.\");",
                "            }",
                "            Fact fact = Facts[stepId];",
                "            WaitGap(fact.GapMs);",
                "            return fact;",
                "        }",
                "",
                "        // Waits for the window the recording expects to be in front, then keeps",
                "        // it.",
                "        internal static void FindWindow(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            string windowClass = fact.WindowClass;",
                "            string windowTitle = fact.WindowTitle;",
                "            AutomationElement root = AutomationElement.RootElement;",
                "            DateTime deadline = DateTime.UtcNow.AddMilliseconds(10000);",
                "            AutomationElement found = null;",
                "            while (DateTime.UtcNow < deadline)",
                "            {",
                "                List<AutomationElement> candidates = new List<AutomationElement>();",
                "                foreach (AutomationElement child in root.FindAll(TreeScope.Children, Condition.TrueCondition))",
                "                {",
                "                    bool sameClass = windowClass.Length == 0 || String.Equals(child.Current.ClassName, windowClass, StringComparison.Ordinal);",
                "                    bool sameTitle = windowTitle.Length == 0 || String.Equals(child.Current.Name, windowTitle, StringComparison.Ordinal);",
                "                    if (sameClass && sameTitle) candidates.Add(child);",
                "                }",
                "                if (candidates.Count == 1)",
                "                {",
                "                    found = candidates[0];",
                "                    break;",
                "                }",
                "                if (candidates.Count > 1)",
                "                {",
                "                    Stop(\"more than one window matches class \\\"\" + windowClass + \"\\\" title \\\"\" + windowTitle +",
                "                        \"\\\" (\" + candidates.Count + \"). Nothing was pressed, because there is no way to tell which one the recording meant.\");",
                "                }",
                "                Thread.Sleep(150);",
                "            }",
                "            if (found == null)",
                "            {",
                "                Stop(\"no window matches class \\\"\" + windowClass + \"\\\" title \\\"\" + windowTitle +",
                "                    \"\\\". The application may not be running, or its title may differ from the recorded run.\");",
                "            }",
                "            Window = found;",
                "            IntPtr handle = new IntPtr(found.Current.NativeWindowHandle);",
                "            if (handle != IntPtr.Zero) Native.SetForegroundWindow(handle);",
                "            Thread.Sleep(120);",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        internal static void FocusElement(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            Focus(fact.Locators);",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        internal static void Focus(List<StepLocator> locators)",
                "        {",
                "            if (locators == null || locators.Count == 0) return;",
                "            AutomationElement element = Finder.Resolve(locators);",
                "            try",
                "            {",
                "                element.SetFocus();",
                "            }",
                "            catch (Exception exception)",
                "            {",
                "                Stop(\"the keyboard could not be put back on this element: \" + exception.Message + \". Nothing was typed.\");",
                "            }",
                "            Thread.Sleep(80);",
                "        }",
                "",
                "        // Presses the element. A pattern the element publishes is preferred,",
                "        // because it acts on the control rather than on the screen; synthetic",
                "        // input is the fallback and it needs the window in front.",
                "        internal static void InvokeElement(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            AutomationElement element = Finder.Resolve(fact.Locators);",
                "            if (fact.WheelDelta != 0)",
                "            {",
                "                int[] at = Finder.Point(element, fact.RelX, fact.RelY);",
                "                Native.Wheel(at[0], at[1], fact.WheelDelta);",
                "            }",
                "            else if (fact.Drop.Count > 0)",
                "            {",
                "                int[] from = Finder.Point(element, fact.RelX, fact.RelY);",
                "                AutomationElement target = Finder.Resolve(fact.Drop);",
                "                int[] to = Finder.Point(target, fact.DropRelX, fact.DropRelY);",
                "                Native.Drag(from[0], from[1], to[0], to[1]);",
                "            }",
                "            else",
                "            {",
                "                PressElement(element, fact.Button, fact.Times, fact.RelX, fact.RelY);",
                "            }",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        private static void PressElement(AutomationElement element, string button, int times, double relX, double relY)",
                "        {",
                "            if (times == 1)",
                "            {",
                "                object pattern;",
                "                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))",
                "                {",
                "                    ((InvokePattern)pattern).Invoke();",
                "                    return;",
                "                }",
                "                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))",
                "                {",
                "                    ((TogglePattern)pattern).Toggle();",
                "                    return;",
                "                }",
                "                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))",
                "                {",
                "                    ((SelectionItemPattern)pattern).Select();",
                "                    return;",
                "                }",
                "            }",
                "            int[] at = Finder.Point(element, relX, relY);",
                "            Native.Click(at[0], at[1], button, times);",
                "        }",
                "",
                "        internal static void SetElementText(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            Focus(fact.Focus);",
                "            AutomationElement element = Finder.Resolve(fact.Locators);",
                "            if (element.Current.IsPassword)",
                "            {",
                "                Stop(\"this element is a password field. Writing into it from a script is refused.\");",
                "            }",
                "            object pattern;",
                "            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) && !((ValuePattern)pattern).Current.IsReadOnly)",
                "            {",
                "                ((ValuePattern)pattern).SetValue(fact.Text);",
                "                WaitIdle(SettleMs);",
                "                return;",
                "            }",
                "            try",
                "            {",
                "                element.SetFocus();",
                "            }",
                "            catch (Exception exception)",
                "            {",
                "                Stop(\"this element publishes no way to set its text and the keyboard could not be put on it either: \" + exception.Message);",
                "            }",
                "            Thread.Sleep(60);",
                "            System.Windows.Forms.SendKeys.SendWait(\"^a\");",
                "            System.Windows.Forms.SendKeys.SendWait(EscapeKeys(fact.Text));",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        internal static string ReadElementText(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            AutomationElement element = Finder.Resolve(fact.Locators);",
                "            if (element.Current.IsPassword) return null;",
                "            object pattern;",
                "            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))",
                "            {",
                "                return ((ValuePattern)pattern).Current.Value;",
                "            }",
                "            if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))",
                "            {",
                "                return ((TextPattern)pattern).DocumentRange.GetText(-1);",
                "            }",
                "            return element.Current.Name;",
                "        }",
                "",
                "        private static string EscapeKeys(string text)",
                "        {",
                "            if (text == null) return \"\";",
                "            StringBuilder built = new StringBuilder();",
                "            for (int index = 0; index < text.Length; index++)",
                "            {",
                "                char character = text[index];",
                "                if (\"+^%~(){}[]\".IndexOf(character) >= 0) built.Append('{').Append(character).Append('}');",
                "                else built.Append(character);",
                "            }",
                "            return built.ToString();",
                "        }",
                "",
                "        // One recorded chord, sent after the keyboard has been put back where the",
                "        // recording had it.",
                "        internal static void SendKeys(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            if (fact.Chord.Length == 0)",
                "            {",
                "                Stop(\"the recorded key \\\"\" + fact.Recorded + \"\\\" has no equivalent that can be sent from here, so nothing was sent.\");",
                "            }",
                "            Focus(fact.Focus);",
                "            System.Windows.Forms.SendKeys.SendWait(fact.Chord);",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        internal static void WaitGap(int ms)",
                "        {",
                "            if (ms <= 0) return;",
                "            int wait = ms;",
                "            if (wait < 120) wait = 120;",
                "            if (wait > 4000) wait = 4000;",
                "            Thread.Sleep(wait);",
                "        }",
                "",
                "        // Waits for the front window to stop changing, up to a stated ceiling.",
                "        // Reaching the ceiling is a measured wait, not a failure.",
                "        internal static void WaitIdle(int budgetMs)",
                "        {",
                "            Stopwatch watch = Stopwatch.StartNew();",
                "            IntPtr lastFront = IntPtr.Zero;",
                "            int stable = 0;",
                "            while (watch.ElapsedMilliseconds < budgetMs)",
                "            {",
                "                IntPtr front = Native.GetForegroundWindow();",
                "                if (front == lastFront) stable++;",
                "                else stable = 0;",
                "                lastFront = front;",
                "                if (stable >= 2) break;",
                "                Thread.Sleep(80);",
                "            }",
                "            watch.Stop();",
                "        }",
                "",
                "        // A value the recording deliberately never kept. It is asked for here and",
                "        // it is never written to a file, a log or the console.",
                "        internal static void AskSecret(string stepId)",
                "        {",
                "            Fact fact = Begin(stepId);",
                "            string prompt = fact.Prompt.Length == 0",
                "                ? \"This step needs a value the recording did not keep. Type it now\"",
                "                : fact.Prompt;",
                "            Console.Write(prompt + \": \");",
                "            StringBuilder typed = ReadHidden();",
                "            try",
                "            {",
                "                if (typed.Length == 0) Stop(\"no value was supplied for a step that needs one.\");",
                "                Focus(fact.Locators);",
                "                Thread.Sleep(60);",
                "                System.Windows.Forms.SendKeys.SendWait(EscapeKeys(typed.ToString()));",
                "            }",
                "            finally",
                "            {",
                "                typed.Remove(0, typed.Length);",
                "            }",
                "            WaitIdle(SettleMs);",
                "        }",
                "",
                "        // The console is asked for the value with nothing echoed. There is no",
                "        // masking character either, because a count of asterisks is a fact about",
                "        // the value that nobody asked to publish.",
                "        private static StringBuilder ReadHidden()",
                "        {",
                "            StringBuilder typed = new StringBuilder();",
                "            while (true)",
                "            {",
                "                ConsoleKeyInfo key = Console.ReadKey(true);",
                "                if (key.Key == ConsoleKey.Enter) break;",
                "                if (key.Key == ConsoleKey.Backspace)",
                "                {",
                "                    if (typed.Length > 0) typed.Remove(typed.Length - 1, 1);",
                "                    continue;",
                "                }",
                "                if (key.KeyChar == '\\0') continue;",
                "                typed.Append(key.KeyChar);",
                "            }",
                "            Console.WriteLine();",
                "            return typed;",
                "        }",
                "",
                "        // The recording holds something no address can be built from. The run",
                "        // stops here with the reason rather than pressing a remembered",
                "        // coordinate.",
                "        internal static void Unsupported(string stepId)",
                "        {",
                "            Step = stepId;",
                "            string reason = \"this step has no address that survives a restart.\";",
                "            if (Facts.ContainsKey(stepId) && Facts[stepId].Reason.Length > 0) reason = Facts[stepId].Reason;",
                "            Stop(reason);",
                "        }",
                "    }",
                "}"
            };
        }

        // ---------- RuntimeLocator.cs : finding the element again ----------

        private static string[] LocatorLines()
        {
            return new string[]
            {
                "//",
                "// Turning a recorded address back into the element that is on screen now.",
                "//",
                "// The locators are tried in the order the recording produced them: the",
                "// identifier the application chose first, a position among siblings last. A",
                "// locator that matches more than one element decides nothing and is not used",
                "// as if it had; the next one is tried instead, and when all of them are",
                "// spent the run stops rather than pressing something it cannot name.",
                "//",
                "// Runtime file. You should not need to change anything here to change what",
                "// the procedure does.",
                "//",
                "",
                "using System;",
                "using System.Collections.Generic;",
                "using System.Windows.Automation;",
                "",
                "namespace " + Namespace,
                "{",
                "    internal static class Finder",
                "    {",
                "        internal static string Label(AutomationElement element)",
                "        {",
                "            ControlType type = element.Current.ControlType;",
                "            string name = element.Current.Name;",
                "            string shortName = type == null ? \"?\" : type.ProgrammaticName.Replace(\"ControlType.\", \"\");",
                "            if (String.IsNullOrEmpty(name)) return shortName;",
                "            return shortName + \" \\\"\" + name + \"\\\"\";",
                "        }",
                "",
                "        // The readable hierarchy path, rebuilt the way the recording wrote it. It",
                "        // is the weakest of the strategies used here and it moves when the",
                "        // application's layout does, which is why it is only ever tried after",
                "        // the identifiers above it have failed.",
                "        internal static string PathOf(AutomationElement element, AutomationElement root)",
                "        {",
                "            TreeWalker walker = TreeWalker.ControlViewWalker;",
                "            List<string> parts = new List<string>();",
                "            AutomationElement node = element;",
                "            AutomationElement desktop = AutomationElement.RootElement;",
                "            int guard = 0;",
                "            while (node != null && guard < 40)",
                "            {",
                "                guard++;",
                "                parts.Insert(0, Label(node));",
                "                if (node.Current.NativeWindowHandle == root.Current.NativeWindowHandle && guard > 1) break;",
                "                try",
                "                {",
                "                    node = walker.GetParent(node);",
                "                }",
                "                catch (Exception)",
                "                {",
                "                    node = null;",
                "                }",
                "                if (node != null && node.Current.NativeWindowHandle == desktop.Current.NativeWindowHandle) break;",
                "            }",
                "            return String.Join(\" > \", parts.ToArray());",
                "        }",
                "",
                "        // Windows Forms and several toolkits append a per process number to the",
                "        // window class. The number changes on every launch, so the volatile tail",
                "        // is dropped rather than compared as if it were stable.",
                "        internal static string StableClass(string value)",
                "        {",
                "            if (String.IsNullOrEmpty(value)) return value;",
                "            int marker = value.IndexOf(\".app.\", StringComparison.OrdinalIgnoreCase);",
                "            if (marker > 0) return value.Substring(0, marker);",
                "            if (value.StartsWith(\"WindowsForms10.\", StringComparison.OrdinalIgnoreCase))",
                "            {",
                "                int dot = value.IndexOf('.', \"WindowsForms10.\".Length);",
                "                if (dot > 0) return value.Substring(0, dot);",
                "            }",
                "            return value;",
                "        }",
                "",
                "        // One locator against the window as it is now. Returns every element it",
                "        // matched: deciding what to do about none or many is the caller's job,",
                "        // and neither answer is turned into a guess here.",
                "        internal static List<AutomationElement> Match(StepLocator locator)",
                "        {",
                "            AutomationElement window = Runtime.Window;",
                "            if (window == null) Runtime.Stop(\"no window has been found yet, so there is nothing to look inside.\");",
                "            List<AutomationElement> hits = new List<AutomationElement>();",
                "            int classSeen = 0;",
                "            foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))",
                "            {",
                "                bool hit = false;",
                "                if (locator.Strategy == \"uia.automationId\")",
                "                {",
                "                    hit = String.Equals(element.Current.AutomationId, locator.AutomationId, StringComparison.Ordinal);",
                "                    if (hit && locator.ControlType.Length > 0)",
                "                    {",
                "                        hit = String.Equals(ShortType(element), locator.ControlType, StringComparison.Ordinal);",
                "                    }",
                "                }",
                "                else if (locator.Strategy == \"uia.nameControlType\")",
                "                {",
                "                    hit = String.Equals(element.Current.Name, locator.Name, StringComparison.Ordinal) &&",
                "                        String.Equals(ShortType(element), locator.ControlType, StringComparison.Ordinal);",
                "                }",
                "                else if (locator.Strategy == \"uia.treePath\")",
                "                {",
                "                    hit = String.Equals(PathOf(element, window), locator.TreePath, StringComparison.Ordinal);",
                "                }",
                "                else if (locator.Strategy == \"win32.ctrlId\")",
                "                {",
                "                    IntPtr handle = new IntPtr(element.Current.NativeWindowHandle);",
                "                    if (handle != IntPtr.Zero && String.Equals(StableClass(element.Current.ClassName), locator.ClassName, StringComparison.Ordinal))",
                "                    {",
                "                        hit = Native.GetWindowLong(handle, -12) == locator.CtrlId;",
                "                    }",
                "                }",
                "                else if (locator.Strategy == \"win32.classIndex\")",
                "                {",
                "                    if (String.Equals(StableClass(element.Current.ClassName), locator.ClassName, StringComparison.Ordinal))",
                "                    {",
                "                        if (classSeen == locator.ClassIndex) hit = true;",
                "                        classSeen++;",
                "                    }",
                "                }",
                "                if (hit) hits.Add(element);",
                "            }",
                "            return hits;",
                "        }",
                "",
                "        private static string ShortType(AutomationElement element)",
                "        {",
                "            ControlType type = element.Current.ControlType;",
                "            if (type == null) return \"\";",
                "            return type.ProgrammaticName.Replace(\"ControlType.\", \"\");",
                "        }",
                "",
                "        internal static AutomationElement Resolve(List<StepLocator> locators)",
                "        {",
                "            if (locators == null || locators.Count == 0)",
                "            {",
                "                Runtime.Stop(\"this step has no address at all, so there is nothing to look for. Its recorded position is a description, never an address.\");",
                "            }",
                "            List<string> ambiguous = new List<string>();",
                "            for (int index = 0; index < locators.Count; index++)",
                "            {",
                "                List<AutomationElement> hits = Match(locators[index]);",
                "                if (hits.Count == 1) return hits[0];",
                "                if (hits.Count > 1) ambiguous.Add(locators[index].Strategy + \" matched \" + hits.Count);",
                "            }",
                "            if (ambiguous.Count > 0)",
                "            {",
                "                Runtime.Stop(\"the element could not be told apart from others like it (\" +",
                "                    String.Join(\"; \", ambiguous.ToArray()) + \"). Nothing was sent.\");",
                "            }",
                "            Runtime.Stop(\"the element could not be found again in this window. Nothing was sent.\");",
                "            return null;",
                "        }",
                "",
                "        // Where inside the element to act, as a fraction of the rectangle it has",
                "        // right now. Never a coordinate the recording remembered.",
                "        internal static int[] Point(AutomationElement element, double relX, double relY)",
                "        {",
                "            System.Windows.Rect rect = element.Current.BoundingRectangle;",
                "            if (rect.Width <= 0 || rect.Height <= 0)",
                "            {",
                "                Runtime.Stop(\"the element was found but has no usable rectangle right now, so there is nowhere to act.\");",
                "            }",
                "            double fx = relX < 0 ? 0.5 : relX;",
                "            double fy = relY < 0 ? 0.5 : relY;",
                "            return new int[] { (int)(rect.X + rect.Width * fx), (int)(rect.Y + rect.Height * fy) };",
                "        }",
                "    }",
                "}"
            };
        }

        // ---------- RuntimeNative.cs : the OS plumbing ----------

        private static string[] NativeLines()
        {
            return new string[]
            {
                "//",
                "// The parts that talk to Windows directly: the declarations, the pointer,",
                "// and turning a desktop point into what SendInput wants.",
                "//",
                "// Runtime file. You should not need to change anything here to change what",
                "// the procedure does.",
                "//",
                "",
                "using System;",
                "using System.Runtime.InteropServices;",
                "using System.Threading;",
                "",
                "// The padding in an interop struct is never assigned by hand. It is there so",
                "// the bytes line up with what the API expects, which is the whole point of it.",
                "#pragma warning disable 649",
                "",
                "namespace " + Namespace,
                "{",
                "    [StructLayout(LayoutKind.Sequential)]",
                "    internal struct MOUSEINPUT",
                "    {",
                "        internal int dx;",
                "        internal int dy;",
                "        internal uint mouseData;",
                "        internal uint dwFlags;",
                "        internal uint time;",
                "        internal IntPtr dwExtraInfo;",
                "    }",
                "",
                "    [StructLayout(LayoutKind.Sequential)]",
                "    internal struct INPUT",
                "    {",
                "        internal uint type;",
                "        internal MOUSEINPUT mi;",
                "        internal int pad1;",
                "        internal int pad2;",
                "    }",
                "",
                "    internal static class Native",
                "    {",
                "        private const uint MoveAbsolute = 0x8001;",
                "        private const uint LeftDown = 0x0002;",
                "        private const uint LeftUp = 0x0004;",
                "        private const uint RightDown = 0x0008;",
                "        private const uint RightUp = 0x0010;",
                "        private const uint MiddleDown = 0x0020;",
                "        private const uint MiddleUp = 0x0040;",
                "        private const uint WheelTurn = 0x0800;",
                "        private const uint LeftDownAbsolute = 0x8002;",
                "        private const uint LeftUpAbsolute = 0x8004;",
                "",
                "        [DllImport(\"user32.dll\")] internal static extern bool SetProcessDPIAware();",
                "        [DllImport(\"user32.dll\")] internal static extern IntPtr GetForegroundWindow();",
                "        [DllImport(\"user32.dll\")] internal static extern bool SetForegroundWindow(IntPtr window);",
                "        [DllImport(\"user32.dll\")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);",
                "        [DllImport(\"user32.dll\")] private static extern int GetSystemMetrics(int index);",
                "        [DllImport(\"user32.dll\", EntryPoint = \"GetWindowLongW\")] internal static extern int GetWindowLong(IntPtr window, int index);",
                "",
                "        private static void Mouse(uint flags, int x, int y, uint data)",
                "        {",
                "            INPUT[] one = new INPUT[1];",
                "            one[0].type = 0;",
                "            one[0].mi.dx = x;",
                "            one[0].mi.dy = y;",
                "            one[0].mi.mouseData = data;",
                "            one[0].mi.dwFlags = flags;",
                "            SendInput(1, one, Marshal.SizeOf(typeof(INPUT)));",
                "        }",
                "",
                "        // A desktop point in the 0..65535 space SendInput uses for an absolute",
                "        // move.",
                "        private static int[] Absolute(int x, int y)",
                "        {",
                "            int spanX = GetSystemMetrics(78);",
                "            int spanY = GetSystemMetrics(79);",
                "            if (spanX <= 0 || spanY <= 0)",
                "            {",
                "                Runtime.Stop(\"the virtual desktop reported no size, so no pointer position can be expressed.\");",
                "            }",
                "            int originX = GetSystemMetrics(76);",
                "            int originY = GetSystemMetrics(77);",
                "            return new int[] { (x - originX) * 65535 / spanX, (y - originY) * 65535 / spanY };",
                "        }",
                "",
                "        internal static void Click(int x, int y, string button, int times)",
                "        {",
                "            int[] at = Absolute(x, y);",
                "            uint down = LeftDown;",
                "            uint up = LeftUp;",
                "            if (button == \"right\")",
                "            {",
                "                down = RightDown;",
                "                up = RightUp;",
                "            }",
                "            else if (button == \"middle\")",
                "            {",
                "                down = MiddleDown;",
                "                up = MiddleUp;",
                "            }",
                "            Mouse(MoveAbsolute, at[0], at[1], 0);",
                "            Thread.Sleep(40);",
                "            for (int index = 0; index < times; index++)",
                "            {",
                "                Mouse(MoveAbsolute | down, at[0], at[1], 0);",
                "                Mouse(MoveAbsolute | up, at[0], at[1], 0);",
                "                if (index < times - 1) Thread.Sleep(60);",
                "            }",
                "        }",
                "",
                "        internal static void Wheel(int x, int y, int delta)",
                "        {",
                "            int[] at = Absolute(x, y);",
                "            Mouse(MoveAbsolute, at[0], at[1], 0);",
                "            Thread.Sleep(40);",
                "            // mouseData carries a signed turn in an unsigned field.",
                "            uint data = delta < 0 ? (uint)(4294967296L + delta) : (uint)delta;",
                "            Mouse(WheelTurn, 0, 0, data);",
                "        }",
                "",
                "        internal static void Drag(int fromX, int fromY, int toX, int toY)",
                "        {",
                "            int[] from = Absolute(fromX, fromY);",
                "            int[] to = Absolute(toX, toY);",
                "            Mouse(MoveAbsolute, from[0], from[1], 0);",
                "            Thread.Sleep(60);",
                "            Mouse(LeftDownAbsolute, from[0], from[1], 0);",
                "            Thread.Sleep(60);",
                "            Mouse(MoveAbsolute, to[0], to[1], 0);",
                "            Thread.Sleep(60);",
                "            Mouse(LeftUpAbsolute, to[0], to[1], 0);",
                "        }",
                "    }",
                "}"
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

        // A C# string literal ends at the first unescaped quote in it, and a
        // backslash starts an escape, so both are written out as escapes. The
        // literal cannot span lines either, which is what Comment already
        // guarantees.
        public static string Escape(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string flat = Comment(value);
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < flat.Length; index++)
            {
                char character = flat[index];
                if (character == '\\') text.Append("\\\\");
                else if (character == '"') text.Append("\\\"");
                else if (character == '\t') text.Append("\\t");
                else text.Append(character);
            }
            return text.ToString();
        }

        // Anything written into a comment or a literal has to stay on the line
        // it was put on.
        //
        // A window title or an element label is often not ASCII, and none of it
        // is flattened here. The built .cmd stays readable because the only part
        // cmd itself parses is the ASCII header at the top; the header then hands
        // the rest to PowerShell naming UTF-8, so what a step aims at survives
        // the trip instead of turning into question marks.
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
