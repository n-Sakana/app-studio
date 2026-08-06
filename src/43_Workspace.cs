namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    // The body of the full window: three panes over one session.
    //
    // There is no longer a home, a guide, a record viewer and a code editor to
    // move between. There is one working state - a session - and two ways of
    // looking at it: this, and the small bar. Everything that used to be its own
    // screen is a pane here, so choosing a different session changes what all
    // three panes hold rather than taking the operator somewhere else.
    //
    // The modules are on the left because that is where a project tree is. The
    // file being edited is in the middle because editing is what this window is
    // mostly for. The workflow and the assistant share the right, as two tabs,
    // because they are two things you do *to* the session rather than two places
    // to be. Either side can be folded away and the middle takes the room.
    //
    // Operations sit with what they act on. Replay is beside the workflow it
    // replays and build is beside the code it builds; neither is on the top bar,
    // which carries only the things that act on the whole window.
    public sealed class Workspace
    {
        public const string TabWorkflow = "workflow";
        public const string TabAssistant = "assistant";

        private readonly Window owner;
        private readonly Action<string, string> say;

        private readonly CodeEditor editor = new CodeEditor();
        private readonly TreeView tree = new TreeView();
        private readonly TextBox requestBox = new TextBox();
        private readonly TextBlock stateLine = new TextBlock();
        private readonly TextBlock moduleLine = new TextBlock();
        private readonly TextBlock moduleNote = new TextBlock();
        private readonly TextBlock buildLine = new TextBlock();
        private readonly TextBlock aiLine = new TextBlock();
        private readonly StackPanel workflowBody = new StackPanel();
        private readonly StackPanel aiBody = new StackPanel();
        private readonly StackPanel diffBody = new StackPanel();
        private readonly Border stateChip;

        // The two faces of the middle pane. Nothing is rebuilt when they swap:
        // the editor keeps its text, its caret and its scroll, so reading a
        // difference and going back is not a way to lose what was typed.
        private UIElement leftPane;
        private UIElement rightPane;
        private UIElement leftRail;
        private UIElement rightRail;
        private ColumnDefinition leftColumn;
        private ColumnDefinition rightColumn;
        private GridSplitter leftSplitter;
        private GridSplitter rightSplitter;
        private Button fullButton;
        private Button launchButton;
        private System.Windows.Controls.Primitives.ToggleButton wrapToggle;
        private System.Windows.Controls.Primitives.ToggleButton workflowTab;
        private System.Windows.Controls.Primitives.ToggleButton assistantTab;
        private Grid root;

        private StudioSession session;
        private CodeProject project;
        private HandoffResult handoff;
        private bool copied;
        private IntakeParts parts = new IntakeParts();
        private List<IntakeFile> pending;
        private List<FileDiff> pendingDiff;
        private string currentFile = CodeModules.Workflow;
        private string currentLanguage = ScriptLanguages.PowerShell;
        private bool loading;
        private bool ready;
        private bool leftOpen = true;
        private bool rightOpen = true;
        private bool diffShowing;
        private string tab = TabWorkflow;
        private string builtPath;
        private Button copyButton;

        // What the window around this has to know, and what it lends back.
        public Func<bool> AskRunConsent;
        public Action StartReplay;
        public Action OpenReport;
        public Func<int> PdfBudgetBytes;
        public Func<double> ReplaySpeed;
        public Action<double> SetReplaySpeed;
        public Action LayoutChanged;

        public Workspace(Window ownerWindow, Action<string, string> status)
        {
            owner = ownerWindow;
            say = status;
            stateChip = Chip(stateLine);
            editor.Changed = delegate
            {
                if (loading) return;
                Remember();
                PaintState();
            };
        }

        public bool HasSession { get { return session != null && project != null; } }
        public string CurrentTab { get { return tab; } }
        public bool LeftOpen { get { return leftOpen; } }
        public bool RightOpen { get { return rightOpen; } }
        public bool DiffShowing { get { return diffShowing; } }

        private static string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        private void Say(string message, string tone)
        {
            if (say != null) say(message, tone);
        }

        // ---------- assembly ----------

        // Built once. Folding a pane away changes a column width and what one
        // holder contains; it never rebuilds the tree, the editor or the
        // difference, because all three carry state a rebuild would drop.
        public UIElement Build()
        {
            if (root != null)
            {
                Detach(root);
                return root;
            }
            root = new Grid();
            root.Margin = new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space3);
            leftColumn = Ui.ShareColumn(Theme.PaneLeftShare);
            leftColumn.MinWidth = Theme.PaneLeftMin;
            ColumnDefinition centreColumn = Ui.ShareColumn(Theme.PaneCentreShare);
            centreColumn.MinWidth = Theme.PaneCentreMin;
            rightColumn = Ui.ShareColumn(Theme.PaneRightShare);
            rightColumn.MinWidth = Theme.PaneRightMin;
            root.ColumnDefinitions.Add(leftColumn);
            root.ColumnDefinitions.Add(Ui.FixedColumn(Theme.SplitterWidth));
            root.ColumnDefinitions.Add(centreColumn);
            root.ColumnDefinitions.Add(Ui.FixedColumn(Theme.SplitterWidth));
            root.ColumnDefinitions.Add(rightColumn);

            leftPane = ModulePane();
            leftRail = Rail(true);
            Grid left = Host(leftPane, leftRail);
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            leftSplitter = Splitter();
            Grid.SetColumn(leftSplitter, 1);
            root.Children.Add(leftSplitter);

            UIElement centre = CentrePane();
            Grid.SetColumn(centre, 2);
            root.Children.Add(centre);

            rightSplitter = Splitter();
            Grid.SetColumn(rightSplitter, 3);
            root.Children.Add(rightSplitter);

            rightPane = RightPane();
            rightRail = Rail(false);
            Grid right = Host(rightPane, rightRail);
            Grid.SetColumn(right, 4);
            root.Children.Add(right);

            ApplyPanes();
            PaintSession();
            return root;
        }

        private GridSplitter Splitter()
        {
            GridSplitter splitter = new GridSplitter();
            splitter.SetResourceReference(FrameworkElement.StyleProperty, "AppSplitter");
            splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            Ui.Name(splitter, Text("pane-resize.txt", "Pane boundary"),
                Text("pane-resize-note.txt", "Drag sideways to change how wide the panes on either side are."));
            return splitter;
        }

        private static void Detach(UIElement child)
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

        // The pane and the rail that brings it back share one cell and are both
        // always in the tree. Only which of the two is visible changes, because
        // taking the pane out of the tree would take the module list, the editor
        // and any difference waiting to be read out with it.
        private static Grid Host(UIElement pane, UIElement rail)
        {
            Grid host = new Grid();
            host.Children.Add(pane);
            host.Children.Add(rail);
            return host;
        }

        // Folding a pane leaves a rail with the drawing that brings it back, so
        // nothing becomes unreachable and the operator can see that something is
        // folded rather than that something is missing.
        private void ApplyPanes()
        {
            if (root == null) return;
            leftColumn.Width = leftOpen ? new GridLength(Theme.PaneLeftShare, GridUnitType.Star) : new GridLength(Theme.PaneRailWidth);
            leftColumn.MinWidth = leftOpen ? Theme.PaneLeftMin : Theme.PaneRailWidth;
            rightColumn.Width = rightOpen ? new GridLength(Theme.PaneRightShare, GridUnitType.Star) : new GridLength(Theme.PaneRailWidth);
            rightColumn.MinWidth = rightOpen ? Theme.PaneRightMin : Theme.PaneRailWidth;
            leftSplitter.Visibility = leftOpen ? Visibility.Visible : Visibility.Hidden;
            rightSplitter.Visibility = rightOpen ? Visibility.Visible : Visibility.Hidden;
            leftPane.Visibility = leftOpen ? Visibility.Visible : Visibility.Collapsed;
            leftRail.Visibility = leftOpen ? Visibility.Collapsed : Visibility.Visible;
            rightPane.Visibility = rightOpen ? Visibility.Visible : Visibility.Collapsed;
            rightRail.Visibility = rightOpen ? Visibility.Collapsed : Visibility.Visible;
            PaintFullButton();
            if (LayoutChanged != null) LayoutChanged();
        }

        private UIElement Rail(bool left)
        {
            Border rail = new Border();
            rail.Background = Theme.Surface;
            rail.BorderBrush = Theme.Border;
            rail.BorderThickness = new Thickness(1);
            rail.CornerRadius = new CornerRadius(Theme.RadiusMd);
            Button show = Ui.IconButton(left ? Icons.ChevronRight : Icons.ChevronLeft,
                left ? Text("pane-modules-show.txt", "Open the modules") : Text("pane-right-show.txt", "Open the workflow and the assistant"),
                Text("pane-show-note.txt", "Puts a folded pane back at its own width."),
                left ? new Action(delegate { leftOpen = true; ApplyPanes(); })
                     : new Action(delegate { rightOpen = true; ApplyPanes(); }));
            show.VerticalAlignment = VerticalAlignment.Top;
            show.Margin = new Thickness(0, Theme.Space2, 0, 0);
            show.Width = Theme.PaneRailWidth - 2;
            rail.Child = show;
            return rail;
        }

        public void SetLeftOpen(bool value) { leftOpen = value; ApplyPanes(); }
        public void SetRightOpen(bool value) { rightOpen = value; ApplyPanes(); }

        // The middle pane taking the whole window is the two side panes folded,
        // not a third layout. So it cannot lose anything, and coming back out of
        // it puts both panes back the way they were.
        private void ToggleFull()
        {
            bool full = !leftOpen && !rightOpen;
            leftOpen = full;
            rightOpen = full;
            ApplyPanes();
            editor.FocusEditor();
        }

        private void PaintFullButton()
        {
            if (fullButton == null) return;
            bool full = !leftOpen && !rightOpen;
            fullButton.Content = Icons.Make(full ? Icons.FullscreenExit : Icons.Fullscreen, 18, Theme.TextSub);
            Ui.Name(fullButton,
                full ? Text("code-full-exit.txt", "Leave full width") : Text("code-full-enter.txt", "Make the editor full width"),
                full ? Text("code-full-exit-note.txt", "Puts both side panes back.")
                     : Text("code-full-enter-note.txt", "Folds both side panes away and gives the editor the whole width."));
        }

        // ---------- the session this is all about ----------

        // Everything on all three panes is redrawn from one session. Choosing a
        // different one in the bar above therefore changes what is on screen
        // without moving the operator anywhere.
        public void SetSession(StudioSession studioSession, CodeProject codeProject)
        {
            Persist();
            session = studioSession;
            project = codeProject;
            handoff = null;
            copied = false;
            parts = new IntakeParts();
            pending = null;
            pendingDiff = null;
            builtPath = null;
            diffShowing = false;
            ready = false;
            currentLanguage = project == null ? ScriptLanguages.PowerShell : project.Language;
            currentFile = CodeModules.Workflow;
            PaintSession();
        }

        private void PaintSession()
        {
            if (root == null) return;
            PaintTree();
            LoadEditor();
            PaintWorkflow();
            PaintAssistant();
            PaintState();
            ShowEditorFace();
            buildLine.Text = "";
            if (launchButton != null) launchButton.IsEnabled = false;
        }

        public void Persist()
        {
            if (project == null) return;
            Remember();
            if (!ready) return;
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        private void Remember()
        {
            if (loading || !ready || project == null) return;
            if (IsWrapper(currentFile)) return;
            project.SetText(currentLanguage, currentFile, editor.Text);
        }

        // ---------- left: the modules ----------

        private UIElement ModulePane()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(Ui.AutoRow());
            pane.RowDefinitions.Add(Ui.StarRow());
            pane.RowDefinitions.Add(Ui.AutoRow());

            Grid head = Ui.PaneHead(Icons.Module, Text("code-modules.txt", "Modules"));
            Button fold = Ui.IconButton(Icons.ChevronLeft, Text("pane-modules-hide.txt", "Fold the modules away"),
                Text("pane-hide-note.txt", "Folds this pane away and gives the room to the editor."),
                delegate { leftOpen = false; ApplyPanes(); });
            Grid.SetColumn(fold, 2);
            head.Children.Add(fold);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            tree.SetResourceReference(FrameworkElement.StyleProperty, "AppTree");
            tree.FontSize = Theme.LabelSize;
            tree.Margin = new Thickness(Theme.Space2, 0, Theme.Space2, 0);
            System.Windows.Automation.AutomationProperties.SetName(tree, Text("code-modules.txt", "Modules"));

            // An empty pane reads as a product that failed to load. This one says
            // which of the two it is, and what would put something in it.
            moduleEmpty = Ui.Empty(Text("empty-modules.txt", "There is no code yet."),
                Text("empty-modules-note.txt", "Record or snap, and the modules written from that session are listed here."));
            Grid holder = new Grid();
            holder.Children.Add(tree);
            holder.Children.Add(moduleEmpty);
            Grid.SetRow(holder, 1);
            pane.Children.Add(holder);

            moduleFoot = new StackPanel();
            moduleFoot.Margin = new Thickness(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space3);
            moduleNote.FontSize = Theme.MicroSize;
            moduleNote.Foreground = Theme.TextMuted;
            moduleNote.TextWrapping = TextWrapping.Wrap;
            moduleFoot.Children.Add(moduleNote);
            Button baseline = Ui.IconTextButton(Icons.Refresh, Text("code-baseline.txt", "Back to the generated version"),
                Text("code-baseline-note.txt", "Goes back to the code as it was written from the recording."), delegate { Baseline(); }, false);
            baseline.HorizontalAlignment = HorizontalAlignment.Stretch;
            baseline.Margin = new Thickness(0, Theme.Space3, 0, 0);
            moduleFoot.Children.Add(baseline);
            Grid.SetRow(moduleFoot, 2);
            pane.Children.Add(moduleFoot);

            return Ui.Panel(pane);
        }

        // A tree with the whole of what was generated in it, grouped by what each
        // group is for.
        //
        // The C# modules are the automation and are the thing to edit, so they
        // are first, open, and said to be the main subject. The PowerShell
        // wrapper is in the list too - it is part of what gets built and leaving
        // it out made the built file look like it came from nowhere - but it is
        // marked as written by the build, because editing it would be editing
        // something that is regenerated on the next build. VBA is the same
        // automation spelled for Excel and is folded, because opening this
        // window to change VBA is the rarer of the two jobs.
        private UIElement moduleEmpty;
        private StackPanel moduleFoot;

        private void PaintTree()
        {
            loading = true;
            tree.Items.Clear();
            bool has = project != null;
            if (moduleEmpty != null) moduleEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            tree.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            // Nothing to go back to, so the way back is not offered. A control
            // that is on screen and does nothing is a control that has to be
            // pressed before it can be ruled out.
            if (moduleFoot != null) moduleFoot.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has)
            {
                loading = false;
                return;
            }
            TreeViewItem sharp = Group(Text("code-group-cs.txt", "C# (the main thing to edit)"),
                Text("code-group-cs-note.txt", "The automation itself. The recorded procedure is in Workflow.cs."), true);
            List<CodeFile> csharp = project.Files(ScriptLanguages.PowerShell);
            for (int index = 0; index < csharp.Count; index++) sharp.Items.Add(ModuleItem(csharp[index]));
            tree.Items.Add(sharp);

            TreeViewItem wrapper = Group(Text("code-group-wrapper.txt", "PowerShell wrapper (written by the build)"),
                Text("code-group-wrapper-note.txt", "A thin layer that compiles the C# and calls it. Rewritten on every build."), false);
            wrapper.Items.Add(WrapperItem());
            tree.Items.Add(wrapper);

            TreeViewItem vba = Group(Text("code-group-vba.txt", "VBA (the same procedure, for Excel)"),
                Text("code-group-vba-note.txt", "The same automation written as an Excel macro."), false);
            List<CodeFile> basic = project.Files(ScriptLanguages.Vba);
            for (int index = 0; index < basic.Count; index++) vba.Items.Add(ModuleItem(basic[index]));
            tree.Items.Add(vba);
            loading = false;
        }

        private TreeViewItem Group(string label, string note, bool open)
        {
            TreeViewItem item = new TreeViewItem();
            item.SetResourceReference(FrameworkElement.StyleProperty, "AppTreeItem");
            TextBlock name = new TextBlock();
            name.Text = label;
            name.FontSize = Theme.LabelSize;
            name.FontWeight = FontWeights.SemiBold;
            name.Foreground = Theme.TextSub;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Header = name;
            item.IsExpanded = open;
            Ui.Name(item, label, note);
            return item;
        }

        private TreeViewItem ModuleItem(CodeFile file)
        {
            TreeViewItem item = new TreeViewItem();
            item.SetResourceReference(FrameworkElement.StyleProperty, "AppTreeItem");
            TextBlock name = new TextBlock();
            name.Text = file.FileName;
            name.FontSize = Theme.LabelSize;
            name.Foreground = Theme.Text;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Header = name;
            System.Windows.Automation.AutomationProperties.SetName(item, file.FileName);
            string language = file.Language;
            string moduleName = file.Name;
            item.Selected += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                if (loading) return;
                Choose(language, moduleName);
            };
            if (String.Equals(file.Language, currentLanguage, StringComparison.Ordinal) &&
                String.Equals(file.Name, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
            }
            return item;
        }

        public const string WrapperName = "Wrapper";

        private static bool IsWrapper(string name)
        {
            return String.Equals(name, WrapperName, StringComparison.OrdinalIgnoreCase);
        }

        private TreeViewItem WrapperItem()
        {
            TreeViewItem item = new TreeViewItem();
            item.SetResourceReference(FrameworkElement.StyleProperty, "AppTreeItem");
            TextBlock name = new TextBlock();
            name.Text = "Wrapper.ps1";
            name.FontSize = Theme.LabelSize;
            name.Foreground = Theme.TextMuted;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            item.Header = name;
            System.Windows.Automation.AutomationProperties.SetName(item, "Wrapper.ps1");
            item.Selected += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                if (loading) return;
                Choose(ScriptLanguages.PowerShell, WrapperName);
            };
            return item;
        }

        private void Choose(string languageName, string name)
        {
            Remember();
            currentLanguage = languageName;
            currentFile = name;
            if (project != null && !IsWrapper(name)) project.Language = languageName;
            LoadEditor();
            PaintState();
            ShowEditorFace();
        }

        // ---------- centre: the file, the build, and the difference ----------

        private UIElement CentrePane()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(Ui.AutoRow());
            pane.RowDefinitions.Add(Ui.StarRow());
            pane.RowDefinitions.Add(Ui.AutoRow());
            pane.RowDefinitions[1].MinHeight = Theme.EditorMinHeight;

            Grid head = Ui.PaneHead(Icons.Module, Text("code-editor-pane.txt", "Code editor"));
            StackPanel headRight = new StackPanel();
            headRight.Orientation = Orientation.Horizontal;
            headRight.VerticalAlignment = VerticalAlignment.Center;
            wrapToggle = Ui.IconToggle(Icons.Wrap, Text("code-wrap.txt", "Wrap long lines"),
                Text("code-wrap-note.txt", "Wraps long lines at the right edge so nothing needs scrolling sideways."));
            wrapToggle.IsChecked = true;
            wrapToggle.Checked += delegate { editor.SetWrap(true); };
            wrapToggle.Unchecked += delegate { editor.SetWrap(false); };
            headRight.Children.Add(wrapToggle);
            fullButton = new Button();
            fullButton.SetResourceReference(FrameworkElement.StyleProperty, "AppIconButton");
            fullButton.Click += delegate { ToggleFull(); };
            headRight.Children.Add(fullButton);
            Grid.SetColumn(headRight, 2);
            head.Children.Add(headRight);

            StackPanel middle = new StackPanel();
            middle.VerticalAlignment = VerticalAlignment.Center;
            middle.Margin = new Thickness(Theme.Space4, 0, Theme.Space3, 0);
            moduleLine.FontSize = Theme.MetaSize;
            moduleLine.Foreground = Theme.TextMuted;
            moduleLine.TextTrimming = TextTrimming.CharacterEllipsis;
            middle.Children.Add(moduleLine);
            Grid.SetColumn(middle, 1);
            head.Children.Add(middle);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            centreFace = new ContentControl();
            centreFace.Margin = new Thickness(Theme.Space4, 0, Theme.Space4, 0);
            Grid.SetRow(centreFace, 1);
            pane.Children.Add(centreFace);

            Grid.SetRow(BuildStrip(), 2);
            pane.Children.Add(buildStrip);
            return Ui.Panel(pane);
        }

        private ContentControl centreFace;
        private Border buildStrip;
        private UIElement editorFace;

        // Building and what came of it are here, beside the code they are about.
        // They used to be reported in the assistant's column, which put the
        // result of a thing you did to the code three panes away from the code.
        private UIElement BuildStrip()
        {
            Grid strip = new Grid();
            strip.RowDefinitions.Add(Ui.AutoRow());
            strip.RowDefinitions.Add(Ui.AutoRow());

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 0, 0, Theme.Space2);
            row.Children.Add(Ui.IconTextButton(Icons.Check, Text("code-check.txt", "Check"),
                Text("code-check-note.txt", "Only checks that it compiles. Nothing is run."), delegate { Check(); }, false));
            Button build = Ui.IconTextButton(Icons.Build, Text("code-build.txt", "Build"),
                Text("code-build-note.txt", "Folds the modules into the single file somebody is given."), delegate { BuildIt(); }, true);
            build.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            row.Children.Add(build);
            Button run = Ui.IconTextButton(Icons.Play, Text("code-run.txt", "Run"),
                Text("code-run-note.txt", "Runs this automation against the real applications on this machine."), delegate { RunIt(); }, false);
            run.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            row.Children.Add(run);
            launchButton = Ui.IconTextButton(Icons.Launch, Text("code-launch.txt", "Start the built file"),
                Text("code-launch-note.txt", "Starts the built file the way the person handed it would start it."), delegate { LaunchBuilt(); }, false);
            launchButton.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            launchButton.IsEnabled = false;
            row.Children.Add(launchButton);
            Grid.SetRow(row, 0);
            strip.Children.Add(row);

            buildLine.FontSize = Theme.MetaSize;
            buildLine.LineHeight = Theme.MetaSize * Theme.BodyLine;
            buildLine.Foreground = Theme.TextMuted;
            buildLine.TextWrapping = TextWrapping.Wrap;
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.MaxHeight = 96;
            scroll.Content = buildLine;
            Grid.SetRow(scroll, 1);
            strip.Children.Add(scroll);

            buildStrip = new Border();
            buildStrip.BorderBrush = Theme.BorderSubtle;
            buildStrip.BorderThickness = new Thickness(0, 1, 0, 0);
            buildStrip.Padding = new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space3);
            buildStrip.Child = strip;
            return buildStrip;
        }

        private void ShowEditorFace()
        {
            diffShowing = false;
            if (centreFace == null) return;
            if (editorFace == null) editorFace = editor.Build();
            if (project == null)
            {
                centreFace.Content = Ui.Empty(
                    Text("empty-code.txt", "No code has been generated yet."),
                    Text("empty-code-note.txt", "Record or snap, and the code written from that session appears here."));
                buildStrip.Visibility = Visibility.Collapsed;
                return;
            }
            buildStrip.Visibility = Visibility.Visible;
            centreFace.Content = editorFace;
            if (LayoutChanged != null) LayoutChanged();
        }

        private void LoadEditor()
        {
            loading = true;
            if (project == null)
            {
                editor.Text = "";
                moduleLine.Text = "";
                loading = false;
                ready = false;
                return;
            }
            if (IsWrapper(currentFile))
            {
                // Shown, not edited. It is written by the build from the modules
                // beside it, so a change typed here would be gone the next time
                // anything is built - and a box that silently discards what is
                // typed into it is worse than one that says it is read only.
                editor.SetLanguage(ScriptLanguages.PowerShell);
                editor.Text = CodeBuild.Script(project.Files(ScriptLanguages.PowerShell));
                editor.SetReadOnly(true);
                moduleLine.Text = "Wrapper.ps1   " + Text("code-readonly.txt", "written by the build, so it cannot be edited here");
                loading = false;
                ready = false;
                return;
            }
            CodeFile file = project.Find(currentLanguage, currentFile);
            editor.SetReadOnly(false);
            editor.SetLanguage(currentLanguage);
            editor.Text = file == null ? "" : file.Text;
            moduleLine.Text = file == null ? Text("code-module-none.txt", "This module is not in the project.") : file.FileName;
            loading = false;
            ready = file != null;
        }

        // ---------- right: the workflow and the assistant ----------

        private UIElement RightPane()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(Ui.AutoRow());
            pane.RowDefinitions.Add(Ui.StarRow());

            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.AutoColumn());
            head.ColumnDefinitions.Add(Ui.StarColumn());
            head.ColumnDefinitions.Add(Ui.AutoColumn());
            head.Margin = new Thickness(Theme.Space3, 0, Theme.Space2, 0);

            StackPanel tabs = new StackPanel();
            tabs.Orientation = Orientation.Horizontal;
            workflowTab = Ui.Tab(Icons.Workflow, Text("tab-workflow.txt", "Workflow"),
                Text("tab-workflow-note.txt", "What this session holds, in what order it was done, and replay."));
            assistantTab = Ui.Tab(Icons.Assistant, Text("tab-assistant.txt", "Ask an assistant"),
                Text("tab-assistant-note.txt", "Copy the request, and read what comes back as a difference."));
            workflowTab.Checked += delegate { SetTab(TabWorkflow); };
            assistantTab.Checked += delegate { SetTab(TabAssistant); };
            workflowTab.Click += delegate { workflowTab.IsChecked = true; };
            assistantTab.Click += delegate { assistantTab.IsChecked = true; };
            tabs.Children.Add(workflowTab);
            tabs.Children.Add(assistantTab);
            Grid.SetColumn(tabs, 0);
            head.Children.Add(tabs);

            Button fold = Ui.IconButton(Icons.ChevronRight, Text("pane-right-hide.txt", "Fold this pane away"),
                Text("pane-hide-note.txt", "Folds this pane away and gives the room to the editor."),
                delegate { rightOpen = false; ApplyPanes(); });
            fold.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fold, 2);
            head.Children.Add(fold);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            rightFace = new ContentControl();
            Grid.SetRow(rightFace, 1);
            pane.Children.Add(rightFace);

            workflowTab.IsChecked = true;
            SetTab(TabWorkflow);
            return Ui.Panel(pane);
        }

        private ContentControl rightFace;

        private void SetTab(string which)
        {
            tab = which;
            bool workflow = String.Equals(which, TabWorkflow, StringComparison.Ordinal);
            if (workflowTab != null) workflowTab.IsChecked = workflow;
            if (assistantTab != null) assistantTab.IsChecked = !workflow;
            if (rightFace == null) return;
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Padding = new Thickness(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space4);
            Detach(workflowBody);
            Detach(aiBody);
            scroll.Content = workflow ? (UIElement)workflowBody : (UIElement)aiBody;
            rightFace.Content = scroll;
        }

        // What the session holds, what was done in it, and the two things you do
        // to a recording: play it back, and read the report.
        private void PaintWorkflow()
        {
            workflowBody.Children.Clear();
            if (session == null)
            {
                workflowBody.Children.Add(Ui.Empty(Text("empty-workflow.txt", "There is no session yet."),
                    Text("empty-workflow-note.txt", "Start with record or snap on the bar above.")));
                return;
            }
            SessionVerdict verdict = SessionVerdict.Of(session);

            WrapPanel stats = new WrapPanel();
            stats.Margin = new Thickness(0, 0, 0, Theme.Space3);
            if (verdict.IsRecording) Stat(stats, verdict.Steps, Text("stat-steps.txt", "actions"));
            Stat(stats, verdict.Screens, Text("stat-screens.txt", "screens"));
            Stat(stats, verdict.Elements, Text("stat-elements.txt", "elements"));
            Stat(stats, verdict.Limits, Text("stat-limits.txt", "What could not be obtained"));
            workflowBody.Children.Add(stats);

            StackPanel actions = new StackPanel();
            actions.Margin = new Thickness(0, 0, 0, Theme.Space3);
            Button replay = Ui.IconTextButton(Icons.Play, Text("detail-replay.txt", "Replay"),
                Text("detail-replay-note.txt", "Carries the recorded actions out again against what is on screen now."),
                delegate { if (StartReplay != null) StartReplay(); }, true);
            replay.HorizontalAlignment = HorizontalAlignment.Stretch;
            replay.IsEnabled = verdict.IsRecording && verdict.Steps > 0;
            actions.Children.Add(replay);
            workflowBody.Children.Add(actions);

            workflowBody.Children.Add(SpeedRow());

            Button report = Ui.IconTextButton(Icons.Report, Text("detail-report.txt", "Open the report"),
                Text("detail-report-note.txt", "Opens the detail of this session in a form a browser can read."),
                delegate { if (OpenReport != null) OpenReport(); }, false);
            report.HorizontalAlignment = HorizontalAlignment.Stretch;
            report.Margin = new Thickness(0, 0, 0, Theme.Space4);
            workflowBody.Children.Add(report);

            workflowBody.Children.Add(Ui.Label(Text("detail-steps.txt", "What was done")));
            workflowBody.Children.Add(StepList());

            if (session.Limits.Count > 0)
            {
                TextBlock limits = Ui.Label(Text("detail-limits.txt", "What could not be obtained"));
                limits.Margin = new Thickness(0, Theme.Space4, 0, 0);
                workflowBody.Children.Add(limits);
                for (int index = 0; index < session.Limits.Count; index++)
                {
                    TextBlock line = Ui.Note(session.Limits[index]);
                    line.Foreground = Theme.CautionText;
                    line.Margin = new Thickness(0, Theme.Space1, 0, 0);
                    workflowBody.Children.Add(line);
                }
            }
        }

        // The wait between steps, as a thing the operator sets rather than a
        // number compiled into the product.
        //
        // Replay is slower than the recording was on purpose: every step waits
        // for the recorded interval, then re-finds the element by meaning, then
        // waits for the application to stop changing before the next one starts.
        // That is what makes it reliable. It is also why a recording that took
        // ten seconds plays back in forty, so the amount of it is adjustable and
        // says what it costs.
        public UIElement SpeedRow()
        {
            StackPanel block = new StackPanel();
            block.Margin = new Thickness(0, 0, 0, Theme.Space4);
            Grid line = new Grid();
            line.ColumnDefinitions.Add(Ui.AutoColumn());
            line.ColumnDefinitions.Add(Ui.StarColumn());
            line.ColumnDefinitions.Add(Ui.AutoColumn());
            UIElement drawing = Icons.Make(Icons.Speed, 16, Theme.TextMuted);
            FrameworkElement framed = drawing as FrameworkElement;
            if (framed != null) framed.Margin = new Thickness(0, 0, Theme.Space2, 0);
            Grid.SetColumn(drawing, 0);
            line.Children.Add(drawing);
            TextBlock caption = Ui.Label(Text("replay-speed.txt", "Replay speed"));
            caption.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(caption, 1);
            line.Children.Add(caption);
            TextBlock value = new TextBlock();
            value.FontSize = Theme.MetaSize;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = Theme.Text;
            value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(value, 2);
            line.Children.Add(value);
            block.Children.Add(line);

            double now = ReplaySpeed == null ? 1.0 : ReplaySpeed();
            Slider slider = Ui.Speed(now, 0.5, 4.0, 0.25);
            slider.Margin = new Thickness(0, Theme.Space1, 0, 0);
            Ui.Name(slider, Text("replay-speed.txt", "Replay speed"),
                Text("replay-speed-note.txt", "1.0 keeps the recording own pace. Higher shortens the wait between steps."));
            value.Text = "x" + Ui.Seconds(now);
            slider.ValueChanged += delegate(object sender, RoutedPropertyChangedEventArgs<double> args)
            {
                value.Text = "x" + Ui.Seconds(args.NewValue);
                if (SetReplaySpeed != null) SetReplaySpeed(args.NewValue);
            };
            block.Children.Add(slider);
            block.Children.Add(Ui.Note(Text("replay-speed-hint.txt",
                "1.0 keeps the pace the recording was made at. Faster shortens every wait between steps, and misses more.")));
            return block;
        }

        private UIElement StepList()
        {
            StackPanel body = new StackPanel();
            if (session.Steps.Count == 0)
            {
                body.Children.Add(Ui.Note(Text("steps-none.txt", "This session has no recorded action.")));
                return body;
            }
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                Grid row = new Grid();
                row.ColumnDefinitions.Add(Ui.AutoColumn());
                row.ColumnDefinitions.Add(Ui.StarColumn());
                row.Margin = new Thickness(0, Theme.Space1, 0, Theme.Space1);
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
                label.TextWrapping = TextWrapping.Wrap;
                bool bad = step.LastReplay != null && step.LastReplay.State != "done";
                label.Foreground = bad ? Theme.CautionText : Theme.TextSub;
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                body.Children.Add(row);
            }
            return body;
        }

        private static void Stat(Panel panel, int value, string label)
        {
            StackPanel stack = new StackPanel();
            TextBlock number = new TextBlock();
            number.Text = value.ToString(CultureInfo.InvariantCulture);
            number.FontSize = Theme.SectionSize;
            number.FontWeight = FontWeights.Bold;
            number.Foreground = Theme.Text;
            stack.Children.Add(number);
            TextBlock caption = new TextBlock();
            caption.Text = label;
            caption.FontSize = Theme.MicroSize;
            caption.Foreground = Theme.TextMuted;
            stack.Children.Add(caption);
            Border card = new Border();
            card.CornerRadius = new CornerRadius(Theme.RadiusSm);
            card.Background = Theme.SurfaceSunken;
            card.BorderBrush = Theme.BorderSubtle;
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            card.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            card.MinWidth = 68;
            card.Child = stack;
            panel.Children.Add(card);
        }

        // ---------- the assistant tab ----------

        private void PaintAssistant()
        {
            aiBody.Children.Clear();
            if (project == null)
            {
                aiBody.Children.Add(Ui.Empty(Text("empty-ai.txt", "There is no code to discuss yet."),
                    Text("empty-ai-note.txt", "After a recording or a snap, the generated code can be discussed here.")));
                return;
            }
            aiBody.Children.Add(Ui.Note(Text("code-ai-note.txt",
                "Copy the request, paste it, and attach the two files it names. What comes back is shown as a difference across the whole editor before anything is replaced.")));

            requestBox.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            requestBox.AcceptsReturn = true;
            requestBox.TextWrapping = TextWrapping.Wrap;
            requestBox.MinHeight = 84;
            requestBox.MaxHeight = 160;
            requestBox.Margin = new Thickness(0, Theme.Space3, 0, 0);
            requestBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (requestBox.Text.Length == 0)
            {
                requestBox.Text = Text("code-ai-request-default.txt",
                    "Make this run reliably against the recorded application. Keep every safety rule in section 10 of the attached file.");
            }
            System.Windows.Automation.AutomationProperties.SetName(requestBox, Text("code-ai-request-name.txt", "What to ask for"));
            Detach(requestBox);
            aiBody.Children.Add(requestBox);

            copyButton = Ui.IconTextButton(Icons.Copy, Text("code-ai-copy.txt", "Copy the request"),
                Text("code-ai-copy-note.txt", "Puts the request on the clipboard. The two files to attach are written here too."),
                delegate { CopyRequest(); }, true);
            copyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            copyButton.Margin = new Thickness(0, Theme.Space3, 0, Theme.Space2);
            PaintCopy();
            aiBody.Children.Add(copyButton);

            Button folder = Ui.IconTextButton(Icons.Folder, Text("code-ai-folder.txt", "Open the two files to attach"),
                Text("code-ai-folder-note.txt", "Opens the folder holding the files to attach with the request."), delegate { OpenAttachments(); }, false);
            folder.HorizontalAlignment = HorizontalAlignment.Stretch;
            folder.Margin = new Thickness(0, 0, 0, Theme.Space2);
            aiBody.Children.Add(folder);

            Button paste = Ui.IconTextButton(Icons.Paste, Text("code-ai-paste.txt", "Paste the answer and see the difference"),
                Text("code-ai-paste-note.txt", "Reads the answer from the clipboard and shows it as a difference. Nothing is replaced yet."),
                delegate { TakeIn(); }, false);
            paste.HorizontalAlignment = HorizontalAlignment.Stretch;
            aiBody.Children.Add(paste);

            aiLine.FontSize = Theme.MetaSize;
            aiLine.LineHeight = Theme.MetaSize * Theme.BodyLine;
            aiLine.TextWrapping = TextWrapping.Wrap;
            aiLine.Foreground = Theme.TextMuted;
            aiLine.Margin = new Thickness(0, Theme.Space3, 0, 0);
            Detach(aiLine);
            aiBody.Children.Add(aiLine);
        }

        // ---------- the difference, across the whole middle pane ----------

        // An answer from an assistant is never put into the editor on arrival.
        // The middle pane - the widest and tallest thing on this window - becomes
        // the difference, whole, and stays that way until it is accepted or
        // dropped. It used to be shown in a 300 unit column at the bottom right,
        // where a change of forty lines was read six lines at a time through two
        // scrollbars, which is not a way anybody can judge whether to take
        // something in.
        private void ShowDiffFace(string summary)
        {
            diffShowing = true;
            diffBody.Children.Clear();

            int changed = 0;
            for (int index = 0; index < pendingDiff.Count; index++) if (pendingDiff[index].Changed) changed++;

            Grid bar = new Grid();
            bar.ColumnDefinitions.Add(Ui.StarColumn());
            bar.ColumnDefinitions.Add(Ui.AutoColumn());
            bar.Margin = new Thickness(0, 0, 0, Theme.Space3);
            StackPanel head = new StackPanel();
            head.VerticalAlignment = VerticalAlignment.Center;
            TextBlock title = Ui.Heading(Text("diff-title.txt", "The answer from the assistant, not yet applied"));
            head.Children.Add(title);
            TextBlock counts = Ui.Note(pendingDiff.Count.ToString(CultureInfo.InvariantCulture) + " " +
                Text("code-intake-files.txt", "file(s)") + " / " + changed.ToString(CultureInfo.InvariantCulture) + " " +
                Text("code-intake-changed.txt", "changed") + (String.IsNullOrEmpty(summary) ? "" : "   " + summary));
            head.Children.Add(counts);
            Grid.SetColumn(head, 0);
            bar.Children.Add(head);

            StackPanel choose = new StackPanel();
            choose.Orientation = Orientation.Horizontal;
            choose.VerticalAlignment = VerticalAlignment.Center;
            Button drop = Ui.IconTextButton(Icons.Cross, Text("diff-reject.txt", "Reject"),
                Text("diff-reject-note.txt", "Drops this answer. Nothing on screen changes."), delegate { DropPending(); }, false);
            choose.Children.Add(drop);
            Button apply = Ui.IconTextButton(Icons.Check, Text("diff-apply.txt", "Take this in"),
                Text("diff-apply-note.txt", "Replaces the code exactly as this difference shows."), delegate { ApplyPending(); }, true);
            apply.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            choose.Children.Add(apply);
            Grid.SetColumn(choose, 1);
            bar.Children.Add(choose);
            diffBody.Children.Add(bar);

            for (int index = 0; index < pendingDiff.Count; index++)
            {
                FileDiff diff = pendingDiff[index];
                TextBlock name = Ui.Label(diff.Name + "." + ScriptLanguages.Extension(diff.Language) +
                    (diff.IsNew ? "   " + Text("code-diff-new.txt", "(new file)") : "") + "   " + diff.Summary);
                name.Margin = new Thickness(0, Theme.Space3, 0, Theme.Space1);
                diffBody.Children.Add(name);
                if (!diff.Changed)
                {
                    diffBody.Children.Add(Ui.Note(Text("diff-unchanged.txt", "There is no change in this file.")));
                    continue;
                }
                diffBody.Children.Add(DiffBlock(diff));
            }
        }

        private UIElement DiffBlock(FileDiff diff)
        {
            int hidden;
            List<DiffLine> lines = Diff.Interesting(diff, out hidden);
            StackPanel rows = new StackPanel();
            for (int index = 0; index < lines.Count && index < 2000; index++)
            {
                rows.Children.Add(DiffRow(lines[index]));
            }
            if (lines.Count > 2000)
            {
                rows.Children.Add(Ui.Note(Text("code-diff-more.txt", "further changed lines are not shown here") + ": " +
                    (lines.Count - 2000).ToString(CultureInfo.InvariantCulture)));
            }
            if (hidden > 0)
            {
                rows.Children.Add(Ui.Note(Text("code-diff-same.txt", "unchanged lines left out") + ": " +
                    hidden.ToString(CultureInfo.InvariantCulture)));
            }
            Border frame = new Border();
            frame.Background = Theme.SurfaceCode;
            frame.BorderBrush = Theme.Border;
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(Theme.RadiusSm);
            frame.Padding = new Thickness(Theme.Space2);
            frame.Child = rows;
            return frame;
        }

        private static UIElement DiffRow(DiffLine line)
        {
            TextBlock block = new TextBlock();
            string mark = line.Kind == DiffLine.Added ? "+ " : (line.Kind == DiffLine.Removed ? "- " : "  ");
            block.Text = mark + line.Text;
            block.FontFamily = Theme.CodeFont;
            block.FontSize = Theme.CodeSize;
            // The difference wraps for the same reason the editor does: a changed
            // line that runs past the right edge is a line nobody read before
            // deciding.
            block.TextWrapping = TextWrapping.Wrap;
            block.Padding = new Thickness(Theme.Space2, 1, Theme.Space2, 1);
            if (line.Kind == DiffLine.Added)
            {
                block.Foreground = Theme.SuccessText;
                block.Background = Theme.SuccessSoft;
            }
            else if (line.Kind == DiffLine.Removed)
            {
                block.Foreground = Theme.DangerText;
                block.Background = Theme.DangerSoft;
            }
            else
            {
                block.Foreground = Theme.TextMuted;
            }
            return block;
        }

        private void ShowDiff(string summary)
        {
            ShowDiffFace(summary);
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = diffBody;
            centreFace.Content = scroll;
            buildStrip.Visibility = Visibility.Collapsed;
            moduleLine.Text = Text("diff-title.txt", "The answer from the assistant, not yet applied");
            // A difference that is waiting has to be seen. The editor is where
            // the operator is looking, so it is the editor that becomes the
            // difference; the pane it came from can be folded away without
            // losing it.
            Say(Text("diff-waiting.txt", "The answer is shown as a difference. Choose whether to take it in or reject it."), null);
        }

        private void ApplyPending()
        {
            if (pending == null) { ShowEditorFace(); return; }
            List<CodeFile> incoming = new List<CodeFile>();
            for (int index = 0; index < pending.Count; index++)
            {
                CodeFile file = new CodeFile();
                file.Language = pending[index].Language;
                file.Name = pending[index].Name;
                file.Role = CodeRoles.Of(pending[index].Name);
                file.Text = pending[index].Text;
                incoming.Add(file);
            }
            project.Apply(incoming);
            pending = null;
            pendingDiff = null;
            parts = new IntakeParts();
            diffBody.Children.Clear();
            Save();
            ready = false;
            PaintTree();
            LoadEditor();
            PaintState();
            ShowEditorFace();
            aiLine.Text = Text("code-diff-applied.txt", "Taken in.");
            aiLine.Foreground = Theme.SuccessText;
            Say(aiLine.Text, "Success");
        }

        private void DropPending()
        {
            pending = null;
            pendingDiff = null;
            diffBody.Children.Clear();
            ShowEditorFace();
            aiLine.Text = Text("code-diff-dropped.txt", "Left alone. Nothing on screen was changed.");
            aiLine.Foreground = Theme.TextMuted;
            Say(aiLine.Text, "Caution");
        }

        // ---------- state ----------

        // What the code on screen is, said against where it came from.
        //
        // A snap has no recording behind it, so saying it came from a recording over a
        // snap was the window stating something that is not true. The source is
        // taken from the session rather than assumed.
        private void PaintState()
        {
            if (project == null || session == null)
            {
                stateLine.Text = "";
                stateChip.Visibility = Visibility.Collapsed;
                moduleNote.Text = "";
                return;
            }
            stateChip.Visibility = Visibility.Visible;
            bool recording = String.Equals(session.Kind, StudioSession.KindRecord, StringComparison.Ordinal);
            string source = recording ? Text("source-record.txt", "Record") : Text("source-snap.txt", "Snap");
            bool changed = project.DiffersFromBaseline(currentLanguage);
            stateLine.Text = changed
                ? String.Format(CultureInfo.InvariantCulture, Text("code-state-edited.txt", "Edited since it was generated from the {0}"), source)
                : String.Format(CultureInfo.InvariantCulture, Text("code-state-generated.txt", "Exactly as it was generated from the {0}"), source);
            stateLine.Foreground = changed ? Theme.CautionText : Theme.SuccessText;
            stateChip.Background = changed ? Theme.CautionSoft : Theme.SuccessSoft;
            stateChip.BorderBrush = changed ? Theme.Caution : Theme.Success;

            StringBuilder note = new StringBuilder();
            note.Append(Text("code-files.txt", "files / lines")).Append(": ").Append(project.Summary(currentLanguage));
            if (project.Plan != null && project.Plan.Unsupported > 0)
            {
                note.Append("   ").Append(Text("code-unsupported.txt", "steps with no address that survives a restart"))
                    .Append(": ").Append(project.Plan.Unsupported.ToString(CultureInfo.InvariantCulture));
            }
            moduleNote.Text = note.ToString();
        }

        public UIElement StateChip { get { return stateChip; } }

        private static Border Chip(TextBlock label)
        {
            label.FontSize = Theme.MicroSize;
            label.FontWeight = FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            Border chip = new Border();
            chip.CornerRadius = new CornerRadius(Theme.RadiusPill);
            chip.BorderThickness = new Thickness(1);
            chip.Padding = new Thickness(Theme.Space3, Theme.Space1, Theme.Space3, Theme.Space1);
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.Child = label;
            return chip;
        }

        // ---------- check, build, run ----------

        private void Check()
        {
            if (project == null) return;
            Remember();
            string languageName = currentLanguage;
            string text = editor.Text;
            string module = currentFile;
            List<CodeFile> modules = project.Files(languageName);
            Say(Text("code-checking.txt", "Checking..."), null);
            buildLine.Text = Text("code-checking.txt", "Checking...");
            buildLine.Foreground = Theme.TextMuted;
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                CheckResult result = ScriptRun.Check(languageName, modules, text, module);
                owner.Dispatcher.BeginInvoke(new Action(delegate { ShowCheck(result); }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowCheck(CheckResult result)
        {
            StringBuilder text = new StringBuilder();
            text.Append(result.Headline).Append("  (").Append(result.Method).Append(")");
            for (int index = 0; index < result.Problems.Count && index < 8; index++)
            {
                text.Append(Environment.NewLine).Append("- ").Append(result.Problems[index]);
            }
            buildLine.Text = text.ToString();
            buildLine.Foreground = result.Ok ? Theme.SuccessText : Theme.DangerText;
            Say(result.Headline, result.Ok ? "Success" : "Danger");
        }

        private string BuildFolder()
        {
            string folder = project.Folder == null ? Path.GetTempPath() : project.Folder;
            return Path.Combine(folder, "build");
        }

        private void BuildIt()
        {
            if (project == null) return;
            Remember();
            string languageName = currentLanguage;
            List<CodeFile> modules = project.Files(languageName);
            string folder = BuildFolder();
            Say(Text("code-building.txt", "Building..."), null);
            buildLine.Text = Text("code-building.txt", "Building...");
            buildLine.Foreground = Theme.TextMuted;
            if (launchButton != null) launchButton.IsEnabled = false;
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                BuildResult result = String.Equals(languageName, ScriptLanguages.Vba, StringComparison.Ordinal)
                    ? CodeBuild.BuildVba(modules, folder)
                    : CodeBuild.BuildPowerShell(modules, folder);
                owner.Dispatcher.BeginInvoke(new Action(delegate { ShowBuild(result); }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowBuild(BuildResult result)
        {
            StringBuilder text = new StringBuilder();
            if (!result.Ok)
            {
                builtPath = null;
                if (launchButton != null) launchButton.IsEnabled = false;
                text.Append(Text("code-build-failed.txt", "Nothing was built.")).Append("  ").Append(result.Problem);
                buildLine.Text = text.ToString();
                buildLine.Foreground = Theme.DangerText;
                Say(Text("code-build-failed.txt", "Nothing was built."), "Danger");
                return;
            }
            builtPath = result.Path;
            if (launchButton != null) launchButton.IsEnabled = true;
            text.Append(Text("code-build-done.txt", "Built one file.")).Append("  ").Append(result.Path);
            text.Append("  (").Append(result.Bytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");
            if (result.Modules.Count > 0)
            {
                text.Append(Environment.NewLine).Append(String.Join("  ", result.Modules.ToArray()));
            }
            buildLine.Text = text.ToString();
            buildLine.Foreground = Theme.SuccessText;
            Say(Text("code-build-done.txt", "Built one file."), "Success");
        }

        // The built file, started the way the person who is handed it would start
        // it. A screen that says a build succeeded is not evidence that what was
        // built runs, so this is here to make that one press away.
        private void LaunchBuilt()
        {
            if (String.IsNullOrEmpty(builtPath) || !File.Exists(builtPath))
            {
                Say(Text("code-build-none.txt", "Nothing has been built yet."), "Caution");
                return;
            }
            if (AskRunConsent != null && !AskRunConsent())
            {
                Say(Text("code-launch-declined.txt", "It was not started."), "Caution");
                return;
            }
            try
            {
                System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo();
                start.FileName = builtPath;
                start.UseShellExecute = true;
                start.WorkingDirectory = Path.GetDirectoryName(builtPath);
                System.Diagnostics.Process.Start(start);
                Say(Text("code-launched.txt", "The built file was started. What it did is in its own window and in the log beside it."), "Success");
            }
            catch (Exception exception)
            {
                Say(Text("open-failed.txt", "It could not be opened") + ": " + exception.Message, "Danger");
            }
        }

        private void RunIt()
        {
            if (project == null) return;
            Remember();
            if (AskRunConsent != null && !AskRunConsent())
            {
                Say(Text("code-run-declined.txt", "The script was not started."), "Caution");
                return;
            }
            string languageName = currentLanguage;
            List<CodeFile> modules = project.Files(languageName);
            string folder = Path.Combine(project.Folder == null ? Path.GetTempPath() : project.Folder, "run");
            Say(Text("code-running.txt", "Running..."), null);
            buildLine.Text = Text("code-running.txt", "Running...");
            buildLine.Foreground = Theme.TextMuted;
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                RunResult result = String.Equals(languageName, ScriptLanguages.Vba, StringComparison.Ordinal)
                    ? ScriptRun.RunVba(modules, folder, VbaGen.EntryPoint, 180000)
                    : ScriptRun.RunPowerShellProject(modules, folder, 180000);
                owner.Dispatcher.BeginInvoke(new Action(delegate { ShowRun(result); }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowRun(RunResult result)
        {
            StringBuilder text = new StringBuilder();
            if (!result.Started) text.Append(Text("code-run-nostart.txt", "It was not run.")).Append("  ").Append(result.Problem);
            else if (result.Problem != null) text.Append(Text("code-run-stopped.txt", "It stopped.")).Append("  ").Append(result.Problem);
            else
            {
                text.Append(result.Ok ? Text("code-run-done.txt", "It ran to the end.") : Text("code-run-failed.txt", "It ended with a failure."));
                text.Append("  (exit ").Append(result.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(")");
            }
            if (result.Output.Length > 0)
            {
                string output = result.Output.Length > 1200 ? result.Output.Substring(0, 1200) + " ..." : result.Output;
                text.Append(Environment.NewLine).Append(output);
            }
            buildLine.Text = text.ToString();
            bool good = result.Started && result.Problem == null && result.Ok;
            buildLine.Foreground = good ? Theme.SuccessText : Theme.DangerText;
            Say(good ? Text("code-run-done.txt", "It ran to the end.") : Text("code-run-failed.txt", "It ended with a failure."), good ? "Success" : "Danger");
        }

        private void Baseline()
        {
            if (project == null) return;
            Remember();
            project.RestoreBaseline(currentLanguage);
            Save();
            ready = false;
            PaintTree();
            LoadEditor();
            PaintState();
            Say(Text("code-baseline-done.txt", "The generated version is back."), "Success");
        }

        private void Save()
        {
            if (project == null) return;
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        // ---------- out to the assistant, and back ----------

        private void CopyRequest()
        {
            if (project == null || session == null) return;
            Remember();
            if (handoff == null)
            {
                project.RequestId = Handoff.NewRequestId();
                Outputs.WriteAll(session, PdfBudgetBytes == null ? ScreensPdf.DefaultBudgetBytes : PdfBudgetBytes(), project);
                handoff = Handoff.Build(session, project, requestBox.Text, project.RequestId);
                Handoff.Write(project, handoff);
                parts = new IntakeParts();
                Save();
            }
            if (!handoff.AttachmentsReady)
            {
                aiLine.Text = Text("code-ai-attach-failed.txt", "The files the request tells the assistant to read were not written, so the request was not copied.") +
                    " " + handoff.MissingText();
                aiLine.Foreground = Theme.DangerText;
                Say(aiLine.Text, "Danger");
                handoff = null;
                return;
            }
            try
            {
                Clipboard.SetText(handoff.Text);
            }
            catch (Exception exception)
            {
                Say(Text("code-copy-failed.txt", "The clipboard refused the request") + ": " + exception.Message, "Danger");
                return;
            }
            copied = true;
            PaintCopy();
            aiLine.Text = Text("code-copy-done.txt", "The request is on the clipboard. Paste it into the chat and attach the two files beside it.");
            aiLine.Foreground = Theme.SuccessText;
            Say(aiLine.Text, "Success");
        }

        // Once a request has been copied, the button says so. Pressing it again
        // copies the same request rather than starting a new one: changing the
        // request would quietly invalidate an answer the operator may already be
        // waiting for.
        private void PaintCopy()
        {
            if (copyButton == null) return;
            string label = copied
                ? Text("code-ai-copied.txt", "Request copied - copy it again")
                : Text("code-ai-copy.txt", "Copy the request");
            copyButton.Content = null;
            copyButton.SetResourceReference(FrameworkElement.StyleProperty, copied ? "AppButtonCompact" : "AppButtonPrimary");
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            UIElement drawing = Icons.Make(copied ? Icons.Check : Icons.Copy, 16, copied ? Theme.TextSub : Theme.TextOnAccent);
            FrameworkElement framed = drawing as FrameworkElement;
            if (framed != null) framed.Margin = new Thickness(0, 0, Theme.Space2, 0);
            row.Children.Add(drawing);
            TextBlock text = new TextBlock();
            text.Text = label;
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            copyButton.Content = row;
            Ui.Name(copyButton, label, Text("code-ai-copy-note.txt", "Puts the request on the clipboard. The two files to attach are written here too."));
        }

        private void OpenAttachments()
        {
            if (session == null || session.AiFolder == null || !Directory.Exists(session.AiFolder))
            {
                Say(Text("code-ai-attach-missing.txt", "not written yet"), "Caution");
                return;
            }
            Open(session.AiFolder);
        }

        private void TakeIn()
        {
            if (project == null) return;
            Remember();
            if (String.IsNullOrEmpty(project.RequestId))
            {
                Say(Text("code-intake-norequest.txt", "Copy the request first: an answer is only accepted against the request it was asked for."), "Caution");
                return;
            }
            string pasted;
            try
            {
                pasted = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception exception)
            {
                Say(Text("code-paste-failed.txt", "The clipboard could not be read") + ": " + exception.Message, "Danger");
                return;
            }
            IntakeResult parsed = Intake.Parse(pasted, project.RequestId);
            if (!parsed.Ok) { Refused(parsed); return; }
            if (parsed.HasPart)
            {
                IntakeResult added = Intake.AddPart(parts, parsed);
                if (!added.Ok) { Refused(added); return; }
                if (!parts.Complete)
                {
                    aiLine.Text = Text("code-intake-partial.txt", "Part taken in. Still needed") + ": " + parts.MissingText();
                    aiLine.Foreground = Theme.CautionText;
                    Say(aiLine.Text, "Caution");
                    return;
                }
                parsed = Intake.Merge(parts);
                if (!parsed.Ok) { Refused(parsed); return; }
            }
            if (parsed.NoChange != null)
            {
                aiLine.Text = Refusal(parsed.NoChange);
                aiLine.Foreground = Theme.CautionText;
                Say(aiLine.Text, "Caution");
                return;
            }
            pending = parsed.Files;
            pendingDiff = Diff.Compare(project, pending);
            ShowDiff(parsed.Summary);
        }

        private string Refusal(string verdict)
        {
            if (String.Equals(verdict, "UNNECESSARY", StringComparison.Ordinal))
            {
                return Text("code-nochange-unnecessary.txt", "The assistant answered that nothing needs changing. Nothing on screen was touched.");
            }
            if (String.Equals(verdict, "IMPOSSIBLE", StringComparison.Ordinal))
            {
                return Text("code-nochange-impossible.txt", "The assistant answered that this cannot be done by changing these files. Nothing on screen was touched.");
            }
            return Text("code-nochange-unclear.txt", "The assistant answered that the request cannot be settled from what it was given. Nothing on screen was touched.");
        }

        private void Refused(IntakeResult result)
        {
            aiLine.Text = Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message + "  [" + result.Reason + "]";
            aiLine.Foreground = Theme.DangerText;
            Say(Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message, "Danger");
        }

        private void Open(string path)
        {
            if (String.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                Say(Text("open-missing.txt", "That is not on disk yet") + ": " + (path == null ? "-" : path), "Caution");
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
    }
}
