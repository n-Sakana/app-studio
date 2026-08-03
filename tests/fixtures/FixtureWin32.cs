using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class FixtureWin32
{
    private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_TABSTOP = 0x00010000;
    private const int ES_AUTOHSCROLL = 0x0080;
    private const int BS_PUSHBUTTON = 0x00000000;
    private const int BS_OWNERDRAW = 0x0000000B;
    private const int LVS_REPORT = 0x0001;
    private const int GWLP_WNDPROC = -4;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_APP_HANG = 0x8001;
    private const int ICC_LISTVIEW_CLASSES = 0x00000001;

    private static string mode;
    private static string readyPath;
    private static string hungPath;
    private static string releasePath;
    private static int temporarySeconds;
    private static WndProc newWindowProc;
    private static IntPtr oldWindowProc;

    [STAThread]
    private static int Main(string[] args)
    {
        Parse(args);
        INITCOMMONCONTROLSEX controls = new INITCOMMONCONTROLSEX();
        controls.Size = Marshal.SizeOf(typeof(INITCOMMONCONTROLSEX));
        controls.Classes = ICC_LISTVIEW_CLASSES;
        InitCommonControlsEx(ref controls);

        IntPtr window = CreateWindowEx(0, "#32770", "FixtureWin32", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 120, 120, 520, 360, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx(#32770) failed: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
        }
        IntPtr label = CreateWindowEx(0, "STATIC", "Customer code", WS_CHILD | WS_VISIBLE, 24, 28, 120, 24, window, new IntPtr(1001), IntPtr.Zero, IntPtr.Zero);
        IntPtr edit = CreateWindowEx(0, "EDIT", "ABC123", WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL, 160, 24, 180, 28, window, new IntPtr(1002), IntPtr.Zero, IntPtr.Zero);
        IntPtr button = CreateWindowEx(0, "BUTTON", "Register", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 360, 24, 110, 30, window, new IntPtr(1005), IntPtr.Zero, IntPtr.Zero);
        IntPtr owner = CreateWindowEx(0, "BUTTON", "Owner Draw", WS_CHILD | WS_VISIBLE | BS_OWNERDRAW, 24, 72, 120, 30, window, new IntPtr(1006), IntPtr.Zero, IntPtr.Zero);
        IntPtr list = CreateWindowEx(0, "SysListView32", "", WS_CHILD | WS_VISIBLE | LVS_REPORT, 24, 120, 446, 150, window, new IntPtr(1010), IntPtr.Zero, IntPtr.Zero);
        if (label == IntPtr.Zero || edit == IntPtr.Zero || button == IntPtr.Zero || owner == IntPtr.Zero || list == IntPtr.Zero)
        {
            throw new InvalidOperationException("Creating a native child control failed.");
        }

        newWindowProc = WindowProcedure;
        oldWindowProc = SetWindowLongPtr(window, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(newWindowProc));
        RECT editRect;
        GetWindowRect(edit, out editRect);
        WriteReady(window, edit, button, list, editRect);

        if (mode != "healthy")
        {
            Thread trigger = new Thread(delegate()
            {
                Thread.Sleep(100);
                SendMessage(window, WM_APP_HANG, IntPtr.Zero, IntPtr.Zero);
            });
            trigger.IsBackground = true;
            trigger.Start();
        }

        MSG message;
        while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return 0;
    }

    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_APP_HANG)
        {
            WriteSignal(hungPath, "hung");
            DateTime until = DateTime.UtcNow.AddSeconds(temporarySeconds);
            while (mode == "permanent" ? !File.Exists(releasePath) : DateTime.UtcNow < until)
            {
                Thread.Sleep(25);
            }
            return IntPtr.Zero;
        }
        if (message == WM_CLOSE)
        {
            DestroyWindow(window);
            return IntPtr.Zero;
        }
        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return CallWindowProc(oldWindowProc, window, message, wParam, lParam);
    }

    private static void Parse(string[] args)
    {
        mode = "healthy";
        temporarySeconds = 15;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--mode") mode = args[++index];
            else if (args[index] == "--ready") readyPath = args[++index];
            else if (args[index] == "--hung") hungPath = args[++index];
            else if (args[index] == "--release") releasePath = args[++index];
            else if (args[index] == "--temporary-seconds") temporarySeconds = int.Parse(args[++index], CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrEmpty(readyPath)) throw new ArgumentException("--ready is required.");
        if (string.IsNullOrEmpty(hungPath)) hungPath = readyPath + ".hung";
        if (string.IsNullOrEmpty(releasePath)) releasePath = readyPath + ".release";
    }

    private static void WriteReady(IntPtr window, IntPtr edit, IntPtr button, IntPtr list, RECT rect)
    {
        string json = "{\"window\":" + window.ToInt64().ToString(CultureInfo.InvariantCulture) +
            ",\"edit\":" + edit.ToInt64().ToString(CultureInfo.InvariantCulture) +
            ",\"button\":" + button.ToInt64().ToString(CultureInfo.InvariantCulture) +
            ",\"list\":" + list.ToInt64().ToString(CultureInfo.InvariantCulture) +
            ",\"editRect\":{\"left\":" + rect.Left.ToString(CultureInfo.InvariantCulture) +
            ",\"top\":" + rect.Top.ToString(CultureInfo.InvariantCulture) +
            ",\"right\":" + rect.Right.ToString(CultureInfo.InvariantCulture) +
            ",\"bottom\":" + rect.Bottom.ToString(CultureInfo.InvariantCulture) + "}}";
        WriteSignal(readyPath, json);
    }

    private static void WriteSignal(string path, string text)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX { internal int Size; internal int Classes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { internal IntPtr Hwnd; internal uint Message; internal IntPtr WParam; internal IntPtr LParam; internal uint Time; internal POINT Point; }
    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX controls);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern IntPtr SetWindowLong32(IntPtr window, int index, IntPtr value);
    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) { return IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : SetWindowLong32(window, index, value); }
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
}

