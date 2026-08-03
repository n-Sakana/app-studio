namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;

    public sealed class RecorderStatus
    {
        public TimeSpan Elapsed;
        public int StepCount;
        public int ScreenCount;
        public string AppName;
        public string WindowTitle;
        public RectValue WindowRect;
        public string LastStep;
        public string Problem;
    }

    // Follows whatever the operator is actually doing, across applications.
    //
    // Two things it deliberately does not do. It does not read the keyboard
    // stream: shortcut keys are recognised from the key state of a fixed list of
    // command keys, and typed text is never taken from key traffic at all, it is
    // read back out of the field that received it. And it does not write down a
    // flood of pointer movement: only a press that lands somewhere meaningful
    // becomes a step.
    public sealed class Recorder : IDisposable
    {
        private const int PollMs = 55;
        private const int MinKeyframeGapMs = 700;

        private readonly object sync = new object();
        private readonly string baseDir;
        private readonly StudioSession session;
        private readonly Acquire.ISurfaceGuard guard;
        private readonly System.Windows.Threading.Dispatcher dispatcher;
        private readonly int ownProcessId;
        private readonly ScanLimits limits;
        private readonly Dictionary<int, bool> keyDown = new Dictionary<int, bool>();
        private readonly Dictionary<long, string> screenByWindow = new Dictionary<long, string>();
        private readonly Dictionary<long, DateTime> lastKeyframe = new Dictionary<long, DateTime>();

        private Thread worker;
        private ScanRunner runner;
        private volatile bool running;
        private DateTime startedUtc;
        private long[] excludedHandles = new long[0];

        private TargetWindowInfo currentWindow;
        private string currentScreenId;
        private List<ScanNode> currentNodes = new List<ScanNode>();
        private bool lastLeftDown;

        private ScanNode pendingField;
        private ElementRef pendingFieldRef;
        private string pendingFieldValue;
        private bool pendingFieldSecret;
        private string pendingFieldRule;
        private string pendingFieldScreen;
        private TargetWindowInfo pendingFieldWindow;

        public event Action<RecorderStatus> Progress;

        public Recorder(string baseDirectory, StudioSession studioSession, Acquire.ISurfaceGuard surfaceGuard, System.Windows.Threading.Dispatcher uiDispatcher)
        {
            baseDir = baseDirectory;
            session = studioSession;
            guard = surfaceGuard == null ? new Acquire.NullGuard() : surfaceGuard;
            dispatcher = uiDispatcher;
            ownProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            limits = new ScanLimits();
            // A recording acquires a window every time the operator moves to
            // one, so the per window allowance is tighter than a single
            // deliberate acquisition. The reduction is a stated limit, not a
            // silent one: it lands in the session as a coverage reason whenever
            // it actually truncates something.
            limits.MaxNodes = 1200;
            limits.UiaBudgetMs = 9000;
            limits.MsaaBudgetMs = 4000;
            limits.HitTestBudgetMs = 5000;
        }

        public void SetExcludedHandles(long[] handles)
        {
            lock (sync) { excludedHandles = handles == null ? new long[0] : handles; }
        }

        public bool Running { get { return running; } }

        public void Start()
        {
            if (running) return;
            running = true;
            startedUtc = DateTime.UtcNow;
            runner = new ScanRunner(baseDir, true);
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Name = "app-studio-recorder";
            worker.Start();
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Thread finishing = worker;
            worker = null;
            if (finishing != null && !finishing.Join(6000))
            {
                session.AddDiagnostic("RECORD-STOP: the recording thread did not finish within six seconds.");
            }
            if (runner != null)
            {
                runner.Dispose();
                runner = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Loop()
        {
            try
            {
                Poll();
            }
            catch (Exception exception)
            {
                session.AddDiagnostic("RECORD-FAIL: " + exception.GetType().Name + ": " + exception.Message);
                RecorderStatus status = new RecorderStatus();
                status.Problem = exception.GetType().Name + ": " + exception.Message;
                Report(status);
            }
        }

        private void Poll()
        {
            // The very first look establishes where the operator already is, so
            // the first thing they do is not attributed to nowhere.
            SwitchTo(WindowTools.Foreground(), true);
            while (running)
            {
                try
                {
                    TargetWindowInfo front = WindowTools.Foreground();
                    if (IsForeign(front) && (currentWindow == null || front.Hwnd != currentWindow.Hwnd)) SwitchTo(front, false);
                    else if (IsForeign(front) && currentWindow != null && !String.Equals(front.Title, currentWindow.Title, StringComparison.Ordinal)) TitleChanged(front);
                    else if (IsForeign(front)) currentWindow.Rect = front.Rect;
                    PollMouse();
                    PollKeys();
                    ReportTick();
                }
                catch (Exception exception)
                {
                    session.AddDiagnostic("RECORD-TICK: " + exception.GetType().Name + ": " + exception.Message);
                }
                Thread.Sleep(PollMs);
            }
            FlushPendingField("recording stopped", false);
        }

        private bool IsForeign(TargetWindowInfo window)
        {
            if (window == null || window.Hwnd == 0) return false;
            if (window.ProcessId == ownProcessId) return false;
            long[] excluded;
            lock (sync) { excluded = excludedHandles; }
            for (int index = 0; index < excluded.Length; index++) if (excluded[index] == window.Hwnd) return false;
            return true;
        }

        // Moving to another window is itself part of the procedure, so it is a
        // step. The window is acquired at that moment because that is when its
        // contents are what the operator is looking at.
        private void SwitchTo(TargetWindowInfo window, bool first)
        {
            if (!IsForeign(window)) return;
            FlushPendingField("moved to another window", false);
            TargetWindowInfo previous = currentWindow;
            currentWindow = window;
            AppRef app = session.Register(window.ProcessId);
            UpdateFrame(window.Rect);

            bool sameApp = previous != null && previous.ProcessId == window.ProcessId;
            StepRecord step = NewStep(StepRecord.KindAppSwitch, window, app);
            step.EffectSummary = first
                ? "the recording started with this window in front"
                : (sameApp ? "another window of the same application came to the front" : "a different application came to the front");
            Emit(step);

            currentScreenId = AcquireWindow(window, first ? "in front when the recording started" : "came to the front during the recording");
            step.ScreenAfter = currentScreenId;
            RewriteLastStep(step);
        }

        private void TitleChanged(TargetWindowInfo window)
        {
            FlushPendingField("the window changed", false);
            currentWindow = window;
            UpdateFrame(window.Rect);
            // A new title on the same window is a new screen of the same
            // application, which is exactly the transition a reader needs to
            // see, so it is acquired again rather than folded into the old one.
            currentScreenId = AcquireWindow(window, "the window title changed to: " + Text(window.Title));
        }

        private string AcquireWindow(TargetWindowInfo window, string note)
        {
            if (runner == null || window == null) return currentScreenId;
            try
            {
                ScanResult result = Acquire.Window(runner, session, window, limits, null);
                string screenId = null;
                if (result != null && result.Windows.Count > 0) screenId = session.Screens.Screens[session.Screens.Screens.Count - 1].ScreenId;
                if (screenId == null)
                {
                    session.AddLimit("A window of " + Text(window.ProcessName) + " could not be acquired while recording: " + Text(window.Title));
                    return currentScreenId;
                }
                ScreenRecord screen = session.Screens.Find(screenId);
                if (screen != null)
                {
                    screen.Note = note;
                    ShootIfDue(window, screen);
                }
                currentNodes = Acquire.NodesForScreen(session, screenId);
                screenByWindow[window.Hwnd] = screenId;
                return screenId;
            }
            catch (Exception exception)
            {
                session.AddDiagnostic("RECORD-ACQUIRE: " + exception.GetType().Name + ": " + exception.Message);
                session.AddLimit("A window could not be acquired while recording: " + exception.Message);
                return currentScreenId;
            }
        }

        private void ShootIfDue(TargetWindowInfo window, ScreenRecord screen)
        {
            DateTime last;
            if (lastKeyframe.TryGetValue(window.Hwnd, out last) && (DateTime.UtcNow - last).TotalMilliseconds < MinKeyframeGapMs)
            {
                screen.ShotProblem = "SHOT-THROTTLED: another picture of this window was taken less than " +
                    MinKeyframeGapMs.ToString(CultureInfo.InvariantCulture) + " ms earlier.";
                SessionStore.Append(session, "screens", screen.ToJson());
                return;
            }
            lastKeyframe[window.Hwnd] = DateTime.UtcNow;
            // A keyframe is a picture of somebody's application like any other,
            // so whatever this session calls a secret is blacked out in it too.
            // The window has just been acquired, so the rectangles are known.
            MaskRect[] masks = Acquire.SecretMasks(session, screen);
            guard.Suppress(true);
            try
            {
                Acquire.CheckSuppressed(screen, guard);
                ScreenCapture.Shoot(screen, session.ShotsFolder, masks);
            }
            catch (Exception exception)
            {
                screen.ShotProblem = "SHOT-FAILED: " + exception.GetType().Name + ": " + exception.Message;
            }
            finally
            {
                guard.Suppress(false);
            }
            Acquire.NoteMasking(screen, masks);
            if (!String.IsNullOrEmpty(screen.ShotProblem)) session.AddLimit("Screen " + screen.ScreenId + " has no picture: " + screen.ShotProblem);
            SessionStore.Append(session, "screens", screen.ToJson());
        }

        // Only the transition matters. Asking whether the button is down right
        // now misses a press that began and ended inside one polling interval,
        // which is what a trackpad tap looks like, so the "pressed since the
        // last call" bit is read as well.
        private void PollMouse()
        {
            short state = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
            bool down = (state & 0x8000) != 0;
            bool pressedSince = (state & 0x0001) != 0;
            bool press = (down && !lastLeftDown) || (!down && pressedSince && !lastLeftDown);
            lastLeftDown = down;
            if (!press) return;
            PointValue cursor = WindowTools.CursorPosition();
            if (cursor == null) return;
            OnClick(cursor.X, cursor.Y);
        }

        private void OnClick(int x, int y)
        {
            int owner = WindowTools.ProcessIdAt(x, y);
            if (owner == ownProcessId)
            {
                // The stop control and the frame belong to this product. A press
                // on them is not part of the procedure being recorded.
                return;
            }
            TargetWindowInfo front = WindowTools.Foreground();
            if (!IsForeign(front)) return;
            if (currentWindow == null || front.Hwnd != currentWindow.Hwnd) SwitchTo(front, false);
            FlushPendingField("the pointer moved to another element", false);

            AppRef app = session.Register(front.ProcessId);
            StepRecord step = NewStep(StepRecord.KindClick, front, app);
            step.Point = new PointValue();
            step.Point.X = x;
            step.Point.Y = y;
            step.ScreenBefore = currentScreenId;

            ElementRef reference = new ElementRef();
            reference.X = x;
            reference.Y = y;
            Snapshot snapshot = null;
            try
            {
                snapshot = Probe.At(x, y, 1200);
            }
            catch (Exception exception)
            {
                step.Diagnostics.Add("probe: " + exception.GetType().Name + ": " + exception.Message);
            }
            ScanNode node = Describe(snapshot, x, y);
            ScanNode acquired = Acquire.NodeAt(currentNodes, x, y);
            Fill(step, node, acquired, snapshot);

            Emit(step);
            AfterAction(step, front);

            // A field that was just pressed is where typing will land. It is
            // remembered so the text can later be read back from the field
            // itself, which is the only way this product ever learns what was
            // typed.
            ScanNode field = node != null && Acquire.LooksEditable(node) ? node : (Acquire.LooksEditable(acquired) ? acquired : null);
            if (field != null)
            {
                pendingField = field;
                pendingFieldRef = reference;
                pendingFieldSecret = Privacy.IsSecretElement(field) || (snapshot != null && snapshot.Uia != null && snapshot.Uia.IsPassword) ||
                    Privacy.HasPasswordStyle(field.Style, field.ClassName);
                pendingFieldRule = Privacy.SecretRuleFor(field);
                if (pendingFieldSecret && pendingFieldRule == null) pendingFieldRule = Privacy.SecretRuleIsPassword;
                pendingFieldValue = pendingFieldSecret ? null : ReadValue(reference);
                pendingFieldScreen = currentScreenId;
                pendingFieldWindow = front;
            }
        }

        // Command keys only. A letter on its own is text and never reaches this
        // list; a letter with Control or Alt held is a shortcut and does.
        private void PollKeys()
        {
            bool ctrl = IsDown(0x11);
            bool alt = IsDown(0x12);
            bool shift = IsDown(0x10);
            bool win = IsDown(0x5B) || IsDown(0x5C);
            bool command = ctrl || alt || win;
            int[] keys = KeyTable.Watched;
            for (int index = 0; index < keys.Length; index++)
            {
                int key = keys[index];
                if (KeyTable.IsModifier(key)) continue;
                short state = NativeMethods.GetAsyncKeyState(key);
                bool down = (state & 0x8000) != 0;
                // As with the pointer, asking only whether the key is down right
                // now misses a press that began and ended inside one polling
                // interval. The "pressed since the last call" bit catches those.
                bool pressedSince = (state & 0x0001) != 0;
                bool was;
                keyDown.TryGetValue(key, out was);
                keyDown[key] = down;
                if (was) continue;
                if (!down && !pressedSince) continue;
                bool alwaysSemantic = KeyTable.IsAlwaysSemantic(key);
                if (!alwaysSemantic && !command) continue;
                KeyChord(KeyTable.Chord(ctrl, alt, shift, win, key));
            }
        }

        private void Emit(StepRecord step)
        {
            if (step == null) return;
            session.Steps.Add(step);
            if (!SessionStore.Append(session, "steps", step.ToJson()))
            {
                session.AddDiagnostic("RECORD-WRITE: step " + step.StepId + " could not be written to disk.");
            }
        }

        private void RewriteLastStep(StepRecord step)
        {
            // The switch step is written the moment it happens so a forced exit
            // keeps it, and written again once the window has been acquired so
            // the screen it produced is attached. Both lines carry the same step
            // id, and the reader takes the last one.
            SessionStore.Append(session, "steps", step.ToJson());
        }

        private void KeyChord(string chord)
        {
            // A shortcut does not move the focus out of the field: Ctrl+A selects
            // what is in it and the operator carries on typing. So the field is
            // written down if it changed and then watched again from its new
            // content, instead of being forgotten - forgetting it here is how
            // everything typed after a shortcut would go unrecorded.
            FlushPendingField("a command key was pressed", true);
            TargetWindowInfo front = WindowTools.Foreground();
            if (!IsForeign(front)) return;
            AppRef app = session.Register(front.ProcessId);
            StepRecord step = NewStep(StepRecord.KindKeyChord, front, app);
            step.KeyChord = chord;
            step.ScreenBefore = currentScreenId;
            step.EffectSummary = "a command key was pressed; what it does is the application's business";
            Emit(step);
            // Looked at on this same thread, like a press is. Nothing about a
            // recording is allowed to run in parallel with itself: two threads
            // acquiring windows would interleave the screen numbering and the
            // element list, and the record would describe an order that never
            // happened.
            AfterAction(step, front);
        }

        private void AfterAction(StepRecord step, TargetWindowInfo before)
        {
            Thread.Sleep(260);
            TargetWindowInfo after = WindowTools.Foreground();
            if (!IsForeign(after)) return;
            bool moved = before == null || after.Hwnd != before.Hwnd;
            bool retitled = before != null && after.Hwnd == before.Hwnd && !String.Equals(after.Title, before.Title, StringComparison.Ordinal);
            step.WindowTitleAfter = after.Title;
            if (moved || retitled)
            {
                step.EffectSummary = moved
                    ? "the front window changed to: " + Text(after.Title)
                    : "the window title changed to: " + Text(after.Title);
                currentWindow = after;
                UpdateFrame(after.Rect);
                currentScreenId = AcquireWindow(after, moved ? "reached after " + step.StepId : "the title changed after " + step.StepId);
                step.ScreenAfter = currentScreenId;
            }
            else
            {
                if (String.IsNullOrEmpty(step.EffectSummary)) step.EffectSummary = "no window or title change was observed after this";
                step.ScreenAfter = currentScreenId;
            }
            SessionStore.Append(session, "steps", step.ToJson());
        }

        // Reads back what the field holds now and, if it differs from what it
        // held when it was pressed, writes down that a value was entered. The
        // characters are never taken from the keyboard.
        private void FlushPendingField(string why, bool keepWatching)
        {
            ScanNode field = pendingField;
            if (field == null) return;
            ElementRef reference = pendingFieldRef;
            string before = pendingFieldValue;
            bool secret = pendingFieldSecret;
            string rule = pendingFieldRule;
            string screen = pendingFieldScreen;
            TargetWindowInfo window = pendingFieldWindow;
            pendingField = null;
            pendingFieldRef = null;
            pendingFieldValue = null;
            pendingFieldSecret = false;
            pendingFieldRule = null;
            pendingFieldWindow = null;

            string after = secret ? null : ReadValue(reference);
            if (keepWatching)
            {
                // The focus never left, so watching continues from what the
                // field holds now.
                pendingField = field;
                pendingFieldRef = reference;
                pendingFieldValue = after;
                pendingFieldSecret = secret;
                pendingFieldRule = rule;
                pendingFieldScreen = screen;
                pendingFieldWindow = window;
            }
            if (!secret)
            {
                if (after == null && before == null) return;
                if (String.Equals(before, after, StringComparison.Ordinal)) return;
            }
            else
            {
                if (keepWatching) return;
                // A secret field cannot be compared, because its content is
                // never read. Whether anything was typed is not knowable, so the
                // step is written as "the operator may have entered something
                // here" rather than invented either way.
            }

            if (window == null) window = currentWindow;
            if (window == null) return;
            AppRef app = session.Register(window.ProcessId);
            StepRecord step = NewStep(secret ? StepRecord.KindSecretInput : StepRecord.KindTextInput, window, app);
            step.ScreenBefore = screen;
            step.ScreenAfter = currentScreenId;
            Fill(step, field, Acquire.NodeAt(currentNodes, reference == null ? 0 : reference.X, reference == null ? 0 : reference.Y), null);
            Privacy.ApplyValue(step, session.ValuePolicy, after, secret, rule);
            step.EffectSummary = "the field was left after " + why;
            if (secret) step.Diagnostics.Add("This field is treated as a secret, so neither its old nor its new content was read.");
            Emit(step);
        }

        private string ReadValue(ElementRef reference)
        {
            if (reference == null) return null;
            try
            {
                Snapshot snapshot = Probe.Deep(reference, 1500);
                if (snapshot == null || snapshot.Uia == null) return null;
                if (snapshot.Uia.IsPassword) return null;
                return snapshot.Uia.LiveValue;
            }
            catch
            {
                return null;
            }
        }

        private StepRecord NewStep(string kind, TargetWindowInfo window, AppRef app)
        {
            StepRecord step = new StepRecord();
            step.Index = session.Steps.Count + 1;
            step.At = DateTimeOffset.Now;
            step.OffsetMs = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
            step.Kind = kind;
            if (window != null)
            {
                step.ProcessId = window.ProcessId;
                step.Hwnd = window.Hwnd;
                step.TopHwnd = window.Hwnd;
                step.WindowTitle = window.Title;
                step.WindowClass = window.ClassName;
            }
            if (app != null)
            {
                step.AppKey = app.Key;
                step.AppName = app.Display;
            }
            return step;
        }

        // Turns one live acquisition at a point into the same shape the scan
        // produces, so a step and a scanned element describe an element with the
        // same words.
        private static ScanNode Describe(Snapshot snapshot, int x, int y)
        {
            if (snapshot == null) return null;
            ScanNode node = new ScanNode();
            node.Provider = "live";
            if (snapshot.Uia != null && snapshot.UiaStatus != null && snapshot.UiaStatus.State != "unavailable")
            {
                node.AddSource("uia");
                node.Name = snapshot.Uia.Name;
                node.AutomationId = snapshot.Uia.AutomationId;
                node.ControlType = snapshot.Uia.ControlType;
                node.LocalizedControlType = snapshot.Uia.LocalizedControlType;
                node.FrameworkId = snapshot.Uia.FrameworkId;
                node.Rect = snapshot.Uia.BoundingRect;
                node.IsPassword = snapshot.Uia.IsPassword;
                node.KeyboardFocusable = snapshot.Uia.IsKeyboardFocusable;
                node.Offscreen = snapshot.Uia.IsOffscreen;
                node.Enabled = snapshot.Uia.IsEnabled;
                node.HelpText = snapshot.Uia.HelpText;
                node.AcceleratorKey = snapshot.Uia.AcceleratorKey;
                node.AccessKey = snapshot.Uia.AccessKey;
                node.Patterns = snapshot.Uia.SupportedPatterns;
                node.RuntimeId = SessionLogJson.RuntimeIdText(snapshot.Uia.RuntimeId);
            }
            if (snapshot.Msaa != null && snapshot.MsaaStatus != null && snapshot.MsaaStatus.State != "unavailable")
            {
                node.AddSource("msaa");
                if (String.IsNullOrEmpty(node.Name)) node.Name = snapshot.Msaa.Name;
                node.Role = snapshot.Msaa.Role;
                node.StateText = snapshot.Msaa.StateText;
                if (node.Rect == null) node.Rect = snapshot.Msaa.Rect;
            }
            if (snapshot.Win32 != null)
            {
                node.AddSource("win32");
                node.Hwnd = snapshot.Win32.Hwnd;
                node.TopHwnd = snapshot.Win32.TopHwnd;
                node.ProcessId = snapshot.Win32.ProcessId;
                node.ClassName = snapshot.Win32.ClassName;
                node.RealClassName = snapshot.Win32.RealClass;
                node.CtrlId = snapshot.Win32.CtrlId;
                node.Style = snapshot.Win32.Style;
                node.ExStyle = snapshot.Win32.ExStyle;
                if (node.Rect == null) node.Rect = snapshot.Win32.WindowRect;
                if (String.IsNullOrEmpty(node.Name)) node.Name = snapshot.Win32.Caption;
            }
            if (node.Rect == null)
            {
                node.Rect = new RectValue();
                node.Rect.X = x;
                node.Rect.Y = y;
                node.Rect.Width = 1;
                node.Rect.Height = 1;
                node.AddNote("no rectangle was exposed; the pointer position is used as the position");
            }
            return node;
        }

        private void Fill(StepRecord step, ScanNode live, ScanNode acquired, Snapshot snapshot)
        {
            ScanNode node = live != null ? live : acquired;
            if (node == null)
            {
                step.Unavailable.Add("element-unidentified: nothing could be obtained about what is at that position.");
                step.Confidence = "none";
                return;
            }
            step.ElementLabel = node.DisplayLabel;
            step.ControlType = node.ControlType;
            step.LocalizedControlType = node.LocalizedControlType;
            step.Role = node.Role;
            step.Name = node.Name;
            step.AutomationId = node.AutomationId;
            step.ClassName = node.ClassName;
            step.RuntimeId = node.RuntimeId;
            step.CtrlId = node.CtrlId;
            step.Rect = node.Rect;
            for (int index = 0; index < node.Sources.Count; index++) step.Sources.Add(node.Sources[index]);

            // The hierarchy path only exists in the acquired list, because that
            // is where the whole tree was walked. Taking it from there keeps a
            // path locator resolvable against a later acquisition.
            if (acquired != null)
            {
                step.TreePath = acquired.Path;
                if (String.IsNullOrEmpty(step.AutomationId)) step.AutomationId = acquired.AutomationId;
                if (String.IsNullOrEmpty(step.ControlType)) step.ControlType = acquired.ControlType;
                if (String.IsNullOrEmpty(step.Name)) step.Name = acquired.Name;
                if (String.IsNullOrEmpty(step.ClassName)) step.ClassName = acquired.ClassName;
                if (step.CtrlId == 0) step.CtrlId = acquired.CtrlId;
                for (int index = 0; index < acquired.Sources.Count; index++)
                {
                    if (!step.Sources.Contains(acquired.Sources[index])) step.Sources.Add(acquired.Sources[index]);
                }
            }
            else
            {
                step.Unavailable.Add("tree-path-unknown: this element was not in the acquired list for the window, so no hierarchy path is available.");
            }

            ScanNode material = new ScanNode();
            material.Name = step.Name;
            material.AutomationId = step.AutomationId;
            material.ControlType = step.ControlType;
            material.ClassName = step.ClassName;
            material.CtrlId = step.CtrlId;
            material.Rect = step.Rect;
            material.Path = step.TreePath;
            step.Locators = LocatorBuilder.Build(material, currentWindow == null ? null : currentWindow.Rect, currentNodes);
            step.Confidence = LocatorBuilder.BestConfidence(step.Locators);
            bool identifiable = false;
            for (int index = 0; index < step.Locators.Count; index++)
            {
                if (LocatorResolver.Identifies(step.Locators[index].Strategy)) identifiable = true;
            }
            if (!identifiable)
            {
                // Position and sibling index are still written down, because
                // they are useful to whoever writes the automation afterwards.
                // They will not make this step replayable, and saying so here is
                // cheaper than finding out during a run.
                step.Unavailable.Add("no-identifying-locator: this element exposed no name, AutomationId, hierarchy path or control id, " +
                    "only where it sits. Replay will stop at this step rather than act on a position.");
            }
            if (snapshot != null)
            {
                if (snapshot.UiaStatus != null && snapshot.UiaStatus.State != "ok") step.Diagnostics.Add("uia: " + snapshot.UiaStatus.State);
                if (snapshot.MsaaStatus != null && snapshot.MsaaStatus.State != "ok") step.Diagnostics.Add("msaa: " + snapshot.MsaaStatus.State);
                if (snapshot.Win32Status != null && snapshot.Win32Status.State != "ok") step.Diagnostics.Add("win32: " + snapshot.Win32Status.State);
            }
        }

        private void UpdateFrame(RectValue rect)
        {
            if (dispatcher == null) return;
            RectValue copy = rect;
            dispatcher.BeginInvoke(new Action(delegate
            {
                RecordHud hud = guard as RecordHud;
                if (hud != null) hud.FollowWindow(copy);
            }));
        }

        private void ReportTick()
        {
            RecorderStatus status = new RecorderStatus();
            status.Elapsed = DateTime.UtcNow - startedUtc;
            status.StepCount = session.Steps.Count;
            status.ScreenCount = session.Screens.Screens.Count;
            status.AppName = currentWindow == null ? null : (currentWindow.ProcessName ?? ("pid " + currentWindow.ProcessId));
            status.WindowTitle = currentWindow == null ? null : currentWindow.Title;
            status.WindowRect = currentWindow == null ? null : currentWindow.Rect;
            if (session.Steps.Count > 0) status.LastStep = session.Steps[session.Steps.Count - 1].Headline;
            Report(status);
        }

        private void Report(RecorderStatus status)
        {
            Action<RecorderStatus> handler = Progress;
            if (handler == null || dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(delegate { handler(status); }));
        }

        private static bool IsDown(int virtualKey)
        {
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static string Text(string value)
        {
            return String.IsNullOrEmpty(value) ? "(no title)" : value;
        }
    }

    // The fixed list of keys whose state is ever looked at, and the names they
    // are written down under. It is a list rather than a range on purpose:
    // anything not on it is never even asked about, so ordinary typing cannot
    // reach a record by accident.
    public static class KeyTable
    {
        public static readonly int[] Watched = BuildWatched();

        public static bool IsModifier(int key)
        {
            return key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5B || key == 0x5C;
        }

        // Keys that mean something on their own. Everything else on the watch
        // list is only written down while Control, Alt or the Windows key is
        // held, which is what makes it a shortcut rather than text.
        public static bool IsAlwaysSemantic(int key)
        {
            if (key == 0x0D || key == 0x09 || key == 0x1B) return true;
            return key >= 0x70 && key <= 0x7B;
        }

        public static string Chord(bool ctrl, bool alt, bool shift, bool win, int key)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            if (ctrl) text.Append("Ctrl+");
            if (alt) text.Append("Alt+");
            if (shift) text.Append("Shift+");
            if (win) text.Append("Win+");
            text.Append(Name(key));
            return text.ToString();
        }

        public static string Name(int key)
        {
            switch (key)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x1B: return "Escape";
                case 0x20: return "Space";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
            }
            if (key >= 0x30 && key <= 0x39) return ((char)key).ToString();
            if (key >= 0x41 && key <= 0x5A) return ((char)key).ToString();
            if (key >= 0x70 && key <= 0x7B) return "F" + (key - 0x6F).ToString(CultureInfo.InvariantCulture);
            return "VK" + key.ToString("X2", CultureInfo.InvariantCulture);
        }

        // Turns a written chord back into the keys that make it, for replay.
        // Anything unrecognised returns false rather than being approximated.
        public static bool TryParse(string chord, out int[] modifiers, out int key)
        {
            modifiers = new int[0];
            key = 0;
            if (String.IsNullOrEmpty(chord)) return false;
            string[] parts = chord.Split('+');
            List<int> found = new List<int>();
            for (int index = 0; index < parts.Length - 1; index++)
            {
                string part = parts[index].Trim();
                if (String.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)) found.Add(0x11);
                else if (String.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) found.Add(0x12);
                else if (String.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) found.Add(0x10);
                else if (String.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)) found.Add(0x5B);
                else return false;
            }
            string last = parts[parts.Length - 1].Trim();
            for (int index = 0; index < Watched.Length; index++)
            {
                if (String.Equals(Name(Watched[index]), last, StringComparison.Ordinal))
                {
                    key = Watched[index];
                    modifiers = found.ToArray();
                    return true;
                }
            }
            return false;
        }

        private static int[] BuildWatched()
        {
            List<int> keys = new List<int>();
            keys.Add(0x10);
            keys.Add(0x11);
            keys.Add(0x12);
            keys.Add(0x5B);
            keys.Add(0x5C);
            keys.Add(0x09);
            keys.Add(0x0D);
            keys.Add(0x1B);
            keys.Add(0x08);
            keys.Add(0x20);
            keys.Add(0x21);
            keys.Add(0x22);
            keys.Add(0x23);
            keys.Add(0x24);
            keys.Add(0x25);
            keys.Add(0x26);
            keys.Add(0x27);
            keys.Add(0x28);
            keys.Add(0x2D);
            keys.Add(0x2E);
            for (int key = 0x30; key <= 0x39; key++) keys.Add(key);
            for (int key = 0x41; key <= 0x5A; key++) keys.Add(key);
            for (int key = 0x70; key <= 0x7B; key++) keys.Add(key);
            return keys.ToArray();
        }
    }
}
