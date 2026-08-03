namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;

    internal static class NativeMethods
    {
        internal const uint WM_GETTEXT = 0x000D;
        internal const uint WM_GETTEXTLENGTH = 0x000E;
        internal const uint SMTO_NORMAL = 0x0000;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;
        internal const uint CWP_SKIPINVISIBLE = 0x0001;
        internal const uint CWP_SKIPDISABLED = 0x0002;
        internal const uint GW_HWNDPREV = 3;
        internal const uint GW_CHILD = 5;
        internal const uint GW_HWNDNEXT = 2;
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint BM_CLICK = 0x00F5;
        internal const uint WM_SETTEXT = 0x000C;
        internal const uint INPUT_MOUSE = 0;
        internal const uint INPUT_KEYBOARD = 1;
        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_UNICODE = 0x0004;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;
        internal const uint GA_ROOT = 2;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            internal int dx;
            internal int dy;
            internal uint mouseData;
            internal uint flags;
            internal uint time;
            internal IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            internal ushort virtualKey;
            internal ushort scanCode;
            internal uint flags;
            internal uint time;
            internal IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)] internal MOUSEINPUT mouse;
            [FieldOffset(0)] internal KEYBDINPUT keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            internal uint type;
            internal INPUTUNION data;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        internal static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT point, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr RealChildWindowFromPoint(IntPtr parent, POINT point);

        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr window, ref POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RealGetWindowClass(IntPtr window, StringBuilder className, uint maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetClientRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetDlgCtrlID(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleObjectFromPoint(POINT point, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, out object childId);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleChildren(Accessibility.IAccessible container, int childStart, int childCount, [Out] object[] children, out int obtained);

        [DllImport("user32.dll")]
        internal static extern bool PhysicalToLogicalPoint(IntPtr window, ref POINT point);

        [DllImport("user32.dll")]
        internal static extern bool LogicalToPhysicalPoint(IntPtr window, ref POINT point);

        [DllImport("oleacc.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetRoleText(uint role, StringBuilder text, uint maxCount);

        [DllImport("oleacc.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetStateText(uint state, StringBuilder text, uint maxCount);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowEnabled(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT point);

        // Only mouse buttons are ever queried. No keyboard state is read, so the
        // tool has no path by which typed text could reach a log.
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        internal const int VK_LBUTTON = 0x01;
        internal const int VK_RBUTTON = 0x02;
        internal const int VK_MBUTTON = 0x04;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr window);

        internal const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        internal const int DWMWA_CLOAKED = 14;

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr window, int command);

        internal const int SW_HIDE = 0;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        internal static long GetWindowLongValue(IntPtr window, int index)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtr64(window, index).ToInt64();
            }
            return GetWindowLong32(window, index);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            IntPtr wParam,
            StringBuilder lParam,
            uint flags,
            uint timeout,
            out IntPtr result);
    }

    internal static class NativeInput
    {
        internal static bool Click(int x, int y)
        {
            if (!NativeMethods.SetCursorPos(x, y)) return false;
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_MOUSE;
            inputs[0].data.mouse.flags = NativeMethods.MOUSEEVENTF_LEFTDOWN;
            inputs[1].type = NativeMethods.INPUT_MOUSE;
            inputs[1].data.mouse.flags = NativeMethods.MOUSEEVENTF_LEFTUP;
            return NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT))) == 2;
        }

        // A shortcut is sent as the keys that make it: the modifiers go down,
        // the key goes down and up, the modifiers come up. Nothing about the
        // key is guessed - the caller has already turned a written chord back
        // into virtual key codes or refused it.
        internal static bool SendChord(int[] modifiers, int key)
        {
            if (key == 0) return false;
            int count = (modifiers == null ? 0 : modifiers.Length) * 2 + 2;
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[count];
            int cursor = 0;
            if (modifiers != null)
            {
                for (int index = 0; index < modifiers.Length; index++)
                {
                    inputs[cursor].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[cursor].data.keyboard.virtualKey = (ushort)modifiers[index];
                    cursor++;
                }
            }
            inputs[cursor].type = NativeMethods.INPUT_KEYBOARD;
            inputs[cursor].data.keyboard.virtualKey = (ushort)key;
            cursor++;
            inputs[cursor].type = NativeMethods.INPUT_KEYBOARD;
            inputs[cursor].data.keyboard.virtualKey = (ushort)key;
            inputs[cursor].data.keyboard.flags = NativeMethods.KEYEVENTF_KEYUP;
            cursor++;
            if (modifiers != null)
            {
                for (int index = modifiers.Length - 1; index >= 0; index--)
                {
                    inputs[cursor].type = NativeMethods.INPUT_KEYBOARD;
                    inputs[cursor].data.keyboard.virtualKey = (ushort)modifiers[index];
                    inputs[cursor].data.keyboard.flags = NativeMethods.KEYEVENTF_KEYUP;
                    cursor++;
                }
            }
            return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT))) == inputs.Length;
        }

        internal static bool SendUnicode(string text)
        {
            if (String.IsNullOrEmpty(text)) return true;
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[text.Length * 2];
            for (int index = 0; index < text.Length; index++)
            {
                inputs[index * 2].type = NativeMethods.INPUT_KEYBOARD;
                inputs[index * 2].data.keyboard.scanCode = text[index];
                inputs[index * 2].data.keyboard.flags = NativeMethods.KEYEVENTF_UNICODE;
                inputs[index * 2 + 1].type = NativeMethods.INPUT_KEYBOARD;
                inputs[index * 2 + 1].data.keyboard.scanCode = text[index];
                inputs[index * 2 + 1].data.keyboard.flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
            }
            return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT))) == inputs.Length;
        }
    }

    public static class DpiAwareness
    {
        public static string State = "not-attempted";
        public static string Reason;

        public static void Enable()
        {
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    State = "PerMonitorV2";
                    return;
                }
                Reason = "SetProcessDpiAwarenessContext returned false.";
            }
            catch (Exception exception)
            {
                Reason = exception.GetType().Name + ": " + exception.Message;
            }
            try
            {
                if (NativeMethods.SetProcessDPIAware())
                {
                    State = "System";
                    return;
                }
            }
            catch (Exception exception)
            {
                Reason = (Reason ?? String.Empty) + " SetProcessDPIAware: " + exception.Message;
            }
            State = "unaware";
        }
    }

    public static class WindowTools
    {
        public static bool Move(IntPtr window, int x, int y, int width, int height)
        {
            return NativeMethods.SetWindowPos(window, IntPtr.Zero, x, y, width, height, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        public static RectValue GetPhysicalRect(IntPtr window)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(window, out rect)) return null;
            RectValue value = new RectValue();
            value.X = rect.Left;
            value.Y = rect.Top;
            value.Width = rect.Right - rect.Left;
            value.Height = rect.Bottom - rect.Top;
            return value;
        }

        public static int CountChildren(IntPtr window)
        {
            int count = 0;
            IntPtr child = NativeMethods.GetWindow(window, NativeMethods.GW_CHILD);
            while (child != IntPtr.Zero && count < 10000)
            {
                count++;
                child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
            }
            return count;
        }

        // Reads where the pointer already is. Nothing in the product moves it.
        public static PointValue CursorPosition()
        {
            NativeMethods.POINT point;
            if (!NativeMethods.GetCursorPos(out point)) return null;
            PointValue value = new PointValue();
            value.X = point.X;
            value.Y = point.Y;
            return value;
        }

        // Cheap ownership test used to keep manual observation inside the target
        // application; it costs two Win32 calls and touches no accessibility API.
        public static int ProcessIdAt(int x, int y)
        {
            NativeMethods.POINT point = new NativeMethods.POINT();
            point.X = x;
            point.Y = y;
            IntPtr window = NativeMethods.WindowFromPoint(point);
            if (window == IntPtr.Zero) return 0;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            return unchecked((int)processId);
        }

        // A packaged application is shown inside a frame window owned by
        // ApplicationFrameHost while the contents belong to the application's
        // own process. Calculator is the plain example: the entry in the window
        // list is ApplicationFrameHost, but every button inside it reports a
        // different process. Anything that filters by the frame's process id
        // alone would discard the whole window, so the processes that actually
        // draw inside it are resolved here.
        public static int[] ContentProcessIds(IntPtr window, int framePid)
        {
            List<int> found = new List<int>();
            if (window == IntPtr.Zero) return found.ToArray();
            try
            {
                NativeMethods.EnumChildWindows(window, delegate(IntPtr child, IntPtr parameter)
                {
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(child, out processId);
                    int value = unchecked((int)processId);
                    if (value != 0 && value != framePid && !found.Contains(value)) found.Add(value);
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // An unreadable frame is not fatal: the caller keeps the frame
                // process id it already had.
            }
            return found.ToArray();
        }

        // Whether a window is actually on the display. Hiding a window leaves its
        // rectangle intact, so a rectangle is no evidence that something was
        // taken off the screen; this is.
        public static bool IsVisible(long hwnd)
        {
            if (hwnd == 0) return false;
            IntPtr window = new IntPtr(hwnd);
            return NativeMethods.IsWindowVisible(window) && !IsCloaked(window);
        }

        public static bool IsCloaked(IntPtr window)
        {
            try
            {
                int value;
                if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out value, 4) == 0) return value != 0;
            }
            catch
            {
            }
            return false;
        }

        // Every visible top level window of one process, including the untitled
        // popups and menus that carry part of what is on screen right now.
        public static TargetWindowInfo[] ListProcessWindows(int processId, long preferredHwnd)
        {
            System.Collections.Generic.List<TargetWindowInfo> result = new System.Collections.Generic.List<TargetWindowInfo>();
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                if (unchecked((int)pid) != processId) return true;
                if (!NativeMethods.IsWindowVisible(window)) return true;
                if (IsCloaked(window)) return true;
                RectValue rect = GetPhysicalRect(window);
                if (rect == null || rect.Width <= 0 || rect.Height <= 0) return true;
                StringBuilder title = new StringBuilder(1024);
                NativeMethods.GetWindowText(window, title, title.Capacity);
                StringBuilder className = new StringBuilder(512);
                NativeMethods.GetClassName(window, className, className.Capacity);
                TargetWindowInfo item = new TargetWindowInfo();
                item.Hwnd = window.ToInt64();
                item.ProcessId = processId;
                item.Title = title.ToString();
                item.ClassName = className.ToString();
                item.Rect = rect;
                result.Add(item);
                return true;
            }, IntPtr.Zero);
            result.Sort(delegate(TargetWindowInfo left, TargetWindowInfo right)
            {
                if (left.Hwnd == preferredHwnd && right.Hwnd != preferredHwnd) return -1;
                if (right.Hwnd == preferredHwnd && left.Hwnd != preferredHwnd) return 1;
                long leftArea = left.Rect == null ? 0 : (long)left.Rect.Width * left.Rect.Height;
                long rightArea = right.Rect == null ? 0 : (long)right.Rect.Width * right.Rect.Height;
                return rightArea.CompareTo(leftArea);
            });
            return result.ToArray();
        }

        // Top level windows in the order the desktop stacks them, front first.
        // The picker draws over the whole screen, so WindowFromPoint would only
        // ever return the picker; the stack order is what answers "which window
        // is the operator pointing at" without any window having to be made
        // click-through.
        public static TargetWindowInfo[] ListStackOrder(long[] excludedHwnds, int excludedProcessId)
        {
            List<TargetWindowInfo> result = new List<TargetWindowInfo>();
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                long handle = window.ToInt64();
                if (excludedHwnds != null)
                {
                    for (int index = 0; index < excludedHwnds.Length; index++) if (excludedHwnds[index] == handle) return true;
                }
                if (!NativeMethods.IsWindowVisible(window)) return true;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                int processId = unchecked((int)pid);
                if (excludedProcessId != 0 && processId == excludedProcessId) return true;
                if (IsCloaked(window)) return true;
                RectValue rect = GetPhysicalRect(window);
                if (rect == null || rect.Width <= 1 || rect.Height <= 1) return true;
                long style = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_STYLE);
                long exStyle = NativeMethods.GetWindowLongValue(window, NativeMethods.GWL_EXSTYLE);
                // A child of another window is not a top level surface, and a
                // transparent layered window is something drawn over the desktop
                // rather than an application the operator can point at.
                if ((style & 0x40000000L) != 0) return true;
                if ((exStyle & 0x00000020L) != 0) return true;
                StringBuilder title = new StringBuilder(1024);
                NativeMethods.GetWindowText(window, title, title.Capacity);
                StringBuilder className = new StringBuilder(512);
                NativeMethods.GetClassName(window, className, className.Capacity);
                TargetWindowInfo item = new TargetWindowInfo();
                item.Hwnd = handle;
                item.ProcessId = processId;
                item.Title = title.ToString();
                item.ClassName = className.ToString();
                item.Rect = rect;
                result.Add(item);
                return true;
            }, IntPtr.Zero);
            FillProcessNames(result);
            return result.ToArray();
        }

        public static TargetWindowInfo WindowAt(TargetWindowInfo[] stack, int x, int y)
        {
            if (stack == null) return null;
            for (int index = 0; index < stack.Length; index++)
            {
                RectValue rect = stack[index].Rect;
                if (rect == null) continue;
                if (x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height) return stack[index];
            }
            return null;
        }

        // The window a foreground change points at, reduced to its top level
        // root so a focus change inside an application is not read as a switch
        // to a different one.
        public static TargetWindowInfo DescribeTopLevel(IntPtr window)
        {
            if (window == IntPtr.Zero) return null;
            IntPtr top = NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
            if (top == IntPtr.Zero) top = window;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(top, out pid);
            StringBuilder title = new StringBuilder(1024);
            NativeMethods.GetWindowText(top, title, title.Capacity);
            StringBuilder className = new StringBuilder(512);
            NativeMethods.GetClassName(top, className, className.Capacity);
            TargetWindowInfo item = new TargetWindowInfo();
            item.Hwnd = top.ToInt64();
            item.ProcessId = unchecked((int)pid);
            item.Title = title.ToString();
            item.ClassName = className.ToString();
            item.Rect = GetPhysicalRect(top);
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(item.ProcessId)) item.ProcessName = process.ProcessName;
            }
            catch
            {
                item.ProcessName = null;
            }
            return item;
        }

        public static TargetWindowInfo Foreground()
        {
            return DescribeTopLevel(NativeMethods.GetForegroundWindow());
        }

        // Windows refuses SetForegroundWindow to a process that is not itself in
        // front, which is exactly the situation during a replay: this window is
        // hidden so it does not cover the target. Sharing the input queue with
        // the thread that currently owns the foreground lifts that refusal for
        // the moment it takes to hand the front over, and the attachment is
        // always undone.
        //
        // The result is never assumed. The caller is told whether the window is
        // actually in front now, because "asked politely" is not the same fact
        // as "is in front", and a step carried out against a window that never
        // came forward would be carried out against whatever did.
        public static bool BringToFront(long hwnd)
        {
            if (hwnd == 0) return false;
            IntPtr window = new IntPtr(hwnd);
            if (NativeMethods.GetForegroundWindow() == window) return true;
            if (NativeMethods.IsIconic(window)) NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(window);
            if (NativeMethods.GetForegroundWindow() == window) return true;

            IntPtr front = NativeMethods.GetForegroundWindow();
            uint frontThread = front == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(front, out Discard);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(window, out Discard);
            uint ownThread = NativeMethods.GetCurrentThreadId();
            bool attachedFront = false;
            bool attachedTarget = false;
            try
            {
                if (frontThread != 0 && frontThread != ownThread) attachedFront = NativeMethods.AttachThreadInput(ownThread, frontThread, true);
                if (targetThread != 0 && targetThread != ownThread) attachedTarget = NativeMethods.AttachThreadInput(ownThread, targetThread, true);
                NativeMethods.BringWindowToTop(window);
                NativeMethods.SetForegroundWindow(window);
            }
            finally
            {
                if (attachedTarget) NativeMethods.AttachThreadInput(ownThread, targetThread, false);
                if (attachedFront) NativeMethods.AttachThreadInput(ownThread, frontThread, false);
            }
            return NativeMethods.GetForegroundWindow() == window;
        }

        private static uint Discard;

        private static void FillProcessNames(List<TargetWindowInfo> result)
        {
            Dictionary<int, string> names = new Dictionary<int, string>();
            for (int index = 0; index < result.Count; index++)
            {
                string name;
                if (!names.TryGetValue(result[index].ProcessId, out name))
                {
                    try
                    {
                        using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(result[index].ProcessId)) name = process.ProcessName;
                    }
                    catch
                    {
                        name = null;
                    }
                    names[result[index].ProcessId] = name;
                }
                result[index].ProcessName = name;
            }
        }

        public static TargetWindowInfo[] ListTopLevelWindows()
        {
            System.Collections.Generic.List<TargetWindowInfo> result = new System.Collections.Generic.List<TargetWindowInfo>();
            int ownProcess = System.Diagnostics.Process.GetCurrentProcess().Id;
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(window)) return true;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(window, out pid);
                if (pid == ownProcess) return true;
                StringBuilder title = new StringBuilder(1024);
                NativeMethods.GetWindowText(window, title, title.Capacity);
                if (String.IsNullOrWhiteSpace(title.ToString())) return true;
                if (IsCloaked(window)) return true;
                RectValue rect = GetPhysicalRect(window);
                if (rect == null || rect.Width <= 0 || rect.Height <= 0) return true;
                StringBuilder className = new StringBuilder(512);
                NativeMethods.GetClassName(window, className, className.Capacity);
                TargetWindowInfo item = new TargetWindowInfo();
                item.Hwnd = window.ToInt64();
                item.ProcessId = unchecked((int)pid);
                item.Title = title.ToString();
                item.ClassName = className.ToString();
                item.Rect = rect;
                result.Add(item);
                return true;
            }, IntPtr.Zero);
            System.Collections.Generic.Dictionary<int, string> names = new System.Collections.Generic.Dictionary<int, string>();
            for (int index = 0; index < result.Count; index++)
            {
                string name;
                if (!names.TryGetValue(result[index].ProcessId, out name))
                {
                    try
                    {
                        using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(result[index].ProcessId)) name = process.ProcessName;
                    }
                    catch
                    {
                        name = null;
                    }
                    names[result[index].ProcessId] = name;
                }
                result[index].ProcessName = name;
            }
            result.Sort(delegate(TargetWindowInfo left, TargetWindowInfo right)
            {
                long leftArea = left.Rect == null ? 0 : (long)left.Rect.Width * left.Rect.Height;
                long rightArea = right.Rect == null ? 0 : (long)right.Rect.Width * right.Rect.Height;
                return rightArea.CompareTo(leftArea);
            });
            return result.ToArray();
        }
    }

    public sealed class PointValue
    {
        public int X;
        public int Y;
    }

    public sealed class TargetWindowInfo
    {
        public long Hwnd;
        public int ProcessId;
        public string Title;
        public string ClassName;
        public RectValue Rect;
        public string ProcessName;

        public string DisplayTitle
        {
            get { return String.IsNullOrWhiteSpace(Title) ? "(" + ClassName + ")" : Title; }
        }

        public override string ToString()
        {
            return Title + "  [" + ClassName + ", pid " + ProcessId + "]";
        }
    }

    public static class DpiTools
    {
        public static int GetDpiAt(int x, int y)
        {
            try
            {
                NativeMethods.POINT point = new NativeMethods.POINT();
                point.X = x;
                point.Y = y;
                IntPtr monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
                uint dpiX;
                uint dpiY;
                if (monitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0) return unchecked((int)dpiX);
            }
            catch
            {
            }
            return 96;
        }

        public static string MonitorIdAt(int x, int y)
        {
            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            return screen == null ? null : screen.DeviceName;
        }
    }
}
