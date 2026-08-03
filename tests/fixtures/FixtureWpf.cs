using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppStudio.WpsFixtures
{
    internal static class WpfProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Dictionary<string, string> options = ParseArgs(args);
            string kind = Get(options, "kind", "healthy");
            string hangMode = Get(options, "hang-mode", "permanent");
            string runDir = Require(options, "run-dir");
            string prefix = Require(options, "prefix");
            int temporarySeconds = Int32.Parse(Get(options, "temporary-seconds", "15"), CultureInfo.InvariantCulture);
            int left = Int32.Parse(Get(options, "left", kind == "healthy" ? "80" : "520"), CultureInfo.InvariantCulture);

            Directory.CreateDirectory(runDir);
            Application app = new Application();
            app.Run(new FixtureWindow(kind, hangMode, runDir, prefix, temporarySeconds, left));
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException("Expected --name value arguments.");
                }
                values[args[i].Substring(2)] = args[++i];
            }
            return values;
        }

        private static string Get(Dictionary<string, string> values, string name, string fallback)
        {
            string value;
            return values.TryGetValue(name, out value) ? value : fallback;
        }

        private static string Require(Dictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || String.IsNullOrEmpty(value))
            {
                throw new ArgumentException("Missing --" + name + ".");
            }
            return value;
        }
    }

    internal sealed class FixtureWindow : Window
    {
        private readonly string kind;
        private readonly string hangMode;
        private readonly string runDir;
        private readonly string prefix;
        private readonly int temporarySeconds;
        private readonly TextBox target;
        private readonly DispatcherTimer triggerTimer;
        private string lastToken;

        internal FixtureWindow(string kindValue, string hangModeValue, string runDirValue, string prefixValue, int temporarySecondsValue, int left)
        {
            kind = kindValue;
            hangMode = hangModeValue;
            runDir = runDirValue;
            prefix = prefixValue;
            temporarySeconds = temporarySecondsValue;
            lastToken = String.Empty;

            Title = "PUI WP-S " + kind + " fixture";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = 80;
            Width = 360;
            Height = 190;
            Topmost = true;
            ShowInTaskbar = true;

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(24);

            TextBlock label = new TextBlock();
            label.Text = "UI Automation target";
            label.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(label);

            target = new TextBox();
            target.Name = "TargetText";
            target.Text = "healthy-value";
            target.Height = 30;
            AutomationProperties.SetName(target, "WP-S target text");
            AutomationProperties.SetAutomationId(target, "TargetText");
            panel.Children.Add(target);

            TextBlock status = new TextBlock();
            status.Text = kind == "hang" ? "Awaiting hang trigger" : "Responsive";
            status.Margin = new Thickness(0, 18, 0, 0);
            panel.Children.Add(status);
            Content = panel;

            triggerTimer = new DispatcherTimer(DispatcherPriority.Normal);
            triggerTimer.Interval = TimeSpan.FromMilliseconds(25);
            triggerTimer.Tick += OnTriggerTick;
            ContentRendered += OnContentRendered;
        }

        private string PathFor(string suffix)
        {
            return Path.Combine(runDir, prefix + "." + suffix);
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            System.Windows.Point center = target.PointToScreen(new System.Windows.Point(target.ActualWidth / 2, target.ActualHeight / 2));
            System.Windows.Interop.WindowInteropHelper helper = new System.Windows.Interop.WindowInteropHelper(this);
            string[] lines = new string[]
            {
                "pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                "window=" + helper.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                "target=" + helper.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                "x=" + ((int)Math.Round(center.X)).ToString(CultureInfo.InvariantCulture),
                "y=" + ((int)Math.Round(center.Y)).ToString(CultureInfo.InvariantCulture)
            };
            WriteAtomic(PathFor("ready"), String.Join(Environment.NewLine, lines));
            if (kind == "hang")
            {
                triggerTimer.Start();
            }
        }

        private void OnTriggerTick(object sender, EventArgs e)
        {
            string triggerPath = PathFor("trigger");
            if (!File.Exists(triggerPath))
            {
                return;
            }

            string token;
            try
            {
                token = File.ReadAllText(triggerPath, Encoding.ASCII).Trim();
            }
            catch (IOException)
            {
                return;
            }

            if (String.IsNullOrEmpty(token) || String.Equals(token, lastToken, StringComparison.Ordinal))
            {
                return;
            }

            lastToken = token;
            WriteAtomic(PathFor("hung"), token);
            if (String.Equals(hangMode, "temporary", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(temporarySeconds * 1000);
            }
            else
            {
                string releasePath = PathFor("release");
                while (!File.Exists(releasePath))
                {
                    Thread.Sleep(25);
                }
            }
            WriteAtomic(PathFor("recovered"), token);
        }

        private static void WriteAtomic(string path, string value)
        {
            int attempt;
            for (attempt = 0; attempt < 20; attempt++)
            {
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, value, new UTF8Encoding(false));
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(temp, path);
                    return;
                }
                catch (IOException)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); }
                    catch { }
                    Thread.Sleep(5);
                }
            }
            throw new IOException("Could not replace signal file " + path + ".");
        }
    }
}
