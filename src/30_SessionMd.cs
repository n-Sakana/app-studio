namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One part of the file that was actually written, with the number it was
    // given this time.
    //
    // Nothing refers to a part by a fixed number any more. Parts are left out
    // whenever the operator leaves them out, so "section 10" was a name that
    // meant a different thing every time and, once anything above it was
    // dropped, meant the wrong thing. What is stable is the id; the number is
    // handed out in order over whatever is present, and everything that points
    // at a part reads its number and its title from here.
    public sealed class SessionMdSection
    {
        public string Id;
        public int Number;
        public string Title;
    }

    public sealed class SessionMdResult
    {
        public string Path;
        public long Bytes;
        public bool Written;
        public string Problem;
        public int ElementRows;
        public int ElementsOmitted;
        // What was actually written, in the order it was written. This is what
        // the window shows and what the request text is built from, so neither
        // can claim a part that is not in the file.
        public List<SessionMdSection> Sections = new List<SessionMdSection>();
        public int EngineModules;
        public int VbaModules;
        public bool WrapperIncluded;
        // Every ceiling that actually bit, in words. A limit that was applied
        // silently is a file that is smaller than it looks.
        public List<string> LimitsApplied = new List<string>();

        public bool Includes(string id)
        {
            for (int index = 0; index < Sections.Count; index++)
            {
                if (String.Equals(Sections[index].Id, id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public SessionMdSection Section(string id)
        {
            for (int index = 0; index < Sections.Count; index++)
            {
                if (String.Equals(Sections[index].Id, id, StringComparison.Ordinal)) return Sections[index];
            }
            return null;
        }

        // How to point at a part in prose: the number it got this time and the
        // words at the top of it, so a reader can find it either way.
        public string Reference(string id)
        {
            SessionMdSection section = Section(id);
            if (section == null) return null;
            return "section " + section.Number.ToString(CultureInfo.InvariantCulture) + ", \"" + section.Title + "\"";
        }
    }

    // The text half of what goes to an assistant. It carries what the operator
    // chose to hand over and nothing else: no pictures, no base64, no styling.
    // What it cannot state, it says it cannot state - and what was deliberately
    // left out it says was left out, which is a different sentence.
    public static class SessionMd
    {
        private const int MaxElementRows = 2500;
        private const int MaxDiagnostics = 60;

        public static SessionMdResult Write(StudioSession session, string path, ScreensPdfResult pdf)
        {
            return Write(session, path, pdf, null, null);
        }

        public static SessionMdResult Write(StudioSession session, string path, ScreensPdfResult pdf, CodeProject project)
        {
            return Write(session, path, pdf, project, null);
        }

        // The automation goes in here rather than into the request text.
        //
        // The request the operator pastes into a chat says what is wanted and how
        // to answer; everything an assistant has to read is attached, and this is
        // the file it is attached in.
        //
        // Which parts are in it is the operator's decision, one part at a time.
        // Nothing here infers a part from another part, and no part is refused
        // because of what it was combined with. A selection with nothing in it
        // writes no file at all, which is the honest result and is what the
        // caller is told.
        public static SessionMdResult Write(StudioSession session, string path, ScreensPdfResult pdf, CodeProject project, AiPicks picks)
        {
            if (picks == null) picks = AiPicks.Default();
            SessionMdResult result = new SessionMdResult();
            result.Path = path;

            // The running order is settled before a word is written, because the
            // heading numbers and the contents list both need to know what is
            // going to be there.
            List<string> included = new List<string>();
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++)
            {
                string id = order[index];
                string kind = AiItems.KindOf(id);
                if (!String.Equals(kind, AiKinds.Section, StringComparison.Ordinal) &&
                    !String.Equals(kind, AiKinds.Code, StringComparison.Ordinal)) continue;
                if (!picks.Has(id)) continue;
                included.Add(id);
            }
            for (int index = 0; index < included.Count; index++)
            {
                SessionMdSection section = new SessionMdSection();
                section.Id = included[index];
                section.Number = index + 1;
                section.Title = AiItems.Title(included[index]);
                result.Sections.Add(section);
            }
            if (included.Count == 0)
            {
                result.Problem = "SESSIONMD-NOTHING: nothing was selected for this file, so it was not written.";
                return result;
            }

            StringBuilder text = new StringBuilder();
            try
            {
                Head(text, session, picks, result);
                Contents(text, result);
                for (int index = 0; index < included.Count; index++)
                {
                    string id = included[index];
                    int number = index + 1;
                    if (String.Equals(id, AiItems.Environment, StringComparison.Ordinal)) Environment(text, session, number);
                    else if (String.Equals(id, AiItems.Privacy, StringComparison.Ordinal)) PrivacySection(text, session, number);
                    else if (String.Equals(id, AiItems.Apps, StringComparison.Ordinal)) Apps(text, session, number);
                    else if (String.Equals(id, AiItems.Screens, StringComparison.Ordinal)) Screens(text, session, pdf, picks, number);
                    else if (String.Equals(id, AiItems.Elements, StringComparison.Ordinal)) result.ElementRows = Elements(text, session, result, number);
                    else if (String.Equals(id, AiItems.Actions, StringComparison.Ordinal)) Actions(text, session, number);
                    else if (String.Equals(id, AiItems.Replay, StringComparison.Ordinal)) Replay(text, session, number);
                    else if (String.Equals(id, AiItems.Coverage, StringComparison.Ordinal)) Coverage(text, session, pdf, picks, result, number);
                    else if (String.Equals(id, AiItems.Guidance, StringComparison.Ordinal)) Guidance(text, session, picks, number);
                    else if (String.Equals(id, AiItems.Engine, StringComparison.Ordinal)) result.EngineModules = Code(text, project, ScriptLanguages.PowerShell, number, result);
                    else if (String.Equals(id, AiItems.Vba, StringComparison.Ordinal)) result.VbaModules = Code(text, project, ScriptLanguages.Vba, number, result);
                    else if (String.Equals(id, AiItems.Wrapper, StringComparison.Ordinal)) result.WrapperIncluded = Wrapper(text, project, number);
                }
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

        private static void Head(StringBuilder text, StudioSession session, AiPicks picks, SessionMdResult result)
        {
            SessionVerdict verdict = SessionVerdict.Of(session);
            text.AppendLine("# App Studio session " + Safe(session.Id));
            text.AppendLine();
            // The same conclusion the window and the report show, in the same
            // words. Three files that describe one session must not give three
            // different answers to "did this work".
            text.AppendLine("**" + Safe(verdict.StateWord) + "** - " + Safe(verdict.Headline));
            text.AppendLine();
            if (verdict.Warnings.Count > 0)
            {
                for (int index = 0; index < verdict.Warnings.Count; index++) text.AppendLine("- " + Safe(verdict.Warnings[index]));
                text.AppendLine();
            }
            if (verdict.IsRecording)
            {
                text.AppendLine("Replay: " + Safe(verdict.ReplayLine));
                text.AppendLine();
            }
            // What is attached is stated from what was actually written, never
            // from a rule about how many files there are meant to be.
            text.AppendLine(picks.Has(AiItems.Pdf)
                ? "This file and `" + Handoff.ScreensFile + "` are what was handed over."
                : "This file is what was handed over. There is no picture document with it: it was not selected.");
            text.AppendLine();
            text.AppendLine("The operator chose which parts of this file to include. A part that is not below was");
            text.AppendLine("left out on purpose and is not missing because it could not be obtained; where");
            text.AppendLine("something could not be obtained, it says so in its own words.");
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
            text.AppendLine("| parts included | " + result.Sections.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                Total().ToString(CultureInfo.InvariantCulture) + " |");
            text.AppendLine();
        }

        private static int Total()
        {
            int total = 0;
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++)
            {
                string kind = AiItems.KindOf(order[index]);
                if (String.Equals(kind, AiKinds.Section, StringComparison.Ordinal) ||
                    String.Equals(kind, AiKinds.Code, StringComparison.Ordinal)) total++;
            }
            return total;
        }

        // What is in this file, by the numbers it was given this time. Anything
        // that points at a part - the request text, the window, a sentence inside
        // another part - reads it from here, so a dropped part cannot leave a
        // reference pointing at whatever moved into its place.
        private static void Contents(StringBuilder text, SessionMdResult result)
        {
            text.AppendLine("## Contents");
            text.AppendLine();
            for (int index = 0; index < result.Sections.Count; index++)
            {
                SessionMdSection section = result.Sections[index];
                text.AppendLine("- " + section.Number.ToString(CultureInfo.InvariantCulture) + ". " + section.Title);
            }
            text.AppendLine();
            string[] order = AiItems.Order();
            List<string> left = new List<string>();
            for (int index = 0; index < order.Length; index++)
            {
                string kind = AiItems.KindOf(order[index]);
                if (!String.Equals(kind, AiKinds.Section, StringComparison.Ordinal) &&
                    !String.Equals(kind, AiKinds.Code, StringComparison.Ordinal)) continue;
                if (result.Includes(order[index])) continue;
                left.Add(AiItems.Title(order[index]));
            }
            if (left.Count > 0)
            {
                text.AppendLine("Left out by the operator, and therefore not part of this handover: " + Join(left) + ".");
                text.AppendLine();
            }
        }

        private static void Heading(StringBuilder text, int number, string title)
        {
            text.AppendLine("## " + number.ToString(CultureInfo.InvariantCulture) + ". " + title);
            text.AppendLine();
        }

        private static void Environment(StringBuilder text, StudioSession session, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Environment));
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

        private static void PrivacySection(StringBuilder text, StudioSession session, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Privacy));
            text.AppendLine(Privacy.PolicyStatement(session.ValuePolicy));
            text.AppendLine();
            text.AppendLine("- Value policy in force: `" + Safe(session.ValuePolicy) + "`");
            text.AppendLine("- Shortcut keys are recorded only while Ctrl, Alt or the Windows key is held, plus Enter, Tab, Escape and F1-F12 on their own. " +
                "An unmodified letter, digit or punctuation key is never looked at.");
            text.AppendLine("- No clipboard while recording, no window contents of applications that were not in front, no screen recording. " +
                "The clipboard is read in one place only: when the operator presses the button that takes an assistant's answer back into the code screen.");
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

        private static void Apps(StringBuilder text, StudioSession session, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Apps));
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

        private static void Screens(StringBuilder text, StudioSession session, ScreensPdfResult pdf, AiPicks picks, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Screens));
            bool pictures = picks.Has(AiItems.Pdf);
            text.AppendLine(pictures
                ? "A screen is one top level window as it was at one moment. `" + Handoff.ScreensFile + "` has one page per screen that has a picture, " +
                  "and the page names the screen id, so the two files line up."
                : "A screen is one top level window as it was at one moment. **No picture document was handed over** - the operator did not select it - " +
                  "so the rows below are the whole description of these windows and there is no page to look at.");
            text.AppendLine();
            text.AppendLine("| screen | " + (pictures ? "pdf page | " : "") + "app | window title | class | hwnd at record time | rect (x,y,w,h) | parts | picture |");
            text.AppendLine("|---|" + (pictures ? "---|" : "") + "---|---|---|---|---|---|---|");
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = session.Screens.Screens[index];
                string app = AppNameFor(session, screen);
                string picture;
                if (!pictures) picture = screen.HasShot ? "taken, not handed over (not selected)" : "no - " + Safe(screen.ShotProblem ?? "reason not recorded");
                else picture = screen.HasShot ? (screen.PdfPage > 0 ? "yes" : "taken, left out of the pdf") : "no - " + Safe(screen.ShotProblem ?? "reason not recorded");
                text.AppendLine("| " + Safe(screen.ScreenId) + " | " +
                    (pictures ? ((screen.PdfPage > 0 ? screen.PdfPage.ToString(CultureInfo.InvariantCulture) : "-") + " | ") : "") +
                    Safe(app) + " | " + Safe(screen.Title) + " | " + Safe(screen.ClassName) + " | 0x" + screen.Hwnd.ToString("X") +
                    " | " + Rect(screen.Rect) + " | " + screen.ComponentIds.Count + " | " + Safe(picture) + " |");
            }
            text.AppendLine();
            if (pictures && pdf != null && pdf.OmittedScreens.Count > 0)
            {
                text.AppendLine("Screens left out of `" + Handoff.ScreensFile + "` to stay inside its size budget: " + Join(pdf.OmittedScreens) +
                    ". Their rows above are complete; only the picture is missing from the attachment.");
                text.AppendLine();
            }
        }

        private static int Elements(StringBuilder text, StudioSession session, SessionMdResult result, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Elements));
            if (session.Elements.Count == 0)
            {
                text.AppendLine("No element was obtained. If the screens show a working application, it draws its own surface and exposes no structure; " +
                    "in that case a picture is the only description of it that exists, and any automation has to be built on " +
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
                result.LimitsApplied.Add("The element table stopped at " + MaxElementRows.ToString(CultureInfo.InvariantCulture) +
                    " rows; " + result.ElementsOmitted.ToString(CultureInfo.InvariantCulture) + " more are in elements.jsonl.");
            }
            return written;
        }

        private static void Actions(StringBuilder text, StudioSession session, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Actions));
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

        private static void Replay(StringBuilder text, StudioSession session, int number)
        {
            bool any = false;
            for (int index = 0; index < session.Steps.Count; index++) if (session.Steps[index].LastReplay != null) any = true;
            Heading(text, number, AiItems.Title(AiItems.Replay));
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

        private static void Coverage(StringBuilder text, StudioSession session, ScreensPdfResult pdf, AiPicks picks, SessionMdResult result, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Coverage));
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
                "without any layer having noticed. It is also not a list of what the operator left out - that is in the contents above.");
            text.AppendLine();
            if (!picks.Has(AiItems.Pdf))
            {
                text.AppendLine("Picture attachment: **none was handed over.** The operator did not select it. This is a choice, not a failure to capture.");
                text.AppendLine();
            }
            else if (pdf != null)
            {
                text.AppendLine("Picture attachment: " + (pdf.Written ? pdf.PageCount + " page(s), " + pdf.SizeText + ", budget " +
                    (pdf.BudgetBytes / 1024) + " KB, stored " + Safe(pdf.Quality) : "not written - " + Safe(pdf.Problem)));
                text.AppendLine();
                for (int index = 0; index < pdf.Notes.Count; index++) text.AppendLine("- " + Safe(pdf.Notes[index]));
                if (pdf.Notes.Count > 0) text.AppendLine();
                for (int index = 0; index < pdf.Notes.Count; index++) result.LimitsApplied.Add(pdf.Notes[index]);
                if (pdf.OmittedScreens.Count > 0)
                {
                    result.LimitsApplied.Add("screens.pdf left out " + pdf.OmittedScreens.Count.ToString(CultureInfo.InvariantCulture) +
                        " screen(s) to stay inside its budget: " + Join(pdf.OmittedScreens) + ".");
                }
            }
            if (session.Diagnostics.Count > 0)
            {
                text.AppendLine("Tool diagnostics during this session:");
                text.AppendLine();
                for (int index = 0; index < session.Diagnostics.Count && index < MaxDiagnostics; index++) text.AppendLine("- " + Safe(session.Diagnostics[index]));
                if (session.Diagnostics.Count > MaxDiagnostics)
                {
                    text.AppendLine("- ... and " + (session.Diagnostics.Count - MaxDiagnostics) + " more, in the session folder");
                    result.LimitsApplied.Add("The diagnostics list stopped at " + MaxDiagnostics.ToString(CultureInfo.InvariantCulture) +
                        " lines; " + (session.Diagnostics.Count - MaxDiagnostics).ToString(CultureInfo.InvariantCulture) + " more are in the session folder.");
                }
                text.AppendLine();
            }
        }

        // How to write an automation from this, and the rules the code may not
        // trade away.
        //
        // The vocabulary lives here rather than beside the code, because it is
        // true whether or not any code was handed over: a request that asks for a
        // module to be written from scratch needs the nine operations and the
        // rules exactly as much as one that asks for an edit.
        private static void Guidance(StringBuilder text, StudioSession session, AiPicks picks, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Guidance));
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
                    "not the controls a person sees in them. A picture is the only description of that surface that exists. " +
                    "Anything built on coordinates alone will break when the window moves or the layout changes; say so rather than presenting it as reliable.");
            }
            text.AppendLine();
            text.AppendLine("### The automation is five modules, in each language");
            text.AppendLine();
            text.AppendLine("C# and VBA are two spellings of one automation here. Neither is the real one with the");
            text.AppendLine("other translated from it: both are written from the same list of steps, so they cannot");
            text.AppendLine("disagree about what the recording meant.");
            text.AppendLine();
            text.AppendLine("| module | what it is for | edited by hand |");
            text.AppendLine("|---|---|---|");
            text.AppendLine("| `" + CodeModules.Workflow + "` | the recorded procedure. One line is one thing the operator did | yes, this is the one |");
            text.AppendLine("| `" + CodeModules.RecordedFacts + "` | the addresses, the intervals and the text the recording captured | only to change what a step aims at |");
            text.AppendLine("| `" + CodeModules.RuntimeCore + "` | the nine operations, the waiting and the stopping | no |");
            text.AppendLine("| `" + CodeModules.RuntimeLocator + "` | turning a recorded address back into the element on screen now | no |");
            text.AppendLine("| `" + CodeModules.RuntimeNative + "` | the declarations and the pointer | no |");
            text.AppendLine();
            text.AppendLine("The split is the point. Taking one step out of the procedure is deleting one line");
            text.AppendLine("of `" + CodeModules.Workflow + "`, and the other four modules are not touched at all. Do not");
            text.AppendLine("undo it by moving the machinery back into the workflow.");
            text.AppendLine();
            text.AppendLine("### The operations, shared by C# and VBA");
            text.AppendLine();
            text.AppendLine("Both languages define the same nine operations. They are the whole surface an");
            text.AppendLine("automation built from this recording is expected to use. Keep the names and the");
            text.AppendLine("meanings; add helpers around them if you need to.");
            text.AppendLine();
            text.AppendLine("| operation | meaning |");
            text.AppendLine("|---|---|");
            text.AppendLine("| `FindWindow` | wait for a window that fits the recorded class, title and application, and bring it to the front. Windows that cannot be operated - hidden, cloaked, no size - are not candidates. If several still fit, the one already in front is used, the run says so, and that window is held for the rest of it. |");
            text.AppendLine("| `FocusElement` | put the keyboard on the element the recording says held it. |");
            text.AppendLine("| `InvokeElement` | press the element. A pattern it publishes is preferred; synthetic input is the fallback and needs the window in front. |");
            text.AppendLine("| `SetElementText` | write text into the element. Refused on a password field. |");
            text.AppendLine("| `ReadElementText` | read the element back, to check an effect. |");
            text.AppendLine("| `SendKeys` | send one recorded chord, after the keyboard has been put back. |");
            text.AppendLine("| `WaitGap` | wait the interval the operator left, clamped to 120 ms - 4000 ms. |");
            text.AppendLine("| `WaitIdle` | wait for the front window to stop changing, up to a stated ceiling. |");
            text.AppendLine("| `AskSecret` | a value the recording deliberately never kept. Ask the operator; never write it anywhere. |");
            text.AppendLine();
            text.AppendLine("A line of the workflow names one of these and the id of a step. The wait before");
            text.AppendLine("that step, putting the keyboard back where the recording had it, and settling");
            text.AppendLine("afterwards are done by the runtime around that one line, which is why one step is");
            text.AppendLine("one line and why deleting the line deletes the wait with it.");
            text.AppendLine();
            text.AppendLine("Rules that may not be traded away for convenience:");
            text.AppendLine();
            text.AppendLine("- **Never press a remembered screen coordinate.** An element is found again by its locators, in");
            text.AppendLine("  the order they are listed. A place inside an element is a fraction of that element's rectangle");
            text.AppendLine("  as it is now, never a stored point on the desktop.");
            text.AppendLine("- A locator that matches more than one element decides nothing. Try the next one; when they are");
            text.AppendLine("  all spent, stop and say so.");
            text.AppendLine("- Window handles and process ids in this file are from the recorded run and mean nothing later.");
            text.AppendLine("- A secret is asked for at the moment it is needed and is never written to a file or a log.");
            text.AppendLine("- Do not swallow a failure. Stopping with a reason is a result; carrying on regardless is not.");
            text.AppendLine();
            text.AppendLine("VBA reaches controls through Win32 only: a class name with a dialog control id, or a class name");
            text.AppendLine("with its index. Where the recording has no such address the VBA workflow says so at that point");
            text.AppendLine("with `Unsupported`. Do not replace those with coordinates.");
            text.AppendLine();
            if (!picks.AnyCode)
            {
                text.AppendLine("**No code was handed over with this.** The operator did not select any, so there is no module");
                text.AppendLine("here to change. Answer about what to do, not with a module nobody can see.");
                text.AppendLine();
            }
        }

        // One language's modules, whole, exactly as they are in the editor now.
        //
        // Never a summary and never an excerpt: an assistant asked to return a
        // whole module has to have been given the whole module, or what comes
        // back is a reconstruction of the parts it was not shown.
        private static int Code(StringBuilder text, CodeProject project, string language, int number, SessionMdResult result)
        {
            string id = String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal) ? AiItems.Vba : AiItems.Engine;
            Heading(text, number, AiItems.Title(id));
            if (project == null)
            {
                text.AppendLine("No automation has been generated for this session yet. It is written when the code screen is opened.");
                text.AppendLine();
                return 0;
            }
            List<CodeFile> files = project.Files(language);
            if (files.Count == 0)
            {
                text.AppendLine("No automation has been generated for this session yet.");
                text.AppendLine();
                return 0;
            }
            string fence = String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal) ? "vb" : "csharp";
            text.AppendLine(String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal)
                ? "The VBA modules as they are on screen now, in full. The single file this becomes is `" +
                  CodeModules.Workflow + "." + ScriptLanguages.ArtefactExtension(language) + "`."
                : "The C# modules as they are on screen now, in full. This is the automation itself, and it is the main thing " +
                  "that gets edited. The single file this becomes is `" + CodeModules.Workflow + "." +
                  ScriptLanguages.ArtefactExtension(language) + "`, which is these modules with a thin PowerShell wrapper over them.");
            text.AppendLine();
            for (int index = 0; index < files.Count; index++)
            {
                CodeFile file = files[index];
                text.AppendLine("### " + file.FileName + " (" +
                    CodeProject.LineCount(file.Text).ToString(CultureInfo.InvariantCulture) + " lines)");
                text.AppendLine();
                text.AppendLine("```" + fence);
                text.AppendLine(file.Text == null ? "" : file.Text.TrimEnd());
                text.AppendLine("```");
                text.AppendLine();
            }
            return files.Count;
        }

        // The PowerShell of a built artefact, without the C# it carries.
        //
        // It is here as a thing of its own because it is a different question:
        // how the handed over file starts, where it writes its log and what it
        // does when a run fails. It is generated on every build and is not edited
        // by hand, which is said here so nothing comes back rewriting it.
        private static bool Wrapper(StringBuilder text, CodeProject project, int number)
        {
            Heading(text, number, AiItems.Title(AiItems.Wrapper));
            text.AppendLine("**This is generated, not written.** The build produces it from the modules beside it on every");
            text.AppendLine("build, so it is not an edit target: a change returned for this file would be overwritten by the");
            text.AppendLine("next build without anyone being told. It is here so that questions about how the built");
            text.AppendLine("`" + CodeModules.Workflow + "." + ScriptLanguages.ArtefactExtension(ScriptLanguages.PowerShell) +
                "` starts, logs and reports a failure can be answered from what it actually does.");
            text.AppendLine();
            text.AppendLine("The C# it compiles is not repeated here; where the wrapper carries the engine, this shows the");
            text.AppendLine("boundary instead.");
            text.AppendLine();
            text.AppendLine("```powershell");
            text.AppendLine(CodeBuild.WrapperOnly().TrimEnd());
            text.AppendLine("```");
            text.AppendLine();
            return true;
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
