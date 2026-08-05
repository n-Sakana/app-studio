namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class HandoffResult
    {
        public string RequestId;
        public string Text;
        public List<string> Chunks = new List<string>();
        public string Path;
        public string Problem;

        public bool Split { get { return Chunks.Count > 1; } }
    }

    // The one text that goes to an assistant, and the one shape an answer may
    // come back in.
    //
    // It is one text on purpose. Everything needed to change the code is in it:
    // what is being asked, the vocabulary the code is written in, the machine,
    // what the operator did, the windows and elements, what could not be
    // obtained, and the code as it stands. The pictures are the second
    // attachment and are the only thing that is not in here.
    //
    // When it is too long to paste in one go it is cut on line boundaries into
    // numbered parts. Nothing is dropped to make it fit.
    public static class Handoff
    {
        public const string Marker = "#@APPSTUDIO";
        // Chat clients differ in what they accept in one paste. This is set so
        // that an ordinary session - both generated files in full, the ledger
        // and the action log - is one copy, because splitting is a cost the
        // operator pays and most sessions should never pay it. A session with a
        // large ledger or code that has grown still splits rather than being
        // trimmed to fit.
        public const int ChunkChars = 60000;

        public static string NewRequestId()
        {
            return Guid.NewGuid().ToString("D");
        }

        public static bool IsRequestId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 36) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (index == 8 || index == 13 || index == 18 || index == 23)
                {
                    if (character != '-') return false;
                    continue;
                }
                bool digit = character >= '0' && character <= '9';
                bool lower = character >= 'a' && character <= 'f';
                bool upper = character >= 'A' && character <= 'F';
                if (!digit && !lower && !upper) return false;
            }
            return true;
        }

        public static HandoffResult Build(StudioSession session, CodeProject project, string requestText, string requestId)
        {
            HandoffResult result = new HandoffResult();
            result.RequestId = IsRequestId(requestId) ? requestId : NewRequestId();
            StringBuilder text = new StringBuilder();
            Ask(text, result.RequestId, requestText);
            ReturnFormat(text, result.RequestId);
            Vocabulary(text);
            Machine(text, session);
            Actions(text, session);
            Ledger(text, session);
            Limits(text, session, project);
            Code(text, project);
            result.Text = text.ToString();
            Chunk(result);
            return result;
        }

        // 1. What is being asked.
        private static void Ask(StringBuilder text, string requestId, string requestText)
        {
            text.AppendLine("# " + App.Name + " " + App.Version + " - request to an assistant");
            text.AppendLine();
            text.AppendLine("Request id: `" + requestId + "`");
            text.AppendLine();
            text.AppendLine("## 1. What is being asked");
            text.AppendLine();
            string ask = requestText == null ? "" : requestText.Trim();
            text.AppendLine(ask.Length == 0
                ? "Improve the automation below so it carries out the recorded procedure reliably. Keep every safety rule stated in section 3."
                : ask);
            text.AppendLine();
        }

        // 2. The only shape an answer may come back in.
        private static void ReturnFormat(StringBuilder text, string requestId)
        {
            text.AppendLine("## 2. How to answer");
            text.AppendLine();
            text.AppendLine("Answer with one code block. Every file in it is wrapped in the lines below, which carry the");
            text.AppendLine("request id this request was issued with, so an answer to a different request can never be");
            text.AppendLine("applied by accident. These lines are protocol, not code: they are removed before anything is");
            text.AppendLine("compiled or run, so do not put them inside a function.");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " SUMMARY BEGIN");
            text.AppendLine("what was changed, in plain words");
            text.AppendLine(Marker + " " + requestId + " SUMMARY END");
            text.AppendLine(Marker + " " + requestId + " BEGIN powershell " + CodeProject.GeneratedName);
            text.AppendLine("...the whole file...");
            text.AppendLine(Marker + " " + requestId + " END powershell " + CodeProject.GeneratedName);
            text.AppendLine(Marker + " " + requestId + " COMPLETE 1");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("Rules:");
            text.AppendLine();
            text.AppendLine("- The language is `powershell` or `vba`. Both are first class here; do not answer in one and");
            text.AppendLine("  describe the other in prose.");
            text.AppendLine("- Return the **whole** file, not a patch and not an excerpt. A file that is cut off is refused.");
            text.AppendLine("- `COMPLETE` carries the number of files in the answer and has to agree with them.");
            text.AppendLine("- A name may be letters, digits and underscores, and has to start with a letter.");
            text.AppendLine("- The same file may appear only once.");
            text.AppendLine("- Several files in one answer are fine: repeat BEGIN..END for each and count them all in COMPLETE.");
            text.AppendLine();
            text.AppendLine("If the answer is too long for one message, send one file per message and put this line above it:");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " PART 00 OF 03");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("Numbering starts at 00 and every part carries the same total.");
            text.AppendLine();
            text.AppendLine("There are exactly two kinds of answer. The other one is a refusal, which is a result rather");
            text.AppendLine("than a failure, so it says which refusal it is and why. It never asks anything back: a request");
            text.AppendLine("that cannot be settled from what is here comes back as UNCLEAR with the reason.");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " SUMMARY BEGIN");
            text.AppendLine("why, and what was looked at");
            text.AppendLine(Marker + " " + requestId + " SUMMARY END");
            text.AppendLine(Marker + " " + requestId + " NOCHANGE UNNECESSARY");
            text.AppendLine(Marker + " " + requestId + " COMPLETE 0");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("`UNNECESSARY` the code already does what was asked. `IMPOSSIBLE` it could be done, but not by");
            text.AppendLine("changing these files. `UNCLEAR` it cannot be settled from what was given, and the summary says");
            text.AppendLine("what is missing. All four lines are required in a refusal too.");
            text.AppendLine();
        }

        // 3. The vocabulary. Both languages carry the same nine names, so an
        // answer that changes one of them has to mean it.
        private static void Vocabulary(StringBuilder text)
        {
            text.AppendLine("## 3. The operation vocabulary, shared by PowerShell and VBA");
            text.AppendLine();
            text.AppendLine("Both files define the same nine operations. They are the whole surface an automation built");
            text.AppendLine("from this recording is expected to use. Keep the names and the meanings; add helpers around");
            text.AppendLine("them if you need to.");
            text.AppendLine();
            text.AppendLine("| operation | meaning |");
            text.AppendLine("|---|---|");
            text.AppendLine("| `FindWindow` | wait until exactly one window matches the recorded class and title, and bring it to the front. More than one match stops the run. |");
            text.AppendLine("| `FocusElement` | put the keyboard on the element the recording says held it. |");
            text.AppendLine("| `InvokeElement` | press the element. A pattern it publishes is preferred; synthetic input is the fallback and needs the window in front. |");
            text.AppendLine("| `SetElementText` | write text into the element. Refused on a password field. |");
            text.AppendLine("| `ReadElementText` | read the element back, to check an effect. |");
            text.AppendLine("| `SendKeys` | send one recorded chord, after the keyboard has been put back. |");
            text.AppendLine("| `WaitGap` | wait the interval the operator left, clamped to 120 ms - 4000 ms. |");
            text.AppendLine("| `WaitIdle` | wait for the front window to stop changing, up to a stated ceiling. |");
            text.AppendLine("| `AskSecret` | a value the recording deliberately never kept. Ask the operator; never write it anywhere. |");
            text.AppendLine();
            text.AppendLine("Rules that may not be traded away for convenience:");
            text.AppendLine();
            text.AppendLine("- **Never press a remembered screen coordinate.** An element is found again by its locators, in");
            text.AppendLine("  the order they are listed. A place inside an element is a fraction of that element's rectangle");
            text.AppendLine("  as it is now, never a stored point on the desktop.");
            text.AppendLine("- A locator that matches more than one element decides nothing. Try the next one; when they are");
            text.AppendLine("  all spent, stop and say so.");
            text.AppendLine("- Window handles and process ids in this text are from the recorded run and mean nothing later.");
            text.AppendLine("- A secret is asked for at the moment it is needed and is never written to a file or a log.");
            text.AppendLine("- Do not swallow a failure. Stopping with a reason is a result; carrying on regardless is not.");
            text.AppendLine();
            text.AppendLine("VBA reaches controls through Win32 only: a class name with a dialog control id, or a class name");
            text.AppendLine("with its index. Where the recording has no such address the VBA file says so at that point. Do");
            text.AppendLine("not replace those with coordinates.");
            text.AppendLine();
        }

        // 4. The machine and the coordinate system.
        private static void Machine(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 4. The machine, the screens and the coordinate system");
            text.AppendLine();
            text.AppendLine("Every rectangle and every point here is in **physical screen pixels** of the virtual desktop,");
            text.AppendLine("with the origin at the top left of the primary display. They are not scaled by the display");
            text.AppendLine("scaling factor.");
            text.AppendLine();
            if (session == null)
            {
                text.AppendLine("No session was available, so the machine is not described here.");
                text.AppendLine();
                return;
            }
            text.AppendLine("- Process DPI awareness while acquiring: " + Safe(DpiAwareness.State));
            text.AppendLine("- Value policy in force while recording: `" + Safe(session.ValuePolicy) + "`");
            text.AppendLine("- Pointer watch: " + Safe(session.InputWatchState));
            text.AppendLine();
            if (session.Environment != null)
            {
                text.AppendLine("```json");
                text.AppendLine(JsonWriter.Write(session.Environment).TrimEnd());
                text.AppendLine("```");
                text.AppendLine();
            }
            if (session.Apps.Count > 0)
            {
                text.AppendLine("| key | process | executable |");
                text.AppendLine("|---|---|---|");
                for (int index = 0; index < session.Apps.Count; index++)
                {
                    AppRef app = session.Apps[index];
                    text.AppendLine("| " + Safe(app.Key) + " | " + Safe(app.ProcessName) + " | " + Safe(app.ExecutablePath ?? "not readable") + " |");
                }
                text.AppendLine();
            }
        }

        // 5. What the operator did, with the timing and the focus that replay
        // honours.
        private static void Actions(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 5. What the operator did");
            text.AppendLine();
            if (session == null || session.Steps.Count == 0)
            {
                text.AppendLine("This session is an acquisition of one window, not a recording, so there is no sequence of actions.");
                text.AppendLine();
                return;
            }
            text.AppendLine("`gap` is how long the operator waited before the action; the generated code waits the same,");
            text.AppendLine("clamped. `focus` is what held the keyboard at that moment.");
            text.AppendLine();
            text.AppendLine("| step | action | window | element | gap ms | focus | value | confidence |");
            text.AppendLine("|---|---|---|---|---|---|---|---|");
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                string value = "-";
                if (step.ValueKind == "secret") value = "not recorded (secret)";
                else if (step.Value != null) value = "`" + Safe(step.Value) + "`";
                else if (step.ValueLength >= 0) value = Privacy.DescribeLength(step.ValueLength);
                text.AppendLine("| " + Safe(step.StepId) + " | " + Safe(step.Headline) + " | " + Safe(step.WindowTitle) +
                    " (" + Safe(step.WindowClass) + ") | " + Safe(step.ElementLabel) + " | " +
                    step.GapMs.ToString(CultureInfo.InvariantCulture) + " | " + Safe(step.FocusLabel) + " | " +
                    value + " | " + Safe(step.Confidence) + " |");
            }
            text.AppendLine();
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.Locators.Count == 0 && step.Unavailable.Count == 0) continue;
                text.AppendLine("**" + Safe(step.StepId) + "** " + Safe(step.Headline));
                text.AppendLine();
                for (int locator = 0; locator < step.Locators.Count; locator++)
                {
                    text.AppendLine("- `" + Safe(step.Locators[locator].Display) + "` - confidence " + Safe(step.Locators[locator].Confidence));
                }
                for (int locator = 0; locator < step.DropLocators.Count; locator++)
                {
                    text.AppendLine("- released at `" + Safe(step.DropLocators[locator].Display) + "`");
                }
                for (int item = 0; item < step.Unavailable.Count; item++)
                {
                    text.AppendLine("- could not be obtained: " + Safe(step.Unavailable[item]));
                }
                text.AppendLine();
            }
        }

        // 6. The windows and elements the addresses are drawn from.
        private static void Ledger(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 6. Windows and elements");
            text.AppendLine();
            if (session == null || session.Screens.Screens.Count == 0)
            {
                text.AppendLine("No window was acquired for this session.");
                text.AppendLine();
                return;
            }
            text.AppendLine("`screens.pdf` has one page per screen that has a picture, and the page names the screen id.");
            text.AppendLine();
            text.AppendLine("| screen | pdf page | window title | class | rect | parts |");
            text.AppendLine("|---|---|---|---|---|---|");
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = session.Screens.Screens[index];
                text.AppendLine("| " + Safe(screen.ScreenId) + " | " + (screen.PdfPage > 0 ? screen.PdfPage.ToString(CultureInfo.InvariantCulture) : "-") +
                    " | " + Safe(screen.Title) + " | " + Safe(screen.ClassName) + " | " + Rect(screen.Rect) + " | " +
                    screen.ComponentIds.Count.ToString(CultureInfo.InvariantCulture) + " |");
            }
            text.AppendLine();
            if (session.Elements.Count == 0)
            {
                text.AppendLine("No element was obtained. The application draws its own surface and publishes no structure,");
                text.AppendLine("so the pictures are the only description of it that exists.");
                text.AppendLine();
                return;
            }
            text.AppendLine("| id | screen | control type | name | AutomationId | class | ctrlId | rect | patterns |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|");
            int written = 0;
            for (int index = 0; index < session.Elements.Count && written < 1200; index++)
            {
                ScanNode node = session.Elements[index];
                if (node.Decoration) continue;
                text.AppendLine("| E" + node.NodeId.ToString(CultureInfo.InvariantCulture) + " | " + Safe(node.ScreenId) + " | " +
                    Safe(node.ControlType ?? node.Role) + " | " + Safe(node.Name) + " | " + Safe(node.AutomationId) + " | " +
                    Safe(node.ClassName) + " | " + (node.CtrlId == 0 ? "-" : node.CtrlId.ToString(CultureInfo.InvariantCulture)) +
                    " | " + Rect(node.Rect) + " | " + Safe(Join(node.Patterns)) + " |");
                written++;
            }
            text.AppendLine();
            if (session.Elements.Count > written)
            {
                text.AppendLine("**" + (session.Elements.Count - written).ToString(CultureInfo.InvariantCulture) +
                    " further elements are not listed here.** The table stops so this text stays pasteable; nothing was");
                text.AppendLine("discarded, only left out of the table.");
                text.AppendLine();
            }
        }

        // 7. What could not be obtained. An empty list is not a proof of
        // completeness and says so.
        private static void Limits(StringBuilder text, StudioSession session, CodeProject project)
        {
            text.AppendLine("## 7. What could not be obtained");
            text.AppendLine();
            if (session != null && session.Limits.Count > 0)
            {
                for (int index = 0; index < session.Limits.Count; index++) text.AppendLine("- " + Safe(session.Limits[index]));
            }
            else
            {
                text.AppendLine("- Nothing was recorded as unobtainable.");
            }
            text.AppendLine();
            if (project != null && project.Plan != null)
            {
                for (int index = 0; index < project.Plan.Notes.Count; index++)
                {
                    text.AppendLine("- " + Safe(project.Plan.Notes[index]));
                }
                if (project.Plan.Notes.Count > 0) text.AppendLine();
            }
            text.AppendLine("This list is what the layers reported while they ran. It is **not a proof of completeness**:");
            text.AppendLine("an area an application draws itself publishes nothing to report, so it can be missing from");
            text.AppendLine("every table here without any layer having noticed.");
            text.AppendLine();
        }

        // 8. The code as it stands. Both languages, whole.
        private static void Code(StringBuilder text, CodeProject project)
        {
            text.AppendLine("## 8. The code as it stands");
            text.AppendLine();
            if (project == null)
            {
                text.AppendLine("No code has been generated for this session yet.");
                text.AppendLine();
                return;
            }
            List<CodeFile> files = project.All();
            if (files.Count == 0)
            {
                text.AppendLine("No code has been generated for this session yet.");
                text.AppendLine();
                return;
            }
            for (int index = 0; index < files.Count; index++)
            {
                CodeFile file = files[index];
                text.AppendLine("### " + file.Language + " / " + file.FileName + " (" + CodeProject.LineCount(file.Text).ToString(CultureInfo.InvariantCulture) + " lines)");
                text.AppendLine();
                text.AppendLine("```" + (String.Equals(file.Language, ScriptLanguages.Vba, StringComparison.Ordinal) ? "vb" : "powershell"));
                text.AppendLine(file.Text.TrimEnd());
                text.AppendLine("```");
                text.AppendLine();
            }
        }

        // Cuts the text on line boundaries when it is too long to paste in one
        // go. Every part says which it is, so the operator copies them in order
        // and the assistant knows to wait for the last one.
        private static void Chunk(HandoffResult result)
        {
            result.Chunks.Clear();
            if (result.Text.Length <= ChunkChars)
            {
                result.Chunks.Add(Marker + " " + result.RequestId + " REQUEST 01 OF 01" + Environment.NewLine + result.Text);
                return;
            }
            List<string> bodies = new List<string>();
            StringBuilder current = new StringBuilder();
            string[] lines = result.Text.Replace("\r\n", "\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (current.Length > 0 && current.Length + lines[index].Length + 1 > ChunkChars)
                {
                    bodies.Add(current.ToString());
                    current.Length = 0;
                }
                current.Append(lines[index]).Append(Environment.NewLine);
            }
            if (current.Length > 0) bodies.Add(current.ToString());
            int total = bodies.Count;
            for (int index = 0; index < total; index++)
            {
                StringBuilder part = new StringBuilder();
                part.Append(Marker).Append(' ').Append(result.RequestId).Append(" REQUEST ")
                    .Append(Two(index + 1)).Append(" OF ").Append(Two(total)).Append(Environment.NewLine);
                if (index == 0)
                {
                    part.Append("This request is being sent in ").Append(total.ToString(CultureInfo.InvariantCulture))
                        .Append(" parts because it is too long for one message. Read them all before answering, and answer only after the last one.")
                        .Append(Environment.NewLine).Append(Environment.NewLine);
                }
                else
                {
                    part.Append("Continued. Part ").Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(" of ")
                        .Append(total.ToString(CultureInfo.InvariantCulture)).Append(". Do not answer yet.")
                        .Append(Environment.NewLine).Append(Environment.NewLine);
                }
                part.Append(bodies[index]);
                if (index == total - 1)
                {
                    part.Append(Environment.NewLine).Append("That is the whole request. Answer now, in the shape section 2 describes.")
                        .Append(Environment.NewLine);
                }
                result.Chunks.Add(part.ToString());
            }
        }

        private static string Two(int value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            return text.Length >= 2 ? text : "0" + text;
        }

        // Writes the request beside the code it is about. It is deliberately not
        // put in the assistant folder: that folder is exactly two files and
        // saying "attach what is in ai/" has to stay a complete instruction.
        public static string Write(CodeProject project, HandoffResult result)
        {
            if (project == null || project.Folder == null) return "This session has no folder on disk.";
            try
            {
                Directory.CreateDirectory(project.Folder);
                string path = Path.Combine(project.Folder, "request.md");
                File.WriteAllText(path, result.Text, new UTF8Encoding(false));
                result.Path = path;
                return null;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result.Problem;
            }
        }

        private static string Rect(RectValue rect)
        {
            if (rect == null) return "-";
            return rect.X + "," + rect.Y + "," + rect.Width + "," + rect.Height;
        }

        private static string Join(string[] values)
        {
            if (values == null || values.Length == 0) return "-";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < values.Length; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(values[index]);
            }
            return text.ToString();
        }

        // A bar inside a value would split a table row and a newline would end
        // it. Both are neutralised without dropping any of the text.
        private static string Safe(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '|') text.Append("\\|");
                else if (character == '\r') continue;
                else if (character == '\n') text.Append(" / ");
                else text.Append(character);
            }
            return text.ToString();
        }
    }
}
