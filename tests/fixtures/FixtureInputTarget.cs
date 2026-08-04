using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// A window whose only job is to say what input actually reached it. It records
// that a click or a key arrived, never what was typed: the only thing written
// about a key is whether it matched the character the test asked for. Anything
// the operator happens to type while this window has focus leaves no trace.
internal static class FixtureInputTarget
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static int Main(string[] args)
    {
        string readyPath = null;
        string eventsPath = null;
        string expected = null;
        int left = 100;
        int top = 100;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--ready" && index + 1 < args.Length) readyPath = args[++index];
            else if (args[index] == "--events" && index + 1 < args.Length) eventsPath = args[++index];
            else if (args[index] == "--expect" && index + 1 < args.Length) expected = args[++index];
            else if (args[index] == "--left" && index + 1 < args.Length) left = int.Parse(args[++index], CultureInfo.InvariantCulture);
            else if (args[index] == "--top" && index + 1 < args.Length) top = int.Parse(args[++index], CultureInfo.InvariantCulture);
        }
        if (String.IsNullOrEmpty(readyPath)) throw new ArgumentException("--ready is required.");
        if (String.IsNullOrEmpty(eventsPath)) throw new ArgumentException("--events is required.");
        // Without this the operating system virtualises the coordinates this
        // window reports, and the recorded click position could not be compared
        // with the physical point the probe aimed at.
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TargetForm(readyPath, eventsPath, expected, left, top));
        return 0;
    }

    private sealed class TargetForm : Form
    {
        private readonly string readyPath;
        private readonly string eventsPath;
        private readonly string expected;
        private readonly Panel surface;
        private readonly TextBox entry;
        private readonly Label status;
        private int clickCount;
        private int keyCount;
        private int doubleCount;
        private int dragCount;
        private int wheelCount;
        private bool dragging;
        private bool travelled;
        private Point dragStart;

        internal TargetForm(string ready, string events, string expectedText, int left, int top)
        {
            readyPath = ready;
            eventsPath = events;
            expected = expectedText;
            Text = "FixtureInputTarget";
            Name = "FixtureInputTarget";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(left, top);
            Size = new Size(320, 190);
            TopMost = true;

            status = new Label();
            status.Text = "Input test target";
            status.SetBounds(10, 8, 290, 20);
            Controls.Add(status);

            // A plain panel exposes no invoke pattern, so the operation probe has
            // to fall through to a real mouse click to reach it.
            surface = new Panel();
            surface.Name = "ClickSurface";
            surface.SetBounds(10, 34, 290, 70);
            surface.BackColor = Color.FromArgb(219, 234, 254);
            surface.BorderStyle = BorderStyle.FixedSingle;
            surface.TabStop = true;
            surface.MouseDown += OnSurfaceMouseDown;
            surface.MouseDoubleClick += OnSurfaceDoubleClick;
            surface.MouseMove += OnSurfaceMouseMove;
            surface.MouseUp += OnSurfaceMouseUp;
            surface.MouseWheel += OnSurfaceWheel;
            Controls.Add(surface);

            entry = new TextBox();
            entry.Name = "KeyTarget";
            entry.SetBounds(10, 116, 290, 26);
            entry.KeyPress += OnKeyPress;
            Controls.Add(entry);

            Shown += delegate
            {
                entry.Focus();
                WriteReady();
            };
        }

        private void OnSurfaceMouseDown(object sender, MouseEventArgs args)
        {
            clickCount++;
            dragging = true;
            dragStart = new Point(args.X, args.Y);
            Point screen = surface.PointToScreen(new Point(args.X, args.Y));
            Append("{\"kind\":\"click\",\"button\":\"" + args.Button.ToString().ToLowerInvariant() +
                "\",\"screenX\":" + screen.X.ToString(CultureInfo.InvariantCulture) +
                ",\"screenY\":" + screen.Y.ToString(CultureInfo.InvariantCulture) +
                ",\"count\":" + clickCount.ToString(CultureInfo.InvariantCulture) + "}");
            Report();
        }

        // A second press inside the double click time, a press that travels
        // before it is released, and a turn of the wheel. Each one is written
        // down as having happened, with nothing about what it contained.
        private void OnSurfaceDoubleClick(object sender, MouseEventArgs args)
        {
            doubleCount++;
            Append("{\"kind\":\"doubleClick\",\"count\":" + doubleCount.ToString(CultureInfo.InvariantCulture) + "}");
            Report();
        }

        private void OnSurfaceMouseMove(object sender, MouseEventArgs args)
        {
            if (!dragging) return;
            if (Math.Max(Math.Abs(args.X - dragStart.X), Math.Abs(args.Y - dragStart.Y)) >= 8) travelled = true;
        }

        // Written at the release, not at the first movement, so the distance is
        // the distance of the whole drag rather than of its first step.
        private void OnSurfaceMouseUp(object sender, MouseEventArgs args)
        {
            bool wasDragging = dragging && travelled;
            dragging = false;
            travelled = false;
            if (!wasDragging) return;
            dragCount++;
            Append("{\"kind\":\"drag\",\"dx\":" + (args.X - dragStart.X).ToString(CultureInfo.InvariantCulture) +
                ",\"dy\":" + (args.Y - dragStart.Y).ToString(CultureInfo.InvariantCulture) +
                ",\"count\":" + dragCount.ToString(CultureInfo.InvariantCulture) + "}");
            Report();
        }

        private void OnSurfaceWheel(object sender, MouseEventArgs args)
        {
            wheelCount++;
            Append("{\"kind\":\"wheel\",\"delta\":" + args.Delta.ToString(CultureInfo.InvariantCulture) +
                ",\"count\":" + wheelCount.ToString(CultureInfo.InvariantCulture) + "}");
            Report();
        }

        private void Report()
        {
            status.Text = "clicks " + clickCount + " / keys " + keyCount + " / double " + doubleCount +
                " / drag " + dragCount + " / wheel " + wheelCount;
        }

        private void OnKeyPress(object sender, KeyPressEventArgs args)
        {
            keyCount++;
            bool matched = !String.IsNullOrEmpty(expected) && expected.Length > 0 && args.KeyChar == expected[0];
            // The character itself is deliberately not written anywhere.
            Append("{\"kind\":\"key\",\"matchedExpected\":" + (matched ? "true" : "false") +
                ",\"count\":" + keyCount.ToString(CultureInfo.InvariantCulture) + "}");
            Report();
            args.Handled = true;
            entry.Clear();
        }

        private void Append(string line)
        {
            try
            {
                File.AppendAllText(eventsPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private void WriteReady()
        {
            Rectangle surfaceRect = surface.RectangleToScreen(surface.ClientRectangle);
            Rectangle entryRect = entry.RectangleToScreen(entry.ClientRectangle);
            string json = "{\"window\":" + Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"surface\":" + surface.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"entry\":" + entry.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"surfaceRect\":" + RectJson(surfaceRect) +
                ",\"entryRect\":" + RectJson(entryRect) + "}";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(readyPath)));
            File.WriteAllText(readyPath, json, new UTF8Encoding(false));
        }

        private static string RectJson(Rectangle rectangle)
        {
            return "{\"left\":" + rectangle.Left.ToString(CultureInfo.InvariantCulture) +
                ",\"top\":" + rectangle.Top.ToString(CultureInfo.InvariantCulture) +
                ",\"right\":" + rectangle.Right.ToString(CultureInfo.InvariantCulture) +
                ",\"bottom\":" + rectangle.Bottom.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }
}
