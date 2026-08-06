namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Interop;
    using System.Windows.Media;

    // What is on screen while a recording runs: a restrained frame around the
    // window in front, and a small panel saying that recording is happening,
    // with the controls a person actually reaches for while it runs.
    //
    // Small is not the same as cramped. Everything here is measured in the units
    // WPF lays out in and then placed in the units Windows positions windows in,
    // with the scale between them applied. Placing a panel by writing "360 by
    // 72" straight into SetWindowPos is how, on a 125 per cent display, a stop
    // control ended up with its right edge and its lower half outside the window
    // that was supposed to contain it.
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
        private readonly TextBlock chipHead;
        private readonly TextBlock chipDetail;
        private readonly TextBlock chipHint;
        private readonly Button stop;
        private readonly System.Windows.Controls.Primitives.ToggleButton pause;
        private string recordingWord;
        private IntPtr frameHandle;
        private IntPtr controlHandle;
        private IntPtr glowHandle;
        private IntPtr chipHandle;
        private bool suppressed;
        private bool frameWanted;
        private RectValue frameRect;
        private bool inspectWanted;
        private RectValue inspectRect;
        private InspectFact lastFact;

        public event Action StopRequested;
        // Stopping and pausing are the two things that are about this panel's own
        // job, so they are the only two on it.
        //
        // The hover reader used to have a switch here as well. It is a recording
        // setting, and it already has a switch in the recording settings, so this
        // was the same setting in two places - and the two disagreed, because
        // turning it off in the dialog left this one still reading "on". One
        // setting, one switch, in the place where settings are.
        public event Action<bool> PauseRequested;

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

            pause = HudToggle(Messages.Text("hud-pause.txt", "Pause"));
            pause.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            pause.Checked += delegate { PaintPause(); Raise(PauseRequested, true); };
            pause.Unchecked += delegate { PaintPause(); Raise(PauseRequested, false); };
            recordingWord = recordingLabel;

            // One row, and it is allowed to be exactly as wide as it needs to be.
            // The clock text grows during a replay - "Replaying 12/40" is wider
            // than "Replaying" - and the panel used to be sized once, at the start,
            // and never again. Everything after the clock therefore moved right
            // as the count grew, and the pause control was the last thing on the
            // row, so it was the thing that ended up outside the window. It is
            // the row that is measured now, on every change of what is in it.
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(dot);
            row.Children.Add(clock);
            row.Children.Add(stop);
            row.Children.Add(pause);

            StackPanel stack = new StackPanel();
            stack.Children.Add(row);

            Border shell = new Border();
            // Opaque. At ninety five per cent whatever is behind the panel reads
            // straight through the words on it, which is exactly the effect a
            // recording indicator must not have.
            shell.Background = new SolidColorBrush(Theme.Parse("#101620"));
            shell.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            shell.BorderThickness = new Thickness(1);
            shell.CornerRadius = new CornerRadius(Theme.RadiusMd);
            shell.Padding = new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space4);
            shell.Child = stack;
            control.Content = shell;
            PaintPause();

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

            // The chip is three lines at most: what the thing is and what it is
            // called, then - only when asked for - the properties that name it
            // to a program, then a line saying those properties exist. It is
            // click-through, so it cannot carry its own control; the panel's
            // Details toggle is what opens it, and the hint says so.
            chip = CreatePassiveWindow();
            chipHead = new TextBlock();
            chipHead.Foreground = Brushes.White;
            chipHead.FontFamily = Theme.UiFont;
            chipHead.FontSize = 13;
            chipHead.FontWeight = FontWeights.SemiBold;
            chipHead.TextWrapping = TextWrapping.NoWrap;
            chipDetail = new TextBlock();
            chipDetail.Foreground = new SolidColorBrush(Theme.Parse("#B9C6D4"));
            chipDetail.FontFamily = Theme.UiFont;
            chipDetail.FontSize = 12;
            chipDetail.TextWrapping = TextWrapping.NoWrap;
            chipDetail.Margin = new Thickness(0, Theme.Space1, 0, 0);
            chipDetail.Visibility = Visibility.Collapsed;
            chipHint = new TextBlock();
            chipHint.Text = Messages.Text("chip-more.txt", "more under Details");
            chipHint.Foreground = new SolidColorBrush(Theme.Parse("#7F94AA"));
            chipHint.FontFamily = Theme.UiFont;
            chipHint.FontSize = 11;
            chipHint.TextWrapping = TextWrapping.NoWrap;
            chipHint.Margin = new Thickness(0, Theme.Space1, 0, 0);
            StackPanel chipStack = new StackPanel();
            chipStack.Children.Add(chipHead);
            chipStack.Children.Add(chipDetail);
            chipStack.Children.Add(chipHint);
            Border chipShell = new Border();
            chipShell.Background = new SolidColorBrush(Theme.Parse("#101620"));
            chipShell.BorderBrush = new SolidColorBrush(Color.FromArgb(200, 58, 160, 255));
            chipShell.BorderThickness = new Thickness(1);
            chipShell.CornerRadius = new CornerRadius(Theme.RadiusSm);
            chipShell.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            chipShell.Child = chipStack;
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

        // The clock says which of the two states the recording is in, not only
        // how long it has been running. "Recording 02:41" over a paused
        // recording is the panel telling the operator something untrue.
        public void SetClock(string label, TimeSpan elapsed)
        {
            string stamp = "  " +
                ((int)elapsed.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" +
                elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
            string running = label;
            OnUi(new Action(delegate
            {
                bool held = pause.IsChecked == true;
                string wanted = (held ? Messages.Text("hud-paused.txt", "Paused") : running) + stamp;
                if (String.Equals(clock.Text, wanted, StringComparison.Ordinal)) return;
                clock.Text = wanted;
                clock.Foreground = held ? new SolidColorBrush(Theme.Parse("#E4C179")) : Brushes.White;
                // The panel is only as wide as its words, and this is where the
                // words change. Without this the window keeps the width it needed
                // for "Replaying 1/40" and clips everything the wider "Replaying 12/40"
                // pushes past that edge - which is the pause control, because it
                // is last on the row.
                Reflow();
            }), false);
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
                    lastFact = null;
                    ApplyInspect(px, py);
                    return;
                }
                inspectWanted = true;
                inspectRect = copy.Rect;
                lastFact = copy;
                PaintChip();
                ApplyInspect(px, py);
            }), false);
        }

        // The panel says which of the two states each switch is in, rather than
        // what pressing it will do. "Pointer info: on" is readable at a glance;
        // a button labelled "turn pointer info off" makes the reader work out
        // the current state from the offer.
        private void PaintPause()
        {
            bool held = pause.IsChecked == true;
            pause.Content = held
                ? Messages.Text("hud-resume.txt", "Resume")
                : Messages.Text("hud-pause.txt", "Pause");
            dot.Opacity = held ? 0.35 : 1.0;
            if (recordingWord == null) return;
            // The clock only redraws on the next tick, which is up to half a
            // second away. The word has to change with the press.
            string stamp = clock.Text;
            int at = stamp.LastIndexOf("  ", StringComparison.Ordinal);
            if (at > 0)
            {
                clock.Text = (held ? Messages.Text("hud-paused.txt", "Paused") : recordingWord) + stamp.Substring(at);
                clock.Foreground = held ? new SolidColorBrush(Theme.Parse("#E4C179")) : Brushes.White;
            }
            Reflow();
        }

        // The panel is only as wide as its words, and its words change: "paused"
        // is longer than "recording" in more than one language. Placed once at
        // the start, the panel keeps the width it needed then and clips whatever
        // it says afterwards, which is the fault this whole panel was rebuilt to
        // stop. The size is recomputed whenever the content changes, and the
        // window is only moved when the answer is actually different.
        private void Reflow()
        {
            if (suppressed || !control.IsVisible) return;
            Size wanted = Wanted();
            RectValue now = WindowTools.GetPhysicalRect(controlHandle);
            if (now != null && now.Width == (int)wanted.Width && now.Height == (int)wanted.Height) return;
            PlaceControl();
        }

        // What the panel needs, in the units the window manager places windows
        // in. WPF caches a measure against the constraint it was given, so
        // measuring again with the same constraint returns the previous answer
        // and the panel keeps a stale width - which is exactly the fault this is
        // here to stop. The measure is invalidated first, every time.
        private Size Wanted()
        {
            double scale = Scale(controlHandle);
            FrameworkElement content = control.Content as FrameworkElement;
            if (content != null) content.InvalidateMeasure();
            control.InvalidateMeasure();
            control.Measure(new Size(2400, 400));
            control.UpdateLayout();
            double needWidth = Math.Max(control.DesiredSize.Width, content == null ? 0 : content.DesiredSize.Width);
            double needHeight = Math.Max(control.DesiredSize.Height, content == null ? 0 : content.DesiredSize.Height);
            // A few units of slack. A window sized to exactly what a layout asked
            // for puts the last control flush against the frame, and one rounding
            // step in the other direction slices its border off.
            int width = (int)Math.Ceiling(needWidth * scale) + 6;
            int height = (int)Math.Ceiling(needHeight * scale) + 6;
            return new Size(width, height);
        }

        // Three short lines: what it is, which window it is in, and what names
        // it. All of it, always. This is a chip about one thing under the
        // pointer - there is no volume of text here to protect anybody from.
        private void PaintChip()
        {
            if (lastFact == null) return;
            chipHead.Text = lastFact.Headline();
            string window = lastFact.WindowLine();
            chipDetail.Text = window;
            chipDetail.Visibility = window.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            string more = lastFact.Detail();
            chipHint.Text = more;
            chipHint.Visibility = more.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void Raise(Action<bool> handler, bool value)
        {
            if (handler != null) handler(value);
        }

        private static System.Windows.Controls.Primitives.ToggleButton HudToggle(string label)
        {
            System.Windows.Controls.Primitives.ToggleButton button = new System.Windows.Controls.Primitives.ToggleButton();
            button.Content = label;
            button.FontFamily = Theme.UiFont;
            button.FontSize = 12;
            button.FontWeight = FontWeights.SemiBold;
            button.Height = 30;
            button.MinWidth = 96;
            button.Padding = new Thickness(Theme.Space3, 0, Theme.Space3, 0);
            button.Foreground = Brushes.White;
            button.Background = new SolidColorBrush(Color.FromArgb(70, 120, 160, 200));
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 120, 170, 220));
            button.BorderThickness = new Thickness(1);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Cursor = System.Windows.Input.Cursors.Hand;
            return button;
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
            //
            // Measure gives what the text needs in layout units; SetWindowPos
            // wants device pixels. Handing the first number to the second is why
            // the chip used to cut its own last word off on any display that is
            // not at 100 per cent.
            double scale = Scale(chipHandle);
            chip.Measure(new Size(1600, 400));
            int width = (int)Math.Ceiling(chip.DesiredSize.Width * scale) + 2;
            int height = (int)Math.Ceiling(chip.DesiredSize.Height * scale) + 2;
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

            // The ring and the panel are both topmost, and among topmost windows
            // the last one placed wins. The ring is placed every time the
            // pointer moves, so without this it ends up drawn across the panel -
            // a blue band through the word "recording" and through the stop
            // control. The panel is put back on top after the ring moves.
            NativeMethods.SetWindowPos(controlHandle, new IntPtr(-1), 0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
        }

        // Layout units to device pixels for this window. WPF reports it on the
        // presentation source; a window that has not been shown yet has none, so
        // the desktop's own ratio is used until it has.
        private static double Scale(IntPtr window)
        {
            System.Windows.Interop.HwndSource source = System.Windows.Interop.HwndSource.FromHwnd(window);
            if (source != null && source.CompositionTarget != null)
            {
                double m11 = source.CompositionTarget.TransformToDevice.M11;
                if (m11 > 0) return m11;
            }
            return DpiAwareness.Scale();
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
            // Same reason as the ring: the frame is placed on every change of
            // foreground window, and a window whose top edge crosses the panel
            // would otherwise draw a line through it.
            if (control.IsVisible)
            {
                NativeMethods.SetWindowPos(controlHandle, new IntPtr(-1), 0, 0, 0, 0,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
            }
        }

        // The panel is exactly the size of what is in it, whatever the display
        // is scaled to. Nothing here is a number somebody measured once on one
        // monitor: the content is asked how much room it needs, and that answer
        // is converted into the units the window manager positions windows in.
        private void PlaceControl()
        {
            if (suppressed) return;
            Size wanted = Wanted();
            int width = (int)wanted.Width;
            int height = (int)wanted.Height;
            System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            if (width > work.Width) width = work.Width;
            if (height > work.Height) height = work.Height;
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
