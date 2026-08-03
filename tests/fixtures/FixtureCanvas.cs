using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class FixtureCanvas
{
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const int SW_SHOW = 5;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const int COLOR_WINDOW = 5;
    private static WndProc windowProcedure;

    [STAThread]
    private static int Main(string[] args)
    {
        string ready = null;
        for (int index = 0; index < args.Length; index++) if (args[index] == "--ready" && index + 1 < args.Length) ready = args[++index];
        if (String.IsNullOrEmpty(ready)) throw new ArgumentException("--ready is required.");
        windowProcedure = WindowProcedure;
        WNDCLASSEX windowClass = new WNDCLASSEX();
        windowClass.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
        windowClass.style = CS_HREDRAW | CS_VREDRAW;
        windowClass.lpfnWndProc = windowProcedure;
        windowClass.hInstance = GetModuleHandle(null);
        windowClass.hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512));
        windowClass.hbrBackground = new IntPtr(COLOR_WINDOW + 1);
        windowClass.lpszClassName = "FixtureCanvasWindow";
        if (RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
        IntPtr window = CreateWindowEx(WS_EX_TOPMOST, windowClass.lpszClassName, String.Empty, WS_POPUP | WS_VISIBLE, 1450, 120, 420, 300, IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        ShowWindow(window, SW_SHOW);
        UpdateWindow(window);
        RECT rect;
        GetWindowRect(window, out rect);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ready)));
        string json = "{\"hwnd\":" + window.ToInt64().ToString(CultureInfo.InvariantCulture) + ",\"left\":" + rect.Left.ToString(CultureInfo.InvariantCulture) + ",\"top\":" + rect.Top.ToString(CultureInfo.InvariantCulture) + ",\"right\":" + rect.Right.ToString(CultureInfo.InvariantCulture) + ",\"bottom\":" + rect.Bottom.ToString(CultureInfo.InvariantCulture) + ",\"x\":" + ((rect.Left + rect.Right) / 2).ToString(CultureInfo.InvariantCulture) + ",\"y\":" + ((rect.Top + rect.Bottom) / 2).ToString(CultureInfo.InvariantCulture) + "}";
        File.WriteAllText(ready, json, new UTF8Encoding(false));
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
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint;
            IntPtr dc = BeginPaint(window, out paint);
            RECT box = new RECT(); box.Left = 24; box.Top = 24; box.Right = 396; box.Bottom = 276;
            Rectangle(dc, box.Left, box.Top, box.Right, box.Bottom);
            string text = "CUSTOM CANVAS\r\nOrder area 42\r\nNo child HWND and no UIA child";
            DrawText(dc, text, text.Length, ref box, 0x00000000 | 0x00000010 | 0x00000400);
            EndPaint(window, ref paint);
            return IntPtr.Zero;
        }
        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEX { internal uint cbSize; internal uint style; internal WndProc lpfnWndProc; internal int cbClsExtra; internal int cbWndExtra; internal IntPtr hInstance; internal IntPtr hIcon; internal IntPtr hCursor; internal IntPtr hbrBackground; internal string lpszMenuName; internal string lpszClassName; internal IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { internal IntPtr hwnd; internal uint message; internal UIntPtr wParam; internal IntPtr lParam; internal uint time; internal POINT point; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { internal IntPtr hdc; internal bool erase; internal RECT paint; internal bool restore; internal bool update; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] reserved; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PAINTSTRUCT paint);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref RECT rect, uint format);
}
