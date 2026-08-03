namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // The identifiers the assistant is allowed to quote. They are the node ids
    // the automatic scan already assigned, so a step that names E12 resolves
    // through exactly the same path as picking the part out of the result list.
    public sealed class CaseElementTable
    {
        private readonly Dictionary<string, ScanNode> byId = new Dictionary<string, ScanNode>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ScanNode> listed = new List<ScanNode>();

        public int TotalCount;
        public int ListedCount { get { return listed.Count; } }
        public int DroppedDecoration;
        public int DroppedOffscreen;
        public int DroppedOverLimit;
        public int DroppedNoRect;
        public string ScanId;

        public static string IdOf(ScanNode node)
        {
            return "E" + node.NodeId.ToString(CultureInfo.InvariantCulture);
        }

        public ScanNode Find(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            ScanNode node;
            return byId.TryGetValue(id.Trim(), out node) ? node : null;
        }

        public ScanNode[] Listed { get { return listed.ToArray(); } }

        // Every node keeps a resolvable id even when it did not make the written
        // table, so an assistant quoting a row from an older attachment is still
        // answered rather than rejected for a reason the reader cannot see.
        public static CaseElementTable Build(ScanResult result, int limit)
        {
            CaseElementTable table = new CaseElementTable();
            if (result == null) return table;
            table.ScanId = result.ScanId;
            table.TotalCount = result.Nodes.Count;
            List<ScanNode> candidates = new List<ScanNode>();
            for (int index = 0; index < result.Nodes.Count; index++)
            {
                ScanNode node = result.Nodes[index];
                table.byId[IdOf(node)] = node;
                if (node.Decoration) { table.DroppedDecoration++; continue; }
                if (node.Rect == null || node.Rect.Width <= 0 || node.Rect.Height <= 0) { table.DroppedNoRect++; continue; }
                if (node.Offscreen == true || node.Visible == false) { table.DroppedOffscreen++; continue; }
                candidates.Add(node);
            }
            if (candidates.Count <= limit)
            {
                table.listed.AddRange(candidates);
                return table;
            }
            // Over the limit the parts that can actually be addressed and acted
            // on are kept first. What was left out is counted and printed.
            List<ScanNode> preferred = new List<ScanNode>();
            List<ScanNode> rest = new List<ScanNode>();
            for (int index = 0; index < candidates.Count; index++)
            {
                if (Addressable(candidates[index])) preferred.Add(candidates[index]); else rest.Add(candidates[index]);
            }
            for (int index = 0; index < preferred.Count && table.listed.Count < limit; index++) table.listed.Add(preferred[index]);
            for (int index = 0; index < rest.Count && table.listed.Count < limit; index++) table.listed.Add(rest[index]);
            table.DroppedOverLimit = candidates.Count - table.listed.Count;
            table.listed.Sort(delegate(ScanNode first, ScanNode second) { return first.NodeId.CompareTo(second.NodeId); });
            return table;
        }

        private static bool Addressable(ScanNode node)
        {
            if (!String.IsNullOrEmpty(node.Name)) return true;
            if (!String.IsNullOrEmpty(node.AutomationId)) return true;
            if (node.Patterns != null && node.Patterns.Length > 0) return true;
            return node.KeyboardFocusable == true;
        }

        // Kept beside the case so an answer can still be taken in and run after
        // the program was closed and reopened. The positions are the ones the
        // scan saw; the operator is told so before a stored case is run again.
        public JsonObject ToJson()
        {
            List<object> items = new List<object>();
            List<string> listedIds = new List<string>();
            for (int index = 0; index < listed.Count; index++) listedIds.Add(IdOf(listed[index]));
            foreach (KeyValuePair<string, ScanNode> entry in byId)
            {
                ScanNode node = entry.Value;
                bool inTable = listedIds.Contains(entry.Key);
                items.Add(new JsonObject()
                    .Add("id", entry.Key)
                    .Add("nodeId", node.NodeId)
                    .Add("listed", inTable)
                    .Add("name", node.Name)
                    .Add("automationId", node.AutomationId)
                    .Add("controlType", node.ControlType)
                    .Add("localizedControlType", node.LocalizedControlType)
                    .Add("role", node.Role)
                    .Add("className", node.ClassName)
                    .Add("ctrlId", node.CtrlId)
                    .Add("hwnd", node.Hwnd)
                    .Add("rect", SessionLogJson.Rect(node.Rect))
                    .Add("enabled", node.Enabled)
                    .Add("offscreen", node.Offscreen)
                    .Add("visible", node.Visible)
                    .Add("keyboardFocusable", node.KeyboardFocusable)
                    .Add("isPassword", node.IsPassword)
                    .Add("decoration", node.Decoration)
                    .Add("patterns", SessionLogJson.Strings(node.Patterns))
                    .Add("sources", SessionLogJson.Strings(node.Sources.ToArray())));
            }
            return new JsonObject()
                .Add("kind", "caseElements")
                .Add("scanId", ScanId)
                .Add("totalCount", TotalCount)
                .Add("listedCount", listed.Count)
                .Add("droppedDecoration", DroppedDecoration)
                .Add("droppedOffscreen", DroppedOffscreen)
                .Add("droppedNoRect", DroppedNoRect)
                .Add("droppedOverLimit", DroppedOverLimit)
                .Add("elements", items.ToArray());
        }

        public static CaseElementTable Load(string path)
        {
            string text = CaseStore.ReadText(path);
            Dictionary<string, object> root = text == null ? null : JsonReader.ReadObject(text);
            if (root == null) return null;
            CaseElementTable table = new CaseElementTable();
            table.ScanId = JsonReader.Text(root, "scanId");
            table.TotalCount = JsonReader.Number(root, "totalCount", 0);
            table.DroppedDecoration = JsonReader.Number(root, "droppedDecoration", 0);
            table.DroppedOffscreen = JsonReader.Number(root, "droppedOffscreen", 0);
            table.DroppedNoRect = JsonReader.Number(root, "droppedNoRect", 0);
            table.DroppedOverLimit = JsonReader.Number(root, "droppedOverLimit", 0);
            object[] items = JsonReader.Items(root, "elements");
            if (items == null) return table;
            for (int index = 0; index < items.Length; index++)
            {
                Dictionary<string, object> item = items[index] as Dictionary<string, object>;
                if (item == null) continue;
                string id = JsonReader.Text(item, "id");
                if (String.IsNullOrEmpty(id)) continue;
                ScanNode node = new ScanNode();
                node.NodeId = JsonReader.Number(item, "nodeId", 0);
                node.Name = JsonReader.Text(item, "name");
                node.AutomationId = JsonReader.Text(item, "automationId");
                node.ControlType = JsonReader.Text(item, "controlType");
                node.LocalizedControlType = JsonReader.Text(item, "localizedControlType");
                node.Role = JsonReader.Text(item, "role");
                node.ClassName = JsonReader.Text(item, "className");
                node.CtrlId = JsonReader.Number(item, "ctrlId", 0);
                node.Hwnd = JsonReader.Number64(item, "hwnd", 0);
                node.Decoration = JsonReader.Flag(item, "decoration", false);
                if (JsonReader.Has(item, "enabled")) node.Enabled = JsonReader.Flag(item, "enabled", true);
                if (JsonReader.Has(item, "offscreen")) node.Offscreen = JsonReader.Flag(item, "offscreen", false);
                if (JsonReader.Has(item, "visible")) node.Visible = JsonReader.Flag(item, "visible", true);
                if (JsonReader.Has(item, "keyboardFocusable")) node.KeyboardFocusable = JsonReader.Flag(item, "keyboardFocusable", false);
                if (JsonReader.Has(item, "isPassword")) node.IsPassword = JsonReader.Flag(item, "isPassword", false);
                Dictionary<string, object> rect = JsonReader.Child(item, "rect");
                if (rect != null)
                {
                    RectValue value = new RectValue();
                    value.X = JsonReader.Number(rect, "x", 0);
                    value.Y = JsonReader.Number(rect, "y", 0);
                    value.Width = JsonReader.Number(rect, "width", 0);
                    value.Height = JsonReader.Number(rect, "height", 0);
                    node.Rect = value;
                }
                node.Patterns = TextArray(JsonReader.Items(item, "patterns"));
                string[] sources = TextArray(JsonReader.Items(item, "sources"));
                if (sources != null) for (int source = 0; source < sources.Length; source++) node.AddSource(sources[source]);
                table.byId[id] = node;
                if (JsonReader.Flag(item, "listed", false)) table.listed.Add(node);
            }
            table.listed.Sort(delegate(ScanNode first, ScanNode second) { return first.NodeId.CompareTo(second.NodeId); });
            return table;
        }

        private static string[] TextArray(object[] items)
        {
            if (items == null) return null;
            string[] result = new string[items.Length];
            for (int index = 0; index < items.Length; index++) result[index] = Convert.ToString(items[index], CultureInfo.InvariantCulture);
            return result;
        }
    }

    public sealed class RequestBundle
    {
        // The whole investigation as its own file, header and all.
        public string Investigation;
        // The same facts without the heading block, for embedding in the single
        // text attachment that already states the target above it.
        public string InvestigationBody;
        public string Request;
        public CaseElementTable Elements;
        public ScreenLedger Screens;
        // Set once the attachments have actually been written, so the request
        // text can name the files that exist rather than the ones intended.
        public HandoffBundle Handoff;
        public string ShotFile;
        public int ClickResults;
        public int ObservedReactions;
        // A placeholder the template never filled in would leave the assistant
        // reading a request with a hole in it, so a missing one is reported
        // instead of being written out as literal braces.
        public List<string> TemplateProblems = new List<string>();
    }

    public static class RequestBuilder
    {
        public const int ElementLimit = 250;
        private const int ClickLimit = 60;

        // Without these the request does not say what to do, what to answer
        // with, or what was attached.
        private static readonly string[] RequiredPlaceholders = new string[]
        {
            "{goal}", "{target}", "{attachments}", "{actions}", "{valueActions}", "{example}"
        };

        public static RequestBundle Build(CaseRecord record, ScanResult scan, string scanSummary, string sessionFolder, string goal)
        {
            return Build(record, scan, scanSummary, sessionFolder, goal, null);
        }

        public static RequestBundle Build(CaseRecord record, ScanResult scan, string scanSummary, string sessionFolder, string goal, ScreenLedger ledger)
        {
            RequestBundle bundle = new RequestBundle();
            bundle.Elements = CaseElementTable.Build(scan, ElementLimit);
            bundle.Screens = ledger;
            bundle.ShotFile = record == null ? null : record.ShotFile;
            StringBuilder head = new StringBuilder();
            head.AppendLine("# " + Messages.Text("request-inv-title.txt", "Investigation of the target application"));
            head.AppendLine();
            head.AppendLine("- " + CaseText.Target + ": " + Flat(record == null ? null : record.TargetProcess) + " / " + Flat(record == null ? null : record.TargetTitle) +
                " (pid " + (record == null ? 0 : record.TargetProcessId) + ")");
            head.AppendLine("- " + CaseText.Screenshot + ": " + Flat(FileNameOf(bundle.ShotFile)));
            head.AppendLine("- " + CaseText.SessionFolder + ": " + Flat(sessionFolder));
            head.AppendLine();
            StringBuilder text = new StringBuilder();

            if (!String.IsNullOrWhiteSpace(scanSummary))
            {
                text.AppendLine("## " + Messages.Text("request-inv-scan.txt", "Automatic scan"));
                text.AppendLine();
                text.AppendLine(scanSummary.TrimEnd());
                text.AppendLine();
            }

            text.AppendLine("## " + Messages.Text("request-inv-elements.txt", "Parts on screen"));
            text.AppendLine();
            AppendElementNote(text, bundle.Elements);
            text.AppendLine();
            text.AppendLine("| id | screen | " + Messages.Text("request-col-type.txt", "type") + " | " + Messages.Text("request-col-name.txt", "name") +
                " | AutomationId | class | ctrlId | HWND | " + Messages.Text("request-col-rect.txt", "position") + " | " +
                Messages.Text("request-col-patterns.txt", "patterns") + " | " + Messages.Text("request-col-source.txt", "read from") + " | " +
                Messages.Text("request-col-state.txt", "state") + " |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            ScanNode[] listed = bundle.Elements.Listed;
            for (int index = 0; index < listed.Length; index++)
            {
                ScanNode node = listed[index];
                text.AppendLine("| " + CaseElementTable.IdOf(node) +
                    " | " + Cell(node.ScreenId) +
                    " | " + Cell(node.DisplayType) +
                    " | " + Cell(node.Name) +
                    " | " + Cell(node.AutomationId) +
                    " | " + Cell(node.ClassName) +
                    " | " + (node.CtrlId == 0 ? "-" : node.CtrlId.ToString(CultureInfo.InvariantCulture)) +
                    " | " + (node.Hwnd == 0 ? "-" : "0x" + node.Hwnd.ToString("X")) +
                    " | " + (node.Rect == null ? "-" : node.Rect.X + "," + node.Rect.Y + " " + node.Rect.Width + "x" + node.Rect.Height) +
                    " | " + Cell(node.Patterns == null || node.Patterns.Length == 0 ? null : String.Join(",", node.Patterns)) +
                    " | " + Cell(String.Join("+", node.Sources.ToArray())) +
                    " | " + Cell(StateText(node)) + " |");
            }
            text.AppendLine();

            if (scan != null)
            {
                text.AppendLine("## " + Messages.Text("request-inv-gaps.txt", "What could not be obtained"));
                text.AppendLine();
                for (int index = 0; index < scan.Coverage.Count; index++)
                {
                    ScanCoverage coverage = scan.Coverage[index];
                    text.AppendLine("- [" + coverage.Provider + "] " + coverage.State + " / " + coverage.NodeCount + " " + Messages.Text("count-items.txt", "items"));
                    for (int reason = 0; reason < coverage.Reasons.Count; reason++)
                    {
                        text.AppendLine("    - " + coverage.Reasons[reason].Code + ": " + coverage.Reasons[reason].Message);
                    }
                }
                for (int index = 0; index < scan.Unknowns.Count; index++) text.AppendLine("- ? " + scan.Unknowns[index]);
                text.AppendLine();
                text.AppendLine(Messages.Text("scan-summary-honesty.txt", "This list covers what the providers exposed while the scan ran."));
                text.AppendLine();
            }

            AppendObservations(text, sessionFolder, bundle);
            bundle.InvestigationBody = text.ToString();
            bundle.Investigation = head.ToString() + bundle.InvestigationBody;
            bundle.Request = BuildRequestText(record, bundle, goal, sessionFolder);
            return bundle;
        }

        // The manual observation log already says which points produced a visible
        // reaction. That is the strongest evidence the assistant can be given, so
        // it is carried over instead of being left in a file nobody opens.
        private static void AppendObservations(StringBuilder text, string sessionFolder, RequestBundle bundle)
        {
            if (String.IsNullOrEmpty(sessionFolder)) return;
            string summaryPath = Path.Combine(sessionFolder, "observation-summary.md");
            string observationPath = Path.Combine(sessionFolder, "observations.jsonl");
            string summary = CaseStore.ReadText(summaryPath);
            List<string> lines = new List<string>();
            string[] raw = null;
            try
            {
                // The observation log is still open for writing, so it has to be
                // read with sharing that tolerates the writer.
                raw = SessionLog.ReadAllLines(observationPath);
            }
            catch
            {
                raw = null;
            }
            if (raw != null)
            {
                for (int index = 0; index < raw.Length && lines.Count < ClickLimit; index++)
                {
                    Dictionary<string, object> item = JsonReader.ReadObject(raw[index]);
                    if (item == null || JsonReader.Text(item, "kind") != "observe.click.result") continue;
                    bundle.ClickResults++;
                    bool observed = JsonReader.Flag(item, "observed", false);
                    if (observed) bundle.ObservedReactions++;
                    Dictionary<string, object> before = JsonReader.Child(item, "before");
                    object[] changes = JsonReader.Items(item, "changes");
                    StringBuilder change = new StringBuilder();
                    if (changes != null)
                    {
                        for (int part = 0; part < changes.Length; part++)
                        {
                            if (change.Length != 0) change.Append(", ");
                            change.Append(Convert.ToString(changes[part], CultureInfo.InvariantCulture));
                        }
                    }
                    lines.Add("| " + JsonReader.Number(item, "x", 0) + "," + JsonReader.Number(item, "y", 0) +
                        " | " + Cell(Label(before)) +
                        " | " + Cell(JsonReader.Text(before, "automationId")) +
                        " | " + (observed ? Messages.Text("request-reacted.txt", "reacted") : Messages.Text("request-noreaction.txt", "no change seen")) +
                        " | " + Cell(change.Length == 0 ? null : change.ToString()) + " |");
                }
            }
            if (summary == null && lines.Count == 0) return;
            text.AppendLine("## " + Messages.Text("request-inv-observed.txt", "What happened when the operator used it"));
            text.AppendLine();
            if (!String.IsNullOrWhiteSpace(summary))
            {
                text.AppendLine(summary.TrimEnd());
                text.AppendLine();
            }
            if (lines.Count == 0) return;
            text.AppendLine("| " + Messages.Text("request-col-point.txt", "point") + " | " + Messages.Text("request-col-clicked.txt", "part clicked") +
                " | AutomationId | " + Messages.Text("request-col-reaction.txt", "reaction") + " | " + Messages.Text("request-col-change.txt", "what changed") + " |");
            text.AppendLine("|---|---|---|---|---|");
            for (int index = 0; index < lines.Count; index++) text.AppendLine(lines[index]);
            if (bundle.ClickResults > lines.Count)
            {
                text.AppendLine();
                text.AppendLine(Messages.Text("request-clicks-trimmed.txt", "Only the first clicks are shown here.") + " " +
                    lines.Count + " / " + bundle.ClickResults);
            }
            text.AppendLine();
        }

        // Called again once the attachments exist, so the request that reaches
        // the assistant lists the files that were really written.
        public static string Recompose(CaseRecord record, RequestBundle bundle, string goal, string sessionFolder)
        {
            if (bundle == null) return null;
            bundle.TemplateProblems.Clear();
            bundle.Request = BuildRequestText(record, bundle, goal, sessionFolder);
            return bundle.Request;
        }

        private static string BuildRequestText(CaseRecord record, RequestBundle bundle, string goal, string sessionFolder)
        {
            string template = Messages.Text("request-template.txt", DefaultTemplate());
            for (int index = 0; index < RequiredPlaceholders.Length; index++)
            {
                if (template.IndexOf(RequiredPlaceholders[index], StringComparison.Ordinal) < 0)
                {
                    bundle.TemplateProblems.Add(Messages.Text("request-template-missing.txt",
                        "The request wording is missing a field it has to fill in:") + " " + RequiredPlaceholders[index]);
                }
            }
            string target = Flat(record == null ? null : record.TargetProcess) + " / " + Flat(record == null ? null : record.TargetTitle) +
                " (pid " + (record == null ? 0 : record.TargetProcessId) + ")";
            int screens = bundle.Handoff != null ? bundle.Handoff.PageCount
                : (bundle.Screens == null ? 0 : bundle.Screens.Screens.Count);
            return template
                .Replace("{goal}", String.IsNullOrWhiteSpace(goal) ? "-" : goal.Trim())
                .Replace("{target}", target)
                .Replace("{attachments}", HandoffBuilder.Attachments(bundle.Handoff).TrimEnd())
                .Replace("{textFile}", HandoffBuilder.TextFileName)
                .Replace("{pdfFile}", HandoffBuilder.PdfFileName)
                .Replace("{screenCount}", screens.ToString(CultureInfo.InvariantCulture))
                .Replace("{shotFile}", Flat(FileNameOf(bundle.ShotFile)))
                .Replace("{investigationFile}", HandoffBuilder.TextFileName)
                .Replace("{elementCount}", bundle.Elements.ListedCount.ToString(CultureInfo.InvariantCulture))
                .Replace("{totalCount}", bundle.Elements.TotalCount.ToString(CultureInfo.InvariantCulture))
                .Replace("{actions}", String.Join(" / ", PlanFormat.Actions))
                .Replace("{valueActions}", String.Join(" / ", PlanFormat.ValueActions))
                .Replace("{example}", PlanFormat.Example())
                .Replace("{caseFolder}", record == null ? "-" : record.Folder)
                .Replace("{sessionFolder}", Flat(sessionFolder));
        }

        private static void AppendElementNote(StringBuilder text, CaseElementTable table)
        {
            text.AppendLine(Messages.Text("request-elements-note.txt", "Rows written here out of everything the scan recorded:") + " " +
                table.ListedCount + " / " + table.TotalCount);
            List<string> dropped = new List<string>();
            if (table.DroppedDecoration > 0) dropped.Add(Messages.Text("request-drop-decoration.txt", "window frame parts") + " " + table.DroppedDecoration);
            if (table.DroppedOffscreen > 0) dropped.Add(Messages.Text("request-drop-offscreen.txt", "not visible now") + " " + table.DroppedOffscreen);
            if (table.DroppedNoRect > 0) dropped.Add(Messages.Text("request-drop-norect.txt", "no position") + " " + table.DroppedNoRect);
            if (table.DroppedOverLimit > 0) dropped.Add(Messages.Text("request-drop-limit.txt", "over the row limit") + " " + table.DroppedOverLimit);
            if (dropped.Count == 0) return;
            text.AppendLine();
            text.AppendLine(Messages.Text("request-drop-heading.txt", "Left out of the table:") + " " + String.Join(" / ", dropped.ToArray()));
        }

        private static string Label(Dictionary<string, object> element)
        {
            if (element == null) return null;
            string type = JsonReader.Text(element, "localizedControlType");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "controlType");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "role");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "className");
            string name = JsonReader.Text(element, "name");
            if (String.IsNullOrEmpty(name)) return type;
            return String.IsNullOrEmpty(type) ? name : type + " \"" + name + "\"";
        }

        private static string StateText(ScanNode node)
        {
            List<string> parts = new List<string>();
            if (node.Enabled == false) parts.Add(Messages.Text("request-state-disabled.txt", "disabled"));
            if (node.KeyboardFocusable == true) parts.Add(Messages.Text("request-state-focusable.txt", "focusable"));
            if (node.IsPassword == true) parts.Add(Messages.Text("request-state-password.txt", "password"));
            return parts.Count == 0 ? null : String.Join(" ", parts.ToArray());
        }

        private static string FileNameOf(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        private static string Flat(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static string Cell(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            string flat = value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 80 ? flat : flat.Substring(0, 79) + "...";
        }

        // Used only when the message asset is missing, so it stays ASCII and
        // still describes the format completely.
        private static string DefaultTemplate()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("I am investigating a Windows application and want to try operating it.");
            text.AppendLine();
            text.AppendLine("What I want to do:");
            text.AppendLine("{goal}");
            text.AppendLine();
            text.AppendLine("Target: {target}");
            text.AppendLine();
            text.AppendLine("Attached ({elementCount} of {totalCount} parts, {screenCount} screens):");
            text.AppendLine("{attachments}");
            text.AppendLine();
            text.AppendLine("Answer with one JSON object and nothing else that could be mistaken for it.");
            text.AppendLine("Every key and every action value is ASCII English from these lists; only");
            text.AppendLine("title, notes, expect and why may quote the target's own wording.");
            text.AppendLine("Allowed action values: {actions}");
            text.AppendLine("These actions need a value field: {valueActions}");
            text.AppendLine("Point at a part with {\"element\":\"E12\"} using an id from the table, or with");
            text.AppendLine("{\"point\":{\"x\":100,\"y\":200}} in screen coordinates when the table has no row for it.");
            text.AppendLine("Do not invent ids that are not in the table.");
            text.AppendLine();
            text.AppendLine("{example}");
            return text.ToString();
        }
    }
}
