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
    // Two rules decide everything here. A step is only performed once the window
    // it expects is actually in front, and the element it names has been found
    // again by meaning. When either is not true the run stops and says why: the
    // recorded coordinates are never used as a second chance, because clicking
    // where something used to be is not replaying a procedure, it is pressing an
    // unknown part of somebody's application.
    public sealed class ReplayEngine : IDisposable
    {
        private readonly string baseDir;
        private readonly StudioSession session;
        private readonly object sync = new object();
        private ScanRunner runner;
        private volatile bool cancelled;

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
                    StepRecord step = session.Steps[index];
                    report.Attempted++;
                    Report(index + 1, session.Steps.Count, step.Headline, "running", null, false);
                    ReplayOutcome outcome = RunStep(step, report.RouteMode, writeEnabled);
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
                    Thread.Sleep(400);
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
                if (step.Kind == StepRecord.KindKeyChord) return Finish(outcome, watch, SendChord(step, outcome));
                return Finish(outcome, watch, Act(step, window, outcome, routeMode));
            }
            catch (Exception exception)
            {
                outcome.State = "failed";
                outcome.Reason = exception.GetType().Name + ": " + exception.Message;
                return Finish(outcome, watch, outcome);
            }
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
            Thread.Sleep(350);
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
            Thread.Sleep(300);
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
            Thread.Sleep(250);
            watch.Stop();
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
            ScanLimits limits = new ScanLimits();
            limits.MaxNodes = 1500;
            limits.UiaBudgetMs = 9000;
            limits.MsaaBudgetMs = 4000;
            limits.HitTestBudgetMs = 5000;
            ScanResult fresh = current.RunWindows(new TargetWindowInfo[] { window }, window.ProcessId, limits, null);
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
            reference.X = rect.X + rect.Width / 2;
            reference.Y = rect.Y + rect.Height / 2;
            reference.Hwnd = resolved.Node.Hwnd;

            ProbeArgs args = new ProbeArgs();
            args.WriteEnabled = true;
            args.RouteMode = routeMode;
            args.BudgetMs = 6000;
            ProbeKind kind = ProbeKind.Invoke;
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
