namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The VBA half of the same automation, written as five modules with the
    // same five names the PowerShell side uses.
    //
    // It is not a translation of the PowerShell one and it is not a lesser
    // version of it: both are written from the same line list and both carry the
    // same nine operations. Workflow is the standard module a person edits, one
    // line per recorded step; RecordedFacts holds what the recording saw; the
    // three Runtime modules hold how it is carried out.
    //
    // What differs between the languages is what each can reach. VBA has no UI
    // Automation on a machine where nothing may be installed, so it addresses
    // controls the Win32 way - a class name with a dialog control id, or a class
    // name with its index among its siblings. An element that only ever existed
    // in the accessibility tree has no Win32 address, and this generator says so
    // at the point it occurs instead of inventing a coordinate for it.
    public static class VbaGen
    {
        // The module a host imports first and calls into. Kept as a name of its
        // own because the runner needs it before any file exists.
        public const string ModuleName = CodeModules.Workflow;
        public const string EntryPoint = "RunRecordedProcedure";

        public static List<CodeFile> BuildFiles(ScriptPlan plan, StudioSession session)
        {
            List<ScriptLine> lines = ScriptModel.Lines(plan);
            List<CodeFile> files = new List<CodeFile>();
            files.Add(Make(CodeModules.Workflow, Workflow(plan, session, lines)));
            files.Add(Make(CodeModules.RecordedFacts, Facts(plan, lines)));
            files.Add(Make(CodeModules.RuntimeCore, Module(CodeModules.RuntimeCore, CoreLines())));
            files.Add(Make(CodeModules.RuntimeLocator, Module(CodeModules.RuntimeLocator, LocatorLines())));
            files.Add(Make(CodeModules.RuntimeNative, Module(CodeModules.RuntimeNative, NativeLines())));
            return files;
        }

        private static CodeFile Make(string name, string text)
        {
            CodeFile file = new CodeFile();
            file.Language = ScriptLanguages.Vba;
            file.Name = name;
            file.Role = CodeRoles.Of(name);
            file.Text = text;
            return file;
        }

        private static string Module(string name, string[] lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Attribute VB_Name = \"" + name + "\"");
            for (int index = 0; index < lines.Length; index++) text.AppendLine(lines[index]);
            return text.ToString();
        }

        // ---------- Workflow : the recorded procedure ----------

        private static string Workflow(ScriptPlan plan, StudioSession session, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Attribute VB_Name = \"" + CodeModules.Workflow + "\"");
            text.AppendLine("' " + App.Name + " " + App.Version + " - the recorded procedure (VBA)");
            text.AppendLine("' session " + Comment(plan.SessionId) + "  " + Comment(plan.SessionTitle));
            text.AppendLine("'");
            // Short, for the same reason the PowerShell one is short: the steps
            // are what somebody opens this to change, and they have to be on
            // screen when it opens. The explanation lives beside the editor.
            text.AppendLine("' One line below is one recorded step, in the order it happened. Deleting a");
            text.AppendLine("' line takes that step out and changes nothing else.");
            text.AppendLine("'");
            text.AppendLine("' The built workbook already holds all five modules. Open it and run " + EntryPoint + ".");
            text.AppendLine("' It drives the real applications on this machine. Read it before you run it.");
            text.AppendLine("'");
            text.AppendLine("' VBA reaches controls through Win32 only. A step whose only address is a UI");
            text.AppendLine("' Automation one is written below as Unsupported and stops the run with the");
            text.AppendLine("' reason, rather than pressing a remembered coordinate.");
            text.AppendLine("'");
            if (session != null && session.ValuePolicy != null)
            {
                text.AppendLine("' Value policy while recording: " + Comment(session.ValuePolicy));
            }
            for (int index = 0; index < plan.Notes.Count; index++) text.AppendLine("' - " + Comment(plan.Notes[index]));
            if (session != null)
            {
                for (int index = 0; index < session.Limits.Count && index < 12; index++)
                {
                    text.AppendLine("' limit: " + Comment(session.Limits[index]));
                }
            }
            text.AppendLine();
            text.AppendLine("Option Explicit");
            text.AppendLine();
            // The recorded steps come before the two ways of starting them.
            // VBA does not care which order a module declares things in, and the
            // reader does: this is the part somebody opened the file to change,
            // and putting the entry points first pushed it past line forty,
            // which is below the bottom of the editor showing it.
            text.AppendLine("'=== the recorded procedure - one line is one step ===");
            text.AppendLine();
            text.AppendLine("Private Sub RunSteps()");
            text.AppendLine("    On Error GoTo Failed");
            if (lines.Count == 0)
            {
                text.AppendLine("    ' This session recorded nothing that can be carried out, so there is no");
                text.AppendLine("    ' procedure here. Nothing is invented to fill the gap.");
            }
            for (int index = 0; index < lines.Count; index++)
            {
                text.AppendLine("    " + WorkflowLine(lines[index]));
            }
            text.AppendLine("    " + CodeModules.RuntimeCore + ".Finished");
            text.AppendLine("    Exit Sub");
            text.AppendLine("Failed:");
            text.AppendLine("    " + CodeModules.RuntimeCore + ".Stopped Err.Description");
            text.AppendLine("End Sub");
            text.AppendLine();
            text.AppendLine("' Run this one yourself. It reports on screen.");
            text.AppendLine("Public Sub " + EntryPoint + "()");
            text.AppendLine("    " + CodeModules.RuntimeCore + ".BeginRun \"\"");
            text.AppendLine("    RunSteps");
            text.AppendLine("End Sub");
            text.AppendLine();
            text.AppendLine("' A host that is watching calls this one and reads the file. Nothing here may");
            text.AppendLine("' open a window in that case, because there is nobody to close it.");
            text.AppendLine("Public Sub " + EntryPoint + "To(ByVal resultPath As String)");
            text.AppendLine("    " + CodeModules.RuntimeCore + ".BeginRun resultPath");
            text.AppendLine("    RunSteps");
            text.AppendLine("End Sub");
            return text.ToString();
        }

        // One call, one comment, one line. A step VBA has no address for is
        // written as Unsupported here rather than looking like something that
        // will run.
        private static string WorkflowLine(ScriptLine line)
        {
            string op = line.Op;
            string note = Comment(line.Comment);
            if (!line.VbaReachable && op != ScriptOp.Unsupported)
            {
                op = ScriptOp.Unsupported;
                note = "addressed only through UI Automation - " + note;
            }
            StringBuilder text = new StringBuilder();
            text.Append(Pad(op, 15)).Append(" \"").Append(Escape(line.Id)).Append("\"");
            if (note.Length > 0)
            {
                while (text.Length < 34) text.Append(' ');
                text.Append("  ' ").Append(note);
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

        // ---------- RecordedFacts : what the recording saw ----------

        private static string Facts(ScriptPlan plan, List<ScriptLine> lines)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Attribute VB_Name = \"" + CodeModules.RecordedFacts + "\"");
            text.AppendLine("' " + App.Name + " " + App.Version + " - what the recording saw");
            text.AppendLine("' session " + Comment(plan.SessionId));
            text.AppendLine("'");
            text.AppendLine("' Generated from the recording. One Case per line of " + CodeModules.Workflow + ", holding");
            text.AppendLine("' the addresses that step was reached by, the interval the operator left");
            text.AppendLine("' before it, and whatever the step carried.");
            text.AppendLine("'");
            text.AppendLine("' An address here is never a screen coordinate. A place inside a control is");
            text.AppendLine("' a fraction of that control's own rectangle, so it follows the control when");
            text.AppendLine("' the window moves. A locator reads \"strategy|class|id\" and several of them");
            text.AppendLine("' are separated by a vertical tab, tried in the order they are written.");
            text.AppendLine("'");
            text.AppendLine("' Editing this changes what a step aims at. Editing " + CodeModules.Workflow + " changes");
            text.AppendLine("' which steps happen. A Case left here with no line using it is harmless.");
            text.AppendLine("'");
            text.AppendLine();
            text.AppendLine("Option Explicit");
            text.AppendLine();
            text.AppendLine("Public gFound As Boolean");
            text.AppendLine("Public gStep As String");
            text.AppendLine("Public gOp As String");
            text.AppendLine("Public gNote As String");
            text.AppendLine("Public gGapMs As Long");
            text.AppendLine("Public gWindowClass As String");
            text.AppendLine("Public gWindowTitle As String");
            text.AppendLine("Public gWindowProcess As String");
            text.AppendLine("Public gElement As String");
            text.AppendLine("Public gLocators As String");
            text.AppendLine("Public gFocus As String");
            text.AppendLine("Public gDrop As String");
            text.AppendLine("Public gDropRelX As Double");
            text.AppendLine("Public gDropRelY As Double");
            text.AppendLine("Public gButton As String");
            text.AppendLine("Public gTimes As Long");
            text.AppendLine("Public gRelX As Double");
            text.AppendLine("Public gRelY As Double");
            text.AppendLine("Public gWheel As Long");
            text.AppendLine("Public gText As String");
            text.AppendLine("Public gChord As String");
            text.AppendLine("Public gRecorded As String");
            text.AppendLine("Public gPrompt As String");
            text.AppendLine("Public gReason As String");
            text.AppendLine();
            text.AppendLine("' Puts one step's facts where the runtime reads them. gFound stays False");
            text.AppendLine("' when the id is not here, and the runtime stops rather than carrying on");
            text.AppendLine("' with empty values.");
            text.AppendLine("Public Sub LoadStep(ByVal stepId As String)");
            text.AppendLine("    ClearStep");
            text.AppendLine("    Select Case stepId");
            for (int index = 0; index < lines.Count; index++)
            {
                Fact(text, lines[index]);
            }
            text.AppendLine("    End Select");
            text.AppendLine("End Sub");
            text.AppendLine();
            text.AppendLine("Private Sub ClearStep()");
            text.AppendLine("    gFound = False");
            text.AppendLine("    gStep = \"\"");
            text.AppendLine("    gOp = \"\"");
            text.AppendLine("    gNote = \"\"");
            text.AppendLine("    gGapMs = 0");
            text.AppendLine("    gWindowClass = \"\"");
            text.AppendLine("    gWindowTitle = \"\"");
            text.AppendLine("    gWindowProcess = \"\"");
            text.AppendLine("    gElement = \"\"");
            text.AppendLine("    gLocators = \"\"");
            text.AppendLine("    gFocus = \"\"");
            text.AppendLine("    gDrop = \"\"");
            text.AppendLine("    gDropRelX = -1");
            text.AppendLine("    gDropRelY = -1");
            text.AppendLine("    gButton = \"left\"");
            text.AppendLine("    gTimes = 1");
            text.AppendLine("    gRelX = -1");
            text.AppendLine("    gRelY = -1");
            text.AppendLine("    gWheel = 0");
            text.AppendLine("    gText = \"\"");
            text.AppendLine("    gChord = \"\"");
            text.AppendLine("    gRecorded = \"\"");
            text.AppendLine("    gPrompt = \"\"");
            text.AppendLine("    gReason = \"\"");
            text.AppendLine("End Sub");
            return text.ToString();
        }

        private static void Fact(StringBuilder text, ScriptLine line)
        {
            text.AppendLine("        Case " + Literal(line.Id));
            string note = Comment(line.Comment);
            text.AppendLine("            gFound = True");
            text.AppendLine("            gStep = " + Literal(line.StepId));
            text.AppendLine("            gOp = " + Literal(line.Op));
            if (note.Length > 0) text.AppendLine("            gNote = " + Literal(note));
            if (line.GapMs > 0) text.AppendLine("            gGapMs = " + line.GapMs.ToString(CultureInfo.InvariantCulture));
            if (line.Op == ScriptOp.FindWindow)
            {
                text.AppendLine("            gWindowClass = " + Literal(line.WindowClass));
                text.AppendLine("            gWindowTitle = " + Literal(line.WindowTitle));
                text.AppendLine("            gWindowProcess = " + Literal(line.AppName));
            }
            if (!String.IsNullOrEmpty(line.ElementLabel))
            {
                text.AppendLine("            gElement = " + Literal(line.ElementLabel));
            }
            string locators = Encode(line.Locators);
            if (locators.Length > 0) text.AppendLine("            gLocators = " + Literal(locators));
            string focus = Encode(line.FocusLocators);
            if (focus.Length > 0) text.AppendLine("            gFocus = " + Literal(focus));
            string drop = Encode(line.DropLocators);
            if (drop.Length > 0)
            {
                text.AppendLine("            gDrop = " + Literal(drop));
                text.AppendLine("            gDropRelX = " + Number(line.DropRelX));
                text.AppendLine("            gDropRelY = " + Number(line.DropRelY));
            }
            if (line.Op == ScriptOp.InvokeElement)
            {
                text.AppendLine("            gButton = " + Literal(line.Button));
                text.AppendLine("            gTimes = " + line.Times.ToString(CultureInfo.InvariantCulture));
                text.AppendLine("            gRelX = " + Number(line.RelX));
                text.AppendLine("            gRelY = " + Number(line.RelY));
                if (line.WheelDelta != 0) text.AppendLine("            gWheel = " + line.WheelDelta.ToString(CultureInfo.InvariantCulture));
            }
            if (line.Op == ScriptOp.SetElementText) text.AppendLine("            gText = " + Literal(line.Text));
            if (line.Op == ScriptOp.SendKeys)
            {
                text.AppendLine("            gChord = " + Literal(line.Chord));
                text.AppendLine("            gRecorded = " + Literal(line.Keys));
            }
            if (line.Op == ScriptOp.AskSecret)
            {
                text.AppendLine("            gPrompt = " + Literal(line.SecretPrompt + " (type it into the application, then press OK)"));
            }
            string reason = line.Reason;
            if (!line.VbaReachable && line.Op != ScriptOp.Unsupported)
            {
                reason = "this step is addressed only through UI Automation (" + Strategies(line) +
                    "), and VBA has no Win32 address for it. Nothing was sent.";
            }
            if (!String.IsNullOrEmpty(reason)) text.AppendLine("            gReason = " + Literal(reason));
        }

        private static string Strategies(ScriptLine line)
        {
            StringBuilder text = new StringBuilder();
            if (line.Locators != null)
            {
                for (int index = 0; index < line.Locators.Count; index++)
                {
                    if (index != 0) text.Append(", ");
                    text.Append(line.Locators[index].Strategy);
                }
            }
            return text.Length == 0 ? "no locator at all" : text.ToString();
        }

        // ---------- RuntimeCore : the nine operations ----------

        private static string[] CoreLines()
        {
            return new string[]
            {
                "' The nine operations, and the bookkeeping around them.",
                "'",
                "' These are the same nine names the PowerShell runtime carries, with the same",
                "' meanings. A line of " + CodeModules.Workflow + " names one of them and the id of a step;",
                "' everything that step needs is looked up in " + CodeModules.RecordedFacts + " rather than",
                "' written out beside the call, because the workflow is meant to be read.",
                "'",
                "' Each operation waits the interval the operator left before its step, does the",
                "' one thing, and then waits for the application to stop changing. That is why a",
                "' step is one line and why deleting the line deletes the wait with it.",
                "'",
                "' Runtime module. You should not need to change anything here to change what",
                "' the procedure does.",
                "",
                "Option Explicit",
                "",
                "#If VBA7 Then",
                "Public gWindow As LongPtr",
                "#Else",
                "Public gWindow As Long",
                "#End If",
                "Public gStepId As String",
                "Public gSettleMs As Long",
                "' Where to write what happened. A host that sets this is running the modules",
                "' unattended, and nothing may open a window it cannot answer: an unhandled VBA",
                "' error puts up a modal break dialog that the caller never sees, which is a",
                "' hang with no explanation.",
                "Public gResultPath As String",
                "' Things the run had to settle for itself. They are carried to the end and",
                "' said with the result rather than announced as they happen: a message box",
                "' in the middle of a run blocks it, and writing to the result file early",
                "' would look to a watching host like the run had finished.",
                "Public gNotes As String",
                "",
                "Public Sub BeginRun(ByVal resultPath As String)",
                "    gResultPath = resultPath",
                "    gWindow = 0",
                "    gStepId = \"-\"",
                "    gSettleMs = 2500",
                "    gNotes = \"\"",
                "End Sub",
                "",
                "Public Sub Notice(ByVal text As String)",
                "    If Len(gNotes) > 0 Then gNotes = gNotes & vbCrLf",
                "    gNotes = gNotes & \"step \" & gStepId & \": \" & text",
                "End Sub",
                "",
                "Public Sub Finished()",
                "    Report \"done\", \"\"",
                "End Sub",
                "",
                "Public Sub Stopped(ByVal detail As String)",
                "    Report \"stopped\", detail",
                "End Sub",
                "",
                "Public Sub AppStudioStop(ByVal reason As String)",
                "    Err.Raise vbObjectError + 513, \"" + CodeModules.RuntimeCore + "\", \"App Studio step \" & gStepId & \" stopped: \" & reason",
                "End Sub",
                "",
                "' What the recording saw about this step. A line naming a step that is not in",
                "' " + CodeModules.RecordedFacts + " stops the run: it is a workflow and a recording that no",
                "' longer agree, and guessing which one is right is not this runtime's to make.",
                "Private Sub BeginStep(ByVal stepId As String)",
                "    gStepId = stepId",
                "    " + CodeModules.RecordedFacts + ".LoadStep stepId",
                "    If Not " + CodeModules.RecordedFacts + ".gFound Then",
                "        AppStudioStop \"there is nothing recorded under this id. Either the line was renamed in " + CodeModules.Workflow + " or the Case was removed from " + CodeModules.RecordedFacts + ".\"",
                "    End If",
                "    WaitGap " + CodeModules.RecordedFacts + ".gGapMs",
                "End Sub",
                "",
                "' Whether the recording's own description of its window still fits this one.",
                "'",
                "' The description is the whole of what the recording knows: the class, the",
                "' title and the application it belonged to. Matching on only two of the",
                "' three, over every window the desktop has ever held open, is what used to",
                "' make one running application look like several.",
                "#If VBA7 Then",
                "Private Function Fits(ByVal hWnd As LongPtr, ByVal className As String, ByVal title As String, ByVal appName As String) As Boolean",
                "#Else",
                "Private Function Fits(ByVal hWnd As Long, ByVal className As String, ByVal title As String, ByVal appName As String) As Boolean",
                "#End If",
                "    Dim owner As String",
                "    Fits = False",
                "    If Not " + CodeModules.RuntimeLocator + ".Operable(hWnd) Then Exit Function",
                "    If Len(className) > 0 Then",
                "        If " + CodeModules.RuntimeLocator + ".ClassOf(hWnd) <> className Then Exit Function",
                "    End If",
                "    If Len(title) > 0 Then",
                "        If " + CodeModules.RuntimeLocator + ".TitleOf(hWnd) <> title Then Exit Function",
                "    End If",
                "    If Len(appName) > 0 Then",
                "        owner = " + CodeModules.RuntimeLocator + ".ProcessOf(hWnd)",
                "        If Len(owner) > 0 Then",
                "            If StrComp(owner, appName, vbTextCompare) <> 0 Then Exit Function",
                "        End If",
                "    End If",
                "    Fits = True",
                "End Function",
                "",
                "' Waits for the window the recording expects to be in front, then keeps it",
                "' for every step that follows.",
                "'",
                "' The step is defined as \"the window this recording had in front\", so when the",
                "' operator has two of the same application open that is how it is settled: the",
                "' one already in front, or failing that the one nearest the front. It is never",
                "' settled quietly - the run says how many fitted and which one it took - and",
                "' the chosen window is then held, so no later step can drift into the other.",
                "Public Sub FindWindow(ByVal stepId As String)",
                "    Dim deadline As Double",
                "    Dim className As String",
                "    Dim title As String",
                "    Dim appName As String",
                "#If VBA7 Then",
                "    Dim candidate As LongPtr",
                "    Dim child As LongPtr",
                "    Dim front As LongPtr",
                "#Else",
                "    Dim candidate As Long",
                "    Dim child As Long",
                "    Dim front As Long",
                "#End If",
                "    Dim matches As Long",
                "    BeginStep stepId",
                "    className = " + CodeModules.RecordedFacts + ".gWindowClass",
                "    title = " + CodeModules.RecordedFacts + ".gWindowTitle",
                "    appName = " + CodeModules.RecordedFacts + ".gWindowProcess",
                "    front = apiGetForegroundWindow()",
                "    deadline = Timer + 10",
                "    Do",
                "        matches = 0",
                "        candidate = 0",
                "        ' The children of the desktop are the top level windows, front first.",
                "        child = apiGetWindow(apiGetDesktopWindow(), GW_CHILD)",
                "        Do While child <> 0",
                "            If Fits(child, className, title, appName) Then",
                "                matches = matches + 1",
                "                If candidate = 0 Then candidate = child",
                "                If child = front Then candidate = child",
                "            End If",
                "            child = apiGetWindow(child, GW_HWNDNEXT)",
                "        Loop",
                "        If matches > 0 Then Exit Do",
                "        apiSleep 150",
                "    Loop While Timer < deadline",
                "    If candidate = 0 Then",
                "        AppStudioStop \"no window matches class \" & className & \" title \" & title & IIf(Len(appName) = 0, \"\", \" in \" & appName) & \". The application may not be running, or its title may differ from the recorded run.\"",
                "    End If",
                "    If matches > 1 Then",
                "        Notice matches & \" windows match class \" & className & \" title \" & title & IIf(Len(appName) = 0, \"\", \" in \" & appName) & \". The one in front was used, and it is held for the rest of the run.\"",
                "    End If",
                "    gWindow = candidate",
                "    apiSetForegroundWindow gWindow",
                "    apiSleep 120",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "Public Sub FocusElement(ByVal stepId As String)",
                "    BeginStep stepId",
                "    FocusOn " + CodeModules.RecordedFacts + ".gLocators",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "Private Sub FocusOn(ByVal locators As String)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim theirs As Long",
                "    Dim mine As Long",
                "    If Len(locators) = 0 Then Exit Sub",
                "    target = " + CodeModules.RuntimeLocator + ".ResolveElement(locators)",
                "    apiSetForegroundWindow gWindow",
                "    theirs = apiGetWindowThreadProcessId(target, 0&)",
                "    mine = apiGetCurrentThreadId()",
                "    If theirs <> mine Then apiAttachThreadInput mine, theirs, 1",
                "    apiSetFocus target",
                "    If theirs <> mine Then apiAttachThreadInput mine, theirs, 0",
                "    apiSleep 80",
                "End Sub",
                "",
                "Public Sub InvokeElement(ByVal stepId As String)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim box As RECT",
                "    Dim atX As Long",
                "    Dim atY As Long",
                "    Dim index As Long",
                "    Dim times As Long",
                "    BeginStep stepId",
                "    If Len(" + CodeModules.RecordedFacts + ".gDrop) > 0 Then",
                "        AppStudioStop \"a drag needs two addresses and VBA sends it as raw pointer movement, which these modules do not emit. Carry this step out in PowerShell or by hand.\"",
                "    End If",
                "    target = " + CodeModules.RuntimeLocator + ".ResolveElement(" + CodeModules.RecordedFacts + ".gLocators)",
                "    If apiGetWindowRect(target, box) = 0 Or (box.Right - box.Left) <= 0 Then",
                "        AppStudioStop \"the element was found but has no usable rectangle right now, so there is nowhere to act.\"",
                "    End If",
                "    times = " + CodeModules.RecordedFacts + ".gTimes",
                "    If " + CodeModules.RecordedFacts + ".gWheel = 0 And times = 1 And " + CodeModules.RecordedFacts + ".gButton = \"left\" Then",
                "        apiSendMessage target, BM_CLICK, 0, 0",
                "        WaitIdle gSettleMs",
                "        Exit Sub",
                "    End If",
                "    atX = box.Left + (box.Right - box.Left) * IIf(" + CodeModules.RecordedFacts + ".gRelX < 0, 0.5, " + CodeModules.RecordedFacts + ".gRelX)",
                "    atY = box.Top + (box.Bottom - box.Top) * IIf(" + CodeModules.RecordedFacts + ".gRelY < 0, 0.5, " + CodeModules.RecordedFacts + ".gRelY)",
                "    " + CodeModules.RuntimeNative + ".MoveTo atX, atY",
                "    If " + CodeModules.RecordedFacts + ".gWheel <> 0 Then",
                "        apiMouseEvent MOUSEEVENTF_WHEEL, 0, 0, " + CodeModules.RecordedFacts + ".gWheel, 0",
                "        WaitIdle gSettleMs",
                "        Exit Sub",
                "    End If",
                "    For index = 1 To times",
                "        If " + CodeModules.RecordedFacts + ".gButton = \"right\" Then",
                "            apiMouseEvent MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0",
                "        ElseIf " + CodeModules.RecordedFacts + ".gButton = \"middle\" Then",
                "            apiMouseEvent MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0",
                "        Else",
                "            apiMouseEvent MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_LEFTUP, 0, 0, 0, 0",
                "        End If",
                "        If index < times Then apiSleep 60",
                "    Next index",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "Public Sub SetElementText(ByVal stepId As String)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    BeginStep stepId",
                "    FocusOn " + CodeModules.RecordedFacts + ".gFocus",
                "    target = " + CodeModules.RuntimeLocator + ".ResolveElement(" + CodeModules.RecordedFacts + ".gLocators)",
                "    If apiSendMessage(target, WM_SETTEXT, 0, StrPtr(" + CodeModules.RecordedFacts + ".gText)) = 0 Then",
                "        AppStudioStop \"the control refused the text, so nothing was written into it.\"",
                "    End If",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "Public Function ReadElementText(ByVal stepId As String) As String",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim length As Long",
                "    Dim buffer As String",
                "    BeginStep stepId",
                "    target = " + CodeModules.RuntimeLocator + ".ResolveElement(" + CodeModules.RecordedFacts + ".gLocators)",
                "    length = CLng(apiSendMessage(target, WM_GETTEXTLENGTH, 0, 0))",
                "    If length <= 0 Then",
                "        ReadElementText = \"\"",
                "        Exit Function",
                "    End If",
                "    buffer = String$(length + 1, vbNullChar)",
                "    apiSendMessage target, WM_GETTEXT, length + 1, StrPtr(buffer)",
                "    ReadElementText = Left$(buffer, length)",
                "End Function",
                "",
                "' One recorded chord, sent after the keyboard has been put back where the",
                "' recording had it.",
                "Public Sub SendKeys(ByVal stepId As String)",
                "    BeginStep stepId",
                "    If Len(" + CodeModules.RecordedFacts + ".gChord) = 0 Then",
                "        AppStudioStop \"the recorded key \" & " + CodeModules.RecordedFacts + ".gRecorded & \" has no equivalent that can be sent from here, so nothing was sent.\"",
                "    End If",
                "    FocusOn " + CodeModules.RecordedFacts + ".gFocus",
                "    VBA.Interaction.SendKeys " + CodeModules.RecordedFacts + ".gChord, True",
                "    apiSleep 60",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "Public Sub WaitGap(ByVal ms As Long)",
                "    Dim wait As Long",
                "    If ms <= 0 Then Exit Sub",
                "    wait = ms",
                "    If wait < 120 Then wait = 120",
                "    If wait > 4000 Then wait = 4000",
                "    apiSleep wait",
                "End Sub",
                "",
                "' Waits for the front window to stop changing, up to a stated ceiling.",
                "' Reaching the ceiling is a measured wait, not a failure.",
                "Public Sub WaitIdle(ByVal budgetMs As Long)",
                "    Dim started As Double",
                "#If VBA7 Then",
                "    Dim last As LongPtr",
                "    Dim front As LongPtr",
                "#Else",
                "    Dim last As Long",
                "    Dim front As Long",
                "#End If",
                "    Dim stable As Long",
                "    started = Timer",
                "    Do While (Timer - started) * 1000 < budgetMs",
                "        front = apiGetForegroundWindow()",
                "        If front = last Then stable = stable + 1 Else stable = 0",
                "        last = front",
                "        If stable >= 2 Then Exit Do",
                "        apiSleep 80",
                "    Loop",
                "End Sub",
                "",
                "' The recording deliberately kept no value for this step. These modules do not",
                "' ask for one either: the keyboard goes on the field and the run waits, so the",
                "' value goes straight from the operator into the application and never exists",
                "' in this project.",
                "Public Sub AskSecret(ByVal stepId As String)",
                "    BeginStep stepId",
                "    FocusOn " + CodeModules.RecordedFacts + ".gLocators",
                "    If Len(gResultPath) > 0 Then",
                "        AppStudioStop \"this step needs a value from the operator, and nobody is here to give it. Run these modules yourself rather than from a host.\"",
                "    End If",
                "    MsgBox " + CodeModules.RecordedFacts + ".gPrompt, vbOKOnly + vbInformation, \"" + CodeModules.Workflow + "\"",
                "    WaitIdle gSettleMs",
                "End Sub",
                "",
                "' The recording holds something no Win32 address can be built from. The run",
                "' stops here with the reason rather than pressing a remembered coordinate.",
                "Public Sub Unsupported(ByVal stepId As String)",
                "    gStepId = stepId",
                "    " + CodeModules.RecordedFacts + ".LoadStep stepId",
                "    If Len(" + CodeModules.RecordedFacts + ".gReason) > 0 Then",
                "        AppStudioStop " + CodeModules.RecordedFacts + ".gReason",
                "    End If",
                "    AppStudioStop \"this step has no address that survives a restart.\"",
                "End Sub",
                "",
                "' Says how it went. Under a host that is watching, into a file; run by a",
                "' person, on the screen. Neither one is left to guess.",
                "Private Sub Report(ByVal state As String, ByVal detail As String)",
                "    Dim channel As Integer",
                "    Dim body As String",
                "    body = detail",
                "    If Len(gNotes) > 0 Then",
                "        If Len(body) > 0 Then body = body & vbCrLf",
                "        body = body & gNotes",
                "    End If",
                "    If Len(gResultPath) > 0 Then",
                "        channel = FreeFile",
                "        Open gResultPath For Output As #channel",
                "        Print #channel, state",
                "        Print #channel, body",
                "        Close #channel",
                "        Exit Sub",
                "    End If",
                "    If state = \"done\" Then",
                "        MsgBox \"The recorded procedure finished.\" & IIf(Len(gNotes) = 0, \"\", vbCrLf & vbCrLf & gNotes), vbOKOnly + vbInformation, \"" + CodeModules.Workflow + "\"",
                "    Else",
                "        MsgBox body, vbOKOnly + vbExclamation, \"" + CodeModules.Workflow + "\"",
                "    End If",
                "End Sub",
                ""
            };
        }

        // ---------- RuntimeLocator : finding the control again ----------

        private static string[] LocatorLines()
        {
            return new string[]
            {
                "' Turning a recorded address back into the control that is on screen now.",
                "'",
                "' VBA has two addresses: a class name with a dialog control id, and a class",
                "' name with its index among its siblings. They are tried in the order the",
                "' recording wrote them, and when none of them finds exactly one control the",
                "' run stops rather than pressing something it cannot name.",
                "'",
                "' Runtime module. You should not need to change anything here to change what",
                "' the procedure does.",
                "",
                "Option Explicit",
                "",
                "Private mSeen As Long",
                "Private mWantClass As String",
                "Private mWantId As Long",
                "Private mWantIndex As Long",
                "#If VBA7 Then",
                "Private mFound As LongPtr",
                "#Else",
                "Private mFound As Long",
                "#End If",
                "",
                "#If VBA7 Then",
                "Public Function ClassOf(ByVal hWnd As LongPtr) As String",
                "#Else",
                "Public Function ClassOf(ByVal hWnd As Long) As String",
                "#End If",
                "    Dim buffer As String",
                "    Dim written As Long",
                "    buffer = String$(256, vbNullChar)",
                "    written = apiGetClassName(hWnd, StrPtr(buffer), 256)",
                "    If written <= 0 Then",
                "        ClassOf = \"\"",
                "    Else",
                "        ClassOf = Left$(buffer, written)",
                "    End If",
                "End Function",
                "",
                "#If VBA7 Then",
                "Public Function TitleOf(ByVal hWnd As LongPtr) As String",
                "#Else",
                "Public Function TitleOf(ByVal hWnd As Long) As String",
                "#End If",
                "    Dim buffer As String",
                "    Dim written As Long",
                "    buffer = String$(512, vbNullChar)",
                "    written = apiGetWindowText(hWnd, StrPtr(buffer), 512)",
                "    If written <= 0 Then",
                "        TitleOf = \"\"",
                "    Else",
                "        TitleOf = Left$(buffer, written)",
                "    End If",
                "End Function",
                "",
                "' The image name of the process a window belongs to, without its path or",
                "' extension - the same shape the recording wrote down. A process this",
                "' account may not open answers as an empty string, which is read as \"not",
                "' known\" rather than as \"does not match\".",
                "#If VBA7 Then",
                "Public Function ProcessOf(ByVal hWnd As LongPtr) As String",
                "    Dim handle As LongPtr",
                "#Else",
                "Public Function ProcessOf(ByVal hWnd As Long) As String",
                "    Dim handle As Long",
                "#End If",
                "    Dim processId As Long",
                "    Dim buffer As String",
                "    Dim room As Long",
                "    Dim cut As Long",
                "    Dim path As String",
                "    ProcessOf = \"\"",
                "    processId = 0",
                "    apiGetWindowThreadProcessId hWnd, processId",
                "    If processId = 0 Then Exit Function",
                "    handle = apiOpenProcess(PROCESS_QUERY_LIMITED, 0, processId)",
                "    If handle = 0 Then Exit Function",
                "    room = 512",
                "    buffer = String$(room, vbNullChar)",
                "    If apiQueryFullProcessImageName(handle, 0, StrPtr(buffer), room) <> 0 Then",
                "        path = Left$(buffer, room)",
                "        cut = InStrRev(path, \"\\\")",
                "        If cut > 0 Then path = Mid$(path, cut + 1)",
                "        cut = InStrRev(path, \".\")",
                "        If cut > 0 Then path = Left$(path, cut - 1)",
                "        ProcessOf = path",
                "    End If",
                "    apiCloseHandle handle",
                "End Function",
                "",
                "' Whether this is a surface a person could click on at all. A window that",
                "' fails any of these cannot be operated, so it is not a candidate and must",
                "' not make a match look ambiguous. A cloaked window in particular is what is",
                "' left of a suspended application, and counting it is how one running",
                "' application came to look like two.",
                "#If VBA7 Then",
                "Public Function Operable(ByVal hWnd As LongPtr) As Boolean",
                "#Else",
                "Public Function Operable(ByVal hWnd As Long) As Boolean",
                "#End If",
                "    Dim box As RECT",
                "    Dim cloaked As Long",
                "    Operable = False",
                "    If apiIsWindowVisible(hWnd) = 0 Then Exit Function",
                "    cloaked = 0",
                "    If apiDwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, cloaked, 4) = 0 Then",
                "        If cloaked <> 0 Then Exit Function",
                "    End If",
                "    If apiGetWindowRect(hWnd, box) = 0 Then Exit Function",
                "    If box.Right - box.Left <= 1 Then Exit Function",
                "    If box.Bottom - box.Top <= 1 Then Exit Function",
                "    If (apiGetWindowLong(hWnd, GWL_STYLE) And WS_CHILD) <> 0 Then Exit Function",
                "    If (apiGetWindowLong(hWnd, GWL_EXSTYLE) And WS_EX_TRANSPARENT) <> 0 Then Exit Function",
                "    Operable = True",
                "End Function",
                "",
                "' Windows Forms and several toolkits append a per process number to the",
                "' window class. The number changes on every launch, so the volatile tail is",
                "' dropped rather than compared as if it were stable.",
                "Public Function StableClass(ByVal value As String) As String",
                "    Dim marker As Long",
                "    StableClass = value",
                "    If Len(value) = 0 Then Exit Function",
                "    marker = InStr(1, value, \".app.\", vbTextCompare)",
                "    If marker > 1 Then",
                "        StableClass = Left$(value, marker - 1)",
                "        Exit Function",
                "    End If",
                "    If InStr(1, value, \"WindowsForms10.\", vbTextCompare) = 1 Then",
                "        marker = InStr(Len(\"WindowsForms10.\") + 1, value, \".\")",
                "        If marker > 0 Then StableClass = Left$(value, marker - 1)",
                "    End If",
                "End Function",
                "",
                "' Walks every descendant of the window without a callback, so the modules",
                "' work in any VBA host. mFound stays zero when nothing matched and the caller",
                "' decides what that means; nothing is guessed here.",
                "#If VBA7 Then",
                "Private Sub WalkChildren(ByVal parent As LongPtr, ByVal mode As Long)",
                "    Dim child As LongPtr",
                "#Else",
                "Private Sub WalkChildren(ByVal parent As Long, ByVal mode As Long)",
                "    Dim child As Long",
                "#End If",
                "    child = apiGetWindow(parent, GW_CHILD)",
                "    Do While child <> 0",
                "        If mode = 1 Then",
                "            If StableClass(ClassOf(child)) = mWantClass Then",
                "                If apiGetDlgCtrlID(child) = mWantId Then",
                "                    mSeen = mSeen + 1",
                "                    If mFound = 0 Then mFound = child",
                "                End If",
                "            End If",
                "        Else",
                "            If StableClass(ClassOf(child)) = mWantClass Then",
                "                If mSeen = mWantIndex And mFound = 0 Then mFound = child",
                "                mSeen = mSeen + 1",
                "            End If",
                "        End If",
                "        WalkChildren child, mode",
                "        child = apiGetWindow(child, GW_HWNDNEXT)",
                "    Loop",
                "End Sub",
                "",
                "#If VBA7 Then",
                "Private Function ResolveByCtrlId(ByVal className As String, ByVal ctrlId As Long) As LongPtr",
                "#Else",
                "Private Function ResolveByCtrlId(ByVal className As String, ByVal ctrlId As Long) As Long",
                "#End If",
                "    If " + CodeModules.RuntimeCore + ".gWindow = 0 Then " + CodeModules.RuntimeCore + ".AppStudioStop \"no window has been found yet, so there is nothing to look inside.\"",
                "    mWantClass = className",
                "    mWantId = ctrlId",
                "    mFound = 0",
                "    mSeen = 0",
                "    WalkChildren " + CodeModules.RuntimeCore + ".gWindow, 1",
                "    If mSeen > 1 Then " + CodeModules.RuntimeCore + ".AppStudioStop \"more than one control has class \" & className & \" and id \" & ctrlId & \". Nothing was sent.\"",
                "    ResolveByCtrlId = mFound",
                "End Function",
                "",
                "#If VBA7 Then",
                "Private Function ResolveByClassIndex(ByVal className As String, ByVal wantedIndex As Long) As LongPtr",
                "#Else",
                "Private Function ResolveByClassIndex(ByVal className As String, ByVal wantedIndex As Long) As Long",
                "#End If",
                "    If " + CodeModules.RuntimeCore + ".gWindow = 0 Then " + CodeModules.RuntimeCore + ".AppStudioStop \"no window has been found yet, so there is nothing to look inside.\"",
                "    mWantClass = className",
                "    mWantIndex = wantedIndex",
                "    mFound = 0",
                "    mSeen = 0",
                "    WalkChildren " + CodeModules.RuntimeCore + ".gWindow, 2",
                "    ResolveByClassIndex = mFound",
                "End Function",
                "",
                "' The locators are given in the order the recording produced them, encoded as",
                "' \"strategy|class|id-or-index\" and separated by a vertical tab. The first one",
                "' that finds exactly one control wins; when none of them does, the run stops.",
                "#If VBA7 Then",
                "Public Function ResolveElement(ByVal locators As String) As LongPtr",
                "    Dim hit As LongPtr",
                "#Else",
                "Public Function ResolveElement(ByVal locators As String) As Long",
                "    Dim hit As Long",
                "#End If",
                "    Dim parts() As String",
                "    Dim fields() As String",
                "    Dim index As Long",
                "    If Len(locators) = 0 Then " + CodeModules.RuntimeCore + ".AppStudioStop \"this step has no Win32 address, so VBA cannot reach it.\"",
                "    parts = Split(locators, vbVerticalTab)",
                "    For index = LBound(parts) To UBound(parts)",
                "        If Len(parts(index)) > 0 Then",
                "            fields = Split(parts(index), \"|\")",
                "            hit = 0",
                "            If fields(0) = \"ctrlId\" Then",
                "                hit = ResolveByCtrlId(fields(1), CLng(fields(2)))",
                "            ElseIf fields(0) = \"classIndex\" Then",
                "                hit = ResolveByClassIndex(fields(1), CLng(fields(2)))",
                "            End If",
                "            If hit <> 0 Then",
                "                ResolveElement = hit",
                "                Exit Function",
                "            End If",
                "        End If",
                "    Next index",
                "    " + CodeModules.RuntimeCore + ".AppStudioStop \"the element could not be found again in this window. Nothing was sent.\"",
                "End Function",
                ""
            };
        }

        // ---------- RuntimeNative : the Win32 declarations ----------

        private static string[] NativeLines()
        {
            return new string[]
            {
                "' The parts that talk to Windows directly: the declarations, the constants,",
                "' and turning a desktop point into what the pointer wants.",
                "'",
                "' Every Declare names the entry point it actually calls with Alias. Without",
                "' it the renamed one is looked for in the DLL and is never found.",
                "'",
                "' Runtime module. You should not need to change anything here to change what",
                "' the procedure does.",
                "",
                "Option Explicit",
                "",
                "#If VBA7 Then",
                "Public Declare PtrSafe Function apiGetDesktopWindow Lib \"user32\" Alias \"GetDesktopWindow\" () As LongPtr",
                "Public Declare PtrSafe Function apiGetWindow Lib \"user32\" Alias \"GetWindow\" (ByVal hWnd As LongPtr, ByVal wCmd As Long) As LongPtr",
                "Public Declare PtrSafe Function apiGetClassName Lib \"user32\" Alias \"GetClassNameW\" (ByVal hWnd As LongPtr, ByVal lpClassName As LongPtr, ByVal nMaxCount As Long) As Long",
                "Public Declare PtrSafe Function apiGetWindowText Lib \"user32\" Alias \"GetWindowTextW\" (ByVal hWnd As LongPtr, ByVal lpString As LongPtr, ByVal nMaxCount As Long) As Long",
                "Public Declare PtrSafe Function apiGetDlgCtrlID Lib \"user32\" Alias \"GetDlgCtrlID\" (ByVal hWnd As LongPtr) As Long",
                "Public Declare PtrSafe Function apiSetForegroundWindow Lib \"user32\" Alias \"SetForegroundWindow\" (ByVal hWnd As LongPtr) As Long",
                "Public Declare PtrSafe Function apiGetForegroundWindow Lib \"user32\" Alias \"GetForegroundWindow\" () As LongPtr",
                "Public Declare PtrSafe Function apiIsWindowVisible Lib \"user32\" Alias \"IsWindowVisible\" (ByVal hWnd As LongPtr) As Long",
                "Public Declare PtrSafe Function apiGetWindowRect Lib \"user32\" Alias \"GetWindowRect\" (ByVal hWnd As LongPtr, ByRef lpRect As RECT) As Long",
                "Public Declare PtrSafe Function apiSendMessage Lib \"user32\" Alias \"SendMessageW\" (ByVal hWnd As LongPtr, ByVal wMsg As Long, ByVal wParam As LongPtr, ByVal lParam As LongPtr) As LongPtr",
                "Public Declare PtrSafe Function apiSetFocus Lib \"user32\" Alias \"SetFocus\" (ByVal hWnd As LongPtr) As LongPtr",
                "Public Declare PtrSafe Function apiAttachThreadInput Lib \"user32\" Alias \"AttachThreadInput\" (ByVal idAttach As Long, ByVal idAttachTo As Long, ByVal fAttach As Long) As Long",
                "Public Declare PtrSafe Function apiGetWindowThreadProcessId Lib \"user32\" Alias \"GetWindowThreadProcessId\" (ByVal hWnd As LongPtr, ByRef lpdwProcessId As Long) As Long",
                "Public Declare PtrSafe Function apiGetCurrentThreadId Lib \"kernel32\" Alias \"GetCurrentThreadId\" () As Long",
                "Public Declare PtrSafe Sub apiMouseEvent Lib \"user32\" Alias \"mouse_event\" (ByVal dwFlags As Long, ByVal dx As Long, ByVal dy As Long, ByVal dwData As Long, ByVal dwExtraInfo As LongPtr)",
                "Public Declare PtrSafe Function apiGetSystemMetrics Lib \"user32\" Alias \"GetSystemMetrics\" (ByVal nIndex As Long) As Long",
                "Public Declare PtrSafe Sub apiSleep Lib \"kernel32\" Alias \"Sleep\" (ByVal dwMilliseconds As Long)",
                "Public Declare PtrSafe Function apiGetWindowLong Lib \"user32\" Alias \"GetWindowLongW\" (ByVal hWnd As LongPtr, ByVal nIndex As Long) As Long",
                "Public Declare PtrSafe Function apiDwmGetWindowAttribute Lib \"dwmapi\" Alias \"DwmGetWindowAttribute\" (ByVal hWnd As LongPtr, ByVal dwAttribute As Long, ByRef pvAttribute As Long, ByVal cbAttribute As Long) As Long",
                "Public Declare PtrSafe Function apiOpenProcess Lib \"kernel32\" Alias \"OpenProcess\" (ByVal dwDesiredAccess As Long, ByVal bInheritHandle As Long, ByVal dwProcessId As Long) As LongPtr",
                "Public Declare PtrSafe Function apiCloseHandle Lib \"kernel32\" Alias \"CloseHandle\" (ByVal hObject As LongPtr) As Long",
                "Public Declare PtrSafe Function apiQueryFullProcessImageName Lib \"kernel32\" Alias \"QueryFullProcessImageNameW\" (ByVal hProcess As LongPtr, ByVal dwFlags As Long, ByVal lpExeName As LongPtr, ByRef lpdwSize As Long) As Long",
                "Public Declare PtrSafe Function apiGetCurrentProcessId Lib \"kernel32\" Alias \"GetCurrentProcessId\" () As Long",
                "#Else",
                "Public Declare Function apiGetDesktopWindow Lib \"user32\" Alias \"GetDesktopWindow\" () As Long",
                "Public Declare Function apiGetWindow Lib \"user32\" Alias \"GetWindow\" (ByVal hWnd As Long, ByVal wCmd As Long) As Long",
                "Public Declare Function apiGetClassName Lib \"user32\" Alias \"GetClassNameW\" (ByVal hWnd As Long, ByVal lpClassName As Long, ByVal nMaxCount As Long) As Long",
                "Public Declare Function apiGetWindowText Lib \"user32\" Alias \"GetWindowTextW\" (ByVal hWnd As Long, ByVal lpString As Long, ByVal nMaxCount As Long) As Long",
                "Public Declare Function apiGetDlgCtrlID Lib \"user32\" Alias \"GetDlgCtrlID\" (ByVal hWnd As Long) As Long",
                "Public Declare Function apiSetForegroundWindow Lib \"user32\" Alias \"SetForegroundWindow\" (ByVal hWnd As Long) As Long",
                "Public Declare Function apiGetForegroundWindow Lib \"user32\" Alias \"GetForegroundWindow\" () As Long",
                "Public Declare Function apiIsWindowVisible Lib \"user32\" Alias \"IsWindowVisible\" (ByVal hWnd As Long) As Long",
                "Public Declare Function apiGetWindowRect Lib \"user32\" Alias \"GetWindowRect\" (ByVal hWnd As Long, ByRef lpRect As RECT) As Long",
                "Public Declare Function apiSendMessage Lib \"user32\" Alias \"SendMessageW\" (ByVal hWnd As Long, ByVal wMsg As Long, ByVal wParam As Long, ByVal lParam As Long) As Long",
                "Public Declare Function apiSetFocus Lib \"user32\" Alias \"SetFocus\" (ByVal hWnd As Long) As Long",
                "Public Declare Function apiAttachThreadInput Lib \"user32\" Alias \"AttachThreadInput\" (ByVal idAttach As Long, ByVal idAttachTo As Long, ByVal fAttach As Long) As Long",
                "Public Declare Function apiGetWindowThreadProcessId Lib \"user32\" Alias \"GetWindowThreadProcessId\" (ByVal hWnd As Long, ByRef lpdwProcessId As Long) As Long",
                "Public Declare Function apiGetCurrentThreadId Lib \"kernel32\" Alias \"GetCurrentThreadId\" () As Long",
                "Public Declare Sub apiMouseEvent Lib \"user32\" Alias \"mouse_event\" (ByVal dwFlags As Long, ByVal dx As Long, ByVal dy As Long, ByVal dwData As Long, ByVal dwExtraInfo As Long)",
                "Public Declare Function apiGetSystemMetrics Lib \"user32\" Alias \"GetSystemMetrics\" (ByVal nIndex As Long) As Long",
                "Public Declare Sub apiSleep Lib \"kernel32\" Alias \"Sleep\" (ByVal dwMilliseconds As Long)",
                "Public Declare Function apiGetWindowLong Lib \"user32\" Alias \"GetWindowLongW\" (ByVal hWnd As Long, ByVal nIndex As Long) As Long",
                "Public Declare Function apiDwmGetWindowAttribute Lib \"dwmapi\" Alias \"DwmGetWindowAttribute\" (ByVal hWnd As Long, ByVal dwAttribute As Long, ByRef pvAttribute As Long, ByVal cbAttribute As Long) As Long",
                "Public Declare Function apiOpenProcess Lib \"kernel32\" Alias \"OpenProcess\" (ByVal dwDesiredAccess As Long, ByVal bInheritHandle As Long, ByVal dwProcessId As Long) As Long",
                "Public Declare Function apiCloseHandle Lib \"kernel32\" Alias \"CloseHandle\" (ByVal hObject As Long) As Long",
                "Public Declare Function apiQueryFullProcessImageName Lib \"kernel32\" Alias \"QueryFullProcessImageNameW\" (ByVal hProcess As Long, ByVal dwFlags As Long, ByVal lpExeName As Long, ByRef lpdwSize As Long) As Long",
                "Public Declare Function apiGetCurrentProcessId Lib \"kernel32\" Alias \"GetCurrentProcessId\" () As Long",
                "#End If",
                "",
                "Public Type RECT",
                "    Left As Long",
                "    Top As Long",
                "    Right As Long",
                "    Bottom As Long",
                "End Type",
                "",
                "Public Const GW_CHILD As Long = 5",
                "Public Const GW_HWNDNEXT As Long = 2",
                "Public Const WM_SETTEXT As Long = &HC",
                "Public Const WM_GETTEXT As Long = &HD",
                "Public Const WM_GETTEXTLENGTH As Long = &HE",
                "Public Const BM_CLICK As Long = &HF5",
                "Public Const MOUSEEVENTF_MOVE_ABS As Long = &H8001",
                "Public Const MOUSEEVENTF_LEFTDOWN As Long = &H2",
                "Public Const MOUSEEVENTF_LEFTUP As Long = &H4",
                "Public Const MOUSEEVENTF_RIGHTDOWN As Long = &H8",
                "Public Const MOUSEEVENTF_RIGHTUP As Long = &H10",
                "Public Const MOUSEEVENTF_MIDDLEDOWN As Long = &H20",
                "Public Const MOUSEEVENTF_MIDDLEUP As Long = &H40",
                "Public Const MOUSEEVENTF_WHEEL As Long = &H800",
                "Public Const SM_XVIRTUALSCREEN As Long = 76",
                "Public Const SM_YVIRTUALSCREEN As Long = 77",
                "Public Const SM_CXVIRTUALSCREEN As Long = 78",
                "Public Const SM_CYVIRTUALSCREEN As Long = 79",
                "Public Const GWL_STYLE As Long = -16",
                "Public Const GWL_EXSTYLE As Long = -20",
                "Public Const WS_CHILD As Long = &H40000000",
                "Public Const WS_EX_TRANSPARENT As Long = &H20",
                "Public Const DWMWA_CLOAKED As Long = 14",
                "Public Const PROCESS_QUERY_LIMITED As Long = &H1000",
                "",
                "Public Sub MoveTo(ByVal x As Long, ByVal y As Long)",
                "    Dim spanX As Long",
                "    Dim spanY As Long",
                "    spanX = apiGetSystemMetrics(SM_CXVIRTUALSCREEN)",
                "    spanY = apiGetSystemMetrics(SM_CYVIRTUALSCREEN)",
                "    If spanX <= 0 Or spanY <= 0 Then " + CodeModules.RuntimeCore + ".AppStudioStop \"the virtual desktop reported no size, so no pointer position can be expressed.\"",
                "    apiMouseEvent MOUSEEVENTF_MOVE_ABS, _",
                "        CLng((x - apiGetSystemMetrics(SM_XVIRTUALSCREEN)) * 65535# / spanX), _",
                "        CLng((y - apiGetSystemMetrics(SM_YVIRTUALSCREEN)) * 65535# / spanY), 0, 0",
                "    apiSleep 40",
                "End Sub",
                ""
            };
        }

        // ---------- shared helpers ----------

        private static string Number(double value)
        {
            if (value < 0) return "-1";
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // Only the two strategies VBA can carry out are written into the
        // modules. Leaving the others in would read as if it had tried them.
        public static string Encode(List<ElementLocator> locators)
        {
            StringBuilder text = new StringBuilder();
            if (locators == null) return "";
            for (int index = 0; index < locators.Count; index++)
            {
                ElementLocator locator = locators[index];
                if (String.Equals(locator.Strategy, ElementLocator.StrategyCtrlId, StringComparison.Ordinal))
                {
                    if (text.Length != 0) text.Append('\v');
                    text.Append("ctrlId|").Append(Field(locator.ClassName)).Append('|').Append(locator.CtrlId.ToString(CultureInfo.InvariantCulture));
                }
                else if (String.Equals(locator.Strategy, ElementLocator.StrategyClassIndex, StringComparison.Ordinal))
                {
                    if (text.Length != 0) text.Append('\v');
                    text.Append("classIndex|").Append(Field(locator.ClassName)).Append('|').Append(locator.ClassIndex.ToString(CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }

        // The separator is a vertical tab and the fields are split on a bar, so
        // neither may appear inside a value.
        private static string Field(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Replace("|", "/").Replace("\v", " ");
        }

        private static string Escape(string value)
        {
            return Comment(value).Replace("\"", "\"\"");
        }

        // A VBA string literal ends at the first quote in it, so every quote
        // inside one is doubled. A literal cannot span lines either, so a line
        // break becomes a separator.
        public static string Literal(string value)
        {
            return "\"" + Escape(value) + "\"";
        }

        public static string Comment(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\r') continue;
                if (character == '\n') { text.Append(" / "); continue; }
                if (character == '\v') { text.Append(' '); continue; }
                text.Append(character);
            }
            return text.ToString();
        }
    }
}
