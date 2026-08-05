namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.Text;

    // What is under the pointer, said in one short line.
    public sealed class InspectFact
    {
        public RectValue Rect;
        public string ClassName;
        public string RealClass;
        public string Caption;
        public int CtrlId;
        public int ProcessId;
        public string ProcessName;
        public long Hwnd;
        public bool TopLevel;
        // What could not be read. It is shown rather than hidden, because a chip
        // that says nothing looks the same as a chip that found nothing.
        public string Problem;

        public bool Known
        {
            get { return Rect != null && Rect.Width > 0 && Rect.Height > 0; }
        }

        // The chip in two parts. The first is what a person needs to tell one
        // thing from another by eye - is it a window or a control, and what is
        // it called - and is always shown. The second is what names it to a
        // program, and is folded, because it is several fields long and putting
        // it all on one line is what made the chip wider than the screen.
        //
        // Only what was actually obtained goes in either: an empty field is left
        // out rather than printed as a dash.
        public string Headline()
        {
            if (Problem != null)
            {
                return Messages.Text("inspect-unknown.txt", "nothing could be read here") + "  (" + Problem + ")";
            }
            StringBuilder text = new StringBuilder();
            Append(text, TopLevel
                ? Messages.Text("inspect-window.txt", "window")
                : Messages.Text("inspect-control.txt", "control"));
            if (!String.IsNullOrEmpty(Caption)) Append(text, "\"" + Shorten(Caption, 48) + "\"");
            return text.ToString();
        }

        public string Detail()
        {
            if (Problem != null) return "";
            StringBuilder text = new StringBuilder();
            string shown = String.IsNullOrEmpty(RealClass) ? ClassName : RealClass;
            if (!String.IsNullOrEmpty(shown)) Append(text, Shorten(shown, 40));
            if (CtrlId != 0 && CtrlId != -1) Append(text, "id " + CtrlId.ToString(CultureInfo.InvariantCulture));
            if (!String.IsNullOrEmpty(ProcessName)) Append(text, ProcessName);
            if (Rect != null)
            {
                Append(text, Rect.Width.ToString(CultureInfo.InvariantCulture) + "x" + Rect.Height.ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        // Everything on one line, for anywhere that has one line to say it in.
        public string Chip()
        {
            string head = Headline();
            string more = Detail();
            if (more.Length == 0) return head;
            return head + "   " + more;
        }

        private static void Append(StringBuilder text, string piece)
        {
            if (String.IsNullOrEmpty(piece)) return;
            if (text.Length != 0) text.Append("   ");
            text.Append(piece);
        }

        private static string Shorten(string value, int limit)
        {
            string flat = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (flat.Length <= limit) return flat;
            return flat.Substring(0, limit - 1) + "...";
        }
    }

    // Reading what is under the pointer while a recording runs.
    //
    // This is deliberately Win32 only and deliberately cheap. It runs on the
    // recorder's own poll, which is also the loop that must never fall behind:
    // asking UI Automation for the element under the pointer takes hundreds of
    // milliseconds and can block on an application that has stopped answering,
    // and a recording that drops presses because the pointer moved is a worse
    // outcome than a chip that says less.
    //
    // Every call is bounded and nothing here throws. Where a fact cannot be had,
    // the fact that it could not be had is what comes back.
    public static class Inspector
    {
        private const int CaptionTimeoutMs = 60;

        public static InspectFact At(int x, int y, long[] ignore)
        {
            InspectFact fact = new InspectFact();
            try
            {
                NativeMethods.POINT point;
                point.X = x;
                point.Y = y;
                IntPtr window = NativeMethods.WindowFromPoint(point);
                if (window == IntPtr.Zero)
                {
                    fact.Problem = "no window at this point";
                    return fact;
                }
                // Nothing this product draws is ever described as if it were the
                // application being recorded.
                if (IsOurs(window, ignore)) return null;

                IntPtr deeper = Descend(window, x, y);
                if (deeper != IntPtr.Zero) window = deeper;
                if (IsOurs(window, ignore)) return null;

                fact.Hwnd = window.ToInt64();
                fact.Rect = WindowTools.GetPhysicalRect(window);
                fact.ClassName = ClassOf(window, false);
                fact.RealClass = ClassOf(window, true);
                fact.CtrlId = SafeCtrlId(window);
                fact.ProcessId = WindowTools.ProcessIdOf(fact.Hwnd);
                fact.ProcessName = ProcessNameOf(fact.ProcessId);
                fact.TopLevel = NativeMethods.GetParent(window) == IntPtr.Zero;
                fact.Caption = Caption(window);
                if (fact.Rect == null) fact.Problem = "this window reported no rectangle";
                return fact;
            }
            catch (Exception exception)
            {
                fact.Problem = exception.GetType().Name;
                return fact;
            }
        }

        private static bool IsOurs(IntPtr window, long[] ignore)
        {
            long handle = window.ToInt64();
            if (ignore != null)
            {
                for (int index = 0; index < ignore.Length; index++)
                {
                    if (ignore[index] == handle) return true;
                }
            }
            try
            {
                int pid = WindowTools.ProcessIdOf(handle);
                return pid == System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                return false;
            }
        }

        // The deepest child that actually contains the point. Bounded, because a
        // badly behaved hierarchy could otherwise be walked for ever.
        private static IntPtr Descend(IntPtr window, int x, int y)
        {
            IntPtr current = window;
            for (int depth = 0; depth < 12; depth++)
            {
                NativeMethods.RECT box;
                if (!NativeMethods.GetWindowRect(current, out box)) return current;
                NativeMethods.POINT local;
                local.X = x - box.Left;
                local.Y = y - box.Top;
                IntPtr child = NativeMethods.RealChildWindowFromPoint(current, local);
                if (child == IntPtr.Zero || child == current) return current;
                current = child;
            }
            return current;
        }

        private static string ClassOf(IntPtr window, bool real)
        {
            try
            {
                StringBuilder builder = new StringBuilder(256);
                if (real) NativeMethods.RealGetWindowClass(window, builder, (uint)builder.Capacity);
                else NativeMethods.GetClassName(window, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static int SafeCtrlId(IntPtr window)
        {
            try { return NativeMethods.GetDlgCtrlID(window); }
            catch { return 0; }
        }

        private static string ProcessNameOf(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return null;
            }
        }

        // Bounded, and an application that does not answer simply has no caption
        // on the chip for that moment. It is never waited for.
        private static string Caption(IntPtr window)
        {
            try
            {
                IntPtr lengthResult;
                IntPtr call = NativeMethods.SendMessageTimeout(
                    window,
                    NativeMethods.WM_GETTEXTLENGTH,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                    unchecked((uint)CaptionTimeoutMs),
                    out lengthResult);
                if (call == IntPtr.Zero) return null;
                int length = Math.Max(0, lengthResult.ToInt32());
                if (length <= 0) return "";
                if (length > 512) length = 512;
                StringBuilder builder = new StringBuilder(length + 1);
                IntPtr textResult;
                IntPtr textCall = NativeMethods.SendMessageTimeout(
                    window,
                    NativeMethods.WM_GETTEXT,
                    new IntPtr(builder.Capacity),
                    builder,
                    NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                    unchecked((uint)CaptionTimeoutMs),
                    out textResult);
                if (textCall == IntPtr.Zero) return null;
                return builder.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
