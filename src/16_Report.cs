namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public static class Report
    {
        public static string Build(Session session)
        {
            if (session == null) throw new ArgumentNullException("session");
            StringBuilder html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>App Studio report</title><style>");
            html.Append("body{margin:0;background:#f4f7fb;color:#172033;font:14px/1.55 Segoe UI,Arial,sans-serif}main{max-width:1100px;margin:auto;padding:24px}h1,h2,h3{line-height:1.2}section,.card{background:#fff;border:1px solid #d8e0ea;border-radius:10px;padding:18px;margin:0 0 18px}.conclusion{background:#132238;color:#fff}.warning{background:#7c2d12;color:#fff;padding:12px;border-radius:7px;font-weight:700}.muted{color:#64748b}.bad{color:#b42318;font-weight:700}.good{color:#087443;font-weight:700}table{border-collapse:collapse;width:100%;margin:8px 0 16px}th,td{border:1px solid #d8e0ea;padding:7px;text-align:left;vertical-align:top}th{background:#eef3f8}code,pre{font-family:Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere}img{max-width:100%;height:auto;border:1px solid #94a3b8}details{margin:8px 0}summary{cursor:pointer;font-weight:600}.pill{display:inline-block;border-radius:999px;background:#e2e8f0;padding:2px 8px;margin:2px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:8px}.toc a{color:#075985;margin-right:12px}");
            html.Append("</style></head><body><main>");
            html.Append("<header><h1>App Studio report</h1><p class=\"muted\">Self-contained investigation record generated ").Append(E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))).Append(".</p><nav class=\"toc\"><a href=\"#conclusion\">Conclusion</a><a href=\"#index\">Elements</a><a href=\"#timeline\">Timeline</a><a href=\"#diagnostics\">Diagnostics</a><a href=\"#appendix\">Appendix</a></nav></header>");
            Conclusion(html, session);
            Index(html, session);
            Details(html, session);
            Timeline(html, session);
            DiagnosticsSection(html, session);
            Appendix(html, session);
            html.Append("</main></body></html>");
            return html.ToString();
        }

        private static void Conclusion(StringBuilder html, Session session)
        {
            int probes = 0;
            for (int index = 0; index < session.Elements.Count; index++) probes += session.Elements[index].Probes.Count;
            html.Append("<section id=\"conclusion\" class=\"conclusion\"><h2>1. Conclusion</h2>");
            if (session.ValueCapture == "full") html.Append("<p class=\"warning\">This session recorded full value text by explicit selection.</p>");
            html.Append("<div class=\"grid\"><div><b>Session</b><br>").Append(E(Value(session.Label, session.Id))).Append("</div><div><b>Targets</b><br>").Append(TargetCount(session)).Append(" process(es)</div><div><b>Pinned elements</b><br>").Append(session.Elements.Count).Append("</div><div><b>Acquisition failures</b><br>").Append(session.AcquisitionFailures.Count).Append("</div><div><b>Operation probes</b><br>").Append(probes == 0 ? "not run" : probes.ToString(CultureInfo.InvariantCulture) + " recorded").Append("</div><div><b>Value policy</b><br>").Append(E(session.ValueCapture)).Append("</div></div>");
            html.Append("<p>Framework / bitness / elevation are reported per target when observed; unknown values remain explicitly marked in the machine-readable record and appendix.</p></section>");
        }

        private static void Index(StringBuilder html, Session session)
        {
            html.Append("<section id=\"index\"><h2>2. Element records</h2><table><thead><tr><th>ID</th><th>Label</th><th>Control type</th><th>Best confidence</th><th>Restart verification</th></tr></thead><tbody>");
            for (int index = 0; index < session.Elements.Count; index++)
            {
                ElementRecord record = session.Elements[index];
                string confidence = "none";
                bool restart = false;
                for (int locatorIndex = 0; locatorIndex < record.Locators.Count; locatorIndex++)
                {
                    if (confidence == "none" || Rank(record.Locators[locatorIndex].Confidence.Level) > Rank(confidence)) confidence = record.Locators[locatorIndex].Confidence.Level;
                    for (int verifyIndex = 0; verifyIndex < record.Locators[locatorIndex].Verifications.Count; verifyIndex++) if (record.Locators[locatorIndex].Verifications[verifyIndex].Context == "restart" && record.Locators[locatorIndex].Verifications[verifyIndex].SameElement) restart = true;
                }
                html.Append("<tr><td><a href=\"#").Append(E(record.ElementId)).Append("\">").Append(E(record.ElementId)).Append("</a></td><td>").Append(E(record.Label)).Append("</td><td>").Append(E(record.Uia == null ? null : record.Uia.ControlType)).Append("</td><td>").Append(E(confidence)).Append("</td><td>").Append(restart ? "survived" : "not verified / failed").Append("</td></tr>");
            }
            if (session.Elements.Count == 0) html.Append("<tr><td colspan=\"5\">No elements were pinned.</td></tr>");
            html.Append("</tbody></table></section>");
        }

        private static void Details(StringBuilder html, Session session)
        {
            for (int index = 0; index < session.Elements.Count; index++)
            {
                ElementRecord record = session.Elements[index];
                html.Append("<article class=\"card\" id=\"").Append(E(record.ElementId)).Append("\"><h3>3. ").Append(E(record.ElementId)).Append(" - ").Append(E(record.Label)).Append("</h3>");
                for (int shotIndex = 0; shotIndex < record.Shots.Count; shotIndex++)
                {
                    string uri = ImageUri(record.Shots[shotIndex].File);
                    if (uri != null) html.Append("<figure><img src=\"").Append(uri).Append("\" alt=\"Captured evidence for ").Append(E(record.ElementId)).Append("\"><figcaption>").Append(E(record.Shots[shotIndex].ShotId)).Append(" / ").Append(E(record.Shots[shotIndex].CaptureMethod)).Append("</figcaption></figure>");
                }
                html.Append("<table><tbody><tr><th>UIA Name</th><td>").Append(E(record.Uia == null ? null : record.Uia.Name)).Append("</td><th>ControlType</th><td>").Append(E(record.Uia == null ? null : record.Uia.ControlType)).Append("</td></tr>");
                html.Append("<tr><th>AutomationId</th><td>").Append(E(record.Uia == null ? null : record.Uia.AutomationId)).Append("</td><th>Win32 class / ctrlId</th><td>").Append(E(record.Win32 == null ? null : record.Win32.ClassName)).Append(" / ").Append(record.Win32 == null ? "unknown" : record.Win32.CtrlId.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
                html.Append("<tr><th>Recorded value</th><td colspan=\"3\">").Append(E(Recorded(record.RecordedValue))).Append("</td></tr></tbody></table>");
                html.Append("<details><summary>Full UI Automation, tree path, Win32 detail, and children</summary><pre>").Append(E(ElementEvidence(session, record))).Append("</pre></details>");
                LocatorTable(html, record);
                ProbeTable(html, record);
                html.Append("<h4>Notes</h4>");
                if (record.Notes.Count == 0) html.Append("<p class=\"muted\">No notes.</p>");
                else { html.Append("<ul>"); for (int noteIndex = 0; noteIndex < record.Notes.Count; noteIndex++) html.Append("<li>").Append(E(record.Notes[noteIndex])).Append("</li>"); html.Append("</ul>"); }
                html.Append("</article>");
            }
        }

        private static void LocatorTable(StringBuilder html, ElementRecord record)
        {
            html.Append("<h4>Locator candidates</h4><table><thead><tr><th>Strategy</th><th>Expression</th><th>Confidence</th><th>Reasons</th><th>Verifications</th></tr></thead><tbody>");
            for (int index = 0; index < record.Locators.Count; index++)
            {
                Locator locator = record.Locators[index];
                StringBuilder reasons = new StringBuilder();
                for (int reasonIndex = 0; reasonIndex < locator.Confidence.Reasons.Count; reasonIndex++) reasons.AppendLine(locator.Confidence.Reasons[reasonIndex]);
                StringBuilder verifications = new StringBuilder();
                for (int verifyIndex = 0; verifyIndex < locator.Verifications.Count; verifyIndex++)
                {
                    Verification value = locator.Verifications[verifyIndex];
                    verifications.Append(value.Context).Append(" run=").Append(value.TargetRunId).Append(" matches=").Append(value.MatchCount).Append(" same=").Append(value.SameElement).Append(" duration=").Append(value.DurationMs).AppendLine("ms");
                }
                html.Append("<tr><td>").Append(E(locator.Strategy)).Append("</td><td><code>").Append(E(JsonWriter.Write(LocatorJson.Build(locator)))).Append("</code></td><td>").Append(E(locator.Confidence.Level)).Append(" (").Append(locator.Confidence.Score).Append(")</td><td>").Append(E(reasons.ToString())).Append("</td><td>").Append(E(verifications.ToString())).Append("</td></tr>");
            }
            if (record.Locators.Count == 0) html.Append("<tr><td colspan=\"5\">No locator candidate could be generated from captured material.</td></tr>");
            html.Append("</tbody></table>");
        }

        private static void ProbeTable(StringBuilder html, ElementRecord record)
        {
            html.Append("<h4>Operation probes</h4><table><thead><tr><th>Kind</th><th>Method used</th><th>Outcome</th><th>Duration</th><th>Side effects / error</th></tr></thead><tbody>");
            for (int index = 0; index < record.Probes.Count; index++)
            {
                ProbeResult probe = record.Probes[index];
                StringBuilder detail = new StringBuilder();
                if (probe.Error != null) detail.Append(probe.Error.Code).Append(' ').Append(probe.Error.Message);
                for (int sideIndex = 0; sideIndex < probe.SideEffects.Count; sideIndex++) detail.AppendLine().Append(probe.SideEffects[sideIndex].Type).Append(": ").Append(probe.SideEffects[sideIndex].Detail);
                html.Append("<tr><td>").Append(E(probe.Kind.ToString().ToLowerInvariant())).Append("</td><td><code>").Append(E(probe.Method)).Append("</code></td><td>").Append(E(probe.Outcome)).Append("</td><td>").Append(probe.DurationMs).Append(" ms</td><td>").Append(E(detail.ToString())).Append("</td></tr>");
            }
            if (record.Probes.Count == 0) html.Append("<tr><td colspan=\"5\">No operation probe was run.</td></tr>");
            html.Append("</tbody></table>");
        }

        private static void Timeline(StringBuilder html, Session session)
        {
            html.Append("<section id=\"timeline\"><h2>4. Timeline</h2><table><thead><tr><th>Seq</th><th>At</th><th>Type</th><th>Source</th><th>Detail</th></tr></thead><tbody>");
            for (int index = 0; index < session.Events.Count; index++)
            {
                SessionEvent item = session.Events[index];
                html.Append("<tr><td>").Append(item.Seq).Append("</td><td>").Append(E(item.At.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))).Append("</td><td>").Append(E(item.Type)).Append("</td><td>").Append(E(item.Source)).Append("</td><td>").Append(E(item.Detail)).Append("</td></tr>");
            }
            html.Append("</tbody></table></section>");
        }

        private static void DiagnosticsSection(StringBuilder html, Session session)
        {
            html.Append("<section id=\"diagnostics\"><h2>5. Technical diagnostics</h2><p>Failures are retained as evidence; an empty list is not required.</p><table><thead><tr><th>At</th><th>Layer</th><th>Code</th><th>Element</th><th>Reason</th></tr></thead><tbody>");
            for (int index = 0; index < session.AcquisitionFailures.Count; index++)
            {
                AcquisitionFailure item = session.AcquisitionFailures[index];
                html.Append("<tr><td>").Append(E(item.At.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))).Append("</td><td>").Append(E(item.Layer)).Append("</td><td class=\"bad\">").Append(E(item.Code)).Append("</td><td>").Append(E(item.ElementId)).Append("</td><td>").Append(E(item.Detail)).Append("</td></tr>");
            }
            if (session.AcquisitionFailures.Count == 0) html.Append("<tr><td colspan=\"5\">No acquisition failure was recorded.</td></tr>");
            html.Append("</tbody></table></section>");
        }

        private static void Appendix(StringBuilder html, Session session)
        {
            html.Append("<section id=\"appendix\"><h2>6. Appendix</h2><h3>Environment</h3><pre>").Append(E(session.Environment == null ? "Environment unavailable" : JsonWriter.Write(session.Environment))).Append("</pre>");
            html.Append("<h3>Schema and MANIFEST</h3><p>schemaVersion: 1. MANIFEST.json in the investigation pack contains byte counts and SHA-256 hashes. Those hashes support change detection; they are not encryption and do not provide confidentiality.</p>");
            html.Append("<h3>Known limits</h3><ul><li>Full-screen captures cannot be masked automatically and can contain business data.</li><li>DPI alignment must be rechecked on the on-site monitor and remote-desktop configuration.</li><li>Java Access Bridge, SAP GUI Scripting, and product-specific automation require separate target-specific paths.</li><li>UI Automation and PrintWindow quality depend on the target implementation.</li></ul></section>");
        }

        private static string ElementEvidence(Session session, ElementRecord record)
        {
            JsonObject value = new JsonObject()
                .Add("uia", record.Uia == null ? null : new JsonObject().Add("name", record.Uia.Name).Add("automationId", record.Uia.AutomationId).Add("controlType", record.Uia.ControlType).Add("localizedControlType", record.Uia.LocalizedControlType).Add("className", record.Uia.ClassName).Add("frameworkId", record.Uia.FrameworkId).Add("isEnabled", record.Uia.IsEnabled).Add("isOffscreen", record.Uia.IsOffscreen).Add("isKeyboardFocusable", record.Uia.IsKeyboardFocusable).Add("isPassword", record.Uia.IsPassword).Add("helpText", record.Uia.HelpText).Add("acceleratorKey", record.Uia.AcceleratorKey).Add("accessKey", record.Uia.AccessKey).Add("boundingRect", Rect(record.Uia.BoundingRect)).Add("supportedPatterns", record.Uia.SupportedPatterns).Add("treePath", Nodes(record.Uia.TreePath)).Add("children", Nodes(record.Uia.Children)))
                .Add("win32", record.Win32 == null ? null : new JsonObject().Add("class", record.Win32.ClassName).Add("realClass", record.Win32.RealClass).Add("caption", SessionSchema.PersistentCaption(session, record)).Add("ctrlId", record.Win32.CtrlId).Add("style", record.Win32.Style).Add("exStyle", record.Win32.ExStyle).Add("windowRect", Rect(record.Win32.WindowRect)).Add("clientRect", Rect(record.Win32.ClientRect)).Add("visible", record.Win32.Visible).Add("enabled", record.Win32.Enabled).Add("zIndex", record.Win32.ZIndex).Add("threadId", record.Win32.ThreadId));
            return JsonWriter.Write(value);
        }

        private static object[] Nodes(UiaNode[] nodes)
        {
            if (nodes == null) return new object[0];
            object[] result = new object[nodes.Length];
            for (int index = 0; index < nodes.Length; index++) result[index] = new JsonObject().Add("controlType", nodes[index].ControlType).Add("name", nodes[index].Name).Add("automationId", nodes[index].AutomationId).Add("indexAmongSameType", nodes[index].IndexAmongSameType).Add("siblingCount", nodes[index].SiblingCount);
            return result;
        }

        private static JsonObject Rect(RectValue rect) { return rect == null ? null : new JsonObject().Add("x", rect.X).Add("y", rect.Y).Add("width", rect.Width).Add("height", rect.Height); }

        private static string ImageUri(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }

        private static string Recorded(RecordedValue value)
        {
            if (value == null || value.Kind == "none") return "none";
            if (!String.IsNullOrEmpty(value.Content) && !value.Masked) return value.Content;
            return value.Kind + " " + value.Length.ToString(CultureInfo.InvariantCulture) + " characters [masked: " + Value(value.MaskRule, "unspecified") + "]";
        }

        private static int TargetCount(Session session)
        {
            System.Collections.Generic.HashSet<int> values = new System.Collections.Generic.HashSet<int>();
            for (int index = 0; index < session.Elements.Count; index++) values.Add(session.Elements[index].Win32 == null ? 0 : session.Elements[index].Win32.ProcessId);
            return values.Count;
        }

        private static int Rank(string confidence) { return confidence == "high" ? 3 : (confidence == "medium" ? 2 : (confidence == "low" ? 1 : 0)); }
        private static string Value(string value, string fallback) { return String.IsNullOrWhiteSpace(value) ? fallback : value; }
        private static string E(string value)
        {
            if (value == null) return "<span class=\"muted\">unknown</span>";
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }
    }
}
