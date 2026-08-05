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
        private readonly Window glow;
        private readonly Window chip;
        private readonly TextBlock clock;
        private readonly TextBlock dot;
        private readonly TextBlock chipText;
        private readonly Button stop;
        private IntPtr frameHandle;
        private IntPtr controlHandle;
        private IntPtr glowHandle;
        private IntPtr chipHandle;
        private bool suppressed;
        private bool frameWanted;
        private RectValue frameRect;
        private bool inspectWanted;
        private RectValue inspectRect;

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

            // The inspector. Two more windows of this application, both of them
            // click-through: the operator is recording, and a ring drawn round
            // what they are about to press must not be the thing that gets
            // pressed. They are in OwnHandles like everything else here, so they
            // are excluded from the foreground tracking and taken off the screen
            // before any picture is made.
            glow = CreatePassiveWindow();
            Border outer = new Border();
            outer.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 58, 160, 255));
            outer.BorderThickness = new Thickness(4);
            outer.CornerRadius = new CornerRadius(3);
            outer.Background = Brushes.Transparent;
            Border inner = new Border();
            inner.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 58, 160, 255));
            inner.BorderThickness = new Thickness(2);
            inner.CornerRadius = new CornerRadius(2);
            inner.Background = new SolidColorBrush(Color.FromArgb(26, 58, 160, 255));
            outer.Child = inner;
            glow.Content = outer;

            chip = CreatePassiveWindow();
            chipText = new TextBlock();
            chipText.Foreground = Brushes.White;
            chipText.FontFamily = Theme.UiFont;
            chipText.FontSize = 12;
            chipText.TextWrapping = TextWrapping.NoWrap;
            Border chipShell = new Border();
            chipShell.Background = new SolidColorBrush(Color.FromArgb(226, 16, 22, 30));
            chipShell.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 58, 160, 255));
            chipShell.BorderThickness = new Thickness(1);
            chipShell.CornerRadius = new CornerRadius(Theme.RadiusSm);
            chipShell.Padding = new Thickness(Theme.Space3, Theme.Space1, Theme.Space3, Theme.Space1);
            chipShell.Child = chipText;
            chip.Content = chipShell;

            frameHandle = new WindowInteropHelper(frame).EnsureHandle();
            controlHandle = new WindowInteropHelper(control).EnsureHandle();
            glowHandle = new WindowInteropHelper(glow).EnsureHandle();
            chipHandle = new WindowInteropHelper(chip).EnsureHandle();
            MakePassive(frameHandle, true);
            MakePassive(controlHandle, false);
            MakePassive(glowHandle, true);
            MakePassive(chipHandle, true);
        }

        public long[] OwnHandles
        {
            get { return new long[] { frameHandle.ToInt64(), controlHandle.ToInt64(), glowHandle.ToInt64(), chipHandle.ToInt64() }; }
        }

        public bool Hidden
        {
            get
            {
                return !WindowTools.IsVisible(frameHandle.ToInt64()) &&
                    !WindowTools.IsVisible(controlHandle.ToInt64()) &&
                    !WindowTools.IsVisible(glowHandle.ToInt64()) &&
                    !WindowTools.IsVisible(chipHandle.ToInt64());
            }
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

        // What is under the pointer, or nothing. Called from the recording
        // thread on its own poll; a fact that could not be read is shown as one
        // rather than leaving the last one up as if it were still true.
        public void ShowInspect(InspectFact fact, int pointerX, int pointerY)
        {
            InspectFact copy = fact;
            int px = pointerX;
            int py = pointerY;
            OnUi(new Action(delegate
            {
                if (copy == null || !copy.Known)
                {
                    inspectWanted = false;
                    inspectRect = null;
                    ApplyInspect(px, py);
                    return;
                }
                inspectWanted = true;
                inspectRect = copy.Rect;
                chipText.Text = copy.Chip();
                ApplyInspect(px, py);
            }), false);
        }

        public void HideInspect()
        {
            OnUi(new Action(delegate
            {
                inspectWanted = false;
                inspectRect = null;
                ApplyInspect(0, 0);
            }), false);
        }

        private void ApplyInspect(int pointerX, int pointerY)
        {
            if (suppressed) return;
            if (!inspectWanted || inspectRect == null)
            {
                glow.Hide();
                chip.Hide();
                NativeMethods.ShowWindow(glowHandle, NativeMethods.SW_HIDE);
                NativeMethods.ShowWindow(chipHandle, NativeMethods.SW_HIDE);
                return;
            }
            if (!glow.IsVisible) glow.Show();
            if (!chip.IsVisible) chip.Show();
            NativeMethods.SetWindowPos(glowHandle, new IntPtr(-1), inspectRect.X, inspectRect.Y,
                Math.Max(1, inspectRect.Width), Math.Max(1, inspectRect.Height),
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            // The chip sits just below the pointer, and is nudged back onto
            // whichever display the pointer is on rather than being allowed to
            // run off the edge of it.
            chip.Measure(new Size(1200, 200));
            int width = (int)Math.Ceiling(chip.DesiredSize.Width) + 2;
            int height = (int)Math.Ceiling(chip.DesiredSize.Height) + 2;
            if (width < 40) width = 40;
            if (height < 18) height = 18;
            System.Drawing.Rectangle bounds =
                System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pointerX, pointerY)).WorkingArea;
            int x = pointerX + 18;
            int y = pointerY + 24;
            if (x + width > bounds.Right) x = bounds.Right - width;
            if (y + height > bounds.Bottom) y = pointerY - height - 12;
            if (x < bounds.Left) x = bounds.Left;
            if (y < bounds.Top) y = bounds.Top;
            NativeMethods.SetWindowPos(chipHandle, new IntPtr(-1), x, y, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
                    glow.Hide();
                    chip.Hide();
                    NativeMethods.ShowWindow(frameHandle, NativeMethods.SW_HIDE);
                    NativeMethods.ShowWindow(controlHandle, NativeMethods.SW_HIDE);
                    NativeMethods.ShowWindow(glowHandle, NativeMethods.SW_HIDE);
                    NativeMethods.ShowWindow(chipHandle, NativeMethods.SW_HIDE);
                }
                else
                {
                    control.Show();
                    PlaceControl();
                    ApplyFrame();
                    // The inspector does not come back on its own after a
                    // picture: it comes back on the next poll, with what is
                    // under the pointer then rather than what was under it
                    // before the shutter.
                    inspectWanted = false;
                    inspectRect = null;
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

        // Every window this made is closed, including the two the inspector
        // draws. Something left on top of somebody's desktop after a recording
        // has finished is not a small fault.
        public void Dispose()
        {
            try { frame.Close(); } catch { }
            try { control.Close(); } catch { }
            try { glow.Close(); } catch { }
            try { chip.Close(); } catch { }
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
