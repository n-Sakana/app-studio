namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class SessionMdResult
    {
        public string Path;
        public long Bytes;
        public bool Written;
        public string Problem;
        public int ElementRows;
        public int ElementsOmitted;
    }

    // The text half of what goes to an assistant. It carries everything needed
    // to design an automation script and nothing that only a person could use:
    // no pictures, no base64, no styling. What it cannot state, it says it
    // cannot state.
    public static class SessionMd
    {
        private const int MaxElementRows = 2500;

        public static SessionMdResult Write(StudioSession session, string path, ScreensPdfResult pdf)
        {
            SessionMdResult result = new SessionMdResult();
            result.Path = path;
            StringBuilder text = new StringBuilder();
            try
            {
                Head(text, session);
                Environment(text, session);
                PrivacySection(text, session);
                Apps(text, session);
                Screens(text, session, pdf);
                result.ElementRows = Elements(text, session, result);
                Actions(text, session);
                Replay(text, session);
                Coverage(text, session, pdf);
                Guidance(text, session);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                result.Written = true;
                result.Bytes = new FileInfo(path).Length;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
            }
            return result;
        }

        private static void Head(StringBuilder text, StudioSession session)
        {
            text.AppendLine("# App Studio session " + Safe(session.Id));
            text.AppendLine();
            text.AppendLine("This file and `screens.pdf` are the whole handover. There is no other attachment.");
            text.AppendLine();
            text.AppendLine("| field | value |");
            text.AppendLine("|---|---|");
            text.AppendLine("| tool | " + App.Name + " " + App.Version + " |");
            text.AppendLine("| kind | " + (session.Kind == StudioSession.KindRecord ? "recording (a procedure carried out by a person)" : "snap (one window acquired at one moment)") + " |");
            text.AppendLine("| started | " + session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " |");
            text.AppendLine("| ended | " + (session.EndedAt.HasValue ? session.EndedAt.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "not recorded") + " |");
            text.AppendLine("| title | " + Safe(session.Title) + " |");
            text.AppendLine("| screens | " + session.Screens.Screens.Count + " (" + session.Screens.ShotCount + " with a picture) |");
            text.AppendLine("| elements | " + session.Elements.Count + " |");
            text.AppendLine("| actions | " + session.Steps.Count + " |");
            text.AppendLine();
        }

        private static void Environment(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 1. Machine, screens and coordinates");
            text.AppendLine();
            text.AppendLine("Every rectangle and every point in this file is in **physical screen pixels** of the virtual desktop, " +
                "with the origin at the top left of the primary display. They are not scaled by the display scaling factor. " +
                "A script that positions anything by coordinate has to be per-monitor DPI aware or it will land somewhere else.");
            text.AppendLine();
            text.AppendLine("- Process DPI awareness while acquiring: " + Safe(DpiAwareness.State) +
                (String.IsNullOrEmpty(DpiAwareness.Reason) ? "" : " (" + DpiAwareness.Reason + ")"));
            text.AppendLine();
            if (session.Environment == null)
            {
                text.AppendLine("Startup diagnostics were not available for this session, so the machine is not described here.");
                text.AppendLine();
                return;
            }
            text.AppendLine("```json");
            text.AppendLine(JsonWriter.Write(session.Environment).TrimEnd());
            text.AppendLine("```");
            text.AppendLine();
        }

        private static void PrivacySection(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 2. What was and was not written down");
            text.AppendLine();
            text.AppendLine(Privacy.PolicyStatement(session.ValuePolicy));
            text.AppendLine();
            text.AppendLine("- Value policy in force: `" + Safe(session.ValuePolicy) + "`");
            text.AppendLine("- Shortcut keys are recorded only while Ctrl, Alt or the Windows key is held, plus Enter, Tab, Escape and F1-F12 on their own. " +
                "An unmodified letter, digit or punctuation key is never looked at.");
            text.AppendLine("- No clipboard, no window contents of applications that were not in front, no screen recording.");
            text.AppendLine();
            int secrets = 0;
            for (int index = 0; index < session.Steps.Count; index++) if (session.Steps[index].Kind == StepRecord.KindSecretInput) secrets++;
            if (secrets > 0)
            {
                text.AppendLine("There " + (secrets == 1 ? "is 1 step" : "are " + secrets + " steps") +
                    " where a secret was entered. No value exists for " + (secrets == 1 ? "it" : "them") +
                    " anywhere in this handover. Any script built from this has to ask the operator at that point.");
                text.AppendLine();
            }
        }

        private static void Apps(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 3. Applications");
            text.AppendLine();
            if (session.Apps.Count == 0)
            {
                text.AppendLine("No application was recorded for this session.");
                text.AppendLine();
                return;
            }
            text.AppendLine("| key | process | executable | note |");
            text.AppendLine("|---|---|---|---|");
            for (int index = 0; index < session.Apps.Count; index++)
            {
                AppRef app = session.Apps[index];
                text.AppendLine("| " + Safe(app.Key) + " | " + Safe(app.ProcessName) + " | " +
                    Safe(app.ExecutablePath ?? "not readable") + " | " + Safe(app.PathProblem ?? "-") + " |");
            }
            text.AppendLine();
            text.AppendLine("Process ids are from the run that was recorded and mean nothing in a later run. " +
                "Address a window by its class and title, never by a handle or a process id from this file.");
            text.AppendLine();
        }

        private static void Screens(StringBuilder text, StudioSession session, ScreensPdfResult pdf)
        {
            text.AppendLine("## 4. Screens");
            text.AppendLine();
            text.AppendLine("A screen is one top level window as it was at one moment. `screens.pdf` has one page per screen that has a picture, " +
                "and the page names the screen id, so the two files line up.");
            text.AppendLine();
            text.AppendLine("| screen | pdf page | app | window title | class | hwnd at record time | rect (x,y,w,h) | parts | picture |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|");
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = session.Screens.Screens[index];
                string app = AppNameFor(session, screen);
                string picture = screen.HasShot
                    ? (screen.PdfPage > 0 ? "yes" : "taken, left out of the pdf")
                    : "no - " + Safe(screen.ShotProblem ?? "reason not recorded");
                text.AppendLine("| " + Safe(screen.ScreenId) + " | " + (screen.PdfPage > 0 ? screen.PdfPage.ToString(CultureInfo.InvariantCulture) : "-") +
                    " | " + Safe(app) + " | " + Safe(screen.Title) + " | " + Safe(screen.ClassName) + " | 0x" + screen.Hwnd.ToString("X") +
                    " | " + Rect(screen.Rect) + " | " + screen.ComponentIds.Count + " | " + Safe(picture) + " |");
            }
            text.AppendLine();
            if (pdf != null && pdf.OmittedScreens.Count > 0)
            {
                text.AppendLine("Screens left out of `screens.pdf` to stay inside its size budget: " + Join(pdf.OmittedScreens) +
                    ". Their rows above are complete; only the picture is missing from the attachment.");
                text.AppendLine();
            }
        }

        private static int Elements(StringBuilder text, StudioSession session, SessionMdResult result)
        {
            text.AppendLine("## 5. Elements");
            text.AppendLine();
            if (session.Elements.Count == 0)
            {
                text.AppendLine("No element was obtained. If the screens show a working application, it draws its own surface and exposes no structure; " +
                    "in that case the pictures in `screens.pdf` are the only description of it that exists, and any automation has to be built on " +
                    "coordinates or on the application's own scripting interface rather than on the parts listed here.");
                text.AppendLine();
                return 0;
            }
            text.AppendLine("Columns: `sources` says which acquisition layers saw the element (uia = UI Automation, msaa = Microsoft Active Accessibility, " +
                "win32 = window enumeration, hit-test = coordinate sampling). An empty cell means the element did not expose that property, " +
                "not that the property is empty.");
            text.AppendLine();
            text.AppendLine("| id | screen | sources | control type | name | AutomationId | class | ctrlId | rect | state | patterns | path |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            int written = 0;
            for (int index = 0; index < session.Elements.Count; index++)
            {
                if (written >= MaxElementRows) break;
                ScanNode node = session.Elements[index];
                text.AppendLine("| E" + node.NodeId.ToString(CultureInfo.InvariantCulture) + " | " + Safe(node.ScreenId) + " | " +
                    Safe(Join(node.Sources)) + " | " + Safe(node.ControlType ?? node.Role) + " | " + Safe(node.Name) + " | " +
                    Safe(node.AutomationId) + " | " + Safe(node.ClassName) + " | " + (node.CtrlId == 0 ? "-" : node.CtrlId.ToString(CultureInfo.InvariantCulture)) +
                    " | " + Rect(node.Rect) + " | " + Safe(State(node)) + " | " + Safe(Join(node.Patterns)) + " | " + Safe(node.Path) + " |");
                written++;
            }
            text.AppendLine();
            if (session.Elements.Count > written)
            {
                result.ElementsOmitted = session.Elements.Count - written;
                text.AppendLine("**" + result.ElementsOmitted + " further elements are not listed here.** This table stops at " + MaxElementRows +
                    " rows so the file stays readable. The complete list is `elements.jsonl` in the session folder; nothing was discarded, only left out of this table.");
                text.AppendLine();
            }
            return written;
        }

        private static void Actions(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 6. What the operator did");
            text.AppendLine();
            if (session.Steps.Count == 0)
            {
                text.AppendLine("This session is an acquisition of one window, not a recording, so there is no sequence of actions.");
                text.AppendLine();
                return;
            }
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                text.AppendLine("### " + step.StepId + "  " + Safe(step.Headline));
                text.AppendLine();
                text.AppendLine("| field | value |");
                text.AppendLine("|---|---|");
                text.AppendLine("| at | +" + (step.OffsetMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s  (" +
                    step.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + ") |");
                text.AppendLine("| kind | " + Safe(step.Kind) + " |");
                text.AppendLine("| application | " + Safe(step.AppName) + " |");
                text.AppendLine("| window | " + Safe(step.WindowTitle) + "  (class " + Safe(step.WindowClass) + ") |");
                text.AppendLine("| screen before / after | " + Safe(step.ScreenBefore ?? "-") + " / " + Safe(step.ScreenAfter ?? "-") + " |");
                if (step.Kind == StepRecord.KindKeyChord)
                {
                    text.AppendLine("| key | `" + Safe(step.KeyChord) + "` |");
                }
                else if (step.Kind != StepRecord.KindAppSwitch)
                {
                    text.AppendLine("| element | " + Safe(step.ElementLabel) + " |");
                    text.AppendLine("| control type | " + Safe(step.ControlType ?? step.Role) + " |");
                    text.AppendLine("| name | " + Safe(step.Name) + " |");
                    text.AppendLine("| AutomationId | " + Safe(step.AutomationId) + " |");
                    text.AppendLine("| class / ctrlId | " + Safe(step.ClassName) + " / " + (step.CtrlId == 0 ? "-" : step.CtrlId.ToString(CultureInfo.InvariantCulture)) + " |");
                    text.AppendLine("| rect | " + Rect(step.Rect) + " |");
                    text.AppendLine("| sources | " + Safe(Join(step.Sources)) + " |");
                    text.AppendLine("| identification confidence | " + Safe(step.Confidence) + " |");
                }
                if (step.ValueKind != "none")
                {
                    string valueText;
                    if (step.ValueKind == "secret") valueText = "**not recorded** (secret field, rule `" + Safe(step.MaskRule) + "`) - a script must ask the operator here";
                    else if (step.Value != null) valueText = "`" + Safe(step.Value) + "`  (" + Privacy.DescribeLength(step.ValueLength) + ")";
                    else valueText = "not recorded, " + Privacy.DescribeLength(step.ValueLength) + " were entered";
                    text.AppendLine("| value entered | " + valueText + " |");
                }
                text.AppendLine("| observed effect | " + Safe(step.EffectSummary) + " |");
                text.AppendLine();
                if (step.Locators.Count > 0)
                {
                    text.AppendLine("Locators, strongest first:");
                    text.AppendLine();
                    for (int locator = 0; locator < step.Locators.Count; locator++)
                    {
                        ElementLocator item = step.Locators[locator];
                        text.AppendLine("- `" + Safe(item.Display) + "` - confidence " + Safe(item.Confidence) +
                            (item.Reasons.Count > 0 ? " - " + Safe(item.Reasons[0]) : ""));
                    }
                    text.AppendLine();
                }
                if (step.Unavailable.Count > 0)
                {
                    text.AppendLine("Could not be obtained for this step:");
                    text.AppendLine();
                    for (int item = 0; item < step.Unavailable.Count; item++) text.AppendLine("- " + Safe(step.Unavailable[item]));
                    text.AppendLine();
                }
                if (step.Diagnostics.Count > 0)
                {
                    text.AppendLine("Diagnostics: " + Safe(Join(step.Diagnostics)));
                    text.AppendLine();
                }
            }
        }

        private static void Replay(StringBuilder text, StudioSession session)
        {
            bool any = false;
            for (int index = 0; index < session.Steps.Count; index++) if (session.Steps[index].LastReplay != null) any = true;
            text.AppendLine("## 7. Replay results");
            text.AppendLine();
            if (!any)
            {
                text.AppendLine("This recording has not been replayed, so there is no evidence here about which route each application answers.");
                text.AppendLine();
                return;
            }
            text.AppendLine("Each row is the last replay of that step. `routes tried` is in order; a route that reported `notSupported` never acted, " +
                "so the next one was tried. Only one route ever carries a state changing operation out.");
            text.AppendLine();
            text.AppendLine("| step | result | resolved by | routes tried | reason |");
            text.AppendLine("|---|---|---|---|---|");
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.LastReplay == null) continue;
                text.AppendLine("| " + Safe(step.StepId) + " | " + Safe(step.LastReplay.State) + " | " + Safe(step.LastReplay.ResolvedBy ?? "-") +
                    " | `" + Safe(step.LastReplay.AttemptLine) + "` | " + Safe(step.LastReplay.Reason) + " |");
            }
            text.AppendLine();
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.LastReplay == null || step.LastReplay.Attempts.Count == 0) continue;
                text.AppendLine("**" + step.StepId + "** route detail:");
                text.AppendLine();
                text.AppendLine("| route | method | outcome | ms | error | observed effect |");
                text.AppendLine("|---|---|---|---|---|---|");
                for (int attempt = 0; attempt < step.LastReplay.Attempts.Count; attempt++)
                {
                    RouteAttempt item = step.LastReplay.Attempts[attempt];
                    text.AppendLine("| " + Safe(item.Route) + " | " + Safe(item.Method) + " | " + Safe(item.Outcome) + " | " +
                        item.DurationMs.ToString(CultureInfo.InvariantCulture) + " | " + Safe(Error(item)) + " | " + Safe(item.Effect ?? "-") + " |");
                }
                text.AppendLine();
            }
        }

        private static void Coverage(StringBuilder text, StudioSession session, ScreensPdfResult pdf)
        {
            text.AppendLine("## 8. Coverage and limits");
            text.AppendLine();
            if (session.Coverage.Count > 0)
            {
                text.AppendLine("| layer | state | elements | ms | truncated |");
                text.AppendLine("|---|---|---|---|---|");
                Dictionary<string, ScanCoverage> totals = new Dictionary<string, ScanCoverage>(StringComparer.Ordinal);
                for (int index = 0; index < session.Coverage.Count; index++)
                {
                    ScanCoverage coverage = session.Coverage[index];
                    string key = coverage.Provider ?? "?";
                    ScanCoverage total;
                    if (!totals.TryGetValue(key, out total))
                    {
                        total = new ScanCoverage();
                        total.Provider = key;
                        total.State = coverage.State;
                        totals[key] = total;
                    }
                    total.NodeCount += coverage.NodeCount;
                    total.DurationMs += coverage.DurationMs;
                    if (coverage.Truncated) total.Truncated = true;
                    if (coverage.State == "unavailable" || coverage.State == "partial") total.State = coverage.State;
                }
                foreach (KeyValuePair<string, ScanCoverage> pair in totals)
                {
                    text.AppendLine("| " + Safe(pair.Key) + " | " + Safe(pair.Value.State) + " | " + pair.Value.NodeCount + " | " +
                        pair.Value.DurationMs + " | " + (pair.Value.Truncated ? "yes" : "no") + " |");
                }
                text.AppendLine();
            }
            text.AppendLine("What could not be obtained:");
            text.AppendLine();
            if (session.Limits.Count == 0)
            {
                text.AppendLine("- Nothing was recorded as unobtainable.");
            }
            else
            {
                for (int index = 0; index < session.Limits.Count; index++) text.AppendLine("- " + Safe(session.Limits[index]));
            }
            text.AppendLine();
            // Said whether or not anything was listed above. A short list of
            // limits is no more a proof of completeness than an empty one.
            text.AppendLine("This list is what the layers reported while they ran. It is **not a proof of completeness**: " +
                "an area an application draws itself publishes nothing to report, so it can be missing from every table here " +
                "without any layer having noticed.");
            text.AppendLine();
            if (pdf != null)
            {
                text.AppendLine("Picture attachment: " + (pdf.Written ? pdf.PageCount + " page(s), " + pdf.SizeText + ", budget " +
                    (pdf.BudgetBytes / 1024) + " KB, stored " + Safe(pdf.Quality) : "not written - " + Safe(pdf.Problem)));
                text.AppendLine();
                for (int index = 0; index < pdf.Notes.Count; index++) text.AppendLine("- " + Safe(pdf.Notes[index]));
                if (pdf.Notes.Count > 0) text.AppendLine();
            }
            if (session.Diagnostics.Count > 0)
            {
                text.AppendLine("Tool diagnostics during this session:");
                text.AppendLine();
                for (int index = 0; index < session.Diagnostics.Count && index < 60; index++) text.AppendLine("- " + Safe(session.Diagnostics[index]));
                if (session.Diagnostics.Count > 60) text.AppendLine("- ... and " + (session.Diagnostics.Count - 60) + " more, in the session folder");
                text.AppendLine();
            }
        }

        private static void Guidance(StringBuilder text, StudioSession session)
        {
            text.AppendLine("## 9. Writing an automation from this");
            text.AppendLine();
            text.AppendLine("- Address elements in the order the locators are listed: AutomationId first, then name plus control type, " +
                "then the hierarchy path, then the Win32 control id, then the class and index. The last one moves as soon as the application changes, " +
                "and a position inside the window is a description, never an address.");
            text.AppendLine("- Window handles and process ids in this file are from the recorded run and are meaningless later. " +
                "Find a window by its class and title.");
            text.AppendLine("- Rectangles are physical pixels. Do not scale them by the display scaling factor.");
            text.AppendLine("- Where the replay table shows which route worked, prefer that route: `uia` means a UI Automation pattern, " +
                "`win32` means a window message, `sendInput` means synthetic input, which is the least reliable and needs the window in front.");
            text.AppendLine("- Where a step is marked as a secret, the script has to prompt. There is no value to recover.");
            if (Acquire.PublishesNoStructure(session))
            {
                text.AppendLine("- **This application publishes no structure.** Every screen came back with nothing inside it, so the element table describes windows, " +
                    "not the controls a person sees in them. The pictures in `screens.pdf` are the only description of that surface that exists here. " +
                    "Anything built on coordinates alone will break when the window moves or the layout changes; say so rather than presenting it as reliable.");
            }
            text.AppendLine();
        }

        private static string AppNameFor(StudioSession session, ScreenRecord screen)
        {
            for (int index = 0; index < session.Elements.Count; index++)
            {
                if (!String.Equals(session.Elements[index].ScreenId, screen.ScreenId, StringComparison.Ordinal)) continue;
                AppRef app = session.AppFor(session.Elements[index].ProcessId);
                if (app != null) return app.Display;
            }
            for (int index = 0; index < session.Steps.Count; index++)
            {
                if (String.Equals(session.Steps[index].ScreenBefore, screen.ScreenId, StringComparison.Ordinal) ||
                    String.Equals(session.Steps[index].ScreenAfter, screen.ScreenId, StringComparison.Ordinal)) return session.Steps[index].AppName;
            }
            return "-";
        }

        private static string State(ScanNode node)
        {
            List<string> parts = new List<string>();
            if (node.Visible.HasValue) parts.Add(node.Visible.Value ? "visible" : "hidden");
            if (node.Enabled.HasValue) parts.Add(node.Enabled.Value ? "enabled" : "disabled");
            if (node.Offscreen.HasValue && node.Offscreen.Value) parts.Add("offscreen");
            if (node.KeyboardFocusable.HasValue && node.KeyboardFocusable.Value) parts.Add("focusable");
            if (node.IsPassword.HasValue && node.IsPassword.Value) parts.Add("password");
            if (node.Decoration) parts.Add("frame part");
            return Join(parts);
        }

        private static string Error(RouteAttempt attempt)
        {
            if (String.IsNullOrEmpty(attempt.ErrorCode) && String.IsNullOrEmpty(attempt.ErrorMessage)) return "-";
            return (attempt.ErrorCode ?? "") + " " + (attempt.ErrorMessage ?? "");
        }

        private static string Rect(RectValue rect)
        {
            if (rect == null) return "-";
            return rect.X + "," + rect.Y + "," + rect.Width + "," + rect.Height;
        }

        private static string Join(List<string> values)
        {
            if (values == null || values.Count == 0) return "-";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < values.Count; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(values[index]);
            }
            return text.ToString();
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

        // Table cells are separated by a bar, so a bar inside a value would
        // split the row. Newlines would end it. Both are neutralised without
        // dropping any of the text.
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
