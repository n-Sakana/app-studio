using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Three ordinary single line text boxes, one after another in tab order. It
// exists so Japanese input through the operating system's own IME can be
// driven at a window that holds what was committed and nothing else: no
// password box, no control that clears itself, no field whose value has to be
// interpreted.
//
// The first box takes the keyboard as soon as the window opens, which is the
// case a recording has to survive without anybody clicking anything.
internal static class FixtureIme
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [STAThread]
    private static int Main(string[] args)
    {
        string readyPath = null;
        int left = 120;
        int top = 120;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--ready" && index + 1 < args.Length) readyPath = args[++index];
            else if (args[index] == "--left" && index + 1 < args.Length) left = int.Parse(args[++index], CultureInfo.InvariantCulture);
            else if (args[index] == "--top" && index + 1 < args.Length) top = int.Parse(args[++index], CultureInfo.InvariantCulture);
        }
        if (String.IsNullOrEmpty(readyPath)) throw new ArgumentException("--ready is required.");
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ImeForm(readyPath, left, top));
        return 0;
    }

    private sealed class ImeForm : Form
    {
        private readonly string readyPath;
        private readonly TextBox first;
        private readonly TextBox second;
        private readonly TextBox third;

        internal ImeForm(string ready, int left, int top)
        {
            readyPath = ready;
            Text = "FixtureIme";
            Name = "FixtureIme";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(left, top);
            Size = new Size(560, 320);
            TopMost = true;

            first = Field("Field1", "First", 24);
            second = Field("Field2", "Second", 84);
            third = Field("Field3", "Third", 144);

            Shown += delegate
            {
                // The keyboard starts here and nothing clicks on it.
                first.Focus();
                WriteReady();
            };
        }

        private TextBox Field(string name, string caption, int y)
        {
            Label label = new Label();
            label.Text = caption;
            label.Location = new Point(24, y + 4);
            label.AutoSize = true;
            Controls.Add(label);
            TextBox box = new TextBox();
            box.Name = name;
            box.Location = new Point(140, y);
            box.Size = new Size(360, 28);
            box.Font = new Font("Yu Gothic UI", 12F);
            Controls.Add(box);
            return box;
        }

        private void WriteReady()
        {
            string json = "{\"window\":" + Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"first\":" + first.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"second\":" + second.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"third\":" + third.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"firstRect\":" + RectJson(first.RectangleToScreen(first.ClientRectangle)) +
                ",\"secondRect\":" + RectJson(second.RectangleToScreen(second.ClientRectangle)) +
                ",\"thirdRect\":" + RectJson(third.RectangleToScreen(third.ClientRectangle)) + "}";
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
