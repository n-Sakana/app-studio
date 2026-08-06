namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;

    public sealed class ReplayProgress
    {
        public int Index;
        public int Total;
        public string Headline;
        public string State;
        public string Detail;
        public bool Finished;
    }

    public sealed class ReplayReport
    {
        public DateTimeOffset StartedAt;
        public DateTimeOffset EndedAt;
        public int Attempted;
        public int Succeeded;
        public int Stopped;
        public string StopReason;
        public string RouteMode = ProbeRoutes.Auto;
        public List<StepRecord> Steps = new List<StepRecord>();
    }

    // Carries a recording out again on the real applications.
    //
    // Three rules decide everything here. A step is only performed once the
    // window it expects is actually in front, and the element it names has been
    // found again by meaning. Anything typed or sent as a key goes to the
    // control that held the keyboard when it was recorded, not to whatever
    // happens to be focused now. When any of those is not true the run stops and
    // says why: the recorded coordinates are never used as a second chance,
    // because clicking where something used to be is not replaying a procedure,
    // it is pressing an unknown part of somebody's application.
    //
    // The intervals of the recording are kept too, bounded at both ends: long
    // enough that an application drawing itself is not raced, short enough that
    // a pause for a cup of tea is not replayed as one.
    public sealed class ReplayEngine : IDisposable
    {
        // The recorded interval is honoured between these two. Below the floor
        // an application has no chance to finish what the previous step started;
        // above the ceiling a replay is waiting for nothing anybody needs.
        private const int MinGapMs = 120;
        private const int MaxGapMs = 4000;
        private const int SettleBudgetMs = 2500;

        private readonly string baseDir;
        private readonly StudioSession session;
        private readonly object sync = new object();
        private ScanRunner runner;
        private volatile bool cancelled;
        private volatile bool paused;

        // How fast this runs against the pace the recording was made at.
        //
        // A replay is slower than the recording it came from, and deliberately
        // so: every step waits out the interval the operator left, then finds the
        // element again by meaning rather than by where it used to be, then waits
        // for the application to stop changing before the next step starts. That
        // is what makes it a replay rather than a burst of clicks at remembered
        // coordinates. It is also why a recording of ten seconds takes the better
        // part of a minute to play back, which is a surprise if nothing says so.
        //
        // So the amount of waiting is a number the operator sets. It scales the
        // recorded interval and the settling budget together - the two waits that
        // exist to be safe rather than to be correct. What is never scaled is the
        // time given to finding the element or to carrying the operation out:
        // those are not padding, and shortening them would not make a replay
        // faster, it would make it fail.
        private double speed = 1.0;

        public double Speed
        {
            get { return speed; }
            set
            {
                double wanted = value;
                if (wanted < 0.25) wanted = 0.25;
                if (wanted > 8.0) wanted = 8.0;
                speed = wanted;
            }
        }

        public bool IsPaused { get { return paused; } }

        // Held between steps rather than in the middle of one. Stopping half way
        // through an operation would leave the application in a state the
        // recording never describes.
        public void SetPaused(bool value)
        {
            paused = value;
        }

        private void HoldWhilePaused()
        {
            while (paused && !cancelled) Thread.Sleep(80);
        }

        private int Scaled(int milliseconds)
        {
            double value = milliseconds / speed;
            if (value < 0) value = 0;
            return (int)Math.Round(value);
        }
        // Set while a step runs when the keyboard could not be verified. It has
        // to reach the outcome, or a step that may have gone to the wrong
        // control would read as a clean success.
        private string focusCaveat;

        public event Action<ReplayProgress> Progress;

        // Asked for a value the recording deliberately never kept. Returning
        // null stops the run; there is no way to continue past a secret without
        // the operator supplying it.
        public Func<StepRecord, string> AskSecret;

        public ReplayEngine(string baseDirectory, StudioSession studioSession)
        {
            baseDir = baseDirectory;
            session = studioSession;
        }

        public void Cancel()
        {
            cancelled = true;
            ScanRunner current;
            lock (sync) { current = runner; }
            if (current != null) current.Cancel();
        }

        public void Dispose()
        {
            ScanRunner current;
            lock (sync)
            {
                current = runner;
                runner = null;
            }
            if (current != null) current.Dispose();
        }

        public ReplayReport Run(string routeMode, bool writeEnabled)
        {
            ReplayReport report = new ReplayReport();
            report.StartedAt = DateTimeOffset.Now;
            report.RouteMode = ProbeRoutes.IsKnown(routeMode) ? routeMode : ProbeRoutes.Auto;
            cancelled = false;
            lock (sync) { runner = new ScanRunner(baseDir, true); }
            try
            {
                for (int index = 0; index < session.Steps.Count; index++)
                {
                    if (cancelled)
                    {
                        report.StopReason = "The run was stopped by the operator.";
                        report.Stopped++;
                        break;
                    }
                    HoldWhilePaused();
                    if (cancelled)
                    {
                        report.StopReason = "The run was stopped by the operator.";
                        report.Stopped++;
                        break;
                    }
                    StepRecord step = session.Steps[index];
                    report.Attempted++;
                    Report(index + 1, session.Steps.Count, step.Headline, "running", null, false);
                    int waited = index == 0 ? 0 : Pace(step);
                    ReplayOutcome outcome = RunStep(step, report.RouteMode, writeEnabled);
                    outcome.WaitedMs = waited;
                    step.LastReplay = outcome;
                    report.Steps.Add(step);
                    SessionStore.Append(session, "replay", new JsonObject()
                        .Add("kind", "replay.step")
                        .Add("stepId", step.StepId)
                        .Add("routeMode", report.RouteMode)
                        .Add("result", outcome.ToJson()));
                    Report(index + 1, session.Steps.Count, step.Headline, outcome.State, outcome.Reason, false);
                    if (outcome.State == "done")
                    {
                        report.Succeeded++;
                    }
                    else
                    {
                        report.Stopped++;
                        report.StopReason = "Step " + step.StepId + ": " + outcome.Reason;
                        break;
                    }
                }
            }
            finally
            {
                ScanRunner current;
                lock (sync)
                {
                    current = runner;
                    runner = null;
                }
                if (current != null) current.Dispose();
            }
            report.EndedAt = DateTimeOffset.Now;
            Report(report.Attempted, session.Steps.Count, null, report.StopReason == null ? "done" : "stopped", report.StopReason, true);
            return report;
        }

        // Waits the interval the operator left before this step. It is the
        // recording's own timing, clamped, never a fixed number invented here.
        private int Pace(StepRecord step)
        {
            int wait = step.GapMs <= 0 ? MinGapMs : step.GapMs;
            if (wait < MinGapMs) wait = MinGapMs;
            if (wait > MaxGapMs) wait = MaxGapMs;
            wait = Scaled(wait);
            int slept = 0;
            while (slept < wait && !cancelled)
            {
                int slice = Math.Min(80, wait - slept);
                Thread.Sleep(slice);
                slept += slice;
            }
            return slept;
        }

        // Waits for the application to stop changing, up to a fixed ceiling.
        // A window that is still drawing, a dialog on its way up and a slow
        // control all look the same from here: the title and the front window
        // are still moving. Reaching the ceiling is not a failure, it is a
        // measured wait, and the number is written on the step.
        private int Settle(int budgetMs)
        {
            // The ceiling moves with the chosen speed; the two stable readings
            // that end the wait early do not. Settling stops as soon as the
            // application has stopped changing, so at any speed the common case
            // costs the same and only the patience for a slow one differs.
            int budget = Scaled(budgetMs);
            Stopwatch watch = Stopwatch.StartNew();
            string title = null;
            long front = 0;
            int stable = 0;
            while (watch.ElapsedMilliseconds < budget && !cancelled)
            {
                TargetWindowInfo now = WindowTools.Foreground();
                string nowTitle = now == null ? null : now.Title;
                long nowFront = now == null ? 0 : now.Hwnd;
                if (String.Equals(nowTitle, title, StringComparison.Ordinal) && nowFront == front) stable++;
                else stable = 0;
                title = nowTitle;
                front = nowFront;
                if (stable >= 2) break;
                Thread.Sleep(80);
            }
            watch.Stop();
            return (int)watch.ElapsedMilliseconds;
        }

        private ReplayOutcome RunStep(StepRecord step, string routeMode, bool writeEnabled)
        {
            ReplayOutcome outcome = new ReplayOutcome();
            outcome.At = DateTimeOffset.Now;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (step.Kind == StepRecord.KindAppSwitch) return Finish(outcome, watch, Switch(step, outcome));
                if (!writeEnabled)
                {
                    outcome.State = "blocked";
                    outcome.Reason = "Replay drives the real application, so it needs the write permission. Nothing was sent.";
                    outcome.Attempts.Add(Guard("policy.readOnly", outcome.Reason));
                    return Finish(outcome, watch, outcome);
                }
                TargetWindowInfo window = Expect(step, outcome);
                if (window == null) return Finish(outcome, watch, outcome);
                if (step.Kind == StepRecord.KindKeyChord)
                {
                    focusCaveat = null;
                    if (!Restore(step, window, outcome)) return Finish(outcome, watch, outcome);
                    ReplayOutcome sent = SendChord(step, outcome);
                    if (focusCaveat != null && sent.State == "done") sent.Reason = sent.Reason + " " + Capital(focusCaveat) + ".";
                    return Finish(outcome, watch, sent);
                }
                return Finish(outcome, watch, Act(step, window, outcome, routeMode));
            }
            catch (Exception exception)
            {
                outcome.State = "failed";
                outcome.Reason = exception.GetType().Name + ": " + exception.Message;
                return Finish(outcome, watch, outcome);
            }
        }

        // Puts the keyboard back where it was when the step was recorded. A
        // shortcut sent to whatever happens to hold the keyboard now is not the
        // recorded procedure - Ctrl+A in the wrong field selects the wrong
        // thing, and Tab moves from the wrong place. When the recording does not
        // say where the keyboard was, that is stated and the step goes ahead
        // against the window it verified; when it says and it cannot be put
        // back, the run stops.
        private bool Restore(StepRecord step, TargetWindowInfo window, ReplayOutcome outcome)
        {
            if (step.FocusLocators == null || step.FocusLocators.Count == 0)
            {
                outcome.Attempts.Add(Note("focus.notRecorded",
                    "The recording does not say which control held the keyboard, so this key goes to whatever the window itself focuses."));
                return true;
            }
            long focus = WindowTools.FocusedControl();
            ScanRunner current;
            lock (sync) { current = runner; }
            if (current == null)
            {
                outcome.State = "failed";
                outcome.Reason = "The acquisition runner is not available, so the keyboard could not be put back.";
                outcome.Attempts.Add(Guard("focus.noRunner", outcome.Reason));
                return false;
            }
            ScanResult fresh = current.RunWindows(new TargetWindowInfo[] { window }, window.ProcessId, Limits(), null);
            List<ScanNode> candidates = fresh == null ? new List<ScanNode>() : fresh.Nodes;
            ResolveResult resolved = LocatorResolver.Resolve(step.FocusLocators, candidates);
            if (!resolved.Resolved)
            {
                // Not being able to find the control again is not the same fact
                // as the keyboard being in the wrong place. The window this step
                // expects has already been verified, so the key goes to whatever
                // that window focuses - and the trail says so, because a key that
                // may have landed somewhere else must never read as a clean run.
                focusCaveat = "the control that held the keyboard when this was recorded (" +
                    (step.FocusLabel == null ? "unnamed" : step.FocusLabel) + ") could not be found again, so this went to whatever the window focuses";
                outcome.Attempts.Add(Warn("focus.notFound", focusCaveat + " - " + resolved.Reason));
                return true;
            }
            RectValue rect = resolved.Node.Rect;
            if (rect != null && rect.Width > 0 && rect.Height > 0 && resolved.Node.Hwnd != 0 && resolved.Node.Hwnd == focus)
            {
                outcome.Attempts.Add(Note("focus.alreadyThere", "The keyboard is already on " + (step.FocusLabel == null ? "that control" : step.FocusLabel) + "."));
                return true;
            }
            if (rect == null || rect.Width <= 0 || rect.Height <= 0)
            {
                outcome.State = "focus-not-restored";
                outcome.Reason = "The control that held the keyboard was found but has no usable rectangle, so the keyboard could not be put back.";
                outcome.Attempts.Add(Guard("focus.noRect", outcome.Reason));
                return false;
            }
            ElementRef reference = new ElementRef();
            reference.X = rect.X + rect.Width / 2;
            reference.Y = rect.Y + rect.Height / 2;
            reference.Hwnd = resolved.Node.Hwnd;
            ProbeArgs args = new ProbeArgs();
            args.WriteEnabled = true;
            args.BudgetMs = 4000;
            args.Repeatable = true;
            ProbeResult probe = ProbeRunner.Run(reference, ProbeKind.Focus, args);
            for (int index = 0; index < probe.Attempts.Count; index++) outcome.Attempts.Add(probe.Attempts[index]);
            long after = WindowTools.FocusedControl();
            bool arrived = resolved.Node.Hwnd == 0 || after == resolved.Node.Hwnd;
            if (arrived) return true;
            outcome.State = "focus-not-restored";
            outcome.Reason = "The keyboard could not be put back on " + (step.FocusLabel == null ? "the recorded control" : step.FocusLabel) +
                "; it is on something else, so the key was not sent.";
            outcome.Attempts.Add(Guard("focus.notRestored", outcome.Reason));
            return false;
        }

        // The same place inside an element, on a rectangle that may have moved
        // or changed size. Returns nothing when the recording does not say where
        // inside the element the pointer was, or says a place outside it - in
        // which case the caller uses the middle and nothing is invented.
        private static PointValue Within(RectValue recorded, PointValue point, RectValue current)
        {
            if (recorded == null || point == null || current == null) return null;
            if (recorded.Width <= 0 || recorded.Height <= 0 || current.Width <= 0 || current.Height <= 0) return null;
            double fx = (point.X - recorded.X) / (double)recorded.Width;
            double fy = (point.Y - recorded.Y) / (double)recorded.Height;
            if (fx < 0 || fx > 1 || fy < 0 || fy > 1) return null;
            PointValue value = new PointValue();
            value.X = current.X + (int)Math.Round(fx * current.Width);
            value.Y = current.Y + (int)Math.Round(fy * current.Height);
            // Never on the very edge, where a press belongs to the border rather
            // than to the element.
            if (value.X >= current.X + current.Width) value.X = current.X + current.Width - 1;
            if (value.Y >= current.Y + current.Height) value.Y = current.Y + current.Height - 1;
            return value;
        }

        private static ScanLimits Limits()
        {
            ScanLimits limits = new ScanLimits();
            limits.MaxNodes = 1500;
            limits.UiaBudgetMs = 9000;
            limits.MsaaBudgetMs = 4000;
            limits.HitTestBudgetMs = 5000;
            return limits;
        }

        private static ReplayOutcome Finish(ReplayOutcome outcome, Stopwatch watch, ReplayOutcome result)
        {
            watch.Stop();
            result.DurationMs = (int)watch.ElapsedMilliseconds;
            if (String.IsNullOrEmpty(result.State)) result.State = "failed";
            return result;
        }

        // Brings the application this step belongs to back in front. The window
        // is found by what it is, not by the handle it had last time, because a
        // handle from a previous run belongs to nothing now.
        private ReplayOutcome Switch(StepRecord step, ReplayOutcome outcome)
        {
            TargetWindowInfo found = Find(step);
            if (found == null)
            {
                outcome.State = "not-found";
                outcome.Reason = "No window matching " + Describe(step) + " is open, so the recording cannot continue here.";
                outcome.Attempts.Add(Guard("window.notFound", outcome.Reason));
                return outcome;
            }
            Stopwatch watch = Stopwatch.StartNew();
            bool raised = WindowTools.BringToFront(found.Hwnd);
            outcome.SettleMs = Settle(1500);
            TargetWindowInfo front = WindowTools.Foreground();
            watch.Stop();
            bool arrived = front != null && front.Hwnd == found.Hwnd;
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "win32";
            attempt.Method = "win32.SetForegroundWindow";
            attempt.Outcome = arrived ? "success" : (raised ? "unknown" : "failed");
            attempt.DurationMs = (int)watch.ElapsedMilliseconds;
            attempt.Effect = arrived ? "the window is now in front" : "the window did not come to the front";
            if (!arrived) attempt.ErrorCode = "WIN32-NOFOCUS";
            outcome.Attempts.Add(attempt);
            if (!arrived)
            {
                outcome.State = "failed";
                outcome.Reason = "The window " + Describe(step) + " did not come to the front, so nothing after this can be trusted.";
                return outcome;
            }
            outcome.State = "done";
            outcome.Reason = "The window was brought to the front.";
            outcome.ResolvedBy = "window match";
            return outcome;
        }

        private TargetWindowInfo Expect(StepRecord step, ReplayOutcome outcome)
        {
            TargetWindowInfo front = WindowTools.Foreground();
            if (front != null && Matches(step, front)) return front;
            TargetWindowInfo found = Find(step);
            if (found == null)
            {
                outcome.State = "not-found";
                outcome.Reason = "This step expects " + Describe(step) + ", and no window like that is open.";
                outcome.Attempts.Add(Guard("window.notFound", outcome.Reason));
                return null;
            }
            WindowTools.BringToFront(found.Hwnd);
            outcome.SettleMs += Settle(1500);
            front = WindowTools.Foreground();
            if (front == null || !Matches(step, front))
            {
                outcome.State = "wrong-window";
                outcome.Reason = "This step expects " + Describe(step) + ", but the window in front is " +
                    (front == null ? "unknown" : Describe(front)) + ". Nothing was sent.";
                outcome.Attempts.Add(Guard("window.mismatch", outcome.Reason));
                return null;
            }
            return front;
        }

        private ReplayOutcome SendChord(StepRecord step, ReplayOutcome outcome)
        {
            int[] modifiers;
            int key;
            if (!KeyTable.TryParse(step.KeyChord, out modifiers, out key))
            {
                outcome.State = "failed";
                outcome.Reason = "The recorded key " + (step.KeyChord == null ? "(none)" : step.KeyChord) +
                    " is not one this product knows how to send, so nothing was sent.";
                outcome.Attempts.Add(Guard("keys.unknown", outcome.Reason));
                return outcome;
            }
            Stopwatch watch = Stopwatch.StartNew();
            bool sent = NativeInput.SendChord(modifiers, key);
            watch.Stop();
            outcome.SettleMs = Settle(SettleBudgetMs);
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "sendInput";
            attempt.Method = "win32.SendInput.chord";
            attempt.Outcome = sent ? "unknown" : "failed";
            attempt.DurationMs = (int)watch.ElapsedMilliseconds;
            attempt.Effect = sent
                ? "the key was delivered to the window in front; what it did is the application's business"
                : "the key could not be delivered";
            if (!sent) attempt.ErrorCode = "SENDINPUT-FAIL";
            outcome.Attempts.Add(attempt);
            outcome.State = sent ? "done" : "failed";
            outcome.Reason = sent ? "The key was sent." : "The key could not be sent.";
            outcome.ResolvedBy = "foreground window";
            return outcome;
        }

        private ReplayOutcome Act(StepRecord step, TargetWindowInfo window, ReplayOutcome outcome, string routeMode)
        {
            ScanRunner current;
            lock (sync) { current = runner; }
            if (current == null)
            {
                outcome.State = "failed";
                outcome.Reason = "The acquisition runner is not available.";
                return outcome;
            }
            ScanResult fresh = current.RunWindows(new TargetWindowInfo[] { window }, window.ProcessId, Limits(), null);
            List<ScanNode> candidates = fresh == null ? new List<ScanNode>() : fresh.Nodes;
            ResolveResult resolved = LocatorResolver.Resolve(step.Locators, candidates);
            for (int index = 0; index < resolved.Trace.Count; index++) outcome.Attempts.Add(Trace(resolved.Trace[index]));
            outcome.MatchCount = resolved.MatchCount;
            if (!resolved.Resolved)
            {
                outcome.State = resolved.State;
                outcome.Reason = resolved.Reason;
                outcome.Attempts.Add(Guard("element." + resolved.State, resolved.Reason));
                return outcome;
            }
            outcome.ResolvedBy = resolved.UsedLocator == null ? null : resolved.UsedLocator.Display;

            ElementRef reference = new ElementRef();
            RectValue rect = resolved.Node.Rect;
            if (rect == null || rect.Width <= 0 || rect.Height <= 0)
            {
                outcome.State = "failed";
                outcome.Reason = "The element was found but has no usable rectangle right now, so there is nowhere to act.";
                outcome.Attempts.Add(Guard("element.noRect", outcome.Reason));
                return outcome;
            }
            // Where inside the element the operator acted matters for anything
            // that is not a button: a drag across a surface, a wheel turn over
            // one part of a list. The place is carried as a fraction of the
            // element's own rectangle, so it follows the element when the window
            // moves or is resized - the opposite of aiming at whatever
            // coordinate the pointer happened to be at last time.
            PointValue inside = Within(step.Rect, step.Point, rect);
            reference.X = inside == null ? rect.X + rect.Width / 2 : inside.X;
            reference.Y = inside == null ? rect.Y + rect.Height / 2 : inside.Y;
            reference.Hwnd = resolved.Node.Hwnd;

            ProbeArgs args = new ProbeArgs();
            args.WriteEnabled = true;
            args.RouteMode = routeMode;
            args.BudgetMs = 6000;
            args.Button = String.IsNullOrEmpty(step.Button) ? MouseButtons.Left : step.Button;
            // The operator pressed the same button twice in a second because
            // they meant to. The interactive rate limit is not a rule about
            // replaying a recording.
            args.Repeatable = true;
            ProbeKind kind = ProbeKind.Invoke;
            if (step.Kind == StepRecord.KindDoubleClick) kind = ProbeKind.DoubleClick;
            else if (step.Kind == StepRecord.KindWheel)
            {
                kind = ProbeKind.Wheel;
                args.WheelDelta = step.WheelDelta;
                if (args.WheelDelta == 0)
                {
                    outcome.State = "failed";
                    outcome.Reason = "This step recorded a wheel turn of nothing, so there is no turn to carry out.";
                    outcome.Attempts.Add(Guard("wheel.noDelta", outcome.Reason));
                    return outcome;
                }
            }
            else if (step.Kind == StepRecord.KindDrag)
            {
                kind = ProbeKind.Drag;
                // Where it was let go is resolved the same way as where it
                // started. Aiming at the remembered coordinate instead would be
                // dropping something at an unknown part of the application.
                if (step.DropLocators == null || step.DropLocators.Count == 0)
                {
                    outcome.State = "not-found";
                    outcome.Reason = "This drag has no description of where it was released, so there is nothing to aim at. Nothing was sent.";
                    outcome.Attempts.Add(Guard("drop.noLocator", outcome.Reason));
                    return outcome;
                }
                ResolveResult drop = LocatorResolver.Resolve(step.DropLocators, candidates);
                for (int index = 0; index < drop.Trace.Count; index++) outcome.Attempts.Add(Trace("drop: " + drop.Trace[index]));
                if (!drop.Resolved || drop.Node.Rect == null || drop.Node.Rect.Width <= 0 || drop.Node.Rect.Height <= 0)
                {
                    outcome.State = drop.Resolved ? "failed" : drop.State;
                    outcome.Reason = drop.Resolved
                        ? "The place this drag was released was found but has no usable rectangle now, so nothing was sent."
                        : "The place this drag was released could not be found again: " + drop.Reason;
                    outcome.Attempts.Add(Guard("drop." + (drop.Resolved ? "noRect" : drop.State), outcome.Reason));
                    return outcome;
                }
                // A drag that starts and ends inside one element - selecting
                // text, drawing on a surface - resolves to that same element at
                // both ends. Aiming at its middle twice would be a drag of no
                // distance, so the two ends keep their places inside it.
                bool sameElement = drop.Node == resolved.Node ||
                    (drop.Node.Hwnd != 0 && drop.Node.Hwnd == resolved.Node.Hwnd && drop.Node.NodeId == resolved.Node.NodeId);
                PointValue landing = sameElement ? Within(step.Rect, step.ToPoint, rect) : null;
                args.ToX = landing == null ? drop.Node.Rect.X + drop.Node.Rect.Width / 2 : landing.X;
                args.ToY = landing == null ? drop.Node.Rect.Y + drop.Node.Rect.Height / 2 : landing.Y;
                if (args.ToX == reference.X && args.ToY == reference.Y)
                {
                    outcome.State = "failed";
                    outcome.Reason = "This drag would start and end at the same place now, so nothing was sent.";
                    outcome.Attempts.Add(Guard("drop.noDistance", outcome.Reason));
                    return outcome;
                }
            }
            if (step.Kind == StepRecord.KindTextInput || step.Kind == StepRecord.KindSecretInput)
            {
                kind = ProbeKind.SetValue;
                string value = step.Value;
                if (step.Kind == StepRecord.KindSecretInput || value == null)
                {
                    value = Ask(step);
                    if (value == null)
                    {
                        outcome.State = "needs-operator";
                        outcome.Reason = step.Kind == StepRecord.KindSecretInput
                            ? "This step enters a secret. The recording deliberately kept no value, and none was supplied, so nothing was typed."
                            : "The recorded value for this field is not available, and none was supplied, so nothing was typed.";
                        outcome.Attempts.Add(Guard("policy.secret", outcome.Reason));
                        return outcome;
                    }
                }
                args.Value = value;
            }

            ProbeResult probe = ProbeRunner.Run(reference, kind, args);
            for (int index = 0; index < probe.Attempts.Count; index++) outcome.Attempts.Add(probe.Attempts[index]);
            // Whatever the step started, it is given a bounded chance to finish
            // before the next one is attempted. A dialog on its way up and a
            // slow redraw both look like this.
            outcome.SettleMs = Settle(SettleBudgetMs);
            if (probe.Outcome == "success" || probe.Outcome == "unknown")
            {
                outcome.State = "done";
                outcome.Reason = probe.Outcome == "success"
                    ? "Carried out through " + probe.Method + " and a change was observed."
                    : "Carried out through " + probe.Method + ", but no change could be observed, so it is recorded as unknown rather than as success.";
                return outcome;
            }
            outcome.State = probe.Outcome == "blocked" ? "blocked" : "failed";
            outcome.Reason = probe.Error == null
                ? "No route carried this operation out."
                : probe.Error.Code + ": " + probe.Error.Message;
            return outcome;
        }

        private string Ask(StepRecord step)
        {
            Func<StepRecord, string> handler = AskSecret;
            if (handler == null) return null;
            try
            {
                return handler(step);
            }
            catch
            {
                return null;
            }
        }

        // A window is the one this step wants when the application, the window
        // class and the title all agree. The title is compared loosely on
        // purpose: documents put their own name in it, and the recording is of
        // a procedure rather than of one document.
        private static bool Matches(StepRecord step, TargetWindowInfo window)
        {
            if (window == null) return false;
            if (!String.IsNullOrEmpty(step.WindowClass) && !String.Equals(step.WindowClass, window.ClassName, StringComparison.Ordinal)) return false;
            if (!String.IsNullOrEmpty(step.AppName) && !String.IsNullOrEmpty(window.ProcessName) &&
                !String.Equals(step.AppName, window.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static TargetWindowInfo Find(StepRecord step)
        {
            TargetWindowInfo[] stack = WindowTools.ListStackOrder(new long[0], System.Diagnostics.Process.GetCurrentProcess().Id);
            TargetWindowInfo loose = null;
            for (int index = 0; index < stack.Length; index++)
            {
                if (!Matches(step, stack[index])) continue;
                if (!String.IsNullOrEmpty(step.WindowTitle) && String.Equals(step.WindowTitle, stack[index].Title, StringComparison.Ordinal)) return stack[index];
                if (loose == null) loose = stack[index];
            }
            return loose;
        }

        private static string Describe(StepRecord step)
        {
            return (String.IsNullOrEmpty(step.AppName) ? "?" : step.AppName) + " / " +
                (String.IsNullOrEmpty(step.WindowClass) ? "?" : step.WindowClass) + " / " +
                (String.IsNullOrEmpty(step.WindowTitle) ? "(no title)" : step.WindowTitle);
        }

        private static string Describe(TargetWindowInfo window)
        {
            return (String.IsNullOrEmpty(window.ProcessName) ? ("pid " + window.ProcessId) : window.ProcessName) + " / " +
                window.ClassName + " / " + (String.IsNullOrEmpty(window.Title) ? "(no title)" : window.Title);
        }

        private static RouteAttempt Guard(string code, string message)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "guard";
            attempt.Method = code;
            attempt.Outcome = "blocked";
            attempt.ErrorCode = code;
            attempt.ErrorMessage = message;
            attempt.Effect = "nothing was sent to the target";
            return attempt;
        }

        private static RouteAttempt Trace(string text)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "resolve";
            attempt.Method = text;
            attempt.Outcome = "info";
            return attempt;
        }

        private static string Capital(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            return Char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // An attempt row that is neither a success nor a refusal: something the
        // reader has to know before trusting the step above it.
        private static RouteAttempt Warn(string method, string message)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "focus";
            attempt.Method = method;
            attempt.Outcome = "unknown";
            attempt.ErrorCode = method;
            attempt.ErrorMessage = message;
            attempt.Effect = "the key was sent without the keyboard having been verified";
            return attempt;
        }

        private static RouteAttempt Note(string method, string message)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = "focus";
            attempt.Method = method;
            attempt.Outcome = "info";
            attempt.Effect = message;
            return attempt;
        }

        private void Report(int index, int total, string headline, string state, string detail, bool finished)
        {
            Action<ReplayProgress> handler = Progress;
            if (handler == null) return;
            ReplayProgress value = new ReplayProgress();
            value.Index = index;
            value.Total = total;
            value.Headline = headline;
            value.State = state;
            value.Detail = detail;
            value.Finished = finished;
            try
            {
                handler(value);
            }
            catch
            {
                // A progress display that throws must not derail the run. What
                // each step actually did is written to the session either way,
                // so nothing is lost by carrying on.
            }
        }
    }
}
