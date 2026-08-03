namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Shapes;

    public sealed class PickResult
    {
        public bool Cancelled;
        public TargetWindowInfo Window;
        public string Problem;
    }

    // The full screen chooser. The desktop is dimmed, the window under the
    // pointer is left bright inside a frame, a click takes it and Escape leaves
    // without taking anything.
    //
    // The pointer is never moved and nothing is sent to the window being
    // pointed at: the chooser only reads the window stack. It also never asks
    // the operating system what is under the pointer, because the chooser
    // itself is what is under the pointer; the stacking order of the other
    // windows is what answers the question.
    public sealed class WindowPicker : IDisposable
    {
        private readonly List<Window> surfaces = new List<Window>();
        private readonly List<IntPtr> handles = new List<IntPtr>();
        private readonly List<Canvas> canvases = new List<Canvas>();
        private readonly List<RectValue> bounds = new List<RectValue>();
        private readonly List<Rectangle> holes = new List<Rectangle>();
        private readonly List<Border> labels = new List<Border>();
        private readonly List<TextBlock> labelTexts = new List<TextBlock>();
        private readonly System.Windows.Threading.DispatcherTimer refresh;
        private readonly int ownProcessId;
        private TargetWindowInfo[] stack = new TargetWindowInfo[0];
        private TargetWindowInfo hovered;
        private PickResult result;
        private bool closed;

        public WindowPicker()
        {
            ownProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            refresh = new System.Windows.Threading.DispatcherTimer();
            refresh.Interval = TimeSpan.FromMilliseconds(400);
            refresh.Tick += delegate { ReloadStack(); };
        }

        // Runs the chooser to its end. Returns when the operator has taken a
        // window or left. Nothing is written and nothing is acquired here; the
        // caller decides what to do with the answer.
        public PickResult Pick(string hintText)
        {
            result = new PickResult();
            result.Cancelled = true;
            ReloadStack();
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            if (screens == null || screens.Length == 0)
            {
                result.Problem = "PICK-NOSCREEN: no display was reported by the system.";
                return result;
            }
            for (int index = 0; index < screens.Length; index++) Build(screens[index], hintText, index == 0);
            refresh.Start();
            UpdateHover();
            // A nested message loop keeps the caller written as one straight
            // line while the chooser is up.
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            EventHandler finished = null;
            finished = delegate
            {
                if (closed) frame.Continue = false;
            };
            System.Windows.Threading.DispatcherTimer pump = new System.Windows.Threading.DispatcherTimer();
            pump.Interval = TimeSpan.FromMilliseconds(30);
            pump.Tick += finished;
            pump.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            pump.Stop();
            refresh.Stop();
            CloseSurfaces();
            return result;
        }

        public void Dispose()
        {
            refresh.Stop();
            CloseSurfaces();
        }

        private void Build(System.Windows.Forms.Screen screen, string hintText, bool primary)
        {
            Window surface = new Window();
            surface.WindowStyle = WindowStyle.None;
            surface.ResizeMode = ResizeMode.NoResize;
            surface.AllowsTransparency = true;
            surface.Background = Brushes.Transparent;
            surface.ShowInTaskbar = false;
            surface.Topmost = true;
            surface.ShowActivated = primary;
            surface.Cursor = Cursors.Cross;
            surface.Left = -30000;
            surface.Top = -30000;
            surface.Width = 1;
            surface.Height = 1;

            Canvas canvas = new Canvas();
            canvas.Background = new SolidColorBrush(Color.FromArgb(96, 8, 12, 18));

            Rectangle hole = new Rectangle();
            hole.Fill = Brushes.Transparent;
            hole.Stroke = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            hole.StrokeThickness = 3;
            hole.Visibility = Visibility.Collapsed;
            canvas.Children.Add(hole);

            TextBlock labelText = new TextBlock();
            labelText.Foreground = Brushes.White;
            labelText.FontFamily = Theme.UiFont;
            labelText.FontSize = Theme.LabelSize;
            labelText.TextWrapping = TextWrapping.NoWrap;
            Border label = new Border();
            label.Background = new SolidColorBrush(Color.FromArgb(238, 16, 22, 30));
            label.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            label.BorderThickness = new Thickness(1);
            label.CornerRadius = new CornerRadius(Theme.RadiusSm);
            label.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            label.Child = labelText;
            label.Visibility = Visibility.Collapsed;
            canvas.Children.Add(label);

            if (primary && !String.IsNullOrEmpty(hintText))
            {
                TextBlock hint = new TextBlock();
                hint.Text = hintText;
                hint.Foreground = Brushes.White;
                hint.FontFamily = Theme.UiFont;
                hint.FontSize = Theme.SectionSize;
                Border hintBox = new Border();
                hintBox.Background = new SolidColorBrush(Color.FromArgb(238, 16, 22, 30));
                hintBox.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
                hintBox.BorderThickness = new Thickness(1);
                hintBox.CornerRadius = new CornerRadius(Theme.RadiusMd);
                hintBox.Padding = new Thickness(Theme.Space5, Theme.Space3, Theme.Space5, Theme.Space3);
                hintBox.Child = hint;
                canvas.Children.Add(hintBox);
                hintBox.Loaded += delegate
                {
                    Canvas.SetLeft(hintBox, Math.Max(0, (canvas.ActualWidth - hintBox.ActualWidth) / 2));
                    Canvas.SetTop(hintBox, Theme.Space7);
                };
            }

            surface.Content = canvas;
            surface.MouseMove += delegate { UpdateHover(); };
            surface.MouseLeftButtonDown += delegate { Take(); };
            surface.MouseRightButtonDown += delegate { Cancel(); };
            surface.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape) Cancel();
                else if (args.Key == Key.Enter || args.Key == Key.Space) Take();
            };

            IntPtr handle = new WindowInteropHelper(surface).EnsureHandle();
            surface.Show();
            System.Drawing.Rectangle area = screen.Bounds;
            NativeMethods.SetWindowPos(handle, new IntPtr(-1), area.Left, area.Top, area.Width, area.Height,
                NativeMethods.SWP_SHOWWINDOW);
            RectValue physical = new RectValue();
            physical.X = area.Left;
            physical.Y = area.Top;
            physical.Width = area.Width;
            physical.Height = area.Height;

            surfaces.Add(surface);
            handles.Add(handle);
            canvases.Add(canvas);
            bounds.Add(physical);
            holes.Add(hole);
            labels.Add(label);
            labelTexts.Add(labelText);
            if (primary)
            {
                surface.Activate();
                surface.Focus();
                Keyboard.Focus(surface);
            }
        }

        private void ReloadStack()
        {
            long[] excluded = handles.ToArray().Length == 0 ? new long[0] : ToLongs(handles);
            stack = WindowTools.ListStackOrder(excluded, ownProcessId);
        }

        private static long[] ToLongs(List<IntPtr> values)
        {
            long[] result = new long[values.Count];
            for (int index = 0; index < values.Count; index++) result[index] = values[index].ToInt64();
            return result;
        }

        private void UpdateHover()
        {
            PointValue cursor = WindowTools.CursorPosition();
            if (cursor == null) return;
            TargetWindowInfo found = WindowTools.WindowAt(stack, cursor.X, cursor.Y);
            hovered = found;
            for (int index = 0; index < surfaces.Count; index++) Paint(index, found, cursor);
        }

        // Physical screen pixels are what every acquisition layer speaks, and
        // WPF draws in its own units, so the conversion is measured from the
        // surface rather than assumed from a scaling factor.
        private void Paint(int index, TargetWindowInfo found, PointValue cursor)
        {
            Canvas canvas = canvases[index];
            RectValue area = bounds[index];
            Rectangle hole = holes[index];
            Border label = labels[index];
            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
            double scaleX = area.Width / canvas.ActualWidth;
            double scaleY = area.Height / canvas.ActualHeight;
            if (scaleX <= 0 || scaleY <= 0) return;

            if (found == null || found.Rect == null)
            {
                hole.Visibility = Visibility.Collapsed;
                label.Visibility = Visibility.Collapsed;
                return;
            }
            double left = (found.Rect.X - area.X) / scaleX;
            double top = (found.Rect.Y - area.Y) / scaleY;
            double width = found.Rect.Width / scaleX;
            double height = found.Rect.Height / scaleY;
            hole.Width = Math.Max(1, width);
            hole.Height = Math.Max(1, height);
            Canvas.SetLeft(hole, left);
            Canvas.SetTop(hole, top);
            hole.Visibility = Visibility.Visible;

            labelTexts[index].Text = Describe(found);
            label.Visibility = Visibility.Visible;
            label.UpdateLayout();
            double labelLeft = (cursor.X - area.X) / scaleX + 18;
            double labelTop = (cursor.Y - area.Y) / scaleY + 22;
            if (labelLeft + label.ActualWidth > canvas.ActualWidth) labelLeft = canvas.ActualWidth - label.ActualWidth - 4;
            if (labelTop + label.ActualHeight > canvas.ActualHeight) labelTop = canvas.ActualHeight - label.ActualHeight - 4;
            Canvas.SetLeft(label, Math.Max(0, labelLeft));
            Canvas.SetTop(label, Math.Max(0, labelTop));
        }

        private static string Describe(TargetWindowInfo window)
        {
            string app = String.IsNullOrEmpty(window.ProcessName) ? ("pid " + window.ProcessId) : window.ProcessName;
            string title = String.IsNullOrWhiteSpace(window.Title) ? "(" + window.ClassName + ")" : window.Title;
            string size = window.Rect == null ? "?" : window.Rect.Width + " x " + window.Rect.Height;
            return app + "   " + title + "   " + size;
        }

        private void Take()
        {
            // The stack is re-read at the moment of the click so a window that
            // moved between the last repaint and the click is not taken by its
            // old rectangle.
            ReloadStack();
            PointValue cursor = WindowTools.CursorPosition();
            TargetWindowInfo found = cursor == null ? hovered : WindowTools.WindowAt(stack, cursor.X, cursor.Y);
            if (found == null)
            {
                result.Cancelled = true;
                result.Problem = "PICK-NOWINDOW: there is no application window under the pointer at that spot.";
                closed = true;
                return;
            }
            result.Cancelled = false;
            result.Window = found;
            closed = true;
        }

        private void Cancel()
        {
            result.Cancelled = true;
            result.Window = null;
            closed = true;
        }

        private void CloseSurfaces()
        {
            for (int index = 0; index < surfaces.Count; index++)
            {
                try
                {
                    surfaces[index].Hide();
                    NativeMethods.ShowWindow(handles[index], NativeMethods.SW_HIDE);
                    surfaces[index].Close();
                }
                catch
                {
                    // Tear-down of this product's own surface. The answer the
                    // operator gave is already decided and nothing about the
                    // target is being discarded here, so there is nothing to
                    // report and nothing a caller could do with it.
                }
            }
            surfaces.Clear();
            handles.Clear();
            canvases.Clear();
            bounds.Clear();
            holes.Clear();
            labels.Clear();
            labelTexts.Clear();
        }
    }
}
