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

    // The third shape of the window: the recording as code.
    //
    // This is a workspace, not a page with an editor on it. Editing code is the
    // whole job here, so the window's ordinary furniture - the launcher's
    // buttons, the theme switch, the health badge - is not carried into it. What
    // is left is a bar that says where you are and offers back, check and run;
    // the modules down the left; and the file, filling everything else.
    //
    // The assistant sits in a column beside the editor rather than a strip under
    // it. A window is far wider than it is tall, so width is the thing there is
    // enough of; taking the height instead was what left the editor showing nine
    // lines of a fifty line file. Concentrating - the same editor with the
    // assistant column put away - is a second layout rather than a mode: both
    // hold the same editor object, so moving between them cannot lose what was
    // typed, where the caret was or how far down it was scrolled.
    //
    // PowerShell and VBA are the same size on this screen, in the same place,
    // with the same buttons; neither is the real one with the other offered as
    // an export.
    //
    // Talking to an assistant is part of this screen rather than a mode of its
    // own. One button copies the request - once, whole - one button opens the
    // folder holding the two files to attach with it, and one takes an answer
    // in. What comes back is shown as a difference before anything is replaced.
    public sealed class CodeScreen
    {
        private readonly Window owner;
        private readonly StudioSession session;
        private readonly CodeProject project;
        private readonly Action<string, string> say;
        private readonly Func<bool> askRunConsent;

        private readonly CodeEditor editor = new CodeEditor();
        private readonly TextBox requestBox = new TextBox();
        private readonly TextBlock stateLine = new TextBlock();
        private readonly TextBlock languageNote = new TextBlock();
        private readonly TextBlock moduleLine = new TextBlock();
        private readonly TextBlock moduleLead = new TextBlock();
        private readonly TextBlock intakeLine = new TextBlock();
        private readonly TextBlock attachLine = new TextBlock();
        private readonly StackPanel diffHost = new StackPanel();
        private readonly TreeView tree = new TreeView();
        private readonly Button copyButton = new Button();
        private readonly Border stateChip;
        private Button psButton;
        private Button vbaButton;
        private Button fullButton;

        // Leaving this screen goes back to the result it was opened from. The
        // bar owns the control, so the window hands it the move rather than
        // keeping a second back button of its own on top.
        public Action GoBack;
        // Concentrating changes what the window around this should carry, so the
        // window is told rather than left to guess.
        public Action FullChanged;

        public bool IsFull { get { return full; } }

        // The picture budget lives in the settings dialog, which this screen
        // does not own. It is asked for rather than copied, so changing it there
        // reaches the attachment written here.
        public Func<int> PdfBudgetBytes;

        private HandoffResult handoff;
        private bool copied;
        private bool folderOpened;
        private IntakeParts parts = new IntakeParts();
        private List<IntakeFile> pending;
        private List<FileDiff> pendingDiff;
        private string currentFile = CodeModules.Workflow;
        private bool loading;
        private bool full;
        private Grid root;
        // Nothing is written back from the editor until the editor has been
        // filled at least once. Without this the first pass writes an empty box
        // over the code that was just generated, and the screen opens blank.
        private bool ready;

        public CodeScreen(Window ownerWindow, StudioSession studioSession, CodeProject codeProject,
            Action<string, string> status, Func<bool> runConsent)
        {
            owner = ownerWindow;
            session = studioSession;
            project = codeProject;
            say = status;
            askRunConsent = runConsent;
            stateChip = Chip(stateLine);
            editor.Changed = delegate
            {
                if (loading) return;
                Remember();
                PaintState();
            };
        }

        public CodeProject Project { get { return project; } }

        // There is no save button here, the same way there is none for a
        // recording. Whatever is in the editor is written when the screen is
        // left, so leaving it and coming back - or closing the window - cannot
        // lose what was typed.
        public void Persist()
        {
            Remember();
            if (!ready) return;
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        private static string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        // The body is built once and handed back on every later visit. WPF
        // refuses to give an element a second parent, so it is taken out of the
        // tree it was in first; rebuilding it instead would throw away whatever
        // the operator had typed and not yet checked.
        public UIElement Build()
        {
            if (root != null)
            {
                Detach(root);
                return root;
            }
            root = new Grid();
            Compose();
            ShowLanguage(project.Language);
            // The status bar still holds whatever the last screen said. Left
            // alone it tells somebody looking at code to start a recording.
            Say(Text("code-opened.txt", "The recording, as code."), null);
            return root;
        }

        private static void Detach(UIElement child)
        {
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

        private static void Release(UIElement child)
        {
            if (child == null) return;
            Detach(child);
        }

        // ---------- the two layouts ----------

        // Laying the screen out again, keeping every control that holds state.
        // The editor, the tree, the request box and the difference are the same
        // objects in both layouts; only where they sit changes.
        private void Compose()
        {
            double scroll = ready ? editor.VerticalOffset : 0;
            double across = ready ? editor.HorizontalOffset : 0;
            int caret = ready ? editor.CaretIndex : 0;

            // Every control that carries state is the same object in both
            // layouts, and WPF refuses to give an element a second parent. They
            // are taken out of the layout they were in before the next one is
            // built; rebuilding them instead would throw away the text, the
            // selection and the difference that is waiting to be read.
            Release(stateChip);
            Release(languageNote);
            Release(moduleLine);
            Release(moduleLead);
            Release(tree);
            Release(requestBox);
            Release(copyButton);
            Release(attachLine);
            Release(intakeLine);
            Release(diffHost);

            root.Children.Clear();
            root.RowDefinitions.Clear();
            root.RowDefinitions.Add(AutoRow());
            root.RowDefinitions.Add(StarRow());

            UIElement bar = WorkspaceBar();
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            UIElement work = Work();
            Grid.SetRow(work, 1);
            root.Children.Add(work);

            PaintFullButton();
            // The language buttons are made fresh by the module pane, so which
            // one is chosen has to be said again. Without this both come back
            // looking like the one that is not on screen.
            PaintSwitcher();
            if (!ready) return;
            // Put the operator back where they were. The editor is the same
            // control, so the text and the selection came across on their own;
            // the scroll offset is the one thing a re-parent drops.
            editor.CaretIndex = caret;
            owner.Dispatcher.BeginInvoke(new Action(delegate { editor.ScrollTo(scroll, across); }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ToggleFull()
        {
            Remember();
            full = !full;
            Compose();
            if (FullChanged != null) FullChanged();
            editor.FocusEditor();
            Say(full
                ? Text("code-full-on.txt", "The editor has the whole window. Press the same button to bring the rest back.")
                : Text("code-full-off.txt", "The rest of the screen is back."), null);
        }

        // ---------- the bar: where you are, and the three moves that are always wanted ----------

        // One row, the height the design system gives a screen header. Back on
        // the left where every back is, the name of the job in the middle with
        // the state beside it, and check / run / concentrate on the right. It
        // does not carry the theme switch, the health badge or the settings:
        // none of them is part of editing a file, and each one was another thing
        // between the operator and the code.
        private UIElement WorkspaceBar()
        {
            Grid line = new Grid();
            line.ColumnDefinitions.Add(AutoColumn());
            line.ColumnDefinitions.Add(StarColumn());
            line.ColumnDefinitions.Add(AutoColumn());

            Button back = new Button();
            back.Content = Text("topbar-back.txt", "Back");
            back.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            back.VerticalAlignment = VerticalAlignment.Center;
            back.Click += delegate { if (GoBack != null) GoBack(); };
            Grid.SetColumn(back, 0);
            line.Children.Add(back);

            StackPanel middle = new StackPanel();
            middle.Orientation = Orientation.Horizontal;
            middle.VerticalAlignment = VerticalAlignment.Center;
            middle.Margin = new Thickness(Theme.Space4, 0, Theme.Space4, 0);
            TextBlock title = new TextBlock();
            title.Text = Text("code-workspace.txt", "Edit as code");
            title.FontSize = Theme.TitleSize;
            title.FontWeight = FontWeights.Bold;
            title.Foreground = Theme.Text;
            title.VerticalAlignment = VerticalAlignment.Center;
            middle.Children.Add(title);
            if (session != null && !String.IsNullOrEmpty(session.Title))
            {
                TextBlock name = new TextBlock();
                name.Text = session.Title;
                name.FontSize = Theme.MetaSize;
                name.Foreground = Theme.TextMuted;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.Margin = new Thickness(Theme.Space3, 0, 0, 0);
                middle.Children.Add(name);
            }
            middle.Children.Add(stateChip);
            Grid.SetColumn(middle, 1);
            line.Children.Add(middle);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            BarButton(right, Text("code-check.txt", "Check"), delegate { Check(); }, false);
            BarButton(right, Text("code-run.txt", "Run"), delegate { RunIt(); }, true);
            fullButton = new Button();
            fullButton.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            fullButton.MinWidth = 132;
            fullButton.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            fullButton.Click += delegate { ToggleFull(); };
            right.Children.Add(fullButton);
            Grid.SetColumn(right, 2);
            line.Children.Add(right);

            Border frame = new Border();
            frame.Background = Theme.Get("TopbarBackground");
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(0, 0, 0, 1);
            frame.Padding = new Thickness(Theme.Space5, Theme.Space2, Theme.Space5, Theme.Space2);
            frame.MinHeight = Theme.ScreenHeaderHeight;
            frame.Child = line;
            return frame;
        }

        // The state is a badge rather than a sentence in the middle of a row:
        // it is one of two things, and which one it is has to be legible at a
        // glance from the edge of the bar.
        private static Border Chip(TextBlock label)
        {
            label.FontSize = Theme.MicroSize;
            label.FontWeight = FontWeights.SemiBold;
            label.VerticalAlignment = VerticalAlignment.Center;
            Border chip = new Border();
            chip.CornerRadius = new CornerRadius(Theme.RadiusPill);
            chip.BorderThickness = new Thickness(1);
            chip.Padding = new Thickness(Theme.Space3, Theme.Space1, Theme.Space3, Theme.Space1);
            chip.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.Child = label;
            return chip;
        }

        private void PaintFullButton()
        {
            if (fullButton == null) return;
            fullButton.Content = full
                ? Text("code-full-exit.txt", "Leave full width editing")
                : Text("code-full-enter.txt", "Edit at full width");
        }

        private Button LanguageButton(string label, string languageName)
        {
            Button button = new Button();
            button.Content = label;
            button.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            button.MinWidth = 0;
            // Two of these share the width of the pane, and one of the two words
            // is "PowerShell". The padding a button gets in a row of its own is
            // what turned that into "PowerSh".
            button.Padding = new Thickness(Theme.Space1, 0, Theme.Space1, 0);
            button.Click += delegate { ShowLanguage(languageName); };
            return button;
        }

        // Neither language is styled as the lesser one. The one on screen is
        // shown as chosen, the other as available, and nothing else differs.
        private void PaintSwitcher()
        {
            bool ps = String.Equals(project.Language, ScriptLanguages.PowerShell, StringComparison.Ordinal);
            psButton.SetResourceReference(FrameworkElement.StyleProperty, ps ? "AppButtonPrimary" : "AppButtonCompact");
            vbaButton.SetResourceReference(FrameworkElement.StyleProperty, ps ? "AppButtonCompact" : "AppButtonPrimary");
        }

        // ---------- the work area: which module, and the module ----------

        private UIElement Work()
        {
            Grid grid = new Grid();
            grid.Margin = new Thickness(Theme.Space5, Theme.Space4, Theme.Space5, Theme.Space4);
            grid.ColumnDefinitions.Add(FixedColumn(Theme.ModulePaneWidth));
            grid.ColumnDefinitions.Add(StarColumn());
            // The editor is what this screen is for, so it is the column that
            // takes whatever is left over. The other two are fixed: a tree that
            // grows with the window shows the same ten rows further apart.
            grid.ColumnDefinitions[1].MinWidth = 360;
            if (!full) grid.ColumnDefinitions.Add(FixedColumn(Theme.AssistantPaneWidth));

            UIElement modules = ModulePane();
            Grid.SetColumn(modules, 0);
            grid.Children.Add(modules);

            UIElement editing = EditorPane();
            Grid.SetColumn(editing, 1);
            grid.Children.Add(editing);

            if (!full)
            {
                UIElement assistant = Assistant();
                Grid.SetColumn(assistant, 2);
                grid.Children.Add(assistant);
            }
            return grid;
        }

        // ---------- the modules ----------

        private UIElement ModulePane()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(AutoRow());
            pane.RowDefinitions.Add(StarRow());
            pane.RowDefinitions.Add(AutoRow());

            // The head of the pane says what the pane is and, under it, which of
            // the two forms is on screen. The form belongs here rather than over
            // the editor: choosing a form and choosing a module are the same
            // question - which file am I looking at - asked twice.
            StackPanel head = new StackPanel();
            TextBlock caption = new TextBlock();
            caption.Text = Text("code-modules.txt", "Modules");
            caption.FontSize = Theme.LabelSize;
            caption.FontWeight = FontWeights.SemiBold;
            caption.Foreground = Theme.TextSub;
            head.Children.Add(caption);
            // Concentrating takes the form switch away with the rest of the
            // chrome. The tree still holds both forms, so nothing becomes
            // unreachable; what goes is the second way of doing the same thing,
            // which is what a screen for reading one file does not need.
            psButton = LanguageButton(Text("code-lang-ps.txt", "PowerShell"), ScriptLanguages.PowerShell);
            vbaButton = LanguageButton(Text("code-lang-vba.txt", "VBA"), ScriptLanguages.Vba);
            if (!full)
            {
                Grid switcher = new Grid();
                switcher.Margin = new Thickness(0, Theme.Space2, 0, 0);
                switcher.ColumnDefinitions.Add(StarColumn());
                switcher.ColumnDefinitions.Add(FixedColumn(Theme.Space2));
                switcher.ColumnDefinitions.Add(StarColumn());
                Grid.SetColumn(psButton, 0);
                Grid.SetColumn(vbaButton, 2);
                switcher.Children.Add(psButton);
                switcher.Children.Add(vbaButton);
                head.Children.Add(switcher);
            }
            head.Margin = new Thickness(0, 0, 0, Theme.Space3);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            tree.SetResourceReference(FrameworkElement.StyleProperty, "AppTree");
            tree.FontSize = Theme.LabelSize;
            System.Windows.Automation.AutomationProperties.SetName(tree, Text("code-modules.txt", "Modules"));
            Grid.SetRow(tree, 1);
            pane.Children.Add(tree);

            // What the operator has, and the moves that are about versions and
            // files rather than about the code on screen. They are down here in
            // plain sight rather than in a menu: rare is not the same as hidden.
            StackPanel foot = new StackPanel();
            foot.Margin = new Thickness(0, Theme.Space3, 0, 0);
            languageNote.FontSize = Theme.MicroSize;
            languageNote.Foreground = Theme.TextMuted;
            languageNote.TextWrapping = TextWrapping.Wrap;
            languageNote.Margin = new Thickness(0, 0, 0, Theme.Space2);
            foot.Children.Add(languageNote);
            if (!full)
            {
                WrapPanel versions = new WrapPanel();
                Button(versions, Text("code-baseline.txt", "Back to the generated version"), delegate { Baseline(); }, false);
                Button(versions, Text("code-undo.txt", "Undo the last change taken in"), delegate { Undo(); }, false);
                Button(versions, Text("code-folder.txt", "Code folder"), delegate { Open(project.Folder); }, false);
                foot.Children.Add(versions);
            }
            Grid.SetRow(foot, 2);
            pane.Children.Add(foot);

            Border card = Panel(pane);
            card.Margin = new Thickness(0, 0, Theme.Space3, 0);
            return card;
        }

        // ---------- the editor ----------

        private UIElement EditorPane()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(AutoRow());
            pane.RowDefinitions.Add(StarRow());
            // A viewport shorter than this is not a place to read a file in. The
            // row takes a scrollbar before it takes the editor's height.
            pane.RowDefinitions[1].MinHeight = Theme.EditorMinHeight;

            StackPanel head = new StackPanel();
            head.Margin = new Thickness(0, 0, 0, Theme.Space3);
            moduleLine.FontSize = Theme.SectionSize;
            moduleLine.FontWeight = FontWeights.Bold;
            moduleLine.Foreground = Theme.Text;
            moduleLine.TextWrapping = TextWrapping.Wrap;
            head.Children.Add(moduleLine);
            moduleLead.FontSize = Theme.MetaSize;
            moduleLead.Foreground = Theme.TextMuted;
            moduleLead.TextWrapping = TextWrapping.Wrap;
            moduleLead.LineHeight = Theme.MetaSize * Theme.BodyLine;
            moduleLead.Margin = new Thickness(0, Theme.Space1, 0, 0);
            head.Children.Add(moduleLead);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            UIElement box = editor.Build();
            Grid.SetRow(box, 1);
            pane.Children.Add(box);

            return Panel(pane);
        }

        // ---------- the assistant ----------

        // A column, not a strip. Everything it holds is a short line or a narrow
        // control, and what it does have a lot of - the difference that comes
        // back - reads down the page rather than across it.
        private UIElement Assistant()
        {
            Grid pane = new Grid();
            pane.RowDefinitions.Add(AutoRow());
            pane.RowDefinitions.Add(StarRow());

            StackPanel head = new StackPanel();
            head.Children.Add(Heading(Text("code-ai-title.txt", "Ask an assistant")));
            head.Children.Add(Note(Text("code-ai-note.txt",
                "The request is one copy and one paste. It carries what you are asking for and how to answer; the code, the recording and the ledger are in the two files you attach with it.")));

            requestBox.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            requestBox.AcceptsReturn = true;
            requestBox.TextWrapping = TextWrapping.Wrap;
            requestBox.MinHeight = 84;
            requestBox.MaxHeight = 140;
            requestBox.Margin = new Thickness(0, Theme.Space3, 0, 0);
            requestBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (requestBox.Text.Length == 0)
            {
                requestBox.Text = Text("code-ai-request-default.txt",
                    "Make this run reliably against the recorded application. Keep every safety rule in section 10 of the attached file.");
            }
            System.Windows.Automation.AutomationProperties.SetName(requestBox, Text("code-ai-request-name.txt", "What to ask for"));
            head.Children.Add(requestBox);

            // The order is the order of the job: copy the request, open the
            // folder holding what goes with it, then bring the answer back.
            StackPanel row = new StackPanel();
            row.Margin = new Thickness(0, Theme.Space3, 0, 0);
            copyButton.Content = Text("code-ai-copy.txt", "Copy the request");
            copyButton.Margin = new Thickness(0, 0, 0, Theme.Space2);
            copyButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            copyButton.Click += delegate { CopyRequest(); };
            row.Children.Add(copyButton);
            Stacked(row, Text("code-ai-folder.txt", "Open the folder with the two files to attach"), delegate { OpenAttachments(); });
            Stacked(row, Text("code-ai-paste.txt", "Take the answer in from the clipboard"), delegate { TakeIn(); });
            Stacked(row, Text("code-ai-restart.txt", "Start the intake again"), delegate { RestartIntake(); });
            head.Children.Add(row);
            PaintCopy();

            attachLine.FontSize = Theme.MicroSize;
            attachLine.TextWrapping = TextWrapping.Wrap;
            attachLine.Foreground = Theme.TextMuted;
            attachLine.LineHeight = Theme.MicroSize * Theme.BodyLine;
            attachLine.Margin = new Thickness(0, Theme.Space2, 0, 0);
            head.Children.Add(attachLine);
            PaintAttachments();

            intakeLine.FontSize = Theme.MetaSize;
            intakeLine.TextWrapping = TextWrapping.Wrap;
            intakeLine.Foreground = Theme.TextMuted;
            intakeLine.LineHeight = Theme.MetaSize * Theme.BodyLine;
            intakeLine.Margin = new Thickness(0, Theme.Space2, 0, 0);
            head.Children.Add(intakeLine);
            Grid.SetRow(head, 0);
            pane.Children.Add(head);

            diffHost.Margin = new Thickness(0, Theme.Space3, 0, 0);
            ScrollViewer diffScroll = new ScrollViewer();
            diffScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            diffScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            diffScroll.Content = diffHost;
            Grid.SetRow(diffScroll, 1);
            pane.Children.Add(diffScroll);

            Border card = Panel(pane);
            card.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            return card;
        }

        private void Stacked(Panel into, string label, Action action)
        {
            Button button = new Button();
            button.Content = label;
            button.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, Theme.Space2);
            button.Click += delegate { action(); };
            into.Children.Add(button);
        }

        // Says which two files go with the request and whether they are on disk.
        // A request that names an attachment nobody can attach is a request that
        // cannot be answered, and that is worth knowing before it is pasted.
        private void PaintAttachments()
        {
            List<HandoffAttachment> attachments = Handoff.Build(session, project, "", Handoff.NewRequestId()).Attachments;
            StringBuilder text = new StringBuilder();
            text.Append(folderOpened
                ? Text("code-ai-attach-opened.txt", "The folder is open. These are the two files to attach")
                : Text("code-ai-attach.txt", "Attach these two files with the request"));
            text.Append(": ");
            bool missing = false;
            for (int index = 0; index < attachments.Count; index++)
            {
                if (index != 0) text.Append("   ");
                HandoffAttachment attachment = attachments[index];
                text.Append(attachment.Name).Append(" ");
                if (attachment.Exists)
                {
                    text.Append("(").Append(Kb(attachment.Bytes)).Append(")");
                }
                else
                {
                    missing = true;
                    text.Append("- ").Append(Text("code-ai-attach-missing.txt", "not written yet"));
                }
            }
            attachLine.Text = text.ToString();
            attachLine.Foreground = missing ? Theme.CautionText : Theme.TextMuted;
        }

        private static string Kb(long bytes)
        {
            long kb = bytes / 1024;
            if (kb < 1) kb = 1;
            return kb.ToString(CultureInfo.InvariantCulture) + " KB";
        }

        // ---------- language, module and file state ----------

        // Redraws from what the project now holds. Switching language keeps
        // whatever was typed, but putting a different version in place must not:
        // the editor is still showing the old text at that moment, and writing
        // it back would undo the change that was just made.
        private void Refresh()
        {
            ready = false;
            ShowLanguage(project.Language);
        }

        private void ShowLanguage(string languageName)
        {
            Remember();
            project.Language = languageName;
            List<CodeFile> files = project.Files(languageName);
            bool here = false;
            for (int index = 0; index < files.Count; index++)
            {
                if (String.Equals(files[index].Name, currentFile, StringComparison.OrdinalIgnoreCase)) here = true;
            }
            if (!here && files.Count > 0) currentFile = files[0].Name;
            PaintSwitcher();
            PaintTree();
            LoadEditor();
        }

        // A tree with a shape, not a list of file names.
        //
        // Under each form the procedure comes first, because that is the one
        // anybody opens this screen to change. What the recording found comes
        // second. The three modules that carry the operations out are gathered
        // under one heading that says what they are for and stays shut, because
        // a person taking one step out of a recording should never have to read
        // five hundred lines of machinery to find the line they want. Nothing is
        // removed by being folded: the heading is on screen, it says how many
        // files are inside it, and it opens.
        private void PaintTree()
        {
            loading = true;
            tree.Items.Clear();
            AddLanguage(ScriptLanguages.PowerShell, Text("code-lang-ps.txt", "PowerShell"));
            AddLanguage(ScriptLanguages.Vba, Text("code-lang-vba.txt", "VBA"));
            loading = false;
        }

        private void AddLanguage(string languageName, string label)
        {
            bool here = String.Equals(languageName, project.Language, StringComparison.Ordinal);
            TreeViewItem head = Node();
            head.Header = GroupHeader(label, null);
            head.IsExpanded = here;
            System.Windows.Automation.AutomationProperties.SetName(head, label);

            TreeViewItem runtimeGroup = null;
            int runtimeFiles = 0;
            int runtimeLines = 0;
            TreeViewItem first = null;
            List<CodeFile> files = project.Files(languageName);
            for (int index = 0; index < files.Count; index++)
            {
                CodeFile file = files[index];
                TreeViewItem item = ModuleItem(file);
                if (first == null) first = item;
                if (String.Equals(file.Role, CodeRoles.Runtime, StringComparison.Ordinal))
                {
                    if (runtimeGroup == null)
                    {
                        runtimeGroup = Node();
                        runtimeGroup.IsExpanded = false;
                        System.Windows.Automation.AutomationProperties.SetName(runtimeGroup,
                            Text("code-group-runtime.txt", "The machinery"));
                        head.Items.Add(runtimeGroup);
                    }
                    runtimeFiles++;
                    runtimeLines += CodeProject.LineCount(file.Text);
                    runtimeGroup.Items.Add(item);
                }
                else
                {
                    head.Items.Add(item);
                }
            }
            if (runtimeGroup != null)
            {
                runtimeGroup.Header = GroupHeader(Text("code-group-runtime.txt", "The machinery"),
                    runtimeFiles.ToString(CultureInfo.InvariantCulture) + " " + Text("code-intake-files.txt", "file(s)"));
                // Choosing the heading is choosing the first thing under it,
                // rather than choosing nothing and leaving the rail on a row
                // that is not what the editor is showing.
                TreeViewItem group = runtimeGroup;
                group.Selected += delegate(object sender, RoutedEventArgs args)
                {
                    args.Handled = true;
                    if (loading) return;
                    group.IsExpanded = true;
                    if (group.Items.Count > 0) ((TreeViewItem)group.Items[0]).IsSelected = true;
                };
            }
            TreeViewItem opener = first;
            head.Selected += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                if (loading || opener == null) return;
                head.IsExpanded = true;
                opener.IsSelected = true;
            };
            tree.Items.Add(head);
        }

        private TreeViewItem ModuleItem(CodeFile file)
        {
            TreeViewItem item = Node();
            item.Header = ModuleHeader(file);
            item.Tag = file.Language + "/" + file.Name;
            // The name a test and the assistant protocol both use is the file
            // name, so that is what this reports to the outside. What a person
            // reads is the role, which is on the row itself.
            System.Windows.Automation.AutomationProperties.SetName(item, file.FileName);
            item.Selected += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                if (loading) return;
                Choose(file.Language, file.Name);
            };
            if (String.Equals(file.Language, project.Language, StringComparison.Ordinal) &&
                String.Equals(file.Name, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
            }
            return item;
        }

        // A row is built here rather than generated from data, and an item that
        // is already its own container never receives the tree's
        // ItemContainerStyle. Without this every row falls back to the stock
        // template, which paints the system highlight behind a chosen row - and
        // that is a pale blue, under white text, in a dark window. The module
        // that was selected was the one you could not read.
        private static TreeViewItem Node()
        {
            TreeViewItem item = new TreeViewItem();
            item.SetResourceReference(FrameworkElement.StyleProperty, "AppTreeItem");
            return item;
        }

        // A grid rather than a dock panel: the label takes what is left after
        // the count, and takes it as a bounded width so a long name is trimmed
        // with an ellipsis instead of running out past the edge of the pane.
        private static UIElement GroupHeader(string label, string summary)
        {
            Grid head = new Grid();
            head.MinHeight = 28;
            head.ColumnDefinitions.Add(StarColumn());
            head.ColumnDefinitions.Add(AutoColumn());
            TextBlock name = new TextBlock();
            name.Text = label;
            name.FontSize = Theme.LabelSize;
            name.FontWeight = FontWeights.SemiBold;
            name.Foreground = Theme.TextSub;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 0);
            head.Children.Add(name);
            if (summary != null)
            {
                TextBlock count = new TextBlock();
                count.Text = summary;
                count.FontSize = Theme.MicroSize;
                count.Foreground = Theme.TextMuted;
                count.VerticalAlignment = VerticalAlignment.Center;
                count.Margin = new Thickness(Theme.Space2, 0, 0, 0);
                Grid.SetColumn(count, 1);
                head.Children.Add(count);
            }
            return head;
        }

        // The row a person reads: what this module is for, in their language,
        // and under it the file name and its size for when they need to name it
        // to somebody else.
        private UIElement ModuleHeader(CodeFile file)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(AutoColumn());
            row.ColumnDefinitions.Add(StarColumn());

            // The one to edit is marked with a dot rather than by being written
            // in a different colour. A tinted word on a tinted selected row is
            // the pair that goes wrong; a dot beside a plain word is legible
            // whichever of the two the row happens to be.
            Border dot = new Border();
            dot.Width = Theme.Space2;
            dot.Height = Theme.Space2;
            dot.CornerRadius = new CornerRadius(Theme.RadiusPill);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 6, Theme.Space2, 0);
            dot.Background = file.IsWorkflow ? Theme.Accent : Theme.BorderStrong;
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            StackPanel stack = new StackPanel();
            TextBlock role = new TextBlock();
            role.Text = RoleWord(file);
            role.FontSize = Theme.LabelSize;
            role.FontWeight = file.IsWorkflow ? FontWeights.Bold : FontWeights.Normal;
            role.Foreground = Theme.Text;
            role.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(role);
            TextBlock note = new TextBlock();
            StringBuilder text = new StringBuilder();
            text.Append(file.FileName);
            // The count on the procedure is the number of things the operator
            // did, which is what they counted while doing them. The number of
            // calls the plan makes is larger - it includes the waits and the
            // window checks - and reporting that as "steps" makes a recording of
            // nine presses read as twenty eight.
            if (file.IsWorkflow && session != null)
            {
                text.Append("  ").Append(session.Steps.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(" ").Append(Text("list-steps.txt", "actions"));
            }
            text.Append("  ").Append(CodeProject.LineCount(file.Text).ToString(CultureInfo.InvariantCulture))
                .Append(" ").Append(Text("code-lines.txt", "lines"));
            note.Text = text.ToString();
            note.FontSize = Theme.MicroSize;
            note.Foreground = Theme.TextMuted;
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.Margin = new Thickness(0, 1, 0, 0);
            stack.Children.Add(note);
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);
            return row;
        }

        // What the module is for, said as a job rather than as a class name. The
        // five internal names are a fact about how this is built; they are not
        // something to hand a person who wants to take one step out.
        private static string RoleWord(CodeFile file)
        {
            if (file.IsWorkflow) return Messages.Text("code-role-workflow.txt", "the procedure");
            if (String.Equals(file.Role, CodeRoles.Recorded, StringComparison.Ordinal))
            {
                return Messages.Text("code-role-recorded.txt", "what the recording found");
            }
            if (String.Equals(file.Name, CodeModules.RuntimeLocator, StringComparison.Ordinal))
            {
                return Messages.Text("code-role-locator.txt", "finding the element on today's screen");
            }
            if (String.Equals(file.Name, CodeModules.RuntimeNative, StringComparison.Ordinal))
            {
                return Messages.Text("code-role-native.txt", "the calls into Windows");
            }
            return Messages.Text("code-role-core.txt", "carrying an operation out");
        }

        // The sentence under the module title: what this file is, whether it is
        // the one to edit, and what editing it means.
        private static string RoleLead(CodeFile file)
        {
            if (file.IsWorkflow)
            {
                return Messages.Text("code-lead-workflow.txt",
                    "This is the one to edit. One line is one recorded action, in the order it happened.");
            }
            if (String.Equals(file.Role, CodeRoles.Recorded, StringComparison.Ordinal))
            {
                return Messages.Text("code-lead-recorded.txt",
                    "What the recording found. Change this only to aim a step somewhere else.");
            }
            return Messages.Text("code-lead-runtime.txt",
                "How an operation is carried out. You should not need to open this.");
        }

        private void Choose(string languageName, string name)
        {
            Remember();
            bool switched = !String.Equals(languageName, project.Language, StringComparison.Ordinal);
            project.Language = languageName;
            currentFile = name;
            if (switched) PaintSwitcher();
            LoadEditor();
        }

        private void LoadEditor()
        {
            loading = true;
            CodeFile file = project.Find(project.Language, currentFile);
            editor.SetLanguage(project.Language);
            editor.Text = file == null ? "" : file.Text;
            loading = false;
            ready = true;
            PaintModuleLine(file);
            PaintState();
        }

        private void PaintModuleLine(CodeFile file)
        {
            if (file == null)
            {
                moduleLine.Text = Text("code-module-none.txt", "This module is not in the project.");
                moduleLine.Foreground = Theme.CautionText;
                moduleLead.Text = "";
                return;
            }
            moduleLine.Text = RoleWord(file) + "   " + file.FileName;
            moduleLine.Foreground = file.IsWorkflow ? Theme.AccentText : Theme.Text;
            // One sentence, said once. The rule about deleting a line is in the
            // lead already; adding the older hint after it said the same thing
            // twice in a row, in two slightly different wordings.
            moduleLead.Text = RoleLead(file);
        }

        private void Remember()
        {
            if (loading || !ready) return;
            project.SetText(project.Language, currentFile, editor.Text);
        }

        private void PaintState()
        {
            bool changed = project.DiffersFromBaseline(project.Language);
            stateLine.Text = changed
                ? Text("code-state-edited.txt", "Edited since it was generated from the recording.")
                : Text("code-state-generated.txt", "Exactly as it was generated from the recording.");
            stateLine.Foreground = changed ? Theme.CautionText : Theme.SuccessText;
            stateChip.Background = changed ? Theme.CautionSoft : Theme.SuccessSoft;
            stateChip.BorderBrush = changed ? Theme.Caution : Theme.Success;
            StringBuilder note = new StringBuilder();
            note.Append(Text("code-files.txt", "files / lines")).Append(": ").Append(project.Summary(project.Language));
            if (project.Plan != null)
            {
                if (project.Plan.Unsupported > 0)
                {
                    note.Append("   ").Append(Text("code-unsupported.txt", "steps with no address that survives a restart"))
                        .Append(": ").Append(project.Plan.Unsupported.ToString(CultureInfo.InvariantCulture));
                }
                if (String.Equals(project.Language, ScriptLanguages.Vba, StringComparison.Ordinal) && project.Plan.UnreachableFromVba > 0)
                {
                    note.Append("   ").Append(Text("code-vba-unreachable.txt", "steps VBA has no Win32 address for"))
                        .Append(": ").Append(project.Plan.UnreachableFromVba.ToString(CultureInfo.InvariantCulture));
                }
                if (project.Plan.SecretCount > 0)
                {
                    note.Append("   ").Append(Text("code-secrets.txt", "steps that ask the operator for a value"))
                        .Append(": ").Append(project.Plan.SecretCount.ToString(CultureInfo.InvariantCulture));
                }
            }
            languageNote.Text = note.ToString();
        }

        // ---------- check and run ----------

        private void Check()
        {
            Remember();
            string languageName = project.Language;
            string text = editor.Text;
            string module = currentFile;
            Say(Text("code-checking.txt", "Checking..."), null);
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                CheckResult result = ScriptRun.Check(languageName, text, module);
                owner.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ShowCheck(result);
                }));
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
            if (result.Problems.Count > 8)
            {
                text.Append(Environment.NewLine).Append("- ...")
                    .Append((result.Problems.Count - 8).ToString(CultureInfo.InvariantCulture));
            }
            intakeLine.Text = text.ToString();
            intakeLine.Foreground = result.Ok ? Theme.SuccessText : Theme.DangerText;
            Say(result.Headline, result.Ok ? "Success" : "Danger");
        }

        // Runs the whole automation, not the module that happens to be on
        // screen. A workflow on its own is a list of calls into a runtime that
        // is not there, so every module of the language goes to the runner.
        private void RunIt()
        {
            Remember();
            if (askRunConsent != null && !askRunConsent())
            {
                Say(Text("code-run-declined.txt", "The script was not started."), "Caution");
                return;
            }
            string languageName = project.Language;
            List<CodeFile> modules = project.Files(languageName);
            string folder = Path.Combine(project.Folder == null ? Path.GetTempPath() : project.Folder, "run");
            Say(Text("code-running.txt", "Running..."), null);
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                RunResult result = String.Equals(languageName, ScriptLanguages.Vba, StringComparison.Ordinal)
                    ? ScriptRun.RunVba(modules, folder, VbaGen.EntryPoint, 180000)
                    : ScriptRun.RunPowerShellProject(modules, folder, 180000);
                owner.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ShowRun(result);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowRun(RunResult result)
        {
            StringBuilder text = new StringBuilder();
            if (!result.Started)
            {
                text.Append(Text("code-run-nostart.txt", "It was not run.")).Append("  ").Append(result.Problem);
            }
            else if (result.Problem != null)
            {
                text.Append(Text("code-run-stopped.txt", "It stopped.")).Append("  ").Append(result.Problem);
            }
            else
            {
                text.Append(result.Ok
                    ? Text("code-run-done.txt", "It ran to the end.")
                    : Text("code-run-failed.txt", "It ended with a failure."));
                text.Append("  (").Append(result.Method).Append(", exit ")
                    .Append(result.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(")");
            }
            if (result.Output.Length > 0)
            {
                string output = result.Output.Length > 1200 ? result.Output.Substring(0, 1200) + " ..." : result.Output;
                text.Append(Environment.NewLine).Append(output);
            }
            intakeLine.Text = text.ToString();
            bool good = result.Started && result.Problem == null && result.Ok;
            intakeLine.Foreground = good ? Theme.SuccessText : Theme.DangerText;
            Say(good ? Text("code-run-done.txt", "It ran to the end.") : Text("code-run-failed.txt", "It ended with a failure."),
                good ? "Success" : "Danger");
        }

        // ---------- versions ----------

        private void Baseline()
        {
            Remember();
            project.RestoreBaseline(project.Language);
            Save();
            Refresh();
            Say(Text("code-baseline-done.txt", "The generated version is back. The version before it can still be brought back."), "Success");
        }

        private void Undo()
        {
            Remember();
            if (!project.UndoApply())
            {
                Say(Text("code-undo-none.txt", "There is nothing to undo: nothing has been taken in yet."), "Caution");
                return;
            }
            Save();
            Refresh();
            Say(Text("code-undo-done.txt", "The version from before the last change is back."), "Success");
        }

        private void Save()
        {
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        // ---------- out to the assistant ----------

        // One copy. The request is short because everything the assistant has to
        // read is in the two files attached with it, and those are written again
        // here so that what is attached is what is on screen.
        private void CopyRequest()
        {
            Remember();
            // Copying again re-copies the same request. Changing the id here
            // would quietly invalidate an answer the operator is already
            // waiting for; a new request is started deliberately, with the
            // button that says so.
            if (handoff == null)
            {
                project.RequestId = Handoff.NewRequestId();
                Outputs.WriteAll(session, PdfBudgetBytes == null ? ScreensPdf.DefaultBudgetBytes : PdfBudgetBytes(), project);
                handoff = Handoff.Build(session, project, requestBox.Text, project.RequestId);
                Handoff.Write(project, handoff);
                parts = new IntakeParts();
                Save();
                PaintAttachments();
            }
            if (!handoff.AttachmentsReady)
            {
                intakeLine.Text = Text("code-ai-attach-failed.txt",
                    "The files the request tells the assistant to read were not written, so the request was not copied.") +
                    " " + handoff.MissingText();
                intakeLine.Foreground = Theme.DangerText;
                Say(intakeLine.Text, "Danger");
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
            Say(Text("code-copy-done.txt",
                "The request is on the clipboard. Paste it into the chat and attach the two files beside it."), "Success");
        }

        private void PaintCopy()
        {
            copyButton.Content = copied
                ? Text("code-ai-copied.txt", "Request copied - copy it again")
                : Text("code-ai-copy.txt", "Copy the request");
            copyButton.SetResourceReference(FrameworkElement.StyleProperty, copied ? "AppButtonCompact" : "AppButtonPrimary");
        }

        private void OpenAttachments()
        {
            if (session == null || session.AiFolder == null || !Directory.Exists(session.AiFolder))
            {
                Say(Text("code-ai-attach-missing.txt", "not written yet"), "Caution");
                return;
            }
            folderOpened = true;
            PaintAttachments();
            Open(session.AiFolder);
        }

        // ---------- back in from the assistant ----------

        private void RestartIntake()
        {
            parts = new IntakeParts();
            pending = null;
            pendingDiff = null;
            // Starting the intake again means the next copy is a new request.
            // This is the one place the id is deliberately let go of.
            handoff = null;
            copied = false;
            PaintCopy();
            diffHost.Children.Clear();
            intakeLine.Text = Text("code-intake-restart.txt", "The intake starts again. Nothing on screen was changed.");
            intakeLine.Foreground = Theme.TextMuted;
            Say(intakeLine.Text, "Caution");
        }

        private void TakeIn()
        {
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
            if (!parsed.Ok)
            {
                Refused(parsed);
                return;
            }
            if (parsed.HasPart)
            {
                IntakeResult added = Intake.AddPart(parts, parsed);
                if (!added.Ok)
                {
                    Refused(added);
                    return;
                }
                if (!parts.Complete)
                {
                    intakeLine.Text = Text("code-intake-partial.txt", "Part taken in. Still needed") + ": " + parts.MissingText();
                    intakeLine.Foreground = Theme.CautionText;
                    Say(intakeLine.Text, "Caution");
                    return;
                }
                parsed = Intake.Merge(parts);
                if (!parsed.Ok)
                {
                    Refused(parsed);
                    return;
                }
            }
            if (parsed.NoChange != null)
            {
                intakeLine.Text = Refusal(parsed.NoChange) + Environment.NewLine + parsed.Summary;
                intakeLine.Foreground = Theme.CautionText;
                diffHost.Children.Clear();
                Say(Refusal(parsed.NoChange), "Caution");
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
            intakeLine.Text = Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message +
                "  [" + result.Reason + "]";
            intakeLine.Foreground = Theme.DangerText;
            diffHost.Children.Clear();
            Say(Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message, "Danger");
        }

        // Nothing is replaced until this has been read and accepted.
        private void ShowDiff(string summary)
        {
            diffHost.Children.Clear();
            int changed = 0;
            for (int index = 0; index < pendingDiff.Count; index++) if (pendingDiff[index].Changed) changed++;
            StringBuilder head = new StringBuilder();
            head.Append(Text("code-intake-ready.txt", "An answer is ready to be looked at.")).Append("  ")
                .Append(pendingDiff.Count.ToString(CultureInfo.InvariantCulture)).Append(" ")
                .Append(Text("code-intake-files.txt", "file(s)")).Append(", ")
                .Append(changed.ToString(CultureInfo.InvariantCulture)).Append(" ")
                .Append(Text("code-intake-changed.txt", "changed"));
            intakeLine.Text = head.ToString() + (String.IsNullOrEmpty(summary) ? "" : Environment.NewLine + summary);
            intakeLine.Foreground = Theme.Text;
            // The status line still holds whatever happened last. Leaving it
            // there while a difference is waiting puts two answers on one
            // screen, and the older one is the wrong one.
            Say(head.ToString(), null);

            for (int index = 0; index < pendingDiff.Count; index++)
            {
                FileDiff diff = pendingDiff[index];
                int hidden;
                List<DiffLine> lines = Diff.Interesting(diff, out hidden);
                StackPanel rows = new StackPanel();
                for (int line = 0; line < lines.Count && line < 400; line++)
                {
                    rows.Children.Add(DiffRow(lines[line]));
                }
                if (lines.Count > 400)
                {
                    rows.Children.Add(Note(Text("code-diff-more.txt", "further changed lines are not shown here") + ": " +
                        (lines.Count - 400).ToString(CultureInfo.InvariantCulture)));
                }
                if (hidden > 0)
                {
                    rows.Children.Add(Note(Text("code-diff-same.txt", "unchanged lines left out") + ": " +
                        hidden.ToString(CultureInfo.InvariantCulture)));
                }
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.MaxHeight = 180;
                scroll.Content = rows;
                TextBlock summaryBlock;
                Expander fold = Accordion(diff.Language + " / " + diff.Name + "." + ScriptLanguages.Extension(diff.Language) +
                    (diff.IsNew ? "  " + Text("code-diff-new.txt", "(new file)") : ""), scroll, out summaryBlock);
                summaryBlock.Text = diff.Summary;
                fold.IsExpanded = diff.Changed && pendingDiff.Count == 1;
                diffHost.Children.Add(fold);
            }

            WrapPanel row = new WrapPanel();
            row.Margin = new Thickness(0, Theme.Space2, 0, 0);
            Button(row, Text("code-diff-apply.txt", "Take this in"), delegate { ApplyPending(); }, true);
            Button(row, Text("code-diff-drop.txt", "Leave it"), delegate { DropPending(); }, false);
            diffHost.Children.Add(row);
        }

        private UIElement DiffRow(DiffLine line)
        {
            TextBlock block = new TextBlock();
            string mark = line.Kind == DiffLine.Added ? "+ " : (line.Kind == DiffLine.Removed ? "- " : "  ");
            block.Text = mark + line.Text;
            block.FontFamily = Theme.CodeFont;
            block.FontSize = Theme.MicroSize;
            block.TextWrapping = TextWrapping.NoWrap;
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

        private void ApplyPending()
        {
            if (pending == null) return;
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
            diffHost.Children.Clear();
            Save();
            Refresh();
            intakeLine.Text = Text("code-diff-applied.txt", "Taken in. The version from before it can be brought back with the undo button above.");
            intakeLine.Foreground = Theme.SuccessText;
            Say(intakeLine.Text, "Success");
        }

        private void DropPending()
        {
            pending = null;
            pendingDiff = null;
            diffHost.Children.Clear();
            intakeLine.Text = Text("code-diff-dropped.txt", "Left alone. Nothing on screen was changed.");
            intakeLine.Foreground = Theme.TextMuted;
            Say(intakeLine.Text, "Caution");
        }

        // ---------- odds and ends ----------

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

        private void Say(string message, string tone)
        {
            if (say != null) say(message, tone);
        }

        private static RowDefinition AutoRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = GridLength.Auto;
            return row;
        }

        private static RowDefinition StarRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = new GridLength(1, GridUnitType.Star);
            return row;
        }

        private static ColumnDefinition StarColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(1, GridUnitType.Star);
            return column;
        }

        private static ColumnDefinition AutoColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = GridLength.Auto;
            return column;
        }

        private static ColumnDefinition FixedColumn(double width)
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(width);
            return column;
        }

        // A panel that fills the cell it is in. Cards on this screen are columns
        // of a work area rather than blocks stacked down a page, so they carry
        // no bottom margin of their own; the gap between them is the gap the
        // work area puts there.
        private static Border Panel(UIElement content)
        {
            Border card = new Border();
            card.Background = Theme.Surface;
            card.BorderBrush = Theme.Border;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.Padding = new Thickness(Theme.Space4);
            card.Child = content;
            return card;
        }

        private static TextBlock Heading(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.SectionSize;
            block.FontWeight = FontWeights.Bold;
            block.Foreground = Theme.Text;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private static TextBlock Note(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.MetaSize;
            block.Foreground = Theme.TextMuted;
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, Theme.Space1, 0, 0);
            return block;
        }

        // A button in a single row rather than in a wrapping block: it needs a
        // gap to its left and none underneath.
        private static Button BarButton(Panel panel, string label, Action action, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            button.Click += delegate { action(); };
            panel.Children.Add(button);
            return button;
        }

        private static Button Button(Panel panel, string label, RoutedEventHandler handler, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            if (handler != null) button.Click += handler;
            panel.Children.Add(button);
            return button;
        }

        private static Expander Accordion(string caption, UIElement content, out TextBlock summary)
        {
            TextBlock captionBlock = new TextBlock();
            captionBlock.Text = caption;
            captionBlock.FontSize = Theme.LabelSize;
            captionBlock.FontWeight = FontWeights.SemiBold;
            captionBlock.Foreground = Theme.TextSub;
            captionBlock.VerticalAlignment = VerticalAlignment.Center;

            summary = new TextBlock();
            summary.FontSize = Theme.MetaSize;
            summary.Foreground = Theme.TextMuted;
            summary.VerticalAlignment = VerticalAlignment.Center;
            summary.HorizontalAlignment = HorizontalAlignment.Right;
            summary.Margin = new Thickness(Theme.Space2, 0, 0, 0);

            DockPanel header = new DockPanel();
            header.LastChildFill = true;
            DockPanel.SetDock(summary, Dock.Right);
            header.Children.Add(summary);
            header.Children.Add(captionBlock);

            Expander expander = new Expander();
            expander.SetResourceReference(FrameworkElement.StyleProperty, "AppAccordion");
            expander.Header = header;
            expander.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(expander, caption);
            return expander;
        }
    }
}
