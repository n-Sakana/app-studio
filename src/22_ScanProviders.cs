namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Automation;

    // The accessibility walks run inside the acquisition worker process. Nothing
    // here moves the pointer, sends input, or invokes a control pattern: every
    // call is a read of what the target already exposes.
    public static class UiaScan
    {
        private sealed class Pending
        {
            internal AutomationElement Element;
            internal int ParentId;
            internal int Depth;
            internal int SiblingIndex;
            internal int IndexAmongSameType;
        }

        public static List<ScanNode> Walk(IntPtr topWindow, ScanLimits limits, int excludeProcessId, ScanCoverage coverage)
        {
            List<ScanNode> nodes = new List<ScanNode>();
            Stopwatch watch = Stopwatch.StartNew();
            coverage.Provider = "uia";
            coverage.State = "ok";
            CacheRequest cache;
            try
            {
                cache = BuildCache();
            }
            catch (Exception exception)
            {
                coverage.State = "unavailable";
                coverage.AddReason("UIA-CACHE", Describe(exception));
                return nodes;
            }
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(topWindow);
            }
            catch (Exception exception)
            {
                coverage.State = "unavailable";
                coverage.AddReason("UIA-FAIL", Describe(exception));
                return nodes;
            }
            if (root == null)
            {
                coverage.State = "unavailable";
                coverage.AddReason("UIA-NOELEMENT", "UI Automation exposes no element for this window handle.");
                return nodes;
            }

            List<Pending> queue = new List<Pending>();
            Pending first = new Pending();
            first.Element = root;
            first.ParentId = -1;
            first.Depth = 0;
            queue.Add(first);
            int cursor = 0;
            int offscreenSkipped = 0;
            int failedNodes = 0;
            while (cursor < queue.Count)
            {
                if (nodes.Count >= limits.MaxNodes)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-MAXNODES", "The UI Automation walk stopped at the " + limits.MaxNodes + " element limit; deeper elements were not read.");
                    break;
                }
                if (watch.ElapsedMilliseconds > limits.UiaBudgetMs)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-TIMEOUT", "The UI Automation walk stopped after " + limits.UiaBudgetMs + " ms; remaining elements were not read.");
                    break;
                }
                Pending pending = queue[cursor];
                cursor++;
                AutomationElement updated;
                try
                {
                    updated = pending.Element.GetUpdatedCache(cache);
                }
                catch (Exception exception)
                {
                    failedNodes++;
                    coverage.State = "partial";
                    coverage.AddReason("UIA-NODEFAIL", "At least one element could not be read: " + Describe(exception));
                    continue;
                }
                if (updated == null) continue;
                ScanNode node = Read(updated, pending, topWindow.ToInt64());
                if (excludeProcessId != 0 && node.ProcessId == excludeProcessId) continue;
                if (node.Offscreen.HasValue && node.Offscreen.Value && !limits.IncludeOffscreen)
                {
                    offscreenSkipped++;
                    continue;
                }
                node.NodeId = nodes.Count;
                nodes.Add(node);
                if (pending.Depth >= limits.MaxDepth)
                {
                    node.AddNote("depth-limit");
                    coverage.Truncated = true;
                    coverage.AddReason("SCAN-MAXDEPTH", "The UI Automation walk stopped at depth " + limits.MaxDepth + " for at least one branch.");
                    continue;
                }
                AutomationElementCollection children = null;
                try
                {
                    children = updated.CachedChildren;
                }
                catch (Exception exception)
                {
                    node.AddNote("children-unavailable");
                    coverage.AddReason("UIA-CHILDREN", "Children of at least one element could not be read: " + Describe(exception));
                }
                if (children == null) continue;
                Dictionary<string, int> typeCounters = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int index = 0; index < children.Count; index++)
                {
                    AutomationElement child = children[index];
                    if (child == null) continue;
                    string type = TypeName(child);
                    int typeIndex;
                    typeCounters.TryGetValue(type, out typeIndex);
                    typeCounters[type] = typeIndex + 1;
                    Pending next = new Pending();
                    next.Element = child;
                    next.ParentId = node.NodeId;
                    next.Depth = pending.Depth + 1;
                    next.SiblingIndex = index;
                    next.IndexAmongSameType = typeIndex;
                    queue.Add(next);
                }
            }
            watch.Stop();
            if (offscreenSkipped > 0)
            {
                coverage.AddReason("SCAN-OFFSCREEN", offscreenSkipped + " element(s) reported IsOffscreen and were left out because only what is currently displayed is recorded.");
            }
            if (failedNodes > 0) coverage.AddReason("UIA-NODEFAIL-COUNT", failedNodes + " element(s) could not be read at all.");
            coverage.NodeCount = nodes.Count;
            coverage.DurationMs = (int)watch.ElapsedMilliseconds;
            if (nodes.Count == 0 && coverage.State == "ok")
            {
                coverage.State = "partial";
                coverage.AddReason("UIA-EMPTYTREE", "The window exposes no UI Automation children; only window-level facts are available from this provider.");
            }
            return nodes;
        }

        private static ScanNode Read(AutomationElement element, Pending pending, long topHwnd)
        {
            ScanNode node = new ScanNode();
            node.Provider = "uia";
            node.AddSource("uia");
            node.ParentId = pending.ParentId;
            node.Depth = pending.Depth;
            node.SiblingIndex = pending.SiblingIndex;
            node.IndexAmongSameType = pending.IndexAmongSameType;
            node.TopHwnd = topHwnd;
            node.Name = Text(element, AutomationElement.NameProperty);
            node.AutomationId = Text(element, AutomationElement.AutomationIdProperty);
            node.ControlType = TypeName(element);
            node.LocalizedControlType = Text(element, AutomationElement.LocalizedControlTypeProperty);
            node.ClassName = Text(element, AutomationElement.ClassNameProperty);
            node.FrameworkId = Text(element, AutomationElement.FrameworkIdProperty);
            node.HelpText = Text(element, AutomationElement.HelpTextProperty);
            node.AcceleratorKey = Text(element, AutomationElement.AcceleratorKeyProperty);
            node.AccessKey = Text(element, AutomationElement.AccessKeyProperty);
            node.Enabled = Flag(element, AutomationElement.IsEnabledProperty);
            node.Offscreen = Flag(element, AutomationElement.IsOffscreenProperty);
            node.KeyboardFocusable = Flag(element, AutomationElement.IsKeyboardFocusableProperty);
            node.IsPassword = Flag(element, AutomationElement.IsPasswordProperty);
            // These two decide whether a raw view member is a real control, so
            // the provider default is wanted rather than "not supplied".
            node.IsControlElement = FlagWithDefault(element, AutomationElement.IsControlElementProperty);
            node.IsContentElement = FlagWithDefault(element, AutomationElement.IsContentElementProperty);
            object processId = Cached(element, AutomationElement.ProcessIdProperty);
            node.ProcessId = processId is int ? (int)processId : 0;
            object handle = Cached(element, AutomationElement.NativeWindowHandleProperty);
            node.Hwnd = handle is int ? (long)(int)handle : 0;
            object rectangle = Cached(element, AutomationElement.BoundingRectangleProperty);
            if (rectangle is Rect)
            {
                Rect value = (Rect)rectangle;
                if (!value.IsEmpty && !Double.IsInfinity(value.Width) && !Double.IsInfinity(value.Height))
                {
                    node.Rect = new RectValue();
                    node.Rect.X = (int)Math.Round(value.X);
                    node.Rect.Y = (int)Math.Round(value.Y);
                    node.Rect.Width = (int)Math.Round(value.Width);
                    node.Rect.Height = (int)Math.Round(value.Height);
                }
            }
            if (node.Rect == null) node.AddNote("no-bounds");
            // Raw view members that are not control elements are template and
            // decoration parts. They stay in the record and stay out of the
            // headline counts.
            node.Decoration = node.IsControlElement.HasValue && !node.IsControlElement.Value;
            node.RuntimeId = RuntimeId(element);
            node.Patterns = Patterns(element);
            // Values are never read during a scan: nothing in the recorded
            // evidence should carry the contents of the operator's screen.
            node.ValueKind = "not-read";
            return node;
        }

        private static string RuntimeId(AutomationElement element)
        {
            object cached = Cached(element, AutomationElement.RuntimeIdProperty);
            int[] values = cached as int[];
            if (values != null) return SessionLogJson.RuntimeIdText(values);
            try
            {
                return SessionLogJson.RuntimeIdText(element.GetRuntimeId());
            }
            catch
            {
                return null;
            }
        }

        private static readonly AutomationProperty[] PatternProperties = new AutomationProperty[]
        {
            AutomationElement.IsInvokePatternAvailableProperty,
            AutomationElement.IsTogglePatternAvailableProperty,
            AutomationElement.IsValuePatternAvailableProperty,
            AutomationElement.IsRangeValuePatternAvailableProperty,
            AutomationElement.IsSelectionItemPatternAvailableProperty,
            AutomationElement.IsSelectionPatternAvailableProperty,
            AutomationElement.IsExpandCollapsePatternAvailableProperty,
            AutomationElement.IsScrollPatternAvailableProperty,
            AutomationElement.IsScrollItemPatternAvailableProperty,
            AutomationElement.IsTextPatternAvailableProperty,
            AutomationElement.IsGridPatternAvailableProperty,
            AutomationElement.IsGridItemPatternAvailableProperty,
            AutomationElement.IsTablePatternAvailableProperty,
            AutomationElement.IsWindowPatternAvailableProperty,
            AutomationElement.IsTransformPatternAvailableProperty
        };

        private static readonly string[] PatternNames = new string[]
        {
            "Invoke", "Toggle", "Value", "RangeValue", "SelectionItem", "Selection", "ExpandCollapse",
            "Scroll", "ScrollItem", "Text", "Grid", "GridItem", "Table", "Window", "Transform"
        };

        private static string[] Patterns(AutomationElement element)
        {
            List<string> names = new List<string>();
            for (int index = 0; index < PatternProperties.Length; index++)
            {
                bool? available = Flag(element, PatternProperties[index]);
                if (available.HasValue && available.Value) names.Add(PatternNames[index]);
            }
            return names.ToArray();
        }

        private static CacheRequest BuildCache()
        {
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element | TreeScope.Children;
            // The raw view is the most complete tree the provider exposes. Nodes
            // carry IsControlElement so a reader can narrow it back down.
            cache.TreeFilter = Automation.RawViewCondition;
            cache.AutomationElementMode = AutomationElementMode.Full;
            Add(cache, AutomationElement.NameProperty);
            Add(cache, AutomationElement.AutomationIdProperty);
            Add(cache, AutomationElement.ControlTypeProperty);
            Add(cache, AutomationElement.LocalizedControlTypeProperty);
            Add(cache, AutomationElement.ClassNameProperty);
            Add(cache, AutomationElement.FrameworkIdProperty);
            Add(cache, AutomationElement.BoundingRectangleProperty);
            Add(cache, AutomationElement.IsEnabledProperty);
            Add(cache, AutomationElement.IsOffscreenProperty);
            Add(cache, AutomationElement.IsKeyboardFocusableProperty);
            Add(cache, AutomationElement.IsPasswordProperty);
            Add(cache, AutomationElement.IsControlElementProperty);
            Add(cache, AutomationElement.IsContentElementProperty);
            Add(cache, AutomationElement.HelpTextProperty);
            Add(cache, AutomationElement.AcceleratorKeyProperty);
            Add(cache, AutomationElement.AccessKeyProperty);
            Add(cache, AutomationElement.NativeWindowHandleProperty);
            Add(cache, AutomationElement.ProcessIdProperty);
            Add(cache, AutomationElement.RuntimeIdProperty);
            for (int index = 0; index < PatternProperties.Length; index++) Add(cache, PatternProperties[index]);
            return cache;
        }

        private static void Add(CacheRequest cache, AutomationProperty property)
        {
            try
            {
                cache.Add(property);
            }
            catch
            {
            }
        }

        private static object Cached(AutomationElement element, AutomationProperty property)
        {
            try
            {
                object value = element.GetCachedPropertyValue(property, true);
                return value == AutomationElement.NotSupported ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string Text(AutomationElement element, AutomationProperty property)
        {
            object value = Cached(element, property);
            if (value == null) return null;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return String.IsNullOrEmpty(text) ? null : text;
        }

        private static bool? Flag(AutomationElement element, AutomationProperty property)
        {
            object value = Cached(element, property);
            return value is bool ? (bool?)(bool)value : null;
        }

        private static bool? FlagWithDefault(AutomationElement element, AutomationProperty property)
        {
            try
            {
                object value = element.GetCachedPropertyValue(property);
                return value is bool ? (bool?)(bool)value : null;
            }
            catch
            {
                return Flag(element, property);
            }
        }

        private static string TypeName(AutomationElement element)
        {
            ControlType type = Cached(element, AutomationElement.ControlTypeProperty) as ControlType;
            return type == null ? "Unknown" : type.ProgrammaticName.Replace("ControlType.", String.Empty);
        }

        private static string Describe(Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }
    }

    public static class MsaaScan
    {
        private const uint ObjIdClient = 0xFFFFFFFC;
        private static Guid iidAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        private sealed class Pending
        {
            internal Accessibility.IAccessible Accessible;
            internal int ChildId;
            internal int ParentId;
            internal int Depth;
            internal int SiblingIndex;
            internal bool ParentDecoration;
        }

        public static List<ScanNode> Walk(IntPtr topWindow, ScanLimits limits, ScanCoverage coverage)
        {
            List<ScanNode> nodes = new List<ScanNode>();
            Stopwatch watch = Stopwatch.StartNew();
            coverage.Provider = "msaa";
            coverage.State = "ok";
            Accessibility.IAccessible root;
            int result = NativeMethods.AccessibleObjectFromWindow(topWindow, ObjIdClient, ref iidAccessible, out root);
            if (result != 0 || root == null)
            {
                coverage.State = "unavailable";
                coverage.AddReason("MSAA-NOELEMENT", "AccessibleObjectFromWindow returned no accessible object (hresult 0x" + result.ToString("X8", CultureInfo.InvariantCulture) + ").");
                return nodes;
            }
            List<Pending> queue = new List<Pending>();
            Pending first = new Pending();
            first.Accessible = root;
            first.ChildId = 0;
            first.ParentId = -1;
            first.Depth = 0;
            queue.Add(first);
            int cursor = 0;
            int failures = 0;
            while (cursor < queue.Count)
            {
                if (nodes.Count >= limits.MaxNodes)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-MAXNODES", "The MSAA walk stopped at the " + limits.MaxNodes + " element limit.");
                    break;
                }
                if (watch.ElapsedMilliseconds > limits.MsaaBudgetMs)
                {
                    coverage.Truncated = true;
                    coverage.State = "partial";
                    coverage.AddReason("SCAN-TIMEOUT", "The MSAA walk stopped after " + limits.MsaaBudgetMs + " ms.");
                    break;
                }
                Pending pending = queue[cursor];
                cursor++;
                ScanNode node = Read(pending, topWindow);
                if (node == null)
                {
                    failures++;
                    continue;
                }
                if (pending.ParentDecoration) node.Decoration = true;
                node.NodeId = nodes.Count;
                nodes.Add(node);
                if (pending.ChildId != 0) continue;
                if (pending.Depth >= limits.MaxDepth)
                {
                    node.AddNote("depth-limit");
                    coverage.Truncated = true;
                    coverage.AddReason("SCAN-MAXDEPTH", "The MSAA walk stopped at depth " + limits.MaxDepth + " for at least one branch.");
                    continue;
                }
                int count = 0;
                try
                {
                    count = pending.Accessible.accChildCount;
                }
                catch (Exception exception)
                {
                    node.AddNote("children-unavailable");
                    coverage.AddReason("MSAA-CHILDREN", "Children of at least one accessible object could not be counted: " + exception.GetType().Name);
                    continue;
                }
                if (count <= 0) continue;
                if (count > 2000)
                {
                    count = 2000;
                    coverage.Truncated = true;
                    coverage.AddReason("MSAA-CHILDCAP", "An accessible object reported more than 2000 children; only the first 2000 were read.");
                }
                object[] children = new object[count];
                int obtained;
                int childResult;
                try
                {
                    childResult = NativeMethods.AccessibleChildren(pending.Accessible, 0, count, children, out obtained);
                }
                catch (Exception exception)
                {
                    node.AddNote("children-unavailable");
                    coverage.AddReason("MSAA-CHILDREN", "AccessibleChildren failed: " + exception.GetType().Name);
                    continue;
                }
                if (childResult != 0 && obtained == 0)
                {
                    node.AddNote("children-unavailable");
                    coverage.AddReason("MSAA-CHILDREN", "AccessibleChildren returned hresult 0x" + childResult.ToString("X8", CultureInfo.InvariantCulture) + ".");
                    continue;
                }
                for (int index = 0; index < obtained; index++)
                {
                    object child = children[index];
                    Pending next = new Pending();
                    next.ParentId = node.NodeId;
                    next.Depth = pending.Depth + 1;
                    next.SiblingIndex = index;
                    next.ParentDecoration = node.Decoration;
                    Accessibility.IAccessible accessible = child as Accessibility.IAccessible;
                    if (accessible != null)
                    {
                        next.Accessible = accessible;
                        next.ChildId = 0;
                    }
                    else if (child is int)
                    {
                        next.Accessible = pending.Accessible;
                        next.ChildId = (int)child;
                        if (next.ChildId == 0) continue;
                    }
                    else
                    {
                        continue;
                    }
                    queue.Add(next);
                }
            }
            watch.Stop();
            if (failures > 0) coverage.AddReason("MSAA-NODEFAIL", failures + " accessible object(s) could not be read.");
            coverage.NodeCount = nodes.Count;
            coverage.DurationMs = (int)watch.ElapsedMilliseconds;
            return nodes;
        }

        private static ScanNode Read(Pending pending, IntPtr topWindow)
        {
            ScanNode node = new ScanNode();
            node.Provider = "msaa";
            node.AddSource("msaa");
            node.ParentId = pending.ParentId;
            node.Depth = pending.Depth;
            node.SiblingIndex = pending.SiblingIndex;
            node.TopHwnd = topWindow.ToInt64();
            object childId = pending.ChildId;
            bool any = false;
            try
            {
                object role = pending.Accessible.get_accRole(childId);
                if (role is int)
                {
                    int roleId = (int)role;
                    node.Role = RoleText(unchecked((uint)roleId));
                    node.Decoration = IsFrameRole(roleId);
                }
                else if (role != null)
                {
                    node.Role = Convert.ToString(role, CultureInfo.InvariantCulture);
                }
                any = true;
            }
            catch
            {
            }
            try
            {
                string name = pending.Accessible.get_accName(childId);
                node.Name = String.IsNullOrEmpty(name) ? null : name;
                any = true;
            }
            catch
            {
            }
            long state = 0;
            try
            {
                object value = pending.Accessible.get_accState(childId);
                if (value is int)
                {
                    state = (int)value;
                    node.StateText = StateText(unchecked((uint)(int)value));
                    node.Visible = (state & 0x00008000L) == 0;
                    node.Enabled = (state & 0x00000001L) == 0;
                    node.IsPassword = (state & 0x20000000L) != 0;
                    node.Offscreen = (state & 0x00010000L) != 0;
                    node.KeyboardFocusable = (state & 0x00100000L) != 0;
                }
                any = true;
            }
            catch
            {
            }
            try
            {
                int left, top, width, height;
                pending.Accessible.accLocation(out left, out top, out width, out height, childId);
                if (width > 0 || height > 0)
                {
                    NativeMethods.POINT topLeft = new NativeMethods.POINT();
                    topLeft.X = left;
                    topLeft.Y = top;
                    NativeMethods.POINT bottomRight = new NativeMethods.POINT();
                    bottomRight.X = left + width;
                    bottomRight.Y = top + height;
                    NativeMethods.LogicalToPhysicalPoint(topWindow, ref topLeft);
                    NativeMethods.LogicalToPhysicalPoint(topWindow, ref bottomRight);
                    node.Rect = new RectValue();
                    node.Rect.X = topLeft.X;
                    node.Rect.Y = topLeft.Y;
                    node.Rect.Width = bottomRight.X - topLeft.X;
                    node.Rect.Height = bottomRight.Y - topLeft.Y;
                }
                any = true;
            }
            catch
            {
            }
            if (!any) return null;
            node.ValueKind = "not-read";
            if (pending.ChildId != 0) node.AddNote("msaa-simple-element");
            return node;
        }

        // Title bars, scroll bars, sizing grips and the like belong to the
        // window frame rather than to what the application shows. They are kept
        // in the record and marked, so a summary does not drown in them.
        private static bool IsFrameRole(int role)
        {
            return role == 1 || role == 2 || role == 3 || role == 4 || role == 9 || role == 10 ||
                role == 17 || role == 18 || role == 19 || role == 39;
        }

        private static string RoleText(uint role)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(256);
            uint length = NativeMethods.GetRoleText(role, builder, (uint)builder.Capacity);
            return length == 0 ? role.ToString(CultureInfo.InvariantCulture) : builder.ToString();
        }

        private static string StateText(uint state)
        {
            if (state == 0) return "normal";
            List<string> parts = new List<string>();
            for (int bit = 0; bit < 31 && parts.Count < 8; bit++)
            {
                uint flag = 1u << bit;
                if ((state & flag) == 0) continue;
                System.Text.StringBuilder builder = new System.Text.StringBuilder(128);
                uint length = NativeMethods.GetStateText(flag, builder, (uint)builder.Capacity);
                if (length != 0) parts.Add(builder.ToString());
            }
            return parts.Count == 0 ? "0x" + state.ToString("X", CultureInfo.InvariantCulture) : String.Join(",", parts.ToArray());
        }
    }

    // Owner-drawn surfaces expose no tree at all. Asking the providers what sits
    // at a grid of coordinates still finds them. The coordinates are passed to
    // the API directly; the physical pointer is never moved.
    public static class HitTestScan
    {
        public static List<ScanNode> Sample(IntPtr topWindow, RectValue area, ScanLimits limits, ScanCoverage coverage)
        {
            List<ScanNode> nodes = new List<ScanNode>();
            coverage.Provider = "hit-test";
            coverage.State = "ok";
            Stopwatch watch = Stopwatch.StartNew();
            if (area == null || area.Width <= 0 || area.Height <= 0)
            {
                coverage.State = "unavailable";
                coverage.AddReason("HITTEST-NORECT", "The window reported no usable rectangle to sample.");
                return nodes;
            }
            int step = 40;
            while ((long)(area.Width / step + 1) * (area.Height / step + 1) > 400) step += 20;
            Dictionary<string, ScanNode> seen = new Dictionary<string, ScanNode>(StringComparer.Ordinal);
            int samples = 0;
            for (int y = area.Y + step / 2; y < area.Y + area.Height; y += step)
            {
                for (int x = area.X + step / 2; x < area.X + area.Width; x += step)
                {
                    if (watch.ElapsedMilliseconds > limits.HitTestBudgetMs)
                    {
                        coverage.Truncated = true;
                        coverage.State = "partial";
                        coverage.AddReason("SCAN-TIMEOUT", "Coordinate sampling stopped after " + limits.HitTestBudgetMs + " ms; part of the window was not sampled.");
                        y = area.Y + area.Height;
                        break;
                    }
                    samples++;
                    UiaInfo info = UiaProbe.AtPoint(x, y, false);
                    if (info != null && info.Status != null && info.Status.State != "unavailable")
                    {
                        ScanNode node = FromUia(info, topWindow.ToInt64());
                        string key = Key(node);
                        if (key != null && !seen.ContainsKey(key))
                        {
                            node.NodeId = nodes.Count;
                            nodes.Add(node);
                            seen[key] = node;
                        }
                    }
                }
            }
            watch.Stop();
            coverage.NodeCount = nodes.Count;
            coverage.DurationMs = (int)watch.ElapsedMilliseconds;
            coverage.AddReason("HITTEST-GRID", "Sampled " + samples + " point(s) on a " + step + " px grid. Anything smaller than that spacing can be missed.");
            return nodes;
        }

        private static ScanNode FromUia(UiaInfo info, long topHwnd)
        {
            ScanNode node = new ScanNode();
            node.Provider = "hit-test";
            node.AddSource("hit-test");
            node.ParentId = -1;
            node.TopHwnd = topHwnd;
            node.Name = info.Name;
            node.AutomationId = info.AutomationId;
            node.ControlType = info.ControlType;
            node.LocalizedControlType = info.LocalizedControlType;
            node.ClassName = info.ClassName;
            node.FrameworkId = info.FrameworkId;
            node.Enabled = info.IsEnabled;
            node.Offscreen = info.IsOffscreen;
            node.KeyboardFocusable = info.IsKeyboardFocusable;
            node.IsPassword = info.IsPassword;
            node.HelpText = info.HelpText;
            node.Rect = info.BoundingRect;
            node.Hwnd = info.NativeWindowHandle;
            node.RuntimeId = SessionLogJson.RuntimeIdText(info.RuntimeId);
            node.ValueKind = "not-read";
            node.AddNote("found-by-coordinate-sampling");
            return node;
        }

        private static string Key(ScanNode node)
        {
            if (!String.IsNullOrEmpty(node.RuntimeId)) return "r:" + node.RuntimeId;
            if (node.Rect == null) return null;
            return "g:" + node.Rect.X + ":" + node.Rect.Y + ":" + node.Rect.Width + ":" + node.Rect.Height + ":" + (node.Name ?? String.Empty);
        }
    }
}
