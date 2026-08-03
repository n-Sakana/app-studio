namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    public sealed class ObservedElement
    {
        public string Key;
        public string Route;
        public string Level;
        public int ProcessId;
        public long Hwnd;
        public long TopHwnd;
        public string TopTitle;
        public string ControlType;
        public string LocalizedControlType;
        public string Name;
        public string AutomationId;
        public string ClassName;
        public string RealClassName;
        public string FrameworkId;
        public string RuntimeId;
        public string Role;
        public string StateText;
        public RectValue Rect;
        public bool? Enabled;
        public bool? Offscreen;
        public bool? KeyboardFocusable;
        public bool IsPassword;
        public int CtrlId;
        public string UiaState;
        public string MsaaState;
        public string Win32State;

        public string Label
        {
            get
            {
                string type = !String.IsNullOrEmpty(ControlType) ? ControlType : (!String.IsNullOrEmpty(Role) ? Role : (!String.IsNullOrEmpty(ClassName) ? ClassName : "?"));
                return String.IsNullOrEmpty(Name) ? type : type + " \"" + Name + "\"";
            }
        }

        public static ObservedElement From(Snapshot snapshot, AcquisitionView view)
        {
            if (snapshot == null || view == null) return null;
            ObservedElement element = new ObservedElement();
            element.Route = view.Route;
            element.Level = view.Level;
            element.ProcessId = view.ProcessId;
            element.TopTitle = view.TopCaption;
            element.Rect = view.Rect;
            if (snapshot.Win32 != null)
            {
                element.Hwnd = snapshot.Win32.Hwnd;
                element.TopHwnd = snapshot.Win32.TopHwnd;
                element.ClassName = snapshot.Win32.ClassName;
                element.RealClassName = snapshot.Win32.RealClass;
                element.CtrlId = snapshot.Win32.CtrlId;
                if (element.Rect == null) element.Rect = snapshot.Win32.WindowRect;
            }
            if (snapshot.Uia != null)
            {
                element.ControlType = snapshot.Uia.ControlType;
                element.LocalizedControlType = snapshot.Uia.LocalizedControlType;
                element.Name = snapshot.Uia.Name;
                element.AutomationId = snapshot.Uia.AutomationId;
                element.FrameworkId = snapshot.Uia.FrameworkId;
                element.RuntimeId = SessionLogJson.RuntimeIdText(snapshot.Uia.RuntimeId);
                element.Enabled = snapshot.Uia.IsEnabled;
                element.Offscreen = snapshot.Uia.IsOffscreen;
                element.KeyboardFocusable = snapshot.Uia.IsKeyboardFocusable;
                element.IsPassword = snapshot.Uia.IsPassword;
                if (String.IsNullOrEmpty(element.ClassName)) element.ClassName = snapshot.Uia.ClassName;
            }
            if (snapshot.Msaa != null)
            {
                element.Role = snapshot.Msaa.Role;
                element.StateText = snapshot.Msaa.StateText;
                if (String.IsNullOrEmpty(element.Name)) element.Name = snapshot.Msaa.Name;
            }
            element.UiaState = State(snapshot.UiaStatus);
            element.MsaaState = State(snapshot.MsaaStatus);
            element.Win32State = State(snapshot.Win32Status);
            element.Key = BuildKey(element);
            return element;
        }

        private static string State(ProbeStatus status)
        {
            return status == null ? "unavailable" : status.State;
        }

        private static string BuildKey(ObservedElement element)
        {
            if (!String.IsNullOrEmpty(element.RuntimeId)) return "r:" + element.RuntimeId;
            StringBuilder key = new StringBuilder();
            key.Append("h:").Append(element.Hwnd.ToString(CultureInfo.InvariantCulture));
            key.Append("|c:").Append(element.ControlType ?? element.Role ?? element.ClassName ?? "?");
            key.Append("|n:").Append(element.Name ?? String.Empty);
            if (element.Rect != null) key.Append("|b:").Append(element.Rect.X).Append(',').Append(element.Rect.Y).Append(',').Append(element.Rect.Width).Append(',').Append(element.Rect.Height);
            return key.ToString();
        }
    }

    public sealed class ObservationStatus
    {
        public bool Active;
        public bool Paused;
        public int TargetProcessId;
        public int EnterCount;
        public int ClickCount;
        public int EventCount;
        public int PointerSamples;
        public int Dropped;
        public string CurrentLabel;
    }

    // Turns ordinary pointer movement and clicks over the target application
    // into a log. Movement inside one element is folded into a dwell record so
    // the log stays readable; the unfolded coordinate trail goes to its own
    // stream. Keyboard input is never observed.
    public sealed class ObservationRecorder
    {
        private readonly SessionLog log;
        private readonly object sync = new object();
        private bool active;
        private bool paused;
        private int targetProcessId;
        private int[] targetProcessIds = new int[0];
        private string targetName;
        private ObservedElement current;
        private DateTime enteredAt;
        private int moveSamples;
        private int enterCount;
        private int clickCount;
        private int eventCount;
        private int pointerSamples;
        private int dropped;
        private int lastSampleX = Int32.MinValue;
        private int lastSampleY = Int32.MinValue;
        private DateTime lastSampleAt = DateTime.MinValue;
        private int pointerSampleIntervalMs = 500;
        private int pointerSampleDistance = 24;

        public ObservationRecorder(SessionLog sessionLog)
        {
            log = sessionLog;
        }

        public bool Active { get { lock (sync) { return active; } } }
        public bool Paused { get { lock (sync) { return paused; } } }
        public ObservedElement Current { get { lock (sync) { return current; } } }

        public ObservationStatus Status
        {
            get
            {
                lock (sync)
                {
                    ObservationStatus status = new ObservationStatus();
                    status.Active = active;
                    status.Paused = paused;
                    status.TargetProcessId = targetProcessId;
                    status.EnterCount = enterCount;
                    status.ClickCount = clickCount;
                    status.EventCount = eventCount;
                    status.PointerSamples = pointerSamples;
                    status.Dropped = dropped;
                    status.CurrentLabel = current == null ? null : current.Label;
                    return status;
                }
            }
        }

        public int RecordCount
        {
            get { lock (sync) { return enterCount + clickCount + eventCount; } }
        }

        public void Start(int processId, string name)
        {
            Start(processId, null, name);
        }

        // A packaged application draws its contents from a process other than
        // the one that owns the window in the target list, so more than one
        // process id can legitimately belong to the chosen window.
        public void Start(int processId, int[] alsoAccepted, string name)
        {
            List<int> accepted = new List<int>();
            if (processId != 0) accepted.Add(processId);
            if (alsoAccepted != null)
            {
                for (int index = 0; index < alsoAccepted.Length; index++)
                {
                    if (alsoAccepted[index] != 0 && !accepted.Contains(alsoAccepted[index])) accepted.Add(alsoAccepted[index]);
                }
            }
            lock (sync)
            {
                active = true;
                paused = false;
                targetProcessId = processId;
                targetProcessIds = accepted.ToArray();
                targetName = name;
                current = null;
                moveSamples = 0;
            }
            Write("observations", new JsonObject()
                .Add("kind", "observe.start")
                .Add("targetProcessId", processId)
                .Add("targetProcessIds", SessionLogJson.Numbers(accepted.ToArray()))
                .Add("targetName", name)
                .Add("scope", "target application only")
                .Add("collects", new object[] { "pointer position", "element under pointer", "mouse button transitions", "application accessibility events" })
                .Add("neverCollects", new object[] { "keyboard input", "clipboard", "values of password elements", "other applications" }));
        }

        public void Stop()
        {
            LeaveCurrent("stop");
            lock (sync)
            {
                active = false;
                paused = false;
                current = null;
            }
            Write("observations", new JsonObject().Add("kind", "observe.stop"));
        }

        public void SetPaused(bool value)
        {
            bool changed;
            lock (sync)
            {
                changed = active && paused != value;
                if (changed) paused = value;
            }
            if (changed) Write("observations", new JsonObject().Add("kind", value ? "observe.pause" : "observe.resume"));
        }

        // Called for every accepted acquisition of the live view. Only elements
        // of the target application are recorded.
        public void OnAcquisition(Snapshot snapshot, AcquisitionView view, int x, int y)
        {
            bool running;
            int target;
            int[] accepted;
            lock (sync)
            {
                running = active && !paused;
                target = targetProcessId;
                accepted = targetProcessIds;
            }
            if (!running) return;
            if (view == null || view.IsSelf) return;
            if (!Belongs(accepted, target, view.ProcessId))
            {
                LeaveCurrent("left-target");
                return;
            }
            ObservedElement element = ObservedElement.From(snapshot, view);
            if (element == null) return;
            ObservedElement previous;
            lock (sync) { previous = current; }
            if (previous != null && previous.Key == element.Key)
            {
                lock (sync)
                {
                    moveSamples++;
                    // Keep the newest geometry: an element can move while hovered.
                    current.Rect = element.Rect;
                }
                SamplePointer(x, y, element);
                return;
            }
            LeaveCurrent("moved");
            lock (sync)
            {
                current = element;
                enteredAt = DateTime.UtcNow;
                moveSamples = 0;
                enterCount++;
            }
            Write("observations", new JsonObject()
                .Add("kind", "observe.enter")
                .Add("x", x)
                .Add("y", y)
                .Add("element", ObservationJson.Element(element)));
            SamplePointer(x, y, element);
        }

        public ObservedElement OnMouseDown(int x, int y, string button)
        {
            bool running;
            int target;
            int[] accepted;
            lock (sync)
            {
                running = active && !paused;
                target = targetProcessId;
                accepted = targetProcessIds;
            }
            if (!running) return null;
            int owner = WindowTools.ProcessIdAt(x, y);
            if (!Belongs(accepted, target, owner))
            {
                // A click outside the investigated application is deliberately
                // not described; only the fact that focus left is worth keeping.
                lock (sync) { dropped++; }
                Write("observations", new JsonObject()
                    .Add("kind", "observe.click.outside")
                    .Add("button", button)
                    .Add("note", "A click outside the target application was not recorded."));
                return null;
            }
            ObservedElement element;
            lock (sync)
            {
                element = current;
                clickCount++;
            }
            Write("observations", new JsonObject()
                .Add("kind", "observe.click")
                .Add("button", button)
                .Add("x", x)
                .Add("y", y)
                .Add("element", element == null ? null : ObservationJson.Element(element))
                .Add("elementKnown", element != null));
            return element;
        }

        public void OnClickOutcome(ObservedElement before, Snapshot after, AcquisitionView afterView, int x, int y, string[] appEvents, int delayMs)
        {
            bool running;
            lock (sync) { running = active; }
            if (!running) return;
            ObservedElement afterElement = ObservedElement.From(after, afterView);
            List<object> changes = new List<object>();
            if (before != null && afterElement != null)
            {
                AddChange(changes, "identity", before.Key, afterElement.Key);
                AddChange(changes, "name", before.Name, afterElement.Name);
                AddChange(changes, "controlType", before.ControlType, afterElement.ControlType);
                AddChange(changes, "state", before.StateText, afterElement.StateText);
                AddChange(changes, "enabled", Flag(before.Enabled), Flag(afterElement.Enabled));
                AddChange(changes, "rect", RectText(before.Rect), RectText(afterElement.Rect));
                AddChange(changes, "windowTitle", before.TopTitle, afterElement.TopTitle);
            }
            Write("observations", new JsonObject()
                .Add("kind", "observe.click.result")
                .Add("x", x)
                .Add("y", y)
                .Add("afterMs", delayMs)
                .Add("before", before == null ? null : ObservationJson.Element(before))
                .Add("after", afterElement == null ? null : ObservationJson.Element(afterElement))
                .Add("changes", changes.ToArray())
                .Add("applicationEvents", SessionLogJson.Strings(appEvents))
                .Add("observed", changes.Count > 0 || (appEvents != null && appEvents.Length > 0))
                .Add("note", changes.Count == 0 && (appEvents == null || appEvents.Length == 0)
                    ? "No change was observed at this point; the application may have reacted somewhere this tool did not look."
                    : null));
        }

        public void OnApplicationEvent(WinEventRecord record)
        {
            bool running;
            int target;
            int[] accepted;
            lock (sync)
            {
                running = active && !paused;
                target = targetProcessId;
                accepted = targetProcessIds;
            }
            if (!running || record == null) return;
            if (!Belongs(accepted, target, record.ProcessId)) return;
            lock (sync) { eventCount++; }
            Write("observations", new JsonObject()
                .Add("kind", "observe.appevent")
                .Add("type", record.Type)
                .Add("hwnd", record.Hwnd)
                .Add("objectId", record.ObjectId)
                .Add("childId", record.ChildId)
                .Add("processId", record.ProcessId));
        }

        // No target chosen means everything is in scope. Otherwise the process
        // has to be one of the ones the chosen window actually draws from.
        private static bool Belongs(int[] accepted, int target, int processId)
        {
            if (target == 0) return true;
            if (accepted == null || accepted.Length == 0) return processId == target;
            for (int index = 0; index < accepted.Length; index++) if (accepted[index] == processId) return true;
            return false;
        }

        private void SamplePointer(int x, int y, ObservedElement element)
        {
            bool write = false;
            lock (sync)
            {
                double distance = lastSampleX == Int32.MinValue ? Double.MaxValue : Math.Abs(x - lastSampleX) + Math.Abs(y - lastSampleY);
                double elapsed = (DateTime.UtcNow - lastSampleAt).TotalMilliseconds;
                if (distance >= pointerSampleDistance && elapsed >= pointerSampleIntervalMs)
                {
                    lastSampleX = x;
                    lastSampleY = y;
                    lastSampleAt = DateTime.UtcNow;
                    pointerSamples++;
                    write = true;
                }
            }
            // The unfolded trail lives in its own stream so the readable log is
            // not drowned by it.
            if (write) Write("pointer-raw", new JsonObject().Add("kind", "pointer").Add("x", x).Add("y", y).Add("elementKey", element == null ? null : element.Key));
        }

        private void LeaveCurrent(string reason)
        {
            ObservedElement leaving;
            int samples;
            int dwell;
            lock (sync)
            {
                leaving = current;
                samples = moveSamples;
                dwell = leaving == null ? 0 : (int)(DateTime.UtcNow - enteredAt).TotalMilliseconds;
                current = null;
                moveSamples = 0;
            }
            if (leaving == null) return;
            Write("observations", new JsonObject()
                .Add("kind", "observe.leave")
                .Add("reason", reason)
                .Add("dwellMs", dwell)
                .Add("moveSamples", samples)
                .Add("element", ObservationJson.Element(leaving)));
        }

        private static void AddChange(List<object> changes, string field, string before, string after)
        {
            if (String.Equals(before, after, StringComparison.Ordinal)) return;
            changes.Add(new JsonObject().Add("field", field).Add("before", before).Add("after", after));
        }

        private static string Flag(bool? value)
        {
            return value.HasValue ? (value.Value ? "true" : "false") : null;
        }

        private static string RectText(RectValue rect)
        {
            return rect == null ? null : rect.X + "," + rect.Y + " " + rect.Width + "x" + rect.Height;
        }

        private void Write(string stream, JsonObject record)
        {
            if (log == null) return;
            if (log.Append(stream, record) == 0)
            {
                lock (sync) { dropped++; }
            }
        }
    }

    public static class ObservationJson
    {
        public static JsonObject Element(ObservedElement element)
        {
            if (element == null) return null;
            return new JsonObject()
                .Add("key", element.Key)
                .Add("route", element.Route)
                .Add("level", element.Level)
                .Add("processId", element.ProcessId)
                .Add("hwnd", element.Hwnd)
                .Add("hasHwnd", element.Hwnd != 0)
                .Add("topHwnd", element.TopHwnd)
                .Add("topTitle", element.TopTitle)
                .Add("controlType", element.ControlType)
                .Add("localizedControlType", element.LocalizedControlType)
                .Add("role", element.Role)
                .Add("name", element.Name)
                .Add("automationId", element.AutomationId)
                .Add("className", element.ClassName)
                .Add("realClassName", element.RealClassName)
                .Add("frameworkId", element.FrameworkId)
                .Add("ctrlId", element.CtrlId)
                .Add("runtimeId", element.RuntimeId)
                .Add("rect", SessionLogJson.Rect(element.Rect))
                .Add("enabled", element.Enabled)
                .Add("offscreen", element.Offscreen)
                .Add("keyboardFocusable", element.KeyboardFocusable)
                .Add("isPassword", element.IsPassword)
                .Add("stateText", element.StateText)
                .Add("providers", new JsonObject().Add("uia", element.UiaState).Add("msaa", element.MsaaState).Add("win32", element.Win32State));
        }
    }

    // Polls the mouse buttons only. There is no keyboard hook and no keyboard
    // polling anywhere in the product, so typed text cannot be captured.
    public sealed class MouseButtonWatcher
    {
        private bool leftDown;
        private bool rightDown;
        private bool middleDown;

        public string[] Poll()
        {
            List<string> pressed = new List<string>();
            if (Pressed(NativeMethods.VK_LBUTTON, ref leftDown)) pressed.Add("left");
            if (Pressed(NativeMethods.VK_RBUTTON, ref rightDown)) pressed.Add("right");
            if (Pressed(NativeMethods.VK_MBUTTON, ref middleDown)) pressed.Add("middle");
            return pressed.ToArray();
        }

        public void Reset()
        {
            // Reading once here also clears the "pressed since the last call"
            // bit, so a button pressed before recording started is not reported
            // as the first click of the session.
            leftDown = Down(NativeMethods.VK_LBUTTON);
            rightDown = Down(NativeMethods.VK_RBUTTON);
            middleDown = Down(NativeMethods.VK_MBUTTON);
        }

        // Watching only "is the button down right now" loses any click that
        // starts and finishes between two polls, which is most of a brisk
        // click at a 50 ms interval. GetAsyncKeyState also reports whether the
        // button went down since the previous call, and that is what catches
        // the presses the level check cannot see.
        private static bool Pressed(int key, ref bool wasDown)
        {
            int state = NativeMethods.GetAsyncKeyState(key);
            bool down = (state & 0x8000) != 0;
            bool wentDownSinceLastCall = (state & 0x0001) != 0;
            bool press = (down && !wasDown) || (!down && wentDownSinceLastCall);
            wasDown = down;
            return press;
        }

        private static bool Down(int key)
        {
            return (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
        }
    }
}
