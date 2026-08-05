namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The VBA half of the same automation. It is not a translation of the
    // PowerShell one and it is not a lesser version of it: both are written
    // from the same operation list and both carry the same nine names.
    //
    // What differs is what each language can reach. VBA has no UI Automation on
    // a machine where nothing may be installed, so it addresses controls the
    // Win32 way - a class name with a dialog control id, or a class name with
    // its index among its siblings. An element that only ever existed in the
    // accessibility tree has no Win32 address, and this generator says so at
    // the point it occurs instead of inventing a coordinate for it.
    public static class VbaGen
    {
        public const string ModuleName = "AppStudioRun";

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
            text.AppendLine("Attribute VB_Name = \"" + ModuleName + "\"");
            text.AppendLine("' " + App.Name + " " + App.Version + " - generated automation (VBA)");
            text.AppendLine("' session " + Comment(plan.SessionId) + "  " + Comment(plan.SessionTitle));
            text.AppendLine("'");
            text.AppendLine("' This drives the real applications on this machine. Read it before you run it.");
            text.AppendLine("' Import this file into a VBA project and run RunRecordedProcedure.");
            text.AppendLine("'");
            text.AppendLine("' VBA reaches controls through Win32 only: a class name with a control id,");
            text.AppendLine("' or a class name with its index. Steps whose only address is a UI Automation");
            text.AppendLine("' one are marked below and stop the run with the reason, because pressing a");
            text.AppendLine("' remembered coordinate instead would be pressing an unknown part of the");
            text.AppendLine("' application.");
            text.AppendLine("'");
            text.AppendLine("' A secret is never typed by this module. It focuses the field and waits for");
            text.AppendLine("' the operator to enter the value directly, so the value never exists here.");
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
        }

        private static void Library(StringBuilder text)
        {
            string[] lines = new string[]
            {
                "'=== operation library - the same nine operations the PowerShell script has ===",
                "",
                "#If VBA7 Then",
                "Private Declare PtrSafe Function apiGetDesktopWindow Lib \"user32\" Alias \"GetDesktopWindow\" () As LongPtr",
                "Private Declare PtrSafe Function apiGetWindow Lib \"user32\" Alias \"GetWindow\" (ByVal hWnd As LongPtr, ByVal wCmd As Long) As LongPtr",
                "Private Declare PtrSafe Function apiGetClassName Lib \"user32\" Alias \"GetClassNameW\" (ByVal hWnd As LongPtr, ByVal lpClassName As LongPtr, ByVal nMaxCount As Long) As Long",
                "Private Declare PtrSafe Function apiGetWindowText Lib \"user32\" Alias \"GetWindowTextW\" (ByVal hWnd As LongPtr, ByVal lpString As LongPtr, ByVal nMaxCount As Long) As Long",
                "Private Declare PtrSafe Function apiGetDlgCtrlID Lib \"user32\" Alias \"GetDlgCtrlID\" (ByVal hWnd As LongPtr) As Long",
                "Private Declare PtrSafe Function apiSetForegroundWindow Lib \"user32\" Alias \"SetForegroundWindow\" (ByVal hWnd As LongPtr) As Long",
                "Private Declare PtrSafe Function apiGetForegroundWindow Lib \"user32\" Alias \"GetForegroundWindow\" () As LongPtr",
                "Private Declare PtrSafe Function apiIsWindowVisible Lib \"user32\" Alias \"IsWindowVisible\" (ByVal hWnd As LongPtr) As Long",
                "Private Declare PtrSafe Function apiGetWindowRect Lib \"user32\" Alias \"GetWindowRect\" (ByVal hWnd As LongPtr, ByRef lpRect As RECT) As Long",
                "Private Declare PtrSafe Function apiSendMessage Lib \"user32\" Alias \"SendMessageW\" (ByVal hWnd As LongPtr, ByVal wMsg As Long, ByVal wParam As LongPtr, ByVal lParam As LongPtr) As LongPtr",
                "Private Declare PtrSafe Function apiSetFocus Lib \"user32\" Alias \"SetFocus\" (ByVal hWnd As LongPtr) As LongPtr",
                "Private Declare PtrSafe Function apiAttachThreadInput Lib \"user32\" Alias \"AttachThreadInput\" (ByVal idAttach As Long, ByVal idAttachTo As Long, ByVal fAttach As Long) As Long",
                "Private Declare PtrSafe Function apiGetWindowThreadProcessId Lib \"user32\" Alias \"GetWindowThreadProcessId\" (ByVal hWnd As LongPtr, ByRef lpdwProcessId As Long) As Long",
                "Private Declare PtrSafe Function apiGetCurrentThreadId Lib \"kernel32\" Alias \"GetCurrentThreadId\" () As Long",
                "Private Declare PtrSafe Sub apiMouseEvent Lib \"user32\" Alias \"mouse_event\" (ByVal dwFlags As Long, ByVal dx As Long, ByVal dy As Long, ByVal dwData As Long, ByVal dwExtraInfo As LongPtr)",
                "Private Declare PtrSafe Function apiGetSystemMetrics Lib \"user32\" Alias \"GetSystemMetrics\" (ByVal nIndex As Long) As Long",
                "Private Declare PtrSafe Sub apiSleep Lib \"kernel32\" Alias \"Sleep\" (ByVal dwMilliseconds As Long)",
                "#Else",
                "Private Declare Function apiGetDesktopWindow Lib \"user32\" Alias \"GetDesktopWindow\" () As Long",
                "Private Declare Function apiGetWindow Lib \"user32\" Alias \"GetWindow\" (ByVal hWnd As Long, ByVal wCmd As Long) As Long",
                "Private Declare Function apiGetClassName Lib \"user32\" Alias \"GetClassNameW\" (ByVal hWnd As Long, ByVal lpClassName As Long, ByVal nMaxCount As Long) As Long",
                "Private Declare Function apiGetWindowText Lib \"user32\" Alias \"GetWindowTextW\" (ByVal hWnd As Long, ByVal lpString As Long, ByVal nMaxCount As Long) As Long",
                "Private Declare Function apiGetDlgCtrlID Lib \"user32\" Alias \"GetDlgCtrlID\" (ByVal hWnd As Long) As Long",
                "Private Declare Function apiSetForegroundWindow Lib \"user32\" Alias \"SetForegroundWindow\" (ByVal hWnd As Long) As Long",
                "Private Declare Function apiGetForegroundWindow Lib \"user32\" Alias \"GetForegroundWindow\" () As Long",
                "Private Declare Function apiIsWindowVisible Lib \"user32\" Alias \"IsWindowVisible\" (ByVal hWnd As Long) As Long",
                "Private Declare Function apiGetWindowRect Lib \"user32\" Alias \"GetWindowRect\" (ByVal hWnd As Long, ByRef lpRect As RECT) As Long",
                "Private Declare Function apiSendMessage Lib \"user32\" Alias \"SendMessageW\" (ByVal hWnd As Long, ByVal wMsg As Long, ByVal wParam As Long, ByVal lParam As Long) As Long",
                "Private Declare Function apiSetFocus Lib \"user32\" Alias \"SetFocus\" (ByVal hWnd As Long) As Long",
                "Private Declare Function apiAttachThreadInput Lib \"user32\" Alias \"AttachThreadInput\" (ByVal idAttach As Long, ByVal idAttachTo As Long, ByVal fAttach As Long) As Long",
                "Private Declare Function apiGetWindowThreadProcessId Lib \"user32\" Alias \"GetWindowThreadProcessId\" (ByVal hWnd As Long, ByRef lpdwProcessId As Long) As Long",
                "Private Declare Function apiGetCurrentThreadId Lib \"kernel32\" Alias \"GetCurrentThreadId\" () As Long",
                "Private Declare Sub apiMouseEvent Lib \"user32\" Alias \"mouse_event\" (ByVal dwFlags As Long, ByVal dx As Long, ByVal dy As Long, ByVal dwData As Long, ByVal dwExtraInfo As Long)",
                "Private Declare Function apiGetSystemMetrics Lib \"user32\" Alias \"GetSystemMetrics\" (ByVal nIndex As Long) As Long",
                "Private Declare Sub apiSleep Lib \"kernel32\" Alias \"Sleep\" (ByVal dwMilliseconds As Long)",
                "#End If",
                "",
                "Private Type RECT",
                "    Left As Long",
                "    Top As Long",
                "    Right As Long",
                "    Bottom As Long",
                "End Type",
                "",
                "Private Const GW_CHILD As Long = 5",
                "Private Const GW_HWNDNEXT As Long = 2",
                "Private Const WM_SETTEXT As Long = &HC",
                "Private Const WM_GETTEXT As Long = &HD",
                "Private Const WM_GETTEXTLENGTH As Long = &HE",
                "Private Const BM_CLICK As Long = &HF5",
                "Private Const MOUSEEVENTF_MOVE_ABS As Long = &H8001",
                "Private Const MOUSEEVENTF_LEFTDOWN As Long = &H2",
                "Private Const MOUSEEVENTF_LEFTUP As Long = &H4",
                "Private Const MOUSEEVENTF_RIGHTDOWN As Long = &H8",
                "Private Const MOUSEEVENTF_RIGHTUP As Long = &H10",
                "Private Const MOUSEEVENTF_MIDDLEDOWN As Long = &H20",
                "Private Const MOUSEEVENTF_MIDDLEUP As Long = &H40",
                "Private Const MOUSEEVENTF_WHEEL As Long = &H800",
                "Private Const SM_XVIRTUALSCREEN As Long = 76",
                "Private Const SM_YVIRTUALSCREEN As Long = 77",
                "Private Const SM_CXVIRTUALSCREEN As Long = 78",
                "Private Const SM_CYVIRTUALSCREEN As Long = 79",
                "",
                "#If VBA7 Then",
                "Private mWindow As LongPtr",
                "Private mFound As LongPtr",
                "#Else",
                "Private mWindow As Long",
                "Private mFound As Long",
                "#End If",
                "Private mStep As String",
                "' Where to write what happened. A host that sets this is running the",
                "' module unattended, and nothing may open a window it cannot answer:",
                "' an unhandled VBA error puts up a modal break dialog that the caller",
                "' never sees, which is a hang with no explanation.",
                "Private mResultPath As String",
                "Private mSeen As Long",
                "Private mWantClass As String",
                "Private mWantId As Long",
                "Private mWantIndex As Long",
                "",
                "Private Sub AppStudioStop(ByVal reason As String)",
                "    Err.Raise vbObjectError + 513, \"" + ModuleName + "\", \"App Studio step \" & mStep & \" stopped: \" & reason",
                "End Sub",
                "",
                "#If VBA7 Then",
                "Private Function ClassOf(ByVal hWnd As LongPtr) As String",
                "#Else",
                "Private Function ClassOf(ByVal hWnd As Long) As String",
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
                "Private Function TitleOf(ByVal hWnd As LongPtr) As String",
                "#Else",
                "Private Function TitleOf(ByVal hWnd As Long) As String",
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
                "' Windows Forms and several toolkits append a per process number to the",
                "' window class. The number changes on every launch, so the volatile tail is",
                "' dropped rather than compared as if it were stable.",
                "Private Function StableClass(ByVal value As String) As String",
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
                "' Walks every descendant of the window without a callback, so the module",
                "' works in any VBA host. mFound stays zero when nothing matched and the",
                "' caller decides what that means; nothing is guessed here.",
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
                "' A control id, or a class and an index. Those are the two addresses VBA",
                "' has. Anything else is refused by the caller rather than approximated.",
                "#If VBA7 Then",
                "Private Function ResolveByCtrlId(ByVal className As String, ByVal ctrlId As Long) As LongPtr",
                "#Else",
                "Private Function ResolveByCtrlId(ByVal className As String, ByVal ctrlId As Long) As Long",
                "#End If",
                "    If mWindow = 0 Then AppStudioStop \"no window has been found yet, so there is nothing to look inside.\"",
                "    mWantClass = className",
                "    mWantId = ctrlId",
                "    mFound = 0",
                "    mSeen = 0",
                "    WalkChildren mWindow, 1",
                "    If mSeen > 1 Then AppStudioStop \"more than one control has class \" & className & \" and id \" & ctrlId & \". Nothing was sent.\"",
                "    ResolveByCtrlId = mFound",
                "End Function",
                "",
                "#If VBA7 Then",
                "Private Function ResolveByClassIndex(ByVal className As String, ByVal wantedIndex As Long) As LongPtr",
                "#Else",
                "Private Function ResolveByClassIndex(ByVal className As String, ByVal wantedIndex As Long) As Long",
                "#End If",
                "    If mWindow = 0 Then AppStudioStop \"no window has been found yet, so there is nothing to look inside.\"",
                "    mWantClass = className",
                "    mWantIndex = wantedIndex",
                "    mFound = 0",
                "    mSeen = 0",
                "    WalkChildren mWindow, 2",
                "    ResolveByClassIndex = mFound",
                "End Function",
                "",
                "' The locators are given in the order the recording produced them, encoded",
                "' as \"strategy|class|id-or-index\" and separated by a vertical tab. The",
                "' first one that finds exactly one control wins; when none of them does,",
                "' the run stops.",
                "#If VBA7 Then",
                "Private Function ResolveElement(ByVal locators As String) As LongPtr",
                "    Dim hit As LongPtr",
                "#Else",
                "Private Function ResolveElement(ByVal locators As String) As Long",
                "    Dim hit As Long",
                "#End If",
                "    Dim parts() As String",
                "    Dim fields() As String",
                "    Dim index As Long",
                "    If Len(locators) = 0 Then AppStudioStop \"this step has no Win32 address, so VBA cannot reach it.\"",
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
                "    AppStudioStop \"the element could not be found again in this window. Nothing was sent.\"",
                "End Function",
                "",
                "Public Sub FindWindow(ByVal className As String, ByVal title As String)",
                "    Dim deadline As Double",
                "#If VBA7 Then",
                "    Dim candidate As LongPtr",
                "    Dim child As LongPtr",
                "#Else",
                "    Dim candidate As Long",
                "    Dim child As Long",
                "#End If",
                "    Dim matches As Long",
                "    deadline = Timer + 10",
                "    Do",
                "        matches = 0",
                "        candidate = 0",
                "        ' The children of the desktop are the top level windows.",
                "        child = apiGetWindow(apiGetDesktopWindow(), GW_CHILD)",
                "        Do While child <> 0",
                "            If apiIsWindowVisible(child) <> 0 Then",
                "                If (Len(className) = 0 Or ClassOf(child) = className) And (Len(title) = 0 Or TitleOf(child) = title) Then",
                "                    matches = matches + 1",
                "                    If candidate = 0 Then candidate = child",
                "                End If",
                "            End If",
                "            child = apiGetWindow(child, GW_HWNDNEXT)",
                "        Loop",
                "        If matches > 1 Then",
                "            AppStudioStop \"more than one window matches class \" & className & \" title \" & title & \" (\" & matches & \"). Nothing was pressed, because there is no way to tell which one the recording meant.\"",
                "        End If",
                "        If matches = 1 Then Exit Do",
                "        apiSleep 150",
                "    Loop While Timer < deadline",
                "    If candidate = 0 Then",
                "        AppStudioStop \"no window matches class \" & className & \" title \" & title & \". The application may not be running, or its title may differ from the recorded run.\"",
                "    End If",
                "    mWindow = candidate",
                "    apiSetForegroundWindow mWindow",
                "    apiSleep 120",
                "End Sub",
                "",
                "Public Sub FocusElement(ByVal locators As String)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim theirs As Long",
                "    Dim mine As Long",
                "    target = ResolveElement(locators)",
                "    apiSetForegroundWindow mWindow",
                "    theirs = apiGetWindowThreadProcessId(target, 0&)",
                "    mine = apiGetCurrentThreadId()",
                "    If theirs <> mine Then apiAttachThreadInput mine, theirs, 1",
                "    apiSetFocus target",
                "    If theirs <> mine Then apiAttachThreadInput mine, theirs, 0",
                "    apiSleep 80",
                "End Sub",
                "",
                "Public Sub InvokeElement(ByVal locators As String, ByVal button As String, ByVal times As Long, ByVal relX As Double, ByVal relY As Double, ByVal wheelDelta As Long)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim box As RECT",
                "    Dim atX As Long",
                "    Dim atY As Long",
                "    Dim index As Long",
                "    target = ResolveElement(locators)",
                "    If apiGetWindowRect(target, box) = 0 Or (box.Right - box.Left) <= 0 Then",
                "        AppStudioStop \"the element was found but has no usable rectangle right now, so there is nowhere to act.\"",
                "    End If",
                "    If wheelDelta = 0 And times = 1 And button = \"left\" Then",
                "        apiSendMessage target, BM_CLICK, 0, 0",
                "        Exit Sub",
                "    End If",
                "    atX = box.Left + (box.Right - box.Left) * IIf(relX < 0, 0.5, relX)",
                "    atY = box.Top + (box.Bottom - box.Top) * IIf(relY < 0, 0.5, relY)",
                "    MoveTo atX, atY",
                "    If wheelDelta <> 0 Then",
                "        apiMouseEvent MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, 0",
                "        Exit Sub",
                "    End If",
                "    For index = 1 To times",
                "        If button = \"right\" Then",
                "            apiMouseEvent MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0",
                "        ElseIf button = \"middle\" Then",
                "            apiMouseEvent MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0",
                "        Else",
                "            apiMouseEvent MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0",
                "            apiMouseEvent MOUSEEVENTF_LEFTUP, 0, 0, 0, 0",
                "        End If",
                "        If index < times Then apiSleep 60",
                "    Next index",
                "End Sub",
                "",
                "Private Sub MoveTo(ByVal x As Long, ByVal y As Long)",
                "    Dim spanX As Long",
                "    Dim spanY As Long",
                "    spanX = apiGetSystemMetrics(SM_CXVIRTUALSCREEN)",
                "    spanY = apiGetSystemMetrics(SM_CYVIRTUALSCREEN)",
                "    If spanX <= 0 Or spanY <= 0 Then AppStudioStop \"the virtual desktop reported no size, so no pointer position can be expressed.\"",
                "    apiMouseEvent MOUSEEVENTF_MOVE_ABS, _",
                "        CLng((x - apiGetSystemMetrics(SM_XVIRTUALSCREEN)) * 65535# / spanX), _",
                "        CLng((y - apiGetSystemMetrics(SM_YVIRTUALSCREEN)) * 65535# / spanY), 0, 0",
                "    apiSleep 40",
                "End Sub",
                "",
                "Public Sub SetElementText(ByVal locators As String, ByVal value As String)",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    target = ResolveElement(locators)",
                "    If apiSendMessage(target, WM_SETTEXT, 0, StrPtr(value)) = 0 Then",
                "        AppStudioStop \"the control refused the text, so nothing was written into it.\"",
                "    End If",
                "End Sub",
                "",
                "Public Function ReadElementText(ByVal locators As String) As String",
                "#If VBA7 Then",
                "    Dim target As LongPtr",
                "#Else",
                "    Dim target As Long",
                "#End If",
                "    Dim length As Long",
                "    Dim buffer As String",
                "    target = ResolveElement(locators)",
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
                "Public Sub SendKeys(ByVal chord As String, ByVal recorded As String)",
                "    If Len(chord) = 0 Then",
                "        AppStudioStop \"the recorded key \" & recorded & \" has no equivalent that can be sent from here, so nothing was sent.\"",
                "    End If",
                "    VBA.Interaction.SendKeys chord, True",
                "    apiSleep 60",
                "End Sub",
                "",
                "Public Sub WaitGap(ByVal ms As Long)",
                "    Dim wait As Long",
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
                "' The recording deliberately kept no value for this step. This module does",
                "' not ask for one either: it puts the keyboard on the field and waits, so",
                "' the value goes straight from the operator into the application and never",
                "' exists in this project.",
                "Public Sub AskSecret(ByVal locators As String, ByVal prompt As String)",
                "    FocusElement locators",
                "    If Len(mResultPath) > 0 Then",
                "        AppStudioStop \"this step needs a value from the operator, and nobody is here to give it. Run this module yourself rather than from a host.\"",
                "    End If",
                "    MsgBox prompt, vbOKOnly + vbInformation, \"" + ModuleName + "\"",
                "End Sub",
                "",
                "' Says how it went. Under a host that is watching, into a file; run by a",
                "' person, on the screen. Neither one is left to guess.",
                "Private Sub Report(ByVal state As String, ByVal detail As String)",
                "    Dim channel As Integer",
                "    If Len(mResultPath) > 0 Then",
                "        channel = FreeFile",
                "        Open mResultPath For Output As #channel",
                "        Print #channel, state",
                "        Print #channel, detail",
                "        Close #channel",
                "        Exit Sub",
                "    End If",
                "    If state = \"done\" Then",
                "        MsgBox \"The recorded procedure finished.\", vbOKOnly + vbInformation, \"" + ModuleName + "\"",
                "    Else",
                "        MsgBox detail, vbOKOnly + vbExclamation, \"" + ModuleName + "\"",
                "    End If",
                "End Sub",
                "",
                "Public Sub Unsupported(ByVal reason As String)",
                "    AppStudioStop reason",
                "End Sub",
                "",
                ""
            };
            for (int index = 0; index < lines.Length; index++) text.AppendLine(lines[index]);
        }

        private static void Procedure(StringBuilder text, ScriptPlan plan)
        {
            text.AppendLine("'=== the recorded procedure ===");
            text.AppendLine();
            text.AppendLine("' Run this one yourself. It reports on screen.");
            text.AppendLine("Public Sub RunRecordedProcedure()");
            text.AppendLine("    mResultPath = \"\"");
            text.AppendLine("    RunBody");
            text.AppendLine("End Sub");
            text.AppendLine();
            text.AppendLine("' A host that is watching calls this one and reads the file. Nothing here");
            text.AppendLine("' may open a window in that case, because there is nobody to close it.");
            text.AppendLine("Public Sub RunRecordedProcedureTo(ByVal resultPath As String)");
            text.AppendLine("    mResultPath = resultPath");
            text.AppendLine("    RunBody");
            text.AppendLine("    mResultPath = \"\"");
            text.AppendLine("End Sub");
            text.AppendLine();
            text.AppendLine("Private Sub RunBody()");
            text.AppendLine("    On Error GoTo Failed");
            for (int index = 0; index < plan.Ops.Count; index++)
            {
                ScriptOp op = plan.Ops[index];
                text.AppendLine("    ' " + op.StepId + "  " + Comment(op.Headline));
                text.AppendLine("    mStep = " + Literal(op.StepId));
                text.AppendLine("    " + Line(op));
            }
            text.AppendLine("    Report \"done\", \"\"");
            text.AppendLine("    Exit Sub");
            text.AppendLine("Failed:");
            text.AppendLine("    Report \"stopped\", Err.Description");
            text.AppendLine("End Sub");
        }

        private static string Line(ScriptOp op)
        {
            if (op.Op == ScriptOp.FindWindow)
            {
                return "FindWindow " + Literal(op.WindowClass) + ", " + Literal(op.WindowTitle);
            }
            if (op.Op == ScriptOp.WaitGap) return "WaitGap " + op.GapMs.ToString(CultureInfo.InvariantCulture);
            if (op.Op == ScriptOp.WaitIdle) return "WaitIdle 2500";
            if (op.Op == ScriptOp.Unsupported) return "Unsupported " + Literal(op.Reason);
            if (op.Op == ScriptOp.SendKeys)
            {
                return "SendKeys " + Literal(PowerShellGen.SendKeysChord(op.Keys)) + ", " + Literal(op.Keys);
            }
            // Everything below needs an address. VBA has one only for the two
            // Win32 strategies, so a step it cannot reach is refused here with
            // the reason rather than approximated with a coordinate.
            if (!op.ReachableFromVba)
            {
                return "Unsupported " + Literal("this step is addressed only through UI Automation (" + Strategies(op) +
                    "), and VBA has no Win32 address for it. Nothing was sent.");
            }
            if (op.Op == ScriptOp.FocusElement) return "FocusElement " + Literal(Encode(op.Locators));
            if (op.Op == ScriptOp.SetElementText)
            {
                return "SetElementText " + Literal(Encode(op.Locators)) + ", " + Literal(op.Text);
            }
            if (op.Op == ScriptOp.AskSecret)
            {
                return "AskSecret " + Literal(Encode(op.Locators)) + ", " + Literal(PowerShellGen.SecretPrompt(op) +
                    " (type it into the application, then press OK)");
            }
            if (op.Op == ScriptOp.ReadElementText) return "ReadElementText " + Literal(Encode(op.Locators));
            if (op.DropLocators != null && op.DropLocators.Count > 0)
            {
                return "Unsupported " + Literal("a drag needs two addresses and VBA sends it as raw pointer movement, " +
                    "which this generator does not emit. Carry this step out in PowerShell or by hand.");
            }
            return "InvokeElement " + Literal(Encode(op.Locators)) + ", " + Literal(op.Button) + ", " +
                op.Times.ToString(CultureInfo.InvariantCulture) + ", " + Number(op.RelX) + ", " + Number(op.RelY) + ", " +
                op.WheelDelta.ToString(CultureInfo.InvariantCulture);
        }

        private static string Strategies(ScriptOp op)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < op.Locators.Count; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(op.Locators[index].Strategy);
            }
            return text.Length == 0 ? "no locator at all" : text.ToString();
        }

        private static string Number(double value)
        {
            if (value < 0) return "-1";
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // Only the two strategies VBA can carry out are written into the module.
        // Leaving the others in would read as if it had tried them.
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

        // A VBA string literal ends at the first quote in it, so every quote
        // inside one is doubled. A literal cannot span lines either, so a line
        // break becomes a separator.
        public static string Literal(string value)
        {
            return "\"" + Comment(value).Replace("\"", "\"\"") + "\"";
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
