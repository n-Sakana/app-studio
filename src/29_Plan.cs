namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // The shape the assistant has to answer in. The action names are exactly the
    // operation kinds the tool already performs, so nothing new can be asked for
    // and no separate list of permitted operations has to be kept in step.
    public static class PlanFormat
    {
        public const string Marker = "pui-plan";
        public const int Version = 1;

        public static readonly string[] Actions = new string[]
        {
            "read", "focus", "invoke", "toggle", "select", "expand", "setValue", "scroll", "click", "keys"
        };

        public static readonly string[] ValueActions = new string[] { "setValue", "keys" };

        public static readonly string[] StepKeys = new string[]
        {
            "id", "action", "target", "value", "expect", "why"
        };

        public static readonly string[] PlanKeys = new string[]
        {
            "format", "version", "title", "notes", "steps"
        };

        // Spelled out rather than indexed off the array, so reordering either
        // list cannot silently turn one operation into another.
        public static bool TryKind(string action, out ProbeKind kind)
        {
            kind = ProbeKind.Read;
            if (String.IsNullOrWhiteSpace(action)) return false;
            string trimmed = action.Trim();
            if (String.Equals(trimmed, "read", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Read; return true; }
            if (String.Equals(trimmed, "focus", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Focus; return true; }
            if (String.Equals(trimmed, "invoke", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Invoke; return true; }
            if (String.Equals(trimmed, "toggle", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Toggle; return true; }
            if (String.Equals(trimmed, "select", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Select; return true; }
            if (String.Equals(trimmed, "expand", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Expand; return true; }
            if (String.Equals(trimmed, "setValue", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.SetValue; return true; }
            if (String.Equals(trimmed, "scroll", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Scroll; return true; }
            if (String.Equals(trimmed, "click", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Click; return true; }
            if (String.Equals(trimmed, "keys", StringComparison.OrdinalIgnoreCase)) { kind = ProbeKind.Keys; return true; }
            return false;
        }

        public static string ActionName(ProbeKind kind)
        {
            string value = kind.ToString();
            return Char.ToLowerInvariant(value[0]) + value.Substring(1);
        }

        public static bool NeedsValue(string action)
        {
            for (int index = 0; index < ValueActions.Length; index++)
            {
                if (String.Equals(ValueActions[index], action, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsWrite(ProbeKind kind)
        {
            return kind != ProbeKind.Read;
        }

        public static string Example()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("{");
            text.AppendLine("  \"format\": \"" + Marker + "\",");
            text.AppendLine("  \"version\": " + Version + ",");
            text.AppendLine("  \"title\": \"...\",");
            text.AppendLine("  \"notes\": \"...\",");
            text.AppendLine("  \"steps\": [");
            text.AppendLine("    { \"id\": 1, \"action\": \"focus\",    \"target\": { \"element\": \"E12\" }, \"expect\": \"...\", \"why\": \"...\" },");
            text.AppendLine("    { \"id\": 2, \"action\": \"setValue\", \"target\": { \"element\": \"E12\" }, \"value\": \"...\", \"expect\": \"...\" },");
            text.AppendLine("    { \"id\": 3, \"action\": \"invoke\",   \"target\": { \"element\": \"E15\" }, \"expect\": \"...\" },");
            text.AppendLine("    { \"id\": 4, \"action\": \"click\",    \"target\": { \"point\": { \"x\": 100, \"y\": 200 } }, \"expect\": \"...\" }");
            text.AppendLine("  ]");
            text.Append("}");
            return text.ToString();
        }
    }

    public sealed class PlanStep
    {
        public int Id;
        public string Action;
        public ProbeKind Kind;
        public string ElementId;
        public bool HasPoint;
        public int X;
        public int Y;
        public string Value;
        public string Expect;
        public string Why;
        public ScanNode Node;
        public ElementRef Element;
        public string TargetLabel;

        public string Describe()
        {
            string where = ElementId != null ? ElementId + " " + (TargetLabel ?? String.Empty) : "(" + X + "," + Y + ")";
            return Id.ToString(CultureInfo.InvariantCulture) + ". " + Action + "  " + where.Trim() +
                (String.IsNullOrEmpty(Value) ? String.Empty : "  = " + Value);
        }
    }

    public sealed class OperationPlan
    {
        public string Title;
        public string Notes;
        public List<PlanStep> Steps = new List<PlanStep>();
        // A plan is run whole or not at all, so anything wrong is a rejection and
        // every reason is listed rather than the bad step being dropped quietly.
        public List<string> Problems = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Ignored = new List<string>();
        public string Json;
        public bool Accepted { get { return Problems.Count == 0 && Steps.Count > 0; } }
        public bool NeedsWrite
        {
            get
            {
                for (int index = 0; index < Steps.Count; index++) if (PlanFormat.IsWrite(Steps[index].Kind)) return true;
                return false;
            }
        }
    }

    public static class PlanReader
    {
        // A chat answer arrives with prose around it and usually inside a fence.
        // The object itself is found rather than demanding a clean paste, but
        // nothing is repaired: a broken object is reported, not guessed at.
        public static string ExtractJson(string paste)
        {
            if (String.IsNullOrWhiteSpace(paste)) return null;
            string text = paste;
            int fence = text.IndexOf("```", StringComparison.Ordinal);
            while (fence >= 0)
            {
                int lineEnd = text.IndexOf('\n', fence);
                if (lineEnd < 0) break;
                int close = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
                string inner = close < 0 ? text.Substring(lineEnd + 1) : text.Substring(lineEnd + 1, close - lineEnd - 1);
                string found = Balanced(inner);
                if (found != null) return found;
                if (close < 0) break;
                fence = text.IndexOf("```", close + 3, StringComparison.Ordinal);
            }
            return Balanced(text);
        }

        private static string Balanced(string text)
        {
            if (text == null) return null;
            int start = text.IndexOf('{');
            if (start < 0) return null;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int index = start; index < text.Length; index++)
            {
                char character = text[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }
                if (character == '"') inString = true;
                else if (character == '{') depth++;
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, index - start + 1);
                }
            }
            return null;
        }

        public static OperationPlan Parse(string paste, CaseElementTable table)
        {
            OperationPlan plan = new OperationPlan();
            string json = ExtractJson(paste);
            if (json == null)
            {
                plan.Problems.Add(Messages.Text("plan-err-nojson.txt", "No JSON object was found in what was pasted."));
                return plan;
            }
            plan.Json = json;
            Dictionary<string, object> root;
            try
            {
                root = JsonReader.ReadObject(json);
            }
            catch (Exception exception)
            {
                plan.Problems.Add(Messages.Text("plan-err-parse.txt", "The JSON could not be read.") + " " + exception.Message);
                return plan;
            }
            if (root == null)
            {
                plan.Problems.Add(Messages.Text("plan-err-parse.txt", "The JSON could not be read."));
                return plan;
            }

            string marker = JsonReader.Text(root, "format");
            if (!String.Equals(marker, PlanFormat.Marker, StringComparison.OrdinalIgnoreCase))
            {
                plan.Problems.Add(Messages.Text("plan-err-format.txt", "The format field is not the expected one.") +
                    " format=" + (marker ?? "-") + " (" + PlanFormat.Marker + ")");
            }
            // The contract handed to the assistant prints version as a JSON
            // number, so a quoted "1" is not that contract. Reading it with the
            // lenient converter would repair a broken answer by guessing, which
            // is exactly what this reader must not do, so the type is checked
            // the same strict way a point's x and y are.
            bool versionIsNumber = JsonReader.IsNumber(root, "version");
            int version = versionIsNumber ? JsonReader.Number(root, "version", -1) : -1;
            if (version != PlanFormat.Version)
            {
                plan.Problems.Add(Messages.Text("plan-err-version.txt", "The version is not supported.") +
                    " version=" + (version < 0 ? "-" : version.ToString(CultureInfo.InvariantCulture)) + " (" + PlanFormat.Version + ")");
            }
            plan.Title = JsonReader.Text(root, "title");
            plan.Notes = JsonReader.Text(root, "notes");
            NoteUnknown(plan, JsonReader.Keys(root), PlanFormat.PlanKeys, String.Empty);

            object[] steps = JsonReader.Items(root, "steps");
            if (steps == null)
            {
                plan.Problems.Add(Messages.Text("plan-err-nosteps.txt", "There is no steps list."));
                return plan;
            }
            if (steps.Length == 0)
            {
                plan.Problems.Add(Messages.Text("plan-err-emptysteps.txt", "The steps list is empty."));
                return plan;
            }

            for (int index = 0; index < steps.Length; index++)
            {
                string where = Messages.Text("plan-err-step.txt", "step") + " " + (index + 1) + ": ";
                Dictionary<string, object> item = steps[index] as Dictionary<string, object>;
                if (item == null)
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-notobject.txt", "this entry is not an object."));
                    continue;
                }
                NoteUnknown(plan, JsonReader.Keys(item), PlanFormat.StepKeys, where);
                PlanStep step = new PlanStep();
                // id may be left out, in which case the position stands in for
                // it. When the answer does give one it has to be the whole
                // number the contract prints; taking "1", rounding 1.5 or
                // wrapping an overflowing value would be repairing the answer.
                if (JsonReader.Has(item, "id") && !JsonReader.IsWholeNumber(item, "id"))
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-id.txt", "the id has to be a whole number."));
                    continue;
                }
                step.Id = JsonReader.Number(item, "id", index + 1);
                step.Action = JsonReader.Text(item, "action");
                step.Value = JsonReader.Text(item, "value");
                step.Expect = JsonReader.Text(item, "expect");
                step.Why = JsonReader.Text(item, "why");
                ProbeKind kind;
                if (!PlanFormat.TryKind(step.Action, out kind))
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-action.txt", "unknown action.") +
                        " action=" + (step.Action ?? "-"));
                    continue;
                }
                step.Action = PlanFormat.ActionName(kind);
                step.Kind = kind;
                if (PlanFormat.NeedsValue(step.Action) && step.Value == null)
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-value.txt", "this action needs a value field.") + " action=" + step.Action);
                    continue;
                }
                if (!ResolveTarget(plan, step, item, table, where)) continue;
                plan.Steps.Add(step);
            }
            if (plan.Steps.Count == 0 && plan.Problems.Count == 0)
            {
                plan.Problems.Add(Messages.Text("plan-err-emptysteps.txt", "The steps list is empty."));
            }
            return plan;
        }

        private static bool ResolveTarget(OperationPlan plan, PlanStep step, Dictionary<string, object> item, CaseElementTable table, string where)
        {
            Dictionary<string, object> target = JsonReader.Child(item, "target");
            if (target == null)
            {
                plan.Problems.Add(where + Messages.Text("plan-err-notarget.txt", "there is no target."));
                return false;
            }
            string elementId = JsonReader.Text(target, "element");
            Dictionary<string, object> point = JsonReader.Child(target, "point");
            if (elementId != null && point != null)
            {
                plan.Problems.Add(where + Messages.Text("plan-err-bothtarget.txt", "a target names both a part and a point."));
                return false;
            }
            if (elementId != null)
            {
                ScanNode node = table == null ? null : table.Find(elementId);
                if (node == null)
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-noelement.txt", "this id is not in the investigation.") + " " + elementId);
                    return false;
                }
                if (node.Rect == null || node.Rect.Width <= 0 || node.Rect.Height <= 0)
                {
                    plan.Problems.Add(where + Messages.Text("plan-err-norect.txt", "this part has no position, so it cannot be operated.") + " " + elementId);
                    return false;
                }
                step.ElementId = elementId.Trim();
                step.Node = node;
                step.TargetLabel = node.DisplayLabel;
                ElementRef reference = new ElementRef();
                reference.X = node.Rect.X + node.Rect.Width / 2;
                reference.Y = node.Rect.Y + node.Rect.Height / 2;
                reference.Hwnd = node.Hwnd;
                step.Element = reference;
                step.X = reference.X;
                step.Y = reference.Y;
                if (node.IsPassword == true && PlanFormat.NeedsValue(step.Action))
                {
                    plan.Warnings.Add(where + Messages.Text("plan-warn-password.txt", "this is a password field, so the tool will refuse to write to it.") + " " + elementId);
                }
                if (node.Enabled == false)
                {
                    plan.Warnings.Add(where + Messages.Text("plan-warn-disabled.txt", "this part is disabled right now.") + " " + elementId);
                }
                return true;
            }
            if (point == null)
            {
                plan.Problems.Add(where + Messages.Text("plan-err-notarget.txt", "there is no target."));
                return false;
            }
            if (!JsonReader.IsNumber(point, "x") || !JsonReader.IsNumber(point, "y"))
            {
                plan.Problems.Add(where + Messages.Text("plan-err-badpoint.txt", "the point has no numeric x and y."));
                return false;
            }
            int x = JsonReader.Number(point, "x", 0);
            int y = JsonReader.Number(point, "y", 0);
            System.Drawing.Rectangle screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (x < screen.Left || y < screen.Top || x >= screen.Right || y >= screen.Bottom)
            {
                plan.Problems.Add(where + Messages.Text("plan-err-offscreen.txt", "the point is outside every screen.") +
                    " (" + x + "," + y + ")");
                return false;
            }
            step.HasPoint = true;
            step.X = x;
            step.Y = y;
            ElementRef reference2 = new ElementRef();
            reference2.X = x;
            reference2.Y = y;
            reference2.Hwnd = 0;
            step.Element = reference2;
            step.TargetLabel = Messages.Text("plan-target-point.txt", "a point on the screen");
            return true;
        }

        private static void NoteUnknown(OperationPlan plan, string[] present, string[] allowed, string where)
        {
            for (int index = 0; index < present.Length; index++)
            {
                bool known = false;
                for (int check = 0; check < allowed.Length; check++)
                {
                    if (String.Equals(present[index], allowed[check], StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                }
                if (!known) plan.Ignored.Add(where + present[index]);
            }
        }
    }
}
