namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class ReportResult
    {
        public string Path;
        public long Bytes;
        public bool Written;
        public string Problem;
        public int EmbeddedPictures;
        public List<string> Notes = new List<string>();
    }

    // The report a person reads. It does not try to look like the application
    // that was investigated - a drawing of a window tells a reader nothing they
    // could not see by opening the application. It is a searchable record:
    // every element, its place in the hierarchy, which layer saw it, how it can
    // be addressed, what state it was in, and what could not be obtained.
    //
    // One file, no external reference of any kind, so it opens from a memory
    // stick on a machine with no network.
    public static class Report
    {
        private const int PictureBudgetBytes = 24 * 1024 * 1024;
        private const int PictureMaxPixels = 1100;
        private const int PictureQuality = 78;

        public static ReportResult Write(StudioSession session, string path, ScreensPdfResult pdf)
        {
            ReportResult result = new ReportResult();
            result.Path = path;
            try
            {
                string html = Build(session, pdf, result);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, html, new UTF8Encoding(false));
                result.Written = true;
                result.Bytes = new FileInfo(path).Length;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
            }
            return result;
        }

        public static string Build(StudioSession session, ScreensPdfResult pdf, ReportResult result)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (result == null) result = new ReportResult();
            SessionVerdict verdict = SessionVerdict.Of(session);
            StringBuilder html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            html.Append("<title>App Studio - ").Append(E(session.Id)).Append("</title><style>");
            html.Append(Css());
            html.Append("</style></head><body>");
            Header(html, session, verdict);
            html.Append("<main>");
            Conclusion(html, session, verdict, pdf);
            StepsSection(html, session, verdict);
            ScreensSection(html, session, pdf, result);
            ElementsSection(html, session);
            ReplaySection(html, session, verdict);
            InputSection(html, session);
            LimitsSection(html, session, pdf);
            MethodSection(html, session);
            html.Append("</main><script>");
            html.Append(Script());
            html.Append("</script></body></html>");
            return html.ToString();
        }

        // ---------- the shape of every section ----------
        //
        // A section is a heading, one line saying what it amounts to, and one
        // fold holding all of it. The fold opens onto a flat box that scrolls.
        // There is never a fold inside a fold: a reader who opens something has
        // to find the answer there, not more things to open.

        private static void Open(StringBuilder html, string id, string title, string lede, string count)
        {
            html.Append("<section id=\"").Append(id).Append("\"><h2>").Append(E(title)).Append("</h2>");
            if (!String.IsNullOrEmpty(lede)) html.Append("<p class=\"lede\">").Append(E(lede)).Append("</p>");
            html.Append("<details class=\"fold\"><summary>").Append(E(Word("report-more.txt", "Details")));
            if (!String.IsNullOrEmpty(count)) html.Append(" <span class=\"muted\">").Append(E(count)).Append("</span>");
            html.Append("</summary><div class=\"panel\">");
        }

        private static void Close(StringBuilder html)
        {
            html.Append("</div></details></section>");
        }

        private static string Word(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        // ---------- sections ----------

        private static void Header(StringBuilder html, StudioSession session, SessionVerdict verdict)
        {
            html.Append("<header><div class=\"bar\"><div><h1>App Studio</h1><p class=\"sub\">");
            html.Append(E(Word(session.Kind == StudioSession.KindRecord ? "kind-record.txt" : "kind-snap.txt",
                session.Kind == StudioSession.KindRecord ? "recording" : "snap"))).Append(" &middot; ");
            html.Append(E(session.Id)).Append(" &middot; ");
            html.Append(E(session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            html.Append("</p></div><div class=\"search\"><input id=\"q\" type=\"search\" placeholder=\"")
                .Append(E(Word("report-filter.txt", "filter rows and steps..."))).Append("\" autocomplete=\"off\">");
            html.Append("<span id=\"qcount\" class=\"muted\"></span></div></div>");
            html.Append("<nav><a href=\"#summary\">").Append(E(Word("report-nav-summary.txt", "summary"))).Append("</a>");
            if (session.Steps.Count > 0) html.Append("<a href=\"#steps\">").Append(E(Word("detail-steps.txt", "what was done"))).Append("</a>");
            html.Append("<a href=\"#screens\">").Append(E(Word("detail-screens.txt", "screens"))).Append("</a>");
            html.Append("<a href=\"#elements\">").Append(E(Word("report-nav-elements.txt", "elements"))).Append("</a>");
            if (verdict.HasReplay) html.Append("<a href=\"#replay\">").Append(E(Word("detail-replay-state.txt", "replay"))).Append("</a>");
            if (session.InputEvents.Count > 0) html.Append("<a href=\"#input\">").Append(E(Word("detail-input.txt", "input timeline"))).Append("</a>");
            html.Append("<a href=\"#limits\">").Append(E(Word("detail-limits.txt", "what could not be obtained"))).Append("</a>");
            html.Append("<a href=\"#method\">").Append(E(Word("report-nav-method.txt", "how this was made"))).Append("</a></nav></header>");
        }

        // The first thing on the page, and the only thing a reader has to read:
        // what happened, how much of it, what is wrong with it, and the one move
        // that follows from that.
        private static void Conclusion(StringBuilder html, StudioSession session, SessionVerdict verdict, ScreensPdfResult pdf)
        {
            html.Append("<section id=\"summary\" class=\"verdict\">");
            html.Append("<p class=\"state\">").Append(StatePill(verdict)).Append("<b>").Append(E(verdict.Headline)).Append("</b></p>");
            html.Append("<div class=\"stats\">");
            if (verdict.IsRecording) Stat(html, verdict.Steps.ToString(CultureInfo.InvariantCulture), Word("stat-steps.txt", "actions"));
            Stat(html, verdict.Screens.ToString(CultureInfo.InvariantCulture), Word("stat-screens.txt", "screens"));
            Stat(html, verdict.Shots.ToString(CultureInfo.InvariantCulture), Word("stat-shots.txt", "pictures"));
            Stat(html, verdict.Elements.ToString(CultureInfo.InvariantCulture), Word("stat-elements.txt", "elements"));
            Stat(html, verdict.Limits.ToString(CultureInfo.InvariantCulture), Word("stat-limits.txt", "stated limits"));
            if (verdict.IsRecording) Stat(html, verdict.InputEvents.ToString(CultureInfo.InvariantCulture), Word("stat-events.txt", "input events"));
            html.Append("</div>");
            if (verdict.Warnings.Count > 0)
            {
                html.Append("<ul class=\"warnings\">");
                for (int index = 0; index < verdict.Warnings.Count; index++) html.Append("<li>").Append(E(verdict.Warnings[index])).Append("</li>");
                html.Append("</ul>");
            }
            if (verdict.IsRecording)
            {
                html.Append("<p class=\"replayline\">").Append(E(Word("detail-replay-state.txt", "Replay"))).Append(": ")
                    .Append(E(verdict.ReplayLine)).Append("</p>");
            }
            html.Append("<p class=\"next\"><b>").Append(E(Word("detail-next.txt", "Next"))).Append("</b>: ").Append(E(verdict.NextAction)).Append("</p>");
            if (pdf != null)
            {
                html.Append("<p class=\"muted\">").Append(E(Word("report-handover.txt", "Assistant handover: ai/session.md and ai/screens.pdf, and nothing else.")));
                if (pdf.Written) html.Append(" (").Append(pdf.PageCount).Append(" page(s), ").Append(E(pdf.SizeText)).Append(", ").Append(E(pdf.Quality)).Append(")");
                else html.Append(" - ").Append(E(Word("report-handover-failed.txt", "the picture document was not written"))).Append(": ").Append(E(pdf.Problem));
                html.Append("</p>");
            }
            html.Append("</section>");
        }

        private static void StepsSection(StringBuilder html, StudioSession session, SessionVerdict verdict)
        {
            if (session.Steps.Count == 0) return;
            string lede = session.Steps.Count.ToString(CultureInfo.InvariantCulture) + " " + Word("list-steps.txt", "actions") +
                (verdict.NotReplayable > 0
                    ? " / " + verdict.NotReplayable.ToString(CultureInfo.InvariantCulture) + " " + Word("verdict-partial-locator.txt", "cannot be replayed")
                    : "");
            Open(html, "steps", Word("detail-steps.txt", "What was done"), lede, session.Steps.Count.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < session.Steps.Count; index++) Step(html, session.Steps[index]);
            Close(html);
        }

        private static void Step(StringBuilder html, StepRecord step)
        {
            bool weak = !SessionVerdict.CanReplay(step);
            html.Append("<div class=\"row block").Append(weak ? " weak" : "").Append("\" id=\"").Append(E(step.StepId)).Append("\">");
            html.Append("<p class=\"blockhead\"><b>").Append(E(step.StepId)).Append("</b> ").Append(E(step.Headline));
            html.Append(" <span class=\"muted\">").Append(E(step.AppName)).Append(" &middot; ").Append(E(step.WindowTitle)).Append("</span>");
            if (step.LastReplay != null) html.Append(" ").Append(Outcome(step.LastReplay.State));
            html.Append("</p>");
            html.Append("<div class=\"scroll\"><table><tbody>");
            Row(html, "time", "+" + (step.OffsetMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s (" + step.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + ")" +
                (step.GapMs > 0 ? ", " + step.GapMs.ToString(CultureInfo.InvariantCulture) + " ms after the previous action" : "") +
                (step.HoldMs > 0 ? ", held " + step.HoldMs.ToString(CultureInfo.InvariantCulture) + " ms" : ""));
            Row(html, "window", (step.WindowTitle == null ? "-" : step.WindowTitle) + "   class " + (step.WindowClass == null ? "-" : step.WindowClass));
            Row(html, "screen before / after", (step.ScreenBefore == null ? "-" : step.ScreenBefore) + " / " + (step.ScreenAfter == null ? "-" : step.ScreenAfter));
            if (step.Kind == StepRecord.KindKeyChord) Row(html, "key", step.KeyChord);
            if (!String.IsNullOrEmpty(step.Modifiers)) Row(html, "modifiers", step.Modifiers);
            if (step.Kind != StepRecord.KindAppSwitch && step.Kind != StepRecord.KindKeyChord)
            {
                Row(html, "element", step.ElementLabel);
                Row(html, "control type", step.ControlType == null ? step.Role : step.ControlType);
                Row(html, "name", step.Name);
                Row(html, "AutomationId", step.AutomationId);
                Row(html, "class / ctrlId", (step.ClassName == null ? "-" : step.ClassName) + " / " + (step.CtrlId == 0 ? "-" : step.CtrlId.ToString(CultureInfo.InvariantCulture)));
                Row(html, "rect", Rect(step.Rect));
                Row(html, "acquisition sources", Join(step.Sources));
                Row(html, "hierarchy path", step.TreePath);
            }
            if (step.Point != null)
            {
                Row(html, "position", step.Point.X + "," + step.Point.Y +
                    (step.ToPoint == null ? "" : " -> " + step.ToPoint.X + "," + step.ToPoint.Y) +
                    (step.Dpi > 0 ? "   " + step.Dpi + " dpi" : "") +
                    (String.IsNullOrEmpty(step.MonitorId) ? "" : "   " + step.MonitorId));
            }
            if (!String.IsNullOrEmpty(step.Button)) Row(html, "button", step.Button);
            if (step.WheelDelta != 0) Row(html, "wheel", step.WheelDelta.ToString(CultureInfo.InvariantCulture));
            if (!String.IsNullOrEmpty(step.DropLabel)) Row(html, "released on", step.DropLabel);
            if (!String.IsNullOrEmpty(step.FocusLabel)) Row(html, "keyboard was on", step.FocusLabel);
            if (step.ValueKind != "none")
            {
                string value;
                if (step.ValueKind == "secret") value = "not recorded - secret field, rule " + (step.MaskRule == null ? "?" : step.MaskRule);
                else if (step.Value != null) value = step.Value + "   (" + Privacy.DescribeLength(step.ValueLength) + ")";
                else value = "not recorded, " + Privacy.DescribeLength(step.ValueLength) + " entered";
                Row(html, "value entered", value);
            }
            Row(html, "observed effect", step.EffectSummary);
            html.Append("</tbody></table></div>");
            if (step.Locators.Count > 0)
            {
                html.Append("<div class=\"scroll\"><table><thead><tr><th>strategy</th><th>expression</th><th>confidence</th><th>why</th></tr></thead><tbody>");
                for (int index = 0; index < step.Locators.Count; index++)
                {
                    ElementLocator locator = step.Locators[index];
                    html.Append("<tr><td class=\"mono\">").Append(E(locator.Strategy)).Append("</td><td class=\"mono\">").Append(E(locator.Display))
                        .Append("</td><td>").Append(Confidence(locator.Confidence)).Append("</td><td class=\"muted\">")
                        .Append(E(locator.Reasons.Count > 0 ? locator.Reasons[0] : "-")).Append("</td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            if (step.Unavailable.Count > 0)
            {
                html.Append("<ul class=\"warnlist\">");
                for (int index = 0; index < step.Unavailable.Count; index++) html.Append("<li>").Append(E(step.Unavailable[index])).Append("</li>");
                html.Append("</ul>");
            }
            if (step.Diagnostics.Count > 0) html.Append("<p class=\"muted\">diagnostics: ").Append(E(Join(step.Diagnostics))).Append("</p>");
            if (step.LastReplay != null) ReplayDetail(html, step);
            html.Append("</div>");
        }

        private static void ScreensSection(StringBuilder html, StudioSession session, ScreensPdfResult pdf, ReportResult result)
        {
            int missing = session.Screens.Screens.Count - session.Screens.ShotCount;
            string lede = session.Screens.Screens.Count == 0
                ? Word("screens-none.txt", "No screen was acquired.")
                : session.Screens.Screens.Count.ToString(CultureInfo.InvariantCulture) + " " + Word("stat-screens.txt", "screens") +
                  (missing > 0 ? " / " + missing.ToString(CultureInfo.InvariantCulture) + " " + Word("screen-noshot.txt", "no picture") : "");
            Open(html, "screens", Word("detail-screens.txt", "Screens"), lede, session.Screens.Screens.Count.ToString(CultureInfo.InvariantCulture));
            if (session.Screens.Screens.Count == 0)
            {
                html.Append("<p class=\"warn\">").Append(E(Word("screens-none.txt", "No screen was acquired."))).Append("</p>");
                Close(html);
                return;
            }
            html.Append("<div class=\"scroll\"><table><thead><tr><th>screen</th><th>pdf page</th><th>window</th><th>class</th><th>rect</th><th>parts</th><th>picture</th></tr></thead><tbody>");
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = session.Screens.Screens[index];
                html.Append("<tr class=\"row\"><td class=\"mono\">").Append(E(screen.ScreenId)).Append("</td><td>")
                    .Append(screen.PdfPage > 0 ? screen.PdfPage.ToString(CultureInfo.InvariantCulture) : "-")
                    .Append("</td><td>").Append(E(screen.Title)).Append("</td><td class=\"mono\">").Append(E(screen.ClassName))
                    .Append("</td><td class=\"mono\">").Append(E(Rect(screen.Rect))).Append("</td><td>").Append(screen.ComponentIds.Count)
                    .Append("</td><td>");
                if (screen.HasShot) html.Append("<span class=\"ok\">yes</span>");
                else html.Append("<span class=\"bad\">no</span> <span class=\"muted\">").Append(E(screen.ShotProblem)).Append("</span>");
                html.Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");

            long budget = PictureBudgetBytes;
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = session.Screens.Screens[index];
                if (!screen.HasShot) continue;
                html.Append("<div class=\"row block\"><p class=\"blockhead\"><b>").Append(E(screen.ScreenId)).Append("</b> ").Append(E(screen.Title));
                html.Append(" <span class=\"muted\">").Append(E(screen.Size)).Append(", ").Append(screen.ComponentIds.Count).Append(" part(s)</span></p>");
                if (!String.IsNullOrEmpty(screen.Note)) html.Append("<p class=\"muted\">").Append(E(screen.Note)).Append("</p>");
                string uri = null;
                string problem = null;
                if (budget > 0)
                {
                    uri = DataUri(screen.ShotFile, out problem);
                    if (uri != null)
                    {
                        budget -= uri.Length;
                        result.EmbeddedPictures++;
                    }
                }
                else
                {
                    problem = "the report picture budget of " + (PictureBudgetBytes / (1024 * 1024)) + " MB was already used";
                }
                if (uri != null)
                {
                    html.Append("<img alt=\"screen ").Append(E(screen.ScreenId)).Append("\" src=\"").Append(uri).Append("\">");
                    html.Append("<p class=\"muted\">Embedded at most ").Append(PictureMaxPixels).Append(" px on the long side at quality ").Append(PictureQuality)
                        .Append("; the untouched original is <code>").Append(E(FileName(screen.ShotFile))).Append("</code> in the session folder.</p>");
                }
                else
                {
                    html.Append("<p class=\"warn\">The picture is not embedded here: ").Append(E(problem))
                        .Append(". The file <code>").Append(E(FileName(screen.ShotFile))).Append("</code> is in the session folder.</p>");
                    result.Notes.Add("Screen " + screen.ScreenId + " picture not embedded: " + problem);
                }
                html.Append("</div>");
            }
            if (pdf != null && pdf.OmittedScreens.Count > 0)
            {
                html.Append("<p class=\"warn\">Left out of <code>screens.pdf</code> to stay inside its size budget: ");
                for (int index = 0; index < pdf.OmittedScreens.Count; index++)
                {
                    if (index != 0) html.Append(", ");
                    html.Append(E(pdf.OmittedScreens[index]));
                }
                html.Append(". Their rows above are complete and their pictures are still in the session folder.</p>");
            }
            Close(html);
        }

        private static void ElementsSection(StringBuilder html, StudioSession session)
        {
            string lede = session.Elements.Count == 0
                ? Word("elements-none.txt", "Nothing was obtained.")
                : session.Elements.Count.ToString(CultureInfo.InvariantCulture) + " " + Word("stat-elements.txt", "elements");
            Open(html, "elements", Word("report-nav-elements.txt", "Elements"), lede, session.Elements.Count.ToString(CultureInfo.InvariantCulture));
            if (session.Elements.Count == 0)
            {
                html.Append("<p class=\"warn\">Nothing was obtained. If the pictures show a working application, it draws its own surface and publishes no structure. ");
                html.Append("That is a real limit, not a failure to look: the pictures are then the only description of the surface that exists, ");
                html.Append("and an automation built on coordinates alone will break as soon as the window moves.</p>");
                Close(html);
                return;
            }
            if (Acquire.PublishesNoStructure(session))
            {
                html.Append("<p class=\"warn\">Every screen here came back with nothing inside it. This application paints its own surface and publishes no structure, ");
                html.Append("so the table below describes windows, not the controls a person sees in them. The pictures are the only description of that surface that exists. ");
                html.Append("Any automation has to work from coordinates or from the application's own scripting interface, and coordinates break as soon as the window moves or the layout changes.</p>");
            }
            html.Append("<p class=\"muted\">An empty cell means the element did not expose that property. Use the filter box at the top to narrow the table.</p>");
            html.Append("<div class=\"scroll\"><table id=\"elements-table\"><thead><tr><th>id</th><th>screen</th><th>sources</th><th>control type</th><th>name</th><th>AutomationId</th><th>class</th><th>ctrlId</th><th>rect</th><th>state</th><th>patterns</th><th>path</th></tr></thead><tbody>");
            for (int index = 0; index < session.Elements.Count; index++)
            {
                ScanNode node = session.Elements[index];
                html.Append("<tr class=\"row\"><td class=\"mono\">E").Append(node.NodeId).Append("</td><td class=\"mono\">").Append(E(node.ScreenId))
                    .Append("</td><td class=\"mono\">").Append(E(Join(node.Sources))).Append("</td><td>").Append(E(node.ControlType == null ? node.Role : node.ControlType))
                    .Append("</td><td>").Append(E(node.Name)).Append("</td><td class=\"mono\">").Append(E(node.AutomationId))
                    .Append("</td><td class=\"mono\">").Append(E(node.ClassName)).Append("</td><td class=\"mono\">").Append(node.CtrlId == 0 ? "-" : node.CtrlId.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td class=\"mono\">").Append(E(Rect(node.Rect))).Append("</td><td>").Append(E(State(node)))
                    .Append("</td><td class=\"mono\">").Append(E(Join(node.Patterns))).Append("</td><td class=\"muted\">").Append(E(node.Path)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");
            Close(html);
        }

        private static void ReplaySection(StringBuilder html, StudioSession session, SessionVerdict verdict)
        {
            if (!verdict.HasReplay) return;
            Open(html, "replay", Word("detail-replay-state.txt", "Replay"), verdict.ReplayLine,
                (verdict.ReplayDone + verdict.ReplayStopped).ToString(CultureInfo.InvariantCulture));
            html.Append("<p class=\"muted\">A route that reported <code>notSupported</code> never acted, so the next one was tried. ");
            html.Append("Only one route ever carries a state changing operation out, and nothing falls back to the recorded coordinates. ");
            html.Append("\"waited\" is the interval this step had in the recording; \"settled\" is how long the application went on changing afterwards.</p>");
            html.Append("<div class=\"scroll\"><table><thead><tr><th>step</th><th>result</th><th>waited</th><th>settled</th><th>resolved by</th><th>routes tried</th><th>reason</th></tr></thead><tbody>");
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.LastReplay == null) continue;
                html.Append("<tr class=\"row\"><td class=\"mono\"><a href=\"#").Append(E(step.StepId)).Append("\">").Append(E(step.StepId)).Append("</a></td><td>")
                    .Append(Outcome(step.LastReplay.State)).Append("</td><td class=\"mono\">").Append(step.LastReplay.WaitedMs)
                    .Append("</td><td class=\"mono\">").Append(step.LastReplay.SettleMs)
                    .Append("</td><td class=\"mono\">").Append(E(step.LastReplay.ResolvedBy))
                    .Append("</td><td class=\"mono\">").Append(E(step.LastReplay.AttemptLine)).Append("</td><td class=\"muted\">")
                    .Append(E(step.LastReplay.Reason)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");
            Close(html);
        }

        private static void ReplayDetail(StringBuilder html, StepRecord step)
        {
            html.Append("<p class=\"muted\">").Append(E(Word("detail-replay-state.txt", "Replay"))).Append(": ")
                .Append(Outcome(step.LastReplay.State)).Append(" ").Append(E(step.LastReplay.Reason)).Append("</p>");
            if (step.LastReplay.Attempts.Count == 0) return;
            html.Append("<div class=\"scroll\"><table><thead><tr><th>route</th><th>method</th><th>outcome</th><th>ms</th><th>error</th><th>observed effect</th></tr></thead><tbody>");
            for (int index = 0; index < step.LastReplay.Attempts.Count; index++)
            {
                RouteAttempt attempt = step.LastReplay.Attempts[index];
                html.Append("<tr><td class=\"mono\">").Append(E(attempt.Route)).Append("</td><td class=\"mono\">").Append(E(attempt.Method))
                    .Append("</td><td>").Append(Outcome(attempt.Outcome)).Append("</td><td class=\"mono\">").Append(attempt.DurationMs)
                    .Append("</td><td class=\"muted\">").Append(E(ErrorText(attempt))).Append("</td><td class=\"muted\">").Append(E(attempt.Effect)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");
        }

        // Every event the watch saw, in order, with the interval between them.
        // This is the layer underneath the steps, and it is where a reader looks
        // when the question is whether something was missed rather than what it
        // meant.
        private static void InputSection(StringBuilder html, StudioSession session)
        {
            if (session.InputEvents.Count == 0) return;
            Open(html, "input", Word("detail-input.txt", "The raw input timeline"),
                session.InputEvents.Count.ToString(CultureInfo.InvariantCulture) + " " + Word("list-events.txt", "events"),
                session.InputEvents.Count.ToString(CultureInfo.InvariantCulture));
            html.Append("<p class=\"muted\">").Append(E(Word("report-input-note.txt",
                "Every event in the order it happened. A key is named only when it is a command key on the fixed watch list; ordinary typing appears as \"typing\" with no key on it."))).Append("</p>");
            html.Append("<div class=\"scroll\"><table><thead><tr><th>#</th><th>+s</th><th>gap ms</th><th>event</th><th>modifiers</th><th>position</th><th>dpi</th><th>window</th><th>element</th><th>step</th><th>note</th></tr></thead><tbody>");
            for (int index = 0; index < session.InputEvents.Count; index++)
            {
                InputEventRecord item = session.InputEvents[index];
                html.Append("<tr class=\"row\"><td class=\"mono\">").Append(item.Index)
                    .Append("</td><td class=\"mono\">").Append(E((item.OffsetMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)))
                    .Append("</td><td class=\"mono\">").Append(item.GapMs)
                    .Append("</td><td class=\"mono\">").Append(E(item.Display))
                    .Append("</td><td class=\"mono\">").Append(E(item.Modifiers))
                    .Append("</td><td class=\"mono\">").Append(item.X == 0 && item.Y == 0 ? "-" : E(item.X + "," + item.Y + (item.ToX == 0 && item.ToY == 0 ? "" : " -> " + item.ToX + "," + item.ToY)))
                    .Append("</td><td class=\"mono\">").Append(item.Dpi == 0 ? "-" : item.Dpi.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(E(item.WindowTitle))
                    .Append("</td><td>").Append(E(item.ElementLabel))
                    .Append("</td><td class=\"mono\">").Append(String.IsNullOrEmpty(item.StepId) ? "-" : "<a href=\"#" + E(item.StepId) + "\">" + E(item.StepId) + "</a>")
                    .Append("</td><td class=\"muted\">").Append(E(item.Note)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");
            Close(html);
        }

        private static void LimitsSection(StringBuilder html, StudioSession session, ScreensPdfResult pdf)
        {
            string lede = session.Limits.Count == 0
                ? Word("limits-none.txt", "No layer reported a limit. That is not a proof of completeness.")
                : session.Limits.Count.ToString(CultureInfo.InvariantCulture) + " " + Word("verdict-warn-limits.txt", "things could not be obtained");
            Open(html, "limits", Word("detail-limits.txt", "What could not be obtained"), lede, session.Limits.Count.ToString(CultureInfo.InvariantCulture));
            if (session.Limits.Count == 0)
            {
                html.Append("<p class=\"muted\">").Append(E(Word("limits-none.txt", "No layer reported a limit."))).Append("</p>");
            }
            else
            {
                html.Append("<ul class=\"warnlist\">");
                for (int index = 0; index < session.Limits.Count; index++) html.Append("<li class=\"row\">").Append(E(session.Limits[index])).Append("</li>");
                html.Append("</ul>");
            }
            // Printed whether or not anything was listed. A short list of limits
            // is no more a proof of completeness than an empty one.
            html.Append("<p class=\"warn\">This list is what the layers reported while they ran. It is not a proof that this record is complete: ");
            html.Append("an area an application draws itself publishes nothing to report, so it can be missing from every table here without any layer having noticed.</p>");
            if (session.Coverage.Count > 0)
            {
                html.Append("<div class=\"scroll\"><table><thead><tr><th>layer</th><th>state</th><th>elements</th><th>ms</th><th>truncated</th><th>reasons</th></tr></thead><tbody>");
                for (int index = 0; index < session.Coverage.Count; index++)
                {
                    ScanCoverage coverage = session.Coverage[index];
                    html.Append("<tr class=\"row\"><td class=\"mono\">").Append(E(coverage.Provider)).Append("</td><td>").Append(E(coverage.State))
                        .Append("</td><td>").Append(coverage.NodeCount).Append("</td><td>").Append(coverage.DurationMs)
                        .Append("</td><td>").Append(coverage.Truncated ? "yes" : "no").Append("</td><td class=\"muted\">");
                    for (int reason = 0; reason < coverage.Reasons.Count; reason++)
                    {
                        if (reason != 0) html.Append("<br>");
                        html.Append(E(coverage.Reasons[reason].Code)).Append(": ").Append(E(coverage.Reasons[reason].Message));
                    }
                    html.Append("</td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            if (pdf != null && pdf.Notes.Count > 0)
            {
                html.Append("<h3>Picture attachment</h3><ul class=\"warnlist\">");
                for (int index = 0; index < pdf.Notes.Count; index++) html.Append("<li class=\"row\">").Append(E(pdf.Notes[index])).Append("</li>");
                html.Append("</ul>");
            }
            Close(html);
        }

        // How the record was made, and what that means for the numbers above.
        // It is never the first thing a reader needs, so it is the last section.
        private static void MethodSection(StringBuilder html, StudioSession session)
        {
            Open(html, "method", Word("report-nav-method.txt", "How this was made"),
                Word("report-method-lede.txt", "What was written down, what was never looked at, and the machine it was made on."), null);
            html.Append("<p>").Append(E(Privacy.PolicyStatement(session.ValuePolicy))).Append("</p>");
            html.Append("<p class=\"muted\">Value policy in force: <code>").Append(E(session.ValuePolicy)).Append("</code>. ");
            if (!String.IsNullOrEmpty(session.InputWatchState)) html.Append("Pointer watch: <code>").Append(E(session.InputWatchState)).Append("</code>. ");
            html.Append("Rectangles are physical screen pixels. Window handles and process ids belong to the run that was recorded and mean nothing later.</p>");
            if (session.Apps.Count > 0)
            {
                html.Append("<div class=\"scroll\"><table><thead><tr><th>key</th><th>process</th><th>executable</th><th>note</th></tr></thead><tbody>");
                for (int index = 0; index < session.Apps.Count; index++)
                {
                    AppRef app = session.Apps[index];
                    html.Append("<tr class=\"row\"><td>").Append(E(app.Key)).Append("</td><td>").Append(E(app.ProcessName))
                        .Append("</td><td class=\"mono\">").Append(E(app.ExecutablePath == null ? "not readable" : app.ExecutablePath))
                        .Append("</td><td>").Append(E(app.PathProblem == null ? "-" : app.PathProblem)).Append("</td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            if (session.Diagnostics.Count > 0)
            {
                html.Append("<h3>Tool diagnostics (").Append(session.Diagnostics.Count).Append(")</h3><pre>");
                for (int index = 0; index < session.Diagnostics.Count; index++) html.Append(E(session.Diagnostics[index])).Append("\n");
                html.Append("</pre>");
            }
            html.Append("<p class=\"muted\">Generated ").Append(E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)))
                .Append(" by ").Append(App.Name).Append(" ").Append(App.Version).Append(". This file has no external reference and works offline.</p>");
            Close(html);
        }

        private static string StatePill(SessionVerdict verdict)
        {
            string tone = "muted";
            if (verdict.State == SessionVerdict.StateOk) tone = "ok";
            else if (verdict.State == SessionVerdict.StatePartial) tone = "warnpill";
            else if (verdict.State == SessionVerdict.StateFailed) tone = "bad";
            return "<span class=\"pill " + tone + "\">" + E(verdict.StateWord) + "</span> ";
        }

        // ---------- helpers ----------

        private static void Stat(StringBuilder html, string value, string label)
        {
            html.Append("<div class=\"stat\"><b>").Append(E(value)).Append("</b><span>").Append(E(label)).Append("</span></div>");
        }

        private static void Row(StringBuilder html, string key, string value)
        {
            html.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }

        private static string Confidence(string value)
        {
            string tone = value == "high" ? "ok" : (value == "medium" ? "warnpill" : "bad");
            return "<span class=\"pill " + tone + "\">" + E(value) + "</span>";
        }

        private static string Outcome(string value)
        {
            string tone = "muted";
            if (value == "done" || value == "success") tone = "ok";
            else if (value == "unknown" || value == "info") tone = "warnpill";
            else if (value == "notSupported" || value == "skipped") tone = "muted";
            else if (value != null) tone = "bad";
            return "<span class=\"pill " + tone + "\">" + E(value) + "</span>";
        }

        private static bool HasReplay(StudioSession session)
        {
            for (int index = 0; index < session.Steps.Count; index++) if (session.Steps[index].LastReplay != null) return true;
            return false;
        }

        // The picture is embedded reduced, because a report carrying a dozen
        // full desktop captures at their original size is a file nobody can
        // open. The reduction is stated on the page next to the picture and the
        // untouched original stays in the session folder.
        private static string DataUri(string path, out string problem)
        {
            problem = null;
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    problem = "the picture file is not on disk any more";
                    return null;
                }
                using (Bitmap source = new Bitmap(path))
                {
                    int width = source.Width;
                    int height = source.Height;
                    if (width > PictureMaxPixels || height > PictureMaxPixels)
                    {
                        double factor = Math.Min(PictureMaxPixels / (double)width, PictureMaxPixels / (double)height);
                        width = Math.Max(1, (int)Math.Round(width * factor));
                        height = Math.Max(1, (int)Math.Round(height * factor));
                    }
                    using (Bitmap copy = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics graphics = Graphics.FromImage(copy))
                        {
                            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                        }
                        ImageCodecInfo codec = null;
                        ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
                        for (int index = 0; index < codecs.Length; index++) if (codecs[index].MimeType == "image/jpeg") codec = codecs[index];
                        if (codec == null)
                        {
                            problem = "this machine has no JPEG encoder";
                            return null;
                        }
                        using (EncoderParameters parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)PictureQuality);
                            using (MemoryStream output = new MemoryStream())
                            {
                                copy.Save(output, codec, parameters);
                                return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                problem = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        private static string FileName(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private static string State(ScanNode node)
        {
            List<string> parts = new List<string>();
            if (node.Visible.HasValue) parts.Add(node.Visible.Value ? "visible" : "hidden");
            if (node.Enabled.HasValue) parts.Add(node.Enabled.Value ? "enabled" : "disabled");
            if (node.Offscreen.HasValue && node.Offscreen.Value) parts.Add("offscreen");
            if (node.KeyboardFocusable.HasValue && node.KeyboardFocusable.Value) parts.Add("focusable");
            if (node.IsPassword.HasValue && node.IsPassword.Value) parts.Add("password");
            if (node.Decoration) parts.Add("frame");
            return Join(parts);
        }

        private static string ErrorText(RouteAttempt attempt)
        {
            if (String.IsNullOrEmpty(attempt.ErrorCode) && String.IsNullOrEmpty(attempt.ErrorMessage)) return "-";
            return (attempt.ErrorCode == null ? "" : attempt.ErrorCode) + " " + (attempt.ErrorMessage == null ? "" : attempt.ErrorMessage);
        }

        private static string Rect(RectValue rect)
        {
            return rect == null ? "-" : rect.X + "," + rect.Y + "," + rect.Width + "," + rect.Height;
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

        private static string E(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '&') text.Append("&amp;");
                else if (character == '<') text.Append("&lt;");
                else if (character == '>') text.Append("&gt;");
                else if (character == '"') text.Append("&quot;");
                else if (character == '\'') text.Append("&#39;");
                else text.Append(character);
            }
            return text.ToString();
        }

        private static string Css()
        {
            return
"*{box-sizing:border-box}" +
":root{--bg:#f4f6f8;--fg:#1f2a37;--sub:#3c4b5c;--muted:#5d6b7a;--surface:#fff;--sunken:#eceff2;--line:#cfd7de;--soft:#e2e7ec;--accent:#2b5c96;--accentsoft:#e5edf7;--ok:#3a7a47;--oksoft:#e4f1e4;--bad:#b03e48;--badsoft:#fbe9ea;--warn:#a2701c;--warnsoft:#fbf3e2}" +
"@media (prefers-color-scheme:dark){:root{--bg:#14171b;--fg:#e9edf2;--sub:#c2cbd6;--muted:#97a3b1;--surface:#1c2127;--sunken:#101317;--line:#333b45;--soft:#272e37;--accent:#3d72b4;--accentsoft:#223650;--ok:#8fcb99;--oksoft:#22321f;--bad:#ed9aa3;--badsoft:#3a2226;--warn:#e4c179;--warnsoft:#2e2718}}" +
"body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.55 'Segoe UI',system-ui,sans-serif}" +
"header{position:sticky;top:0;z-index:5;background:var(--surface);border-bottom:1px solid var(--line)}" +
".bar{display:flex;flex-wrap:wrap;gap:12px;align-items:center;justify-content:space-between;max-width:1400px;margin:auto;padding:12px 20px}" +
"h1{font-size:18px;margin:0}h2{font-size:16px;margin:0 0 10px}h3{font-size:14px;margin:16px 0 6px}h4{font-size:13px;margin:14px 0 4px;color:var(--sub)}" +
".sub{margin:2px 0 0;color:var(--muted);font-size:12px}" +
".search{display:flex;align-items:center;gap:8px}" +
"input[type=search]{width:min(340px,60vw);padding:7px 10px;border:1px solid var(--line);border-radius:6px;background:var(--sunken);color:var(--fg);font:inherit}" +
"nav{max-width:1400px;margin:auto;padding:0 20px 10px;display:flex;flex-wrap:wrap;gap:14px}" +
"nav a{color:var(--accent);text-decoration:none;font-size:12px;font-weight:600}" +
"main{max-width:1400px;margin:auto;padding:20px}" +
"section{background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:18px;margin:0 0 18px}" +
"section.callout{background:var(--accentsoft);border-color:var(--accent)}" +
".stats{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}" +
".stat{background:var(--sunken);border:1px solid var(--soft);border-radius:8px;padding:8px 14px;min-width:104px}" +
".stat b{display:block;font-size:22px}.stat span{font-size:11px;font-weight:600;color:var(--muted)}" +
".scroll{overflow-x:auto;max-width:100%}" +
"table{border-collapse:collapse;width:100%;margin:6px 0 10px;font-size:13px}" +
"th,td{border:1px solid var(--soft);padding:5px 8px;text-align:left;vertical-align:top}" +
"thead th{background:var(--sunken)}" +
"tbody th{background:var(--sunken);white-space:nowrap;width:1%}" +
".mono{font-family:Consolas,'Cascadia Mono',monospace;font-size:12px;overflow-wrap:anywhere}" +
".muted{color:var(--muted)}" +
".pill{display:inline-block;border-radius:999px;padding:1px 9px;font-size:11px;font-weight:700;background:var(--sunken)}" +
".pill.ok{background:var(--oksoft);color:var(--ok)}.pill.bad{background:var(--badsoft);color:var(--bad)}.pill.warnpill{background:var(--warnsoft);color:var(--warn)}" +
".ok{color:var(--ok);font-weight:600}.bad{color:var(--bad);font-weight:600}" +
".warn{background:var(--warnsoft);border:1px solid var(--warn);color:var(--warn);border-radius:6px;padding:8px 12px}" +
".warnlist li{margin:3px 0}" +
"section.verdict{border-left:4px solid var(--accent)}" +
".state{display:flex;flex-wrap:wrap;align-items:baseline;gap:8px;margin:0 0 12px;font-size:17px;line-height:1.45}" +
".lede{margin:0 0 10px;color:var(--sub)}" +
".warnings{margin:10px 0 0;padding-left:20px;color:var(--warn)}" +
".warnings li{margin:3px 0}" +
".replayline{margin:10px 0 0;color:var(--sub)}" +
".next{margin:12px 0 0;padding:8px 12px;border-radius:6px;background:var(--accentsoft);color:var(--fg)}" +
/* One fold per section, and nothing inside it folds again. */
"details.fold{border:1px solid var(--soft);border-radius:8px;margin:10px 0 0;background:var(--sunken)}" +
"details.fold>summary{cursor:pointer;font-weight:600;padding:8px 12px}" +
".panel{max-height:62vh;overflow:auto;padding:0 12px 12px;background:var(--surface);border-top:1px solid var(--soft)}" +
".block{padding:10px 0;border-bottom:1px solid var(--soft)}" +
".block:last-child{border-bottom:0}" +
".block.weak{border-left:3px solid var(--warn);padding-left:10px}" +
".blockhead{margin:0 0 4px}" +
"img{max-width:100%;height:auto;border:1px solid var(--line);border-radius:6px;margin-top:8px}" +
"pre{white-space:pre-wrap;overflow-wrap:anywhere;font-family:Consolas,monospace;font-size:12px;background:var(--sunken);padding:10px;border-radius:6px}" +
"code{font-family:Consolas,monospace;font-size:12px;background:var(--sunken);padding:1px 5px;border-radius:4px}" +
"a{color:var(--accent)}" +
".hide{display:none}";
        }

        private static string Script()
        {
            return
"(function(){" +
"var box=document.getElementById('q');var count=document.getElementById('qcount');" +
"if(!box)return;" +
"function apply(){" +
"var term=box.value.toLowerCase();" +
"var rows=document.querySelectorAll('.row');var shown=0;" +
"for(var i=0;i<rows.length;i++){" +
"var hit=term===''||rows[i].textContent.toLowerCase().indexOf(term)>=0;" +
"if(hit){rows[i].classList.remove('hide');shown++;}else{rows[i].classList.add('hide');}" +
/* A match inside a closed fold is a match nobody can see, so the one fold
   that holds it is opened. There is only ever one to open. */
"if(hit&&term!==''){var fold=rows[i].closest('details');if(fold)fold.open=true;}" +
"}" +
"count.textContent=term===''?'':shown+' shown';" +
"}" +
"box.addEventListener('input',apply);" +
"})();";
        }
    }
}
