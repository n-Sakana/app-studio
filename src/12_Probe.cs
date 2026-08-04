namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;

    public enum ProbeKind
    {
        Read,
        Focus,
        Invoke,
        Toggle,
        Select,
        Expand,
        SetValue,
        Scroll,
        Click,
        Keys,
        // Three things a person does with a pointer that no UI Automation
        // pattern expresses. They exist as their own kinds so the route trail
        // says plainly that synthetic input was the only route there ever was,
        // rather than showing a pattern that was never tried.
        DoubleClick,
        Drag,
        Wheel
    }

    public sealed class ProbeArgs
    {
        public bool WriteEnabled;
        public string Value;
        public int BudgetMs = 5000;
        public string Button = MouseButtons.Left;
        public int ToX;
        public int ToY;
        public int WheelDelta;
        // A replay is a deliberate re-run of something the operator already did,
        // and an operator who pressed the same button twice in a second meant
        // to. The rate limit exists to stop an interactive probe repeating
        // itself, so replay says so rather than being blocked by it.
        public bool Repeatable;
        // Which carrying-out routes may be used. "auto" is the documented
        // ladder; the single-route settings exist so an operator can prove which
        // route an application actually answers. They never widen the ladder,
        // they only narrow it.
        public string RouteMode = ProbeRoutes.Auto;
    }

    public static class ProbeRoutes
    {
        public const string Auto = "auto";
        public const string UiaOnly = "uia";
        public const string Win32Only = "win32";
        public const string SendInputOnly = "sendInput";

        public static bool Allows(string mode, string route)
        {
            if (String.IsNullOrEmpty(mode) || String.Equals(mode, Auto, StringComparison.Ordinal)) return true;
            return String.Equals(mode, route, StringComparison.Ordinal);
        }

        public static bool IsKnown(string mode)
        {
            return String.Equals(mode, Auto, StringComparison.Ordinal) ||
                String.Equals(mode, UiaOnly, StringComparison.Ordinal) ||
                String.Equals(mode, Win32Only, StringComparison.Ordinal) ||
                String.Equals(mode, SendInputOnly, StringComparison.Ordinal);
        }
    }

    public sealed class ProbeError
    {
        public string Code;
        public int Hresult;
        public string Message;
    }

    public sealed class ProbeObservation
    {
        public string Value;
        public string State;
        public string FocusedElement;
        public string WindowTitle;
        public int ChildCount;
        public RectValue Rect;
    }

    public sealed class ProbeSideEffect
    {
        public string Type;
        public string Detail;
    }

    public sealed class ProbeUndo
    {
        public bool Available;
        public DateTimeOffset? PerformedAt;
        public string OriginalValue;
    }

    public sealed class ProbeResult
    {
        public string ProbeId;
        public string ElementId;
        public ProbeKind Kind;
        public DateTimeOffset RequestedAt;
        public string Method;
        public string Outcome;
        public int DurationMs;
        public ProbeError Error;
        public ProbeObservation Before;
        public ProbeObservation After;
        public List<ProbeSideEffect> SideEffects = new List<ProbeSideEffect>();
        public ProbeUndo Undo;
        public ElementRef Element;
        // Every route that was tried, in the order it was tried, with what it
        // answered. A single "method" cannot say that UI Automation refused
        // before a window message worked, and that difference is exactly what a
        // reader needs in order to write the automation later.
        public List<RouteAttempt> Attempts = new List<RouteAttempt>();

        public string AttemptLine
        {
            get
            {
                if (Attempts.Count == 0) return Method == null ? "(no route was attempted)" : Method + ":" + Outcome;
                System.Text.StringBuilder text = new System.Text.StringBuilder();
                for (int index = 0; index < Attempts.Count; index++)
                {
                    if (index != 0) text.Append(" -> ");
                    text.Append(Attempts[index].Line);
                }
                return text.ToString();
            }
        }
    }

    public static class ProbeRunner
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DateTime> LastRun = new Dictionary<string, DateTime>();
        private static int sequence;

        public static ProbeResult Run(ElementRef element, ProbeKind kind, ProbeArgs args)
        {
            return RunCore(element, kind, args, false);
        }

        public static ProbeResult Undo(ProbeResult original)
        {
            if (original == null || original.Undo == null || !original.Undo.Available || original.Element == null)
            {
                return Blocked(null, ProbeKind.SetValue, "policy.undo", "No setValue undo value is available.");
            }
            ProbeArgs args = new ProbeArgs();
            args.WriteEnabled = true;
            args.Value = original.Undo.OriginalValue;
            args.BudgetMs = 5000;
            ProbeResult result = RunCore(original.Element, ProbeKind.SetValue, args, true);
            if (result.Outcome == "success") original.Undo.PerformedAt = DateTimeOffset.Now;
            return result;
        }

        public static void EmergencyStop()
        {
            lock (Sync) LastRun.Clear();
        }

        // A packaged application is shown inside a frame window owned by
        // ApplicationFrameHost while its contents are drawn by the application's
        // own process. Calculator is the plain example: the top level window
        // reports one process and every point inside it reports another. That is
        // not another window covering the target, it is the target drawing
        // itself, so refusing it would refuse every operation aimed at the
        // window of any packaged application. The observation layer already
        // resolves this the same way; only the processes that draw inside this
        // very window are accepted, so a genuinely foreign window sitting on top
        // is still refused.
        private static bool DrawnInsideTarget(ElementRef element, int targetProcessId, int ownerAtPoint)
        {
            IntPtr top = IntPtr.Zero;
            if (element.Hwnd != 0) top = NativeMethods.GetAncestor(new IntPtr(element.Hwnd), NativeMethods.GA_ROOT);
            if (top == IntPtr.Zero) return false;
            uint framePid;
            NativeMethods.GetWindowThreadProcessId(top, out framePid);
            if (unchecked((int)framePid) != targetProcessId) return false;
            int[] content = WindowTools.ContentProcessIds(top, targetProcessId);
            for (int index = 0; index < content.Length; index++) if (content[index] == ownerAtPoint) return true;
            return false;
        }

        private static ProbeResult RunCore(ElementRef element, ProbeKind kind, ProbeArgs args, bool bypassRateLimit)
        {
            if (args == null) args = new ProbeArgs();
            if (element == null) return Blocked(element, kind, "probe.noElement", "No element was supplied for the operation probe.");
            if (kind != ProbeKind.Read && !args.WriteEnabled) return Blocked(element, kind, "policy.readOnly", "Write operations are disabled because the tool is in read-only mode.");
            string key = element.Hwnd.ToString(CultureInfo.InvariantCulture) + ":" + element.X + ":" + element.Y + ":" + kind;
            lock (Sync)
            {
                DateTime last;
                if (!bypassRateLimit && !args.Repeatable && LastRun.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalMilliseconds < 1000)
                {
                    return Blocked(element, kind, "policy.rateLimit", "The same operation on this element is limited to one attempt per second.");
                }
                LastRun[key] = DateTime.UtcNow;
            }

            Stopwatch watch = Stopwatch.StartNew();
            Snapshot beforeSnapshot = Probe.Deep(element, Math.Max(1, Math.Min(3000, Remaining(watch, args.BudgetMs))));
            bool writes = kind == ProbeKind.SetValue || kind == ProbeKind.Keys;
            // This snapshot is the only thing that says whether the element is a
            // password box, and it comes from a different acquisition than the one
            // that carries out the action. A property that could not be read leaves
            // IsPassword sitting at false, which reads exactly like a real "this is
            // not a password box", so a degraded read used to let a write through
            // onto a pass phrase box while the action stage still worked. When the
            // acquisition did not come back clean, nothing it says about the element
            // can be leaned on, so a write is refused: not knowing what the element
            // is cannot mean typing into it anyway.
            string uiaState = beforeSnapshot.UiaStatus == null ? null : beforeSnapshot.UiaStatus.State;
            if (writes && (beforeSnapshot.Uia == null || uiaState != "ok"))
            {
                // A timeout is not an answer about the element. The first deep
                // probe after a whole window has been walked regularly spends its
                // entire allowance and restarts the worker, so refusing on that
                // first unclean read would refuse every ordinary write that
                // follows an acquisition. The warm spare answers in tens of
                // milliseconds, so the identity is asked for once more and only a
                // second unclean answer is treated as "cannot be established".
                int retryBudget = Remaining(watch, args.BudgetMs);
                if (retryBudget >= 250)
                {
                    beforeSnapshot = Probe.Deep(element, Math.Max(1, Math.Min(3000, retryBudget)));
                    uiaState = beforeSnapshot.UiaStatus == null ? null : beforeSnapshot.UiaStatus.State;
                }
            }
            if (writes && (beforeSnapshot.Uia == null || uiaState != "ok"))
            {
                ProbeResult unknownIdentity = Blocked(element, kind, "policy.identityUnknown",
                    "The element could not be inspected before the write, so it is refused; it may be a password box.");
                unknownIdentity.DurationMs = (int)watch.ElapsedMilliseconds;
                unknownIdentity.Before = Observation(beforeSnapshot, null, true);
                unknownIdentity.After = unknownIdentity.Before;
                return unknownIdentity;
            }
            if (beforeSnapshot.Uia != null && beforeSnapshot.Uia.IsPassword && writes)
            {
                ProbeResult passwordBlocked = Blocked(element, kind, "policy.isPassword", "setValue and keys are disabled for password elements.");
                passwordBlocked.DurationMs = (int)watch.ElapsedMilliseconds;
                passwordBlocked.Before = Observation(beforeSnapshot, null, true);
                passwordBlocked.After = passwordBlocked.Before;
                return passwordBlocked;
            }

            int processId = beforeSnapshot.Win32 == null ? 0 : beforeSnapshot.Win32.ProcessId;
            // Everything but a read is carried out at a screen point. If another
            // application has come to cover that point, acting there would touch
            // the wrong program, so the attempt is refused instead.
            if (kind != ProbeKind.Read && processId != 0)
            {
                int ownerAtPoint = WindowTools.ProcessIdAt(element.X, element.Y);
                if (ownerAtPoint != 0 && ownerAtPoint != processId && !DrawnInsideTarget(element, processId, ownerAtPoint))
                {
                    ProbeResult covered = Blocked(element, kind, "policy.covered",
                        "Another window now covers that point (process " + ownerAtPoint + " instead of " + processId + "), so nothing was done.");
                    covered.DurationMs = (int)watch.ElapsedMilliseconds;
                    covered.Before = Observation(beforeSnapshot, null, true);
                    covered.After = covered.Before;
                    return covered;
                }
            }
            TopWindowState topBefore = TopWindowState.Capture(processId);
            List<RouteAttempt> attempts = new List<RouteAttempt>();
            string routeMode = ProbeRoutes.IsKnown(args.RouteMode) ? args.RouteMode : ProbeRoutes.Auto;
            string outcome = "notSupported";
            string method = null;
            ProbeError error = null;
            UiaActionResult uia = null;

            if (Physical(kind))
            {
                // No UI Automation pattern and no window message carries a
                // double click, a drag or a wheel turn. Saying so is not the
                // same as having tried and failed, so the trail says which it
                // is.
                outcome = "notSupported";
                attempts.Add(Attempt("uia", "uia.pattern", "notSupported", 0, "UIA-NOPATTERN",
                    "No UI Automation pattern expresses a " + KindText(kind) + ".", null));
            }
            else if (ProbeRoutes.Allows(routeMode, ProbeRoutes.UiaOnly))
            {
                Stopwatch uiaWatch = Stopwatch.StartNew();
                int actionBudget = Remaining(watch, args.BudgetMs);
                if (actionBudget <= 0)
                {
                    uia = new UiaActionResult();
                    uia.Outcome = "failed";
                    uia.Method = "uia.worker";
                    uia.ErrorCode = "UIA-TIMEOUT";
                    uia.ErrorMessage = "The operation probe exhausted its overall " + args.BudgetMs + " ms budget before the action stage.";
                }
                else
                {
                    uia = Probe.RunUiaOperation(element, KindText(kind), args.Value, actionBudget);
                }
                uiaWatch.Stop();
                outcome = uia.Outcome;
                method = uia.Method;
                error = Error(uia.ErrorCode, uia.ErrorHresult, uia.ErrorMessage);
                attempts.Add(Attempt("uia", uia.Method, uia.Outcome, (int)uiaWatch.ElapsedMilliseconds, uia.ErrorCode, uia.ErrorMessage, null));
            }
            else
            {
                attempts.Add(Attempt("uia", "uia.pattern", "skipped", 0, "ROUTE-MODE",
                    "The session is limited to the " + routeMode + " route, so UI Automation was not tried.", null));
            }

            // The routes are tried in the documented order. A route that reported
            // notSupported never acted, so the next one is tried. A route that
            // threw did not act either, but pushing a second route through after
            // a refusal is only safe where the operation changes nothing inside
            // the target, so that widening stops at focus: a setValue that UI
            // Automation refused, on a read-only field for instance, must not be
            // forced through a window message instead.
            bool ladder = outcome == "notSupported" || (kind == ProbeKind.Focus && outcome == "failed");
            if (ladder)
            {
                bool win32Acted = false;
                if (Physical(kind))
                {
                    attempts.Add(Attempt("win32", "win32.message", "notSupported", 0, "WIN32-NOROUTE",
                        "No window message carries a " + KindText(kind) + ".", null));
                }
                else if (ProbeRoutes.Allows(routeMode, ProbeRoutes.Win32Only))
                {
                    Stopwatch win32Watch = Stopwatch.StartNew();
                    FallbackResult fallback = TryWin32(beforeSnapshot, kind, args.Value, Math.Min(Math.Max(1, Remaining(watch, args.BudgetMs)), 500));
                    win32Watch.Stop();
                    if (fallback.Performed)
                    {
                        win32Acted = true;
                        method = fallback.Method;
                        outcome = fallback.Failed ? "failed" : "unknown";
                        error = fallback.Error;
                        attempts.Add(Attempt("win32", fallback.Method, outcome, (int)win32Watch.ElapsedMilliseconds,
                            fallback.Error == null ? null : fallback.Error.Code, fallback.Error == null ? null : fallback.Error.Message, null));
                    }
                    else
                    {
                        attempts.Add(Attempt("win32", "win32.message", "notSupported", (int)win32Watch.ElapsedMilliseconds,
                            "WIN32-NOROUTE", "No window message carries this operation for this element.", null));
                    }
                }
                else if (routeMode != ProbeRoutes.Auto)
                {
                    attempts.Add(Attempt("win32", "win32.message", "skipped", 0, "ROUTE-MODE",
                        "The session is limited to the " + routeMode + " route, so window messages were not tried.", null));
                }

                // Only a route that did not act lets the next one be tried. This
                // is the guard that keeps a state changing operation from being
                // carried out twice by two different routes.
                if (!win32Acted)
                {
                    if (ProbeRoutes.Allows(routeMode, ProbeRoutes.SendInputOnly))
                    {
                        Stopwatch inputWatch = Stopwatch.StartNew();
                        FallbackResult fallback = TryInput(beforeSnapshot, element, kind, args);
                        inputWatch.Stop();
                        if (fallback.Performed)
                        {
                            method = fallback.Method;
                            outcome = fallback.Failed ? "failed" : "unknown";
                            error = fallback.Error;
                            attempts.Add(Attempt("sendInput", fallback.Method, outcome, (int)inputWatch.ElapsedMilliseconds,
                                fallback.Error == null ? null : fallback.Error.Code, fallback.Error == null ? null : fallback.Error.Message, null));
                        }
                        else
                        {
                            attempts.Add(Attempt("sendInput", "win32.SendInput", "notSupported", (int)inputWatch.ElapsedMilliseconds,
                                "SENDINPUT-NOROUTE", "Synthetic input does not carry this operation.", null));
                        }
                    }
                    else if (routeMode != ProbeRoutes.Auto)
                    {
                        attempts.Add(Attempt("sendInput", "win32.SendInput", "skipped", 0, "ROUTE-MODE",
                            "The session is limited to the " + routeMode + " route, so synthetic input was not tried.", null));
                    }
                }
            }
            int settle = Math.Min(150, Math.Max(0, Remaining(watch, args.BudgetMs)));
            if (settle > 0) Thread.Sleep(settle);
            int afterBudget = Remaining(watch, args.BudgetMs);
            Snapshot afterSnapshot = afterBudget > 0 ? Probe.Deep(element, afterBudget) : null;
            ProbeObservation before = Observation(beforeSnapshot, uia, true);
            ProbeObservation after = Observation(afterSnapshot, uia, false);
            bool changed = Changed(before, after, kind);
            if (outcome == "unknown" && changed) outcome = "success";
            TopWindowState topAfter = TopWindowState.Capture(processId);
            watch.Stop();

            ProbeResult result = NewResult(element, kind);
            result.Method = String.IsNullOrWhiteSpace(method) ? "none.available" : method;
            result.Outcome = NormalizeOutcome(outcome);
            result.DurationMs = (int)watch.ElapsedMilliseconds;
            result.Error = error;
            result.Before = before;
            result.After = after;
            result.Attempts = attempts;
            // The observed change belongs to whichever route actually acted,
            // which is the last one that did not report notSupported or skipped.
            for (int index = attempts.Count - 1; index >= 0; index--)
            {
                if (attempts[index].Outcome == "notSupported" || attempts[index].Outcome == "skipped") continue;
                attempts[index].Outcome = NormalizeOutcome(outcome);
                attempts[index].Effect = kind == ProbeKind.Read
                    ? "read only, nothing was changed"
                    : (changed ? DescribeChange(before, after) : "no change was observed before and after");
                break;
            }
            AddSideEffects(result, topBefore, topAfter);
            if (kind == ProbeKind.SetValue && result.Outcome == "success" && before != null && before.Value != null)
            {
                result.Undo = new ProbeUndo();
                result.Undo.Available = true;
                result.Undo.OriginalValue = before.Value;
            }
            return result;
        }

        private static FallbackResult TryWin32(Snapshot snapshot, ProbeKind kind, string value, int timeoutMs)
        {
            FallbackResult result = new FallbackResult();
            if (snapshot == null || snapshot.Win32 == null || snapshot.Win32.Hwnd == 0) return result;
            IntPtr window = new IntPtr(snapshot.Win32.Hwnd);
            IntPtr messageResult;
            // UI Automation focuses an element; the ordinary Win32 way to focus
            // is to bring the window that owns it to the front. That is the only
            // route left when the element under the point does not take focus,
            // which is what a container such as a whole window looks like, and
            // it moves nothing inside the target.
            if (kind == ProbeKind.Focus)
            {
                IntPtr top = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
                if (top == IntPtr.Zero) top = window;
                result.Performed = true;
                result.Method = "win32.SetForegroundWindow";
                result.Failed = !NativeMethods.SetForegroundWindow(top);
                if (result.Failed) result.Error = Error("WIN32-NOFOCUS", Marshal.GetLastWin32Error(), "SetForegroundWindow did not bring the window to the front.");
            }
            else if ((kind == ProbeKind.Invoke || kind == ProbeKind.Click || kind == ProbeKind.Toggle) && !String.IsNullOrEmpty(snapshot.Win32.ClassName) && snapshot.Win32.ClassName.IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Performed = true;
                result.Method = "win32.BM_CLICK";
                IntPtr call = NativeMethods.SendMessageTimeout(window, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, (uint)Math.Max(1, timeoutMs), out messageResult);
                result.Failed = call == IntPtr.Zero;
                if (result.Failed) result.Error = Error("WIN32-HUNG", 0, "BM_CLICK did not return within the operation timeout.");
            }
            else if (kind == ProbeKind.SetValue)
            {
                result.Performed = true;
                result.Method = "win32.WM_SETTEXT";
                StringBuilder text = new StringBuilder(value ?? String.Empty);
                IntPtr call = NativeMethods.SendMessageTimeout(window, NativeMethods.WM_SETTEXT, IntPtr.Zero, text, NativeMethods.SMTO_ABORTIFHUNG, (uint)Math.Max(1, timeoutMs), out messageResult);
                result.Failed = call == IntPtr.Zero;
                if (result.Failed) result.Error = Error("WIN32-HUNG", 0, "WM_SETTEXT did not return within the operation timeout.");
            }
            return result;
        }

        private static bool Physical(ProbeKind kind)
        {
            return kind == ProbeKind.DoubleClick || kind == ProbeKind.Drag || kind == ProbeKind.Wheel;
        }

        private static FallbackResult TryInput(Snapshot snapshot, ElementRef element, ProbeKind kind, ProbeArgs args)
        {
            FallbackResult result = new FallbackResult();
            bool supportsClick = kind == ProbeKind.Invoke || kind == ProbeKind.Click || kind == ProbeKind.Toggle || kind == ProbeKind.Select || kind == ProbeKind.Expand;
            if (supportsClick)
            {
                result.Performed = true;
                result.Method = "win32.SendInput.click";
                result.Failed = !NativeInput.ClickButton(element.X, element.Y, args.Button, false);
                if (result.Failed) result.Error = Error("SENDINPUT-FAIL", Marshal.GetLastWin32Error(), "SendInput could not emit a mouse click.");
            }
            else if (kind == ProbeKind.DoubleClick)
            {
                result.Performed = true;
                result.Method = "win32.SendInput.doubleClick";
                result.Failed = !NativeInput.ClickButton(element.X, element.Y, args.Button, true);
                if (result.Failed) result.Error = Error("SENDINPUT-FAIL", Marshal.GetLastWin32Error(), "SendInput could not emit a double click.");
            }
            else if (kind == ProbeKind.Drag)
            {
                result.Performed = true;
                result.Method = "win32.SendInput.drag";
                result.Failed = !NativeInput.Drag(element.X, element.Y, args.ToX, args.ToY, args.Button);
                if (result.Failed) result.Error = Error("SENDINPUT-FAIL", Marshal.GetLastWin32Error(), "SendInput could not emit a drag.");
            }
            else if (kind == ProbeKind.Wheel)
            {
                result.Performed = true;
                result.Method = "win32.SendInput.wheel";
                result.Failed = !NativeInput.Wheel(element.X, element.Y, args.WheelDelta);
                if (result.Failed) result.Error = Error("SENDINPUT-FAIL", Marshal.GetLastWin32Error(), "SendInput could not emit a wheel turn.");
            }
            else if (kind == ProbeKind.Keys)
            {
                result.Performed = true;
                result.Method = "win32.SendInput.keys";
                IntPtr window = snapshot != null && snapshot.Win32 != null ? new IntPtr(snapshot.Win32.Hwnd) : IntPtr.Zero;
                if (window != IntPtr.Zero) NativeMethods.SetForegroundWindow(window);
                result.Failed = !NativeInput.SendUnicode(args.Value ?? String.Empty);
                if (result.Failed) result.Error = Error("SENDINPUT-FAIL", Marshal.GetLastWin32Error(), "SendInput could not emit Unicode keys.");
            }
            return result;
        }

        private static ProbeObservation Observation(Snapshot snapshot, UiaActionResult action, bool before)
        {
            ProbeObservation value = new ProbeObservation();
            if (snapshot != null && snapshot.Uia != null)
            {
                value.Value = snapshot.Uia.IsPassword ? null : snapshot.Uia.LiveValue;
                value.State = null;
                value.ChildCount = snapshot.Uia.Children == null ? 0 : snapshot.Uia.Children.Length;
                value.Rect = snapshot.Uia.BoundingRect;
                value.FocusedElement = snapshot.Uia.Name;
            }
            if (value.Rect == null && snapshot != null && snapshot.Win32 != null) value.Rect = snapshot.Win32.WindowRect;
            if (snapshot != null && snapshot.Win32 != null)
            {
                value.WindowTitle = snapshot.Win32.Caption;
                if (snapshot.Win32.Ancestors != null && snapshot.Win32.Ancestors.Count != 0) value.WindowTitle = snapshot.Win32.Ancestors[snapshot.Win32.Ancestors.Count - 1].Caption;
            }
            if (action != null)
            {
                value.Value = before ? action.BeforeValue : action.AfterValue;
                value.State = before ? action.BeforeState : action.AfterState;
                if (before ? action.BeforeFocused : action.AfterFocused) value.FocusedElement = before ? action.BeforeName : action.AfterName;
            }
            return value;
        }

        private static bool Changed(ProbeObservation before, ProbeObservation after, ProbeKind kind)
        {
            if (kind == ProbeKind.Read) return true;
            if (before == null || after == null) return false;
            return !String.Equals(before.Value, after.Value, StringComparison.Ordinal) ||
                !String.Equals(before.State, after.State, StringComparison.Ordinal) ||
                !String.Equals(before.WindowTitle, after.WindowTitle, StringComparison.Ordinal) ||
                before.ChildCount != after.ChildCount || !RectEqual(before.Rect, after.Rect);
        }

        private static RouteAttempt Attempt(string route, string method, string outcome, int durationMs, string errorCode, string errorMessage, string effect)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = route;
            attempt.Method = method;
            attempt.Outcome = outcome;
            attempt.DurationMs = durationMs;
            attempt.ErrorCode = errorCode;
            attempt.ErrorMessage = errorMessage;
            attempt.Effect = effect;
            return attempt;
        }

        private static string DescribeChange(ProbeObservation before, ProbeObservation after)
        {
            if (before == null || after == null) return "the state before and after could not be compared";
            List<string> parts = new List<string>();
            if (!String.Equals(before.Value, after.Value, StringComparison.Ordinal)) parts.Add("value changed");
            if (!String.Equals(before.State, after.State, StringComparison.Ordinal)) parts.Add("state changed");
            if (!String.Equals(before.WindowTitle, after.WindowTitle, StringComparison.Ordinal)) parts.Add("window title changed");
            if (before.ChildCount != after.ChildCount) parts.Add("child count " + before.ChildCount + " -> " + after.ChildCount);
            if (!RectEqual(before.Rect, after.Rect)) parts.Add("rectangle changed");
            if (parts.Count == 0) return "a change was observed";
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < parts.Count; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(parts[index]);
            }
            return text.ToString();
        }

        private static bool RectEqual(RectValue first, RectValue second)
        {
            if (first == null || second == null) return first == second;
            return first.X == second.X && first.Y == second.Y && first.Width == second.Width && first.Height == second.Height;
        }

        private static ProbeResult Blocked(ElementRef element, ProbeKind kind, string code, string message)
        {
            ProbeResult result = NewResult(element, kind);
            result.Method = code;
            result.Outcome = "blocked";
            result.Error = Error(code, 0, message);
            // A refusal is also an attempt record: it says a guard stopped the
            // operation before any route was reached, so the trail never has a
            // hole where the reason should be.
            result.Attempts.Add(Attempt("guard", code, "blocked", 0, code, message, "nothing was sent to the target"));
            return result;
        }

        private static ProbeResult NewResult(ElementRef element, ProbeKind kind)
        {
            ProbeResult result = new ProbeResult();
            lock (Sync)
            {
                sequence++;
                result.ProbeId = "pr-" + sequence.ToString("0000", CultureInfo.InvariantCulture);
            }
            result.Element = element;
            result.Kind = kind;
            result.RequestedAt = DateTimeOffset.Now;
            return result;
        }

        private static ProbeError Error(string code, int hresult, string message)
        {
            if (String.IsNullOrWhiteSpace(code) && String.IsNullOrWhiteSpace(message)) return null;
            ProbeError error = new ProbeError();
            error.Code = code;
            error.Hresult = hresult;
            error.Message = message;
            return error;
        }

        private static string KindText(ProbeKind kind)
        {
            string value = kind.ToString();
            return Char.ToLowerInvariant(value[0]) + value.Substring(1);
        }

        private static string NormalizeOutcome(string outcome)
        {
            if (outcome == "success" || outcome == "failed" || outcome == "blocked" || outcome == "notSupported" || outcome == "unknown") return outcome;
            return "unknown";
        }

        private static int Remaining(Stopwatch watch, int budgetMs)
        {
            return Math.Max(0, budgetMs - (int)watch.ElapsedMilliseconds);
        }

        private static void AddSideEffects(ProbeResult result, TopWindowState before, TopWindowState after)
        {
            if (before.Foreground != after.Foreground)
            {
                ProbeSideEffect effect = new ProbeSideEffect();
                effect.Type = "foregroundChanged";
                effect.Detail = "0x" + before.Foreground.ToString("X") + " -> 0x" + after.Foreground.ToString("X");
                result.SideEffects.Add(effect);
            }
            foreach (long handle in after.Windows)
            {
                if (!before.Windows.Contains(handle))
                {
                    ProbeSideEffect effect = new ProbeSideEffect();
                    effect.Type = "newWindow";
                    effect.Detail = "hwnd=0x" + handle.ToString("X");
                    result.SideEffects.Add(effect);
                }
            }
        }

        private sealed class FallbackResult
        {
            internal bool Performed;
            internal bool Failed;
            internal string Method;
            internal ProbeError Error;
        }

        private sealed class TopWindowState
        {
            internal long Foreground;
            internal HashSet<long> Windows = new HashSet<long>();

            internal static TopWindowState Capture(int processId)
            {
                TopWindowState state = new TopWindowState();
                state.Foreground = NativeMethods.GetForegroundWindow().ToInt64();
                if (processId == 0) return state;
                NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(window, out pid);
                    if (pid == unchecked((uint)processId)) state.Windows.Add(window.ToInt64());
                    return true;
                }, IntPtr.Zero);
                return state;
            }
        }
    }
}
