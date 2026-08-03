namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // One observed element. Every field that could not be obtained stays null so
    // that "not exposed by the target" is never printed as a confident value.
    public sealed class ScanNode
    {
        public int NodeId;
        public int ParentId = -1;
        public int Depth;
        // Which screen of the target this part was found on. The scan walks one
        // top level window at a time, and that window is what a reader calls a
        // screen, so the picture taken of it and the parts listed under it carry
        // the same id.
        public string ScreenId;
        public string Provider;
        public List<string> Sources = new List<string>();
        public long Hwnd;
        public long TopHwnd;
        public int ProcessId;
        public string Name;
        public string AutomationId;
        public string ControlType;
        public string LocalizedControlType;
        public string ClassName;
        public string RealClassName;
        public string FrameworkId;
        public string Role;
        public string StateText;
        public int CtrlId;
        public string RuntimeId;
        public RectValue Rect;
        public bool? Visible;
        public bool? Enabled;
        public bool? Offscreen;
        public bool? KeyboardFocusable;
        public bool? IsPassword;
        public bool? IsControlElement;
        public bool? IsContentElement;
        public string HelpText;
        public string AcceleratorKey;
        public string AccessKey;
        public long Style;
        public long ExStyle;
        public int ZIndex;
        public int SiblingIndex;
        public int IndexAmongSameType;
        public string[] Patterns;
        public string Path;
        // Position inside the provider list this node came from. Merging
        // renumbers NodeId, so the original link is kept to rebuild the tree.
        public int LocalId = -1;
        public int LocalParentId = -1;
        public bool Decoration;
        public int ValueLength = -1;
        public string ValueKind;
        public List<string> Notes = new List<string>();

        public string Label
        {
            get
            {
                string type = !String.IsNullOrEmpty(ControlType) ? ControlType : (!String.IsNullOrEmpty(Role) ? Role : (!String.IsNullOrEmpty(ClassName) ? ClassName : "?"));
                string name = !String.IsNullOrEmpty(Name) ? Name : null;
                return name == null ? type : type + " \"" + name + "\"";
            }
        }

        // What a reader sees. The operating system already localises both the
        // UI Automation and the MSAA names, so the same kind of part is one
        // line in a summary instead of two spellings of it.
        public string DisplayType
        {
            get
            {
                if (!String.IsNullOrEmpty(LocalizedControlType)) return LocalizedControlType;
                if (!String.IsNullOrEmpty(Role)) return Role;
                if (!String.IsNullOrEmpty(ControlType)) return ControlType;
                return String.IsNullOrEmpty(ClassName) ? "?" : ClassName;
            }
        }

        public string DisplayLabel
        {
            get { return String.IsNullOrEmpty(Name) ? DisplayType : DisplayType + " \"" + Name + "\""; }
        }

        public void AddSource(string source)
        {
            if (String.IsNullOrEmpty(source)) return;
            for (int index = 0; index < Sources.Count; index++) if (Sources[index] == source) return;
            Sources.Add(source);
        }

        public void AddNote(string note)
        {
            if (String.IsNullOrEmpty(note)) return;
            for (int index = 0; index < Notes.Count; index++) if (Notes[index] == note) return;
            Notes.Add(note);
        }
    }

    public sealed class ScanLimits
    {
        public int MaxNodes = 4000;
        public int MaxDepth = 32;
        public int UiaBudgetMs = 25000;
        public int MsaaBudgetMs = 10000;
        public int HitTestBudgetMs = 12000;
        public int Win32BudgetMs = 6000;
        public bool IncludeOffscreen;
        // auto: sample coordinates only when the accessibility trees stayed thin.
        public string HitTest = "auto";
    }

    public sealed class ScanCoverage
    {
        public string Provider;
        public string State = "unknown";
        public int NodeCount;
        public int DurationMs;
        public bool Truncated;
        public List<StatusReason> Reasons = new List<StatusReason>();

        public void AddReason(string code, string message)
        {
            for (int index = 0; index < Reasons.Count; index++) if (Reasons[index].Code == code) return;
            Reasons.Add(new StatusReason(code, message));
        }
    }

    public sealed class ScanWindowResult
    {
        public string ScreenId;
        public long Hwnd;
        public string Title;
        public string ClassName;
        public RectValue Rect;
        public List<ScanNode> Nodes = new List<ScanNode>();
        public List<ScanCoverage> Coverage = new List<ScanCoverage>();
    }

    public sealed class ScanResult
    {
        public string ScanId;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? EndedAt;
        public int ProcessId;
        public string ProcessName;
        public int DurationMs;
        public List<ScanWindowResult> Windows = new List<ScanWindowResult>();
        public List<ScanNode> Nodes = new List<ScanNode>();
        public List<ScanCoverage> Coverage = new List<ScanCoverage>();
        public List<string> Unknowns = new List<string>();
        public bool Cancelled;

        public ScanCoverage CoverageFor(string provider)
        {
            for (int index = 0; index < Coverage.Count; index++) if (Coverage[index].Provider == provider) return Coverage[index];
            ScanCoverage created = new ScanCoverage();
            created.Provider = provider;
            Coverage.Add(created);
            return created;
        }

        public void AddUnknown(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            for (int index = 0; index < Unknowns.Count; index++) if (Unknowns[index] == text) return;
            Unknowns.Add(text);
        }
    }

    public static class ScanNodeWire
    {
        public static Dictionary<string, object> ToWire(ScanNode node)
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            value["nodeId"] = node.NodeId;
            value["parentId"] = node.ParentId;
            value["depth"] = node.Depth;
            value["provider"] = node.Provider;
            value["hwnd"] = node.Hwnd;
            value["topHwnd"] = node.TopHwnd;
            value["processId"] = node.ProcessId;
            value["name"] = node.Name;
            value["automationId"] = node.AutomationId;
            value["controlType"] = node.ControlType;
            value["localizedControlType"] = node.LocalizedControlType;
            value["className"] = node.ClassName;
            value["frameworkId"] = node.FrameworkId;
            value["role"] = node.Role;
            value["stateText"] = node.StateText;
            value["runtimeId"] = node.RuntimeId;
            value["visible"] = node.Visible;
            value["enabled"] = node.Enabled;
            value["offscreen"] = node.Offscreen;
            value["keyboardFocusable"] = node.KeyboardFocusable;
            value["isPassword"] = node.IsPassword;
            value["isControlElement"] = node.IsControlElement;
            value["isContentElement"] = node.IsContentElement;
            value["helpText"] = node.HelpText;
            value["acceleratorKey"] = node.AcceleratorKey;
            value["accessKey"] = node.AccessKey;
            value["siblingIndex"] = node.SiblingIndex;
            value["indexAmongSameType"] = node.IndexAmongSameType;
            value["patterns"] = node.Patterns;
            value["decoration"] = node.Decoration;
            value["valueLength"] = node.ValueLength;
            value["valueKind"] = node.ValueKind;
            value["notes"] = node.Notes.ToArray();
            if (node.Rect != null)
            {
                Dictionary<string, object> rect = new Dictionary<string, object>();
                rect["x"] = node.Rect.X;
                rect["y"] = node.Rect.Y;
                rect["width"] = node.Rect.Width;
                rect["height"] = node.Rect.Height;
                value["rect"] = rect;
            }
            return value;
        }

        public static ScanNode FromWire(Dictionary<string, object> value)
        {
            ScanNode node = new ScanNode();
            node.NodeId = WireValue.Int(value, "nodeId");
            node.ParentId = WireValue.IntOr(value, "parentId", -1);
            node.Depth = WireValue.Int(value, "depth");
            node.Provider = WireValue.Text(value, "provider");
            node.Hwnd = WireValue.Long(value, "hwnd");
            node.TopHwnd = WireValue.Long(value, "topHwnd");
            node.ProcessId = WireValue.Int(value, "processId");
            node.Name = WireValue.Text(value, "name");
            node.AutomationId = WireValue.Text(value, "automationId");
            node.ControlType = WireValue.Text(value, "controlType");
            node.LocalizedControlType = WireValue.Text(value, "localizedControlType");
            node.ClassName = WireValue.Text(value, "className");
            node.FrameworkId = WireValue.Text(value, "frameworkId");
            node.Role = WireValue.Text(value, "role");
            node.StateText = WireValue.Text(value, "stateText");
            node.RuntimeId = WireValue.Text(value, "runtimeId");
            node.Visible = WireValue.Flag(value, "visible");
            node.Enabled = WireValue.Flag(value, "enabled");
            node.Offscreen = WireValue.Flag(value, "offscreen");
            node.KeyboardFocusable = WireValue.Flag(value, "keyboardFocusable");
            node.IsPassword = WireValue.Flag(value, "isPassword");
            node.IsControlElement = WireValue.Flag(value, "isControlElement");
            node.IsContentElement = WireValue.Flag(value, "isContentElement");
            node.HelpText = WireValue.Text(value, "helpText");
            node.AcceleratorKey = WireValue.Text(value, "acceleratorKey");
            node.AccessKey = WireValue.Text(value, "accessKey");
            node.SiblingIndex = WireValue.Int(value, "siblingIndex");
            node.IndexAmongSameType = WireValue.Int(value, "indexAmongSameType");
            node.Patterns = WireValue.TextArray(value, "patterns");
            node.Decoration = WireValue.Bool(value, "decoration");
            node.ValueLength = WireValue.IntOr(value, "valueLength", -1);
            node.ValueKind = WireValue.Text(value, "valueKind");
            string[] notes = WireValue.TextArray(value, "notes");
            if (notes != null) for (int index = 0; index < notes.Length; index++) node.AddNote(notes[index]);
            Dictionary<string, object> rect = WireValue.Object(value, "rect");
            if (rect != null)
            {
                node.Rect = new RectValue();
                node.Rect.X = WireValue.Int(rect, "x");
                node.Rect.Y = WireValue.Int(rect, "y");
                node.Rect.Width = WireValue.Int(rect, "width");
                node.Rect.Height = WireValue.Int(rect, "height");
            }
            node.AddSource(node.Provider);
            return node;
        }
    }

    public static class WireValue
    {
        public static string Text(Dictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            string text = Convert.ToString(item, CultureInfo.InvariantCulture);
            return String.IsNullOrEmpty(text) ? null : text;
        }

        public static int Int(Dictionary<string, object> value, string key)
        {
            return IntOr(value, key, 0);
        }

        public static int IntOr(Dictionary<string, object> value, string key, int fallback)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return fallback;
            try { return Convert.ToInt32(item, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static long Long(Dictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return 0;
            try { return Convert.ToInt64(item, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static bool Bool(Dictionary<string, object> value, string key)
        {
            bool? flag = Flag(value, key);
            return flag.HasValue && flag.Value;
        }

        public static bool? Flag(Dictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            try { return Convert.ToBoolean(item, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item)) return null;
            return item as Dictionary<string, object>;
        }

        public static string[] TextArray(Dictionary<string, object> value, string key)
        {
            object item;
            if (value == null || !value.TryGetValue(key, out item) || item == null) return null;
            object[] source = item as object[];
            if (source == null)
            {
                System.Collections.IEnumerable sequence = item as System.Collections.IEnumerable;
                if (sequence == null || item is string) return null;
                List<string> collected = new List<string>();
                foreach (object entry in sequence) collected.Add(Convert.ToString(entry, CultureInfo.InvariantCulture));
                return collected.ToArray();
            }
            string[] result = new string[source.Length];
            for (int index = 0; index < source.Length; index++) result[index] = Convert.ToString(source[index], CultureInfo.InvariantCulture);
            return result;
        }
    }

    // Window handles are enumerated in the host process: it needs no COM, cannot
    // hang on a busy target, and covers controls that expose no accessibility
    // object at all.
    public static class Win32Scan
    {
        public static List<ScanNode> Enumerate(IntPtr topWindow, int processId, ScanLimits limits, ScanCoverage coverage)
        {
            List<ScanNode> nodes = new List<ScanNode>();
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            coverage.Provider = "win32";
            coverage.State = "ok";
            Dictionary<long, int> byHandle = new Dictionary<long, int>();
            List<IntPtr> pending = new List<IntPtr>();
            List<int> pendingDepth = new List<int>();
            List<int> pendingParent = new List<int>();
            pending.Add(topWindow);
            pendingDepth.Add(0);
            pendingParent.Add(-1);
            int cursor = 0;
            while (cursor < pending.Count)
            {
                if (nodes.Count >= limits.MaxNodes)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-MAXNODES", "The window enumeration stopped at the " + limits.MaxNodes + " element limit.");
                    break;
                }
                if (watch.ElapsedMilliseconds > limits.Win32BudgetMs)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-TIMEOUT", "The window enumeration stopped after " + limits.Win32BudgetMs + " ms.");
                    break;
                }
                IntPtr window = pending[cursor];
                int depth = pendingDepth[cursor];
                int parent = pendingParent[cursor];
                cursor++;
                if (byHandle.ContainsKey(window.ToInt64())) continue;
                ScanNode node = Describe(window, depth, parent, topWindow.ToInt64(), processId);
                if (node == null) continue;
                node.NodeId = nodes.Count;
                nodes.Add(node);
                byHandle[node.Hwnd] = node.NodeId;
                if (depth >= limits.MaxDepth) continue;
                List<IntPtr> children = Children(window);
                for (int index = 0; index < children.Count; index++)
                {
                    pending.Add(children[index]);
                    pendingDepth.Add(depth + 1);
                    pendingParent.Add(node.NodeId);
                }
            }
            watch.Stop();
            coverage.NodeCount = nodes.Count;
            coverage.DurationMs = (int)watch.ElapsedMilliseconds;
            return nodes;
        }

        private static ScanNode Describe(IntPtr window, int depth, int parentId, long topHwnd, int processId)
        {
            ScanNode node = new ScanNode();
            node.Provider = "win32";
            node.AddSource("win32");
            node.Depth = depth;
            node.ParentId = parentId;
            node.Hwnd = window.ToInt64();
            node.TopHwnd = topHwnd;
            node.ProcessId = processId;
            StringBuilder className = new StringBuilder(512);
            NativeMethods.GetClassName(window, className, className.Capacity);
            node.ClassName = className.ToString();
            StringBuilder realClass = new StringBuilder(512);
            NativeMethods.RealGetWindowClass(window, realClass, (uint)realClass.Capacity);
            node.RealClassName = realClass.ToString();
            StringBuilder caption = new StringBuilder(1024);
            NativeMethods.GetWindowText(window, caption, caption.Capacity);
            string text = caption.ToString();
            node.Name = String.IsNullOrEmpty(text) ? null : text;
            node.CtrlId = NativeMethods.GetDlgCtrlID(window);
            node.Style = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_STYLE);
            node.ExStyle = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_EXSTYLE);
            node.Visible = NativeMethods.IsWindowVisible(window);
            node.Enabled = NativeMethods.IsWindowEnabled(window);
            node.Rect = WindowTools.GetPhysicalRect(window);
            node.ControlType = null;
            return node;
        }

        private static List<IntPtr> Children(IntPtr parent)
        {
            List<IntPtr> children = new List<IntPtr>();
            IntPtr child = NativeMethods.GetWindow(parent, NativeMethods.GW_CHILD);
            int guard = 0;
            while (child != IntPtr.Zero && guard < 4000)
            {
                guard++;
                children.Add(child);
                child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
            }
            return children;
        }
    }

    // Nodes coming from different providers are only merged on evidence that
    // cannot be a coincidence. Anything less keeps both rows so the operator can
    // see that two providers disagree.
    public static class ScanMerge
    {
        public static List<ScanNode> Merge(List<ScanNode> uia, List<ScanNode> msaa, List<ScanNode> win32, List<ScanNode> hitTest, ScanResult result)
        {
            List<ScanNode> merged = new List<ScanNode>();
            Dictionary<string, ScanNode> byRuntimeId = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
            Dictionary<string, ScanNode> byGeometry = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
            Dictionary<long, ScanNode> byHandle = new Dictionary<long, ScanNode>();

            // Where every original node ended up, so parent links survive the merge.
            Dictionary<string, ScanNode> owner = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
            AddAll(uia, merged, byRuntimeId, byGeometry, byHandle, owner, true);
            AddAll(hitTest, merged, byRuntimeId, byGeometry, byHandle, owner, true);
            AddAll(msaa, merged, byRuntimeId, byGeometry, byHandle, owner, false);
            AddAll(win32, merged, byRuntimeId, byGeometry, byHandle, owner, false);

            for (int index = 0; index < merged.Count; index++) merged[index].NodeId = index;
            for (int index = 0; index < merged.Count; index++)
            {
                ScanNode node = merged[index];
                ScanNode parent;
                if (node.LocalParentId >= 0 && owner.TryGetValue(node.Provider + ":" + node.LocalParentId, out parent) && parent != node)
                {
                    node.ParentId = parent.NodeId;
                }
                else
                {
                    node.ParentId = -1;
                }
            }
            // With the parent links remapped, the merged list is one tree and the
            // readable path can be built from it.
            ScanPaths.Build(merged);
            if (result != null)
            {
                int mergedCount = Count(uia) + Count(msaa) + Count(win32) + Count(hitTest) - merged.Count;
                if (mergedCount > 0) result.AddUnknown("duplicate-merged=" + mergedCount);
            }
            return merged;
        }

        private static int Count(List<ScanNode> nodes)
        {
            return nodes == null ? 0 : nodes.Count;
        }

        private static void AddAll(List<ScanNode> source, List<ScanNode> merged, Dictionary<string, ScanNode> byRuntimeId, Dictionary<string, ScanNode> byGeometry, Dictionary<long, ScanNode> byHandle, Dictionary<string, ScanNode> owner, bool geometryOwner)
        {
            if (source == null) return;
            for (int index = 0; index < source.Count; index++)
            {
                ScanNode node = source[index];
                node.LocalId = node.NodeId;
                node.LocalParentId = node.ParentId;
                string localKey = node.Provider + ":" + node.LocalId;
                ScanNode existing = FindExisting(node, byRuntimeId, byGeometry, byHandle);
                if (existing != null)
                {
                    Absorb(existing, node);
                    owner[localKey] = existing;
                    continue;
                }
                merged.Add(node);
                owner[localKey] = node;
                Register(node, byRuntimeId, byGeometry, byHandle, geometryOwner);
            }
        }

        private static ScanNode FindExisting(ScanNode node, Dictionary<string, ScanNode> byRuntimeId, Dictionary<string, ScanNode> byGeometry, Dictionary<long, ScanNode> byHandle)
        {
            ScanNode found;
            if (!String.IsNullOrEmpty(node.RuntimeId) && byRuntimeId.TryGetValue(node.RuntimeId, out found)) return found;
            string geometry = GeometryKey(node);
            if (geometry != null && byGeometry.TryGetValue(geometry, out found)) return found;
            // A window handle alone is only decisive when the candidate is a bare
            // window record; accessibility nodes often share the client handle.
            if (node.Provider == "win32" && node.Hwnd != 0 && byHandle.TryGetValue(node.Hwnd, out found)) return found;
            return null;
        }

        private static void Register(ScanNode node, Dictionary<string, ScanNode> byRuntimeId, Dictionary<string, ScanNode> byGeometry, Dictionary<long, ScanNode> byHandle, bool geometryOwner)
        {
            if (!String.IsNullOrEmpty(node.RuntimeId)) byRuntimeId[node.RuntimeId] = node;
            string geometry = GeometryKey(node);
            if (geometry != null && geometryOwner && !byGeometry.ContainsKey(geometry)) byGeometry[geometry] = node;
            if (node.Hwnd != 0 && !byHandle.ContainsKey(node.Hwnd)) byHandle[node.Hwnd] = node;
        }

        private static string GeometryKey(ScanNode node)
        {
            if (node.Rect == null || node.Rect.Width <= 0 || node.Rect.Height <= 0) return null;
            string name = node.Name == null ? String.Empty : node.Name;
            return node.Rect.X + ":" + node.Rect.Y + ":" + node.Rect.Width + ":" + node.Rect.Height + ":" + name;
        }

        private static void Absorb(ScanNode target, ScanNode extra)
        {
            for (int index = 0; index < extra.Sources.Count; index++) target.AddSource(extra.Sources[index]);
            target.AddSource(extra.Provider);
            if (String.IsNullOrEmpty(target.Name)) target.Name = extra.Name;
            if (String.IsNullOrEmpty(target.ClassName)) target.ClassName = extra.ClassName;
            if (String.IsNullOrEmpty(target.RealClassName)) target.RealClassName = extra.RealClassName;
            if (String.IsNullOrEmpty(target.Role)) target.Role = extra.Role;
            if (String.IsNullOrEmpty(target.StateText)) target.StateText = extra.StateText;
            if (String.IsNullOrEmpty(target.ControlType)) target.ControlType = extra.ControlType;
            if (String.IsNullOrEmpty(target.LocalizedControlType)) target.LocalizedControlType = extra.LocalizedControlType;
            if (String.IsNullOrEmpty(target.AutomationId)) target.AutomationId = extra.AutomationId;
            if (String.IsNullOrEmpty(target.FrameworkId)) target.FrameworkId = extra.FrameworkId;
            if (String.IsNullOrEmpty(target.RuntimeId)) target.RuntimeId = extra.RuntimeId;
            if (target.Hwnd == 0) target.Hwnd = extra.Hwnd;
            if (target.CtrlId == 0) target.CtrlId = extra.CtrlId;
            if (target.Style == 0) target.Style = extra.Style;
            if (target.ExStyle == 0) target.ExStyle = extra.ExStyle;
            if (target.Rect == null) target.Rect = extra.Rect;
            if (!target.Visible.HasValue) target.Visible = extra.Visible;
            if (!target.Enabled.HasValue) target.Enabled = extra.Enabled;
            if (!target.Offscreen.HasValue) target.Offscreen = extra.Offscreen;
            if (!target.KeyboardFocusable.HasValue) target.KeyboardFocusable = extra.KeyboardFocusable;
            if (!target.IsPassword.HasValue) target.IsPassword = extra.IsPassword;
            if (target.Patterns == null) target.Patterns = extra.Patterns;
            if (target.Decoration && !extra.Decoration) target.Decoration = false;
            if (target.ValueLength < 0) target.ValueLength = extra.ValueLength;
            if (String.IsNullOrEmpty(target.ValueKind)) target.ValueKind = extra.ValueKind;
            if (String.IsNullOrEmpty(target.Path)) target.Path = extra.Path;
            for (int index = 0; index < extra.Notes.Count; index++) target.AddNote(extra.Notes[index]);
        }
    }

    // The readable hierarchy path is built while the provider list is still
    // intact, because that is the only place the parent links are unambiguous.
    public static class ScanPaths
    {
        public static void Build(List<ScanNode> nodes)
        {
            if (nodes == null) return;
            for (int index = 0; index < nodes.Count; index++)
            {
                List<string> parts = new List<string>();
                ScanNode current = nodes[index];
                int guard = 0;
                while (current != null && guard < 40)
                {
                    guard++;
                    parts.Add(current.Label);
                    int parentId = current.ParentId;
                    if (parentId < 0 || parentId >= nodes.Count) break;
                    ScanNode parent = nodes[parentId];
                    if (parent == current) break;
                    current = parent;
                }
                parts.Reverse();
                StringBuilder path = new StringBuilder();
                for (int part = 0; part < parts.Count; part++)
                {
                    if (part != 0) path.Append(" > ");
                    path.Append(parts[part]);
                }
                nodes[index].Path = path.ToString();
            }
        }
    }

    public static class ScanJson
    {
        public static JsonObject Node(ScanNode node, string scanId, long topHwnd)
        {
            return new JsonObject()
                .Add("kind", "element")
                .Add("scanId", scanId)
                .Add("screenId", node.ScreenId)
                .Add("componentId", "E" + node.NodeId.ToString(CultureInfo.InvariantCulture))
                .Add("nodeId", node.NodeId)
                .Add("parentId", node.ParentId)
                .Add("depth", node.Depth)
                .Add("sources", SourceArray(node))
                .Add("processId", node.ProcessId)
                .Add("topHwnd", topHwnd == 0 ? node.TopHwnd : topHwnd)
                .Add("hwnd", node.Hwnd)
                .Add("hasHwnd", node.Hwnd != 0)
                .Add("controlType", node.ControlType)
                .Add("localizedControlType", node.LocalizedControlType)
                .Add("role", node.Role)
                .Add("name", node.Name)
                .Add("automationId", node.AutomationId)
                .Add("className", node.ClassName)
                .Add("realClassName", node.RealClassName)
                .Add("frameworkId", node.FrameworkId)
                .Add("ctrlId", node.CtrlId)
                .Add("runtimeId", node.RuntimeId)
                .Add("rect", SessionLogJson.Rect(node.Rect))
                .Add("visible", node.Visible)
                .Add("enabled", node.Enabled)
                .Add("offscreen", node.Offscreen)
                .Add("keyboardFocusable", node.KeyboardFocusable)
                .Add("isPassword", node.IsPassword)
                .Add("isControlElement", node.IsControlElement)
                .Add("isContentElement", node.IsContentElement)
                .Add("helpText", node.HelpText)
                .Add("acceleratorKey", node.AcceleratorKey)
                .Add("accessKey", node.AccessKey)
                .Add("style", node.Style)
                .Add("exStyle", node.ExStyle)
                .Add("siblingIndex", node.SiblingIndex)
                .Add("indexAmongSameType", node.IndexAmongSameType)
                .Add("patterns", SessionLogJson.Strings(node.Patterns))
                .Add("decoration", node.Decoration)
                .Add("displayType", node.DisplayType)
                .Add("path", node.Path)
                .Add("valueLength", node.ValueLength < 0 ? null : (object)node.ValueLength)
                .Add("valueKind", node.ValueKind)
                .Add("notes", node.Notes.ToArray());
        }

        public static JsonObject Coverage(ScanCoverage coverage)
        {
            List<object> reasons = new List<object>();
            for (int index = 0; index < coverage.Reasons.Count; index++)
            {
                reasons.Add(new JsonObject().Add("code", coverage.Reasons[index].Code).Add("message", coverage.Reasons[index].Message));
            }
            return new JsonObject()
                .Add("provider", coverage.Provider)
                .Add("state", coverage.State)
                .Add("nodeCount", coverage.NodeCount)
                .Add("durationMs", coverage.DurationMs)
                .Add("truncated", coverage.Truncated)
                .Add("reasons", reasons.ToArray());
        }

        public static JsonObject Summary(ScanResult result)
        {
            List<object> coverage = new List<object>();
            for (int index = 0; index < result.Coverage.Count; index++) coverage.Add(Coverage(result.Coverage[index]));
            List<object> windows = new List<object>();
            for (int index = 0; index < result.Windows.Count; index++)
            {
                ScanWindowResult window = result.Windows[index];
                windows.Add(new JsonObject()
                    .Add("screenId", window.ScreenId)
                    .Add("hwnd", window.Hwnd)
                    .Add("title", window.Title)
                    .Add("className", window.ClassName)
                    .Add("rect", SessionLogJson.Rect(window.Rect))
                    .Add("nodeCount", window.Nodes.Count));
            }
            return new JsonObject()
                .Add("kind", "scan.summary")
                .Add("scanId", result.ScanId)
                .Add("startedAt", result.StartedAt)
                .Add("endedAt", result.EndedAt.HasValue ? (object)result.EndedAt.Value : null)
                .Add("processId", result.ProcessId)
                .Add("processName", result.ProcessName)
                .Add("durationMs", result.DurationMs)
                .Add("elementCount", result.Nodes.Count)
                .Add("windowCount", result.Windows.Count)
                .Add("cancelled", result.Cancelled)
                .Add("windows", windows.ToArray())
                .Add("coverage", coverage.ToArray())
                .Add("unknown", result.Unknowns.ToArray());
        }

        private static object[] SourceArray(ScanNode node)
        {
            if (node.Sources.Count == 0) return new object[] { node.Provider };
            object[] result = new object[node.Sources.Count];
            for (int index = 0; index < node.Sources.Count; index++) result[index] = node.Sources[index];
            return result;
        }
    }
}
