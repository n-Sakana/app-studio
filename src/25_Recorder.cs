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
    // What comes out is two layers over the same events. The timeline is every
    // event the watch saw, in order, with the interval between them: presses,
    // releases, drags, wheel turns, command keys down and up, the moments the
    // front window or the keyboard focus moved. The steps are what those events
    // meant, described by element rather than by coordinate, and they are what
    // replay carries out.
    //
    // Two things it deliberately does not do. It does not read the keyboard
    // stream: shortcut keys are recognised from the key state of a fixed list of
    // command keys, and typed text is never taken from key traffic at all, it is
    // read back out of the field that received it. And it does not write down a
    // flood of pointer movement: pressing, releasing, dragging and turning the
    // wheel are events, moving the pointer about is not.
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
        private readonly Dictionary<long, string> screenByWindow = new Dictionary<long, string>();
        private readonly Dictionary<long, DateTime> lastKeyframe = new Dictionary<long, DateTime>();

        private Thread worker;
        private Thread sampler;
        private ScanRunner runner;
        private PointerWatch pointer;
        private volatile bool running;
        private DateTime startedUtc;
        private long[] excludedHandles = new long[0];
        // What the sampler saw, waiting for the worker to describe it. The two
        // are separate threads on purpose: see SampleLoop.
        private readonly System.Collections.Concurrent.ConcurrentQueue<InputMoment> pending =
            new System.Collections.Concurrent.ConcurrentQueue<InputMoment>();
        private int droppedByBacklog;

        private int inputSequence;
        private DateTime lastEventUtc = DateTime.MinValue;
        private DateTime lastActionUtc = DateTime.MinValue;
        private DateTime lastWheelUtc = DateTime.MinValue;
        private DateTime processingUtc = DateTime.MinValue;
        private long focusedControl;

        private TargetWindowInfo currentWindow;
        private string currentScreenId;
        private List<ScanNode> currentNodes = new List<ScanNode>();

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
            sampler = new Thread(SampleLoop);
            sampler.IsBackground = true;
            sampler.Name = "app-studio-recorder-input";
            sampler.Priority = ThreadPriority.AboveNormal;
            sampler.Start();
        }

        // Watching the input and describing what it landed on have to be two
        // different threads.
        //
        // Describing one press costs hundreds of milliseconds - a UI Automation
        // probe, and sometimes a whole window acquisition. A single thread that
        // describes a press while the operator carries on working is not
        // watching during that time, and whatever happened meanwhile is lost.
        //
        // This loop therefore does nothing but collect events, which costs
        // microseconds, and hands each moment to the worker. Order is preserved
        // because there is exactly one reader of the queue.
        //
        // The pointer is collected through the operating system's own low level
        // hook, which is the only place a wheel turn, the release point of a
        // drag and a second click inside the double click time exist at all. The
        // keyboard is not hooked: the state of a fixed list of command keys is
        // read once per tick, and ordinary typing is noticed without ever asking
        // which key it was.
        private void SampleLoop()
        {
            const int SampleMs = 10;
            PointerWatch watch = new PointerWatch(Enqueue);
            pointer = watch;
            bool hooked = watch.Install();
            session.InputWatchState = watch.State;
            if (!hooked)
            {
                session.AddLimit("POINTER-WATCH: the pointer could not be watched at the event level (" +
                    (watch.Problem == null ? "no reason given" : watch.Problem) + "). Presses are sampled instead, " +
                    "so which button was used, a double click, a drag and a wheel turn are missing from this recording.");
            }
            bool wasDown = false;
            Dictionary<int, bool> keys = new Dictionary<int, bool>();
            Dictionary<int, string> recorded = new Dictionary<int, string>();
            DateTime lastTyping = DateTime.MinValue;
            while (running)
            {
                try
                {
                    if (hooked) watch.Pump();
                    else SamplePointer(ref wasDown);
                    SampleKeyboard(keys, recorded, ref lastTyping);
                }
                catch
                {
                    // A sampling tick that throws must not stop the watch; the
                    // worker records anything that actually goes wrong.
                }
                Thread.Sleep(SampleMs);
            }
            watch.Dispose();
        }

        // The reduced watch, used only when the hook could not be installed. It
        // can see that the left button went down and nothing else, which is why
        // reaching this path is stated as a limit on the session.
        private void SamplePointer(ref bool wasDown)
        {
            short state = NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON);
            bool down = (state & 0x8000) != 0;
            bool pressedSince = (state & 0x0001) != 0;
            if (!wasDown && (down || pressedSince))
            {
                PointValue cursor = WindowTools.CursorPosition();
                if (cursor != null)
                {
                    InputMoment moment = new InputMoment();
                    moment.Kind = InputKinds.Click;
                    moment.Button = MouseButtons.Left;
                    moment.X = cursor.X;
                    moment.Y = cursor.Y;
                    moment.AtUtc = DateTime.UtcNow;
                    moment.Modifiers = KeyTable.ModifierText();
                    moment.Foreground = NativeMethods.GetForegroundWindow().ToInt64();
                    Enqueue(moment);
                }
            }
            wasDown = down;
        }

        // Every watched key is read exactly once per tick. Reading a key state
        // clears the "pressed since you last asked" bit, so two readers of the
        // same key would each see half the presses.
        private void SampleKeyboard(Dictionary<int, bool> keys, Dictionary<int, string> recorded, ref DateTime lastTyping)
        {
            bool ctrl = IsDown(0x11);
            bool alt = IsDown(0x12);
            bool shift = IsDown(0x10);
            bool win = IsDown(0x5B) || IsDown(0x5C);
            bool command = ctrl || alt || win;
            bool typed = false;
            int[] watched = KeyWatch.All;
            for (int index = 0; index < watched.Length; index++)
            {
                int key = watched[index];
                if (KeyTable.IsModifier(key)) continue;
                short state = NativeMethods.GetAsyncKeyState(key);
                bool down = (state & 0x8000) != 0;
                bool pressedSince = (state & 0x0001) != 0;
                bool was;
                keys.TryGetValue(key, out was);
                keys[key] = down;
                bool wentDown = !was && (down || pressedSince);
                bool wentUp = was && !down;
                bool isCommandKey = KeyTable.IsWatched(key) && (KeyTable.IsAlwaysSemantic(key) || command);
                if (wentDown && isCommandKey)
                {
                    InputMoment moment = New(InputKinds.KeyDown);
                    moment.Chord = KeyTable.Chord(ctrl, alt, shift, win, key);
                    moment.Key = key;
                    moment.Modifiers = KeyTable.ModifierText(ctrl, alt, shift, win);
                    recorded[key] = moment.Chord;
                    Enqueue(moment);
                    continue;
                }
                if (wentDown && KeyWatch.IsTyping(key)) typed = true;
                if (!wentUp) continue;
                string chord;
                if (!recorded.TryGetValue(key, out chord)) continue;
                recorded.Remove(key);
                InputMoment release = New(InputKinds.KeyUp);
                release.Chord = chord;
                release.Key = key;
                Enqueue(release);
            }
            if (!typed || command) return;
            // Which key it was is deliberately not passed on. All this says is
            // that typing is happening, so the field that is receiving it can be
            // read back - or, if no field is known, so the recording can say
            // that text was entered somewhere it could not identify.
            if ((DateTime.UtcNow - lastTyping).TotalMilliseconds < 220) return;
            lastTyping = DateTime.UtcNow;
            Enqueue(New(InputKinds.Typing));
        }

        private static InputMoment New(string kind)
        {
            InputMoment moment = new InputMoment();
            moment.Kind = kind;
            moment.AtUtc = DateTime.UtcNow;
            moment.Modifiers = KeyTable.ModifierText();
            moment.Foreground = NativeMethods.GetForegroundWindow().ToInt64();
            return moment;
        }

        // The queue is bounded so a wedged worker cannot grow it without limit.
        // Reaching the bound is a lost step, so it is counted and stated rather
        // than passed over.
        private void Enqueue(InputMoment moment)
        {
            if (pending.Count >= 512)
            {
                System.Threading.Interlocked.Increment(ref droppedByBacklog);
                return;
            }
            pending.Enqueue(moment);
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            Thread watching = sampler;
            sampler = null;
            if (watching != null && !watching.Join(2000))
            {
                session.AddDiagnostic("RECORD-STOP: the input watch did not finish within two seconds.");
            }
            Thread finishing = worker;
            worker = null;
            if (finishing != null && !finishing.Join(20000))
            {
                session.AddDiagnostic("RECORD-STOP: the recording thread did not finish within twenty seconds.");
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
            // the first thing they do is not attributed to nowhere. That includes
            // the keyboard: a recording that starts with the cursor already
            // sitting in a field has to know about that field, or everything
            // typed into it before the first click is missing.
            SwitchTo(WindowTools.Foreground(), true);
            FollowFocus("the recording started with the keyboard here", true);
            while (running)
            {
                try
                {
                    // Everything the operator did is taken from the queue, in
                    // order, so a slow description never loses the next press.
                    InputMoment moment;
                    while (running && pending.TryDequeue(out moment)) Handle(moment);

                    TargetWindowInfo front = WindowTools.Foreground();
                    if (IsForeign(front) && (currentWindow == null || front.Hwnd != currentWindow.Hwnd)) SwitchTo(front, false);
                    else if (IsForeign(front) && currentWindow != null && !String.Equals(front.Title, currentWindow.Title, StringComparison.Ordinal)) TitleChanged(front);
                    else if (IsForeign(front)) currentWindow.Rect = front.Rect;
                    FollowFocus("the keyboard moved to another control", false);
                    ReportTick();
                }
                catch (Exception exception)
                {
                    session.AddDiagnostic("RECORD-TICK: " + exception.GetType().Name + ": " + exception.Message);
                }
                Thread.Sleep(PollMs);
            }
            // Whatever the operator did just before pressing stop is still in
            // the queue and is part of the recording.
            InputMoment last;
            while (pending.TryDequeue(out last))
            {
                try { Handle(last); }
                catch (Exception exception) { session.AddDiagnostic("RECORD-DRAIN: " + exception.GetType().Name + ": " + exception.Message); }
            }
            FlushPendingField("recording stopped", false);
            int lost = droppedByBacklog;
            if (lost > 0)
            {
                session.AddLimit(lost + " input event(s) were dropped because the description could not keep up with the operator. " +
                    "Those actions are missing from this recording.");
            }
        }

        private void Handle(InputMoment moment)
        {
            if (moment == null) return;
            int lagMs = (int)(DateTime.UtcNow - moment.AtUtc).TotalMilliseconds;
            string kind = moment.Kind;
            if (kind == InputKinds.MouseDown || kind == InputKinds.MouseUp)
            {
                // The press and the release are part of the timeline. What they
                // amounted to - a click, a double click or a drag - arrives as
                // its own moment once the button is up.
                Timeline(moment, null, null, null);
                return;
            }
            // Anything this event causes to be written down - a window that came
            // to the front, a field that was left, a focus that moved - belongs
            // beside the event, not at the later moment the worker noticed it.
            processingUtc = moment.AtUtc;
            try
            {
                if (kind == InputKinds.Click || kind == InputKinds.DoubleClick) OnClick(moment, lagMs);
                else if (kind == InputKinds.Drag) OnDrag(moment, lagMs);
                else if (kind == InputKinds.Wheel) OnWheel(moment, lagMs);
                else if (kind == InputKinds.KeyDown) KeyChord(moment, lagMs);
                else if (kind == InputKinds.KeyUp) OnKeyUp(moment);
                else if (kind == InputKinds.Typing) OnTyping(moment);
            }
            finally
            {
                processingUtc = DateTime.MinValue;
            }
        }

        // One row of the raw timeline. Every moment the watch produced goes
        // through here, including the ones that became no step, because a gap in
        // the timeline is exactly what a reader cannot tell from an idle
        // operator.
        private InputEventRecord Timeline(InputMoment moment, string stepId, string elementLabel, string note)
        {
            InputEventRecord record = new InputEventRecord();
            record.Index = ++inputSequence;
            // The rows are in the order things were dealt with, and each row
            // carries the time its own event happened. Noticing a transition
            // costs time, so one noticed while an earlier event was still being
            // described could otherwise be stamped before it and the timeline
            // would run backwards. A row is therefore never given a time earlier
            // than the row before it. The adjustment is at most one watch tick
            // and it is stated here rather than left to be discovered.
            DateTime when = moment.AtUtc;
            if (lastEventUtc != DateTime.MinValue && when < lastEventUtc) when = lastEventUtc;
            record.At = new DateTimeOffset(when.ToLocalTime());
            record.OffsetMs = (int)(when - startedUtc).TotalMilliseconds;
            record.GapMs = lastEventUtc == DateTime.MinValue ? 0 : (int)(when - lastEventUtc).TotalMilliseconds;
            if (record.GapMs < 0) record.GapMs = 0;
            lastEventUtc = when;
            record.Kind = moment.Kind;
            record.Button = moment.Button;
            record.Key = moment.Chord;
            record.Modifiers = moment.Modifiers;
            record.X = moment.X;
            record.Y = moment.Y;
            record.ToX = moment.ToX;
            record.ToY = moment.ToY;
            record.WheelDelta = moment.WheelDelta;
            record.HoldMs = moment.HoldMs;
            record.Hwnd = moment.Foreground;
            record.StepId = stepId;
            record.ElementLabel = elementLabel;
            record.Note = note;
            if (moment.X != 0 || moment.Y != 0)
            {
                record.Dpi = DpiTools.GetDpiAt(moment.X, moment.Y);
                record.MonitorId = DpiTools.MonitorIdAt(moment.X, moment.Y);
            }
            if (currentWindow != null)
            {
                record.AppName = currentWindow.ProcessName;
                record.WindowTitle = currentWindow.Title;
            }
            session.InputEvents.Add(record);
            SessionStore.Append(session, "input", record.ToJson());
            return record;
        }

        // A transition the watch cannot see because it is not an input event at
        // all: the front window changed, or the keyboard moved. Written into the
        // same timeline so the order of everything is one order.
        private void TimelineNote(string kind, string note, string elementLabel)
        {
            InputMoment moment = new InputMoment();
            moment.Kind = kind;
            moment.AtUtc = processingUtc == DateTime.MinValue ? DateTime.UtcNow : processingUtc;
            moment.Button = null;
            moment.Foreground = currentWindow == null ? 0 : currentWindow.Hwnd;
            Timeline(moment, null, elementLabel, note);
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
            TimelineNote(InputKinds.Foreground, step.EffectSummary + ": " + Text(window.Title), null);

            currentScreenId = AcquireWindow(window, first ? "in front when the recording started" : "came to the front during the recording");
            step.ScreenAfter = currentScreenId;
            RewriteLastStep(step);
        }

        private void TitleChanged(TargetWindowInfo window)
        {
            FlushPendingField("the window changed", false);
            currentWindow = window;
            UpdateFrame(window.Rect);
            TimelineNote(InputKinds.Foreground, "the window title changed to: " + Text(window.Title), null);
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

        private void OnClick(InputMoment moment, int lagMs)
        {
            if (moment.Kind == InputKinds.DoubleClick && Promote(moment)) return;
            int x = moment.X;
            int y = moment.Y;
            TargetWindowInfo front = Target(moment, x, y);
            if (front == null) return;
            FlushPendingField("the pointer moved to another element", false);

            AppRef app = session.Register(front.ProcessId);
            string kind = moment.Kind == InputKinds.DoubleClick ? StepRecord.KindDoubleClick : StepRecord.KindClick;
            StepRecord step = NewStep(kind, front, app, moment.AtUtc);
            NoteLag(step, lagMs);
            Place(step, moment, x, y);
            step.ScreenBefore = currentScreenId;

            ElementRef reference = new ElementRef();
            reference.X = x;
            reference.Y = y;
            Snapshot snapshot = Look(step, x, y);
            ScanNode node = Describe(snapshot, x, y);
            ScanNode acquired = Acquire.NodeAt(currentNodes, x, y);
            Fill(step, node, acquired, snapshot);

            Emit(step);
            Timeline(moment, step.StepId, step.ElementLabel, null);
            AfterAction(step, front);

            // A field that was just pressed is where typing will land. It is
            // remembered so the text can later be read back from the field
            // itself, which is the only way this product ever learns what was
            // typed.
            ScanNode field = node != null && Acquire.LooksEditable(node) ? node : (Acquire.LooksEditable(acquired) ? acquired : null);
            if (field != null) Arm(field, reference, snapshot, front);
        }

        // A press on the same spot inside the double click time is not another
        // click, it is the second half of one double click. The step that was
        // already written is turned into that double click rather than a second
        // step being added, which is what would make replay press twice as
        // often as the operator did.
        private bool Promote(InputMoment moment)
        {
            StepRecord step = LastStep();
            if (step == null || step.Kind != StepRecord.KindClick || step.Point == null) return false;
            if (!String.Equals(step.Button, moment.Button, StringComparison.Ordinal)) return false;
            if (Math.Max(Math.Abs(step.Point.X - moment.X), Math.Abs(step.Point.Y - moment.Y)) > 8) return false;
            if ((moment.AtUtc - step.At.UtcDateTime).TotalMilliseconds > 1500) return false;
            step.Kind = StepRecord.KindDoubleClick;
            step.EffectSummary = "a second press arrived inside the double click time, so this is one double click";
            SessionStore.Append(session, "steps", step.ToJson());
            Timeline(moment, step.StepId, step.ElementLabel, "completes " + step.StepId + " into a double click");
            return true;
        }

        private void OnDrag(InputMoment moment, int lagMs)
        {
            TargetWindowInfo front = Target(moment, moment.X, moment.Y);
            if (front == null) return;
            FlushPendingField("a drag started somewhere else", false);
            AppRef app = session.Register(front.ProcessId);
            StepRecord step = NewStep(StepRecord.KindDrag, front, app, moment.AtUtc);
            NoteLag(step, lagMs);
            Place(step, moment, moment.X, moment.Y);
            step.ToPoint = new PointValue();
            step.ToPoint.X = moment.ToX;
            step.ToPoint.Y = moment.ToY;
            step.ScreenBefore = currentScreenId;
            Snapshot snapshot = Look(step, moment.X, moment.Y);
            ScanNode node = Describe(snapshot, moment.X, moment.Y);
            ScanNode acquired = Acquire.NodeAt(currentNodes, moment.X, moment.Y);
            Fill(step, node, acquired, snapshot);

            // Where it was let go is described exactly the way where it started
            // is - the live look and the acquired list put together - because a
            // drop described from one of those alone gets the weaker half of the
            // material and stops resolving. Without a description of the drop,
            // replaying a drag would have to aim at a remembered coordinate,
            // which is not replaying a procedure.
            string dropLabel;
            List<ElementLocator> dropLocators = LocatorsAt(step, moment.ToX, moment.ToY, "drop", out dropLabel);
            if (dropLocators.Count > 0)
            {
                step.DropLabel = dropLabel;
                step.DropLocators = dropLocators;
            }
            else
            {
                step.Unavailable.Add("drop-target-unknown: nothing could be obtained about where this drag was released, " +
                    "so replay will stop here rather than let go at a remembered position.");
            }
            step.EffectSummary = "the pointer was dragged from one place to another";
            Emit(step);
            Timeline(moment, step.StepId, step.ElementLabel, null);
            AfterAction(step, front);
        }

        private void OnWheel(InputMoment moment, int lagMs)
        {
            TargetWindowInfo front = Target(moment, moment.X, moment.Y);
            if (front == null) return;
            // One turn of a wheel produces a burst of notches. They are one
            // gesture, so they become one step whose total is what the operator
            // actually scrolled; every notch still stands in the timeline.
            StepRecord last = LastStep();
            if (last != null && last.Kind == StepRecord.KindWheel && last.Point != null &&
                Math.Max(Math.Abs(last.Point.X - moment.X), Math.Abs(last.Point.Y - moment.Y)) <= 8 &&
                lastWheelUtc != DateTime.MinValue && (moment.AtUtc - lastWheelUtc).TotalMilliseconds <= 500)
            {
                last.WheelDelta += moment.WheelDelta;
                last.EffectSummary = "the wheel was turned; the total for this gesture is " +
                    last.WheelDelta.ToString(CultureInfo.InvariantCulture);
                lastWheelUtc = moment.AtUtc;
                SessionStore.Append(session, "steps", last.ToJson());
                Timeline(moment, last.StepId, last.ElementLabel, "part of " + last.StepId);
                return;
            }
            FlushPendingField("the wheel was used", true);
            AppRef app = session.Register(front.ProcessId);
            StepRecord step = NewStep(StepRecord.KindWheel, front, app, moment.AtUtc);
            NoteLag(step, lagMs);
            Place(step, moment, moment.X, moment.Y);
            step.WheelDelta = moment.WheelDelta;
            step.ScreenBefore = currentScreenId;
            Snapshot snapshot = Look(step, moment.X, moment.Y);
            ScanNode node = Describe(snapshot, moment.X, moment.Y);
            ScanNode acquired = Acquire.NodeAt(currentNodes, moment.X, moment.Y);
            Fill(step, node, acquired, snapshot);
            step.EffectSummary = "the wheel was turned over this element";
            lastWheelUtc = moment.AtUtc;
            Emit(step);
            Timeline(moment, step.StepId, step.ElementLabel, null);
            AfterAction(step, front);
        }

        // Notes that typing is happening. Nothing here knows or asks which key
        // it was; all it does is make sure the field that is receiving the text
        // is being watched, and say so when there is no such field.
        private void OnTyping(InputMoment moment)
        {
            if (pendingField != null)
            {
                Timeline(moment, null, pendingField.DisplayLabel, "text is being entered into a field this recording is watching");
                return;
            }
            FollowFocus("text arrived while the keyboard was here", true);
            if (pendingField != null)
            {
                Timeline(moment, null, pendingField.DisplayLabel, "the field receiving this text was found from the keyboard focus");
                return;
            }
            Timeline(moment, null, null, "text was entered, but no field could be identified to read it back from");
            session.AddLimit("TEXT-UNSEEN: text was entered while no field could be identified. " +
                "What was typed is not in this recording, because this product only ever reads a value back from the element that received it.");
        }

        private void OnKeyUp(InputMoment moment)
        {
            StepRecord step = LastStep();
            if (step != null && step.Kind == StepRecord.KindKeyChord && String.Equals(step.KeyChord, moment.Chord, StringComparison.Ordinal))
            {
                step.HoldMs = (int)(moment.AtUtc - step.At.UtcDateTime).TotalMilliseconds;
                if (step.HoldMs < 0) step.HoldMs = 0;
                SessionStore.Append(session, "steps", step.ToJson());
                Timeline(moment, step.StepId, null, null);
                return;
            }
            Timeline(moment, null, null, null);
        }

        // The window a pointer event belongs to, or nothing when the event is
        // not part of the procedure. Either way the event stays in the timeline,
        // because "the operator pressed our own stop button here" is a thing a
        // reader needs to see rather than a silent hole.
        private TargetWindowInfo Target(InputMoment moment, int x, int y)
        {
            if (WindowTools.ProcessIdAt(x, y) == ownProcessId)
            {
                Timeline(moment, null, null, "on a control belonging to App Studio itself, so it is not part of the procedure");
                return null;
            }
            TargetWindowInfo front = WindowTools.Foreground();
            if (!IsForeign(front))
            {
                Timeline(moment, null, null, "the window in front is not one this recording follows");
                return null;
            }
            if (currentWindow == null || front.Hwnd != currentWindow.Hwnd) SwitchTo(front, false);
            return front;
        }

        private void Place(StepRecord step, InputMoment moment, int x, int y)
        {
            step.Point = new PointValue();
            step.Point.X = x;
            step.Point.Y = y;
            step.Button = moment.Button;
            step.Modifiers = moment.Modifiers;
            step.HoldMs = moment.HoldMs;
            step.Dpi = DpiTools.GetDpiAt(x, y);
            step.MonitorId = DpiTools.MonitorIdAt(x, y);
        }

        private Snapshot Look(StepRecord step, int x, int y)
        {
            try
            {
                return Probe.At(x, y, 1200);
            }
            catch (Exception exception)
            {
                step.Diagnostics.Add("probe: " + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private StepRecord LastStep()
        {
            return session.Steps.Count == 0 ? null : session.Steps[session.Steps.Count - 1];
        }

        // How a point is addressed again later. The live look supplies the name,
        // the AutomationId and the control type; the acquired list supplies the
        // place in the hierarchy. Either one on its own produces a description
        // that will not resolve, so both are put together here exactly as Fill
        // does for the element a step acted on.
        private List<ElementLocator> LocatorsAt(StepRecord step, int x, int y, string what, out string label)
        {
            label = null;
            Snapshot snapshot = null;
            try
            {
                snapshot = Probe.At(x, y, 900);
            }
            catch (Exception exception)
            {
                step.Diagnostics.Add(what + " probe: " + exception.GetType().Name + ": " + exception.Message);
            }
            ScanNode live = Describe(snapshot, x, y);
            ScanNode acquired = Acquire.NodeAt(currentNodes, x, y);
            ScanNode node = live != null ? live : acquired;
            if (node == null) return new List<ElementLocator>();
            ScanNode material = new ScanNode();
            material.Name = node.Name;
            material.AutomationId = node.AutomationId;
            material.ControlType = node.ControlType;
            material.ClassName = node.ClassName;
            material.CtrlId = node.CtrlId;
            material.Rect = node.Rect;
            if (acquired != null)
            {
                material.Path = acquired.Path;
                if (String.IsNullOrEmpty(material.AutomationId)) material.AutomationId = acquired.AutomationId;
                if (String.IsNullOrEmpty(material.ControlType)) material.ControlType = acquired.ControlType;
                if (String.IsNullOrEmpty(material.Name)) material.Name = acquired.Name;
                if (String.IsNullOrEmpty(material.ClassName)) material.ClassName = acquired.ClassName;
                if (material.CtrlId == 0) material.CtrlId = acquired.CtrlId;
                if (material.Rect == null) material.Rect = acquired.Rect;
            }
            label = material.DisplayLabel;
            return LocatorBuilder.Build(material, currentWindow == null ? null : currentWindow.Rect, currentNodes);
        }

        private ScanNode NodeForHandle(long hwnd)
        {
            if (hwnd == 0) return null;
            for (int index = 0; index < currentNodes.Count; index++)
            {
                if (currentNodes[index].Hwnd == hwnd) return currentNodes[index];
            }
            return null;
        }

        // What had the keyboard at the moment of this step. A shortcut delivered
        // to whatever happens to be focused during a replay is not the recorded
        // procedure, so replay puts the keyboard back here first - and says so
        // when it cannot.
        private void RememberFocus(StepRecord step)
        {
            long focus = WindowTools.FocusedControl();
            if (focus == 0) return;
            RectValue rect = WindowTools.GetPhysicalRect(new IntPtr(focus));
            if (rect == null || rect.Width <= 0 || rect.Height <= 0) return;
            // The control that owns the window handle is a better answer than
            // whatever happens to sit under its middle, which for a document is
            // often a decoration with no name at all.
            ScanNode node = NodeForHandle(focus);
            if (node == null) node = Acquire.NodeAt(currentNodes, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            if (node == null)
            {
                step.Diagnostics.Add("the control holding the keyboard was not in the acquired list, so replay cannot put the keyboard back before this step");
                return;
            }
            step.FocusLabel = node.DisplayLabel;
            step.FocusLocators = LocatorBuilder.Build(node, currentWindow == null ? null : currentWindow.Rect, currentNodes);
        }

        private void Arm(ScanNode field, ElementRef reference, Snapshot snapshot, TargetWindowInfo window)
        {
            pendingField = field;
            pendingFieldRef = reference;
            pendingFieldSecret = Privacy.IsSecretElement(field) || (snapshot != null && snapshot.Uia != null && snapshot.Uia.IsPassword) ||
                Privacy.HasPasswordStyle(field.Style, field.ClassName);
            pendingFieldRule = Privacy.SecretRuleFor(field);
            if (pendingFieldSecret && pendingFieldRule == null) pendingFieldRule = Privacy.SecretRuleIsPassword;
            pendingFieldValue = pendingFieldSecret ? null : ReadValue(reference);
            pendingFieldScreen = currentScreenId;
            pendingFieldWindow = window;
        }

        // Where the keyboard is, and what to do when it moves. Tab moves it, a
        // dialog moves it, and so does the application itself; in every case the
        // field that was being watched has been left and a new one may need
        // watching. Without this, everything typed into a field the operator
        // never clicked on - including the field a window opens with - is
        // missing from the recording.
        private void FollowFocus(string why, bool force)
        {
            long focus = WindowTools.FocusedControl();
            if (!force && focus == focusedControl) return;
            focusedControl = focus;
            if (focus == 0) return;
            TargetWindowInfo front = WindowTools.Foreground();
            if (!IsForeign(front)) return;
            FlushPendingField(why, false);
            RectValue rect = WindowTools.GetPhysicalRect(new IntPtr(focus));
            if (rect == null || rect.Width <= 0 || rect.Height <= 0) return;
            int x = rect.X + rect.Width / 2;
            int y = rect.Y + rect.Height / 2;
            ScanNode node = Acquire.NodeAt(currentNodes, x, y);
            Snapshot snapshot = null;
            if (node == null || !Acquire.LooksEditable(node))
            {
                try { snapshot = Probe.At(x, y, 900); }
                catch (Exception exception) { session.AddDiagnostic("RECORD-FOCUS: " + exception.GetType().Name + ": " + exception.Message); }
                ScanNode live = Describe(snapshot, x, y);
                if (live != null && Acquire.LooksEditable(live)) node = live;
            }
            string label = node == null ? null : node.DisplayLabel;
            TimelineNote(InputKinds.Focus, why, label);
            if (node == null || !Acquire.LooksEditable(node)) return;
            ElementRef reference = new ElementRef();
            reference.X = x;
            reference.Y = y;
            reference.Hwnd = focus;
            Arm(node, reference, snapshot, front);
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

        private void KeyChord(InputMoment moment, int lagMs)
        {
            // A shortcut does not move the focus out of the field: Ctrl+A selects
            // what is in it and the operator carries on typing. So the field is
            // written down if it changed and then watched again from its new
            // content, instead of being forgotten - forgetting it here is how
            // everything typed after a shortcut would go unrecorded.
            FlushPendingField("a command key was pressed", true);
            TargetWindowInfo front = WindowTools.Foreground();
            if (!IsForeign(front))
            {
                Timeline(moment, null, null, "the window in front is not one this recording follows");
                return;
            }
            AppRef app = session.Register(front.ProcessId);
            StepRecord step = NewStep(StepRecord.KindKeyChord, front, app, moment.AtUtc);
            NoteLag(step, lagMs);
            step.KeyChord = moment.Chord;
            step.Modifiers = moment.Modifiers;
            step.ScreenBefore = currentScreenId;
            step.EffectSummary = "a command key was pressed; what it does is the application's business";
            RememberFocus(step);
            Emit(step);
            Timeline(moment, step.StepId, step.FocusLabel, null);
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
            TimelineNote(InputKinds.Text, "text was entered into " + (step.ElementLabel == null ? "a field" : step.ElementLabel) +
                " and read back from it: " + why, step.ElementLabel);
        }

        // How far behind the operator the description ran. A large value means
        // the element was looked at after the application had already moved on,
        // so the reader is told rather than left to assume it was instantaneous.
        private static void NoteLag(StepRecord step, int lagMs)
        {
            if (step == null || lagMs < 400) return;
            step.Diagnostics.Add("described " + lagMs + " ms after it happened; the element was read at that later moment");
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
            return NewStep(kind, window, app, DateTime.UtcNow);
        }

        // The time written down is when the operator acted, not when this thread
        // got round to describing it. The interval since the previous action is
        // written down with it, because a procedure carried out at a speed
        // nobody worked at is a different procedure.
        private StepRecord NewStep(string kind, TargetWindowInfo window, AppRef app, DateTime atUtc)
        {
            StepRecord step = new StepRecord();
            step.Index = session.Steps.Count + 1;
            step.At = new DateTimeOffset(atUtc.ToLocalTime());
            step.OffsetMs = (int)(atUtc - startedUtc).TotalMilliseconds;
            step.GapMs = lastActionUtc == DateTime.MinValue ? 0 : (int)(atUtc - lastActionUtc).TotalMilliseconds;
            if (step.GapMs < 0) step.GapMs = 0;
            lastActionUtc = atUtc;
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

    // Every key whose state is read during a recording: the command keys, whose
    // names are written down, and the typing keys, whose names never are. They
    // are read together in one pass because reading a key state clears the
    // "pressed since you last asked" bit, so two passes over overlapping lists
    // would each see half the presses.
    public static class KeyWatch
    {
        public static readonly int[] All = Build();

        public static bool IsTyping(int key)
        {
            int[] keys = TypingKeys.Watched;
            for (int index = 0; index < keys.Length; index++) if (keys[index] == key) return true;
            return false;
        }

        private static int[] Build()
        {
            List<int> keys = new List<int>();
            for (int index = 0; index < KeyTable.Watched.Length; index++) keys.Add(KeyTable.Watched[index]);
            int[] typing = TypingKeys.Watched;
            for (int index = 0; index < typing.Length; index++)
            {
                if (!keys.Contains(typing[index])) keys.Add(typing[index]);
            }
            return keys.ToArray();
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

        public static bool IsWatched(int key)
        {
            for (int index = 0; index < Watched.Length; index++) if (Watched[index] == key) return true;
            return false;
        }

        // Which modifiers are held right now, written the same way everywhere so
        // a timeline row, a step and the report all say "Ctrl+Shift".
        public static string ModifierText()
        {
            return ModifierText(
                (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0,
                (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0,
                (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0,
                (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0);
        }

        public static string ModifierText(bool ctrl, bool alt, bool shift, bool win)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            if (ctrl) text.Append("Ctrl");
            if (alt) { if (text.Length > 0) text.Append("+"); text.Append("Alt"); }
            if (shift) { if (text.Length > 0) text.Append("+"); text.Append("Shift"); }
            if (win) { if (text.Length > 0) text.Append("+"); text.Append("Win"); }
            return text.Length == 0 ? null : text.ToString();
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
            if (key >= 0x60 && key <= 0x69) return "Num" + (key - 0x60).ToString(CultureInfo.InvariantCulture);
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
            for (int key = 0x60; key <= 0x69; key++) keys.Add(key);
            for (int key = 0x70; key <= 0x7B; key++) keys.Add(key);
            return keys.ToArray();
        }
    }
}
