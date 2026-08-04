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

    // The whole product is three things: take a snap of a window, record what a
    // person does across applications, and play that recording back. Everything
    // else - the reports, the assistant handover, the diagnostics - follows from
    // a session and is reached from the session, never from a wizard step.
    public sealed class StudioWindow : Window
    {
        private readonly string baseDir;
        private readonly JsonObject diagnostics;
        private readonly DispatcherTimer clock;

        private readonly ListBox sessionList = new ListBox();
        private readonly StackPanel detail = new StackPanel();
        private readonly TextBlock status = new TextBlock();
        private readonly TextBlock compactHint = new TextBlock();
        private readonly TextBlock healthLabel = new TextBlock();
        private readonly Border healthBadge;
        private readonly Button snapButton = new Button();
        private readonly Button recordButton = new Button();
        private readonly ProgressBar progress = new ProgressBar();

        private readonly List<StudioSession> sessions = new List<StudioSession>();
        private StudioSession current;
        private HotkeyManager hotkeys;

        private RecordHud hud;
        private Recorder recorder;
        private ReplayEngine replay;
        private DateTime busySince;
        private string busyLabel;
        private bool busy;

        private const string ModeCompact = "compact";
        private const string ModeResult = "result";

        // The launcher is deliberately about the size of the one Windows itself
        // uses for the same kind of job; the result window is sized to be read.
        private const int CompactWidth = 660;
        private const int CompactHeight = 356;
        private const int ResultWidth = 1120;
        private const int ResultHeight = 840;

        private string mode = ModeCompact;
        private string hotkeyNotice;
        // Held on the window, not on a control, so rebuilding a view can never
        // silently drop a permission the operator granted.
        private bool writeEnabled;
        private string routeMode = ProbeRoutes.Auto;
        private string valuePolicy = Privacy.PolicyRecordText;
        private int pdfBudgetKb = ScreensPdf.DefaultBudgetBytes / 1024;
        private CheckBox writeSwitch;

        public StudioWindow(string directory, JsonObject startupDiagnostics, int autoCloseMs)
        {
            baseDir = directory;
            diagnostics = startupDiagnostics;
            Messages.Init(baseDir);
            Theme.Init(baseDir);
            Theme.Install(Resources);

            Title = Text("app-title.txt", "App Studio");
            // Two shapes, not one. On launch this is a small form that offers the
            // three things and the settings and nothing else; a result is a
            // different job and gets a window sized for reading. Carrying a large
            // empty result area around from the first moment is what made the
            // ordinary window feel oversized.
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
            ApplyMode(ModeCompact);

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

        // ---------- window shape ----------

        // The status line, the progress track, the health badge, the session list
        // and the detail panel are one instance each and are carried between the
        // two window shapes. WPF refuses to give an element a second parent, so
        // each one is taken out of the tree it is in before the new tree is
        // built. Without this the rebuild throws, and it throws inside the
        // callback that finishes an acquisition, which is how a snap could end
        // with its records on disk but no outputs written.
        private static void Orphan(UIElement child)
        {
            if (child == null) return;
            DependencyObject parent = System.Windows.LogicalTreeHelper.GetParent(child);
            Panel panel = parent as Panel;
            if (panel != null)
            {
                panel.Children.Remove(child);
                return;
            }
            Decorator decorator = parent as Decorator;
            if (decorator != null)
            {
                decorator.Child = null;
                return;
            }
            ContentControl holder = parent as ContentControl;
            if (holder != null) holder.Content = null;
        }

        private void DetachShared()
        {
            Orphan(progress);
            Orphan(detail);
            Orphan(status);
            Orphan(healthBadge);
            Orphan(sessionList);
            Orphan(compactHint);
            Orphan(snapButton);
            Orphan(recordButton);
        }

        private void OnSnapClick(object sender, RoutedEventArgs args) { StartSnap(); }
        private void OnRecordClick(object sender, RoutedEventArgs args) { StartRecord(); }

        private void ApplyMode(string next)
        {
            mode = next;
            DetachShared();
            bool compact = String.Equals(mode, ModeCompact, StringComparison.Ordinal);
            // The minimums come first. Shrinking back to the launcher while the
            // result window's minimum is still in force leaves the window stuck
            // at that minimum, because the assignment is clamped as it is made.
            MinWidth = compact ? CompactWidth : 940;
            MinHeight = compact ? CompactHeight : 620;
            Width = compact ? CompactWidth : ResultWidth;
            Height = compact ? CompactHeight : ResultHeight;
            ResizeMode = compact ? ResizeMode.CanMinimize : ResizeMode.CanResize;
            Background = Theme.SurfaceCanvas;
            Content = compact ? BuildCompact() : BuildResult();
            if (!compact) ShowDetail();
        }

        private void GoResult()
        {
            if (String.Equals(mode, ModeResult, StringComparison.Ordinal))
            {
                ShowDetail();
                return;
            }
            ApplyMode(ModeResult);
            CentreOnScreen();
        }

        private void GoCompact()
        {
            if (String.Equals(mode, ModeCompact, StringComparison.Ordinal)) return;
            ApplyMode(ModeCompact);
            CentreOnScreen();
        }

        private void CentreOnScreen()
        {
            System.Drawing.Rectangle work = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            double scale = 1.0;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                double m11 = source.CompositionTarget.TransformToDevice.M11;
                if (m11 > 0) scale = m11;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (work.Left + (work.Width - Width * scale) / 2) / scale;
            Top = (work.Top + (work.Height - Height * scale) / 2) / scale;
        }

        // The launcher: the things this product does, the settings, and one line
        // of state. There is no result area here at all - that is what made the
        // ordinary window feel oversized.
        private UIElement BuildCompact()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(Row(GridLength.Auto));
            root.RowDefinitions.Add(Row(new GridLength(Theme.ProgressTrackHeight)));
            root.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));
            root.RowDefinitions.Add(Row(GridLength.Auto));
            root.Children.Add(TopBar());
            Grid.SetRow(progress, 1);
            root.Children.Add(progress);

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(Theme.Space5, Theme.Space4, Theme.Space5, 0);

            Grid actions = new Grid();
            actions.ColumnDefinitions.Add(Column(new GridLength(1, GridUnitType.Star)));
            actions.ColumnDefinitions.Add(Column(new GridLength(Theme.Space3)));
            actions.ColumnDefinitions.Add(Column(new GridLength(1, GridUnitType.Star)));
            UIElement snap = CompactAction(snapButton, Text("home-snap.txt", "Snap"), Text("home-snap-short.txt", "Pick a window and take it apart."), StartSnap);
            UIElement record = CompactAction(recordButton, Text("home-record.txt", "Record"), Text("home-record-short.txt", "Follow what you do across applications."), StartRecord);
            Grid.SetColumn(snap, 0);
            Grid.SetColumn(record, 2);
            actions.Children.Add(snap);
            actions.Children.Add(record);
            body.Children.Add(actions);

            WrapPanel row = new WrapPanel();
            row.Margin = new Thickness(0, Theme.Space4, 0, 0);
            Button results = new Button();
            results.Content = Text("compact-results.txt", "Results");
            results.SetResourceReference(StyleProperty, "AppButtonCompact");
            results.Margin = new Thickness(0, 0, Theme.Space2, 0);
            results.Click += delegate { GoResult(); };
            row.Children.Add(results);
            Button options = new Button();
            options.Content = Text("compact-options.txt", "Settings");
            options.SetResourceReference(StyleProperty, "AppButtonCompact");
            options.Click += delegate { ShowOptionsDialog(); };
            row.Children.Add(options);
            body.Children.Add(row);

            compactHint.Text = SessionSummary();
            compactHint.FontSize = Theme.MetaSize;
            compactHint.Foreground = Theme.TextMuted;
            compactHint.TextWrapping = TextWrapping.Wrap;
            compactHint.Margin = new Thickness(0, Theme.Space3, 0, 0);
            body.Children.Add(compactHint);

            Grid.SetRow(body, 2);
            root.Children.Add(body);
            root.Children.Add(StatusBar());
            return root;
        }

        private Border CompactAction(Button button, string label, string note, Action action)
        {
            Orphan(button);
            button.Content = label;
            button.SetResourceReference(StyleProperty, "AppButtonPrimary");
            button.Height = 56;
            button.FontSize = Theme.TitleSize;
            button.FontWeight = FontWeights.Bold;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            // The same instance is rewrapped whenever the window changes shape,
            // so the handler is replaced rather than added to.
            if (button == snapButton) { button.Click -= OnSnapClick; button.Click += OnSnapClick; }
            else { button.Click -= OnRecordClick; button.Click += OnRecordClick; }
            StackPanel stack = new StackPanel();
            stack.Children.Add(button);
            TextBlock text = new TextBlock();
            text.Text = note;
            text.FontSize = Theme.MicroSize;
            text.Foreground = Theme.TextMuted;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(2, Theme.Space2, 0, 0);
            text.MinHeight = 32;
            stack.Children.Add(text);
            Border box = new Border();
            box.Child = stack;
            return box;
        }

        private UIElement BuildResult()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(Row(GridLength.Auto));
            root.RowDefinitions.Add(Row(new GridLength(Theme.ProgressTrackHeight)));
            root.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));
            root.RowDefinitions.Add(Row(GridLength.Auto));
            root.Children.Add(TopBar());
            Grid.SetRow(progress, 1);
            root.Children.Add(progress);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(Column(new GridLength(320)));
            body.ColumnDefinitions.Add(Column(new GridLength(1, GridUnitType.Star)));
            body.Children.Add(LeftRail());
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Padding = new Thickness(Theme.Space5, Theme.Space4, Theme.Space5, Theme.Space5);
            detail.Margin = new Thickness(0);
            scroll.Content = detail;
            Grid.SetColumn(scroll, 1);
            body.Children.Add(scroll);
            Grid.SetRow(body, 2);
            root.Children.Add(body);
            root.Children.Add(StatusBar());
            return root;
        }

        private string SessionSummary()
        {
            if (sessions.Count == 0) return Text("compact-none.txt", "No session yet.");
            return Text("compact-count.txt", "sessions") + ": " + sessions.Count;
        }

        // ---------- chrome ----------

        private UIElement TopBar()
        {
            DockPanel bar = new DockPanel();
            bar.LastChildFill = true;
            bar.Background = Theme.Get("TopbarBackground");
            bar.Height = Theme.TopbarHeight;

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(0, 0, Theme.Space4, 0);
            right.Children.Add(healthBadge);
            Button back = new Button();
            back.Content = Text("topbar-back.txt", "Back");
            back.SetResourceReference(StyleProperty, "AppButtonCompact");
            back.Margin = new Thickness(0, 0, Theme.Space2, 0);
            back.Visibility = String.Equals(mode, ModeResult, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
            back.Click += delegate { GoCompact(); };
            right.Children.Add(back);
            Button theme = new Button();
            theme.Content = Text("topbar-theme.txt", "Theme");
            theme.SetResourceReference(StyleProperty, "AppButtonCompact");
            theme.Click += delegate { Theme.Toggle(); Theme.Persist(); Background = Theme.SurfaceCanvas; };
            right.Children.Add(theme);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            TextBlock title = new TextBlock();
            title.Text = Text("app-title.txt", "App Studio");
            title.FontSize = Theme.TitleSize;
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.Text;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(Theme.Space5, 0, 0, 0);
            bar.Children.Add(title);

            Border frame = new Border();
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 0, 0, 1);
            frame.Child = bar;
            Grid.SetRow(frame, 0);
            return frame;
        }

        private UIElement LeftRail()
        {
            Grid rail = new Grid();
            rail.RowDefinitions.Add(Row(GridLength.Auto));
            rail.RowDefinitions.Add(Row(GridLength.Auto));
            rail.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));

            StackPanel actions = new StackPanel();
            actions.Margin = new Thickness(Theme.Space5, Theme.Space5, Theme.Space4, 0);
            actions.Children.Add(BigButton(snapButton, Text("home-snap.txt", "Snap"), Text("home-snap-note.txt", "Point at a window and take everything it is made of."), StartSnap));
            actions.Children.Add(BigButton(recordButton, Text("home-record.txt", "Record"), Text("home-record-note.txt", "Count down, then follow what you do across applications."), StartRecord));
            rail.Children.Add(actions);

            DockPanel header = new DockPanel();
            header.Margin = new Thickness(Theme.Space5, Theme.Space6, Theme.Space4, Theme.Space2);
            Button refresh = new Button();
            refresh.Content = Text("home-refresh.txt", "Refresh");
            refresh.SetResourceReference(StyleProperty, "AppButtonCompact");
            refresh.Click += delegate { LoadSessions(); };
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);
            TextBlock caption = new TextBlock();
            caption.Text = Text("home-sessions.txt", "Sessions");
            caption.FontSize = Theme.SectionSize;
            caption.FontWeight = FontWeights.Bold;
            caption.Foreground = Theme.Text;
            caption.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(caption);
            Grid.SetRow(header, 1);
            rail.Children.Add(header);

            sessionList.SetResourceReference(StyleProperty, "AppListBox");
            sessionList.Margin = new Thickness(Theme.Space4, 0, Theme.Space4, Theme.Space4);
            sessionList.SelectionChanged += delegate { ShowSelected(); };
            Grid.SetRow(sessionList, 2);
            rail.Children.Add(sessionList);

            Border frame = new Border();
            frame.Background = Theme.Surface;
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 0, 1, 0);
            frame.Child = rail;
            Grid.SetColumn(frame, 0);
            return frame;
        }

        private UIElement StatusBar()
        {
            status.Text = Text("status-ready.txt", "Ready.");
            status.Foreground = Theme.TextMuted;
            status.FontSize = Theme.MetaSize;
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(Theme.Space5, Theme.Space2, Theme.Space5, Theme.Space2);
            Border frame = new Border();
            frame.Background = Theme.Surface;
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 1, 0, 0);
            frame.Child = status;
            Grid.SetRow(frame, 3);
            return frame;
        }

        private Border BigButton(Button button, string label, string note, Action action)
        {
            Orphan(button);
            button.Content = label;
            button.SetResourceReference(StyleProperty, "AppButtonPrimary");
            button.Height = 46;
            button.FontSize = Theme.SectionSize;
            button.FontWeight = FontWeights.Bold;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (button == snapButton) { button.Click -= OnSnapClick; button.Click += OnSnapClick; }
            else { button.Click -= OnRecordClick; button.Click += OnRecordClick; }
            StackPanel stack = new StackPanel();
            stack.Children.Add(button);
            TextBlock text = new TextBlock();
            text.Text = note;
            text.FontSize = Theme.MetaSize;
            text.LineHeight = Theme.MetaSize * Theme.BodyLine;
            text.Foreground = Theme.TextMuted;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(2, Theme.Space2, 0, 0);
            stack.Children.Add(text);
            Border box = new Border();
            box.Margin = new Thickness(0, 0, 0, Theme.Space4);
            box.Child = stack;
            return box;
        }

        // ---------- session list ----------

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            try
            {
                hotkeys = new HotkeyManager(this, Path.Combine(baseDir, "runtime", "settings", "hotkeys.txt"));
                hotkeys.Pressed += OnHotkey;
                // A key that another program already holds is registered under a
                // different combination, or not at all. Either way the operator
                // has to be told, because the stop key is what they reach for
                // while this window is hidden.
                ReportHotkeys(hotkeys.Registrations);
            }
            catch (Exception exception)
            {
                Say(Text("hotkey-failed.txt", "The global keys could not be registered") + ": " + exception.Message, "Caution");
            }
            LoadSessions();
            ShowWelcome();
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
            sessions.Clear();
            sessionList.Items.Clear();
            string[] folders = SessionStore.List(baseDir);
            int unreadable = 0;
            for (int index = 0; index < folders.Length && index < 200; index++)
            {
                StudioSession session = null;
                try
                {
                    session = SessionStore.Load(folders[index]);
                }
                catch
                {
                    session = null;
                }
                if (session == null)
                {
                    unreadable++;
                    continue;
                }
                sessions.Add(session);
                sessionList.Items.Add(SessionItem(session));
            }
            if (sessions.Count == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = Text("home-empty.txt", "No session yet.");
                empty.TextWrapping = TextWrapping.Wrap;
                empty.Foreground = Theme.TextMuted;
                empty.FontSize = Theme.MetaSize;
                empty.Margin = new Thickness(Theme.Space2, Theme.Space3, Theme.Space2, 0);
                ListBoxItem item = new ListBoxItem();
                item.Content = empty;
                item.IsHitTestVisible = false;
                sessionList.Items.Add(item);
            }
            if (unreadable > 0) Say(Text("sessions-unreadable.txt", "Some session folders could not be read") + ": " + unreadable, "Caution");
            compactHint.Text = SessionSummary();
        }

        private ListBoxItem SessionItem(StudioSession session)
        {
            StackPanel stack = new StackPanel();
            DockPanel head = new DockPanel();
            TextBlock kind = new TextBlock();
            Border badge = Badge(kind, session.Kind == StudioSession.KindRecord
                ? Text("kind-record.txt", "record")
                : Text("kind-snap.txt", "snap"), session.Kind == StudioSession.KindRecord ? "Caution" : "Accent");
            DockPanel.SetDock(badge, Dock.Right);
            badge.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            head.Children.Add(badge);
            TextBlock title = new TextBlock();
            title.Text = String.IsNullOrEmpty(session.Title) ? session.Id : session.Title;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Theme.Text;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            stack.Children.Add(head);
            TextBlock meta = new TextBlock();
            meta.Text = session.StartedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "   " +
                Text("list-screens.txt", "screens") + " " + session.Screens.Screens.Count + "   " +
                Text("list-elements.txt", "elements") + " " + session.Elements.Count +
                (session.Steps.Count > 0 ? "   " + Text("list-steps.txt", "actions") + " " + session.Steps.Count : "");
            meta.FontSize = Theme.MicroSize;
            meta.Foreground = Theme.TextMuted;
            meta.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(meta);
            ListBoxItem item = new ListBoxItem();
            item.SetResourceReference(StyleProperty, "AppListItem");
            item.Content = stack;
            item.Tag = session;
            return item;
        }

        private void ShowSelected()
        {
            ListBoxItem item = sessionList.SelectedItem as ListBoxItem;
            StudioSession session = item == null ? null : item.Tag as StudioSession;
            if (session == null) return;
            current = session;
            ShowDetail();
        }

        // ---------- right pane ----------

        private void ShowWelcome()
        {
            if (String.Equals(mode, ModeCompact, StringComparison.Ordinal)) return;
            detail.Children.Clear();
            StackPanel card = new StackPanel();
            card.Children.Add(Heading(Text("welcome-title.txt", "Three things")));
            card.Children.Add(Body(Text("welcome-body.txt", "Snap, record and replay.")));
            card.Children.Add(Note(Privacy.PolicyStatement(valuePolicy)));
            detail.Children.Add(Card(card));
            detail.Children.Add(Settings());
        }

        private void ShowDetail()
        {
            if (String.Equals(mode, ModeCompact, StringComparison.Ordinal)) return;
            detail.Children.Clear();
            if (current == null)
            {
                ShowWelcome();
                return;
            }
            StackPanel head = new StackPanel();
            head.Children.Add(Heading(String.IsNullOrEmpty(current.Title) ? current.Id : current.Title));
            head.Children.Add(Note(current.Id + "   " + current.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            WrapPanel stats = new WrapPanel();
            stats.Margin = new Thickness(0, Theme.Space4, 0, 0);
            AddStat(stats, current.Screens.Screens.Count, Text("stat-screens.txt", "screens"));
            AddStat(stats, current.Screens.ShotCount, Text("stat-shots.txt", "pictures"));
            AddStat(stats, current.Elements.Count, Text("stat-elements.txt", "elements"));
            AddStat(stats, current.Steps.Count, Text("stat-steps.txt", "actions"));
            AddStat(stats, current.Limits.Count, Text("stat-limits.txt", "stated limits"));
            head.Children.Add(stats);
            head.Children.Add(OutputRow());
            detail.Children.Add(Card(head));

            if (current.Steps.Count > 0) detail.Children.Add(StepsCard());
            detail.Children.Add(ScreensCard());
            detail.Children.Add(LimitsCard());
            detail.Children.Add(Settings());
        }

        private UIElement OutputRow()
        {
            WrapPanel row = new WrapPanel();
            row.Margin = new Thickness(0, Theme.Space5, 0, 0);
            bool hasReport = current.ReportPath != null && File.Exists(current.ReportPath);
            bool hasAi = current.SessionMdPath != null && File.Exists(current.SessionMdPath) &&
                current.ScreensPdfPath != null && File.Exists(current.ScreensPdfPath);

            if (current.Steps.Count > 0)
            {
                AddButton(row, Text("detail-replay.txt", "Replay"), delegate { StartReplay(); }, true);
            }
            AddButton(row, Text("detail-report.txt", "Open the report"), delegate { OpenPath(current.ReportPath, hasReport); }, false);
            AddButton(row, Text("detail-ai.txt", "Open the two files for an assistant"), delegate { OpenPath(current.AiFolder, hasAi); }, false);
            AddButton(row, Text("detail-folder.txt", "Session folder"), delegate { OpenPath(current.Folder, true); }, false);
            AddButton(row, Text("detail-rebuild.txt", "Build the outputs again"), delegate { BuildOutputs(current, true); }, false);
            AddButton(row, Text("detail-export.txt", "Copy the outputs elsewhere"), delegate { ExportElsewhere(); }, false);

            StackPanel stack = new StackPanel();
            stack.Children.Add(row);
            if (!hasReport || !hasAi)
            {
                TextBlock warn = Note(Text("detail-missing.txt", "Some outputs are not on disk for this session. Use \"Build the outputs again\"."));
                warn.Foreground = Theme.CautionText;
                stack.Children.Add(warn);
            }
            return stack;
        }

        private UIElement StepsCard()
        {
            StackPanel body = new StackPanel();
            ListBox list = new ListBox();
            list.SetResourceReference(StyleProperty, "AppListBox");
            list.MaxHeight = 320;
            for (int index = 0; index < current.Steps.Count; index++)
            {
                StepRecord step = current.Steps[index];
                ListBoxItem item = new ListBoxItem();
                item.SetResourceReference(StyleProperty, "AppListItem");
                TextBlock text = new TextBlock();
                text.TextWrapping = TextWrapping.Wrap;
                text.Text = step.StepId + "  " + StepLabel(step) +
                    (step.AppName == null ? "" : "   [" + step.AppName + "]") +
                    (step.LastReplay == null ? "" : "   -> " + step.LastReplay.State);
                text.FontSize = Theme.MetaSize;
                item.Content = text;
                list.Items.Add(item);
            }
            body.Children.Add(list);
            TextBlock summary;
            StackPanel outer = new StackPanel();
            outer.Children.Add(Heading(Text("detail-steps.txt", "What was done")));
            Expander fold = Accordion(Text("detail-steps-fold.txt", "The recorded actions"), body, out summary);
            summary.Text = current.Steps.Count + " " + Text("list-steps.txt", "actions");
            fold.IsExpanded = true;
            outer.Children.Add(fold);
            return Card(outer);
        }

        private UIElement ScreensCard()
        {
            StackPanel body = new StackPanel();
            int missing = 0;
            for (int index = 0; index < current.Screens.Screens.Count; index++)
            {
                ScreenRecord screen = current.Screens.Screens[index];
                if (!screen.HasShot) missing++;
                TextBlock line = new TextBlock();
                line.TextWrapping = TextWrapping.Wrap;
                line.FontSize = Theme.MetaSize;
                line.Margin = new Thickness(0, 2, 0, 2);
                line.Text = screen.ScreenId + "  " + (String.IsNullOrEmpty(screen.Title) ? "(" + screen.ClassName + ")" : screen.Title) +
                    "   " + screen.Size + "   " + screen.ComponentIds.Count + " " + Text("list-elements.txt", "elements") +
                    (screen.HasShot ? "" : "   " + Text("screen-noshot.txt", "no picture") + ": " + screen.ShotProblem);
                line.Foreground = screen.HasShot ? Theme.TextSub : Theme.CautionText;
                body.Children.Add(line);
            }
            TextBlock summary;
            StackPanel outer = new StackPanel();
            outer.Children.Add(Heading(Text("detail-screens.txt", "Screens")));
            Expander fold = Accordion(Text("detail-screens-fold.txt", "Every screen in this session"), body, out summary);
            summary.Text = current.Screens.Screens.Count + " / " + Text("screen-noshot.txt", "no picture") + " " + missing;
            outer.Children.Add(fold);
            return Card(outer);
        }

        private UIElement LimitsCard()
        {
            StackPanel body = new StackPanel();
            if (current.Limits.Count == 0)
            {
                body.Children.Add(Note(Text("limits-none.txt", "No layer reported a limit. That is not a proof of completeness.")));
            }
            else
            {
                for (int index = 0; index < current.Limits.Count; index++)
                {
                    TextBlock line = Note(current.Limits[index]);
                    line.Foreground = Theme.CautionText;
                    body.Children.Add(line);
                }
            }
            TextBlock summary;
            StackPanel outer = new StackPanel();
            outer.Children.Add(Heading(Text("detail-limits.txt", "What could not be obtained")));
            Expander fold = Accordion(Text("detail-limits-fold.txt", "Stated limits and omissions"), body, out summary);
            summary.Text = current.Limits.Count.ToString(CultureInfo.InvariantCulture);
            outer.Children.Add(fold);
            return Card(outer);
        }

        private UIElement Settings()
        {
            TextBlock summary;
            StackPanel outer = new StackPanel();
            outer.Children.Add(Heading(Text("settings-title.txt", "Detailed settings")));
            Expander fold = Accordion(Text("settings-fold.txt", "Routes, privacy, budget and diagnostics"), SettingsBody(), out summary);
            summary.Text = routeMode + " / " + (valuePolicy == Privacy.PolicyLengthOnly ? Text("value-length-short.txt", "length only") : Text("value-record-short.txt", "text recorded")) +
                " / " + pdfBudgetKb + " KB";
            outer.Children.Add(fold);
            return Card(outer);
        }

        private UIElement SettingsBody()
        {
            StackPanel body = new StackPanel();

            body.Children.Add(FieldLabel(Text("settings-route.txt", "Route used when replaying")));
            ComboBox routes = new ComboBox();
            routes.SetResourceReference(StyleProperty, "AppComboBox");
            AddRoute(routes, ProbeRoutes.Auto, Text("route-auto.txt", "Auto - UIA, then a window message, then synthetic input"));
            AddRoute(routes, ProbeRoutes.UiaOnly, Text("route-uia.txt", "UI Automation only"));
            AddRoute(routes, ProbeRoutes.Win32Only, Text("route-win32.txt", "Window messages only"));
            AddRoute(routes, ProbeRoutes.SendInputOnly, Text("route-input.txt", "Synthetic input only"));
            routes.SelectedIndex = IndexOfRoute(routeMode);
            routes.SelectionChanged += delegate
            {
                ComboBoxItem item = routes.SelectedItem as ComboBoxItem;
                if (item != null) routeMode = Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
            };
            body.Children.Add(routes);
            body.Children.Add(Note(Text("settings-route-note.txt", "MSAA is an acquisition layer only. It is never used to carry an operation out, so it is not offered here.")));

            body.Children.Add(FieldLabel(Text("settings-value.txt", "What to write down when text is entered")));
            ComboBox values = new ComboBox();
            values.SetResourceReference(StyleProperty, "AppComboBox");
            AddValue(values, Privacy.PolicyRecordText, Text("value-record.txt", "Record the text so the recording can be replayed"));
            AddValue(values, Privacy.PolicyLengthOnly, Text("value-length.txt", "Record the length only; ask on replay"));
            values.SelectedIndex = valuePolicy == Privacy.PolicyLengthOnly ? 1 : 0;
            values.SelectionChanged += delegate
            {
                ComboBoxItem item = values.SelectedItem as ComboBoxItem;
                if (item != null) valuePolicy = Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
            };
            body.Children.Add(values);
            body.Children.Add(Note(Privacy.PolicyStatement(valuePolicy)));

            body.Children.Add(FieldLabel(Text("settings-budget.txt", "Size budget for screens.pdf (KB)")));
            TextBox budget = new TextBox();
            budget.SetResourceReference(StyleProperty, "AppTextBox");
            budget.Text = pdfBudgetKb.ToString(CultureInfo.InvariantCulture);
            budget.Width = 140;
            budget.HorizontalAlignment = HorizontalAlignment.Left;
            budget.TextChanged += delegate
            {
                int parsed;
                if (Int32.TryParse(budget.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 256) pdfBudgetKb = parsed;
            };
            body.Children.Add(budget);
            body.Children.Add(Note(Text("settings-budget-note.txt", "Pictures are reduced in stated steps, and screens are only dropped when reduction is exhausted. Every reduction and omission is written into the outputs.")));

            writeSwitch = PermissionSwitch(Text("settings-write.txt", "Let replay act on the real application"),
                Text("settings-write-note.txt", "Replay presses buttons and types into the applications on this machine."));
            // The answer lives on the window. Rebuilding this view used to make a
            // fresh unticked box, which silently withdrew a permission the
            // operator had already given.
            writeSwitch.IsChecked = writeEnabled;
            writeSwitch.Checked += delegate { writeEnabled = true; };
            writeSwitch.Unchecked += delegate { writeEnabled = false; };
            body.Children.Add(PermissionBox(writeSwitch, Text("settings-write-note.txt", "Replay presses buttons and types into the applications on this machine.")));

            if (!String.IsNullOrEmpty(hotkeyNotice))
            {
                body.Children.Add(FieldLabel(Text("settings-hotkeys.txt", "Global keys")));
                TextBlock keys = Note(hotkeyNotice);
                keys.Foreground = Theme.CautionText;
                body.Children.Add(keys);
            }
            body.Children.Add(FieldLabel(Text("settings-diagnostics.txt", "Startup diagnostics")));
            TextBox diag = new TextBox();
            diag.SetResourceReference(StyleProperty, "AppReadOnlyText");
            diag.IsReadOnly = true;
            diag.AcceptsReturn = true;
            diag.TextWrapping = TextWrapping.Wrap;
            diag.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            diag.MaxHeight = 180;
            diag.Text = diagnostics == null ? "-" : JsonWriter.Write(diagnostics);
            body.Children.Add(diag);

            return body;
        }

        // The same settings the result window folds away, shown as a dialog so
        // the small launcher does not have to carry them.
        private void ShowOptionsDialog()
        {
            Window dialog = new Window();
            dialog.Title = Text("settings-title.txt", "Detailed settings");
            dialog.Owner = this;
            dialog.Width = 620;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.MaxHeight = 560;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.Background = Theme.Surface;
            dialog.FontFamily = Theme.UiFont;
            Theme.Install(dialog.Resources);
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.Margin = new Thickness(Theme.Space5);
            StackPanel stack = new StackPanel();
            stack.Children.Add(SettingsBody());
            Button close = new Button();
            close.Content = Text("settings-close.txt", "Close");
            close.SetResourceReference(StyleProperty, "AppButtonPrimary");
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, Theme.Space5, 0, 0);
            close.Click += delegate { dialog.Close(); };
            stack.Children.Add(close);
            scroll.Content = stack;
            dialog.Content = scroll;
            dialog.ShowDialog();
        }

        private static void AddRoute(ComboBox box, string mode, string label)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = label;
            item.Tag = mode;
            box.Items.Add(item);
        }

        private static void AddValue(ComboBox box, string policy, string label)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = label;
            item.Tag = policy;
            box.Items.Add(item);
        }

        private static int IndexOfRoute(string mode)
        {
            if (mode == ProbeRoutes.UiaOnly) return 1;
            if (mode == ProbeRoutes.Win32Only) return 2;
            if (mode == ProbeRoutes.SendInputOnly) return 3;
            return 0;
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
                                Say(Text("busy-snap.txt", "Acquiring the window...") + "  " + value.NodeCount + " " + Text("list-elements.txt", "elements"), null);
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
                    // The acquisition raised the target to photograph it, so the
                    // front window is somebody else's. Take it back before the
                    // result appears, and show the result in the window shape
                    // meant for reading it.
                    GoResult();
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
            StudioSession session = SessionStore.Create(baseDir, StudioSession.KindRecord, Text("record-title.txt", "Recording"));
            session.Environment = diagnostics;
            session.ValuePolicy = valuePolicy;
            SessionStore.WriteMeta(session);
            current = session;

            hud = new RecordHud(Text("hud-recording.txt", "Recording"), Text("hud-stop.txt", "Stop"));
            hud.StopRequested += delegate { StopRecord(); };
            hud.ShowControl();
            recorder = new Recorder(baseDir, session, hud, Dispatcher);
            recorder.SetExcludedHandles(hud.OwnHandles);
            recorder.Progress += OnRecorderProgress;
            busy = true;
            busyLabel = Text("busy-record.txt", "Recording");
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
            try
            {
                finishing.Stop();
            }
            catch (Exception exception)
            {
                if (session != null) session.AddDiagnostic("RECORD-STOP: " + exception.Message);
            }
            finishing.Dispose();
            if (hud != null)
            {
                hud.Dispose();
                hud = null;
            }
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
                    GoResult();
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
                // A one line note at the bottom of the window is not an answer to
                // a button press: pressing replay and seeing nothing happen reads
                // as a broken product. Ask plainly, and let the answer be given
                // here rather than hunted for in a fold.
                Confirm prompt = new Confirm(
                    Text("replay-consent-title.txt", "Replay drives the real application"),
                    Text("replay-consent-body.txt", "Replay presses buttons and types into the applications on this machine."),
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
            hud = new RecordHud(Text("hud-replaying.txt", "Replaying"), Text("hud-stop.txt", "Stop"));
            hud.StopRequested += delegate { if (replay != null) replay.Cancel(); };
            hud.ShowControl();
            busy = true;
            busyLabel = Text("busy-replay.txt", "Replaying");
            busySince = DateTime.UtcNow;
            Hide();
            string mode = routeMode;
            replay.Progress += delegate(ReplayProgress value)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (hud != null && value.Headline != null) hud.SetClock(Text("hud-replaying.txt", "Replaying") + " " + value.Index + "/" + value.Total, DateTime.UtcNow - busySince);
                }));
            };
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                ReplayReport report = null;
                string problem = null;
                try
                {
                    report = replay.Run(mode, true);
                }
                catch (Exception exception)
                {
                    problem = exception.GetType().Name + ": " + exception.Message;
                }
                OutputSet outputs = Outputs.WriteAll(session, pdfBudgetKb * 1024);
                ReplayReport finished = report;
                string failure = problem;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (hud != null)
                    {
                        hud.Dispose();
                        hud = null;
                    }
                    if (replay != null)
                    {
                        replay.Dispose();
                        replay = null;
                    }
                    busy = false;
                    Idle();
                    GoResult();
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

        // A value the recording deliberately never kept. Asked for on the UI
        // thread, in a window that does not echo what is typed anywhere else and
        // never writes it down.
        private string AskSecret(StepRecord step)
        {
            string answer = null;
            Dispatcher.Invoke(new Action(delegate
            {
                SecretPrompt prompt = new SecretPrompt(
                    Text("secret-title.txt", "A value is needed"),
                    Text("secret-note.txt", "This step enters something the recording deliberately did not keep.") + "\n" +
                        step.StepId + "  " + step.Headline,
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
                    // The detail was drawn before these files existed, so it is
                    // still saying they are missing. Redraw it against what is
                    // now on disk rather than leaving the two contradicting
                    // each other on screen.
                    ShowDetail();
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

        private void ExportElsewhere()
        {
            if (current == null) return;
            System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog();
            dialog.Description = Text("export-pick.txt", "Choose where to copy the outputs.");
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string problem = Outputs.CopyTo(current, dialog.SelectedPath);
            if (problem == null) Say(Text("export-done.txt", "The outputs were copied."), "Success");
            else Say(Text("export-failed.txt", "The copy failed") + ": " + problem, "Danger");
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
            try
            {
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception exception)
            {
                Say(Text("open-failed.txt", "It could not be opened") + ": " + exception.Message, "Danger");
            }
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
            busyLabel = label;
            busySince = DateTime.UtcNow;
            progress.IsIndeterminate = true;
            snapButton.IsEnabled = false;
            recordButton.IsEnabled = false;
            Say(label, null);
        }

        private void Idle()
        {
            busy = false;
            busyLabel = null;
            progress.IsIndeterminate = false;
            progress.Value = 0;
            snapButton.IsEnabled = true;
            recordButton.IsEnabled = true;
        }

        private void SelectById(string id)
        {
            for (int index = 0; index < sessionList.Items.Count; index++)
            {
                ListBoxItem item = sessionList.Items[index] as ListBoxItem;
                StudioSession session = item == null ? null : item.Tag as StudioSession;
                if (session != null && session.Id == id)
                {
                    sessionList.SelectedIndex = index;
                    return;
                }
            }
        }

        // After a snap, a recording or a replay this window has deliberately been
        // hidden or pushed behind the target. Coming back is part of the job:
        // the operator asked this product for something and has to be able to
        // carry on with it without hunting for the window in the task bar.
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
            if (snapButton != null && snapButton.IsEnabled) snapButton.Focus();
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
            if (recorder != null)
            {
                recorder.Stop();
                recorder.Dispose();
                recorder = null;
            }
            if (replay != null)
            {
                replay.Cancel();
                replay.Dispose();
                replay = null;
            }
            if (hud != null)
            {
                hud.Dispose();
                hud = null;
            }
            if (hotkeys != null)
            {
                hotkeys.Dispose();
                hotkeys = null;
            }
        }

        private string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        // What a step reads like on screen. The written outputs keep their own
        // English wording, because they travel to an assistant; this is the one
        // the operator reads, so it is worded like the rest of the window.
        private string StepLabel(StepRecord step)
        {
            if (step == null) return "-";
            string subject = step.ElementLabel;
            if (String.IsNullOrEmpty(subject)) subject = step.Name;
            if (String.IsNullOrEmpty(subject)) subject = step.ControlType;
            if (String.IsNullOrEmpty(subject)) subject = Text("step-unknown.txt", "(unidentified element)");
            if (step.Kind == StepRecord.KindAppSwitch) return Text("step-appswitch.txt", "switch to") + " " + (step.WindowTitle == null ? Value(step.AppName) : step.WindowTitle);
            if (step.Kind == StepRecord.KindKeyChord) return Text("step-keychord.txt", "key") + " " + Value(step.KeyChord);
            if (step.Kind == StepRecord.KindTextInput) return Text("step-textinput.txt", "type into") + " " + subject;
            if (step.Kind == StepRecord.KindSecretInput) return Text("step-secretinput.txt", "secret entry into") + " " + subject;
            return Text("step-click.txt", "click") + " " + subject;
        }

        private static string Value(string value)
        {
            return String.IsNullOrEmpty(value) ? "-" : value;
        }

        // ---------- shared shapes ----------

        private static RowDefinition Row(GridLength height)
        {
            RowDefinition row = new RowDefinition();
            row.Height = height;
            return row;
        }

        private static ColumnDefinition Column(GridLength width)
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = width;
            return column;
        }

        private static TextBlock Heading(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.SectionSize;
            block.FontWeight = FontWeights.Bold;
            block.Foreground = Theme.Text;
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, 0, 0, Theme.Space1);
            return block;
        }

        private static TextBlock FieldLabel(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.LabelSize;
            block.FontWeight = FontWeights.SemiBold;
            block.Foreground = Theme.TextSub;
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, Theme.Space4, 0, Theme.Space1);
            return block;
        }

        private static TextBlock Body(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.BodySize;
            block.Foreground = Theme.TextSub;
            block.LineHeight = Theme.BodySize * Theme.BodyLine;
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, Theme.Space1, 0, 0);
            return block;
        }

        private static TextBlock Note(string text)
        {
            TextBlock block = Body(text);
            block.FontSize = Theme.MetaSize;
            block.LineHeight = Theme.MetaSize * Theme.BodyLine;
            block.Foreground = Theme.TextMuted;
            return block;
        }

        private static Border Card(UIElement content)
        {
            Border card = new Border();
            card.Background = Theme.Surface;
            card.BorderBrush = Theme.Border;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.Padding = new Thickness(Theme.Space5);
            card.Margin = new Thickness(0, 0, 0, Theme.Space4);
            card.Child = content;
            return card;
        }

        private Expander Accordion(string caption, UIElement content, out TextBlock summary)
        {
            TextBlock captionBlock = new TextBlock();
            captionBlock.Text = caption;
            captionBlock.FontSize = Theme.LabelSize;
            captionBlock.FontWeight = FontWeights.SemiBold;
            captionBlock.Foreground = Theme.TextSub;
            captionBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            captionBlock.VerticalAlignment = VerticalAlignment.Center;

            summary = new TextBlock();
            summary.FontSize = Theme.MetaSize;
            summary.Foreground = Theme.TextMuted;
            summary.VerticalAlignment = VerticalAlignment.Center;
            summary.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            summary.HorizontalAlignment = HorizontalAlignment.Right;

            DockPanel header = new DockPanel();
            header.LastChildFill = true;
            DockPanel.SetDock(summary, Dock.Right);
            header.Children.Add(summary);
            header.Children.Add(captionBlock);

            Expander expander = new Expander();
            expander.SetResourceReference(StyleProperty, "AppAccordion");
            expander.Header = header;
            expander.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(expander, caption);
            return expander;
        }

        private CheckBox PermissionSwitch(string label, string accessibleNote)
        {
            CheckBox box = new CheckBox();
            box.SetResourceReference(StyleProperty, "AppCheckBox");
            box.Content = label;
            box.FontWeight = FontWeights.SemiBold;
            System.Windows.Automation.AutomationProperties.SetHelpText(box, accessibleNote);
            return box;
        }

        private static Border PermissionBox(CheckBox box, string note)
        {
            StackPanel stack = new StackPanel();
            stack.Children.Add(box);
            TextBlock warning = Note(note);
            warning.Margin = new Thickness(26, Theme.Space1, 0, 0);
            stack.Children.Add(warning);
            Border frame = new Border();
            frame.CornerRadius = new CornerRadius(Theme.RadiusSm);
            frame.BorderThickness = new Thickness(1);
            frame.BorderBrush = Theme.Caution;
            frame.Background = Theme.CautionSoft;
            frame.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            frame.Margin = new Thickness(0, Theme.Space4, 0, 0);
            frame.Child = stack;
            return frame;
        }

        private static void AddStat(Panel panel, int value, string label)
        {
            TextBlock number = new TextBlock();
            number.Text = value.ToString(CultureInfo.InvariantCulture);
            number.FontSize = Theme.NumSize;
            number.FontWeight = FontWeights.Bold;
            number.Foreground = Theme.Text;
            StackPanel stack = new StackPanel();
            stack.Children.Add(number);
            TextBlock caption = new TextBlock();
            caption.Text = label;
            caption.FontSize = Theme.MicroSize;
            caption.FontWeight = FontWeights.SemiBold;
            caption.Foreground = Theme.TextMuted;
            caption.Margin = new Thickness(0, 1, 0, 0);
            stack.Children.Add(caption);
            Border card = new Border();
            card.MinWidth = 104;
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.Background = Theme.SurfaceSunken;
            card.BorderBrush = Theme.BorderSubtle;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            card.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            card.Child = stack;
            panel.Children.Add(card);
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
                badge.Background = Theme.DangerSoft;
                badge.BorderBrush = Theme.Danger;
                label.Foreground = Theme.DangerText;
            }
            else if (String.Equals(tone, "Caution", StringComparison.Ordinal))
            {
                badge.Background = Theme.CautionSoft;
                badge.BorderBrush = Theme.Caution;
                label.Foreground = Theme.CautionText;
            }
            else if (String.Equals(tone, "Success", StringComparison.Ordinal))
            {
                badge.Background = Theme.SuccessSoft;
                badge.BorderBrush = Theme.Success;
                label.Foreground = Theme.SuccessText;
            }
            else
            {
                badge.Background = Theme.AccentSoft;
                badge.BorderBrush = Theme.Accent;
                label.Foreground = Theme.AccentText;
            }
        }

        private static Button AddButton(Panel panel, string label, RoutedEventHandler handler, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            button.SetResourceReference(StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            if (handler != null) button.Click += handler;
            panel.Children.Add(button);
            return button;
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

            TextBlock number = new TextBlock();
            number.FontFamily = Theme.UiFont;
            number.FontSize = 92;
            number.FontWeight = FontWeights.Bold;
            number.Foreground = Brushes.White;
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.Text = seconds.ToString(CultureInfo.InvariantCulture);

            TextBlock hint = new TextBlock();
            hint.FontFamily = Theme.UiFont;
            hint.FontSize = Theme.LabelSize;
            hint.Foreground = Brushes.White;
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, Theme.Space2, 0, 0);
            hint.Text = Messages.Text("countdown-hint.txt", "Escape cancels");

            StackPanel stack = new StackPanel();
            stack.Children.Add(number);
            stack.Children.Add(hint);

            Border shell = new Border();
            shell.Background = new SolidColorBrush(Color.FromArgb(238, 16, 22, 30));
            shell.BorderBrush = new SolidColorBrush(Theme.Parse("#3AA0FF"));
            shell.BorderThickness = new Thickness(1);
            shell.CornerRadius = new CornerRadius(Theme.RadiusLg);
            shell.Padding = new Thickness(56, 36, 56, 28);
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
