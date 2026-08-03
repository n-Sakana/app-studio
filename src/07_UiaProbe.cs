namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Windows;
    using System.Windows.Automation;

    public static class UiaProbe
    {
        public static long WarmUp()
        {
            DateTime started = DateTime.UtcNow;
            AutomationElement root = AutomationElement.RootElement;
            root.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty, true);
            return (long)(DateTime.UtcNow - started).TotalMilliseconds;
        }

        public static UiaInfo AtPoint(int x, int y, bool deep)
        {
            UiaInfo info = new UiaInfo();
            info.Status = ProbeStatus.Ok();
            try
            {
                AutomationElement element = AutomationElement.FromPoint(new Point(x, y));
                if (element == null)
                {
                    info.Status = ProbeStatus.Unavailable("UIA-NOELEMENT", "UI Automation returned no element at the requested point.");
                    return info;
                }
                AutomationElement refined = DescendToPoint(element, x, y, 250);
                if (refined != null && refined != element)
                {
                    element = refined;
                    info.RawRefined = true;
                }
                ReadProperties(element, info, deep);
                return info;
            }
            catch (Exception exception)
            {
                info.Status = ProbeStatus.Unavailable("UIA-FAIL", FormatException(exception));
                return info;
            }
        }

        private static AutomationElement DescendToPoint(AutomationElement start, int x, int y, int budgetMs)
        {
            // FromPoint on single-HWND applications (Chromium, Qt, owner-drawn)
            // often stops at a huge container. Descend only through container
            // control types so that template internals of well-formed controls
            // (for example PART_ContentHost inside a WPF TextBox) stay hidden.
            try
            {
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement current = start;
                Rect currentRect = SafeRect(current);
                for (int depth = 0; depth < 24; depth++)
                {
                    if (watch.ElapsedMilliseconds > budgetMs) break;
                    if (!IsContainer(current)) break;
                    AutomationElement best = null;
                    Rect bestRect = Rect.Empty;
                    bool bestIsControl = false;
                    AutomationElement child = walker.GetFirstChild(current);
                    int scanned = 0;
                    while (child != null && scanned < 96 && watch.ElapsedMilliseconds <= budgetMs)
                    {
                        scanned++;
                        Rect rect = SafeRect(child);
                        if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0 && rect.Contains(x, y))
                        {
                            bool isControl = SafeIsControl(child);
                            bool smaller = best == null || rect.Width * rect.Height < bestRect.Width * bestRect.Height;
                            // A control element wins over raw-only elements; among
                            // equals the smaller rectangle wins.
                            if (best == null || (isControl && !bestIsControl) || (isControl == bestIsControl && smaller))
                            {
                                best = child;
                                bestRect = rect;
                                bestIsControl = isControl;
                            }
                        }
                        child = walker.GetNextSibling(child);
                    }
                    if (best == null) break;
                    if (!currentRect.IsEmpty && bestRect.Width * bestRect.Height >= currentRect.Width * currentRect.Height) break;
                    current = best;
                    currentRect = bestRect;
                }
                return current;
            }
            catch
            {
                return start;
            }
        }

        private static bool IsContainer(AutomationElement element)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
                ControlType type = value as ControlType;
                if (type == null) return true;
                int id = type.Id;
                return id == ControlType.Window.Id || id == ControlType.Pane.Id || id == ControlType.Custom.Id ||
                    id == ControlType.Group.Id || id == ControlType.Document.Id || id == ControlType.Table.Id ||
                    id == ControlType.List.Id || id == ControlType.Tree.Id || id == ControlType.DataGrid.Id ||
                    id == ControlType.Tab.Id || id == ControlType.ToolBar.Id || id == ControlType.MenuBar.Id;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeIsControl(AutomationElement element)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(AutomationElement.IsControlElementProperty, true);
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }

        private static Rect SafeRect(AutomationElement element)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty, true);
                if (value is Rect) return (Rect)value;
                return Rect.Empty;
            }
            catch
            {
                return Rect.Empty;
            }
        }

        private static void ReadProperties(AutomationElement element, UiaInfo info, bool deep)
        {
            info.Name = ReadString(element, AutomationElement.NameProperty, info);
            info.AutomationId = ReadString(element, AutomationElement.AutomationIdProperty, info);
            object controlType = ReadValue(element, AutomationElement.ControlTypeProperty, info);
            ControlType typedControl = controlType as ControlType;
            info.ControlType = typedControl == null ? null : typedControl.ProgrammaticName.Replace("ControlType.", String.Empty);
            info.LocalizedControlType = ReadString(element, AutomationElement.LocalizedControlTypeProperty, info);
            info.ClassName = ReadString(element, AutomationElement.ClassNameProperty, info);
            info.FrameworkId = ReadString(element, AutomationElement.FrameworkIdProperty, info);
            info.IsEnabled = ReadBool(element, AutomationElement.IsEnabledProperty, info);
            info.IsOffscreen = ReadBool(element, AutomationElement.IsOffscreenProperty, info);
            info.IsKeyboardFocusable = ReadBool(element, AutomationElement.IsKeyboardFocusableProperty, info);
            info.IsPassword = ReadBool(element, AutomationElement.IsPasswordProperty, info);
            info.HelpText = ReadString(element, AutomationElement.HelpTextProperty, info);
            info.AcceleratorKey = ReadString(element, AutomationElement.AcceleratorKeyProperty, info);
            info.AccessKey = ReadString(element, AutomationElement.AccessKeyProperty, info);
            object nativeHandle = ReadValue(element, AutomationElement.NativeWindowHandleProperty, info);
            info.NativeWindowHandle = nativeHandle is int ? (int)nativeHandle : 0;
            try
            {
                Rect rectangle = element.Current.BoundingRectangle;
                info.BoundingRect = new RectValue();
                info.BoundingRect.X = (int)Math.Round(rectangle.X);
                info.BoundingRect.Y = (int)Math.Round(rectangle.Y);
                info.BoundingRect.Width = (int)Math.Round(rectangle.Width);
                info.BoundingRect.Height = (int)Math.Round(rectangle.Height);
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("UIA-FAIL", "BoundingRectangle: " + FormatException(exception));
            }
            try
            {
                info.RuntimeId = element.GetRuntimeId();
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("UIA-FAIL", "RuntimeId: " + FormatException(exception));
            }
            try
            {
                if (deep)
                {
                    AutomationPattern[] patterns = element.GetSupportedPatterns();
                    List<string> names = new List<string>();
                    for (int index = 0; index < patterns.Length; index++)
                    {
                        names.Add(patterns[index].ProgrammaticName.Replace("PatternIdentifiers.Pattern", String.Empty));
                    }
                    info.SupportedPatterns = names.ToArray();
                }
                if (!info.IsPassword)
                {
                    object valuePattern;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern))
                    {
                        info.LiveValue = ((ValuePattern)valuePattern).Current.Value;
                    }
                }
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("UIA-FAIL", "Patterns: " + FormatException(exception));
            }
            if (deep)
            {
                ReadTree(element, info);
            }
        }

        private static void ReadTree(AutomationElement element, UiaInfo info)
        {
            try
            {
                List<UiaNode> reverse = new List<UiaNode>();
                AutomationElement current = element;
                TreeWalker walker = TreeWalker.ControlViewWalker;
                for (int depth = 0; depth < 12 && current != null; depth++)
                {
                    reverse.Add(Node(current, walker));
                    current = walker.GetParent(current);
                }
                reverse.Reverse();
                info.TreePath = reverse.ToArray();

                List<UiaNode> children = new List<UiaNode>();
                AutomationElement child = walker.GetFirstChild(element);
                while (child != null && children.Count < 50)
                {
                    children.Add(Node(child, walker));
                    child = walker.GetNextSibling(child);
                }
                info.Children = children.ToArray();
                if (children.Count == 0 && (info.ControlType == "Custom" || info.ControlType == "Pane" || info.ControlType == "Window"))
                {
                    info.Status.AddPartial("UIA-EMPTYTREE", "The custom, pane, or window element exposes no UI Automation children.");
                }
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("UIA-FAIL", "Tree: " + FormatException(exception));
            }
        }

        private static UiaNode Node(AutomationElement element, TreeWalker walker)
        {
            UiaNode node = new UiaNode();
            object typeValue = element.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
            ControlType type = typeValue as ControlType;
            node.ControlType = type == null ? null : type.ProgrammaticName.Replace("ControlType.", String.Empty);
            node.Name = Convert.ToString(element.GetCurrentPropertyValue(AutomationElement.NameProperty, true), CultureInfo.InvariantCulture);
            node.AutomationId = Convert.ToString(element.GetCurrentPropertyValue(AutomationElement.AutomationIdProperty, true), CultureInfo.InvariantCulture);
            AutomationElement parent = walker.GetParent(element);
            if (parent != null)
            {
                AutomationElement sibling = walker.GetFirstChild(parent);
                int sameType = 0;
                int index = 0;
                while (sibling != null)
                {
                    object siblingTypeValue = sibling.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
                    ControlType siblingType = siblingTypeValue as ControlType;
                    if (siblingType != null && type != null && siblingType.Id == type.Id)
                    {
                        if (Automation.Compare(sibling, element)) index = sameType;
                        sameType++;
                    }
                    sibling = walker.GetNextSibling(sibling);
                }
                node.IndexAmongSameType = index;
                node.SiblingCount = sameType;
            }
            return node;
        }

        private static object ReadValue(AutomationElement element, AutomationProperty property, UiaInfo info)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(property, true);
                return value == AutomationElement.NotSupported ? null : value;
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("UIA-FAIL", property.ProgrammaticName + ": " + FormatException(exception));
                return null;
            }
        }

        private static string ReadString(AutomationElement element, AutomationProperty property, UiaInfo info)
        {
            object value = ReadValue(element, property, info);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool ReadBool(AutomationElement element, AutomationProperty property, UiaInfo info)
        {
            object value = ReadValue(element, property, info);
            return value is bool && (bool)value;
        }

        private static string FormatException(Exception exception)
        {
            return "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + exception.GetType().Name + ": " + exception.Message;
        }
    }

    public static class MsaaProbe
    {
        private static Guid iidAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        private const uint ObjIdClient = 0xFFFFFFFC;

        public static MsaaInfo AtPoint(int x, int y)
        {
            MsaaInfo info = new MsaaInfo();
            info.Status = ProbeStatus.Ok();
            try
            {
                NativeMethods.POINT physical = new NativeMethods.POINT();
                physical.X = x;
                physical.Y = y;
                IntPtr window = NativeMethods.WindowFromPoint(physical);
                if (window == IntPtr.Zero)
                {
                    info.Status = ProbeStatus.Unavailable("MSAA-NOHWND", "No window exists at the requested screen point.");
                    return info;
                }
                // A DPI-unaware target answers accHitTest and accLocation in its
                // own virtualized coordinate space, so the physical point must be
                // translated into that space first (a no-op for aware targets).
                NativeMethods.POINT logical = physical;
                NativeMethods.PhysicalToLogicalPoint(window, ref logical);
                Accessibility.IAccessible accessible;
                int result = NativeMethods.AccessibleObjectFromWindow(window, ObjIdClient, ref iidAccessible, out accessible);
                if (result != 0 || accessible == null)
                {
                    info.Status = ProbeStatus.Unavailable("MSAA-NOELEMENT", "AccessibleObjectFromWindow returned no accessible object (hresult 0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
                    return info;
                }
                object child = (object)0;
                for (int depth = 0; depth < 32; depth++)
                {
                    object hit;
                    try { hit = accessible.accHitTest(logical.X, logical.Y); }
                    catch { break; }
                    if (hit == null) break;
                    Accessibility.IAccessible descend = hit as Accessibility.IAccessible;
                    if (descend != null)
                    {
                        accessible = descend;
                        child = (object)0;
                        continue;
                    }
                    if (hit is int)
                    {
                        if ((int)hit == 0) break;
                        child = hit;
                        break;
                    }
                    break;
                }
                info.ChildId = child is int ? (int)child : 0;
                info.Hwnd = window.ToInt64();
                try { info.Name = accessible.get_accName(child); }
                catch (Exception exception) { info.Status.AddPartial("MSAA-NAME", exception.GetType().Name); }
                try
                {
                    object role = accessible.get_accRole(child);
                    if (role is int) info.Role = RoleText(unchecked((uint)(int)role));
                    else if (role != null) info.Role = role.ToString();
                }
                catch (Exception exception) { info.Status.AddPartial("MSAA-ROLE", exception.GetType().Name); }
                try
                {
                    object state = accessible.get_accState(child);
                    if (state is int)
                    {
                        info.State = (int)state;
                        info.StateText = StateText(unchecked((uint)(int)state));
                    }
                }
                catch (Exception exception) { info.Status.AddPartial("MSAA-STATE", exception.GetType().Name); }
                // STATE_SYSTEM_PROTECTED marks password fields; their value must never be read.
                if ((info.State & 0x20000000L) == 0)
                {
                    try { info.Value = accessible.get_accValue(child); }
                    catch { }
                }
                try
                {
                    int left, top, width, height;
                    accessible.accLocation(out left, out top, out width, out height, child);
                    NativeMethods.POINT topLeft = new NativeMethods.POINT();
                    topLeft.X = left;
                    topLeft.Y = top;
                    NativeMethods.POINT bottomRight = new NativeMethods.POINT();
                    bottomRight.X = left + width;
                    bottomRight.Y = top + height;
                    NativeMethods.LogicalToPhysicalPoint(window, ref topLeft);
                    NativeMethods.LogicalToPhysicalPoint(window, ref bottomRight);
                    RectValue rect = new RectValue();
                    rect.X = topLeft.X;
                    rect.Y = topLeft.Y;
                    rect.Width = bottomRight.X - topLeft.X;
                    rect.Height = bottomRight.Y - topLeft.Y;
                    info.Rect = rect;
                }
                catch (Exception exception) { info.Status.AddPartial("MSAA-LOCATION", exception.GetType().Name); }
                return info;
            }
            catch (Exception exception)
            {
                info.Status = ProbeStatus.Unavailable("MSAA-FAIL", exception.GetType().Name + ": " + exception.Message);
                return info;
            }
        }

        private static string RoleText(uint role)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder(256);
            uint length = NativeMethods.GetRoleText(role, builder, (uint)builder.Capacity);
            return length == 0 ? role.ToString(System.Globalization.CultureInfo.InvariantCulture) : builder.ToString();
        }

        private static string StateText(uint state)
        {
            if (state == 0) return "normal";
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            for (int bit = 0; bit < 31 && parts.Count < 8; bit++)
            {
                uint flag = 1u << bit;
                if ((state & flag) == 0) continue;
                System.Text.StringBuilder builder = new System.Text.StringBuilder(128);
                uint length = NativeMethods.GetStateText(flag, builder, (uint)builder.Capacity);
                if (length != 0) parts.Add(builder.ToString());
            }
            return parts.Count == 0 ? "0x" + state.ToString("X", System.Globalization.CultureInfo.InvariantCulture) : String.Join(",", parts.ToArray());
        }
    }

    public sealed class UiaActionResult
    {
        public string Outcome;
        public string Method;
        public string ErrorCode;
        public int ErrorHresult;
        public string ErrorMessage;
        public string BeforeValue;
        public string AfterValue;
        public string BeforeState;
        public string AfterState;
        public bool BeforeFocused;
        public bool AfterFocused;
        public string BeforeName;
        public string AfterName;
    }

    public static class UiaOperation
    {
        public static UiaActionResult AtPoint(int x, int y, string kind, string value)
        {
            UiaActionResult result = new UiaActionResult();
            result.Outcome = "notSupported";
            result.Method = "uia.none";
            try
            {
                AutomationElement element = AutomationElement.FromPoint(new Point(x, y));
                if (element == null)
                {
                    result.Outcome = "failed";
                    result.ErrorCode = "UIA-NOELEMENT";
                    result.ErrorMessage = "UI Automation returned no element at the requested point.";
                    return result;
                }
                ReadObservation(element, result, true);
                bool performed = false;
                if (kind == "read")
                {
                    result.Method = "uia.properties";
                    result.Outcome = "success";
                    performed = true;
                }
                else if (kind == "focus")
                {
                    result.Method = "uia.SetFocus";
                    element.SetFocus();
                    performed = true;
                }
                else if (kind == "invoke" || kind == "click")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        result.Method = "uia.InvokePattern.Invoke";
                        ((InvokePattern)pattern).Invoke();
                        performed = true;
                    }
                }
                else if (kind == "toggle")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                    {
                        result.Method = "uia.TogglePattern.Toggle";
                        ((TogglePattern)pattern).Toggle();
                        performed = true;
                    }
                }
                else if (kind == "select")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                    {
                        result.Method = "uia.SelectionItemPattern.Select";
                        ((SelectionItemPattern)pattern).Select();
                        performed = true;
                    }
                }
                else if (kind == "expand")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                    {
                        result.Method = "uia.ExpandCollapsePattern.Expand";
                        ((ExpandCollapsePattern)pattern).Expand();
                        performed = true;
                    }
                }
                else if (kind == "setValue")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                    {
                        result.Method = "uia.ValuePattern.SetValue";
                        ((ValuePattern)pattern).SetValue(value ?? String.Empty);
                        performed = true;
                    }
                }
                else if (kind == "scroll")
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out pattern))
                    {
                        result.Method = "uia.ScrollItemPattern.ScrollIntoView";
                        ((ScrollItemPattern)pattern).ScrollIntoView();
                        performed = true;
                    }
                }
                if (!performed)
                {
                    result.ErrorCode = "UIA-NOTSUPPORTED";
                    result.ErrorMessage = "No matching UI Automation pattern is available for " + kind + ".";
                    return result;
                }
                if (kind != "read") Thread.Sleep(120);
                ReadObservation(element, result, false);
                if (kind == "read") result.Outcome = "success";
                else if (Changed(result, kind)) result.Outcome = "success";
                else result.Outcome = "unknown";
                return result;
            }
            catch (Exception exception)
            {
                result.Outcome = "failed";
                result.ErrorCode = "UIA-FAIL";
                result.ErrorHresult = exception.HResult;
                result.ErrorMessage = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        private static void ReadObservation(AutomationElement element, UiaActionResult result, bool before)
        {
            string value = null;
            string state = null;
            bool focused = false;
            string name = null;
            try { name = element.Current.Name; } catch { }
            try { focused = element.Current.HasKeyboardFocus; } catch { }
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) value = ((ValuePattern)pattern).Current.Value;
                else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) state = ((TogglePattern)pattern).Current.ToggleState.ToString();
                else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) state = ((SelectionItemPattern)pattern).Current.IsSelected ? "selected" : "notSelected";
                else if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) state = ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState.ToString();
            }
            catch { }
            if (before)
            {
                result.BeforeValue = value;
                result.BeforeState = state;
                result.BeforeFocused = focused;
                result.BeforeName = name;
            }
            else
            {
                result.AfterValue = value;
                result.AfterState = state;
                result.AfterFocused = focused;
                result.AfterName = name;
            }
        }

        private static bool Changed(UiaActionResult result, string kind)
        {
            if (kind == "focus") return !result.BeforeFocused && result.AfterFocused;
            if (kind == "setValue") return !String.Equals(result.BeforeValue, result.AfterValue, StringComparison.Ordinal);
            if (kind == "toggle" || kind == "select" || kind == "expand") return !String.Equals(result.BeforeState, result.AfterState, StringComparison.Ordinal);
            return !String.Equals(result.BeforeName, result.AfterName, StringComparison.Ordinal) ||
                !String.Equals(result.BeforeValue, result.AfterValue, StringComparison.Ordinal) ||
                !String.Equals(result.BeforeState, result.AfterState, StringComparison.Ordinal);
        }
    }
}
