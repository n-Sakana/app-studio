namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Text;
    using System.Windows.Threading;

    // One window, one working state, two ways of looking at it.
    //
    // What this product does has not changed: take a snap of a window, record
    // what a person does across applications, play that back, and write the
    // recording out as code that can be edited and handed over. What has changed
    // is that those are no longer four screens with a way between them. There is
    // a session, and there are two shapes:
    //
    //   full - a fixed bar over three panes: the modules, the file, and the
    //          workflow and the assistant sharing the right hand side.
    //   mini - the same session folded down to the controls a person needs while
    //          they are recording something, because during a recording this
    //          window is in the way of the thing being recorded.
    //
    // Both hold the same session, the same chosen module and the same unsaved
    // edit, so moving between them is a change of shape and never a change of
    // state. Nothing but the operator changes which one is on screen: starting a
    // recording used to switch the window to the result screen by itself, which
    // took the operator somewhere they had not asked to go and threw away the
    // shape they had chosen.
    public sealed class StudioWindow : Window
    {
        private const string ModeFull = "full";
        private const string ModeMini = "mini";

        private readonly string baseDir;
        private readonly JsonObject diagnostics;
        private readonly DispatcherTimer clock;

        private readonly ComboBox sessionPicker = new ComboBox();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock healthLabel = new TextBlock();
        private readonly Border healthBadge;
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Workspace workspace;
        private readonly StackPanel miniWorkflow = new StackPanel();

        private readonly List<StudioSession> sessions = new List<StudioSession>();
        private StudioSession current;
        private CodeProject currentProject;
        private HotkeyManager hotkeys;

        private RecordHud hud;
        private Recorder recorder;
        private ReplayEngine replay;
        private DateTime busySince;
        private bool busy;
        private bool loadingSessions;
        private bool miniListOpen = true;

        private string mode = ModeFull;
        private string hotkeyNotice;
        private bool writeEnabled;
        private string routeMode = ProbeRoutes.Auto;
        private string valuePolicy = Privacy.PolicyRecordText;
        private int pdfBudgetKb = ScreensPdf.DefaultBudgetBytes / 1024;
        private bool inspectEnabled;
        // How fast a replay runs relative to the recording's own pace. Held on
        // the window so that the slider in the workflow pane, the one on the
        // small bar and a run already under way are all the same number.
        private double replaySpeed = 1.0;
        private System.Windows.Controls.Primitives.ToggleButton inspectSwitch;
        private System.Windows.Controls.Primitives.ToggleButton writeSwitch;

        public StudioWindow(string directory, JsonObject startupDiagnostics, int autoCloseMs)
        {
            baseDir = directory;
            diagnostics = startupDiagnostics;
            Messages.Init(baseDir);
            Theme.Init(baseDir);
            VbaWorkbook.Init(baseDir);
            Theme.Install(Resources);

            Title = Text("app-title.txt", "App Studio");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.SurfaceCanvas;
            FontFamily = Theme.UiFont;
            FontSize = Theme.BodySize;

            healthLabel.VerticalAlignment = VerticalAlignment.Center;
            healthBadge = Badge(healthLabel, "-", "Accent");
            progress.Height = Theme.ProgressTrackHeight;
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.IsIndeterminate = false;
            progress.Value = 0;
            progress.Foreground = Theme.Accent;
            progress.Background = Theme.SurfaceSunken;
            progress.BorderThickness = new Thickness(0);

            workspace = new Workspace(this, Say);
            workspace.AskRunConsent = AskRunConsent;
            workspace.StartReplay = StartReplay;
            workspace.OpenReport = OpenReport;
            workspace.PdfBudgetBytes = delegate { return pdfBudgetKb * 1024; };
            workspace.ReplaySpeed = delegate { return replaySpeed; };
            workspace.SetReplaySpeed = delegate(double value) { replaySpeed = value; };

            mode = ReadMode();
            ApplyMode(mode);

            Loaded += OnLoaded;
            Closed += OnClosed;

            clock = new DispatcherTimer();
            clock.Interval = TimeSpan.FromMilliseconds(500);
            clock.Tick += OnClock;
            clock.Start();

            if (autoCloseMs > 0)
            {
                DispatcherTimer autoClose = new DispatcherTimer();
                autoClose.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
                autoClose.Tick += delegate { autoClose.Stop(); Close(); };
                autoClose.Start();
            }
        }

        // ---------- which shape, and remembering it ----------

        private string ModePath()
        {
            return Path.Combine(baseDir, "runtime", "settings", "view.txt");
        }

        // The shape the operator last chose. A window that opens in whichever
        // shape the product feels like is a window that has to be rearranged
        // before every use.
        private string ReadMode()
        {
            try
            {
                string path = ModePath();
                if (!File.Exists(path)) return ModeFull;
                string value = File.ReadAllText(path).Trim();
                return String.Equals(value, ModeMini, StringComparison.OrdinalIgnoreCase) ? ModeMini : ModeFull;
            }
            catch (IOException) { return ModeFull; }
            catch (UnauthorizedAccessException) { return ModeFull; }
        }

        private void WriteMode()
        {
            try
            {
                string path = ModePath();
                string folder = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(path, mode);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Orphan(UIElement child)
        {
            if (child == null) return;
            DependencyObject parent = System.Windows.LogicalTreeHelper.GetParent(child);
            Panel panel = parent as Panel;
            if (panel != null) { panel.Children.Remove(child); return; }
            Decorator decorator = parent as Decorator;
            if (decorator != null) { decorator.Child = null; return; }
            ContentControl holder = parent as ContentControl;
            if (holder != null) holder.Content = null;
        }

        private void DetachShared()
        {
            Orphan(progress);
            Orphan(status);
            Orphan(healthBadge);
            Orphan(sessionPicker);
            Orphan(miniWorkflow);
        }

        private void ApplyMode(string next)
        {
            // Whatever is in the editor is written before the shape changes.
            // There is no save button here, so this is the moment it happens.
            workspace.Persist();
            mode = next;
            DetachShared();
            bool mini = String.Equals(mode, ModeMini, StringComparison.Ordinal);
            Size room = WorkAreaDip();
            MinWidth = Math.Min(mini ? 620 : 1040, room.Width);
            MinHeight = Math.Min(mini ? Theme.MiniBarHeight + 48 : 620, room.Height);
            if (mini)
            {
                Width = Math.Min(Theme.MiniWidth, room.Width);
                Height = Math.Min(miniListOpen ? Theme.MiniBarHeight + Theme.MiniListHeight : Theme.MiniBarHeight + 44, room.Height);
                SizeToContent = SizeToContent.Height;
            }
            else
            {
                SizeToContent = SizeToContent.Manual;
                Width = Math.Min(1440, room.Width);
                Height = Math.Min(900, room.Height);
            }
            ResizeMode = ResizeMode.CanResize;
            Background = Theme.SurfaceCanvas;
            Content = mini ? BuildMini() : BuildFull();
            WriteMode();
        }

        private void GoFull()
        {
            if (String.Equals(mode, ModeFull, StringComparison.Ordinal)) return;
            ApplyMode(ModeFull);
            CentreOnScreen();
        }

        private void GoMini()
        {
            if (String.Equals(mode, ModeMini, StringComparison.Ordinal)) return;
            ApplyMode(ModeMini);
            CentreOnScreen();
        }

        // How many device pixels one layout unit is worth on this display.
        //
        // The first time this is asked, the window has not been shown yet - the
        // shape is decided in the constructor - so WPF has no presentation source
        // to answer from. Returning 1.0 there is not a neutral guess: on a 125
        // per cent display it makes every size a quarter larger than it was meant
        // to be, so a window asked to be 900 units tall came out 1125 pixels tall
        // on a work area of 1032, and the status line spent its life behind the
        // task bar. The desktop's own ratio is the right answer until the window
        // has one of its own.
        private double DeviceScale()
        {
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                double m11 = source.CompositionTarget.TransformToDevice.M11;
                if (m11 > 0) return m11;
            }
            double desktop = DpiAwareness.Scale();
            return desktop > 0 ? desktop : 1.0;
        }

        private Size WorkAreaDip()
        {
            System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double scale = DeviceScale();
            double width = work.Width / scale - Theme.Space3;
            double height = work.Height / scale - Theme.Space3;
            if (width < 480) width = 480;
            if (height < 400) height = 400;
            return new Size(width, height);
        }

        // Brings the window inside the desktop it is actually on.
        //
        // A window taller than the work area is not a large window: it is a
        // window whose last row is behind the task bar. That row is the status
        // line, which is where this product says what just happened, so losing
        // it is losing the answer to whatever the operator last pressed.
        private void FitToDesktop()
        {
            Size room = WorkAreaDip();
            if (Width > room.Width) Width = room.Width;
            if (Height > room.Height) Height = room.Height;
            if (MinWidth > room.Width) MinWidth = room.Width;
            if (MinHeight > room.Height) MinHeight = room.Height;
            CentreOnScreen();
        }

        private void CentreOnScreen()
        {
            System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double scale = DeviceScale();
            WindowStartupLocation = WindowStartupLocation.Manual;
            double left = (work.Left + (work.Width - Width * scale) / 2) / scale;
            double top = (work.Top + (work.Height - Height * scale) / 2) / scale;
            if (left < work.Left / scale) left = work.Left / scale;
            if (top < work.Top / scale) top = work.Top / scale;
            Left = left;
            Top = top;
        }

        // ---------- the full shape ----------

        private UIElement BuildFull()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(Ui.AutoRow());
            root.RowDefinitions.Add(Ui.FixedRow(Theme.ProgressTrackHeight));
            root.RowDefinitions.Add(Ui.StarRow());
            root.RowDefinitions.Add(Ui.AutoRow());

            UIElement bar = TopBar(false);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            Grid.SetRow(progress, 1);
            root.Children.Add(progress);

            UIElement body = workspace.Build();
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            UIElement footer = StatusBar();
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);
            return root;
        }

        // The bar carries the operations that act on the whole window and nothing
        // else. Replay is not here - it belongs beside the workflow it replays -
        // and neither is build, which belongs beside the code. Both used to be
        // here, which made a row of eight buttons where none of them said what it
        // acted on.
        //
        // The theme switch and the settings are in the same place on both shapes
        // of the window and do not move or disappear with what else is on screen.
        private UIElement TopBar(bool mini)
        {
            Grid line = new Grid();
            line.ColumnDefinitions.Add(Ui.AutoColumn());
            line.ColumnDefinitions.Add(Ui.StarColumn());
            line.ColumnDefinitions.Add(Ui.AutoColumn());
            line.VerticalAlignment = VerticalAlignment.Center;

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;

            Button record = Ui.IconTextButton(Icons.Record, Text("home-record.txt", "Record"),
                Text("home-record-note.txt", "Counts down, then follows what you do across applications."), StartRecord, true);
            recordButton = record;
            left.Children.Add(record);

            Button snap = Ui.IconTextButton(Icons.Snap, Text("home-snap.txt", "Snap"),
                Text("home-snap-note.txt", "Point at a window and take everything it is made of."), StartSnap, false);
            snap.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            snapButton = snap;
            left.Children.Add(snap);

            Button recordSettings = Ui.IconButton(Icons.Pointer, Text("record-settings.txt", "Recording settings"),
                Text("record-settings-note.txt", "What is written down when text is entered, and whether the pointer reader is on."),
                ShowRecordSettings);
            recordSettings.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            left.Children.Add(recordSettings);
            Grid.SetColumn(left, 0);
            line.Children.Add(left);

            StackPanel middle = new StackPanel();
            middle.Orientation = Orientation.Horizontal;
            middle.VerticalAlignment = VerticalAlignment.Center;
            middle.HorizontalAlignment = HorizontalAlignment.Left;
            middle.Margin = new Thickness(Theme.Space5, 0, Theme.Space4, 0);
            if (!mini)
            {
                TextBlock caption = Ui.Label(Text("session-current.txt", "Session"));
                caption.VerticalAlignment = VerticalAlignment.Center;
                caption.Margin = new Thickness(0, 0, Theme.Space2, 0);
                middle.Children.Add(caption);
            }
            Orphan(sessionPicker);
            sessionPicker.SetResourceReference(FrameworkElement.StyleProperty, "AppComboBox");
            // The picker is the only way to reach a past session now, so it is
            // never allowed to be squeezed out of the bar by whatever else is on
            // it. On the small bar it is narrower, not absent.
            sessionPicker.MinWidth = mini ? 150 : 260;
            sessionPicker.MaxWidth = mini ? 240 : 420;
            sessionPicker.VerticalAlignment = VerticalAlignment.Center;
            Ui.Name(sessionPicker, Text("session-current.txt", "Session"),
                Text("session-current-note.txt", "Past sessions are chosen here. Choosing one changes what all three panes hold."));
            middle.Children.Add(sessionPicker);
            if (!mini)
            {
                // One folder, the session's own. There used to be a code folder
                // button, a built file folder button and an outputs folder
                // button, which is three doors into one house. It is not on the
                // small bar because the small bar is for recording, and nothing
                // about recording needs a folder open.
                Button folder = Ui.IconButton(Icons.Folder, Text("session-folder.txt", "Open this session folder"),
                    Text("session-folder-note.txt", "The records, the code, the built file and the report are all in here."),
                    OpenSessionFolder);
                folder.Margin = new Thickness(Theme.Space2, 0, 0, 0);
                middle.Children.Add(folder);
            }
            Grid.SetColumn(middle, 1);
            line.Children.Add(middle);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            if (mini)
            {
                right.Children.Add(MiniReplayControls());
            }
            else
            {
                Orphan(healthBadge);
                right.Children.Add(healthBadge);
            }
            right.Children.Add(ThemeButton());
            right.Children.Add(Ui.IconButton(Icons.Settings, Text("compact-options.txt", "Settings"),
                Text("settings-note.txt", "The replay route, the output size budget, and the startup diagnostics."), ShowOptionsDialog));
            Button shape = Ui.IconButton(mini ? Icons.Restore : Icons.Minimise,
                mini ? Text("view-full.txt", "Back to the full view") : Text("view-mini.txt", "Fold down to the small bar"),
                mini ? Text("view-full-note.txt", "Back to the modules, the code and the assistant. Nothing being worked on is lost.")
                     : Text("view-mini-note.txt", "Folds down to the bar a recording needs. Nothing being worked on is lost."),
                mini ? new Action(GoFull) : new Action(GoMini));
            right.Children.Add(shape);
            Grid.SetColumn(right, 2);
            line.Children.Add(right);

            Border frame = new Border();
            frame.Background = Theme.Get("TopbarBackground");
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 0, 0, 1);
            frame.Padding = new Thickness(Theme.Space4, Theme.Space2, Theme.Space3, Theme.Space2);
            frame.MinHeight = Theme.TopbarHeight;
            frame.Child = line;
            return frame;
        }

        private Button snapButton;
        private Button recordButton;

        private Button ThemeButton()
        {
            Button button = new Button();
            button.SetResourceReference(FrameworkElement.StyleProperty, "AppIconButton");
            PaintTheme(button);
            button.Click += delegate
            {
                Theme.Toggle();
                Theme.Persist();
                Background = Theme.SurfaceCanvas;
                PaintTheme(button);
            };
            return button;
        }

        // The sun and the moon, which mean the same thing in every application on
        // this desktop. The drawing shows the theme that pressing it will give,
        // which is the convention; the name says it in words for anything reading
        // the screen.
        private void PaintTheme(Button button)
        {
            bool dark = Theme.IsDark;
            button.Content = Icons.Make(dark ? Icons.Sun : Icons.Moon, 18, Theme.TextSub);
            Ui.Name(button,
                dark ? Text("theme-light.txt", "Switch to the light theme") : Text("theme-dark.txt", "Switch to the dark theme"),
                Text("theme-note.txt", "Changes how light the whole window is. The chosen theme is remembered."));
        }

        private UIElement StatusBar()
        {
            Orphan(status);
            status.Foreground = Theme.TextMuted;
            status.FontSize = Theme.MetaSize;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(Theme.Space5, Theme.Space2, Theme.Space5, Theme.Space2);
            if (String.IsNullOrEmpty(status.Text)) status.Text = Text("status-ready.txt", "Ready.");
            Border frame = new Border();
            frame.Background = Theme.Surface;
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 1, 0, 0);
            frame.Child = status;
            return frame;
        }

        // ---------- the small shape ----------

        // The same session, folded down to what a person needs while they are
        // recording: start and stop, which session, what is in it, and how fast
        // to play it back. No modules, no code, no assistant - during a recording
        // this window is something to get out of the way of the application being
        // recorded.
        private UIElement BuildMini()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(Ui.AutoRow());
            root.RowDefinitions.Add(Ui.FixedRow(Theme.ProgressTrackHeight));
            root.RowDefinitions.Add(Ui.AutoRow());
            root.RowDefinitions.Add(Ui.AutoRow());

            UIElement bar = TopBar(true);
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            Grid.SetRow(progress, 1);
            root.Children.Add(progress);

            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.AutoColumn());
            head.ColumnDefinitions.Add(Ui.StarColumn());
            head.Margin = new Thickness(Theme.Space4, Theme.Space2, Theme.Space3, 0);
            Button fold = Ui.IconButton(miniListOpen ? Icons.ChevronUp : Icons.ChevronDown,
                miniListOpen ? Text("mini-fold.txt", "Fold the step list away") : Text("mini-unfold.txt", "Open the step list"),
                Text("mini-fold-note.txt", "Folded, this window is no taller than its bar."),
                delegate { miniListOpen = !miniListOpen; ApplyMode(ModeMini); });
            Grid.SetColumn(fold, 0);
            head.Children.Add(fold);
            TextBlock caption = Ui.Label(Text("mini-workflow.txt", "The current workflow"));
            caption.VerticalAlignment = VerticalAlignment.Center;
            caption.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            Grid.SetColumn(caption, 1);
            head.Children.Add(caption);
            Grid.SetRow(head, 2);
            root.Children.Add(head);

            Orphan(miniWorkflow);
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.MaxHeight = Theme.MiniListHeight;
            scroll.Margin = new Thickness(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space3);
            scroll.Content = miniWorkflow;
            scroll.Visibility = miniListOpen ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(scroll, 3);
            root.Children.Add(scroll);
            PaintMiniWorkflow();
            return root;
        }

        // Replay and its speed, on the small bar, because playing a recording
        // back is one of the two things this shape is for.
        private UIElement MiniReplayControls()
        {
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Margin = new Thickness(0, 0, Theme.Space2, 0);
            Button play = Ui.IconButton(Icons.Play, Text("detail-replay.txt", "Replay"),
                Text("detail-replay-note.txt", "Carries the recorded actions out again against what is on screen now."), StartReplay);
            row.Children.Add(play);

            TextBlock value = new TextBlock();
            value.FontSize = Theme.MetaSize;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = Theme.TextSub;
            value.VerticalAlignment = VerticalAlignment.Center;
            value.MinWidth = 34;
            value.Text = "x" + Ui.Seconds(replaySpeed);

            Slider slider = Ui.Speed(replaySpeed, 0.5, 4.0, 0.25);
            slider.Width = 110;
            slider.VerticalAlignment = VerticalAlignment.Center;
            slider.Margin = new Thickness(Theme.Space2, 0, Theme.Space2, 0);
            Ui.Name(slider, Text("replay-speed.txt", "Replay speed"),
                Text("replay-speed-note.txt", "1.0 keeps the recording own pace. Higher shortens the wait between steps."));
            slider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> args)
            {
                replaySpeed = args.NewValue;
                value.Text = "x" + Ui.Seconds(args.NewValue);
                if (replay != null) replay.Speed = args.NewValue;
            };
            row.Children.Add(slider);
            row.Children.Add(value);
            return row;
        }

        private void PaintMiniWorkflow()
        {
            miniWorkflow.Children.Clear();
            if (current == null)
            {
                miniWorkflow.Children.Add(Ui.Note(Text("empty-workflow.txt", "There is no session yet.")));
                return;
            }
            if (current.Steps.Count == 0)
            {
                miniWorkflow.Children.Add(Ui.Note(Text("steps-none.txt", "This session has no recorded action.")));
                return;
            }
            for (int index = 0; index < current.Steps.Count; index++)
            {
                StepRecord step = current.Steps[index];
                Grid row = new Grid();
                row.ColumnDefinitions.Add(Ui.AutoColumn());
                row.ColumnDefinitions.Add(Ui.StarColumn());
                row.Margin = new Thickness(0, 1, 0, 1);
                TextBlock number = new TextBlock();
                number.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
                number.FontSize = Theme.MicroSize;
                number.FontWeight = FontWeights.SemiBold;
                number.Foreground = Theme.TextMuted;
                number.MinWidth = 22;
                Grid.SetColumn(number, 0);
                row.Children.Add(number);
                TextBlock label = new TextBlock();
                label.Text = step.Headline;
                label.FontSize = Theme.MetaSize;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.Foreground = Theme.TextSub;
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                miniWorkflow.Children.Add(row);
            }
        }

        // ---------- sessions ----------

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            try
            {
                hotkeys = new HotkeyManager(this, Path.Combine(baseDir, "runtime", "settings", "hotkeys.txt"));
                hotkeys.Pressed += OnHotkey;
                ReportHotkeys(hotkeys.Registrations);
            }
            catch (Exception exception)
            {
                Say(Text("hotkey-failed.txt", "The global keys could not be registered") + ": " + exception.Message, "Caution");
            }
            // Now that the window exists it can be asked what display it is on
            // and how that display is scaled, which is not knowable while the
            // constructor is deciding the shape.
            FitToDesktop();
            sessionPicker.SelectionChanged += delegate { OnSessionPicked(); };
            LoadSessions();
        }

        private void ReportHotkeys(HotkeyRegistration[] registrations)
        {
            if (registrations == null) return;
            List<string> trouble = new List<string>();
            for (int index = 0; index < registrations.Length; index++)
            {
                HotkeyRegistration item = registrations[index];
                if (item.Registered && String.IsNullOrEmpty(item.Reason)) continue;
                trouble.Add(item.Action + " " + (item.Registered
                    ? Text("hotkey-alternative.txt", "was taken, now") + " " + item.Combo
                    : Text("hotkey-none.txt", "could not be registered")));
            }
            if (trouble.Count == 0) return;
            StringBuilder text = new StringBuilder();
            text.Append(Text("hotkey-taken.txt", "HOTKEY-TAKEN"));
            for (int index = 0; index < trouble.Count; index++)
            {
                text.Append(index == 0 ? ": " : " / ");
                text.Append(trouble[index]);
            }
            hotkeyNotice = text.ToString();
            Say(hotkeyNotice, "Caution");
        }

        private void LoadSessions()
        {
            loadingSessions = true;
            string keep = current == null ? null : current.Id;
            sessions.Clear();
            sessionPicker.Items.Clear();
            string[] folders = SessionStore.List(baseDir);
            int unreadable = 0;
            for (int index = 0; index < folders.Length && index < 200; index++)
            {
                StudioSession session = null;
                try { session = SessionStore.Load(folders[index]); }
                catch { session = null; }
                if (session == null) { unreadable++; continue; }
                sessions.Add(session);
                sessionPicker.Items.Add(SessionItem(session));
            }
            loadingSessions = false;
            if (unreadable > 0) Say(Text("sessions-unreadable.txt", "Some session folders could not be read") + ": " + unreadable, "Caution");
            if (sessions.Count == 0)
            {
                current = null;
                currentProject = null;
                workspace.SetSession(null, null);
                PaintMiniWorkflow();
                return;
            }
            if (keep != null && SelectById(keep)) return;
            sessionPicker.SelectedIndex = 0;
        }

        private ComboBoxItem SessionItem(StudioSession session)
        {
            ComboBoxItem item = new ComboBoxItem();
            string kind = String.Equals(session.Kind, StudioSession.KindRecord, StringComparison.Ordinal)
                ? Text("kind-record.txt", "Record") : Text("kind-snap.txt", "Snap");
            string label = session.StartedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "  [" + kind + "]  " +
                (String.IsNullOrEmpty(session.Title) ? session.Id : session.Title);
            item.Content = label;
            item.Tag = session;
            System.Windows.Automation.AutomationProperties.SetName(item, label);
            return item;
        }

        private bool SelectById(string id)
        {
            for (int index = 0; index < sessionPicker.Items.Count; index++)
            {
                ComboBoxItem item = sessionPicker.Items[index] as ComboBoxItem;
                StudioSession session = item == null ? null : item.Tag as StudioSession;
                if (session != null && session.Id == id)
                {
                    sessionPicker.SelectedIndex = index;
                    return true;
                }
            }
            return false;
        }

        // Choosing a session is the only navigation left in this product. It
        // changes what all three panes hold; it never changes which screen the
        // operator is on, because there is only one.
        private void OnSessionPicked()
        {
            if (loadingSessions) return;
            ComboBoxItem item = sessionPicker.SelectedItem as ComboBoxItem;
            StudioSession session = item == null ? null : item.Tag as StudioSession;
            if (session == null) return;
            if (current != null && String.Equals(current.Id, session.Id, StringComparison.Ordinal)) return;
            current = session;
            currentProject = null;
            try
            {
                currentProject = CodeProject.Open(session);
            }
            catch (Exception exception)
            {
                Say(Text("code-failed.txt", "The code could not be written from this session") + ": " + exception.Message, "Danger");
            }
            workspace.SetSession(session, currentProject);
            PaintMiniWorkflow();
        }

        private void OpenSessionFolder()
        {
            if (current == null || String.IsNullOrEmpty(current.Folder))
            {
                Say(Text("open-nopath.txt", "There is no path to open."), "Caution");
                return;
            }
            OpenPath(current.Folder, true);
        }

        private void OpenReport()
        {
            if (current == null) return;
            bool has = current.ReportPath != null && File.Exists(current.ReportPath);
            if (!has)
            {
                // The report is written from the session, so a missing one is a
                // thing to write rather than a thing to report as absent.
                BuildOutputs(current, true);
                return;
            }
            OpenPath(current.ReportPath, true);
        }

        // ---------- settings ----------

        private bool AskRunConsent()
        {
            if (writeEnabled) return true;
            Confirm prompt = new Confirm(
                Text("code-consent-title.txt", "This drives the real application"),
                Text("code-consent-body.txt", "It presses buttons and types into the applications on this machine."),
                Text("code-consent-ok.txt", "Allow and run"),
                Text("replay-consent-cancel.txt", "Cancel"));
            if (!prompt.Ask(this)) return false;
            writeEnabled = true;
            if (writeSwitch != null) writeSwitch.IsChecked = true;
            return true;
        }

        // The recording settings, in one place. The hover reader used to be here
        // and on the recording panel and on the launcher, which is one setting
        // shown three times: turning it off in one of them left the other two
        // saying it was on.
        private void ShowRecordSettings()
        {
            StackPanel body = new StackPanel();
            body.Children.Add(Ui.Label(Text("settings-value.txt", "What to write down when text is entered")));
            ComboBox values = new ComboBox();
            values.SetResourceReference(FrameworkElement.StyleProperty, "AppComboBox");
            values.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddItem(values, Privacy.PolicyRecordText, Text("value-record.txt", "Record the text so the recording can be replayed"));
            AddItem(values, Privacy.PolicyLengthOnly, Text("value-length.txt", "Record the length only; ask on replay"));
            values.SelectedIndex = valuePolicy == Privacy.PolicyLengthOnly ? 1 : 0;
            values.SelectionChanged += delegate
            {
                ComboBoxItem item = values.SelectedItem as ComboBoxItem;
                if (item != null) valuePolicy = Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
            };
            body.Children.Add(values);
            body.Children.Add(Ui.Note(Privacy.PolicyStatementForScreen(valuePolicy)));

            inspectSwitch = Ui.Switch(Text("settings-inspect.txt", "Show what is under the pointer"), null, inspectEnabled);
            inspectSwitch.Checked += delegate { inspectEnabled = true; ApplyInspect(); };
            inspectSwitch.Unchecked += delegate { inspectEnabled = false; ApplyInspect(); };
            body.Children.Add(Ui.SwitchBlock(inspectSwitch,
                Text("settings-inspect-note.txt",
                    "While recording, it outlines the control under the pointer and names it. Clicks pass straight through it, and it never reaches the recording or a picture.")));

            Dialog(Text("record-settings.txt", "Recording settings"), body);
        }

        private void ShowOptionsDialog()
        {
            StackPanel body = new StackPanel();

            body.Children.Add(Ui.Label(Text("settings-route.txt", "Route used when replaying")));
            ComboBox routes = new ComboBox();
            routes.SetResourceReference(FrameworkElement.StyleProperty, "AppComboBox");
            routes.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddItem(routes, ProbeRoutes.Auto, Text("route-auto.txt", "Auto - UIA, then a window message, then synthetic input"));
            AddItem(routes, ProbeRoutes.UiaOnly, Text("route-uia.txt", "UI Automation only"));
            AddItem(routes, ProbeRoutes.Win32Only, Text("route-win32.txt", "Window messages only"));
            AddItem(routes, ProbeRoutes.SendInputOnly, Text("route-input.txt", "Synthetic input only"));
            routes.SelectedIndex = IndexOfRoute(routeMode);
            routes.SelectionChanged += delegate
            {
                ComboBoxItem item = routes.SelectedItem as ComboBoxItem;
                if (item != null) routeMode = Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
            };
            body.Children.Add(routes);

            TextBlock budgetLabel = Ui.Label(Text("settings-budget.txt", "Size budget for screens.pdf (KB)"));
            budgetLabel.Margin = new Thickness(0, Theme.Space4, 0, Theme.Space2);
            body.Children.Add(budgetLabel);
            TextBox budget = new TextBox();
            budget.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            budget.Text = pdfBudgetKb.ToString(CultureInfo.InvariantCulture);
            budget.Width = 140;
            budget.HorizontalAlignment = HorizontalAlignment.Left;
            budget.TextChanged += delegate
            {
                int parsed;
                if (Int32.TryParse(budget.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 256) pdfBudgetKb = parsed;
            };
            body.Children.Add(budget);

            writeSwitch = Ui.Switch(Text("settings-write.txt", "Let replay act on the real application"), null, writeEnabled);
            writeSwitch.Checked += delegate { writeEnabled = true; };
            writeSwitch.Unchecked += delegate { writeEnabled = false; };
            body.Children.Add(Ui.SwitchBlock(writeSwitch,
                Text("settings-write-note.txt", "Replay presses buttons and types into the applications on this machine.")));

            if (!String.IsNullOrEmpty(hotkeyNotice))
            {
                TextBlock keysLabel = Ui.Label(Text("settings-hotkeys.txt", "Global keys"));
                keysLabel.Margin = new Thickness(0, Theme.Space4, 0, Theme.Space2);
                body.Children.Add(keysLabel);
                TextBlock keys = Ui.Note(hotkeyNotice);
                keys.Foreground = Theme.CautionText;
                body.Children.Add(keys);
            }

            TextBlock diagLabel = Ui.Label(Text("settings-diagnostics.txt", "Startup diagnostics"));
            diagLabel.Margin = new Thickness(0, Theme.Space4, 0, Theme.Space2);
            body.Children.Add(diagLabel);
            TextBox diag = new TextBox();
            diag.SetResourceReference(FrameworkElement.StyleProperty, "AppReadOnlyText");
            diag.IsReadOnly = true;
            diag.AcceptsReturn = true;
            diag.TextWrapping = TextWrapping.Wrap;
            diag.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            diag.MaxHeight = 160;
            diag.Text = diagnostics == null ? "-" : JsonWriter.Write(diagnostics);
            body.Children.Add(diag);

            Dialog(Text("settings-title.txt", "Settings"), body);
        }

        private void Dialog(string title, UIElement body)
        {
            Window dialog = new Window();
            dialog.Title = title;
            dialog.Owner = this;
            dialog.Width = 620;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.MaxHeight = 620;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.Background = Theme.Surface;
            dialog.FontFamily = Theme.UiFont;
            Theme.Install(dialog.Resources);
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.Margin = new Thickness(Theme.Space5);
            StackPanel stack = new StackPanel();
            TextBlock heading = new TextBlock();
            heading.Text = title;
            heading.FontSize = Theme.TitleSize;
            heading.FontWeight = FontWeights.Bold;
            heading.Foreground = Theme.Text;
            heading.Margin = new Thickness(0, 0, 0, Theme.Space4);
            stack.Children.Add(heading);
            stack.Children.Add(body);
            Button close = new Button();
            close.Content = Text("settings-close.txt", "Close");
            close.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonPrimary");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, Theme.Space5, 0, 0);
            close.Click += delegate { dialog.Close(); };
            stack.Children.Add(close);
            scroll.Content = stack;
            dialog.Content = scroll;
            dialog.ShowDialog();
        }

        private static void AddItem(ComboBox box, string tag, string label)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = label;
            item.Tag = tag;
            box.Items.Add(item);
        }

        private static int IndexOfRoute(string value)
        {
            if (value == ProbeRoutes.UiaOnly) return 1;
            if (value == ProbeRoutes.Win32Only) return 2;
            if (value == ProbeRoutes.SendInputOnly) return 3;
            return 0;
        }

        private void ApplyInspect()
        {
            if (recorder != null) recorder.Inspect = inspectEnabled;
            if (hud != null && !inspectEnabled) hud.HideInspect();
        }

        // ---------- snap ----------

        private void StartSnap()
        {
            if (busy) return;
            Hide();
            Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            System.Threading.Thread.Sleep(220);
            PickResult picked;
            using (WindowPicker picker = new WindowPicker())
            {
                picked = picker.Pick(Text("pick-hint.txt", "Point at a window and click. Escape leaves."));
            }
            FocusSelf();
            if (picked == null || picked.Cancelled)
            {
                Say(picked != null && picked.Problem != null ? picked.Problem : Text("pick-cancelled.txt", "Nothing was taken."), "Caution");
                return;
            }
            TargetWindowInfo target = picked.Window;
            StudioSession session = SessionStore.Create(baseDir, StudioSession.KindSnap,
                (String.IsNullOrEmpty(target.ProcessName) ? "pid " + target.ProcessId : target.ProcessName) + " - " +
                (String.IsNullOrWhiteSpace(target.Title) ? target.ClassName : target.Title));
            session.Environment = diagnostics;
            session.ValuePolicy = valuePolicy;
            SessionStore.WriteMeta(session);
            Busy(Text("busy-snap.txt", "Acquiring the window..."));

            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                string problem = null;
                try
                {
                    using (ScanRunner runner = new ScanRunner(baseDir))
                    {
                        Acquire.Window(runner, session, target, new ScanLimits(), delegate(ScanProgress value)
                        {
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                Say(Text("busy-snap.txt", "Acquiring the window...") + "  " + value.NodeCount, null);
                            }));
                        });
                    }
                    for (int index = 0; index < session.Screens.Screens.Count; index++)
                    {
                        Acquire.Shoot(session, session.Screens.Screens[index], new Acquire.NullGuard(), 220);
                    }
                }
                catch (Exception exception)
                {
                    problem = exception.GetType().Name + ": " + exception.Message;
                    session.AddDiagnostic("SNAP-FAIL: " + problem);
                }
                session.EndedAt = DateTimeOffset.Now;
                string failure = problem;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Idle();
                    if (failure != null) Say(Text("snap-failed.txt", "The acquisition failed") + ": " + failure, "Danger");
                    // The shape the operator chose is kept. Coming back to the
                    // window is not the same thing as being moved to a different
                    // one, and this used to do both.
                    FocusSelf();
                    BuildOutputs(session, false);
                    LoadSessions();
                    SelectById(session.Id);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        // ---------- record ----------

        private void StartRecord()
        {
            if (busy) return;
            Hide();
            Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            System.Threading.Thread.Sleep(200);
            if (!Countdown())
            {
                Show();
                Activate();
                Say(Text("record-cancelled.txt", "The recording was cancelled before it started."), "Caution");
                return;
            }
            StudioSession session = SessionStore.Create(baseDir, StudioSession.KindRecord, Text("record-title.txt", "Record"));
            session.Environment = diagnostics;
            session.ValuePolicy = valuePolicy;
            SessionStore.WriteMeta(session);
            current = session;

            hud = new RecordHud(Text("hud-recording.txt", "Recording"), Text("hud-stop.txt", "Stop"));
            hud.StopRequested += delegate { StopRecord(); };
            hud.PauseRequested += delegate(bool value) { if (recorder != null) recorder.SetPaused(value); };
            hud.ShowControl();
            recorder = new Recorder(baseDir, session, hud, Dispatcher);
            recorder.SetExcludedHandles(hud.OwnHandles);
            recorder.Inspect = inspectEnabled;
            recorder.Progress += OnRecorderProgress;
            busy = true;
            busySince = DateTime.UtcNow;
            recorder.Start();
        }

        private bool Countdown()
        {
            CountdownWindow window = new CountdownWindow();
            return window.Run(3);
        }

        private void OnRecorderProgress(RecorderStatus value)
        {
            if (hud != null) hud.SetClock(Text("hud-recording.txt", "Recording"), value.Elapsed);
            if (value.Problem != null) Say(value.Problem, "Danger");
        }

        private void StopRecord()
        {
            if (recorder == null) return;
            Recorder finishing = recorder;
            recorder = null;
            StudioSession session = current;
            try { finishing.Stop(); }
            catch (Exception exception)
            {
                if (session != null) session.AddDiagnostic("RECORD-STOP: " + exception.Message);
            }
            finishing.Dispose();
            if (hud != null) { hud.Dispose(); hud = null; }
            busy = false;
            FocusSelf();
            if (session == null) return;
            session.EndedAt = DateTimeOffset.Now;
            if (session.Steps.Count == 0) session.AddLimit("Nothing was recorded: no action was observed between start and stop.");
            Busy(Text("busy-outputs.txt", "Writing the outputs..."));
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                OutputSet outputs = Outputs.WriteAll(session, pdfBudgetKb * 1024);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Idle();
                    FocusSelf();
                    ReportOutputs(outputs);
                    LoadSessions();
                    SelectById(session.Id);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        // ---------- replay ----------

        private void StartReplay()
        {
            if (busy || current == null) return;
            if (current.Steps.Count == 0)
            {
                Say(Text("replay-nosteps.txt", "This session has no recorded action to play back."), "Caution");
                return;
            }
            if (!writeEnabled)
            {
                Confirm prompt = new Confirm(
                    Text("replay-consent-title.txt", "Replay drives the real application"),
                    Text("replay-consent-body.txt", "It presses buttons and types into the applications on this machine."),
                    Text("replay-consent-ok.txt", "Allow and replay"),
                    Text("replay-consent-cancel.txt", "Cancel"));
                if (!prompt.Ask(this))
                {
                    Say(Text("replay-declined.txt", "Replay was not started."), "Caution");
                    return;
                }
                writeEnabled = true;
                if (writeSwitch != null) writeSwitch.IsChecked = true;
            }
            StudioSession session = current;
            replay = new ReplayEngine(baseDir, session);
            replay.AskSecret = AskSecret;
            replay.Speed = replaySpeed;
            hud = new RecordHud(Text("hud-replaying.txt", "Replaying"), Text("hud-stop.txt", "Stop"));
            hud.StopRequested += delegate { if (replay != null) replay.Cancel(); };
            // Pausing a replay is the same control as pausing a recording, and it
            // now reaches something: it used to be on the panel during a replay
            // and be wired to nothing at all.
            hud.PauseRequested += delegate(bool value) { if (replay != null) replay.SetPaused(value); };
            hud.ShowControl();
            busy = true;
            busySince = DateTime.UtcNow;
            Hide();
            string route = routeMode;
            replay.Progress += delegate(ReplayProgress value)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (hud != null) hud.SetClock(Text("hud-replaying.txt", "Replaying") + " " + value.Index + "/" + value.Total, DateTime.UtcNow - busySince);
                }));
            };
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                ReplayReport report = null;
                string problem = null;
                try { report = replay.Run(route, true); }
                catch (Exception exception) { problem = exception.GetType().Name + ": " + exception.Message; }
                OutputSet outputs = Outputs.WriteAll(session, pdfBudgetKb * 1024);
                ReplayReport finished = report;
                string failure = problem;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (hud != null) { hud.Dispose(); hud = null; }
                    if (replay != null) { replay.Dispose(); replay = null; }
                    busy = false;
                    Idle();
                    FocusSelf();
                    if (failure != null) Say(Text("replay-failed.txt", "The replay stopped with an error") + ": " + failure, "Danger");
                    else if (finished != null)
                    {
                        string message = Text("replay-done.txt", "Replay finished") + ": " + finished.Succeeded + "/" + finished.Attempted +
                            (finished.StopReason == null ? "" : "   " + finished.StopReason);
                        Say(message, finished.StopReason == null ? "Success" : "Caution");
                    }
                    ReportOutputs(outputs);
                    LoadSessions();
                    SelectById(session.Id);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private string AskSecret(StepRecord step)
        {
            string answer = null;
            Dispatcher.Invoke(new Action(delegate
            {
                SecretPrompt prompt = new SecretPrompt(
                    Text("secret-title.txt", "A value is needed"),
                    Text("secret-note.txt", "This step enters something the recording deliberately did not keep.") + "\n" + step.StepId + "  " + step.Headline,
                    Text("secret-ok.txt", "Use this"),
                    Text("secret-cancel.txt", "Stop here"));
                answer = prompt.Ask();
            }));
            return answer;
        }

        // ---------- outputs ----------

        private void BuildOutputs(StudioSession session, bool interactive)
        {
            if (session == null) return;
            Busy(Text("busy-outputs.txt", "Writing the outputs..."));
            int budget = pdfBudgetKb * 1024;
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                OutputSet outputs = Outputs.WriteAll(session, budget);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Idle();
                    ReportOutputs(outputs);
                    if (interactive && session.ReportPath != null && File.Exists(session.ReportPath)) OpenPath(session.ReportPath, true);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ReportOutputs(OutputSet outputs)
        {
            if (outputs == null) return;
            if (outputs.Complete)
            {
                string extra = outputs.Pdf.OmittedScreens.Count > 0
                    ? "   " + Text("outputs-omitted.txt", "screens left out of the pdf") + ": " + outputs.Pdf.OmittedScreens.Count
                    : "";
                Say(Text("outputs-done.txt", "report.html, session.md and screens.pdf were written.") + extra,
                    outputs.Pdf.OmittedScreens.Count > 0 ? "Caution" : "Success");
                return;
            }
            Say(Text("outputs-failed.txt", "Some outputs could not be written") + ": " + outputs.Problems, "Danger");
        }

        private void OpenPath(string path, bool exists)
        {
            if (String.IsNullOrEmpty(path))
            {
                Say(Text("open-nopath.txt", "There is no path to open."), "Caution");
                return;
            }
            if (!exists && !File.Exists(path) && !Directory.Exists(path))
            {
                Say(Text("open-missing.txt", "That is not on disk yet") + ": " + path, "Caution");
                return;
            }
            try { System.Diagnostics.Process.Start(path); }
            catch (Exception exception) { Say(Text("open-failed.txt", "It could not be opened") + ": " + exception.Message, "Danger"); }
        }

        // ---------- plumbing ----------

        private void OnHotkey(string action)
        {
            if (action == "stop")
            {
                if (recorder != null) StopRecord();
                else if (replay != null) replay.Cancel();
                return;
            }
            if (action == "emergency")
            {
                if (replay != null) replay.Cancel();
                if (recorder != null) StopRecord();
                ProbeRunner.EmergencyStop();
                writeEnabled = false;
                if (writeSwitch != null) writeSwitch.IsChecked = false;
                Say(Text("emergency.txt", "Everything was stopped and permission was withdrawn."), "Danger");
            }
        }

        private void OnClock(object sender, EventArgs args)
        {
            AcquisitionHealth health = Probe.GetHealth();
            string state = health == null || health.State == null ? "-" : health.State;
            SetBadgeTone(healthBadge, healthLabel, state == "ready" ? "Success" : (state == "degraded" ? "Caution" : "Accent"));
            healthLabel.Text = Text("health.txt", "acquisition") + " " + state;
            if (busy && !IsVisible) return;
            if (busy && progress.IsIndeterminate == false) progress.IsIndeterminate = true;
        }

        private void Busy(string label)
        {
            busy = true;
            busySince = DateTime.UtcNow;
            progress.IsIndeterminate = true;
            if (snapButton != null) snapButton.IsEnabled = false;
            if (recordButton != null) recordButton.IsEnabled = false;
            Say(label, null);
        }

        private void Idle()
        {
            busy = false;
            progress.IsIndeterminate = false;
            progress.Value = 0;
            if (snapButton != null) snapButton.IsEnabled = true;
            if (recordButton != null) recordButton.IsEnabled = true;
        }

        private void FocusSelf()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            Activate();
            Topmost = false;
            long handle = new System.Windows.Interop.WindowInteropHelper(this).Handle.ToInt64();
            bool front = WindowTools.BringToFront(handle);
            Focus();
            if (!front)
            {
                Say(Text("focus-failed.txt", "This window could not be brought to the front. Select it from the task bar."), "Caution");
            }
        }

        private void Say(string message, string tone)
        {
            status.Text = message;
            if (tone == "Danger") status.Foreground = Theme.DangerText;
            else if (tone == "Caution") status.Foreground = Theme.CautionText;
            else if (tone == "Success") status.Foreground = Theme.SuccessText;
            else status.Foreground = Theme.TextMuted;
        }

        private void OnClosed(object sender, EventArgs args)
        {
            clock.Stop();
            try { workspace.Persist(); }
            catch { }
            if (recorder != null) { recorder.Stop(); recorder.Dispose(); recorder = null; }
            if (replay != null) { replay.Cancel(); replay.Dispose(); replay = null; }
            if (hud != null) { hud.Dispose(); hud = null; }
            if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
        }

        private string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        private static Border Badge(TextBlock label, string text, string tone)
        {
            label.Text = text;
            label.FontSize = Theme.MicroSize;
            label.FontWeight = FontWeights.SemiBold;
            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(Theme.RadiusSm);
            badge.BorderThickness = new Thickness(1);
            badge.Padding = new Thickness(Theme.Space2, 2, Theme.Space2, 2);
            badge.Margin = new Thickness(0, 0, Theme.Space2, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = label;
            SetBadgeTone(badge, label, tone);
            return badge;
        }

        private static void SetBadgeTone(Border badge, TextBlock label, string tone)
        {
            if (badge == null) return;
            if (String.Equals(tone, "Danger", StringComparison.Ordinal))
            {
                badge.Background = Theme.DangerSoft; badge.BorderBrush = Theme.Danger; label.Foreground = Theme.DangerText;
            }
            else if (String.Equals(tone, "Caution", StringComparison.Ordinal))
            {
                badge.Background = Theme.CautionSoft; badge.BorderBrush = Theme.Caution; label.Foreground = Theme.CautionText;
            }
            else if (String.Equals(tone, "Success", StringComparison.Ordinal))
            {
                badge.Background = Theme.SuccessSoft; badge.BorderBrush = Theme.Success; label.Foreground = Theme.SuccessText;
            }
            else
            {
                badge.Background = Theme.AccentSoft; badge.BorderBrush = Theme.Accent; label.Foreground = Theme.AccentText;
            }
        }
    }

    // A question the operator cannot walk past. Used where pressing a button
    // would otherwise appear to do nothing, because the product refused for a
    // reason the operator never saw.
    public sealed class Confirm
    {
        private readonly string title;
        private readonly string body;
        private readonly string okLabel;
        private readonly string cancelLabel;

        public Confirm(string windowTitle, string explanation, string ok, string cancel)
        {
            title = windowTitle;
            body = explanation;
            okLabel = ok;
            cancelLabel = cancel;
        }

        public bool Ask(Window owner)
        {
            Window dialog = new Window();
            dialog.Title = title;
            dialog.Owner = owner;
            dialog.Width = 460;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            dialog.Background = Theme.Surface;
            dialog.FontFamily = Theme.UiFont;
            Theme.Install(dialog.Resources);

            StackPanel stack = new StackPanel();
            stack.Margin = new Thickness(Theme.Space5);
            TextBlock heading = new TextBlock();
            heading.Text = title;
            heading.FontSize = Theme.SectionSize;
            heading.FontWeight = FontWeights.Bold;
            heading.Foreground = Theme.Text;
            heading.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(heading);
            TextBlock text = new TextBlock();
            text.Text = body;
            text.TextWrapping = TextWrapping.Wrap;
            text.Foreground = Theme.TextSub;
            text.FontSize = Theme.BodySize;
            text.LineHeight = Theme.BodySize * Theme.BodyLine;
            text.Margin = new Thickness(0, Theme.Space3, 0, 0);
            stack.Children.Add(text);

            bool answer = false;
            WrapPanel row = new WrapPanel();
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, Theme.Space5, 0, 0);
            Button cancel = new Button();
            cancel.Content = cancelLabel;
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            cancel.Margin = new Thickness(0, 0, Theme.Space2, 0);
            cancel.IsCancel = true;
            cancel.Click += delegate { answer = false; dialog.Close(); };
            row.Children.Add(cancel);
            Button ok = new Button();
            ok.Content = okLabel;
            ok.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonPrimary");
            ok.IsDefault = true;
            ok.Click += delegate { answer = true; dialog.Close(); };
            row.Children.Add(ok);
            stack.Children.Add(row);

            dialog.Content = stack;
            dialog.ShowDialog();
            return answer;
        }
    }

    // Three, two, one. It sits in the middle of the primary display, takes no
    // focus away from anything, and Escape during the count leaves without
    // starting.
    public sealed class CountdownWindow
    {
        public bool Run(int seconds)
        {
            Window window = new Window();
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.AllowsTransparency = true;
            window.Background = Brushes.Transparent;
            window.ShowInTaskbar = false;
            window.Topmost = true;
            window.ShowActivated = true;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            TextBlock caption = new TextBlock();
            caption.FontFamily = Theme.UiFont;
            caption.FontSize = Theme.LabelSize;
            caption.FontWeight = FontWeights.SemiBold;
            caption.Foreground = new SolidColorBrush(Theme.Parse("#B9C6D4"));
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Text = Messages.Text("countdown-title.txt", "The recording starts in");

            TextBlock number = new TextBlock();
            number.FontFamily = Theme.UiFont;
            number.FontSize = 72;
            number.LineHeight = 74;
            number.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            number.FontWeight = FontWeights.Bold;
            number.Foreground = Brushes.White;
            number.TextAlignment = TextAlignment.Center;
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.Margin = new Thickness(0, Theme.Space4, 0, 0);
            number.Text = seconds.ToString(CultureInfo.InvariantCulture);

            TextBlock hint = new TextBlock();
            hint.FontFamily = Theme.UiFont;
            hint.FontSize = Theme.MetaSize;
            hint.Foreground = new SolidColorBrush(Theme.Parse("#B9C6D4"));
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, Theme.Space4, 0, 0);
            hint.Text = Messages.Text("countdown-hint.txt", "Escape cancels");

            StackPanel stack = new StackPanel();
            stack.MinWidth = 192;
            stack.Children.Add(caption);
            stack.Children.Add(number);
            stack.Children.Add(hint);

            Border shell = new Border();
            shell.Background = new SolidColorBrush(Theme.Parse("#101620"));
            shell.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            shell.BorderThickness = new Thickness(1);
            shell.CornerRadius = new CornerRadius(Theme.RadiusLg);
            shell.Padding = new Thickness(Theme.Space7, Theme.Space5, Theme.Space7, Theme.Space5);
            shell.Child = stack;
            window.Content = shell;

            bool cancelled = false;
            int remaining = seconds;
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000);
            DispatcherFrame frame = new DispatcherFrame();
            timer.Tick += delegate
            {
                remaining--;
                if (remaining <= 0)
                {
                    timer.Stop();
                    frame.Continue = false;
                    return;
                }
                number.Text = remaining.ToString(CultureInfo.InvariantCulture);
            };
            window.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key != System.Windows.Input.Key.Escape) return;
                cancelled = true;
                timer.Stop();
                frame.Continue = false;
            };
            window.Show();
            window.Activate();
            timer.Start();
            Dispatcher.PushFrame(frame);
            window.Close();
            return !cancelled;
        }
    }

    // Asks for something the recording refused to keep. What is typed here is
    // handed straight to the step that needs it and is never written to disk,
    // never logged and never put in any output.
    public sealed class SecretPrompt
    {
        private readonly string title;
        private readonly string note;
        private readonly string okLabel;
        private readonly string cancelLabel;

        public SecretPrompt(string windowTitle, string explanation, string ok, string cancel)
        {
            title = windowTitle;
            note = explanation;
            okLabel = ok;
            cancelLabel = cancel;
        }

        public string Ask()
        {
            Window window = new Window();
            window.Title = title;
            window.Width = 460;
            window.SizeToContent = SizeToContent.Height;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ResizeMode = ResizeMode.NoResize;
            window.Topmost = true;
            window.Background = Theme.Surface;
            window.FontFamily = Theme.UiFont;
            Theme.Install(window.Resources);

            StackPanel stack = new StackPanel();
            stack.Margin = new Thickness(Theme.Space5);
            TextBlock text = new TextBlock();
            text.Text = note;
            text.TextWrapping = TextWrapping.Wrap;
            text.Foreground = Theme.TextSub;
            text.FontSize = Theme.MetaSize;
            stack.Children.Add(text);

            PasswordBox box = new PasswordBox();
            box.Margin = new Thickness(0, Theme.Space4, 0, 0);
            box.Height = 32;
            box.FontSize = Theme.BodySize;
            stack.Children.Add(box);

            string answer = null;
            WrapPanel row = new WrapPanel();
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(0, Theme.Space5, 0, 0);
            Button cancel = new Button();
            cancel.Content = cancelLabel;
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            cancel.Margin = new Thickness(0, 0, Theme.Space2, 0);
            cancel.Click += delegate { answer = null; window.Close(); };
            row.Children.Add(cancel);
            Button ok = new Button();
            ok.Content = okLabel;
            ok.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonPrimary");
            ok.IsDefault = true;
            ok.Click += delegate { answer = box.Password; window.Close(); };
            row.Children.Add(ok);
            stack.Children.Add(row);

            window.Content = stack;
            window.Loaded += delegate { box.Focus(); };
            window.ShowDialog();
            return answer;
        }
    }
}
