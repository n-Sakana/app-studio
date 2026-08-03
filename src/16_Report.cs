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
            StringBuilder html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            html.Append("<title>App Studio - ").Append(E(session.Id)).Append("</title><style>");
            html.Append(Css());
            html.Append("</style></head><body>");
            Header(html, session);
            html.Append("<main>");
            Summary(html, session, pdf);
            PrivacyBox(html, session);
            ScreensSection(html, session, pdf, result);
            StepsSection(html, session);
            ElementsSection(html, session);
            ReplaySection(html, session);
            LimitsSection(html, session, pdf);
            html.Append("</main><script>");
            html.Append(Script());
            html.Append("</script></body></html>");
            return html.ToString();
        }

        // ---------- sections ----------

        private static void Header(StringBuilder html, StudioSession session)
        {
            html.Append("<header><div class=\"bar\"><div><h1>App Studio</h1><p class=\"sub\">");
            html.Append(session.Kind == StudioSession.KindRecord ? "recording" : "snap").Append(" &middot; ");
            html.Append(E(session.Id)).Append(" &middot; ");
            html.Append(E(session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            html.Append("</p></div><div class=\"search\"><input id=\"q\" type=\"search\" placeholder=\"filter rows and steps...\" autocomplete=\"off\">");
            html.Append("<span id=\"qcount\" class=\"muted\"></span></div></div>");
            html.Append("<nav><a href=\"#summary\">summary</a><a href=\"#screens\">screens</a>");
            if (session.Steps.Count > 0) html.Append("<a href=\"#steps\">actions</a>");
            html.Append("<a href=\"#elements\">elements</a>");
            if (HasReplay(session)) html.Append("<a href=\"#replay\">replay</a>");
            html.Append("<a href=\"#limits\">limits</a></nav></header>");
        }

        private static void Summary(StringBuilder html, StudioSession session, ScreensPdfResult pdf)
        {
            html.Append("<section id=\"summary\"><h2>Summary</h2><div class=\"stats\">");
            Stat(html, session.Screens.Screens.Count.ToString(CultureInfo.InvariantCulture), "screens");
            Stat(html, session.Screens.ShotCount.ToString(CultureInfo.InvariantCulture), "with a picture");
            Stat(html, session.Elements.Count.ToString(CultureInfo.InvariantCulture), "elements");
            Stat(html, session.Steps.Count.ToString(CultureInfo.InvariantCulture), "actions");
            Stat(html, session.Apps.Count.ToString(CultureInfo.InvariantCulture), "applications");
            Stat(html, session.Limits.Count.ToString(CultureInfo.InvariantCulture), "stated limits");
            html.Append("</div>");
            if (session.Apps.Count > 0)
            {
                html.Append("<div class=\"scroll\"><table><thead><tr><th>key</th><th>process</th><th>executable</th><th>note</th></tr></thead><tbody>");
                for (int index = 0; index < session.Apps.Count; index++)
                {
                    AppRef app = session.Apps[index];
                    html.Append("<tr><td>").Append(E(app.Key)).Append("</td><td>").Append(E(app.ProcessName))
                        .Append("</td><td class=\"mono\">").Append(E(app.ExecutablePath == null ? "not readable" : app.ExecutablePath))
                        .Append("</td><td>").Append(E(app.PathProblem == null ? "-" : app.PathProblem)).Append("</td></tr>");
                }
                html.Append("</tbody></table></div>");
            }
            html.Append("<p class=\"muted\">Rectangles are physical screen pixels. Window handles and process ids belong to the run that was recorded and mean nothing later.</p>");
            if (pdf != null)
            {
                html.Append("<p class=\"muted\">Assistant handover: <code>ai/session.md</code> and <code>ai/screens.pdf</code>");
                if (pdf.Written) html.Append(" (").Append(pdf.PageCount).Append(" page(s), ").Append(E(pdf.SizeText)).Append(", ").Append(E(pdf.Quality)).Append(")");
                else html.Append(" - the picture document was not written: ").Append(E(pdf.Problem));
                html.Append(". Those two files are the whole handover; nothing else is attached.</p>");
            }
            html.Append("</section>");
        }

        private static void PrivacyBox(StringBuilder html, StudioSession session)
        {
            html.Append("<section class=\"callout\"><h2>What was written down</h2><p>");
            html.Append(E(Privacy.PolicyStatement(session.ValuePolicy)));
            html.Append("</p><p class=\"muted\">Value policy in force: <code>").Append(E(session.ValuePolicy)).Append("</code>. ");
            html.Append("Shortcut keys are recorded only while Ctrl, Alt or the Windows key is held, plus Enter, Tab, Escape and F1-F12 on their own. ");
            html.Append("An unmodified letter, digit or punctuation key is never looked at, and the clipboard is never read.</p></section>");
        }

        private static void ScreensSection(StringBuilder html, StudioSession session, ScreensPdfResult pdf, ReportResult result)
        {
            html.Append("<section id=\"screens\"><h2>Screens</h2>");
            if (session.Screens.Screens.Count == 0)
            {
                html.Append("<p class=\"warn\">No screen was recorded for this session.</p></section>");
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
                html.Append("<details class=\"row\"><summary>").Append(E(screen.ScreenId)).Append(" &mdash; ").Append(E(screen.Title));
                html.Append(" <span class=\"muted\">").Append(E(screen.Size)).Append(", ").Append(screen.ComponentIds.Count).Append(" part(s)</span></summary>");
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
                html.Append("</details>");
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
            html.Append("</section>");
        }

        private static void StepsSection(StringBuilder html, StudioSession session)
        {
            if (session.Steps.Count == 0) return;
            html.Append("<section id=\"steps\"><h2>What the operator did</h2>");
            html.Append("<div class=\"scroll\"><table><thead><tr><th>step</th><th>+s</th><th>kind</th><th>application</th><th>target</th><th>confidence</th><th>effect</th></tr></thead><tbody>");
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                html.Append("<tr class=\"row\"><td class=\"mono\"><a href=\"#").Append(E(step.StepId)).Append("\">").Append(E(step.StepId)).Append("</a></td><td class=\"mono\">")
                    .Append(E((step.OffsetMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)))
                    .Append("</td><td>").Append(E(step.Kind)).Append("</td><td>").Append(E(step.AppName))
                    .Append("</td><td>").Append(E(step.Kind == StepRecord.KindKeyChord ? step.KeyChord : step.ElementLabel))
                    .Append("</td><td>").Append(Confidence(step.Confidence)).Append("</td><td class=\"muted\">").Append(E(step.EffectSummary)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div>");
            for (int index = 0; index < session.Steps.Count; index++) Step(html, session.Steps[index]);
            html.Append("</section>");
        }

        private static void Step(StringBuilder html, StepRecord step)
        {
            html.Append("<details class=\"row\" id=\"").Append(E(step.StepId)).Append("\"><summary><b>").Append(E(step.StepId))
                .Append("</b> ").Append(E(step.Headline)).Append(" <span class=\"muted\">").Append(E(step.AppName)).Append(" &middot; ")
                .Append(E(step.WindowTitle)).Append("</span></summary>");
            html.Append("<div class=\"scroll\"><table><tbody>");
            Row(html, "time", "+" + (step.OffsetMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s (" + step.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + ")");
            Row(html, "window", (step.WindowTitle == null ? "-" : step.WindowTitle) + "   class " + (step.WindowClass == null ? "-" : step.WindowClass));
            Row(html, "screen before / after", (step.ScreenBefore == null ? "-" : step.ScreenBefore) + " / " + (step.ScreenAfter == null ? "-" : step.ScreenAfter));
            if (step.Kind == StepRecord.KindKeyChord) Row(html, "key", step.KeyChord);
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
                html.Append("<h4>How this element can be found again</h4><div class=\"scroll\"><table><thead><tr><th>strategy</th><th>expression</th><th>confidence</th><th>why</th></tr></thead><tbody>");
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
                html.Append("<h4>Could not be obtained</h4><ul class=\"warnlist\">");
                for (int index = 0; index < step.Unavailable.Count; index++) html.Append("<li>").Append(E(step.Unavailable[index])).Append("</li>");
                html.Append("</ul>");
            }
            if (step.Diagnostics.Count > 0)
            {
                html.Append("<p class=\"muted\">diagnostics: ").Append(E(Join(step.Diagnostics))).Append("</p>");
            }
            if (step.LastReplay != null) ReplayDetail(html, step);
            html.Append("</details>");
        }

        private static void ElementsSection(StringBuilder html, StudioSession session)
        {
            html.Append("<section id=\"elements\"><h2>Elements</h2>");
            if (session.Elements.Count == 0)
            {
                html.Append("<p class=\"warn\">Nothing was obtained. If the pictures show a working application, it draws its own surface and publishes no structure. ");
                html.Append("That is a real limit, not a failure to look: the pictures are then the only description of the surface that exists, ");
                html.Append("and an automation built on coordinates alone will break as soon as the window moves.</p></section>");
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
            html.Append("</tbody></table></div></section>");
        }

        private static void ReplaySection(StringBuilder html, StudioSession session)
        {
            if (!HasReplay(session)) return;
            html.Append("<section id=\"replay\"><h2>Replay</h2>");
            html.Append("<p class=\"muted\">A route that reported <code>notSupported</code> never acted, so the next one was tried. ");
            html.Append("Only one route ever carries a state changing operation out, and nothing falls back to the recorded coordinates.</p>");
            html.Append("<div class=\"scroll\"><table><thead><tr><th>step</th><th>result</th><th>resolved by</th><th>routes tried</th><th>reason</th></tr></thead><tbody>");
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.LastReplay == null) continue;
                html.Append("<tr class=\"row\"><td class=\"mono\"><a href=\"#").Append(E(step.StepId)).Append("\">").Append(E(step.StepId)).Append("</a></td><td>")
                    .Append(Outcome(step.LastReplay.State)).Append("</td><td class=\"mono\">").Append(E(step.LastReplay.ResolvedBy))
                    .Append("</td><td class=\"mono\">").Append(E(step.LastReplay.AttemptLine)).Append("</td><td class=\"muted\">")
                    .Append(E(step.LastReplay.Reason)).Append("</td></tr>");
            }
            html.Append("</tbody></table></div></section>");
        }

        private static void ReplayDetail(StringBuilder html, StepRecord step)
        {
            html.Append("<h4>Replay of this step</h4><p>").Append(Outcome(step.LastReplay.State)).Append(" <span class=\"muted\">")
                .Append(E(step.LastReplay.Reason)).Append("</span></p>");
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

        private static void LimitsSection(StringBuilder html, StudioSession session, ScreensPdfResult pdf)
        {
            html.Append("<section id=\"limits\"><h2>Coverage and limits</h2>");
            if (session.Coverage.Count > 0)
            {
                html.Append("<div class=\"scroll\"><table><thead><tr><th>layer</th><th>state</th><th>elements</th><th>ms</th><th>truncated</th><th>reasons</th></tr></thead><tbody>");
                for (int index = 0; index < session.Coverage.Count; index++)
                {
                    ScanCoverage coverage = session.Coverage[index];
                    html.Append("<tr><td class=\"mono\">").Append(E(coverage.Provider)).Append("</td><td>").Append(E(coverage.State))
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
            html.Append("<h3>What could not be obtained</h3>");
            if (session.Limits.Count == 0)
            {
                html.Append("<p class=\"muted\">No layer reported a limit.</p>");
            }
            else
            {
                html.Append("<ul class=\"warnlist\">");
                for (int index = 0; index < session.Limits.Count; index++) html.Append("<li>").Append(E(session.Limits[index])).Append("</li>");
                html.Append("</ul>");
            }
            // Printed whether or not anything was listed. A short list of limits
            // is no more a proof of completeness than an empty one.
            html.Append("<p class=\"warn\">This list is what the layers reported while they ran. It is not a proof that this record is complete: ");
            html.Append("an area an application draws itself publishes nothing to report, so it can be missing from every table here without any layer having noticed.</p>");
            if (pdf != null && pdf.Notes.Count > 0)
            {
                html.Append("<h3>Picture attachment</h3><ul class=\"warnlist\">");
                for (int index = 0; index < pdf.Notes.Count; index++) html.Append("<li>").Append(E(pdf.Notes[index])).Append("</li>");
                html.Append("</ul>");
            }
            if (session.Diagnostics.Count > 0)
            {
                html.Append("<details><summary>Tool diagnostics (").Append(session.Diagnostics.Count).Append(")</summary><pre>");
                for (int index = 0; index < session.Diagnostics.Count; index++) html.Append(E(session.Diagnostics[index])).Append("\n");
                html.Append("</pre></details>");
            }
            html.Append("<p class=\"muted\">Generated ").Append(E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)))
                .Append(" by ").Append(App.Name).Append(" ").Append(App.Version).Append(". This file has no external reference and works offline.</p>");
            html.Append("</section>");
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
"details{border:1px solid var(--soft);border-radius:8px;padding:8px 12px;margin:8px 0;background:var(--surface)}" +
"summary{cursor:pointer;font-weight:600}" +
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
"if(hit&&term!==''&&rows[i].tagName==='DETAILS')rows[i].open=true;" +
"}" +
"count.textContent=term===''?'':shown+' shown';" +
"}" +
"box.addEventListener('input',apply);" +
"})();";
        }
    }
}
