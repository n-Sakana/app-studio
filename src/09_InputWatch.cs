namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    public static class MouseButtons
    {
        public const string Left = "left";
        public const string Right = "right";
        public const string Middle = "middle";
    }

    // The kinds of thing the watch can see. They are the vocabulary of the raw
    // timeline as well as of the steps, so a reader comparing the two is not
    // reading two different sets of words for the same event.
    public static class InputKinds
    {
        public const string MouseDown = "mouseDown";
        public const string MouseUp = "mouseUp";
        public const string Click = "click";
        public const string DoubleClick = "doubleClick";
        public const string Drag = "drag";
        public const string Wheel = "wheel";
        public const string KeyDown = "keyDown";
        public const string KeyUp = "keyUp";
        public const string Typing = "typing";
        public const string Foreground = "foreground";
        public const string Focus = "focus";
        public const string Text = "text";
    }

    // One thing the operator did, as the watch saw it. It carries the moment it
    // happened so the record shows when the operator acted, not when this
    // program got round to looking at it.
    public sealed class InputMoment
    {
        public string Kind;
        public string Button = MouseButtons.Left;
        public int X;
        public int Y;
        public int ToX;
        public int ToY;
        public int WheelDelta;
        public string Chord;
        public string Modifiers;
        public int Key;
        public int HoldMs;
        public DateTime AtUtc;
        public long Foreground;
    }

    // Watches the pointer at the event level.
    //
    // Polling the button state, which is what this used to do, can only answer
    // "has a press happened since you last asked". It cannot see which button,
    // where the button came up, that a second press arrived inside the double
    // click time, or that the wheel turned at all - the wheel has no state to
    // poll. So the pointer is watched through the low level hook the operating
    // system provides for exactly this, and the callback does nothing but note
    // what happened and hand it on, because a slow callback delays every mouse
    // event on the machine.
    //
    // Pointer movement is deliberately not recorded. Only a press, a release,
    // the two ends of a drag and a wheel turn become events; moving the pointer
    // about produces nothing.
    public sealed class PointerWatch : IDisposable
    {
        public const string StateHook = "hook";
        public const string StateUnavailable = "unavailable";

        private readonly Action<InputMoment> sink;
        private readonly object sync = new object();
        private NativeMethods.HookProc callback;
        private IntPtr hook;

        private bool down;
        private string downButton;
        private int downX;
        private int downY;
        private DateTime downAt;
        private DateTime lastClickAt = DateTime.MinValue;
        private int lastClickX;
        private int lastClickY;
        private string lastClickButton;

        public string State = StateUnavailable;
        public string Problem;

        public PointerWatch(Action<InputMoment> handler)
        {
            sink = handler;
        }

        // Installs the hook on the calling thread. That thread has to pump
        // messages afterwards, because the operating system delivers a low level
        // hook by calling back into the thread that installed it.
        public bool Install()
        {
            try
            {
                callback = new NativeMethods.HookProc(OnMouse);
                IntPtr module = NativeMethods.GetModuleHandle(null);
                hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, callback, module, 0);
                if (hook == IntPtr.Zero)
                {
                    Problem = "SetWindowsHookEx returned nothing (error " +
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")";
                    State = StateUnavailable;
                    return false;
                }
                State = StateHook;
                return true;
            }
            catch (Exception exception)
            {
                Problem = exception.GetType().Name + ": " + exception.Message;
                State = StateUnavailable;
                return false;
            }
        }

        // Called from the watching loop. Without this the hook never fires.
        public void Pump()
        {
            NativeMethods.MSG message;
            int guard = 0;
            while (guard < 64 && NativeMethods.PeekMessage(out message, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
                guard++;
            }
        }

        public void Dispose()
        {
            IntPtr current;
            lock (sync)
            {
                current = hook;
                hook = IntPtr.Zero;
            }
            if (current != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWindowsHookEx(current); }
                catch { }
            }
            callback = null;
        }

        private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            IntPtr next = IntPtr.Zero;
            try
            {
                if (code >= 0) Note((uint)wParam.ToInt64(), lParam);
            }
            catch
            {
                // A callback that throws would be taken off the chain by the
                // operating system, which would end the recording without
                // anything being said. The loop that owns this watch reports the
                // state; nothing here is allowed to escape.
            }
            IntPtr current;
            lock (sync) { current = hook; }
            next = NativeMethods.CallNextHookEx(current, code, wParam, lParam);
            return next;
        }

        private void Note(uint message, IntPtr lParam)
        {
            NativeMethods.MSLLHOOKSTRUCT data = (NativeMethods.MSLLHOOKSTRUCT)
                System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
            DateTime now = DateTime.UtcNow;
            if (message == NativeMethods.WM_MOUSEWHEEL)
            {
                InputMoment wheel = New(InputKinds.Wheel, data.pt.X, data.pt.Y, now);
                wheel.WheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
                Emit(wheel);
                return;
            }
            string button = ButtonOf(message);
            if (button == null) return;
            bool isDown = message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_RBUTTONDOWN ||
                message == NativeMethods.WM_MBUTTONDOWN;
            if (isDown)
            {
                down = true;
                downButton = button;
                downX = data.pt.X;
                downY = data.pt.Y;
                downAt = now;
                InputMoment press = New(InputKinds.MouseDown, data.pt.X, data.pt.Y, now);
                press.Button = button;
                Emit(press);
                return;
            }
            InputMoment release = New(InputKinds.MouseUp, data.pt.X, data.pt.Y, now);
            release.Button = button;
            Emit(release);
            if (!down || !String.Equals(downButton, button, StringComparison.Ordinal))
            {
                // The button went down before the recording started, or while
                // another watch owned it. What happened in between is not known,
                // so nothing is invented about it.
                down = false;
                return;
            }
            down = false;
            int hold = (int)(now - downAt).TotalMilliseconds;
            int moved = Math.Max(Math.Abs(data.pt.X - downX), Math.Abs(data.pt.Y - downY));
            if (moved > DragThreshold)
            {
                InputMoment drag = New(InputKinds.Drag, downX, downY, downAt);
                drag.Button = button;
                drag.ToX = data.pt.X;
                drag.ToY = data.pt.Y;
                drag.HoldMs = hold;
                Emit(drag);
                lastClickAt = DateTime.MinValue;
                return;
            }
            bool second = String.Equals(lastClickButton, button, StringComparison.Ordinal) &&
                lastClickAt != DateTime.MinValue &&
                (now - lastClickAt).TotalMilliseconds <= NativeMethods.GetDoubleClickTime() &&
                Math.Max(Math.Abs(downX - lastClickX), Math.Abs(downY - lastClickY)) <= DoubleClickSlop;
            InputMoment click = New(second ? InputKinds.DoubleClick : InputKinds.Click, downX, downY, downAt);
            click.Button = button;
            click.HoldMs = hold;
            Emit(click);
            // A double click is the second of the pair, so a third press starts
            // a fresh pair rather than reporting a double click again.
            lastClickAt = second ? DateTime.MinValue : now;
            lastClickX = downX;
            lastClickY = downY;
            lastClickButton = button;
        }

        private const int DragThreshold = 8;
        private const int DoubleClickSlop = 6;

        private static string ButtonOf(uint message)
        {
            if (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_LBUTTONUP) return MouseButtons.Left;
            if (message == NativeMethods.WM_RBUTTONDOWN || message == NativeMethods.WM_RBUTTONUP) return MouseButtons.Right;
            if (message == NativeMethods.WM_MBUTTONDOWN || message == NativeMethods.WM_MBUTTONUP) return MouseButtons.Middle;
            return null;
        }

        private static InputMoment New(string kind, int x, int y, DateTime atUtc)
        {
            InputMoment moment = new InputMoment();
            moment.Kind = kind;
            moment.X = x;
            moment.Y = y;
            moment.AtUtc = atUtc;
            moment.Modifiers = KeyTable.ModifierText();
            moment.Foreground = NativeMethods.GetForegroundWindow().ToInt64();
            return moment;
        }

        private void Emit(InputMoment moment)
        {
            Action<InputMoment> handler = sink;
            if (handler != null) handler(moment);
        }
    }

    // The keys whose being pressed is taken as a sign that typing is happening.
    // Which one it was is never written down anywhere, and this list is a subset
    // of KeyTable.Watched on purpose: that one fixed list stays the whole truth
    // about which keys this product ever asks the state of. Punctuation is not
    // on it and is never looked at, so text made only of punctuation is not
    // noticed - which is a stated limit, not a silent one.
    //
    // Without this the text a person types into a field they never clicked on -
    // including the field a window opens with the cursor already in - would be
    // missing from the recording with nothing to say it had happened.
    public static class TypingKeys
    {
        public static readonly int[] Watched = Build();

        private static int[] Build()
        {
            List<int> keys = new List<int>();
            keys.Add(0x08);
            keys.Add(0x20);
            keys.Add(0x2E);
            for (int key = 0x30; key <= 0x39; key++) keys.Add(key);
            for (int key = 0x41; key <= 0x5A; key++) keys.Add(key);
            for (int key = 0x60; key <= 0x69; key++) keys.Add(key);
            return keys.ToArray();
        }
    }
}
