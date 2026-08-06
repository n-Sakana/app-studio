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
        private ColumnDefinition centreColumn;
        private ColumnDefinition rightColumn;
        private ColumnDefinition leftSplitColumn;
        private ColumnDefinition rightSplitColumn;
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
        private bool editorOnly;
        private bool diffShowing;

        // The one place the three widths are held.
        //
        // They were previously held in the ColumnDefinitions alone, which two
        // things wrote to: this class, when a pane was folded, and the splitter,
        // when it was dragged. Folding rewrote the outer two from the constants
        // and left the middle at whatever the splitter had made it, so the three
        // no longer summed to anything in particular and every fold moved a pane
        // the operator had not touched. They are shares of one hundred here, they
        // always sum to one hundred, and every arrangement is written from them.
        private double leftShare = Theme.PaneLeftShare;
        private double centreShare = Theme.PaneCentreShare;
        private double rightShare = Theme.PaneRightShare;
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
            // The panes are never allowed to be wider than the room there is, so
            // nothing can be pushed past the right hand edge of the window.
            root.ClipToBounds = true;
            leftColumn = Ui.ShareColumn(Theme.PaneLeftShare);
            centreColumn = Ui.ShareColumn(Theme.PaneCentreShare);
            rightColumn = Ui.ShareColumn(Theme.PaneRightShare);
            leftSplitColumn = Ui.FixedColumn(Theme.SplitterWidth);
            rightSplitColumn = Ui.FixedColumn(Theme.SplitterWidth);
            root.ColumnDefinitions.Add(leftColumn);
            root.ColumnDefinitions.Add(leftSplitColumn);
            root.ColumnDefinitions.Add(centreColumn);
            root.ColumnDefinitions.Add(rightSplitColumn);
            root.ColumnDefinitions.Add(rightColumn);
            // Every change of room re-decides what each pane may keep, so a
            // window narrowed past the sum of the minimums shrinks the panes
            // rather than letting the arrangement overflow. The window is
            // listened to as well as this grid: once the grid is already wider
            // than the window, narrowing the window further does not change the
            // grid's own width, so the grid alone would never hear about it.
            root.SizeChanged += delegate { ApplyWidths(); };
            if (owner != null) owner.SizeChanged += delegate { ApplyWidths(); Settle(); };

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

        // A boundary between two panes.
        //
        // What it changes is read back into the shares the moment the drag ends,
        // because the splitter writes straight into the columns and this class
        // writes them from the shares. Two writers and one set of numbers is how
        // a fold came to move a pane nobody had dragged.
        private GridSplitter Splitter()
        {
            GridSplitter splitter = new GridSplitter();
            splitter.SetResourceReference(FrameworkElement.StyleProperty, "AppSplitter");
            splitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
            // Read after the arrangement has caught up, not during the drag. The
            // splitter writes the two columns it moved and the measured sizes
            // follow a moment later; reading them in the same instant mixes one
            // new width with one old one, and the proportions that come out of
            // that describe an arrangement that never existed - which is how a
            // boundary that had just been dragged came back part of the way and
            // took the pane at the far end of the window with it.
            splitter.DragCompleted += delegate
            {
                if (owner == null) { TakeShares(); return; }
                owner.Dispatcher.BeginInvoke(new Action(TakeShares), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            Ui.Name(splitter, Text("pane-resize.txt", "Pane boundary"),
                Text("pane-resize-note.txt", "Drag sideways to change how wide the panes on either side are."));
            return splitter;
        }

        // What the operator has just dragged the panes to, as shares.
        //
        // A folded pane keeps the share it had. It is not on screen to have been
        // dragged, and forgetting it would mean opening it again at whatever
        // width the arithmetic happened to leave, rather than at the width it was
        // folded from.
        private void TakeShares()
        {
            if (root == null) return;
            double left = leftColumn.ActualWidth;
            double centre = centreColumn.ActualWidth;
            double right = rightColumn.ActualWidth;
            double shown = (leftOpen ? left : 0) + centre + (rightOpen ? right : 0);
            if (shown <= 1) return;
            double budget = 100.0 - (leftOpen ? 0 : leftShare) - (rightOpen ? 0 : rightShare);
            if (budget <= 0) return;
            if (leftOpen) leftShare = budget * left / shown;
            if (rightOpen) rightShare = budget * right / shown;
            centreShare = 100.0 - leftShare - rightShare;
            if (centreShare < 1)
            {
                centreShare = 1;
                double rest = 99.0;
                double sum = leftShare + rightShare;
                if (sum > 0)
                {
                    leftShare = rest * leftShare / sum;
                    rightShare = rest * rightShare / sum;
                }
            }
            ApplyWidths();
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
            bool showLeft = leftOpen && !editorOnly;
            bool showRight = rightOpen && !editorOnly;
            leftSplitter.Visibility = showLeft ? Visibility.Visible : Visibility.Hidden;
            rightSplitter.Visibility = showRight ? Visibility.Visible : Visibility.Hidden;
            leftPane.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
            leftRail.Visibility = (!editorOnly && !leftOpen) ? Visibility.Visible : Visibility.Collapsed;
            rightPane.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
            rightRail.Visibility = (!editorOnly && !rightOpen) ? Visibility.Visible : Visibility.Collapsed;
            ApplyWidths();
            Settle();
            PaintFullButton();
            if (LayoutChanged != null) LayoutChanged();
        }

        // The five columns, worked out here and written as widths rather than as
        // proportions for the grid to work out.
        //
        // Proportions and minimums together are not something a grid settles the
        // way a reader expects: given minimums that do not fit, it keeps them and
        // becomes wider than the window rather than becoming narrower than its
        // minimums, and what hangs over the edge is the last pane and the control
        // that folds it. So the arithmetic is done once, here, from three things
        // - what the operator dragged the panes to, what each pane needs, and how
        // much room there is - and the answer is three exact widths that add up
        // to the room. Nothing downstream can then disagree about the total.
        private void ApplyWidths()
        {
            if (root == null || leftColumn == null) return;
            bool showLeft = leftOpen && !editorOnly;
            bool showRight = rightOpen && !editorOnly;
            bool leftRailOn = !editorOnly && !leftOpen;
            bool rightRailOn = !editorOnly && !rightOpen;

            leftSplitColumn.Width = new GridLength(showLeft ? Theme.SplitterWidth : 0);
            rightSplitColumn.Width = new GridLength(showRight ? Theme.SplitterWidth : 0);

            double fixedTaken = (showLeft ? Theme.SplitterWidth : 0) + (showRight ? Theme.SplitterWidth : 0) +
                (leftRailOn ? Theme.PaneRailWidth : 0) + (rightRailOn ? Theme.PaneRailWidth : 0);
            double had = Available();
            double room = had - fixedTaken;
            if (room < 0) room = 0;

            leftColumn.MinWidth = 0;
            centreColumn.MinWidth = 0;
            rightColumn.MinWidth = 0;

            if (room <= 0)
            {
                // Nothing has been measured yet. The proportions stand until
                // there is a width to divide.
                leftColumn.Width = showLeft ? new GridLength(leftShare, GridUnitType.Star)
                    : new GridLength(leftRailOn ? Theme.PaneRailWidth : 0);
                rightColumn.Width = showRight ? new GridLength(rightShare, GridUnitType.Star)
                    : new GridLength(rightRailOn ? Theme.PaneRailWidth : 0);
                centreColumn.Width = new GridLength(centreShare, GridUnitType.Star);
                return;
            }

            double minLeft = showLeft ? Theme.PaneLeftMin : 0;
            double minRight = showRight ? Theme.PaneRightMin : 0;
            double minCentre = Theme.PaneCentreMin;
            double minSum = minLeft + minCentre + minRight;
            // Too narrow for every minimum at once: they are given up together
            // and in proportion, so no pane is sacrificed to keep another whole.
            if (minSum > room && minSum > 0)
            {
                double give = room / minSum;
                minLeft = minLeft * give;
                minCentre = minCentre * give;
                minRight = minRight * give;
            }

            // Folding a pane gives its width to the middle and to nothing else.
            //
            // So the two side panes are measured against the room there would be
            // with both of them open, and the middle takes whatever is left over.
            // Sharing the freed width out in proportion instead would widen the
            // pane at the far end of the window - a pane the operator had not
            // touched, moving because something at the other end was folded.
            double open = had - Theme.SplitterWidth * 2;
            if (open < 0) open = 0;
            double left = showLeft ? open * leftShare / 100.0 : 0;
            double right = showRight ? open * rightShare / 100.0 : 0;
            double centre = room - left - right;

            // Nothing under what it needs...
            if (left < minLeft) left = minLeft;
            if (right < minRight) right = minRight;
            if (centre < minCentre) centre = minCentre;
            // ...and nothing over the room there is. What the minimums took is
            // paid back by whichever panes are above theirs, in proportion to how
            // far above they are. The minimums fit by now, so this always settles.
            double over = left + centre + right - room;
            if (over > 0.5)
            {
                double spare = (left - minLeft) + (centre - minCentre) + (right - minRight);
                if (spare > 0)
                {
                    left = left - over * (left - minLeft) / spare;
                    centre = centre - over * (centre - minCentre) / spare;
                    right = right - over * (right - minRight) / spare;
                }
            }
            if (left < 0) left = 0;
            if (right < 0) right = 0;
            if (centre < 0) centre = 0;

            leftColumn.Width = showLeft ? new GridLength(left) : new GridLength(leftRailOn ? Theme.PaneRailWidth : 0);
            rightColumn.Width = showRight ? new GridLength(right) : new GridLength(rightRailOn ? Theme.PaneRailWidth : 0);
            centreColumn.Width = new GridLength(centre);

            // Under the width a pane stops holding anything readable, its head
            // drops to the drawings alone. The words are still on them as name
            // and tooltip, so nothing is lost but the room they took.
            SetTight(showRight && right > 0 && right < Theme.PaneFloorWidth * 2.4);
        }

        // Decide the widths again once the window has finished changing size.
        //
        // A window announces its new size while its content is still being
        // arranged, so the cell this grid stands in can still be reporting the
        // width it had a moment ago. Deciding from that number gives the panes
        // more than there is, and because the arrangement is then pinned by its
        // own minimums nothing changes size again - so nothing asks a second
        // time, and the panes stay hanging over the edge until something else
        // moves. This is that second time.
        private void Settle()
        {
            if (owner == null) return;
            owner.Dispatcher.BeginInvoke(new Action(ApplyWidths), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // How much width there actually is.
        //
        // Not this grid's own width: when the columns' minimums add up to more
        // than the window, a Grid does not shrink - it reports the larger number
        // and hangs over the edge. Measuring against that is measuring the
        // symptom, and the arrangement would never be told to give anything up.
        // The cell this grid stands in is the honest number, because a star sized
        // cell is exactly as wide as what is there to fill.
        private double Available()
        {
            if (root == null) return 0;
            FrameworkElement holder = root.Parent as FrameworkElement;
            if (holder == null) return root.ActualWidth;
            double width = holder.ActualWidth - root.Margin.Left - root.Margin.Right;
            if (width <= 0) return root.ActualWidth;
            return width;
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
        public bool EditorOnly { get { return editorOnly; } }

        // Whether the window still shows its bar and its status line. The editor
        // asks for them to go, because a screen kept for reading one file has no
        // room for controls that act on the window around it.
        public Action<bool> ShowChrome;

        // The editor with the window to itself.
        //
        // Folding the two side panes was not this. The bar over them stayed, so
        // what the operator got was the same window with two panes missing - and
        // the thing they had asked for was the file, larger. This takes the bar
        // and the status line with the panes, and the only thing on screen is the
        // file and the one control that gives the rest back.
        public void SetEditorOnly(bool value)
        {
            if (editorOnly == value) return;
            editorOnly = value;
            if (ShowChrome != null) ShowChrome(!editorOnly);
            ApplyPanes();
            if (editorOnly) editor.FocusEditor();
        }

        private void ToggleFull()
        {
            SetEditorOnly(!editorOnly);
        }

        // The standard pair of drawings for this, so it is recognised without
        // being read: the four corners going out, and the four coming back in.
        private void PaintFullButton()
        {
            if (fullButton == null) return;
            fullButton.Content = Icons.Make(editorOnly ? Icons.FullscreenExit : Icons.Fullscreen, 18, Theme.TextSub);
            Ui.Name(fullButton,
                editorOnly ? Text("code-full-exit.txt", "Leave full width") : Text("code-full-enter.txt", "Make the editor full width"),
                editorOnly ? Text("code-full-exit-note.txt", "Puts both side panes back.")
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
            lastWritten = null;
            copied = false;
            parts = new IntakeParts();
            pending = null;
            pendingDiff = null;
            builtPath = null;
            diffShowing = false;
            ready = false;
            currentLanguage = project == null ? ScriptLanguages.PowerShell : project.Language;
            currentFile = CodeModules.Workflow;
            // What the assistant said about the session before this one is not
            // about this one.
            AiSaid("", Theme.TextMuted);
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
            ClearResult();
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

            Grid.SetRow(LanguageSwitch(), 1);
            pane.Children.Add(languageSwitch);

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
            Grid.SetRow(holder, 2);
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
            Grid.SetRow(moduleFoot, 3);
            pane.Children.Add(moduleFoot);

            return Ui.Panel(pane);
        }

        private Border languageSwitch;
        private System.Windows.Controls.Primitives.ToggleButton psSegment;
        private System.Windows.Controls.Primitives.ToggleButton vbaSegment;

        // Which of the two languages this pane is showing.
        //
        // They used to be two headings in one list, next to a third for the
        // wrapper, which read as three peers to choose between - and one of the
        // three is not a thing anybody edits. They are two ways of writing the
        // same recording, so this is one control with two positions rather than
        // two places to go: the pane underneath changes, the pane itself does
        // not, and nothing about it suggests another screen.
        private UIElement LanguageSwitch()
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(Ui.StarColumn());
            row.ColumnDefinitions.Add(Ui.StarColumn());
            psSegment = Ui.Segment(Text("lang-powershell.txt", "PowerShell"),
                Text("lang-powershell-note.txt", "The C# engine and the thin PowerShell wrapper the build puts over it. This is the one that becomes Workflow.cmd."));
            vbaSegment = Ui.Segment(Text("lang-vba.txt", "VBA"),
                Text("lang-vba-note.txt", "The same recorded procedure written for Excel. This is the one that becomes Workflow.xlsm."));
            // Checked rather than Click, because a screen reader and the
            // automation tree turn this control on through its toggle rather
            // than by pressing it, and a handler on Click alone is one this
            // window can be driven past without it ever running. Click puts the
            // mark back on the half that is already chosen, so pressing it twice
            // does not leave both halves blank.
            psSegment.Checked += delegate { ChooseLanguage(ScriptLanguages.PowerShell); };
            vbaSegment.Checked += delegate { ChooseLanguage(ScriptLanguages.Vba); };
            psSegment.Click += delegate { psSegment.IsChecked = true; };
            vbaSegment.Click += delegate { vbaSegment.IsChecked = true; };
            Grid.SetColumn(psSegment, 0);
            Grid.SetColumn(vbaSegment, 1);
            row.Children.Add(psSegment);
            row.Children.Add(vbaSegment);

            languageSwitch = new Border();
            languageSwitch.Background = Theme.SurfaceSunken;
            languageSwitch.BorderBrush = Theme.Border;
            languageSwitch.BorderThickness = new Thickness(1);
            languageSwitch.CornerRadius = new CornerRadius(Theme.RadiusMd);
            languageSwitch.Padding = new Thickness(2);
            languageSwitch.Margin = new Thickness(Theme.Space4, 0, Theme.Space4, Theme.Space3);
            languageSwitch.Child = row;
            System.Windows.Automation.AutomationProperties.SetName(languageSwitch, Text("lang-switch.txt", "Language"));
            return languageSwitch;
        }

        private bool switching;

        private void ChooseLanguage(string languageName)
        {
            if (switching) return;
            if (String.Equals(currentLanguage, languageName, StringComparison.Ordinal) && !IsWrapper(currentFile)) return;
            Remember();
            currentLanguage = languageName;
            currentFile = CodeModules.Workflow;
            if (project != null) project.Language = languageName;
            PaintTree();
            LoadEditor();
            PaintState();
            ShowEditorFace();
        }

        private void PaintLanguageSwitch()
        {
            if (psSegment == null) return;
            bool vba = String.Equals(currentLanguage, ScriptLanguages.Vba, StringComparison.Ordinal);
            switching = true;
            psSegment.IsChecked = !vba;
            vbaSegment.IsChecked = vba;
            switching = false;
            if (languageSwitch != null) languageSwitch.Visibility = project == null ? Visibility.Collapsed : Visibility.Visible;
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

        // What the chosen language is made of, arranged as what it is made of.
        //
        // PowerShell mode is not a list of PowerShell files. It is one handed
        // over file, and that file is a C# engine with a thin PowerShell wrapper
        // over it. Shown as a flat list beside the C#, the wrapper read as a peer
        // of the thing it wraps and as somewhere the operator might work; shown
        // under the file both belong to, the relation is the arrangement. The
        // whole of it is still here to read, including the wrapper.
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
            PaintLanguageSwitch();
            if (!has)
            {
                loading = false;
                return;
            }
            if (String.Equals(currentLanguage, ScriptLanguages.Vba, StringComparison.Ordinal))
            {
                TreeViewItem book = Group(Text("code-artefact-vba.txt", "Workflow.xlsm - the one file handed over"),
                    Text("code-artefact-vba-note.txt", "The workbook the build makes. These five modules go into it as they are."), true);
                List<CodeFile> basic = project.Files(ScriptLanguages.Vba);
                for (int index = 0; index < basic.Count; index++) book.Items.Add(ModuleItem(basic[index]));
                tree.Items.Add(book);
                loading = false;
                return;
            }

            TreeViewItem artefact = Group(Text("code-artefact-ps.txt", "Workflow.cmd - the one file handed over"),
                Text("code-artefact-ps-note.txt", "The single file the build makes. It is the C# below, with a wrapper over it that compiles and calls it."), true);

            TreeViewItem sharp = Group(Text("code-group-cs.txt", "C# (the main thing to edit)"),
                Text("code-group-cs-note.txt", "The automation itself. The recorded procedure is in Workflow.cs."), true);
            List<CodeFile> csharp = project.Files(ScriptLanguages.PowerShell);
            for (int index = 0; index < csharp.Count; index++) sharp.Items.Add(ModuleItem(csharp[index]));
            artefact.Items.Add(sharp);

            TreeViewItem wrapper = Group(Text("code-group-wrapper.txt", "PowerShell wrapper (written by the build)"),
                Text("code-group-wrapper-note.txt", "A thin layer that compiles the C# and calls it. Rewritten on every build."), false);
            wrapper.Items.Add(WrapperItem());
            artefact.Items.Add(wrapper);

            tree.Items.Add(artefact);
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
            PaintLanguageSwitch();
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

            // Four operations, on as many rows as the pane's width needs. In one
            // row that could not fit they went on past the pane and off the side
            // of the window, and the last of them - the one that starts what was
            // just built - was the first to go.
            WrapPanel row = Ui.Row();
            row.Margin = new Thickness(0, 0, 0, Theme.Space1);
            Button check = Ui.IconTextButton(Icons.Check, Text("code-check.txt", "Check"),
                Text("code-check-note.txt", "Only checks that it compiles. Nothing is run."), delegate { Check(); }, false);
            check.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space1);
            row.Children.Add(check);
            Button build = Ui.IconTextButton(Icons.Build, Text("code-build.txt", "Build"),
                Text("code-build-note.txt", "Folds the modules into the single file somebody is given."), delegate { BuildIt(); }, true);
            build.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space1);
            row.Children.Add(build);
            Button run = Ui.IconTextButton(Icons.Play, Text("code-run.txt", "Run"),
                Text("code-run-note.txt", "Runs this automation against the real applications on this machine."), delegate { RunIt(); }, false);
            run.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space1);
            row.Children.Add(run);
            launchButton = Ui.IconTextButton(Icons.Launch, Text("code-launch.txt", "Start the built file"),
                Text("code-launch-note.txt", "Starts the built file the way the person handed it would start it."), delegate { LaunchBuilt(); }, false);
            launchButton.Margin = new Thickness(0, 0, 0, Theme.Space1);
            launchButton.IsEnabled = false;
            row.Children.Add(launchButton);
            Grid.SetRow(row, 0);
            strip.Children.Add(row);

            Grid.SetRow(ResultBox(), 1);
            strip.Children.Add(resultBox);

            buildStrip = new Border();
            buildStrip.BorderBrush = Theme.BorderSubtle;
            buildStrip.BorderThickness = new Thickness(0, 1, 0, 0);
            buildStrip.Padding = new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space3);
            buildStrip.Child = strip;
            return buildStrip;
        }

        private Border resultBox;
        private TextBlock resultHead;
        private StackPanel resultBody;
        private Border resultRule;

        // What checking, building or running just did, beside the code it was
        // done to.
        //
        // It is a region rather than a line of prose. A build produces a verdict,
        // a path to something that now exists on disk, a size and a list of what
        // went into it; written as one paragraph those become a wall with a very
        // long path in the middle of it, and the path - the one part somebody
        // needs to hand to another person - is the hardest thing in it to pick
        // out. Each part is given its own place here, and the path can be copied
        // without being selected by hand.
        private UIElement ResultBox()
        {
            resultHead = new TextBlock();
            resultHead.FontSize = Theme.MetaSize;
            resultHead.FontWeight = FontWeights.SemiBold;
            resultHead.TextWrapping = TextWrapping.Wrap;
            resultHead.Foreground = Theme.TextSub;

            resultBody = new StackPanel();

            StackPanel stack = new StackPanel();
            stack.Children.Add(resultHead);
            stack.Children.Add(resultBody);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.MaxHeight = Theme.ResultHeight;
            scroll.Content = stack;

            Grid inner = new Grid();
            inner.ColumnDefinitions.Add(Ui.AutoColumn());
            inner.ColumnDefinitions.Add(Ui.StarColumn());
            resultRule = new Border();
            resultRule.Width = 3;
            resultRule.CornerRadius = new CornerRadius(2);
            resultRule.Background = Theme.BorderSubtle;
            resultRule.Margin = new Thickness(0, 0, Theme.Space3, 0);
            Grid.SetColumn(resultRule, 0);
            inner.Children.Add(resultRule);
            Grid.SetColumn(scroll, 1);
            inner.Children.Add(scroll);

            resultBox = new Border();
            resultBox.Background = Theme.SurfaceSunken;
            resultBox.BorderBrush = Theme.BorderSubtle;
            resultBox.BorderThickness = new Thickness(1);
            resultBox.CornerRadius = new CornerRadius(Theme.RadiusSm);
            resultBox.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            resultBox.Visibility = Visibility.Collapsed;
            resultBox.Child = inner;
            System.Windows.Automation.AutomationProperties.SetName(resultBox, Text("code-result.txt", "What happened"));
            return resultBox;
        }

        private void ClearResult()
        {
            if (resultBox == null) return;
            resultHead.Text = "";
            resultBody.Children.Clear();
            resultBox.Visibility = Visibility.Collapsed;
        }

        // tone: Success, Danger, Caution or null for "still going".
        private void SayResult(string headline, string tone)
        {
            if (resultBox == null) return;
            resultBody.Children.Clear();
            resultBox.Visibility = Visibility.Visible;
            resultHead.Text = headline;
            Brush ink = Theme.TextMuted;
            Brush rule = Theme.BorderSubtle;
            if (String.Equals(tone, "Success", StringComparison.Ordinal)) { ink = Theme.SuccessText; rule = Theme.Success; }
            else if (String.Equals(tone, "Danger", StringComparison.Ordinal)) { ink = Theme.DangerText; rule = Theme.Danger; }
            else if (String.Equals(tone, "Caution", StringComparison.Ordinal)) { ink = Theme.CautionText; rule = Theme.Caution; }
            resultHead.Foreground = ink;
            resultRule.Background = rule;
        }

        private void ResultLines(List<string> lines, int limit)
        {
            if (lines == null) return;
            for (int index = 0; index < lines.Count && index < limit; index++)
            {
                TextBlock line = Ui.Note(lines[index]);
                line.Margin = new Thickness(0, Theme.Space1, 0, 0);
                resultBody.Children.Add(line);
            }
        }

        // A path is not prose. It is one thing, it does not wrap where a sentence
        // would, and what the reader does with it is copy it - so it is given a
        // box of its own, in the code face, with the control that copies it.
        private void ResultPath(string label, string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            StackPanel block = new StackPanel();
            block.Margin = new Thickness(0, Theme.Space2, 0, 0);
            block.Children.Add(Ui.Note(label));

            Grid row = new Grid();
            row.ColumnDefinitions.Add(Ui.StarColumn());
            row.ColumnDefinitions.Add(Ui.AutoColumn());
            row.ColumnDefinitions[0].MinWidth = 0;
            row.Margin = new Thickness(0, Theme.Space1, 0, 0);

            TextBox box = new TextBox();
            box.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            box.Text = path;
            box.IsReadOnly = true;
            box.FontFamily = Theme.CodeFont;
            box.FontSize = Theme.MetaSize;
            box.TextWrapping = TextWrapping.NoWrap;
            box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Ui.Name(box, label, Text("code-path-note.txt", "The place on disk. It can be selected and copied."));
            Grid.SetColumn(box, 0);
            row.Children.Add(box);

            string copied = path;
            Button copy = Ui.IconButton(Icons.Copy, Text("code-copy-path.txt", "Copy this path"),
                Text("code-copy-path-note.txt", "Puts the path on the clipboard."),
                delegate { CopyText(copied); });
            copy.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            block.Children.Add(row);
            resultBody.Children.Add(block);
        }

        // The names of the modules that went in, as chips. A row of names run
        // together with two spaces between them is one long word to the eye.
        private void ResultChips(List<string> names)
        {
            if (names == null || names.Count == 0) return;
            WrapPanel chips = new WrapPanel();
            chips.Margin = new Thickness(0, Theme.Space2, 0, 0);
            for (int index = 0; index < names.Count; index++)
            {
                TextBlock text = new TextBlock();
                text.Text = names[index];
                text.FontSize = Theme.MicroSize;
                text.FontFamily = Theme.CodeFont;
                text.Foreground = Theme.TextSub;
                Border chip = new Border();
                chip.Background = Theme.Surface;
                chip.BorderBrush = Theme.BorderSubtle;
                chip.BorderThickness = new Thickness(1);
                chip.CornerRadius = new CornerRadius(Theme.RadiusSm);
                chip.Padding = new Thickness(Theme.Space2, 1, Theme.Space2, 1);
                chip.Margin = new Thickness(0, 0, Theme.Space1, Theme.Space1);
                chip.Child = text;
                chips.Children.Add(chip);
            }
            resultBody.Children.Add(chips);
        }

        // What a program printed while it ran. It keeps the shape it was printed
        // in, because that is how the reader finds the line they are looking for.
        private void ResultOutput(string output)
        {
            if (String.IsNullOrEmpty(output)) return;
            TextBox box = new TextBox();
            box.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            box.Text = output;
            box.IsReadOnly = true;
            box.AcceptsReturn = true;
            box.FontFamily = Theme.CodeFont;
            box.FontSize = Theme.MetaSize;
            box.TextWrapping = TextWrapping.Wrap;
            box.MaxHeight = Theme.ResultOutputHeight;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.Margin = new Thickness(0, Theme.Space2, 0, 0);
            Ui.Name(box, Text("code-output.txt", "What it printed"), Text("code-output-note.txt", "Everything the run wrote out. It can be selected and copied."));
            resultBody.Children.Add(box);
        }

        private void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                Say(Text("code-path-copied.txt", "The path is on the clipboard."), "Success");
            }
            catch (Exception exception)
            {
                Say(Text("code-copy-failed.txt", "The clipboard refused the request") + ": " + exception.Message, "Danger");
            }
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

            // The control that folds this pane away is declared before the tabs
            // and given a column that cannot be squeezed. The tabs take what is
            // left and trim. When the tabs held an Auto column they kept their
            // full width at any pane width and pushed the fold control past the
            // right hand edge, which is how narrowing this pane took away the one
            // control that would have widened it again.
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.StarColumn());
            head.ColumnDefinitions.Add(Ui.AutoColumn());
            head.ColumnDefinitions[0].MinWidth = 0;
            head.Margin = new Thickness(Theme.Space3, 0, Theme.Space2, 0);

            Grid tabs = new Grid();
            tabs.ColumnDefinitions.Add(Ui.AutoColumn());
            tabs.ColumnDefinitions.Add(Ui.AutoColumn());
            tabs.HorizontalAlignment = HorizontalAlignment.Left;
            tabs.ClipToBounds = true;
            workflowTab = Ui.Tab(Icons.Workflow, Text("tab-workflow.txt", "Workflow"),
                Text("tab-workflow-note.txt", "What this session holds, in what order it was done, and replay."));
            assistantTab = Ui.Tab(Icons.Assistant, Text("tab-assistant.txt", "Ask an assistant"),
                Text("tab-assistant-note.txt", "Copy the request, and read what comes back as a difference."));
            workflowTab.Checked += delegate { SetTab(TabWorkflow); };
            assistantTab.Checked += delegate { SetTab(TabAssistant); };
            workflowTab.Click += delegate { workflowTab.IsChecked = true; };
            assistantTab.Click += delegate { assistantTab.IsChecked = true; };
            Grid.SetColumn(workflowTab, 0);
            Grid.SetColumn(assistantTab, 1);
            tabs.Children.Add(workflowTab);
            tabs.Children.Add(assistantTab);
            Grid.SetColumn(tabs, 0);
            head.Children.Add(tabs);

            Button fold = Ui.IconButton(Icons.ChevronRight, Text("pane-right-hide.txt", "Fold this pane away"),
                Text("pane-hide-note.txt", "Folds this pane away and gives the room to the editor."),
                delegate { rightOpen = false; ApplyPanes(); });
            fold.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fold, 1);
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
        private Grid requestHolder;
        private bool tight;

        // Under the width its words fit in, a tab keeps its drawing and drops its
        // caption. The caption is still its accessible name and its tooltip, so
        // what is lost is the room the words took and not the words.
        private void SetTight(bool value)
        {
            if (tight == value) return;
            tight = value;
            TrimTab(workflowTab, value);
            TrimTab(assistantTab, value);
        }

        private static void TrimTab(System.Windows.Controls.Primitives.ToggleButton tab, bool value)
        {
            if (tab == null) return;
            Panel row = tab.Content as Panel;
            if (row == null || row.Children.Count < 2) return;
            for (int index = 0; index < row.Children.Count; index++)
            {
                TextBlock text = row.Children[index] as TextBlock;
                if (text != null) text.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            }
        }

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

            // The recorded actions are a list of their own, in a box of their own,
            // with a height of its own. A recording of sixty steps used to make
            // this pane sixty steps long, so reaching replay - which is above
            // them - meant scrolling the whole pane back up past all of them.
            workflowBody.Children.Add(Section(Text("detail-steps.txt", "What was done"),
                verdict.Steps > 0 ? verdict.Steps.ToString(CultureInfo.InvariantCulture) : null,
                Scrolled(StepList(), Theme.StepListHeight)));

            if (session.Limits.Count > 0)
            {
                StackPanel limits = new StackPanel();
                for (int index = 0; index < session.Limits.Count; index++)
                {
                    limits.Children.Add(LimitRow(session.Limits[index], index == 0));
                }
                workflowBody.Children.Add(Section(Text("detail-limits.txt", "What could not be obtained"),
                    session.Limits.Count.ToString(CultureInfo.InvariantCulture),
                    Scrolled(limits, Theme.LimitListHeight)));
            }
        }

        // A named region with what is in it, and how many. Everything variable in
        // this product goes in one of these rather than being written into
        // whatever space was free: a heading says what the reader is looking at,
        // a count says how much of it there is, and the frame says where it stops.
        private static UIElement Section(string title, string count, UIElement body)
        {
            StackPanel block = new StackPanel();
            block.Margin = new Thickness(0, Theme.Space4, 0, 0);
            Grid head = new Grid();
            head.ColumnDefinitions.Add(Ui.StarColumn());
            head.ColumnDefinitions.Add(Ui.AutoColumn());
            head.Margin = new Thickness(0, 0, 0, Theme.Space2);
            TextBlock label = Ui.Label(title);
            Grid.SetColumn(label, 0);
            head.Children.Add(label);
            if (!String.IsNullOrEmpty(count))
            {
                TextBlock number = new TextBlock();
                number.Text = count;
                number.FontSize = Theme.MicroSize;
                number.FontWeight = FontWeights.SemiBold;
                number.Foreground = Theme.TextMuted;
                number.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(number, 1);
                head.Children.Add(number);
            }
            block.Children.Add(head);
            Border frame = new Border();
            frame.Background = Theme.SurfaceSunken;
            frame.BorderBrush = Theme.BorderSubtle;
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(Theme.RadiusSm);
            frame.Padding = new Thickness(Theme.Space2);
            frame.Child = body;
            block.Children.Add(frame);
            return block;
        }

        // A list that stops at a height and scrolls inside itself, so the pane
        // around it stays the length of the pane.
        private static UIElement Scrolled(UIElement body, double maxHeight)
        {
            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.MaxHeight = maxHeight;
            scroll.Content = body;
            return scroll;
        }

        private static UIElement LimitRow(string text, bool first)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(Ui.AutoColumn());
            row.ColumnDefinitions.Add(Ui.StarColumn());
            row.Margin = new Thickness(0, first ? 0 : Theme.Space2, 0, 0);
            Border mark = new Border();
            mark.Width = 3;
            mark.CornerRadius = new CornerRadius(2);
            mark.Background = Theme.Caution;
            mark.Margin = new Thickness(0, 1, Theme.Space2, 1);
            Grid.SetColumn(mark, 0);
            row.Children.Add(mark);
            TextBlock line = Ui.Note(text);
            line.Foreground = Theme.CautionText;
            Grid.SetColumn(line, 1);
            row.Children.Add(line);
            return row;
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
            // One recorded action, as a row of its own: its place in the order, a
            // mark for how it went the last time it was played back, and what it
            // was. Run together as sentences these were a paragraph in which the
            // reader had to find the boundaries between actions themselves.
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                bool bad = step.LastReplay != null && step.LastReplay.State != "done";
                Grid row = new Grid();
                row.ColumnDefinitions.Add(Ui.AutoColumn());
                row.ColumnDefinitions.Add(Ui.AutoColumn());
                row.ColumnDefinitions.Add(Ui.StarColumn());
                row.ColumnDefinitions[2].MinWidth = 0;

                Border mark = new Border();
                mark.Width = 3;
                mark.CornerRadius = new CornerRadius(2);
                mark.Background = bad ? Theme.Caution : Theme.BorderSubtle;
                mark.Margin = new Thickness(0, 0, Theme.Space2, 0);
                Grid.SetColumn(mark, 0);
                row.Children.Add(mark);

                TextBlock number = new TextBlock();
                number.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
                number.FontSize = Theme.MicroSize;
                number.FontWeight = FontWeights.SemiBold;
                number.Foreground = Theme.TextMuted;
                number.MinWidth = 20;
                number.Margin = new Thickness(0, 0, Theme.Space2, 0);
                number.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(number, 1);
                row.Children.Add(number);

                TextBlock label = new TextBlock();
                label.Text = step.Headline;
                label.FontSize = Theme.MetaSize;
                label.LineHeight = Theme.MetaSize * Theme.BodyLine;
                label.TextWrapping = TextWrapping.Wrap;
                label.Foreground = bad ? Theme.CautionText : Theme.TextSub;
                Grid.SetColumn(label, 2);
                row.Children.Add(label);

                Border card = new Border();
                card.Background = index % 2 == 0 ? Theme.Surface : Brushes.Transparent;
                card.CornerRadius = new CornerRadius(Theme.RadiusSm);
                card.Padding = new Thickness(Theme.Space2, Theme.Space1, Theme.Space2, Theme.Space1);
                card.Child = row;
                Ui.Name(card, (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + step.Headline, null);
                body.Children.Add(card);
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
            pickBoxes.Clear();
            if (project == null)
            {
                aiBody.Children.Add(Ui.Empty(Text("empty-ai.txt", "There is no code to discuss yet."),
                    Text("empty-ai-note.txt", "After a recording or a snap, the generated code can be discussed here.")));
                return;
            }
            aiBody.Children.Add(Ui.Note(Text("code-ai-note.txt",
                "Choose what to hand over, write the request, and copy it. Whatever is named in the request is written beside it and nothing else is. What comes back is shown as a difference across the whole editor before anything is replaced.")));

            requestBox.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            requestBox.AcceptsReturn = true;
            requestBox.TextWrapping = TextWrapping.Wrap;
            requestBox.MinHeight = 84;
            requestBox.MaxHeight = 160;
            requestBox.Margin = new Thickness(0, Theme.Space3, 0, 0);
            requestBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            // Empty, and it stays empty. This box used to open with a request
            // already typed into it, so a product that knows nothing about what
            // the operator wants was stating what they wanted - and a request
            // nobody wrote is one that gets sent unread. The prompt behind the
            // box says what the box is for without putting words in it.
            System.Windows.Automation.AutomationProperties.SetName(requestBox, Text("code-ai-request-name.txt", "What to ask for"));
            Detach(requestBox);
            if (requestHolder == null)
            {
                requestHolder = Ui.Placeholder(requestBox, Text("code-ai-request-hint.txt", "Type what to ask the assistant for"));
            }
            Detach(requestHolder);
            aiBody.Children.Add(requestHolder);

            // What goes with it, one thing at a time. It sits between what is
            // being asked and the button that sends it, because that is the order
            // the decisions are made in.
            aiBody.Children.Add(PickCard());

            // What the selection costs, said before anything is generated and
            // never in the way of generating it.
            if (pickWarnings == null) pickWarnings = new StackPanel();
            Detach(pickWarnings);
            aiBody.Children.Add(pickWarnings);

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

            // What the last generation actually produced, read back from the
            // files it produced. It is not a restatement of the selection: a
            // selection is what was asked for, and this is what exists.
            if (generatedBox == null)
            {
                generatedBody = new StackPanel();
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.MaxHeight = Theme.PickHeight;
                scroll.Content = generatedBody;
                generatedBox = new Border();
                generatedBox.Background = Theme.SurfaceSunken;
                generatedBox.BorderBrush = Theme.BorderSubtle;
                generatedBox.BorderThickness = new Thickness(1);
                generatedBox.CornerRadius = new CornerRadius(Theme.RadiusSm);
                generatedBox.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
                generatedBox.Margin = new Thickness(0, Theme.Space3, 0, 0);
                generatedBox.ClipToBounds = true;
                generatedBox.Child = scroll;
                System.Windows.Automation.AutomationProperties.SetName(generatedBox,
                    Text("ai-made-title.txt", "What was generated"));
            }
            Detach(generatedBox);
            aiBody.Children.Add(generatedBox);

            // What the last exchange with the assistant came to, in a place of
            // its own rather than as a sentence left under the last button. It is
            // hidden until there is something to say, so an empty tab is not a
            // tab with a blank line at the bottom of it.
            aiLine.FontSize = Theme.MetaSize;
            aiLine.LineHeight = Theme.MetaSize * Theme.BodyLine;
            aiLine.TextWrapping = TextWrapping.Wrap;
            aiLine.Foreground = Theme.TextMuted;
            Detach(aiLine);
            if (aiBox == null)
            {
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.MaxHeight = Theme.ResultHeight;
                scroll.Content = aiLine;
                aiBox = new Border();
                aiBox.Background = Theme.SurfaceSunken;
                aiBox.BorderBrush = Theme.BorderSubtle;
                aiBox.BorderThickness = new Thickness(1);
                aiBox.CornerRadius = new CornerRadius(Theme.RadiusSm);
                aiBox.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
                aiBox.Margin = new Thickness(0, Theme.Space3, 0, 0);
                aiBox.Child = scroll;
                System.Windows.Automation.AutomationProperties.SetName(aiBox, Text("code-ai-said.txt", "What came of the last exchange"));
            }
            else
            {
                ScrollViewer holder = aiBox.Child as ScrollViewer;
                if (holder != null) holder.Content = aiLine;
            }
            aiBox.Visibility = aiLine.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Detach(aiBox);
            aiBody.Children.Add(aiBox);

            PaintPickState();
            PaintGenerated();
        }

        private Border aiBox;
        private readonly List<CheckBox> pickBoxes = new List<CheckBox>();
        private StackPanel pickWarnings;
        private TextBlock pickCount;
        private StackPanel generatedBody;
        private Border generatedBox;
        private Outputs.RequestOutputs lastWritten;
        // Set while every box is being moved at once, so the fourteen handlers
        // that fire do not each rebuild the panel underneath the press that
        // started it.
        private bool pickSetting;

        // The list of what can be handed over.
        //
        // It is a flat list of items with a sentence each, in a fixed order, with
        // a ceiling and a scrollbar. There is no preset, no recommended set and no
        // grouping that acts: the four headings are labels over a continuous list,
        // not switches of their own. Nothing in here is ever disabled - every
        // combination, including none of them, is allowed and is generated as
        // asked.
        private UIElement PickCard()
        {
            StackPanel stack = new StackPanel();
            stack.Children.Add(Ui.Label(Text("ai-picks-title.txt", "What to hand over")));
            pickCount = Ui.Note("");
            pickCount.Margin = new Thickness(0, Theme.Space1, 0, 0);
            stack.Children.Add(pickCount);
            stack.Children.Add(Ui.Note(Text("ai-picks-note.txt",
                "Each one is separate. Only what is ticked is written, and the request describes exactly that.")));

            WrapPanel both = Ui.Row();
            both.Margin = new Thickness(0, Theme.Space2, 0, 0);
            Button all = Ui.IconTextButton(Icons.Check, Text("ai-pick-all.txt", "Tick all"),
                Text("ai-pick-all-note.txt", "Ticks every item once. It is not remembered and nothing re-applies it."),
                delegate { SelectAll(true); }, false);
            all.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space1);
            both.Children.Add(all);
            Button none = Ui.IconTextButton(Icons.Cross, Text("ai-pick-none.txt", "Clear all"),
                Text("ai-pick-none-note.txt", "Unticks every item once. A request with nothing attached is allowed."),
                delegate { SelectAll(false); }, false);
            none.Margin = new Thickness(0, 0, 0, Theme.Space1);
            both.Children.Add(none);
            stack.Children.Add(both);

            StackPanel list = new StackPanel();
            list.Margin = new Thickness(0, Theme.Space2, Theme.Space2, 0);
            PickGroup(list, Text("ai-group-record.txt", "About the recording"), AiItems.Context());
            PickGroup(list, Text("ai-group-code.txt", "The code"), new string[] { AiItems.Engine, AiItems.Vba, AiItems.Wrapper });
            PickGroup(list, Text("ai-group-attach.txt", "A separate attachment"), new string[] { AiItems.Pdf });
            PickGroup(list, Text("ai-group-answer.txt", "How the answer comes back"), new string[] { AiItems.Protocol });

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.MaxHeight = Theme.PickHeight;
            scroll.Content = list;
            stack.Children.Add(scroll);

            Border card = new Border();
            card.Background = Theme.SurfaceSunken;
            card.BorderBrush = Theme.BorderSubtle;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusSm);
            card.Padding = new Thickness(Theme.Space3, Theme.Space3, Theme.Space3, Theme.Space3);
            card.Margin = new Thickness(0, Theme.Space3, 0, 0);
            card.ClipToBounds = true;
            card.Child = stack;
            return card;
        }

        private void PickGroup(Panel into, string title, string[] ids)
        {
            TextBlock label = Ui.Note(title);
            label.FontWeight = FontWeights.SemiBold;
            label.Foreground = Theme.TextSub;
            label.Margin = new Thickness(0, Theme.Space3, 0, Theme.Space2);
            into.Children.Add(label);
            for (int index = 0; index < ids.Length; index++)
            {
                AiItem item = AiItems.Of(ids[index]);
                CheckBox box = Ui.Check(item.Label, item.Note, project.Picks.Has(item.Id));
                string id = item.Id;
                box.Checked += delegate { SetPick(id, true); };
                box.Unchecked += delegate { SetPick(id, false); };
                pickBoxes.Add(box);
                into.Children.Add(Ui.CheckBlock(box, item.Note));
            }
        }

        // One item changed, and nothing else.
        //
        // No other item is read and no other item is written. What is dropped is
        // the last generation: files written for a different selection are not
        // this selection's handover, and leaving the summary of them on screen
        // would present them as if they were.
        private void SetPick(string id, bool on)
        {
            if (pickSetting || project == null) return;
            project.Picks.Set(id, on);
            Invalidate();
            PaintPickState();
        }

        private void SelectAll(bool on)
        {
            if (project == null) return;
            pickSetting = true;
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++) project.Picks.Set(order[index], on);
            for (int index = 0; index < pickBoxes.Count; index++) pickBoxes[index].IsChecked = on;
            pickSetting = false;
            Invalidate();
            PaintPickState();
        }

        // Whatever was generated belongs to the selection it was generated from.
        // Once that changes there is nothing ready any more, and saying so is the
        // difference between a stale attachment and a missing one.
        private void Invalidate()
        {
            handoff = null;
            copied = false;
            lastWritten = null;
            PaintCopy();
            PaintGenerated();
            SavePicks();
        }

        // Only the note beside the code is rewritten. Ticking a box does not
        // change a module, so it does not rewrite twenty module files.
        private void SavePicks()
        {
            if (project == null) return;
            string problem = project.SaveMeta();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        private void PaintPickState()
        {
            if (project == null) return;
            if (pickCount != null)
            {
                pickCount.Text = project.Picks.Count.ToString(CultureInfo.InvariantCulture) + " / " +
                    AiItems.Order().Length.ToString(CultureInfo.InvariantCulture) + "   " +
                    Text("ai-picks-chosen.txt", "ticked");
            }
            if (pickWarnings == null) return;
            pickWarnings.Children.Clear();
            List<AiWarning> warnings = project.Picks.Warnings();
            for (int index = 0; index < warnings.Count; index++)
            {
                pickWarnings.Children.Add(WarningRow(warnings[index].Text));
            }
            pickWarnings.Margin = new Thickness(0, warnings.Count == 0 ? 0 : Theme.Space3, 0, 0);
        }

        // A consequence, stated. It is not an error and it does not stop
        // anything: it says what will be missing from the handover and leaves the
        // decision where it was.
        private UIElement WarningRow(string message)
        {
            TextBlock text = new TextBlock();
            text.Text = message;
            text.FontSize = Theme.MetaSize;
            text.LineHeight = Theme.MetaSize * Theme.BodyLine;
            text.TextWrapping = TextWrapping.Wrap;
            text.Foreground = Theme.CautionText;
            Border row = new Border();
            row.Background = Theme.SurfaceSunken;
            row.BorderBrush = Theme.CautionText;
            row.BorderThickness = new Thickness(2, 0, 0, 0);
            row.CornerRadius = new CornerRadius(0, Theme.RadiusSm, Theme.RadiusSm, 0);
            row.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            row.Margin = new Thickness(0, 0, 0, Theme.Space2);
            row.ClipToBounds = true;
            row.Child = text;
            Ui.Name(row, Text("ai-warn-name.txt", "What this selection leaves out"), message);
            return row;
        }

        // What the last generation produced, counted from the files themselves.
        private void PaintGenerated()
        {
            if (generatedBody == null || generatedBox == null) return;
            generatedBody.Children.Clear();
            if (lastWritten == null || handoff == null)
            {
                generatedBox.Visibility = Visibility.Collapsed;
                return;
            }
            generatedBox.Visibility = Visibility.Visible;
            SessionMdResult markdown = lastWritten.Markdown;
            generatedBody.Children.Add(Ui.Label(Text("ai-made-title.txt", "What was generated")));

            WrapPanel stats = new WrapPanel();
            stats.Margin = new Thickness(0, Theme.Space2, 0, 0);
            Stat(stats, markdown == null ? 0 : markdown.Sections.Count, Text("ai-made-parts.txt", "parts"));
            Stat(stats, markdown == null ? 0 : markdown.EngineModules, Text("ai-made-cs.txt", "C# modules"));
            Stat(stats, markdown == null ? 0 : markdown.VbaModules, Text("ai-made-vba.txt", "VBA modules"));
            Stat(stats, lastWritten.Pdf == null || !lastWritten.Pdf.Written ? 0 : lastWritten.Pdf.PageCount,
                Text("ai-made-pages.txt", "PDF pages"));
            generatedBody.Children.Add(stats);

            // The files that exist, with what each one weighs. Named from the
            // handover, so a file that is not an attachment is not listed as one.
            generatedBody.Children.Add(Sub(Text("ai-made-files.txt", "Files written for this request")));
            if (handoff.Attachments.Count == 0)
            {
                generatedBody.Children.Add(Ui.Note(Text("ai-made-none.txt",
                    "No attachment. The request carries the question on its own and says so.")));
            }
            for (int index = 0; index < handoff.Attachments.Count; index++)
            {
                HandoffAttachment attachment = handoff.Attachments[index];
                generatedBody.Children.Add(FileRow(attachment.Name, attachment.Bytes));
            }
            if (handoff.Path != null) generatedBody.Children.Add(FileRow("request.md", Weigh(handoff.Path)));

            if (markdown != null && markdown.Sections.Count > 0)
            {
                generatedBody.Children.Add(Sub(Text("ai-made-included.txt", "Parts included, in this order")));
                WrapPanel chips = new WrapPanel();
                for (int index = 0; index < markdown.Sections.Count; index++)
                {
                    SessionMdSection section = markdown.Sections[index];
                    // The number is the part's place in the file this time, and
                    // the words are the item that was ticked. The heading as it
                    // is written in the file is on the tooltip, so the two can be
                    // matched up without the chip being in a language nobody
                    // chose in.
                    chips.Children.Add(Chip(section.Number.ToString(CultureInfo.InvariantCulture) + ". " +
                        AiItems.Of(section.Id).Label, section.Title));
                }
                generatedBody.Children.Add(chips);
            }

            List<string> applied = new List<string>();
            if (markdown != null) applied.AddRange(markdown.LimitsApplied);
            if (lastWritten.Pdf != null && lastWritten.Pdf.Written)
            {
                applied.Add("screens.pdf: " + lastWritten.Pdf.PageCount.ToString(CultureInfo.InvariantCulture) +
                    " page(s), " + lastWritten.Pdf.SizeText + ", stored " + lastWritten.Pdf.Quality +
                    ", budget " + (lastWritten.Pdf.BudgetBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB.");
            }
            if (lastWritten.Pdf != null && !lastWritten.Pdf.Written && lastWritten.Pdf.Problem != null)
            {
                applied.Add(lastWritten.Pdf.Problem);
            }
            for (int index = 0; index < lastWritten.Removed.Count; index++)
            {
                applied.Add(Text("ai-made-removed.txt", "Left over from an earlier request and taken out of the attachment folder") +
                    ": " + lastWritten.Removed[index]);
            }
            generatedBody.Children.Add(Sub(Text("ai-made-limits.txt", "Ceilings, compression and omissions applied")));
            if (applied.Count == 0)
            {
                generatedBody.Children.Add(Ui.Note(Text("ai-made-nolimits.txt", "None. Nothing was cut, shrunk or left out.")));
            }
            for (int index = 0; index < applied.Count; index++)
            {
                TextBlock line = Ui.Note("- " + applied[index]);
                line.Margin = new Thickness(0, 0, 0, Theme.Space1);
                generatedBody.Children.Add(line);
            }
        }

        private static long Weigh(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static TextBlock Sub(string text)
        {
            TextBlock block = Ui.Note(text);
            block.FontWeight = FontWeights.SemiBold;
            block.Foreground = Theme.TextSub;
            block.Margin = new Thickness(0, Theme.Space3, 0, Theme.Space1);
            return block;
        }

        private static UIElement FileRow(string name, long bytes)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(Ui.StarColumn());
            row.ColumnDefinitions.Add(Ui.AutoColumn());
            row.ColumnDefinitions[0].MinWidth = 0;
            TextBlock label = new TextBlock();
            label.Text = name;
            label.FontSize = Theme.MetaSize;
            label.FontFamily = Theme.CodeFont;
            label.Foreground = Theme.Text;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(label, 0);
            row.Children.Add(label);
            TextBlock size = new TextBlock();
            size.Text = bytes.ToString("#,##0", CultureInfo.InvariantCulture) + " bytes";
            size.FontSize = Theme.MicroSize;
            size.Foreground = Theme.TextMuted;
            size.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            row.Margin = new Thickness(0, 0, 0, Theme.Space1);
            Ui.Name(row, name, name + " - " + size.Text);
            return row;
        }

        private static UIElement Chip(string label, string tooltip)
        {
            TextBlock text = new TextBlock();
            text.Text = label;
            text.FontSize = Theme.MicroSize;
            text.Foreground = Theme.TextSub;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Border chip = new Border();
            chip.Background = Theme.Surface;
            chip.BorderBrush = Theme.BorderSubtle;
            chip.BorderThickness = new Thickness(1);
            chip.CornerRadius = new CornerRadius(Theme.RadiusSm);
            chip.Padding = new Thickness(Theme.Space2, 1, Theme.Space2, 1);
            chip.Margin = new Thickness(0, 0, Theme.Space1, Theme.Space1);
            chip.MaxWidth = 240;
            chip.Child = text;
            Ui.Name(chip, label, tooltip);
            return chip;
        }

        // Everything the assistant tab has to say goes through here, so it always
        // lands in the same place and the place appears only when it is used.
        private void AiSaid(string message, Brush ink)
        {
            aiLine.Text = message;
            aiLine.Foreground = ink;
            if (aiBox != null) aiBox.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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

            WrapPanel choose = Ui.Row();
            choose.VerticalAlignment = VerticalAlignment.Center;
            Button drop = Ui.IconTextButton(Icons.Cross, Text("diff-reject.txt", "Reject"),
                Text("diff-reject-note.txt", "Drops this answer. Nothing on screen changes."), delegate { DropPending(); }, false);
            drop.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space1);
            choose.Children.Add(drop);
            Button apply = Ui.IconTextButton(Icons.Check, Text("diff-apply.txt", "Take this in"),
                Text("diff-apply-note.txt", "Replaces the code exactly as this difference shows."), delegate { ApplyPending(); }, true);
            apply.Margin = new Thickness(0, 0, 0, Theme.Space1);
            choose.Children.Add(apply);
            Grid.SetColumn(choose, 1);
            bar.Children.Add(choose);
            diffBody.Children.Add(bar);

            // An answer that parsed against a request which never asked for this
            // shape is still shown, and can still be accepted - the difference
            // below and the two buttons above are the check. What is not done is
            // presenting it as something the request contracted for.
            if (!ProtocolPromised)
            {
                UIElement caution = WarningRow(Text("diff-noprotocol.txt",
                    "The request that was sent did not ask for a machine readable answer, so nothing here was agreed in advance. Read the difference before taking it in."));
                diffBody.Children.Add(caution);
            }

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
            AiSaid(Text("code-diff-applied.txt", "Taken in."), Theme.SuccessText);
            Say(aiLine.Text, "Success");
        }

        private void DropPending()
        {
            pending = null;
            pendingDiff = null;
            diffBody.Children.Clear();
            ShowEditorFace();
            AiSaid(Text("code-diff-dropped.txt", "Left alone. Nothing on screen was changed."), Theme.TextMuted);
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
            SayResult(Text("code-checking.txt", "Checking..."), null);
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
            SayResult(result.Headline, result.Ok ? "Success" : "Danger");
            List<string> lines = new List<string>();
            lines.Add(Text("code-check-how.txt", "checked by") + ": " + result.Method);
            for (int index = 0; index < result.Problems.Count && index < 8; index++)
            {
                lines.Add("- " + result.Problems[index]);
            }
            if (result.Problems.Count > 8)
            {
                lines.Add(Text("code-check-more.txt", "further problems not shown here") + ": " +
                    (result.Problems.Count - 8).ToString(CultureInfo.InvariantCulture));
            }
            ResultLines(lines, 12);
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
            SayResult(Text("code-building.txt", "Building..."), null);
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

        // A build produces four different kinds of thing, so it is reported as
        // four things rather than as one sentence with a path buried in it: the
        // verdict, the file that now exists, how large it is, and what went into
        // it.
        private void ShowBuild(BuildResult result)
        {
            if (!result.Ok)
            {
                builtPath = null;
                if (launchButton != null) launchButton.IsEnabled = false;
                SayResult(Text("code-build-failed.txt", "Nothing was built."), "Danger");
                List<string> why = new List<string>();
                if (!String.IsNullOrEmpty(result.Problem)) why.Add(result.Problem);
                ResultLines(why, 6);
                Say(Text("code-build-failed.txt", "Nothing was built."), "Danger");
                return;
            }
            builtPath = result.Path;
            if (launchButton != null) launchButton.IsEnabled = true;
            SayResult(Text("code-build-done.txt", "Built one file."), "Success");
            ResultPath(Text("code-built-file.txt", "the file to hand over") + "  (" +
                result.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes)", result.Path);
            ResultChips(result.Modules);
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
            SayResult(Text("code-running.txt", "Running..."), null);
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
            bool good = result.Started && result.Problem == null && result.Ok;
            string headline;
            if (!result.Started) headline = Text("code-run-nostart.txt", "It was not run.");
            else if (result.Problem != null) headline = Text("code-run-stopped.txt", "It stopped.");
            else headline = result.Ok ? Text("code-run-done.txt", "It ran to the end.") : Text("code-run-failed.txt", "It ended with a failure.");
            SayResult(headline, good ? "Success" : "Danger");
            List<string> lines = new List<string>();
            if (!String.IsNullOrEmpty(result.Problem)) lines.Add(result.Problem);
            if (result.Started) lines.Add("exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
            ResultLines(lines, 6);
            // What it printed keeps its own lines, in its own box, because that
            // is what somebody reads to find out where it stopped.
            ResultOutput(result.Output);
            Say(headline, good ? "Success" : "Danger");
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

        // Everything is written again, every time.
        //
        // Nothing is reused: not a document from the last press, not one from an
        // earlier selection, not code from before the last edit. What is attached
        // has to be what is on screen now and what was ticked now, or the answer
        // comes back about something nobody has any more.
        //
        // The request id is the one exception, and deliberately: it is minted once
        // per session on screen, so pressing this again after changing the
        // selection does not silently invalidate an answer somebody is already
        // writing.
        private void CopyRequest()
        {
            if (project == null || session == null) return;
            Remember();
            if (handoff == null || String.IsNullOrEmpty(project.RequestId)) project.RequestId = Handoff.NewRequestId();
            lastWritten = Outputs.WriteForRequest(session,
                PdfBudgetBytes == null ? ScreensPdf.DefaultBudgetBytes : PdfBudgetBytes(), project, project.Picks);
            handoff = Handoff.Build(session, project, requestBox.Text, project.RequestId,
                project.Picks, lastWritten.Markdown, lastWritten.Pdf);
            Handoff.Write(project, handoff);
            parts = new IntakeParts();
            Save();
            if (!handoff.AttachmentsReady)
            {
                AiSaid(Text("code-ai-attach-failed.txt", "The files the request tells the assistant to read were not written, so the request was not copied.") +
                    " " + handoff.MissingText(), Theme.DangerText);
                Say(aiLine.Text, "Danger");
                handoff = null;
                lastWritten = null;
                PaintGenerated();
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
            PaintGenerated();
            // What to do next is said in terms of what was actually made, so it
            // never asks for a file that does not exist.
            string done;
            if (handoff.Attachments.Count == 0)
            {
                done = Text("code-copy-done-alone.txt", "The request is on the clipboard. There is no attachment: paste it on its own.");
            }
            else if (handoff.Attachments.Count == 1)
            {
                done = Text("code-copy-done-one.txt", "The request is on the clipboard. Paste it and attach the one file beside it") +
                    ": " + handoff.Attachments[0].Name;
            }
            else
            {
                done = Text("code-copy-done-many.txt", "The request is on the clipboard. Paste it and attach the files beside it") +
                    ": " + Names(handoff.Attachments);
            }
            AiSaid(done, Theme.SuccessText);
            Say(aiLine.Text, "Success");
        }

        private static string Names(List<HandoffAttachment> attachments)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < attachments.Count; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append(attachments[index].Name);
            }
            return text.ToString();
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

        // Whether the request that was sent actually asked for an answer this can
        // read. When it did not, everything downstream says so: an answer that
        // happens to parse is still an answer to a request that never promised
        // this shape, and it is shown as a difference to be judged rather than
        // presented as something that was contracted for.
        //
        // It is not forbidden. A person looking at a difference and pressing
        // accept is the check that matters, and it is still there.
        private bool ProtocolPromised
        {
            get
            {
                // What the request that was actually sent asked for, while that
                // is still known. Once the selection is changed the generated
                // request is dropped, and the current selection is used instead -
                // which errs towards warning, and warning about an answer that
                // was in fact contracted for costs a sentence, while the reverse
                // costs the operator the reason to look.
                if (handoff != null && handoff.Picks != null) return handoff.Picks.Has(AiItems.Protocol);
                return project != null && project.Picks.Has(AiItems.Protocol);
            }
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
                    AiSaid(Text("code-intake-partial.txt", "Part taken in. Still needed") + ": " + parts.MissingText(), Theme.CautionText);
                    Say(aiLine.Text, "Caution");
                    return;
                }
                parsed = Intake.Merge(parts);
                if (!parsed.Ok) { Refused(parsed); return; }
            }
            if (parsed.NoChange != null)
            {
                AiSaid(Refusal(parsed.NoChange), Theme.CautionText);
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
            string why = Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message;
            // When the request never asked for a marked answer, an answer without
            // the marks is the expected outcome rather than a misbehaving
            // assistant, and saying which of the two it is saves the operator
            // hunting for a fault that is a setting.
            if (!ProtocolPromised)
            {
                why = why + " " + Text("code-intake-noprotocol.txt",
                    "This request did not ask for a machine readable answer, so there is nothing here to read back. Apply the answer by hand, or tick the answer format and ask again.");
            }
            AiSaid(why + "  [" + result.Reason + "]", Theme.DangerText);
            Say(why, "Danger");
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
