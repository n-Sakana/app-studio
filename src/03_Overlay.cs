namespace AppStudio
{
    using System;
    using System.Runtime.InteropServices;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Interop;
    using System.Windows.Media;

    public sealed class OverlayController : IDisposable
    {
        private readonly Window frame;
        private readonly Window summary;
        private readonly TextBlock summaryText;
        private IntPtr frameHandle;
        private IntPtr summaryHandle;

        public OverlayController()
        {
            // The overlay lies on top of somebody else's application, so it does
            // not follow the light and dark theme: it has to stay legible over
            // whatever is underneath. It still takes its accent and its type
            // scale from the same tokens as the window, so the highlight is
            // recognisably the same product.
            frame = CreateWindow();
            Border border = new Border();
            border.BorderBrush = new SolidColorBrush(Theme.Parse("#1679D6"));
            border.BorderThickness = new Thickness(3);
            border.Background = Brushes.Transparent;
            frame.Content = border;

            summary = CreateWindow();
            Border summaryBorder = new Border();
            summaryBorder.Background = new SolidColorBrush(Color.FromArgb(225, 24, 28, 36));
            summaryBorder.BorderBrush = new SolidColorBrush(Theme.Parse("#1679D6"));
            summaryBorder.BorderThickness = new Thickness(1);
            summaryBorder.CornerRadius = new CornerRadius(Theme.RadiusSm);
            summaryBorder.Padding = new Thickness(Theme.Space2, 5, Theme.Space2, 5);
            summaryText = new TextBlock();
            summaryText.Foreground = Brushes.White;
            summaryText.FontFamily = Theme.UiFont;
            summaryText.FontSize = Theme.MetaSize;
            summaryText.TextWrapping = TextWrapping.NoWrap;
            summaryBorder.Child = summaryText;
            summary.Content = summaryBorder;

            frameHandle = new WindowInteropHelper(frame).EnsureHandle();
            summaryHandle = new WindowInteropHelper(summary).EnsureHandle();
            MakePassive(frameHandle);
            MakePassive(summaryHandle);
        }

        public void ShowSnapshot(Snapshot snapshot, int cursorX, int cursorY)
        {
            ShowHighlight(SelectRectangle(snapshot), BuildSummary(snapshot), cursorX, cursorY);
        }

        public void ShowHighlight(RectValue rectangle, string summaryContent, int cursorX, int cursorY)
        {
            if (rectangle == null || rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                Hide();
                return;
            }
            summaryText.Text = summaryContent ?? String.Empty;
            if (!frame.IsVisible) frame.Show();
            if (!summary.IsVisible) summary.Show();
            NativeMethods.SetWindowPos(frameHandle, new IntPtr(-1), rectangle.X, rectangle.Y, Math.Max(1, rectangle.Width), Math.Max(1, rectangle.Height), NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursorX, cursorY));
            int summaryWidth = 300;
            int summaryHeight = 100;
            int summaryX = cursorX + 20;
            int summaryY = cursorY + 20;
            if (summaryX + summaryWidth > screen.WorkingArea.Right) summaryX = cursorX - summaryWidth - 20;
            if (summaryY + summaryHeight > screen.WorkingArea.Bottom) summaryY = cursorY - summaryHeight - 20;
            summaryX = Math.Max(screen.WorkingArea.Left, summaryX);
            summaryY = Math.Max(screen.WorkingArea.Top, summaryY);
            NativeMethods.SetWindowPos(summaryHandle, new IntPtr(-1), summaryX, summaryY, summaryWidth, summaryHeight, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        public RectValue GetFrameRect()
        {
            return WindowTools.GetPhysicalRect(frameHandle);
        }

        public void Hide()
        {
            frame.Hide();
            summary.Hide();
            // Layered, per-pixel-alpha windows can linger on screen for a frame
            // after WPF marks them hidden. Force the native hide so a capture or
            // UI Automation re-probe that follows immediately sees no overlay.
            NativeMethods.ShowWindow(frameHandle, NativeMethods.SW_HIDE);
            NativeMethods.ShowWindow(summaryHandle, NativeMethods.SW_HIDE);
        }

        public void SetSummaryVisible(bool visible)
        {
            if (visible)
            {
                if (frame.IsVisible && !summary.IsVisible) summary.Show();
            }
            else
            {
                summary.Hide();
            }
        }

        public bool ContainsPoint(int x, int y)
        {
            return Contains(frameHandle, x, y) || Contains(summaryHandle, x, y);
        }

        public void Dispose()
        {
            frame.Close();
            summary.Close();
        }

        private static Window CreateWindow()
        {
            Window window = new Window();
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.AllowsTransparency = true;
            window.Background = Brushes.Transparent;
            window.ShowInTaskbar = false;
            window.Topmost = true;
            window.ShowActivated = false;
            window.Focusable = false;
            window.Left = -10000;
            window.Top = -10000;
            window.Width = 1;
            window.Height = 1;
            return window;
        }

        private static RectValue SelectRectangle(Snapshot snapshot)
        {
            if (snapshot != null && snapshot.Uia != null && snapshot.Uia.BoundingRect != null && snapshot.Uia.BoundingRect.Width > 0)
                return snapshot.Uia.BoundingRect;
            if (snapshot != null && snapshot.Win32 != null) return snapshot.Win32.WindowRect;
            return null;
        }

        private static string BuildSummary(Snapshot snapshot)
        {
            string type = snapshot.Uia == null ? null : snapshot.Uia.ControlType;
            string name = snapshot.Uia == null ? null : snapshot.Uia.Name;
            string id = snapshot.Uia == null ? null : snapshot.Uia.AutomationId;
            string winClass = snapshot.Win32 == null ? null : snapshot.Win32.ClassName;
            int ctrlId = snapshot.Win32 == null ? 0 : snapshot.Win32.CtrlId;
            RectValue rect = SelectRectangle(snapshot);
            string line1 = Short(type, 24) + "  " + Short(name, 34);
            string line2 = !String.IsNullOrEmpty(id) ? "AutomationId: " + Short(id, 36) : "class: " + Short(winClass, 28) + "  ctrlId: " + ctrlId;
            string line3 = (rect == null ? "size: unknown" : rect.Width + "x" + rect.Height) + "  UIA:" + State(snapshot.UiaStatus) + " Win32:" + State(snapshot.Win32Status);
            return line1.Trim() + Environment.NewLine + line2 + Environment.NewLine + line3;
        }

        private static string State(ProbeStatus status)
        {
            return status == null ? "-" : status.State;
        }

        private static string Short(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Length <= limit ? value : value.Substring(0, limit - 1) + "...";
        }

        private static bool Contains(IntPtr window, int x, int y)
        {
            RectValue rect = WindowTools.GetPhysicalRect(window);
            return rect != null && x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height;
        }

        private static void MakePassive(IntPtr window)
        {
            const int GWL_EXSTYLE = -20;
            const long WS_EX_TRANSPARENT = 0x00000020L;
            const long WS_EX_TOOLWINDOW = 0x00000080L;
            const long WS_EX_NOACTIVATE = 0x08000000L;
            long style = GetWindowLongValue(window, GWL_EXSTYLE);
            SetWindowLongValue(window, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private static long GetWindowLongValue(IntPtr window, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(window, index).ToInt64() : GetWindowLong(window, index);
        }

        private static void SetWindowLongValue(IntPtr window, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(window, index, new IntPtr(value));
            else SetWindowLong(window, index, unchecked((int)value));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    }
}
