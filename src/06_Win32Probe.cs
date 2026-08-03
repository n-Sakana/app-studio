namespace AppStudio
{
    using System;
    using System.Runtime.InteropServices;
    using System.Text;

    public static class Win32Probe
    {
        public static Win32Info AtPoint(int screenX, int screenY, int messageTimeoutMs)
        {
            NativeMethods.POINT point = new NativeMethods.POINT();
            point.X = screenX;
            point.Y = screenY;
            IntPtr window = NativeMethods.WindowFromPoint(point);
            if (window == IntPtr.Zero)
            {
                Win32Info missing = new Win32Info();
                missing.Status = ProbeStatus.Unavailable("WIN32-NOHWND", "No window exists at the requested screen point.");
                return missing;
            }
            IntPtr deepest = FindDeepest(window, point);
            return AtHandle(deepest, messageTimeoutMs);
        }

        public static Win32Info AtHandle(IntPtr window, int messageTimeoutMs)
        {
            if (window == IntPtr.Zero)
            {
                Win32Info missing = new Win32Info();
                missing.Status = ProbeStatus.Unavailable("WIN32-NOHWND", "The supplied window handle is zero.");
                return missing;
            }

            Win32Info info = new Win32Info();
            info.Status = ProbeStatus.Ok();
            info.Hwnd = window.ToInt64();
            try
            {
                uint processId;
                info.ThreadId = unchecked((int)NativeMethods.GetWindowThreadProcessId(window, out processId));
                info.ProcessId = unchecked((int)processId);
                info.ClassName = ReadClass(window, false);
                info.RealClass = ReadClass(window, true);
                info.CtrlId = NativeMethods.GetDlgCtrlID(window);
                info.Style = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_STYLE);
                info.ExStyle = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_EXSTYLE);
                info.WindowRect = ReadWindowRect(window);
                info.ClientRect = ReadClientRect(window);
                if (info.WindowRect != null)
                {
                    int centerX = info.WindowRect.X + info.WindowRect.Width / 2;
                    int centerY = info.WindowRect.Y + info.WindowRect.Height / 2;
                    info.MonitorId = DpiTools.MonitorIdAt(centerX, centerY);
                    info.Dpi = DpiTools.GetDpiAt(centerX, centerY);
                }
                info.Visible = NativeMethods.IsWindowVisible(window);
                info.Enabled = NativeMethods.IsWindowEnabled(window);
                info.ZIndex = ReadZIndex(window);
                ReadCaption(window, messageTimeoutMs, info);
                ReadAncestors(window, info);
                ReadTopLevel(window, info);
            }
            catch (Exception exception)
            {
                info.Status.AddPartial("WIN32-ACCESS", exception.GetType().Name + ": " + exception.Message);
            }
            return info;
        }

        private static IntPtr FindDeepest(IntPtr initial, NativeMethods.POINT screenPoint)
        {
            IntPtr current = initial;
            for (int depth = 0; depth < 16; depth++)
            {
                NativeMethods.POINT client = screenPoint;
                if (!NativeMethods.ScreenToClient(current, ref client))
                {
                    break;
                }
                IntPtr child = NativeMethods.ChildWindowFromPointEx(current, client, NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPDISABLED);
                if (child == IntPtr.Zero || child == current)
                {
                    break;
                }
                IntPtr realChild = NativeMethods.RealChildWindowFromPoint(current, client);
                current = realChild != IntPtr.Zero && realChild != current ? realChild : child;
            }
            return current;
        }

        private static string ReadClass(IntPtr window, bool real)
        {
            StringBuilder builder = new StringBuilder(512);
            if (real)
            {
                NativeMethods.RealGetWindowClass(window, builder, (uint)builder.Capacity);
            }
            else
            {
                NativeMethods.GetClassName(window, builder, builder.Capacity);
            }
            return builder.ToString();
        }

        private static void ReadCaption(IntPtr window, int timeoutMs, Win32Info info)
        {
            IntPtr lengthResult;
            IntPtr lengthCall = NativeMethods.SendMessageTimeout(
                window,
                NativeMethods.WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                unchecked((uint)Math.Max(1, timeoutMs)),
                out lengthResult);
            if (lengthCall == IntPtr.Zero)
            {
                info.Caption = ReadCachedCaption(window);
                info.CaptionSource = "GetWindowText";
                info.Status.AddPartial("WIN32-HUNG", "WM_GETTEXT did not return within " + timeoutMs + " ms; cached caption was retained.");
                return;
            }
            int length = Math.Max(0, lengthResult.ToInt32());
            StringBuilder builder = new StringBuilder(Math.Max(2, length + 1));
            IntPtr textResult;
            IntPtr textCall = NativeMethods.SendMessageTimeout(
                window,
                NativeMethods.WM_GETTEXT,
                new IntPtr(builder.Capacity),
                builder,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_NORMAL,
                unchecked((uint)Math.Max(1, timeoutMs)),
                out textResult);
            if (textCall == IntPtr.Zero)
            {
                info.Caption = ReadCachedCaption(window);
                info.CaptionSource = "GetWindowText";
                info.Status.AddPartial("WIN32-HUNG", "WM_GETTEXT did not return within " + timeoutMs + " ms; cached caption was retained.");
                return;
            }
            info.Caption = builder.ToString();
            info.CaptionSource = "WM_GETTEXT";
        }

        private static string ReadCachedCaption(IntPtr window)
        {
            StringBuilder builder = new StringBuilder(1024);
            NativeMethods.GetWindowText(window, builder, builder.Capacity);
            return builder.ToString();
        }

        private static RectValue ReadWindowRect(IntPtr window)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(window, out rect))
            {
                return null;
            }
            return ToRect(rect);
        }

        private static RectValue ReadClientRect(IntPtr window)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetClientRect(window, out rect))
            {
                return null;
            }
            return ToRect(rect);
        }

        private static RectValue ToRect(NativeMethods.RECT rect)
        {
            RectValue value = new RectValue();
            value.X = rect.Left;
            value.Y = rect.Top;
            value.Width = rect.Right - rect.Left;
            value.Height = rect.Bottom - rect.Top;
            return value;
        }

        private static int ReadZIndex(IntPtr window)
        {
            int index = 0;
            IntPtr current = window;
            while (index < 512)
            {
                current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDPREV);
                if (current == IntPtr.Zero)
                {
                    break;
                }
                index++;
            }
            return index;
        }

        private static void ReadTopLevel(IntPtr window, Win32Info info)
        {
            IntPtr top = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
            if (top == IntPtr.Zero) top = window;
            info.TopHwnd = top.ToInt64();
            info.TopCaption = ReadCachedCaption(top);
            info.TopClass = ReadClass(top, false);
            info.TopRect = ReadWindowRect(top);
        }

        private static void ReadAncestors(IntPtr window, Win32Info info)
        {
            IntPtr parent = NativeMethods.GetParent(window);
            for (int depth = 0; depth < 16 && parent != IntPtr.Zero; depth++)
            {
                Win32Ancestor ancestor = new Win32Ancestor();
                ancestor.Hwnd = parent.ToInt64();
                ancestor.ClassName = ReadClass(parent, false);
                ancestor.Caption = ReadCachedCaption(parent);
                ancestor.CtrlId = NativeMethods.GetDlgCtrlID(parent);
                info.Ancestors.Add(ancestor);
                parent = NativeMethods.GetParent(parent);
            }
        }
    }
}
