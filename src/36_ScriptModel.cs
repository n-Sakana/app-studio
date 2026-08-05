namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    // The names both languages use. PowerShell and VBA are two spellings of one
    // vocabulary here: neither is the real one with the other translated from
    // it, so the operation names, their order and their meaning are decided in
    // this file and nowhere else.
    public static class ScriptLanguages
    {
        public const string PowerShell = "powershell";
        public const string Vba = "vba";

        public static bool IsKnown(string value)
        {
            return String.Equals(value, PowerShell, StringComparison.Ordinal) ||
                String.Equals(value, Vba, StringComparison.Ordinal);
        }

        public static string Extension(string language)
        {
            return String.Equals(language, Vba, StringComparison.Ordinal) ? "bas" : "ps1";
        }

        // The mark a line comment starts with. The generators write their own
        // notes with it, so a caller never has to know which language it holds.
        public static string Comment(string language)
        {
            return String.Equals(language, Vba, StringComparison.Ordinal) ? "'" : "#";
        }
    }

    // One operation of the automation, named the same in both languages.
    //
    // Nothing here carries a screen coordinate as an address. A place inside an
    // element is kept as a fraction of that element's own rectangle, exactly as
    // replay does, so it follows the element when the window moves instead of
    // aiming at wherever the pointer happened to be.
    public sealed class ScriptOp
    {
        public const string FindWindow = "FindWindow";
        public const string FocusElement = "FocusElement";
        public const string InvokeElement = "InvokeElement";
        public const string SetElementText = "SetElementText";
        public const string SendKeys = "SendKeys";
        public const string WaitGap = "WaitGap";
        public const string WaitIdle = "WaitIdle";
        public const string ReadElementText = "ReadElementText";
        public const string AskSecret = "AskSecret";
        // Not an operation the script performs but one it refuses to invent.
        // It is emitted where the recording holds something no address can be
        // built from, so the script stops and says which step and why.
        public const string Unsupported = "Unsupported";

        public string Op;
        public string StepId;
        public string Headline;
        public string WindowTitle;
        public string WindowClass;
        public int GapMs;
        public string ElementLabel;
        public List<ElementLocator> Locators = new List<ElementLocator>();
        public List<ElementLocator> DropLocators = new List<ElementLocator>();
        public string DropLabel;
        // Where inside the element, as a fraction of its rectangle. Negative
        // means the middle, which is what a button wants.
        public double RelX = -1;
        public double RelY = -1;
        public double DropRelX = -1;
        public double DropRelY = -1;
        public string Text;
        public bool TextKnown;
        public int TextLength = -1;
        public string Keys;
        public string Button = MouseButtons.Left;
        public int WheelDelta;
        public int Times = 1;
        public string Reason;

        public bool HasAddress
        {
            get { return Locators != null && Locators.Count > 0; }
        }

        // Whether VBA can reach this element at all. VBA has no UI Automation
        // without a type library that cannot be assumed on a machine nothing is
        // installed on, so it addresses controls the Win32 way: a class name
        // with a dialog control id, or a class name with its index. An element
        // that only ever existed in the accessibility tree has no Win32 address
        // and is not given a pretend one.
        public bool ReachableFromVba
        {
            get
            {
                if (Op == FindWindow || Op == SendKeys || Op == WaitGap || Op == WaitIdle || Op == Unsupported) return true;
                for (int index = 0; index < Locators.Count; index++)
                {
                    string strategy = Locators[index].Strategy;
                    if (String.Equals(strategy, ElementLocator.StrategyCtrlId, StringComparison.Ordinal)) return true;
                    if (String.Equals(strategy, ElementLocator.StrategyClassIndex, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }
    }

    public sealed class ScriptPlan
    {
        public string SessionId;
        public string SessionTitle;
        public List<ScriptOp> Ops = new List<ScriptOp>();
        // Everything the plan could not express, said once and kept, so both
        // generators write the same warning and the screen shows the same count.
        public List<string> Notes = new List<string>();
        public int SecretCount;

        public int Unsupported
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Ops.Count; index++) if (Ops[index].Op == ScriptOp.Unsupported) count++;
                return count;
            }
        }

        public int UnreachableFromVba
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Ops.Count; index++) if (!Ops[index].ReachableFromVba) count++;
                return count;
            }
        }
    }

    // Turns a recording into the operation list both generators read.
    //
    // This is the only place a step becomes an operation. If PowerShell and VBA
    // ever disagree about what a recording meant, it is because one of them
    // read this list wrongly, not because they each decided for themselves.
    public static class ScriptModel
    {
        public static ScriptPlan Build(StudioSession session)
        {
            ScriptPlan plan = new ScriptPlan();
            if (session == null) return plan;
            plan.SessionId = session.Id;
            plan.SessionTitle = session.Title;
            string window = null;
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (step.Kind == StepRecord.KindAppSwitch)
                {
                    plan.Ops.Add(WindowOp(step));
                    window = Key(step);
                    continue;
                }
                // A recording does not always begin with a switch: the operator
                // was already in the application. The window the step names is
                // still what has to be in front, so it is stated before the
                // first action and whenever it changes.
                if (!String.Equals(window, Key(step), StringComparison.Ordinal))
                {
                    plan.Ops.Add(WindowOp(step));
                    window = Key(step);
                }
                if (step.GapMs > 0) plan.Ops.Add(Gap(step));
                Emit(plan, step);
                plan.Ops.Add(Idle(step));
            }
            if (plan.Unsupported > 0)
            {
                plan.Notes.Add(plan.Unsupported.ToString(CultureInfo.InvariantCulture) +
                    " step(s) have no address that survives a restart, so this script stops at them instead of pressing a remembered coordinate.");
            }
            if (plan.SecretCount > 0)
            {
                plan.Notes.Add(plan.SecretCount.ToString(CultureInfo.InvariantCulture) +
                    " step(s) entered a secret. No value was ever recorded, so the script asks the operator at that point.");
            }
            if (plan.UnreachableFromVba > 0)
            {
                plan.Notes.Add(plan.UnreachableFromVba.ToString(CultureInfo.InvariantCulture) +
                    " step(s) are addressed only through UI Automation. VBA has no Win32 address for them and says so where they occur.");
            }
            return plan;
        }

        private static string Key(StepRecord step)
        {
            return (step.WindowClass == null ? "" : step.WindowClass) + "|" + (step.WindowTitle == null ? "" : step.WindowTitle);
        }

        private static ScriptOp WindowOp(StepRecord step)
        {
            ScriptOp op = new ScriptOp();
            op.Op = ScriptOp.FindWindow;
            op.StepId = step.StepId;
            op.Headline = step.Kind == StepRecord.KindAppSwitch ? step.Headline : "the window this step expects in front";
            op.WindowTitle = step.WindowTitle;
            op.WindowClass = step.WindowClass;
            return op;
        }

        private static ScriptOp Gap(StepRecord step)
        {
            ScriptOp op = new ScriptOp();
            op.Op = ScriptOp.WaitGap;
            op.StepId = step.StepId;
            op.GapMs = step.GapMs;
            op.Headline = "the interval the operator left before this";
            return op;
        }

        private static ScriptOp Idle(StepRecord step)
        {
            ScriptOp op = new ScriptOp();
            op.Op = ScriptOp.WaitIdle;
            op.StepId = step.StepId;
            op.Headline = "wait for the application to stop changing";
            return op;
        }

        private static void Emit(ScriptPlan plan, StepRecord step)
        {
            if (step.Kind == StepRecord.KindKeyChord)
            {
                ScriptOp keys = Common(step);
                keys.Op = ScriptOp.SendKeys;
                keys.Keys = step.KeyChord;
                if (String.IsNullOrEmpty(step.KeyChord))
                {
                    plan.Ops.Add(Refuse(step, "This step recorded a key press with no key name on it, so there is nothing to send."));
                    return;
                }
                // A key goes to whatever held the keyboard when it was recorded,
                // not to whatever has it now. Replay restores focus first and so
                // does the script.
                if (step.FocusLocators != null && step.FocusLocators.Count > 0)
                {
                    ScriptOp focus = Common(step);
                    focus.Op = ScriptOp.FocusElement;
                    focus.Locators = step.FocusLocators;
                    focus.ElementLabel = step.FocusLabel;
                    focus.Headline = "put the keyboard back where it was";
                    plan.Ops.Add(focus);
                }
                plan.Ops.Add(keys);
                return;
            }
            if (!Addressable(step))
            {
                plan.Ops.Add(Refuse(step, "This step has no name, no AutomationId, no hierarchy path and no control id, so nothing addresses it again. " +
                    "Its recorded position is a description, never an address."));
                return;
            }
            if (step.Kind == StepRecord.KindTextInput || step.Kind == StepRecord.KindSecretInput)
            {
                ScriptOp focus = Common(step);
                focus.Op = ScriptOp.FocusElement;
                focus.Headline = "give this field the keyboard before typing into it";
                plan.Ops.Add(focus);
                if (step.Kind == StepRecord.KindSecretInput)
                {
                    plan.SecretCount++;
                    ScriptOp ask = Common(step);
                    ask.Op = ScriptOp.AskSecret;
                    ask.Headline = "the recording deliberately kept no value for this";
                    plan.Ops.Add(ask);
                    return;
                }
                ScriptOp set = Common(step);
                set.Op = ScriptOp.SetElementText;
                set.Text = step.Value;
                set.TextKnown = step.Value != null;
                set.TextLength = step.ValueLength;
                if (!set.TextKnown)
                {
                    // The length only policy was in force. There is a step and a
                    // field but no text, so the operator supplies it rather than
                    // the script inventing one.
                    set.Op = ScriptOp.AskSecret;
                    set.Headline = "only the length of this entry was recorded, so the text has to be supplied";
                    plan.SecretCount++;
                }
                plan.Ops.Add(set);
                return;
            }
            if (step.Kind == StepRecord.KindDrag)
            {
                if (step.DropLocators == null || step.DropLocators.Count == 0)
                {
                    plan.Ops.Add(Refuse(step, "This drag has no description of where it was released, so there is nothing to aim at."));
                    return;
                }
                ScriptOp drag = Common(step);
                drag.Op = ScriptOp.InvokeElement;
                drag.Button = String.IsNullOrEmpty(step.Button) ? MouseButtons.Left : step.Button;
                drag.DropLocators = step.DropLocators;
                drag.DropLabel = step.DropLabel;
                Fraction(drag, step.Rect, step.Point, false);
                Fraction(drag, step.Rect, step.ToPoint, true);
                plan.Ops.Add(drag);
                return;
            }
            if (step.Kind == StepRecord.KindWheel)
            {
                if (step.WheelDelta == 0)
                {
                    plan.Ops.Add(Refuse(step, "This step recorded a wheel turn of nothing, so there is no turn to carry out."));
                    return;
                }
                ScriptOp wheel = Common(step);
                wheel.Op = ScriptOp.InvokeElement;
                wheel.WheelDelta = step.WheelDelta;
                Fraction(wheel, step.Rect, step.Point, false);
                plan.Ops.Add(wheel);
                return;
            }
            ScriptOp click = Common(step);
            click.Op = ScriptOp.InvokeElement;
            click.Button = String.IsNullOrEmpty(step.Button) ? MouseButtons.Left : step.Button;
            click.Times = step.Kind == StepRecord.KindDoubleClick ? 2 : 1;
            Fraction(click, step.Rect, step.Point, false);
            plan.Ops.Add(click);
        }

        // An element is addressable when at least one locator identifies it.
        // A place inside the window is a description of where it was, so it is
        // never enough on its own.
        private static bool Addressable(StepRecord step)
        {
            if (step.Locators == null) return false;
            for (int index = 0; index < step.Locators.Count; index++)
            {
                if (!String.Equals(step.Locators[index].Strategy, ElementLocator.StrategyWindowRelative, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static ScriptOp Common(StepRecord step)
        {
            ScriptOp op = new ScriptOp();
            op.StepId = step.StepId;
            op.Headline = step.Headline;
            op.WindowTitle = step.WindowTitle;
            op.WindowClass = step.WindowClass;
            op.ElementLabel = step.ElementLabel;
            op.Locators = Identifying(step.Locators);
            return op;
        }

        // The locators worth writing into a script, strongest first. The window
        // relative one is dropped here rather than in each generator, because a
        // position is a description and a script that used it would be pressing
        // an unknown part of somebody's application.
        private static List<ElementLocator> Identifying(List<ElementLocator> locators)
        {
            List<ElementLocator> kept = new List<ElementLocator>();
            if (locators == null) return kept;
            for (int index = 0; index < locators.Count; index++)
            {
                if (String.Equals(locators[index].Strategy, ElementLocator.StrategyWindowRelative, StringComparison.Ordinal)) continue;
                kept.Add(locators[index]);
            }
            return kept;
        }

        private static ScriptOp Refuse(StepRecord step, string reason)
        {
            ScriptOp op = new ScriptOp();
            op.Op = ScriptOp.Unsupported;
            op.StepId = step.StepId;
            op.Headline = step.Headline;
            op.WindowTitle = step.WindowTitle;
            op.WindowClass = step.WindowClass;
            op.ElementLabel = step.ElementLabel;
            op.Reason = reason;
            return op;
        }

        private static void Fraction(ScriptOp op, RectValue rect, PointValue point, bool drop)
        {
            if (rect == null || point == null || rect.Width <= 0 || rect.Height <= 0) return;
            double x = (point.X - rect.X) / (double)rect.Width;
            double y = (point.Y - rect.Y) / (double)rect.Height;
            if (x < 0 || x > 1 || y < 0 || y > 1) return;
            if (drop)
            {
                op.DropRelX = x;
                op.DropRelY = y;
                return;
            }
            op.RelX = x;
            op.RelY = y;
        }
    }
}
