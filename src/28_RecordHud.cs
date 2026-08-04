namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Interop;
    using System.Windows.Media;

    // What is on screen while a recording runs: a restrained frame around the
    // window in front, and a small control saying that recording is happening
    // with a way to stop it.
    //
    // Both are windows of this application, so both are excluded from the
    // foreground tracking, from the pointer ownership test and from the window
    // chooser, and both are taken off the screen before any picture is made.
    // Nothing this product draws is allowed to end up in the evidence.
    public sealed class RecordHud : IDisposable, Acquire.ISurfaceGuard
    {
        private readonly Window frame;
        private readonly Window control;
        private readonly TextBlock clock;
        private readonly TextBlock dot;
        private readonly Button stop;
        private IntPtr frameHandle;
        private IntPtr controlHandle;
        private bool suppressed;
        private bool frameWanted;
        private RectValue frameRect;

        public event Action StopRequested;

        public RecordHud(string recordingLabel, string stopLabel)
        {
            frame = CreatePassiveWindow();
            Border edge = new Border();
            // While a recording runs the frame is red, the same colour as the
            // mark on the control, so the state of the machine is legible from
            // the edge of the screen without reading anything.
            edge.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 226, 74, 74));
            edge.BorderThickness = new Thickness(3);
            edge.Background = Brushes.Transparent;
            frame.Content = edge;

            control = CreateControlWindow();
            dot = new TextBlock();
            // The mark itself lives with the wording, because the sources are
            // held to plain ASCII.
            dot.Text = Messages.Text("hud-dot.txt", "*");
            dot.Foreground = new SolidColorBrush(Theme.Parse("#E24A4A"));
            dot.FontSize = 20;
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Margin = new Thickness(0, 0, Theme.Space2, 0);

            clock = new TextBlock();
            clock.Foreground = Brushes.White;
            clock.FontFamily = Theme.UiFont;
            clock.FontSize = 17;
            clock.FontWeight = FontWeights.Bold;
            clock.VerticalAlignment = VerticalAlignment.Center;
            clock.Text = recordingLabel + "  00:00";

            stop = new Button();
            stop.Content = stopLabel;
            stop.FontFamily = Theme.UiFont;
            // The stop control is the one thing an operator must never have to
            // hunt for, so it is a large target rather than a neat one.
            stop.FontSize = 15;
            stop.FontWeight = FontWeights.Bold;
            stop.Height = 44;
            stop.MinWidth = 132;
            stop.Margin = new Thickness(Theme.Space5, 0, 0, 0);
            stop.Padding = new Thickness(Theme.Space4, 0, Theme.Space4, 0);
            stop.Foreground = Brushes.White;
            stop.Background = new SolidColorBrush(Theme.Parse("#B03E48"));
            stop.BorderBrush = new SolidColorBrush(Theme.Parse("#E27680"));
            stop.BorderThickness = new Thickness(1);
            stop.Cursor = System.Windows.Input.Cursors.Hand;
            stop.Click += delegate
            {
                Action handler = StopRequested;
                if (handler != null) handler();
            };

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(dot);
            row.Children.Add(clock);
            row.Children.Add(stop);

            Border shell = new Border();
            shell.Background = new SolidColorBrush(Color.FromArgb(238, 16, 22, 30));
            shell.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            shell.BorderThickness = new Thickness(1);
            shell.CornerRadius = new CornerRadius(Theme.RadiusMd);
            shell.Padding = new Thickness(Theme.Space5, Theme.Space3, Theme.Space4, Theme.Space3);
            shell.Child = row;
            control.Content = shell;

            frameHandle = new WindowInteropHelper(frame).EnsureHandle();
            controlHandle = new WindowInteropHelper(control).EnsureHandle();
            MakePassive(frameHandle, true);
            MakePassive(controlHandle, false);
        }

        public long[] OwnHandles
        {
            get { return new long[] { frameHandle.ToInt64(), controlHandle.ToInt64() }; }
        }

        public bool Hidden
        {
            get { return !WindowTools.IsVisible(frameHandle.ToInt64()) && !WindowTools.IsVisible(controlHandle.ToInt64()); }
        }

        public void ShowControl()
        {
            OnUi(new Action(delegate
            {
                if (!control.IsVisible) control.Show();
                PlaceControl();
            }), true);
        }

        public void SetClock(string label, TimeSpan elapsed)
        {
            string text = label + "  " +
                ((int)elapsed.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" +
                elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
            OnUi(new Action(delegate { clock.Text = text; }), false);
        }

        public void FollowWindow(RectValue rect)
        {
            RectValue copy = rect;
            OnUi(new Action(delegate
            {
                frameRect = copy;
                frameWanted = copy != null && copy.Width > 0 && copy.Height > 0;
                ApplyFrame();
            }), false);
        }

        // Called around every picture, from the recording thread. While
        // suppressed the frame and the control are off the screen at the
        // operating system level, not merely marked hidden, because a layered
        // window can survive one frame after WPF stops drawing it.
        //
        // The caller waits for this. A picture taken while these were still up
        // would have this product's own frame in it, so "put them away" has to
        // have finished before the shutter, not merely have been asked for.
        public void Suppress(bool value)
        {
            bool wanted = value;
            OnUi(new Action(delegate
            {
                if (suppressed == wanted) return;
                suppressed = wanted;
                if (suppressed)
                {
                    frame.Hide();
                    control.Hide();
                    NativeMethods.ShowWindow(frameHandle, NativeMethods.SW_HIDE);
                    NativeMethods.ShowWindow(controlHandle, NativeMethods.SW_HIDE);
                }
                else
                {
                    control.Show();
                    PlaceControl();
                    ApplyFrame();
                }
            }), true);
        }

        // Every one of these windows belongs to the thread that made it, and the
        // recorder runs on its own. Touching them from anywhere else throws, and
        // a throw here would look like "the window could not be acquired" rather
        // than what it is.
        private void OnUi(Action work, bool wait)
        {
            System.Windows.Threading.Dispatcher dispatcher = control.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                work();
                return;
            }
            if (wait) dispatcher.Invoke(work);
            else dispatcher.BeginInvoke(work);
        }

        public bool ContainsPoint(int x, int y)
        {
            return Contains(frameHandle, x, y) || Contains(controlHandle, x, y);
        }

        public bool ControlContainsPoint(int x, int y)
        {
            return Contains(controlHandle, x, y);
        }

        public void Dispose()
        {
            try { frame.Close(); } catch { }
            try { control.Close(); } catch { }
        }

        private void ApplyFrame()
        {
            if (suppressed) return;
            if (!frameWanted || frameRect == null)
            {
                frame.Hide();
                NativeMethods.ShowWindow(frameHandle, NativeMethods.SW_HIDE);
                return;
            }
            if (!frame.IsVisible) frame.Show();
            NativeMethods.SetWindowPos(frameHandle, new IntPtr(-1), frameRect.X, frameRect.Y,
                Math.Max(1, frameRect.Width), Math.Max(1, frameRect.Height),
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private void PlaceControl()
        {
            if (suppressed) return;
            System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int width = 360;
            int height = 72;
            int x = work.Left + (work.Width - width) / 2;
            int y = work.Top + 12;
            NativeMethods.SetWindowPos(controlHandle, new IntPtr(-1), x, y, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private static Window CreatePassiveWindow()
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
            window.Left = -30000;
            window.Top = -30000;
            window.Width = 1;
            window.Height = 1;
            return window;
        }

        private static Window CreateControlWindow()
        {
            Window window = CreatePassiveWindow();
            // The stop control has to be clickable, so it is not made
            // click-through. It still refuses activation, so pressing stop does
            // not change which application is in front and does not appear in
            // the recording as an application switch.
            window.Focusable = false;
            return window;
        }

        private static bool Contains(IntPtr window, int x, int y)
        {
            RectValue rect = WindowTools.GetPhysicalRect(window);
            return rect != null && x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height;
        }

        private static void MakePassive(IntPtr window, bool clickThrough)
        {
            const int GWL_EXSTYLE = -20;
            const long WS_EX_TRANSPARENT = 0x00000020L;
            const long WS_EX_TOOLWINDOW = 0x00000080L;
            const long WS_EX_NOACTIVATE = 0x08000000L;
            long style = NativeMethods.GetWindowLongValue(window, GWL_EXSTYLE);
            long wanted = style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (clickThrough) wanted = wanted | WS_EX_TRANSPARENT;
            SetWindowLongValue(window, GWL_EXSTYLE, wanted);
        }

        private static void SetWindowLongValue(IntPtr window, int index, long value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(window, index, new IntPtr(value));
            else SetWindowLong(window, index, unchecked((int)value));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    }
}
