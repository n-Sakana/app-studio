using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class FixtureWinForms
{
    [STAThread]
    private static int Main(string[] args)
    {
        string readyPath = null;
        string commandPath = null;
        string donePath = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--ready" && index + 1 < args.Length) readyPath = args[++index];
            else if (args[index] == "--command" && index + 1 < args.Length) commandPath = args[++index];
            else if (args[index] == "--done" && index + 1 < args.Length) donePath = args[++index];
        }
        if (String.IsNullOrEmpty(readyPath)) throw new ArgumentException("--ready is required.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FixtureForm(readyPath, commandPath, donePath));
        return 0;
    }

    private sealed class FixtureForm : Form
    {
        private readonly string readyPath;
        private readonly TextBox normal;
        private readonly TextBox password;
        private readonly string commandPath;
        private readonly string donePath;
        private readonly ToolStripMenuItem fileMenu;
        private readonly Timer commandTimer;
        private readonly Button firstButton;
        private readonly Button noEffectButton;
        private readonly CheckBox toggleBox;
        private readonly ComboBox choiceBox;
        private readonly ListBox customerList;
        private string lastCommand;
        private int saveCount;

        internal FixtureForm(string path, string command, string done)
        {
            readyPath = path;
            commandPath = command;
            donePath = done;
            lastCommand = String.Empty;
            Text = "FixtureWinForms";
            Name = "FixtureWinForms";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(900, 120);
            Size = new Size(560, 500);
            TopMost = true;

            MenuStrip menu = new MenuStrip();
            fileMenu = new ToolStripMenuItem("File");
            fileMenu.DropDownItems.Add("Open");
            fileMenu.DropDownItems.Add("Close");
            menu.Items.Add(fileMenu);
            MainMenuStrip = menu;
            Controls.Add(menu);

            Label normalLabel = new Label();
            normalLabel.Text = "Customer code";
            normalLabel.Location = new Point(24, 24);
            normalLabel.AutoSize = true;
            Controls.Add(normalLabel);
            normal = new TextBox();
            normal.Name = "CustomerCode";
            normal.Text = "secret-value-42";
            normal.Location = new Point(160, 20);
            normal.Size = new Size(220, 24);
            Controls.Add(normal);

            Label passwordLabel = new Label();
            passwordLabel.Text = "Password";
            passwordLabel.Location = new Point(24, 64);
            passwordLabel.AutoSize = true;
            Controls.Add(passwordLabel);
            password = new TextBox();
            password.Name = "PasswordField";
            password.Text = "P@ssword123";
            password.UseSystemPasswordChar = true;
            password.Location = new Point(160, 60);
            password.Size = new Size(220, 24);
            Controls.Add(password);

            firstButton = new Button();
            firstButton.Name = "FirstSave";
            firstButton.Text = "Save";
            firstButton.Location = new Point(24, 108);
            firstButton.Click += delegate { saveCount++; firstButton.Text = "Saved " + saveCount.ToString(CultureInfo.InvariantCulture); };
            Controls.Add(firstButton);
            Button second = new Button();
            second.Name = "SecondSave";
            second.Text = "Save";
            second.Location = new Point(120, 108);
            Controls.Add(second);
            noEffectButton = new Button();
            noEffectButton.Name = "NoEffectButton";
            noEffectButton.Text = "No effect";
            noEffectButton.Location = new Point(216, 108);
            Controls.Add(noEffectButton);

            Label dynamicLabel = new Label();
            dynamicLabel.Name = "DynamicLabel";
            dynamicLabel.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            dynamicLabel.Location = new Point(24, 154);
            dynamicLabel.AutoSize = true;
            Controls.Add(dynamicLabel);

            Button disabled = new Button();
            disabled.Name = "DisabledButton";
            disabled.Text = "Disabled";
            disabled.Enabled = false;
            disabled.Location = new Point(24, 188);
            Controls.Add(disabled);
            toggleBox = new CheckBox();
            toggleBox.Name = "FeatureToggle";
            toggleBox.Text = "Feature";
            toggleBox.Location = new Point(128, 190);
            Controls.Add(toggleBox);
            choiceBox = new ComboBox();
            choiceBox.Name = "ChoiceBox";
            choiceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            choiceBox.Items.AddRange(new object[] { "One", "Two", "Three" });
            choiceBox.SelectedIndex = 0;
            choiceBox.Location = new Point(240, 188);
            choiceBox.Size = new Size(140, 24);
            Controls.Add(choiceBox);

            TabControl tabs = new TabControl();
            tabs.Name = "FixtureTabs";
            tabs.Location = new Point(24, 230);
            tabs.Size = new Size(480, 190);
            TabPage listPage = new TabPage("List");
            customerList = new ListBox();
            customerList.Name = "CustomerList";
            customerList.Dock = DockStyle.Fill;
            customerList.Items.AddRange(new object[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda", "Mu" });
            listPage.Controls.Add(customerList);
            TabPage treePage = new TabPage("Tree");
            TreeView tree = new TreeView();
            tree.Name = "CustomerTree";
            tree.Dock = DockStyle.Fill;
            tree.Nodes.Add("Root").Nodes.Add("Child");
            treePage.Controls.Add(tree);
            tabs.TabPages.Add(listPage);
            tabs.TabPages.Add(treePage);
            Controls.Add(tabs);
            Shown += OnShown;
            commandTimer = new Timer();
            commandTimer.Interval = 30;
            commandTimer.Tick += OnCommand;
            if (!String.IsNullOrEmpty(commandPath)) commandTimer.Start();
        }

        private void OnCommand(object sender, EventArgs args)
        {
            if (String.IsNullOrEmpty(commandPath) || !File.Exists(commandPath)) return;
            string command;
            try { command = File.ReadAllText(commandPath, Encoding.UTF8).Trim(); }
            catch (IOException) { return; }
            if (String.IsNullOrEmpty(command) || command == lastCommand) return;
            lastCommand = command;
            normal.Focus();
            fileMenu.ShowDropDown();
            Timer closeMenu = new Timer();
            closeMenu.Interval = 180;
            closeMenu.Tick += delegate
            {
                closeMenu.Stop();
                fileMenu.HideDropDown();
                Form popup = new Form();
                popup.Text = "Fixture popup";
                popup.Size = new Size(220, 120);
                popup.StartPosition = FormStartPosition.CenterParent;
                popup.Shown += delegate
                {
                    Timer closePopup = new Timer();
                    closePopup.Interval = 180;
                    closePopup.Tick += delegate
                    {
                        closePopup.Stop();
                        popup.Close();
                        password.Focus();
                        if (!String.IsNullOrEmpty(donePath)) File.WriteAllText(donePath, command, new UTF8Encoding(false));
                    };
                    closePopup.Start();
                };
                popup.Show(this);
            };
            closeMenu.Start();
        }

        private void OnShown(object sender, EventArgs args)
        {
            Rectangle normalRect = normal.RectangleToScreen(normal.ClientRectangle);
            Rectangle passwordRect = password.RectangleToScreen(password.ClientRectangle);
            Rectangle firstRect = firstButton.RectangleToScreen(firstButton.ClientRectangle);
            Rectangle noEffectRect = noEffectButton.RectangleToScreen(noEffectButton.ClientRectangle);
            Rectangle toggleRect = toggleBox.RectangleToScreen(toggleBox.ClientRectangle);
            Rectangle choiceRect = choiceBox.RectangleToScreen(choiceBox.ClientRectangle);
            Rectangle listRect = customerList.RectangleToScreen(customerList.ClientRectangle);
            string json = "{\"window\":" + Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"normal\":" + normal.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"password\":" + password.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"first\":" + firstButton.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"noEffect\":" + noEffectButton.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"toggle\":" + toggleBox.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"choice\":" + choiceBox.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"list\":" + customerList.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) +
                ",\"normalRect\":" + RectJson(normalRect) +
                ",\"passwordRect\":" + RectJson(passwordRect) +
                ",\"firstRect\":" + RectJson(firstRect) +
                ",\"noEffectRect\":" + RectJson(noEffectRect) +
                ",\"toggleRect\":" + RectJson(toggleRect) +
                ",\"choiceRect\":" + RectJson(choiceRect) +
                ",\"listRect\":" + RectJson(listRect) + "}";
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
