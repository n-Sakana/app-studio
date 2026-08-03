namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;

    public enum ShellStep
    {
        Target,
        Menu,
        Scan,
        ScanResult,
        Observe,
        ObserveResult,
        Operate,
        AiRequest,
        AiImport,
        AiRun,
        AiResult,
        History
    }

    public sealed class ShellWindow : Window
    {
        private readonly string baseDir;
        private readonly SessionRecorder session;
        private readonly SessionLog log;
        private readonly ObservationRecorder observation;
        private readonly MouseButtonWatcher mouseWatcher = new MouseButtonWatcher();
        private readonly WinEventMonitor winEvents;
        private readonly OverlayController overlay;
        private readonly HotkeyManager hotkeys;
        private readonly DispatcherTimer hoverTimer;
        private readonly DispatcherTimer healthTimer;
        private readonly DispatcherTimer eventTimer;
        private readonly DispatcherTimer mouseTimer;
        private readonly DispatcherTimer flushTimer;
        private DispatcherTimer autoCloseTimer;

        private readonly TextBlock headerState;
        private readonly TextBlock headerTarget;
        private readonly TextBlock headerSave;
        private readonly TextBlock headerHealth;
        private readonly TextBlock headerMode;
        private readonly Border headerSaveBadge;
        private readonly Border headerHealthBadge;
        private readonly Border headerModeBadge;
        private readonly StackPanel stepRail;
        private readonly Border progressTrack;
        private readonly Border progressFill;
        private readonly TextBlock screenStepTag;
        private readonly StackPanel actionButtons;
        private readonly Button themeSwitch;
        private readonly List<Border> stepRailMarks = new List<Border>();
        private readonly List<TextBlock> stepRailNumbers = new List<TextBlock>();
        private readonly List<TextBlock> stepRailLabels = new List<TextBlock>();
        private readonly Border toastHost;
        private readonly TextBlock toastLabel;
        private readonly TextBlock toastText;
        private readonly DispatcherTimer toastTimer;
        private readonly ScrollViewer workspaceScroll;
        private readonly StackPanel stepHost;
        private readonly Border targetPanel;
        private readonly Border menuPanel;
        private readonly Border scanPanel;
        private readonly Border resultPanel;
        private readonly Border observePanel;
        private readonly Border livePanel;
        private readonly Border operatePanel;
        private readonly ListBox targetList;
        private readonly TextBlock targetHint;
        private readonly Button targetPickButton;
        private readonly TextBlock scanProgressText;
        private readonly TextBlock scanCountText;
        private readonly TextBox resultSummary;
        private readonly ListBox resultList;
        private readonly TextBox resultGaps;
        private readonly TextBlock resultStatParts;
        private readonly TextBlock resultStatWindows;
        private readonly TextBlock resultStatFrame;
        private readonly TextBlock resultElementsSummary;
        private readonly TextBlock resultGapsSummary;
        private readonly TextBlock resultScreensText;
        private readonly Border resultScreensCallout;
        private readonly TextBlock liveFactsSummary;
        private readonly TextBlock memoSummary;
        private readonly TextBlock observeResultStepsSummary;
        private readonly TextBlock aiCollectedSummary;
        private readonly TextBlock detailsSummary;
        private readonly Border operateResultCallout;
        private readonly Border aiRequestCallout;
        private readonly Border aiImportCallout;
        private readonly TextBlock liveTitle;
        private readonly TextBlock liveFacts;
        private readonly TextBlock liveRoute;
        private readonly TextBlock liveValueText;
        private readonly TextBlock observeCounts;
        private readonly Button observePauseButton;
        private readonly TextBlock observeHint;
        private readonly Button freezeButton;
        private readonly TextBox noteInput;
        private readonly Expander memoAccordion;
        private readonly ComboBox probeKind;
        private readonly TextBox probeValue;
        private readonly TextBlock probeValueLabel;
        private readonly CheckBox writeToggle;
        private readonly Button undoButton;
        private readonly TextBlock operateTargetText;
        private readonly TextBlock operateResultText;
        private readonly ComboBox valuePolicy;
        private readonly TextBox diagnosticsText;
        private readonly ListBox timelineList;
        private readonly ListBox pinnedList;
        private readonly TextBlock savePathText;
        private readonly Border aiRequestPanel;
        private readonly Border aiImportPanel;
        private readonly Border aiRunPanel;
        private readonly Border aiResultPanel;
        private readonly Border historyPanel;
        private readonly Border observeResultPanel;
        private readonly TextBox observeResultSummary;
        private readonly ListBox observeResultList;
        private readonly TextBlock observeResultSaved;
        private readonly TextBlock aiCollectedText;
        private readonly TextBox aiGoalInput;
        private readonly TextBlock aiRequestStatus;
        private Button aiCopyButton;
        private Button aiImportGoButton;
        private readonly TextBox aiAnswerInput;
        private readonly TextBlock aiImportStatus;
        private readonly TextBox aiPlanText;
        private readonly CheckBox aiWriteToggle;
        private readonly CheckBox aiStopOnFailure;
        private Button aiRunGoButton;
        private readonly TextBlock aiRunProgress;
        private readonly ListBox aiRunList;
        private readonly TextBox aiResultSummary;
        private readonly TextBlock historyHint;
        private readonly ListBox historyList;
        private readonly TextBox historyDetail;

        private readonly int ownProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private readonly Dictionary<int, string> processNames = new Dictionary<int, string>();
        private readonly List<KeyValuePair<DateTime, string>> recentEvents = new List<KeyValuePair<DateTime, string>>();
        private readonly LiveValuePresenter liveValuePresenter = new LiveValuePresenter();

        private ShellStep step = ShellStep.Target;
        private IntPtr shellHandle;
        private bool inspecting;
        private bool frozen;
        private bool suppressOverlay;
        private bool acquisitionInFlight;
        private bool pendingPoint;
        private int pendingX;
        private int pendingY;
        private const int IdleRefreshMs = 1000;
        private int previousX = Int32.MinValue;
        private int previousY = Int32.MinValue;
        private DateTime lastAcquisitionAt = DateTime.MinValue;
        private int droppedRequests;
        private int lastRestartCount;
        private Snapshot currentSnapshot;
        private AcquisitionView currentView;
        private ProbeResult lastProbe;
        private TargetWindowInfo selectedTarget;
        private int selectedTargetProcessId;
        private bool targetDragActive;
        private Point targetDragStart;
        private ScanRunner scanRunner;
        private ScanResult lastScan;
        private ScreenLedger lastScreens;
        private bool scanRunning;
        private bool screenShotsRunning;
        private ElementRef operateElement;
        private string operateLabel;
        private ElementRecord operateRecord;
        private string hotkeyHint = String.Empty;
        private CaseRecord caseRecord;
        private CaseElementTable caseElements;
        private RequestBundle caseBundle;
        private HandoffBundle caseHandoff;
        // The screens this case was built from, which is not always the screens
        // the tool is looking at now. Keeping them apart is what lets a fresh
        // scan be noticed instead of quietly replacing the ground an answer
        // was written on.
        private ScreenLedger caseScreens;
        private OperationPlan currentPlan;
        private PlanRunner planRunner;
        private PlanRunResult lastRun;
        private bool planRunning;
        private bool premiseMismatch;
        private string answerSha256;
        private bool caseReopened;
        private int[] selectedContentProcessIds = new int[0];
        private bool syncingWriteToggle;
        private CaseRecord historySelection;

        public ShellWindow(string directory, JsonObject diagnostics, int autoCloseMs)
        {
            baseDir = directory;
            Messages.Init(baseDir);
            session = new SessionRecorder(Path.Combine(baseDir, "runtime", "live-session", "shots"), diagnostics);
            log = new SessionLog(Path.Combine(baseDir, "runtime", "live-session", session.Data.Id));
            observation = new ObservationRecorder(log);
            session.Data.RegisterWriteTarget(log.Folder, "automatic session log");

            Theme.Init(baseDir);
            Theme.Install(Resources);

            Title = Text("app-title.txt", "App Studio");
            Width = 600;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 24);
            MinWidth = 520;
            MinHeight = 360;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 12);
            Top = SystemParameters.WorkArea.Top + 12;
            Background = Theme.SurfaceCanvas;
            FontFamily = Theme.UiFont;
            FontSize = Theme.BodySize;
            Foreground = Theme.Text;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // The shell has the same four bands as the design system: who and where
            // on top, the work in the middle, and the one action that moves the
            // run forward pinned to the bottom right. Those bands never move, so
            // the next click is always in the same place.
            DockPanel root = new DockPanel();
            root.LastChildFill = true;
            Content = root;

            Border topbar = new Border();
            topbar.Height = Theme.TopbarHeight;
            topbar.Background = Theme.Get("TopbarBackground");
            topbar.BorderBrush = Theme.BorderSubtle;
            topbar.BorderThickness = new Thickness(0, 0, 0, 1);
            topbar.Padding = new Thickness(Theme.Space3, 0, Theme.Space3, 0);
            DockPanel topRow = new DockPanel();
            topRow.LastChildFill = true;

            StackPanel brand = new StackPanel();
            brand.Orientation = Orientation.Horizontal;
            brand.VerticalAlignment = VerticalAlignment.Center;
            brand.Margin = new Thickness(0, 0, Theme.Space3, 0);
            Border brandMark = new Border();
            brandMark.Width = 26;
            brandMark.Height = 26;
            brandMark.CornerRadius = new CornerRadius(Theme.RadiusMd);
            brandMark.Background = Theme.AccentSoft;
            brandMark.BorderBrush = Theme.Border;
            brandMark.BorderThickness = new Thickness(1);
            TextBlock brandInitials = new TextBlock();
            brandInitials.Text = "AS";
            brandInitials.FontSize = Theme.MicroSize;
            brandInitials.FontWeight = FontWeights.Bold;
            brandInitials.Foreground = Theme.AccentText;
            brandInitials.HorizontalAlignment = HorizontalAlignment.Center;
            brandInitials.VerticalAlignment = VerticalAlignment.Center;
            brandMark.Child = brandInitials;
            brand.Children.Add(brandMark);
            TextBlock brandName = new TextBlock();
            brandName.Text = Text("app-title.txt", "App Studio");
            brandName.FontSize = Theme.LabelSize;
            brandName.FontWeight = FontWeights.Bold;
            brandName.Foreground = Theme.Text;
            brandName.VerticalAlignment = VerticalAlignment.Center;
            brandName.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            brand.Children.Add(brandName);
            DockPanel.SetDock(brand, Dock.Left);
            topRow.Children.Add(brand);

            Button themeButton = new Button();
            themeButton.SetResourceReference(StyleProperty, "AppIconButton");
            themeButton.VerticalAlignment = VerticalAlignment.Center;
            themeButton.Click += delegate { ToggleTheme(); };
            themeButton.Content = Theme.IsDark ? "Light" : "Dark";
            themeButton.ToolTip = Text("theme-toggle.txt", "Switch between light and dark");
            System.Windows.Automation.AutomationProperties.SetName(themeButton, Text("theme-toggle.txt", "Switch between light and dark"));
            themeSwitch = themeButton;
            DockPanel.SetDock(themeButton, Dock.Right);
            topRow.Children.Add(themeButton);

            stepRail = new StackPanel();
            stepRail.Orientation = Orientation.Horizontal;
            stepRail.VerticalAlignment = VerticalAlignment.Center;
            stepRail.HorizontalAlignment = HorizontalAlignment.Center;
            topRow.Children.Add(stepRail);
            topbar.Child = topRow;
            DockPanel.SetDock(topbar, Dock.Top);
            root.Children.Add(topbar);

            progressTrack = new Border();
            progressTrack.Height = Theme.ProgressTrackHeight;
            progressTrack.Background = Theme.SurfaceSunken;
            progressFill = new Border();
            progressFill.Background = Theme.Accent;
            progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            progressFill.Width = 0;
            progressTrack.Child = progressFill;
            progressTrack.SizeChanged += delegate { UpdateProgressFill(); };
            DockPanel.SetDock(progressTrack, Dock.Top);
            root.Children.Add(progressTrack);

            // Screen header: which step this is, and what it is for. The title
            // wraps rather than truncating, because a half sentence is not a
            // shorter sentence.
            Border screenHeader = new Border();
            screenHeader.MinHeight = Theme.ScreenHeaderHeight;
            screenHeader.Background = Theme.Surface;
            screenHeader.BorderBrush = Theme.BorderSubtle;
            screenHeader.BorderThickness = new Thickness(0, 0, 0, 1);
            screenHeader.Padding = new Thickness(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space2);
            StackPanel screenStack = new StackPanel();
            screenStack.VerticalAlignment = VerticalAlignment.Center;
            screenStepTag = new TextBlock();
            screenStepTag.FontSize = Theme.MicroSize;
            screenStepTag.FontWeight = FontWeights.Bold;
            screenStepTag.Foreground = Theme.AccentText;
            screenStack.Children.Add(screenStepTag);
            headerState = new TextBlock();
            headerState.FontSize = Theme.TitleSize;
            headerState.FontWeight = FontWeights.Bold;
            headerState.Foreground = Theme.Text;
            headerState.TextWrapping = TextWrapping.Wrap;
            headerState.Margin = new Thickness(0, 1, 0, 0);
            screenStack.Children.Add(headerState);
            screenHeader.Child = screenStack;
            DockPanel.SetDock(screenHeader, Dock.Top);
            root.Children.Add(screenHeader);

            // Three pieces of state that stay true whatever step is on screen,
            // shown the same way every time: what is allowed, whether reading
            // works, whether the record is on disk.
            Border badgeStrip = new Border();
            badgeStrip.Background = Theme.SurfaceCanvas;
            badgeStrip.BorderBrush = Theme.BorderSubtle;
            badgeStrip.BorderThickness = new Thickness(0, 0, 0, 1);
            badgeStrip.Padding = new Thickness(Theme.Space4, 6, Theme.Space4, 6);
            WrapPanel badges = new WrapPanel();
            headerMode = new TextBlock();
            headerModeBadge = Badge(headerMode, Text("read-only.txt", "Read only"), "Accent");
            headerHealth = new TextBlock();
            headerHealthBadge = Badge(headerHealth, Text("acquisition-starting.txt", "Acquisition: starting"), "Caution");
            headerSave = new TextBlock();
            headerSaveBadge = Badge(headerSave, Text("autosave-on.txt", "Auto save on"), "Success");
            badges.Children.Add(headerModeBadge);
            badges.Children.Add(headerHealthBadge);
            badges.Children.Add(headerSaveBadge);
            badgeStrip.Child = badges;
            DockPanel.SetDock(badgeStrip, Dock.Top);
            root.Children.Add(badgeStrip);

            // Action bar: the target on the left, the buttons that move the run
            // on the right. Same place on every screen.
            Border actionBar = new Border();
            actionBar.MinHeight = Theme.ActionBarHeight;
            actionBar.Background = Theme.Surface;
            actionBar.BorderBrush = Theme.Border;
            actionBar.BorderThickness = new Thickness(0, 1, 0, 0);
            actionBar.Padding = new Thickness(Theme.Space4, Theme.Space2, Theme.Space4, Theme.Space2);
            DockPanel actionRow = new DockPanel();
            actionRow.LastChildFill = true;
            actionButtons = new StackPanel();
            actionButtons.Orientation = Orientation.Horizontal;
            actionButtons.HorizontalAlignment = HorizontalAlignment.Right;
            actionButtons.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(actionButtons, Dock.Right);
            actionRow.Children.Add(actionButtons);
            StackPanel context = new StackPanel();
            context.Orientation = Orientation.Horizontal;
            context.VerticalAlignment = VerticalAlignment.Center;
            context.Margin = new Thickness(0, 0, Theme.Space3, 0);
            Border contextDot = new Border();
            contextDot.Width = 7;
            contextDot.Height = 7;
            contextDot.CornerRadius = new CornerRadius(4);
            contextDot.Background = Theme.Accent;
            contextDot.VerticalAlignment = VerticalAlignment.Center;
            contextDot.Margin = new Thickness(0, 0, Theme.Space2, 0);
            context.Children.Add(contextDot);
            headerTarget = new TextBlock();
            headerTarget.FontSize = Theme.MetaSize;
            headerTarget.Foreground = Theme.TextMuted;
            // Wrapping and trimming together meant neither happened properly: the
            // second line was cut by MaxHeight with no ellipsis, so the pid this
            // line exists to show was lost with nothing saying it had been cut.
            // One line that ends in an ellipsis is honest about being shortened.
            headerTarget.TextTrimming = TextTrimming.CharacterEllipsis;
            headerTarget.TextWrapping = TextWrapping.NoWrap;
            headerTarget.MaxHeight = 34;
            headerTarget.VerticalAlignment = VerticalAlignment.Center;
            context.Children.Add(headerTarget);
            actionRow.Children.Add(context);
            actionBar.Child = actionRow;
            DockPanel.SetDock(actionBar, Dock.Bottom);
            root.Children.Add(actionBar);

            Grid workspace = new Grid();
            ScrollViewer scroll = new ScrollViewer();
            workspaceScroll = scroll;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Padding = new Thickness(Theme.Space4, Theme.Space3, Theme.Space4, Theme.Space4);
            StackPanel body = new StackPanel();
            scroll.Content = body;
            workspace.Children.Add(scroll);

            // A short lived confirmation, in the same shape as a design system
            // toast: it never covers the action bar and never blocks a click.
            toastHost = new Border();
            toastHost.Visibility = Visibility.Collapsed;
            toastHost.HorizontalAlignment = HorizontalAlignment.Center;
            toastHost.VerticalAlignment = VerticalAlignment.Bottom;
            toastHost.Margin = new Thickness(Theme.Space4, 0, Theme.Space4, Theme.Space3);
            toastHost.CornerRadius = new CornerRadius(Theme.RadiusSm);
            toastHost.Background = Theme.Surface;
            toastHost.BorderBrush = Theme.BorderStrong;
            toastHost.BorderThickness = new Thickness(1);
            toastHost.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            toastHost.IsHitTestVisible = false;
            StackPanel toastStack = new StackPanel();
            toastStack.Orientation = Orientation.Horizontal;
            toastLabel = new TextBlock();
            toastLabel.FontSize = Theme.MicroSize;
            toastLabel.FontWeight = FontWeights.SemiBold;
            toastLabel.Foreground = Theme.TextMuted;
            toastLabel.Margin = new Thickness(0, 0, Theme.Space2, 0);
            toastLabel.VerticalAlignment = VerticalAlignment.Center;
            toastStack.Children.Add(toastLabel);
            toastText = new TextBlock();
            toastText.FontSize = Theme.MetaSize;
            toastText.Foreground = Theme.TextSub;
            toastText.TextWrapping = TextWrapping.Wrap;
            toastText.MaxWidth = 380;
            toastStack.Children.Add(toastText);
            toastHost.Child = toastStack;
            workspace.Children.Add(toastHost);
            toastTimer = new DispatcherTimer();
            toastTimer.Interval = TimeSpan.FromMilliseconds(3200);
            toastTimer.Tick += delegate { toastTimer.Stop(); toastHost.Visibility = Visibility.Collapsed; };

            root.Children.Add(workspace);

            BuildStepRail();

            stepHost = new StackPanel();
            body.Children.Add(stepHost);

            // Step 1: choose what to investigate.
            StackPanel targetContent = new StackPanel();
            targetContent.Children.Add(Heading(Text("step-target-title.txt", "1. Choose the screen to investigate")));
            targetHint = Body(Text("step-target-hint.txt", "Pick one of the windows that are on screen now."));
            targetContent.Children.Add(targetHint);
            targetList = new ListBox();
            targetList.SetResourceReference(StyleProperty, "AppListBox");
            targetList.ItemContainerStyle = (Style)Resources["AppListItem"];
            targetList.MinHeight = 190;
            targetList.MaxHeight = 320;
            targetList.Margin = new Thickness(0, 8, 0, 0);
            // A few dozen windows at most, and every row has to be reachable by
            // an assistive tool, so the rows are all built up front.
            VirtualizingStackPanel.SetIsVirtualizing(targetList, false);
            targetList.MouseDoubleClick += delegate { UseSelectedTarget(); };
            targetContent.Children.Add(targetList);
            // Utility actions sit with the thing they act on; the action bar
            // keeps only what moves the run forward.
            WrapPanel targetButtons = new WrapPanel();
            targetButtons.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddButton(targetButtons, Text("target-refresh.txt", "Refresh the list"), delegate { RefreshTargets(); }, false);
            targetPickButton = AddButton(targetButtons, Text("target-point.txt", "Point at it instead"), null, false);
            targetPickButton.PreviewMouseLeftButtonDown += BeginTargetDrag;
            PreviewMouseLeftButtonUp += EndTargetDrag;
            targetContent.Children.Add(targetButtons);
            targetPanel = Card(targetContent);

            // Step 2: choose the purpose.
            StackPanel menuContent = new StackPanel();
            menuContent.Children.Add(Heading(Text("step-menu-title.txt", "2. Choose what to do")));
            menuContent.Children.Add(Choice(
                Text("mode-scan-title.txt", "Find every part automatically"),
                Text("mode-scan-note.txt", "Reads what is on screen now. Nothing is clicked or typed."),
                delegate { StartScan(); }));
            menuContent.Children.Add(Choice(
                Text("mode-observe-title.txt", "Record while I use it myself"),
                Text("mode-observe-note.txt", "Move and click as usual. What you point at is written down for you."),
                delegate { StartObservation(); }));
            menuContent.Children.Add(Choice(
                Text("mode-operate-title.txt", "Try operating one part"),
                Text("mode-operate-note.txt", "Reading is allowed straight away. Anything that changes the target needs an explicit switch."),
                delegate { StartOperate(); }));
            menuContent.Children.Add(Choice(
                Text("mode-ai-title.txt", "Have an assistant work out the operations"),
                Text("mode-ai-note.txt", "Hand the screenshot, the investigation and what you want to do to an assistant, then take its answer back in and try it."),
                delegate { StartCase(); }));
            menuPanel = Card(menuContent);

            // Automatic scan in progress. A count that keeps climbing is the
            // clearest proof that work is happening, so it is the largest thing
            // on the screen.
            StackPanel scanContent = new StackPanel();
            scanContent.Children.Add(Heading(Text("scan-found.txt", "Parts found so far")));
            scanCountText = new TextBlock();
            scanCountText.FontSize = Theme.NumSize + 6;
            scanCountText.FontWeight = FontWeights.Bold;
            scanCountText.Foreground = Theme.AccentText;
            scanCountText.Margin = new Thickness(0, Theme.Space1, 0, 0);
            scanContent.Children.Add(scanCountText);
            scanProgressText = Body(String.Empty);
            scanContent.Children.Add(scanProgressText);
            scanPanel = Card(scanContent);

            // Scan result.
            StackPanel resultContent = new StackPanel();
            resultContent.Children.Add(Heading(Text("step-result-title.txt", "What was found")));
            // The three numbers that answer "did it work" go at the top, large
            // and always visible. The full prose stays underneath, but it no
            // longer pushes the parts list off the screen.
            StackPanel statRow = new StackPanel();
            statRow.Orientation = Orientation.Horizontal;
            statRow.Margin = new Thickness(0, Theme.Space2, 0, 0);
            statRow.Children.Add(StatCard(out resultStatParts, Text("scan-stat-parts.txt", "Parts")));
            statRow.Children.Add(StatCard(out resultStatWindows, Text("scan-stat-windows.txt", "Windows")));
            statRow.Children.Add(StatCard(out resultStatFrame, Text("scan-stat-frame.txt", "Frame parts")));
            resultContent.Children.Add(statRow);
            // Whether the pictures of the scanned screens exist is required
            // reading, not a detail: without them the assistant is asked to work
            // from a table alone. So it sits in the open, never in an accordion.
            resultScreensText = Body(String.Empty);
            resultScreensCallout = Callout(resultScreensText);
            resultScreensCallout.Visibility = Visibility.Collapsed;
            resultContent.Children.Add(resultScreensCallout);
            resultSummary = ReadOnlyText(96);
            resultSummary.MaxHeight = 190;
            resultContent.Children.Add(resultSummary);
            TextBlock resultListHeader = new TextBlock();
            resultListHeader.Text = Text("result-elements.txt", "The parts themselves");
            StackPanel resultElementsContent = new StackPanel();
            resultList = new ListBox();
            resultList.SetResourceReference(StyleProperty, "AppListBox");
            resultList.ItemContainerStyle = (Style)Resources["AppListItem"];
            resultList.MinHeight = 130;
            resultList.MaxHeight = 240;
            resultElementsContent.Children.Add(resultList);
            WrapPanel resultElementButtons = new WrapPanel();
            resultElementButtons.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddButton(resultElementButtons, Text("result-operate.txt", "Try operating the selected part"), delegate { OperateSelectedScanNode(); }, false);
            resultElementsContent.Children.Add(resultElementButtons);
            // Closed, the accordion still says how many parts are inside and how
            // many facts were missed, so nothing has to be opened to find out
            // whether opening it is worthwhile.
            Expander resultElementsAccordion = Accordion(resultListHeader, resultElementsContent, out resultElementsSummary);
            resultContent.Children.Add(resultElementsAccordion);
            resultGaps = ReadOnlyText(110);
            Expander resultGapsAccordion = Accordion(Text("result-gaps.txt", "What could not be obtained"), resultGaps, out resultGapsSummary);
            resultGapsAccordion.Margin = new Thickness(0, Theme.Space2, 0, 0);
            resultContent.Children.Add(resultGapsAccordion);
            WrapPanel resultButtons = new WrapPanel();
            resultButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            AddButton(resultButtons, Text("open-folder.txt", "Open the saved folder"), delegate { OpenLogFolder(); }, false);
            resultContent.Children.Add(resultButtons);
            resultPanel = Card(resultContent);

            // Live element block, shared by manual observation and operation.
            StackPanel liveContent = new StackPanel();
            liveContent.Children.Add(Heading(Text("live-heading.txt", "The part under the pointer")));
            liveTitle = new TextBlock();
            liveTitle.FontSize = 18;
            liveTitle.FontWeight = FontWeights.Bold;
            liveTitle.TextWrapping = TextWrapping.Wrap;
            liveTitle.Foreground = Theme.Text;
            liveTitle.Margin = new Thickness(0, Theme.Space1, 0, 0);
            liveTitle.Text = Text("live-idle.txt", "Move the pointer over the target application.");
            liveContent.Children.Add(liveTitle);
            liveRoute = new TextBlock();
            liveRoute.Margin = new Thickness(0, Theme.Space2, 0, 0);
            liveRoute.TextWrapping = TextWrapping.Wrap;
            liveRoute.FontSize = Theme.MetaSize;
            liveRoute.Foreground = Theme.TextSub;
            liveRoute.Visibility = Visibility.Collapsed;
            liveContent.Children.Add(liveRoute);
            liveValueText = new TextBlock();
            liveValueText.Margin = new Thickness(0, Theme.Space1, 0, 0);
            liveValueText.TextWrapping = TextWrapping.Wrap;
            liveValueText.FontSize = Theme.MetaSize;
            liveValueText.Foreground = Theme.TextMuted;
            liveValueText.Visibility = Visibility.Collapsed;
            liveContent.Children.Add(liveValueText);
            liveFacts = new TextBlock();
            liveFacts.TextWrapping = TextWrapping.Wrap;
            liveFacts.FontFamily = Theme.CodeFont;
            liveFacts.FontSize = Theme.MicroSize;
            liveFacts.Foreground = Theme.TextSub;
            Expander liveDetail = Accordion(Text("live-detail.txt", "Identifying details"), liveFacts, out liveFactsSummary);
            liveDetail.Margin = new Thickness(0, Theme.Space3, 0, 0);
            liveContent.Children.Add(liveDetail);

            livePanel = Card(liveContent);

            StackPanel observeContent = new StackPanel();
            observeCounts = new TextBlock();
            observeCounts.FontSize = Theme.SectionSize;
            observeCounts.FontWeight = FontWeights.Bold;
            observeCounts.Foreground = Theme.Text;
            observeContent.Children.Add(observeCounts);
            observeHint = Note(String.Empty);
            observeContent.Children.Add(observeHint);
            WrapPanel observeButtons = new WrapPanel();
            observeButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            observePauseButton = AddButton(observeButtons, Text("observe-pause.txt", "Pause"), delegate { TogglePause(); }, false);
            freezeButton = AddButton(observeButtons, Text("freeze.txt", "Hold the display"), delegate { ToggleFreeze(); }, false);
            AddButton(observeButtons, Text("pin.txt", "Keep this part"), delegate { PinCurrent(); }, false);
            observeContent.Children.Add(observeButtons);
            // The memo only matters at the moment a part is kept, so it is
            // folded away with its own summary rather than sitting open.
            StackPanel memoContent = new StackPanel();
            noteInput = new TextBox();
            noteInput.SetResourceReference(StyleProperty, "AppTextBox");
            noteInput.MinHeight = 30;
            noteInput.ToolTip = Text("memo-placeholder.txt", "Memo for the next kept part");
            System.Windows.Automation.AutomationProperties.SetName(noteInput, Text("memo-heading.txt", "Memo kept with the next part"));
            noteInput.TextChanged += delegate { UpdateMemoSummary(); };
            memoContent.Children.Add(noteInput);
            memoAccordion = Accordion(Text("memo-heading.txt", "Memo kept with the next part"), memoContent, out memoSummary);
            memoAccordion.Margin = new Thickness(0, Theme.Space3, 0, 0);
            observeContent.Children.Add(memoAccordion);
            UpdateMemoSummary();
            observePanel = Card(observeContent);

            // What was just recorded, read back from the file it was written to
            // so the screen cannot show anything that was not persisted.
            StackPanel observeResultContent = new StackPanel();
            observeResultContent.Children.Add(Heading(Text("step-observeresult-title.txt", "What was recorded")));
            observeResultSummary = ReadOnlyText(96);
            observeResultContent.Children.Add(observeResultSummary);
            observeResultSaved = Note(String.Empty);
            observeResultContent.Children.Add(observeResultSaved);
            observeResultList = new ListBox();
            observeResultList.SetResourceReference(StyleProperty, "AppListBox");
            observeResultList.ItemContainerStyle = (Style)Resources["AppListItem"];
            observeResultList.ItemTemplate = (DataTemplate)Resources["AppWrapRow"];
            observeResultList.MinHeight = 150;
            observeResultList.MaxHeight = 280;
            VirtualizingStackPanel.SetIsVirtualizing(observeResultList, false);
            Expander observeResultAccordion = Accordion(Text("observe-result-steps.txt", "In the order it happened"), observeResultList, out observeResultStepsSummary);
            observeResultAccordion.IsExpanded = true;
            observeResultAccordion.Margin = new Thickness(0, Theme.Space3, 0, 0);
            observeResultContent.Children.Add(observeResultAccordion);
            WrapPanel observeResultButtons = new WrapPanel();
            observeResultButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            AddButton(observeResultButtons, Text("open-folder.txt", "Open the saved folder"), delegate { OpenLogFolder(); }, false);
            observeResultContent.Children.Add(observeResultButtons);
            observeResultPanel = Card(observeResultContent);

            // Operation probe.
            StackPanel operateContent = new StackPanel();
            operateContent.Children.Add(Heading(Text("step-operate-title.txt", "Try operating one part")));
            operateTargetText = Body(Text("operate-none.txt", "No part chosen yet."));
            operateContent.Children.Add(operateTargetText);
            WrapPanel operatePick = new WrapPanel();
            operatePick.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddButton(operatePick, Text("operate-use-live.txt", "Use the part under the pointer"), delegate { UseLiveForOperation(); }, false);
            operateContent.Children.Add(operatePick);
            operateContent.Children.Add(FieldLabel(Text("probe-kind.txt", "Operation")));
            probeKind = new ComboBox();
            probeKind.SetResourceReference(StyleProperty, "AppComboBox");
            probeKind.ItemContainerStyle = (Style)Resources["AppComboItem"];
            System.Windows.Automation.AutomationProperties.SetName(probeKind, Text("probe-kind.txt", "Operation"));
            FillOperationKinds();
            probeKind.SelectionChanged += delegate { UpdateOperateAvailability(); };
            operateContent.Children.Add(probeKind);
            probeValueLabel = FieldLabel(Text("probe-value.txt", "Value used by setValue or keys"));
            operateContent.Children.Add(probeValueLabel);
            probeValue = new TextBox();
            probeValue.SetResourceReference(StyleProperty, "AppTextBox");
            probeValue.ToolTip = Text("probe-value.txt", "Value used by setValue or keys");
            System.Windows.Automation.AutomationProperties.SetName(probeValue, Text("probe-value.txt", "Value used by setValue or keys"));
            operateContent.Children.Add(probeValue);
            writeToggle = PermissionSwitch(Text("write-enable.txt", "Allow operations that change the target in this session"),
                Text("operate-warning.txt", "Only use changing operations on an application where a change is acceptable."));
            writeToggle.Checked += delegate { OnWriteToggle(true); };
            writeToggle.Unchecked += delegate { OnWriteToggle(false); };
            operateContent.Children.Add(PermissionBox(writeToggle,
                Text("operate-warning.txt", "Only use changing operations on an application where a change is acceptable.")));
            WrapPanel operateButtons = new WrapPanel();
            operateButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            undoButton = AddButton(operateButtons, Text("undo-value.txt", "Undo the value change"), delegate { UndoOperation(); }, false);
            undoButton.IsEnabled = false;
            operateContent.Children.Add(operateButtons);
            operateResultText = Body(String.Empty);
            operateResultCallout = Callout(operateResultText);
            operateResultCallout.Visibility = Visibility.Collapsed;
            operateContent.Children.Add(operateResultCallout);
            operatePanel = Card(operateContent);

            // Step A of the assistant flow: what has been collected, and the one
            // free text box the operator fills in. There is deliberately no list
            // of ready made purposes to pick from.
            StackPanel aiRequestContent = new StackPanel();
            aiRequestContent.Children.Add(Heading(Text("step-ai-title.txt", "Build the request for the assistant")));
            aiRequestContent.Children.Add(FieldLabel(Text("ai-goal-heading.txt", "What do you want to do? Write it in your own words.")));
            aiGoalInput = new TextBox();
            aiGoalInput.SetResourceReference(StyleProperty, "AppTextBox");
            aiGoalInput.MinHeight = 72;
            aiGoalInput.AcceptsReturn = true;
            aiGoalInput.TextWrapping = TextWrapping.Wrap;
            aiGoalInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            System.Windows.Automation.AutomationProperties.SetName(aiGoalInput, Text("ai-goal-heading.txt", "What do you want to do? Write it in your own words."));
            aiGoalInput.TextChanged += delegate { UpdateCaseAvailability(); };
            aiRequestContent.Children.Add(aiGoalInput);
            aiRequestContent.Children.Add(Note(Text("ai-goal-note.txt", "Look at the real screen and describe the operation you want. Nothing here is a fixed choice.")));
            WrapPanel aiRequestButtons = new WrapPanel();
            aiRequestButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            AddButton(aiRequestButtons, Text("ai-open-files.txt", "Open the files to attach"), delegate { OpenHandoffFolder(); }, false);
            AddButton(aiRequestButtons, Text("ai-reshoot.txt", "Take the screenshot again"), delegate { TakeCaseScreenshot(true); }, false);
            aiRequestContent.Children.Add(aiRequestButtons);
            aiRequestStatus = Body(String.Empty);
            aiRequestCallout = Callout(aiRequestStatus);
            aiRequestCallout.Visibility = Visibility.Collapsed;
            aiRequestContent.Children.Add(aiRequestCallout);
            // What has already been gathered is a fact the operator can check
            // but rarely needs to read, so it is folded with a live summary.
            aiCollectedText = Body(String.Empty);
            Expander aiCollectedAccordion = Accordion(Text("ai-collected-heading.txt", "What is already gathered"), aiCollectedText, out aiCollectedSummary);
            aiCollectedAccordion.Margin = new Thickness(0, Theme.Space3, 0, 0);
            aiRequestContent.Children.Add(aiCollectedAccordion);
            aiRequestPanel = Card(aiRequestContent);

            // Step B: the answer comes back by hand, is read, and is shown as a
            // list of operations. Nothing runs until that list has been seen.
            StackPanel aiImportContent = new StackPanel();
            aiImportContent.Children.Add(Heading(Text("step-aiimport-title.txt", "Take the answer in")));
            aiImportContent.Children.Add(Note(Text("ai-import-note.txt", "Paste the whole answer. Any explanation around it is ignored.")));
            aiAnswerInput = new TextBox();
            aiAnswerInput.SetResourceReference(StyleProperty, "AppTextBox");
            aiAnswerInput.Margin = new Thickness(0, Theme.Space2, 0, 0);
            aiAnswerInput.MinHeight = 96;
            aiAnswerInput.MaxHeight = 220;
            aiAnswerInput.AcceptsReturn = true;
            aiAnswerInput.TextWrapping = TextWrapping.NoWrap;
            aiAnswerInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            aiAnswerInput.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            aiAnswerInput.FontFamily = Theme.CodeFont;
            aiAnswerInput.FontSize = Theme.LabelSize;
            System.Windows.Automation.AutomationProperties.SetName(aiAnswerInput, Text("ai-import-note.txt", "Paste the whole answer. Any explanation around it is ignored."));
            aiImportContent.Children.Add(aiAnswerInput);
            aiImportStatus = Body(String.Empty);
            aiImportCallout = Callout(aiImportStatus);
            aiImportCallout.Visibility = Visibility.Collapsed;
            aiImportContent.Children.Add(aiImportCallout);
            aiPlanText = ReadOnlyText(120);
            aiPlanText.Margin = new Thickness(0, Theme.Space3, 0, 0);
            // This box carries the read steps, and every reason when an answer is
            // refused. Its label is the heading beside it, so without a stated
            // name assistive technology meets a long unnamed edit box.
            System.Windows.Automation.AutomationProperties.SetName(aiPlanText, Text("ai-plan-text.txt", "The steps that were read, or why the answer cannot be used"));
            aiImportContent.Children.Add(aiPlanText);
            aiWriteToggle = PermissionSwitch(Text("write-enable.txt", "Allow operations that change the target in this session"),
                Text("operate-warning.txt", "Only use changing operations on an application where a change is acceptable."));
            aiWriteToggle.Checked += delegate { OnWriteToggle(true); UpdatePlanAvailability(); };
            aiWriteToggle.Unchecked += delegate { OnWriteToggle(false); UpdatePlanAvailability(); };
            aiImportContent.Children.Add(PermissionBox(aiWriteToggle,
                Text("operate-warning.txt", "Only use changing operations on an application where a change is acceptable.")));
            aiStopOnFailure = new CheckBox();
            aiStopOnFailure.SetResourceReference(StyleProperty, "AppCheckBox");
            aiStopOnFailure.Margin = new Thickness(0, Theme.Space3, 0, 0);
            aiStopOnFailure.IsChecked = true;
            aiStopOnFailure.Content = Text("ai-stop-on-failure.txt", "Stop the rest if a step does not succeed");
            aiImportContent.Children.Add(aiStopOnFailure);
            aiImportPanel = Card(aiImportContent);

            // Step C: running. Only the progress, what each step did, and stop.
            StackPanel aiRunContent = new StackPanel();
            aiRunContent.Children.Add(Heading(Text("step-airun-title.txt", "Running on the target")));
            aiRunProgress = new TextBlock();
            aiRunProgress.FontSize = Theme.NumSize;
            aiRunProgress.FontWeight = FontWeights.Bold;
            aiRunProgress.Foreground = Theme.AccentText;
            aiRunProgress.Margin = new Thickness(0, Theme.Space1, 0, 0);
            aiRunContent.Children.Add(aiRunProgress);
            aiRunList = new ListBox();
            aiRunList.SetResourceReference(StyleProperty, "AppListBox");
            aiRunList.ItemContainerStyle = (Style)Resources["AppListItem"];
            aiRunList.ItemTemplate = (DataTemplate)Resources["AppWrapRow"];
            aiRunList.MinHeight = 120;
            aiRunList.MaxHeight = 220;
            aiRunList.Margin = new Thickness(0, Theme.Space3, 0, 0);
            aiRunContent.Children.Add(aiRunList);
            aiRunPanel = Card(aiRunContent);

            // Step D: what happened, already written into the case folder.
            StackPanel aiResultContent = new StackPanel();
            aiResultContent.Children.Add(Heading(Text("step-airesult-title.txt", "What the operations did")));
            aiResultSummary = ReadOnlyText(150);
            aiResultContent.Children.Add(aiResultSummary);
            WrapPanel aiResultButtons = new WrapPanel();
            aiResultButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            AddButton(aiResultButtons, Text("history-open.txt", "Look at earlier records"), delegate { ShowHistory(); }, false);
            aiResultContent.Children.Add(aiResultButtons);
            aiResultPanel = Card(aiResultContent);

            // History of every case on this machine.
            StackPanel historyContent = new StackPanel();
            historyContent.Children.Add(Heading(Text("step-history-title.txt", "Earlier records")));
            historyHint = Body(String.Empty);
            historyContent.Children.Add(historyHint);
            historyList = new ListBox();
            historyList.SetResourceReference(StyleProperty, "AppListBox");
            historyList.ItemContainerStyle = (Style)Resources["AppListItem"];
            historyList.MinHeight = 120;
            historyList.MaxHeight = 200;
            historyList.Margin = new Thickness(0, Theme.Space3, 0, 0);
            VirtualizingStackPanel.SetIsVirtualizing(historyList, false);
            historyList.SelectionChanged += delegate { ShowHistoryDetail(); };
            historyContent.Children.Add(historyList);
            historyDetail = ReadOnlyText(84);
            historyDetail.MaxHeight = 190;
            historyDetail.Text = Text("history-detail-idle.txt", "Select a record in the list to see it here.");
            historyContent.Children.Add(historyDetail);
            WrapPanel historyButtons = new WrapPanel();
            historyButtons.Margin = new Thickness(0, Theme.Space3, 0, 0);
            AddButton(historyButtons, Text("history-folder.txt", "Open this case folder"), delegate { OpenSelectedCaseFolder(); }, false);
            AddButton(historyButtons, Text("history-refresh.txt", "Refresh"), delegate { RefreshHistory(); }, false);
            historyContent.Children.Add(historyButtons);
            historyPanel = Card(historyContent);

            // Everything a specialist may want to follow, folded away so it
            // never crowds the main route, but stated in the summary so its
            // existence is never a surprise.
            StackPanel detailContent = new StackPanel();
            savePathText = Note(String.Empty);
            detailContent.Children.Add(savePathText);
            WrapPanel detailButtons = new WrapPanel();
            detailButtons.Margin = new Thickness(0, Theme.Space2, 0, 0);
            AddButton(detailButtons, Text("open-folder.txt", "Open the saved folder"), delegate { OpenLogFolder(); }, false);
            AddButton(detailButtons, Text("export.txt", "Write an investigation pack"), delegate { ExportPack(); }, false);
            AddButton(detailButtons, Text("full-shot.txt", "Whole screen picture"), delegate { TakeFullScreenshot(); }, false);
            detailContent.Children.Add(detailButtons);
            detailContent.Children.Add(SubHeading(Text("kept-heading.txt", "Kept parts")));
            pinnedList = new ListBox();
            pinnedList.SetResourceReference(StyleProperty, "AppListBox");
            pinnedList.ItemContainerStyle = (Style)Resources["AppListItem"];
            pinnedList.ItemTemplate = (DataTemplate)Resources["AppWrapRow"];
            pinnedList.MinHeight = 60;
            pinnedList.MaxHeight = 140;
            detailContent.Children.Add(pinnedList);
            detailContent.Children.Add(SubHeading(Text("value-policy-heading.txt", "How much of a value may be recorded")));
            valuePolicy = new ComboBox();
            valuePolicy.SetResourceReference(StyleProperty, "AppComboBox");
            valuePolicy.ItemContainerStyle = (Style)Resources["AppComboItem"];
            System.Windows.Automation.AutomationProperties.SetName(valuePolicy, Text("value-policy-heading.txt", "How much of a value may be recorded"));
            valuePolicy.Items.Add(Text("value-masked.txt", "Length only (default)"));
            valuePolicy.Items.Add(Text("value-full.txt", "Record the text itself"));
            valuePolicy.Items.Add(Text("value-none.txt", "Record nothing about values"));
            valuePolicy.SelectedIndex = 0;
            valuePolicy.SelectionChanged += OnValuePolicyChanged;
            detailContent.Children.Add(valuePolicy);
            detailContent.Children.Add(SubHeading(Text("timeline-heading.txt", "Application events")));
            timelineList = new ListBox();
            timelineList.SetResourceReference(StyleProperty, "AppListBox");
            timelineList.ItemContainerStyle = (Style)Resources["AppListItem"];
            timelineList.ItemTemplate = (DataTemplate)Resources["AppWrapRow"];
            timelineList.MinHeight = 60;
            timelineList.MaxHeight = 140;
            detailContent.Children.Add(timelineList);
            detailContent.Children.Add(SubHeading(Text("diagnostics-heading.txt", "Environment and diagnostics")));
            diagnosticsText = ReadOnlyText(120);
            diagnosticsText.Text = Diagnostics.Summary(diagnostics);
            detailContent.Children.Add(diagnosticsText);
            Expander details = Accordion(Text("details-heading.txt", "Detailed record and settings"), detailContent, out detailsSummary);
            details.Margin = new Thickness(0, Theme.Space3, 0, 0);
            body.Children.Add(details);

            overlay = new OverlayController();
            shellHandle = new WindowInteropHelper(this).EnsureHandle();
            string hotkeySettings = Path.Combine(baseDir, "runtime", "settings", "hotkeys.txt");
            hotkeys = new HotkeyManager(this, hotkeySettings);
            session.Data.RegisterWriteTarget(hotkeySettings, "hotkey settings");
            hotkeys.Pressed += OnHotkey;
            HotkeyRegistration[] registrations = hotkeys.Registrations;
            List<object> hotkeyRecords = new List<object>();
            for (int index = 0; index < registrations.Length; index++)
            {
                AddDiagnostic("HOTKEY " + registrations[index].Action + "=" + registrations[index].Combo + (registrations[index].Registered ? " (active)" : " (UNAVAILABLE)") + (String.IsNullOrEmpty(registrations[index].Reason) ? String.Empty : " " + registrations[index].Reason));
                hotkeyRecords.Add(new JsonObject().Add("action", registrations[index].Action).Add("combo", registrations[index].Combo).Add("registered", registrations[index].Registered).Add("reason", registrations[index].Reason));
            }
            hotkeyHint = BuildHotkeyHint(registrations);
            observeHint.Text = hotkeyHint;

            log.Append("events", new JsonObject()
                .Add("kind", "session.start")
                .Add("sessionId", session.Data.Id)
                .Add("version", App.Version)
                .Add("baseDirectory", baseDir)
                .Add("hotkeys", hotkeyRecords.ToArray()));
            log.WriteText("environment.json", JsonWriter.Write(diagnostics));

            hoverTimer = new DispatcherTimer();
            hoverTimer.Interval = TimeSpan.FromMilliseconds(100);
            hoverTimer.Tick += OnHoverTick;
            hoverTimer.Start();
            healthTimer = new DispatcherTimer();
            healthTimer.Interval = TimeSpan.FromMilliseconds(250);
            healthTimer.Tick += delegate { UpdateHealth(); };
            healthTimer.Start();
            winEvents = new WinEventMonitor();
            eventTimer = new DispatcherTimer();
            eventTimer.Interval = TimeSpan.FromMilliseconds(100);
            eventTimer.Tick += delegate { DrainWinEvents(); };
            eventTimer.Start();
            mouseTimer = new DispatcherTimer();
            mouseTimer.Interval = TimeSpan.FromMilliseconds(50);
            mouseTimer.Tick += delegate { PollMouse(); };
            flushTimer = new DispatcherTimer();
            flushTimer.Interval = TimeSpan.FromMilliseconds(2000);
            flushTimer.Tick += delegate { log.FlushDurable(); UpdateSaveChip(); };
            flushTimer.Start();
            Closed += OnClosed;

            RefreshTargets();
            GoTo(ShellStep.Target);
            UpdateSaveChip();

            if (autoCloseMs > 0)
            {
                autoCloseTimer = new DispatcherTimer();
                autoCloseTimer.Interval = TimeSpan.FromMilliseconds(autoCloseMs);
                autoCloseTimer.Tick += delegate
                {
                    autoCloseTimer.Stop();
                    Close();
                };
                autoCloseTimer.Start();
            }
        }

        public Snapshot CurrentSnapshot { get { return currentSnapshot; } }
        public string AcquisitionHealthText { get { return headerHealth.Text; } }
        public ShellStep Step { get { return step; } }
        public SessionLog Log { get { return log; } }

        // ---------- step handling ----------

        // Only the current step is in the tree at all. A collapsed panel would
        // still be announced by a screen reader and still be found by anything
        // driving the window, which is exactly the confusion being removed.
        private void GoTo(ShellStep next)
        {
            step = next;
            stepHost.Children.Clear();
            // Manual recording is about what is under the pointer, so the live
            // card leads. The operation test already knows its part, so the card
            // that acts leads and the live readout supports it from below.
            if (next == ShellStep.Observe) stepHost.Children.Add(livePanel);
            if (next == ShellStep.Target) stepHost.Children.Add(targetPanel);
            else if (next == ShellStep.Menu) stepHost.Children.Add(menuPanel);
            else if (next == ShellStep.Scan) stepHost.Children.Add(scanPanel);
            else if (next == ShellStep.ScanResult) stepHost.Children.Add(resultPanel);
            else if (next == ShellStep.Observe) stepHost.Children.Add(observePanel);
            else if (next == ShellStep.ObserveResult) stepHost.Children.Add(observeResultPanel);
            else if (next == ShellStep.Operate) { stepHost.Children.Add(operatePanel); stepHost.Children.Add(livePanel); }
            else if (next == ShellStep.AiRequest) stepHost.Children.Add(aiRequestPanel);
            else if (next == ShellStep.AiImport) stepHost.Children.Add(aiImportPanel);
            else if (next == ShellStep.AiRun) stepHost.Children.Add(aiRunPanel);
            else if (next == ShellStep.AiResult) stepHost.Children.Add(aiResultPanel);
            else if (next == ShellStep.History) stepHost.Children.Add(historyPanel);
            BuildActions(next);
            // A new screen starts at its own beginning. Without this the
            // scroller keeps the offset from the previous step and the first
            // thing the operator sees is the middle of the card.
            if (workspaceScroll != null) workspaceScroll.ScrollToTop();
            // A confirmation belongs to the screen that produced it. Carrying it
            // onto the next one makes it read as that screen's state.
            HideToast();
            if (next == ShellStep.AiRequest) UpdateCaseAvailability();
            if (next == ShellStep.AiImport) UpdatePlanAvailability();
            UpdateHeader();
        }

        // ---------- progress ----------

        // Four bands cover every route through the product: pick the target,
        // pick the purpose, do it, read what happened. The AI route has three
        // screens inside the third band; the rail does not grow a step for each
        // of them, because the operator's position in the run has not changed.
        private static readonly string[] RailKeys = new string[] { "rail-target.txt", "rail-purpose.txt", "rail-run.txt", "rail-result.txt" };
        private static readonly string[] RailFallbacks = new string[] { "Target", "Purpose", "Run", "Result" };

        private void BuildStepRail()
        {
            for (int index = 0; index < RailKeys.Length; index++)
            {
                if (index > 0)
                {
                    Border gap = new Border();
                    gap.Width = 10;
                    gap.Height = 1;
                    gap.Background = Theme.Border;
                    gap.VerticalAlignment = VerticalAlignment.Center;
                    gap.Margin = new Thickness(2, 0, 2, 0);
                    stepRail.Children.Add(gap);
                }
                Border chip = new Border();
                chip.CornerRadius = new CornerRadius(Theme.RadiusMd);
                chip.Padding = new Thickness(Theme.Space2, 3, Theme.Space2, 3);
                chip.Background = System.Windows.Media.Brushes.Transparent;
                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                Border mark = new Border();
                mark.Width = Theme.ProgressMarkSize;
                mark.Height = Theme.ProgressMarkSize;
                mark.CornerRadius = new CornerRadius(Theme.ProgressMarkSize / 2);
                mark.BorderBrush = Theme.Border;
                mark.BorderThickness = new Thickness(1);
                mark.VerticalAlignment = VerticalAlignment.Center;
                TextBlock number = new TextBlock();
                number.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
                number.FontSize = Theme.MicroSize;
                number.FontWeight = FontWeights.Bold;
                number.Foreground = Theme.TextMuted;
                number.HorizontalAlignment = HorizontalAlignment.Center;
                number.VerticalAlignment = VerticalAlignment.Center;
                mark.Child = number;
                row.Children.Add(mark);
                TextBlock label = new TextBlock();
                label.Text = Text(RailKeys[index], RailFallbacks[index]);
                label.FontSize = Theme.MetaSize;
                label.Foreground = Theme.TextMuted;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(label);
                chip.Child = row;
                stepRail.Children.Add(chip);
                stepRailMarks.Add(chip);
                stepRailNumbers.Add(number);
                stepRailLabels.Add(label);
                mark.Tag = number;
            }
        }

        private int RailIndex()
        {
            if (step == ShellStep.Target || step == ShellStep.History) return 0;
            if (step == ShellStep.Menu) return 1;
            if (step == ShellStep.ScanResult || step == ShellStep.ObserveResult || step == ShellStep.AiResult) return 3;
            return 2;
        }

        private void UpdateStepRail()
        {
            int active = RailIndex();
            for (int index = 0; index < stepRailMarks.Count; index++)
            {
                Border chip = stepRailMarks[index];
                TextBlock number = stepRailNumbers[index];
                TextBlock label = stepRailLabels[index];
                Border mark = (Border)((StackPanel)chip.Child).Children[0];
                if (index == active)
                {
                    chip.Background = Theme.SurfaceSelected;
                    mark.Background = Theme.Accent;
                    mark.BorderBrush = Theme.Accent;
                    number.Foreground = Theme.TextOnAccent;
                    label.Foreground = Theme.AccentText;
                    label.FontWeight = FontWeights.Bold;
                }
                else if (index < active)
                {
                    chip.Background = System.Windows.Media.Brushes.Transparent;
                    mark.Background = Theme.SuccessSoft;
                    mark.BorderBrush = Theme.Success;
                    number.Foreground = Theme.SuccessText;
                    label.Foreground = Theme.TextSub;
                    label.FontWeight = FontWeights.Normal;
                }
                else
                {
                    chip.Background = System.Windows.Media.Brushes.Transparent;
                    mark.Background = System.Windows.Media.Brushes.Transparent;
                    mark.BorderBrush = Theme.Border;
                    number.Foreground = Theme.TextMuted;
                    label.Foreground = Theme.TextMuted;
                    label.FontWeight = FontWeights.Normal;
                }
            }
            UpdateProgressFill();
        }

        private void UpdateProgressFill()
        {
            if (progressTrack == null || progressFill == null) return;
            double width = progressTrack.ActualWidth;
            if (width <= 0) return;
            double ratio = (RailIndex() + 1) / (double)RailKeys.Length;
            progressFill.Width = Math.Max(0, Math.Min(width, width * ratio));
        }

        // ---------- theme ----------

        private void ToggleTheme()
        {
            Theme.Toggle();
            if (themeSwitch != null) themeSwitch.Content = Theme.IsDark ? "Light" : "Dark";
            string failure = Theme.Persist();
            if (failure != null)
            {
                // A preference that quietly fails to stick is a silent
                // degradation, so it is said out loud.
                Toast(Text("toast-warning.txt", "Warning"), Text("theme-not-saved.txt", "The theme was applied but could not be remembered.") + " " + failure, "Caution");
                AddDiagnostic("Theme preference could not be written: " + failure);
            }
            UpdateStepRail();
            UpdateHealth();
            UpdateSaveChip();
            UpdateModeBadge(writeToggle != null && writeToggle.IsChecked == true);
        }

        // ---------- action bar ----------

        private void BuildActions(ShellStep next)
        {
            actionButtons.Children.Clear();
            if (next == ShellStep.Target)
            {
                Action(Text("history-open.txt", "Look at earlier records"), delegate { ShowHistory(); }, false);
                Action(Text("target-use.txt", "Investigate this window"), delegate { UseSelectedTarget(); }, true);
            }
            else if (next == ShellStep.Menu)
            {
                Action(Text("history-open.txt", "Look at earlier records"), delegate { ShowHistory(); }, false);
                Action(Text("target-change.txt", "Change target"), delegate { GoTo(ShellStep.Target); }, false);
            }
            else if (next == ShellStep.Scan)
            {
                Action(Text("scan-cancel.txt", "Stop"), delegate { CancelScan(); }, false);
            }
            else if (next == ShellStep.ScanResult)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { GoTo(ShellStep.Menu); }, false);
                Action(Text("result-again.txt", "Scan again"), delegate { StartScan(); }, false);
                Action(Text("result-ai.txt", "Hand this to an assistant"), delegate { StartCase(); }, true);
            }
            else if (next == ShellStep.Observe)
            {
                Action(Text("observe-stop.txt", "Finish"), delegate { StopObservation(); }, true);
            }
            else if (next == ShellStep.ObserveResult)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { GoTo(ShellStep.Menu); }, false);
                Action(Text("observe-result-again.txt", "Record again"), delegate { StartObservation(); }, true);
            }
            else if (next == ShellStep.Operate)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { StopOperate(); }, false);
                Action(Text("operate-run.txt", "Run"), delegate { RunOperationProbe(); }, true);
            }
            else if (next == ShellStep.AiRequest)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { GoTo(ShellStep.Menu); }, false);
                aiImportGoButton = Action(Text("ai-goto-import.txt", "Take the answer in"), delegate { GoTo(ShellStep.AiImport); }, false);
                aiCopyButton = Action(Text("ai-copy.txt", "Copy the request text"), delegate { CopyRequestText(); }, true);
            }
            else if (next == ShellStep.AiImport)
            {
                Action(Text("ai-back-request.txt", "Back to the request"), delegate { GoTo(ShellStep.AiRequest); }, false);
                Action(Text("ai-read.txt", "Read what was pasted"), delegate { ImportAnswer(); }, false);
                aiRunGoButton = Action(Text("ai-run.txt", "Run this on the target"), delegate { StartPlanRun(); }, true);
                aiRunGoButton.IsEnabled = false;
            }
            else if (next == ShellStep.AiRun)
            {
                Action(Text("scan-cancel.txt", "Stop"), delegate { CancelPlanRun(); }, false);
            }
            else if (next == ShellStep.AiResult)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { GoTo(ShellStep.Menu); }, false);
                Action(Text("ai-import-again.txt", "Take another answer in"), delegate { GoTo(ShellStep.AiImport); }, false);
                Action(Text("ai-open-case.txt", "Open the case folder"), delegate { OpenCaseFolder(); }, true);
            }
            else if (next == ShellStep.History)
            {
                Action(Text("result-back.txt", "Choose something else"), delegate { GoTo(selectedTarget == null ? ShellStep.Target : ShellStep.Menu); }, false);
                Action(Text("history-continue.txt", "Carry on with this case"), delegate { ContinueSelectedCase(); }, true);
            }
        }

        private Button Action(string label, RoutedEventHandler handler, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            button.SetResourceReference(StyleProperty, primary ? "AppButtonPrimary" : "AppButton");
            if (handler != null) button.Click += handler;
            actionButtons.Children.Add(button);
            return button;
        }

        // ---------- transient confirmation ----------

        private void Toast(string label, string message, string tone)
        {
            if (toastHost == null) return;
            toastLabel.Text = label;
            toastText.Text = message;
            if (String.Equals(tone, "Danger", StringComparison.Ordinal))
            {
                toastHost.BorderBrush = Theme.Danger;
                toastLabel.Foreground = Theme.DangerText;
            }
            else if (String.Equals(tone, "Caution", StringComparison.Ordinal))
            {
                toastHost.BorderBrush = Theme.Caution;
                toastLabel.Foreground = Theme.CautionText;
            }
            else
            {
                toastHost.BorderBrush = Theme.Success;
                toastLabel.Foreground = Theme.SuccessText;
            }
            toastHost.Background = Theme.Surface;
            toastText.Foreground = Theme.TextSub;
            toastHost.Visibility = Visibility.Visible;
            toastTimer.Stop();
            toastTimer.Start();
        }

        private void HideToast()
        {
            if (toastHost == null) return;
            toastTimer.Stop();
            toastHost.Visibility = Visibility.Collapsed;
        }

        private void UpdateHeader()
        {
            string state;
            if (step == ShellStep.Target) state = Text("state-target.txt", "Choose the screen to investigate");
            else if (step == ShellStep.Menu) state = Text("state-menu.txt", "Choose what to do");
            else if (step == ShellStep.Scan) state = Text("state-scan.txt", "Finding parts. The target is not touched.");
            else if (step == ShellStep.ScanResult) state = Text("state-result.txt", "Finished. The result is saved already.");
            else if (step == ShellStep.Observe) state = frozen ? Text("state-observe-frozen.txt", "Display held. Recording continues.") : (observation.Paused ? Text("state-observe-paused.txt", "Recording paused") : Text("state-observe.txt", "Recording while you use the target"));
            else if (step == ShellStep.ObserveResult) state = Text("state-observeresult.txt", "Recording finished. It is saved already.");
            else if (step == ShellStep.AiRequest) state = Text("state-airequest.txt", "Build the request for the assistant");
            else if (step == ShellStep.AiImport) state = Text("state-aiimport.txt", "Check the answer before anything runs");
            else if (step == ShellStep.AiRun) state = Text("state-airun.txt", "Running the operations on the target");
            else if (step == ShellStep.AiResult) state = Text("state-airesult.txt", "Finished. The record is saved already.");
            else if (step == ShellStep.History) state = Text("state-history.txt", "Earlier records");
            else state = Text("state-operate.txt", "Operation test");
            headerState.Text = state;
            int rail = RailIndex();
            screenStepTag.Text = Text("step-tag.txt", "STEP") + " " + (rail + 1) + " / " + RailKeys.Length +
                "   " + Text(RailKeys[rail], RailFallbacks[rail]);
            headerTarget.Text = selectedTarget == null
                ? Text("no-target.txt", "No target chosen")
                : Text("target-label.txt", "Target") + ": " + TargetText(selectedTarget);
            UpdateStepRail();
        }

        private string TargetText(TargetWindowInfo target)
        {
            if (target == null) return "-";
            string name = String.IsNullOrEmpty(target.ProcessName) ? ProcessName(target.ProcessId) : target.ProcessName;
            string title = Shorten(target.DisplayTitle, 42);
            string joined = String.Equals(name, title, StringComparison.OrdinalIgnoreCase) ? name : name + " / " + title;
            return joined + " (pid " + target.ProcessId + ")";
        }

        // ---------- target selection ----------

        private void RefreshTargets()
        {
            TargetWindowInfo[] targets = WindowTools.ListTopLevelWindows();
            targetList.Items.Clear();
            for (int index = 0; index < targets.Length; index++) targetList.Items.Add(TargetItem(targets[index]));
            targetHint.Text = Text("step-target-hint.txt", "Pick one of the windows that are on screen now.") +
                "  (" + targets.Length + ")";
        }

        private ListBoxItem TargetItem(TargetWindowInfo target)
        {
            StackPanel content = new StackPanel();
            TextBlock title = new TextBlock();
            title.Text = Shorten(target.DisplayTitle, 60);
            title.FontSize = Theme.LabelSize;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = Theme.Text;
            title.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(title);
            TextBlock detail = new TextBlock();
            detail.FontSize = Theme.MicroSize;
            detail.Margin = new Thickness(0, 2, 0, 0);
            detail.Foreground = Theme.TextMuted;
            detail.Text = (String.IsNullOrEmpty(target.ProcessName) ? "?" : target.ProcessName) + "   " +
                (target.Rect == null ? "?" : target.Rect.Width + "x" + target.Rect.Height) + "   pid " + target.ProcessId;
            content.Children.Add(detail);
            ListBoxItem item = new ListBoxItem();
            item.Content = content;
            item.Tag = target;
            // The rows are built from panels, so an explicit accessible name is
            // what a screen reader and our own tests can rely on.
            System.Windows.Automation.AutomationProperties.SetName(item, target.DisplayTitle + " / " +
                (String.IsNullOrEmpty(target.ProcessName) ? "?" : target.ProcessName) + " / pid " + target.ProcessId);
            return item;
        }

        private void UseSelectedTarget()
        {
            ListBoxItem item = targetList.SelectedItem as ListBoxItem;
            if (item == null)
            {
                targetHint.Text = Text("target-select-first.txt", "Select a window in the list first.");
                return;
            }
            SelectTarget(item.Tag as TargetWindowInfo, "window-list");
        }

        private void SelectTarget(TargetWindowInfo target, string route)
        {
            if (target == null || target.ProcessId == 0) return;
            selectedTarget = target;
            selectedTargetProcessId = target.ProcessId;
            // Calculator and other packaged applications appear in the list as
            // ApplicationFrameHost while everything inside them belongs to a
            // different process. Without this the recorder treats the whole
            // window as somebody else's and writes nothing.
            selectedContentProcessIds = WindowTools.ContentProcessIds(new IntPtr(target.Hwnd), target.ProcessId);
            if (selectedContentProcessIds.Length > 0)
            {
                AddDiagnostic("Window contents are drawn by process " + String.Join(", ", ProcessIdText(selectedContentProcessIds)) + " as well.");
            }
            session.AddEvent("target.attach", "tool", "pid=" + target.ProcessId + " route=" + route + " class=" + target.ClassName);
            log.Append("events", new JsonObject()
                .Add("kind", "target.select")
                .Add("route", route)
                .Add("processId", target.ProcessId)
                .Add("processName", target.ProcessName)
                .Add("hwnd", target.Hwnd)
                .Add("title", target.Title)
                .Add("className", target.ClassName)
                .Add("rect", SessionLogJson.Rect(target.Rect)));
            AddDiagnostic("Target selected via " + route + ": " + target.Title + " (pid " + target.ProcessId + ").");
            GoTo(ShellStep.Menu);
        }

        private void BeginTargetDrag(object sender, MouseButtonEventArgs args)
        {
            targetDragActive = true;
            targetDragStart = args.GetPosition(this);
            Mouse.Capture(this);
            targetPickButton.Content = Text("drag-target.txt", "Drag onto the window");
        }

        private void EndTargetDrag(object sender, MouseButtonEventArgs args)
        {
            if (!targetDragActive) return;
            targetDragActive = false;
            ReleaseMouseCapture();
            targetPickButton.Content = Text("target-point.txt", "Point at it instead");
            Point end = args.GetPosition(this);
            if (Math.Abs(end.X - targetDragStart.X) < 8 && Math.Abs(end.Y - targetDragStart.Y) < 8) return;
            NativeMethods.POINT point;
            if (!NativeMethods.GetCursorPos(out point)) return;
            Win32Info info = Win32Probe.AtPoint(point.X, point.Y, 150);
            if (info == null || info.Hwnd == 0 || info.ProcessId == ownProcessId) return;
            TargetWindowInfo target = new TargetWindowInfo();
            target.ProcessId = info.ProcessId;
            target.Hwnd = info.TopHwnd != 0 ? info.TopHwnd : info.Hwnd;
            target.Title = String.IsNullOrEmpty(info.TopCaption) ? info.Caption : info.TopCaption;
            target.ClassName = String.IsNullOrEmpty(info.TopClass) ? info.ClassName : info.TopClass;
            target.Rect = info.TopRect != null ? info.TopRect : info.WindowRect;
            target.ProcessName = ProcessName(info.ProcessId);
            SelectTarget(target, "crosshair-drag");
        }

        // ---------- automatic scan ----------

        private void StartScan()
        {
            if (selectedTarget == null || scanRunning) return;
            scanRunning = true;
            lastScan = null;
            scanCountText.Text = "0";
            scanProgressText.Text = Text("scan-starting.txt", "Starting.");
            GoTo(ShellStep.Scan);
            log.Append("events", new JsonObject().Add("kind", "scan.start").Add("processId", selectedTargetProcessId).Add("hwnd", selectedTarget.Hwnd));
            ScanLimits limits = new ScanLimits();
            int processId = selectedTargetProcessId;
            long hwnd = selectedTarget.Hwnd;
            ScanRunner runner = new ScanRunner(baseDir);
            scanRunner = runner;
            Action<ScanProgress> progress = delegate(ScanProgress value)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ShowScanProgress(value); }));
            };
            Task<ScanResult> task = Task.Factory.StartNew(delegate { return runner.Run(processId, hwnd, limits, progress); });
            task.ContinueWith(delegate(Task<ScanResult> completed)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    scanRunning = false;
                    scanRunner = null;
                    runner.Dispose();
                    if (completed.IsFaulted)
                    {
                        string message = completed.Exception.GetBaseException().Message;
                        AddDiagnostic("Scan failed: " + message);
                        log.Append("events", new JsonObject().Add("kind", "scan.failed").Add("message", message));
                        scanProgressText.Text = Text("scan-failed.txt", "The scan could not be completed.") + " " + message;
                        return;
                    }
                    FinishScan(completed.Result);
                }));
            });
        }

        private void ShowScanProgress(ScanProgress value)
        {
            if (value == null) return;
            scanCountText.Text = value.NodeCount.ToString(CultureInfo.InvariantCulture);
            if (value.Finished)
            {
                scanProgressText.Text = Text("scan-finishing.txt", "Putting the result together.");
                return;
            }
            scanProgressText.Text = Text("scan-window.txt", "Window") + " " + (value.WindowIndex + 1) + "/" + Math.Max(1, value.WindowCount) +
                (String.IsNullOrEmpty(value.WindowTitle) ? String.Empty : ": " + Shorten(value.WindowTitle, 40));
        }

        private void FinishScan(ScanResult result)
        {
            lastScan = result;
            // The ledger is made from the scan before any picture is taken, so a
            // screen whose picture fails still has a row of its own to say so.
            lastScreens = ScreenLedger.FromScan(result);
            string summary = ScanSummary.Build(result);
            // Thousands of records are written off the interface thread so a
            // large application does not freeze the window while it saves.
            Task.Factory.StartNew(delegate
            {
                for (int index = 0; index < result.Nodes.Count; index++)
                {
                    log.Append("elements", ScanJson.Node(result.Nodes[index], result.ScanId, 0));
                }
                log.Append("events", ScanJson.Summary(result));
                log.WriteText("summary-" + result.ScanId + ".md", summary);
                log.FlushDurable();
                Dispatcher.BeginInvoke(new Action(delegate { UpdateSaveChip(); }));
            });
            resultSummary.Text = summary;
            resultGaps.Text = BuildGapText(result);
            resultList.Items.Clear();
            int shown = 0;
            int decorations = 0;
            for (int index = 0; index < result.Nodes.Count; index++) if (result.Nodes[index].Decoration) decorations++;
            for (int index = 0; index < result.Nodes.Count && shown < 400; index++)
            {
                ScanNode node = result.Nodes[index];
                // Window frame parts stay in the saved record; the list on screen
                // shows what the application itself puts there.
                if (node.Decoration) continue;
                ListBoxItem item = new ListBoxItem();
                item.Content = ScanNodeText(node);
                item.Tag = node;
                System.Windows.Automation.AutomationProperties.SetName(item, node.DisplayLabel);
                resultList.Items.Add(item);
                shown++;
            }
            // The caption keeps its wording so the accordion stays the same
            // control to anything reading the window; the count rides in the
            // summary slot, which is what a closed accordion is for.
            resultStatParts.Text = (result.Nodes.Count - decorations).ToString(CultureInfo.InvariantCulture);
            resultStatWindows.Text = result.Windows.Count.ToString(CultureInfo.InvariantCulture);
            resultStatFrame.Text = decorations.ToString(CultureInfo.InvariantCulture);
            resultElementsSummary.Text = shown + " / " + (result.Nodes.Count - decorations) +
                (decorations > 0 ? "   " + Text("result-decoration.txt", "frame parts kept in the record") + " " + decorations : String.Empty);
            resultGapsSummary.Text = GapSummary(result);
            session.AddEvent("scan.done", "tool", result.ScanId + " elements=" + result.Nodes.Count);
            AddDiagnostic("Scan " + result.ScanId + ": " + result.Nodes.Count + " elements in " + result.DurationMs + " ms.");
            GoTo(ShellStep.ScanResult);
            UpdateSaveChip();
            CaptureScanScreens();
        }

        // A picture of every screen the scan walked, taken straight after it and
        // tied to the same scan by the screen id. The window has to be in front
        // for the picture to show it, so each one is raised in turn and App
        // Studio puts itself back at the end.
        private void CaptureScanScreens()
        {
            if (lastScreens == null || lastScreens.Screens.Count == 0 || screenShotsRunning)
            {
                UpdateScreenSummary();
                return;
            }
            screenShotsRunning = true;
            string folder = log.Folder == null ? null : Path.Combine(log.Folder, "shots");
            if (folder != null) session.Data.RegisterWriteTarget(folder, "screen pictures");
            ScreenLedger ledger = lastScreens;
            bool wasTopmost = Topmost;
            int index = 0;
            UpdateScreenSummary();
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(420);
            Topmost = false;
            if (ledger.Screens.Count > 0) ScreenCapture.Raise(ledger.Screens[0].Hwnd);
            timer.Tick += delegate
            {
                if (index >= ledger.Screens.Count)
                {
                    timer.Stop();
                    Topmost = wasTopmost;
                    Activate();
                    screenShotsRunning = false;
                    log.Append("events", new JsonObject()
                        .Add("kind", "scan.screens")
                        .Add("scanId", ledger.ScanId)
                        .Add("screenCount", ledger.Screens.Count)
                        .Add("shotCount", ledger.ShotCount));
                    log.FlushDurable();
                    UpdateScreenSummary();
                    UpdateSaveChip();
                    AddDiagnostic("Screens captured: " + ledger.ShotCount + " / " + ledger.Screens.Count);
                    return;
                }
                ScreenRecord screen = ledger.Screens[index];
                ScreenCapture.Shoot(screen, folder);
                log.Append("screens", screen.ToJson());
                index++;
                if (index < ledger.Screens.Count) ScreenCapture.Raise(ledger.Screens[index].Hwnd);
                UpdateScreenSummary();
            };
            timer.Start();
        }

        private void UpdateScreenSummary()
        {
            if (resultScreensCallout == null) return;
            if (lastScreens == null || lastScreens.Screens.Count == 0)
            {
                resultScreensCallout.Visibility = Visibility.Collapsed;
                return;
            }
            resultScreensCallout.Visibility = Visibility.Visible;
            int shots = lastScreens.ShotCount;
            int total = lastScreens.Screens.Count;
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append(Text("screens-heading.txt", "Pictures of the screens that were scanned"));
            text.Append(":  ").Append(shots).Append(" / ").Append(total);
            if (screenShotsRunning) text.Append("   ").Append(Text("screens-running.txt", "taking them now"));
            for (int index = 0; index < lastScreens.Screens.Count; index++)
            {
                ScreenRecord screen = lastScreens.Screens[index];
                text.AppendLine();
                text.Append(screen.ScreenId).Append("  ").Append(Shorten(screen.Title, 36)).Append("  ").Append(screen.Size)
                    .Append("  ").Append(Text("screens-components.txt", "parts")).Append(' ').Append(screen.ComponentIds.Count)
                    .Append("  ").Append(screen.HasShot
                        ? Text("screens-have.txt", "picture taken")
                        : (String.IsNullOrEmpty(screen.ShotProblem) ? Text("screens-waiting.txt", "not yet") : screen.ShotProblem));
            }
            resultScreensText.Text = text.ToString();
            SetCalloutTone(resultScreensCallout, resultScreensText,
                screenShotsRunning ? "Neutral" : (shots == total ? "Success" : "Caution"));
        }

        private static string ScanNodeText(ScanNode node)
        {
            string rect = node.Rect == null ? "-" : node.Rect.Width + "x" + node.Rect.Height;
            return node.DisplayLabel + "   [" + String.Join("+", node.Sources.ToArray()) + "]   " + rect +
                (node.Hwnd == 0 ? String.Empty : "   hwnd 0x" + node.Hwnd.ToString("X"));
        }

        private string BuildGapText(ScanResult result)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int index = 0; index < result.Coverage.Count; index++)
            {
                ScanCoverage coverage = result.Coverage[index];
                text.AppendLine("[" + coverage.Provider + "] " + coverage.State + " / " + coverage.NodeCount + " " + Text("count-items.txt", "items"));
                for (int reason = 0; reason < coverage.Reasons.Count; reason++)
                {
                    text.AppendLine("   " + coverage.Reasons[reason].Code + ": " + coverage.Reasons[reason].Message);
                }
            }
            for (int index = 0; index < result.Unknowns.Count; index++) text.AppendLine("? " + result.Unknowns[index]);
            text.AppendLine(Text("scan-summary-honesty.txt", "This list covers what the providers exposed while the scan ran."));
            return text.ToString();
        }

        // What a closed accordion has to say: how many reasons are inside, and
        // whether any of them matter. Zero is stated, not left blank.
        private string GapSummary(ScanResult result)
        {
            int reasons = 0;
            for (int index = 0; index < result.Coverage.Count; index++) reasons += result.Coverage[index].Reasons.Count;
            reasons += result.Unknowns.Count;
            if (reasons == 0) return Text("gap-none.txt", "none");
            return reasons + " " + Text("count-items.txt", "items");
        }

        private void CancelScan()
        {
            ScanRunner runner = scanRunner;
            if (runner != null)
            {
                runner.Cancel();
                AddDiagnostic("Scan cancelled by the operator.");
                log.Append("events", new JsonObject().Add("kind", "scan.cancel"));
            }
            else
            {
                GoTo(ShellStep.Menu);
            }
        }

        // ---------- manual observation ----------

        private void StartObservation()
        {
            if (selectedTarget == null) return;
            observation.Start(selectedTargetProcessId, selectedContentProcessIds, TargetText(selectedTarget));
            mouseWatcher.Reset();
            mouseTimer.Start();
            StartHover();
            GoTo(ShellStep.Observe);
            UpdateObserveCounts();
        }

        private void StopObservation()
        {
            observation.Stop();
            mouseTimer.Stop();
            StopHover();
            log.FlushDurable();
            WriteObservationSummary();
            ShowObservationReview();
            UpdateSaveChip();
        }

        // The list is read back out of observations.jsonl rather than from
        // memory, so what the operator reviews is exactly what survived to
        // disk. If a case is open the same record is folded into case.md, which
        // is where a case is meant to be read from later.
        private void ShowObservationReview()
        {
            ObservationStatus status = observation.Status;
            List<string> lines = new List<string>();
            string path = log.Folder == null ? null : Path.Combine(log.Folder, "observations.jsonl");
            string[] raw = null;
            string readError = null;
            try
            {
                raw = SessionLog.ReadAllLines(path);
            }
            catch (Exception exception)
            {
                readError = exception.GetType().Name + ": " + exception.Message;
                AddDiagnostic("The recording could not be read back: " + exception.Message);
            }
            observeResultList.Items.Clear();
            int step = 0;
            if (raw != null)
            {
                for (int index = 0; index < raw.Length; index++)
                {
                    Dictionary<string, object> item = JsonReader.ReadObject(raw[index]);
                    if (item == null) continue;
                    string kind = JsonReader.Text(item, "kind");
                    if (kind != "observe.enter" && kind != "observe.click" && kind != "observe.click.result") continue;
                    Dictionary<string, object> element = JsonReader.Child(item, kind == "observe.click.result" ? "before" : "element");
                    string label = ObservedLabel(element);
                    string text;
                    if (kind == "observe.enter")
                    {
                        step++;
                        text = step + ". " + Text("observe-result-hover.txt", "pointed at") + "  " + label +
                            "   (" + JsonReader.Number(item, "x", 0) + "," + JsonReader.Number(item, "y", 0) + ")";
                    }
                    else if (kind == "observe.click")
                    {
                        step++;
                        text = step + ". " + Text("observe-result-click.txt", "clicked") + "  " + label +
                            "   (" + JsonReader.Number(item, "x", 0) + "," + JsonReader.Number(item, "y", 0) + ")";
                    }
                    else
                    {
                        bool observed = JsonReader.Flag(item, "observed", false);
                        text = "      -> " + (observed
                            ? Text("observe-result-reacted.txt", "the application changed")
                            : Text("observe-result-noreaction.txt", "no change could be observed here"));
                    }
                    lines.Add(text);
                    ListBoxItem row = new ListBoxItem();
                    row.Content = text;
                    System.Windows.Automation.AutomationProperties.SetName(row, text);
                    observeResultList.Items.Add(row);
                }
            }
            System.Text.StringBuilder summary = new System.Text.StringBuilder();
            summary.AppendLine(Text("observe-summary-title.txt", "Manual observation") + "   " +
                Text("scan-summary-target.txt", "Target") + ": " + (selectedTarget == null ? "-" : TargetText(selectedTarget)));
            summary.AppendLine(Text("observe-summary-elements.txt", "Parts pointed at") + ": " + status.EnterCount +
                "   " + Text("observe-summary-clicks.txt", "Clicks") + ": " + status.ClickCount +
                "   " + Text("observe-summary-events.txt", "Application events") + ": " + status.EventCount);
            if (status.Dropped > 0) summary.AppendLine(Text("observe-summary-dropped.txt", "Records that could not be written") + ": " + status.Dropped);
            summary.AppendLine(Text("observe-summary-scope.txt", "Only the target application was recorded. Keyboard input was never read."));
            observeResultSummary.Text = summary.ToString();
            observeResultStepsSummary.Text = status.EnterCount + " / " + status.ClickCount;

            // An empty list next to a non-zero count would read as "nothing was
            // recorded", so the reason is put on the screen instead.
            if (readError != null)
            {
                observeResultList.Items.Add(Text("observe-result-readfailed.txt", "The saved recording could not be read back to show here.") + " " + readError);
            }
            else if (lines.Count == 0 && status.EnterCount + status.ClickCount > 0)
            {
                observeResultList.Items.Add(Text("observe-result-empty.txt", "The counts above came from this session, but no line could be read back from the saved file."));
            }
            else if (lines.Count == 0)
            {
                // An empty box with no words reads as a fault. It is not one,
                // so the reason is put where the rows would have been.
                observeResultList.Items.Add(Text("observe-result-nothing.txt", "Nothing was pointed at while this recording ran, so there is no step to list."));
            }
            string saved = Text("observe-result-saved.txt", "Saved without asking, here") + ": " + (log.Folder ?? "-");
            if (caseRecord != null)
            {
                bool wrote = CaseStore.AppendMarkdown(caseRecord, ObservationMarkdown(status, lines));
                if (wrote)
                {
                    CaseStore.Save(caseRecord);
                    saved += Environment.NewLine + Text("observe-result-case.txt", "Also written into the case record") + ": " + caseRecord.MarkdownPath;
                }
                else
                {
                    saved += Environment.NewLine + Text("observe-result-casefailed.txt", "The case record could not be updated.") + " " + CaseStore.LastError;
                }
            }
            observeResultSaved.Text = saved;
            log.Append("events", new JsonObject()
                .Add("kind", "observe.review")
                .Add("steps", lines.Count)
                .Add("enterCount", status.EnterCount)
                .Add("clickCount", status.ClickCount)
                .Add("caseId", caseRecord == null ? null : caseRecord.CaseId));
            GoTo(ShellStep.ObserveResult);
        }

        private string ObservationMarkdown(ObservationStatus status, List<string> lines)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.AppendLine();
            text.AppendLine("## " + Text("case-md-observation.txt", "Recorded operations"));
            text.AppendLine();
            text.AppendLine("- " + Text("plan-run-when.txt", "Run at") + ": " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("- " + Text("observe-summary-elements.txt", "Parts pointed at") + ": " + status.EnterCount +
                " / " + Text("observe-summary-clicks.txt", "Clicks") + ": " + status.ClickCount +
                " / " + Text("observe-summary-events.txt", "Application events") + ": " + status.EventCount);
            text.AppendLine("- " + Text("case-md-session.txt", "Investigation log folder") + ": " + (log.Folder ?? "-"));
            text.AppendLine();
            for (int index = 0; index < lines.Count; index++) text.AppendLine("    " + lines[index]);
            text.AppendLine();
            text.AppendLine(Text("observe-summary-scope.txt", "Only the target application was recorded. Keyboard input was never read."));
            text.AppendLine();
            return text.ToString();
        }

        private string ObservedLabel(Dictionary<string, object> element)
        {
            if (element == null) return Text("operate-unknown.txt", "the part under the pointer");
            string type = JsonReader.Text(element, "localizedControlType");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "controlType");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "role");
            if (String.IsNullOrEmpty(type)) type = JsonReader.Text(element, "className");
            string name = JsonReader.Text(element, "name");
            string label = String.IsNullOrEmpty(name) ? (type ?? "?") : (String.IsNullOrEmpty(type) ? name : type + " \"" + name + "\"");
            string automationId = JsonReader.Text(element, "automationId");
            return String.IsNullOrEmpty(automationId) ? label : label + "  [" + automationId + "]";
        }

        private void TogglePause()
        {
            observation.SetPaused(!observation.Paused);
            observePauseButton.Content = observation.Paused ? Text("observe-resume.txt", "Resume") : Text("observe-pause.txt", "Pause");
            UpdateHeader();
        }

        private void WriteObservationSummary()
        {
            ObservationStatus status = observation.Status;
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.AppendLine(Text("observe-summary-title.txt", "Manual observation"));
            text.AppendLine(Text("scan-summary-target.txt", "Target") + ": " + (selectedTarget == null ? "-" : TargetText(selectedTarget)));
            text.AppendLine(Text("observe-summary-elements.txt", "Parts pointed at") + ": " + status.EnterCount);
            text.AppendLine(Text("observe-summary-clicks.txt", "Clicks") + ": " + status.ClickCount);
            text.AppendLine(Text("observe-summary-events.txt", "Application events") + ": " + status.EventCount);
            text.AppendLine(Text("observe-summary-samples.txt", "Pointer samples in the raw trail") + ": " + status.PointerSamples);
            if (status.Dropped > 0) text.AppendLine(Text("observe-summary-dropped.txt", "Records that could not be written") + ": " + status.Dropped);
            text.AppendLine(Text("observe-summary-scope.txt", "Only the target application was recorded. Keyboard input was never read."));
            log.WriteText("observation-summary.md", text.ToString());
        }

        private void UpdateObserveCounts()
        {
            ObservationStatus status = observation.Status;
            observeCounts.Text = Text("observe-counts.txt", "Recorded") + ": " + (status.EnterCount + status.ClickCount + status.EventCount) +
                "   " + Text("observe-counts-parts.txt", "parts") + " " + status.EnterCount +
                " / " + Text("observe-counts-clicks.txt", "clicks") + " " + status.ClickCount +
                " / " + Text("observe-counts-events.txt", "app events") + " " + status.EventCount +
                (status.Dropped > 0 ? "   " + Text("observe-counts-dropped.txt", "not written") + " " + status.Dropped : String.Empty);
        }

        private void PollMouse()
        {
            string[] pressed = mouseWatcher.Poll();
            if (pressed.Length == 0) return;
            NativeMethods.POINT point;
            if (!NativeMethods.GetCursorPos(out point)) return;
            if (ContainsShell(point.X, point.Y)) return;
            for (int index = 0; index < pressed.Length; index++)
            {
                ObservedElement before = observation.OnMouseDown(point.X, point.Y, pressed[index]);
                ScheduleClickOutcome(before, point.X, point.Y);
            }
            UpdateObserveCounts();
        }

        private void ScheduleClickOutcome(ObservedElement before, int x, int y)
        {
            DateTime clickedAt = DateTime.UtcNow;
            DispatcherTimer after = new DispatcherTimer();
            after.Interval = TimeSpan.FromMilliseconds(320);
            after.Tick += delegate
            {
                after.Stop();
                int px = x;
                int py = y;
                Task<Snapshot> task = Task.Factory.StartNew(delegate { return Probe.At(px, py, 1500); });
                task.ContinueWith(delegate(Task<Snapshot> completed)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (completed.IsFaulted) return;
                        AcquisitionView view = SnapshotAnalysis.Analyze(completed.Result, px, py, ownProcessId);
                        observation.OnClickOutcome(before, completed.Result, view, px, py, EventsSince(clickedAt), 320);
                        UpdateObserveCounts();
                    }));
                });
            };
            after.Start();
        }

        private string[] EventsSince(DateTime start)
        {
            List<string> types = new List<string>();
            for (int index = 0; index < recentEvents.Count; index++)
            {
                if (recentEvents[index].Key >= start && !types.Contains(recentEvents[index].Value)) types.Add(recentEvents[index].Value);
            }
            return types.ToArray();
        }

        // ---------- operation probe ----------

        private void StartOperate()
        {
            if (selectedTarget == null) return;
            StartHover();
            operateResultText.Text = String.Empty;
            SetCalloutTone(operateResultCallout, operateResultText, "Neutral");
            UpdateOperateAvailability();
            GoTo(ShellStep.Operate);
        }

        private void StopOperate()
        {
            StopHover();
            GoTo(ShellStep.Menu);
        }

        private void FillOperationKinds()
        {
            probeKind.Items.Clear();
            probeKind.Items.Add(KindItem(ProbeKind.Read, Text("kind-read.txt", "Read only (safe)")));
            probeKind.Items.Add(KindItem(ProbeKind.Focus, Text("kind-focus.txt", "Move the keyboard focus")));
            probeKind.Items.Add(KindItem(ProbeKind.Invoke, Text("kind-invoke.txt", "Press it")));
            probeKind.Items.Add(KindItem(ProbeKind.Toggle, Text("kind-toggle.txt", "Switch it on or off")));
            probeKind.Items.Add(KindItem(ProbeKind.Select, Text("kind-select.txt", "Select it")));
            probeKind.Items.Add(KindItem(ProbeKind.Expand, Text("kind-expand.txt", "Open it")));
            probeKind.Items.Add(KindItem(ProbeKind.SetValue, Text("kind-setvalue.txt", "Put a value in")));
            probeKind.Items.Add(KindItem(ProbeKind.Scroll, Text("kind-scroll.txt", "Scroll it into view")));
            probeKind.Items.Add(KindItem(ProbeKind.Click, Text("kind-click.txt", "Click it")));
            probeKind.Items.Add(KindItem(ProbeKind.Keys, Text("kind-keys.txt", "Send the text above as keys")));
            probeKind.SelectedIndex = 0;
        }

        private static ComboBoxItem KindItem(ProbeKind kind, string label)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = label;
            item.Tag = kind;
            return item;
        }

        private ProbeKind SelectedKind()
        {
            ComboBoxItem item = probeKind.SelectedItem as ComboBoxItem;
            return item == null ? ProbeKind.Read : (ProbeKind)item.Tag;
        }

        private void UpdateOperateAvailability()
        {
            ProbeKind kind = SelectedKind();
            bool needsValue = kind == ProbeKind.SetValue || kind == ProbeKind.Keys;
            probeValue.IsEnabled = needsValue;
            probeValueLabel.Text = needsValue
                ? Text("probe-value.txt", "Value used by setValue or keys")
                : Text("probe-value-unused.txt", "This operation does not use the text box below.");
            operateTargetText.Text = operateElement == null
                ? Text("operate-none.txt", "No part chosen yet.")
                : Text("operate-chosen.txt", "Chosen part") + ": " + operateLabel;
        }

        private void UseLiveForOperation()
        {
            if (currentSnapshot == null)
            {
                operateResultText.Text = Text("operate-need-live.txt", "Point at a part of the target first.");
                SetCalloutTone(operateResultCallout, operateResultText, "Caution");
                return;
            }
            operateElement = ElementRef.FromSnapshot(currentSnapshot);
            operateLabel = currentView == null ? Text("operate-unknown.txt", "the part under the pointer") : currentView.Title;
            operateRecord = null;
            UpdateOperateAvailability();
        }

        private void OperateSelectedScanNode()
        {
            ListBoxItem item = resultList.SelectedItem as ListBoxItem;
            ScanNode node = item == null ? null : item.Tag as ScanNode;
            if (node == null || node.Rect == null || node.Rect.Width <= 0)
            {
                AddDiagnostic("This part has no rectangle, so it cannot be operated from the list.");
                return;
            }
            ElementRef reference = new ElementRef();
            reference.X = node.Rect.X + node.Rect.Width / 2;
            reference.Y = node.Rect.Y + node.Rect.Height / 2;
            reference.Hwnd = node.Hwnd;
            operateElement = reference;
            operateLabel = node.DisplayLabel;
            operateRecord = null;
            StartOperate();
        }

        // Both the single operation test and the assistant flow offer the same
        // permission, so flipping it in one place must not leave the other
        // showing the opposite of what is in force.
        private void OnWriteToggle(bool enabled)
        {
            if (syncingWriteToggle) return;
            syncingWriteToggle = true;
            try
            {
                if (writeToggle.IsChecked != enabled) writeToggle.IsChecked = enabled;
                if (aiWriteToggle != null && aiWriteToggle.IsChecked != enabled) aiWriteToggle.IsChecked = enabled;
            }
            finally
            {
                syncingWriteToggle = false;
            }
            UpdateModeBadge(enabled);
            session.Data.Mode = enabled ? "write" : "readOnly";
            session.AddEvent("mode.change", "tool", "write=" + enabled);
            log.Append("events", new JsonObject().Add("kind", "mode.change").Add("writeEnabled", enabled));
        }

        // Whether the target may be changed is the single most consequential
        // piece of state in the product, so it is a badge that changes colour
        // rather than a line of text that changes wording.
        private void UpdateModeBadge(bool enabled)
        {
            headerMode.Text = enabled ? Text("write-enabled.txt", "Changing operations allowed") : Text("read-only.txt", "Read only");
            SetBadgeTone(headerModeBadge, headerMode, enabled ? "Caution" : "Accent");
        }

        private void RunOperationProbe()
        {
            if (operateElement == null)
            {
                operateResultText.Text = Text("operate-need-element.txt", "Choose the part to operate first.");
                SetCalloutTone(operateResultCallout, operateResultText, "Caution");
                return;
            }
            ProbeKind kind = SelectedKind();
            ProbeArgs arguments = new ProbeArgs();
            arguments.WriteEnabled = writeToggle.IsChecked == true;
            arguments.Value = probeValue.Text;
            arguments.BudgetMs = 5000;
            ElementRef element = operateElement;
            bool wasFrozen = frozen;
            frozen = true;
            suppressOverlay = true;
            overlay.Hide();
            operateResultText.Text = Text("operate-running.txt", "Running.");
            SetCalloutTone(operateResultCallout, operateResultText, "Neutral");
            AddDiagnostic("Probe started: " + kind + ".");
            Task<ProbeResult> task = Task.Factory.StartNew(delegate { return ProbeRunner.Run(element, kind, arguments); });
            task.ContinueWith(delegate(Task<ProbeResult> completed)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    frozen = wasFrozen;
                    suppressOverlay = false;
                    if (completed.IsFaulted)
                    {
                        string message = completed.Exception.GetBaseException().Message;
                        operateResultText.Text = Text("operate-failed.txt", "The operation could not be run.") + " " + message;
                        SetCalloutTone(operateResultCallout, operateResultText, "Danger");
                        AddDiagnostic("Probe failed: " + message);
                        return;
                    }
                    lastProbe = completed.Result;
                    RecordProbe(lastProbe);
                    undoButton.IsEnabled = lastProbe.Undo != null && lastProbe.Undo.Available;
                    operateResultText.Text = OutcomeText(lastProbe);
                    SetCalloutTone(operateResultCallout, operateResultText, OutcomeTone(lastProbe));
                    ShowOutcome(lastProbe);
                    AddDiagnostic("Probe " + lastProbe.ProbeId + ": " + lastProbe.Method + " -> " + lastProbe.Outcome + " (" + lastProbe.DurationMs + " ms).");
                }));
            });
        }

        private void RecordProbe(ProbeResult probe)
        {
            if (probe == null) return;
            if (operateRecord != null) session.AddProbe(operateRecord, probe);
            log.Append("events", new JsonObject()
                .Add("kind", "operation.result")
                .Add("probeId", probe.ProbeId)
                .Add("operation", probe.Kind.ToString().ToLowerInvariant())
                .Add("label", operateLabel)
                .Add("method", probe.Method)
                .Add("outcome", probe.Outcome)
                .Add("durationMs", probe.DurationMs)
                .Add("writeEnabled", writeToggle.IsChecked == true)
                .Add("error", probe.Error == null ? null : new JsonObject().Add("code", probe.Error.Code).Add("message", probe.Error.Message))
                .Add("undoAvailable", probe.Undo != null && probe.Undo.Available));
            UpdateSaveChip();
        }

        // The toast states the verdict only. The full route, timing and error
        // stay in the callout, where there is room to read them.
        private string OutcomeHeadline(ProbeResult probe)
        {
            string full = OutcomeText(probe);
            int stop = full.IndexOf(Environment.NewLine, StringComparison.Ordinal);
            return stop < 0 ? full : full.Substring(0, stop);
        }

        private string OutcomeText(ProbeResult probe)
        {
            string outcome;
            if (probe.Outcome == "success") outcome = Text("outcome-success.txt", "It worked and the change was observed");
            else if (probe.Outcome == "blocked") outcome = Text("outcome-blocked.txt", "Refused by a safety rule");
            else if (probe.Outcome == "notSupported") outcome = Text("outcome-notsupported.txt", "This part does not offer that operation");
            else if (probe.Outcome == "failed") outcome = Text("outcome-failed.txt", "The attempt failed");
            else outcome = Text("outcome-unknown.txt", "Carried out, but no change could be observed");
            string reason = probe.Error == null || String.IsNullOrEmpty(probe.Error.Message) ? String.Empty : Environment.NewLine + probe.Error.Message;
            return outcome + Environment.NewLine + Text("operate-method.txt", "Route used") + ": " + probe.Method + "   " + probe.DurationMs + " ms" + reason;
        }

        // The outcome of an operation is the whole point of pressing Run, so it
        // is brought into view and repeated as a short confirmation. A result
        // that has scrolled below the fold is a result the operator never saw.
        private void ShowOutcome(ProbeResult probe)
        {
            string tone = OutcomeTone(probe);
            // The callout has only just become visible, so it has no place in
            // the layout yet. Measuring first is what makes the scroll land.
            operateResultCallout.UpdateLayout();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { operateResultCallout.BringIntoView(); }));
            string head;
            if (probe.Outcome == "success") head = Text("toast-done.txt", "Done");
            else if (probe.Outcome == "failed") head = Text("toast-failed.txt", "Failed");
            else head = Text("toast-warning.txt", "Warning");
            Toast(head, OutcomeHeadline(probe), tone);
        }

        // Success, refusal and failure are three different answers, so they get
        // three different colours instead of one grey paragraph.
        private static string OutcomeTone(ProbeResult probe)
        {
            if (probe.Outcome == "success") return "Success";
            if (probe.Outcome == "failed") return "Danger";
            if (probe.Outcome == "blocked" || probe.Outcome == "notSupported") return "Caution";
            return "Neutral";
        }

        private void UndoOperation()
        {
            if (lastProbe == null || lastProbe.Undo == null || !lastProbe.Undo.Available) return;
            ProbeResult undo = ProbeRunner.Undo(lastProbe);
            RecordProbe(undo);
            undoButton.IsEnabled = lastProbe.Undo.PerformedAt == null;
            operateResultText.Text = OutcomeText(undo);
            SetCalloutTone(operateResultCallout, operateResultText, OutcomeTone(undo));
            ShowOutcome(undo);
            AddDiagnostic("Undo " + undo.ProbeId + ": " + undo.Method + " -> " + undo.Outcome + ".");
        }

        // ---------- live acquisition (unchanged behaviour) ----------

        private void StartHover()
        {
            if (inspecting) return;
            inspecting = true;
            frozen = false;
            freezeButton.Content = Text("freeze.txt", "Hold the display");
            try
            {
                winEvents.Start(selectedTargetProcessId);
            }
            catch (Exception exception)
            {
                AddDiagnostic("Application events unavailable: " + exception.Message);
                log.Append("events", new JsonObject().Add("kind", "winevent.unavailable").Add("message", exception.Message));
            }
        }

        private void StopHover()
        {
            if (!inspecting) return;
            inspecting = false;
            frozen = false;
            winEvents.Stop();
            overlay.Hide();
            liveValuePresenter.Clear();
            liveValueText.Text = String.Empty;
            liveValueText.Visibility = String.IsNullOrEmpty(liveValueText.Text) ? Visibility.Collapsed : Visibility.Visible;
            liveTitle.Text = Text("live-idle.txt", "Move the pointer over the target application.");
            liveFacts.Text = String.Empty;
            liveFactsSummary.Text = Text("live-detail-none.txt", "nothing acquired yet");
            liveRoute.Text = String.Empty;
            liveRoute.Visibility = String.IsNullOrEmpty(liveRoute.Text) ? Visibility.Collapsed : Visibility.Visible;
            currentSnapshot = null;
            currentView = null;
        }

        // "Go to the memo" has to work from the state the screen is actually in.
        // The memo lives in an accordion that starts closed, and a collapsed
        // element has no visual to focus, so the key used to do nothing at all.
        // The panel is opened first, and the focus is asked for again after the
        // layout pass that creates the box, because it does not exist yet in the
        // turn that opens the accordion.
        private void FocusMemo()
        {
            if (noteInput == null) return;
            if (memoAccordion != null && !memoAccordion.IsExpanded)
            {
                memoAccordion.IsExpanded = true;
                memoAccordion.UpdateLayout();
            }
            if (!TryFocusMemo())
            {
                Dispatcher.BeginInvoke(new Action(delegate { TryFocusMemo(); }), DispatcherPriority.Loaded);
            }
        }

        private bool TryFocusMemo()
        {
            if (noteInput == null) return false;
            noteInput.BringIntoView();
            bool taken = noteInput.Focus();
            if (!taken) taken = Keyboard.Focus(noteInput) == noteInput;
            if (taken) noteInput.CaretIndex = noteInput.Text == null ? 0 : noteInput.Text.Length;
            return taken;
        }

        private void ToggleFreeze()
        {
            frozen = !frozen;
            freezeButton.Content = frozen ? Text("unfreeze.txt", "Release the display") : Text("freeze.txt", "Hold the display");
            UpdateHeader();
        }

        private void OnHoverTick(object sender, EventArgs args)
        {
            if (!inspecting || frozen) return;
            NativeMethods.POINT point;
            if (!NativeMethods.GetCursorPos(out point)) return;
            bool moved = point.X != previousX || point.Y != previousY;
            // A still pointer over a screen that changes underneath it would
            // otherwise keep showing a part that is no longer there, so the
            // point is read again once a second even without movement.
            bool stale = (DateTime.UtcNow - lastAcquisitionAt).TotalMilliseconds >= IdleRefreshMs;
            if (!moved && !stale) return;
            previousX = point.X;
            previousY = point.Y;
            lastAcquisitionAt = DateTime.UtcNow;
            // The pointer resting on App Studio's own panel keeps the last
            // result on screen; the overlay windows are hit-test transparent
            // and are excluded by process id on the result side instead.
            if (ContainsShell(point.X, point.Y)) return;
            RequestProbe(point.X, point.Y);
        }

        private void RequestProbe(int x, int y)
        {
            if (acquisitionInFlight)
            {
                pendingPoint = true;
                pendingX = x;
                pendingY = y;
                droppedRequests++;
                if (droppedRequests % 50 == 1)
                {
                    AddDiagnostic("ACQ-DROPPED stale hover request; total=" + droppedRequests);
                    session.AddFailure("acquisition", "ACQ-DROPPED", "Stale hover requests were discarded; total=" + droppedRequests + ".", null);
                }
                return;
            }
            acquisitionInFlight = true;
            Task<Snapshot> task = Task.Factory.StartNew(delegate { return Probe.At(x, y, 1500); });
            task.ContinueWith(delegate(Task<Snapshot> completed)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    acquisitionInFlight = false;
                    if (completed.IsFaulted)
                    {
                        AddDiagnostic("ACQ-FAIL " + completed.Exception.GetBaseException().Message);
                    }
                    else
                    {
                        AcquisitionView view = SnapshotAnalysis.Analyze(completed.Result, x, y, ownProcessId);
                        if (view.IsSelf)
                        {
                            liveRoute.Text = Text("self-excluded.txt", "This is App Studio itself, so it is left out.");
                            liveRoute.Visibility = String.IsNullOrEmpty(liveRoute.Text) ? Visibility.Collapsed : Visibility.Visible;
                            overlay.Hide();
                        }
                        else if (view.IsShellSurface)
                        {
                            // Passing over the desktop or taskbar on the way to a
                            // control must not clobber the element being inspected.
                            overlay.Hide();
                        }
                        else if (!IsChosenTarget(view.ProcessId))
                        {
                            liveRoute.Text = Text("other-process.txt", "Outside the chosen target") +
                                ": " + ProcessName(view.ProcessId) + " (pid " + view.ProcessId + ")";
                            liveRoute.Visibility = Visibility.Visible;
                            overlay.Hide();
                            observation.OnAcquisition(completed.Result, view, x, y);
                        }
                        else
                        {
                            Display(completed.Result, view, x, y);
                            observation.OnAcquisition(completed.Result, view, x, y);
                            if (step == ShellStep.Observe) UpdateObserveCounts();
                        }
                    }
                    if (pendingPoint)
                    {
                        int nextX = pendingX;
                        int nextY = pendingY;
                        pendingPoint = false;
                        RequestProbe(nextX, nextY);
                    }
                }));
            });
        }

        private void Display(Snapshot snapshot, AcquisitionView view, int x, int y)
        {
            currentSnapshot = snapshot;
            currentView = view;
            liveTitle.Text = Value(view.Title);
            liveRoute.Text = Text("route-label.txt", "Read from") + ": " + RouteText(view.Route) +
                " / " + (view.Level == "element" ? Text("level-element.txt", "an inner part") : Text("level-window.txt", "the window itself")) +
                (view.Level == "window" ? Environment.NewLine + Text("window-fallback.txt", "Inner parts are not exposed here; the window is being recorded instead.") : String.Empty);
            liveRoute.Visibility = Visibility.Visible;
            string msaaLine = snapshot.Msaa != null && snapshot.Msaa.Status != null && snapshot.Msaa.Status.State != "unavailable"
                ? Environment.NewLine + "MSAA: " + Value(snapshot.Msaa.Role) + "  " + Value(snapshot.Msaa.Name)
                : String.Empty;
            liveFacts.Text = Text("target-label.txt", "Target") + ": " + ProcessName(view.ProcessId) + " (pid " + view.ProcessId + ")" +
                (String.IsNullOrEmpty(view.TopCaption) ? String.Empty : "  " + Shorten(view.TopCaption, 40)) + Environment.NewLine +
                "AutomationId: " + Value(snapshot.Uia == null ? null : snapshot.Uia.AutomationId) + Environment.NewLine +
                "class: " + Value(snapshot.Win32 == null ? null : snapshot.Win32.ClassName) + "  ctrlId: " + (snapshot.Win32 == null ? 0 : snapshot.Win32.CtrlId) + Environment.NewLine +
                "HWND: " + (snapshot.Win32 == null || snapshot.Win32.Hwnd == 0 ? Text("no-hwnd.txt", "none") : "0x" + snapshot.Win32.Hwnd.ToString("X")) +
                "  rect: " + (view.Rect == null ? "-" : view.Rect.X + "," + view.Rect.Y + " " + view.Rect.Width + "x" + view.Rect.Height) +
                "  " + snapshot.DurationMs + " ms" + Environment.NewLine +
                "UIA: " + State(snapshot.UiaStatus) + (snapshot.Uia != null && snapshot.Uia.RawRefined ? "+raw" : String.Empty) +
                "  MSAA: " + State(snapshot.MsaaStatus) + "  Win32: " + State(snapshot.Win32Status) + msaaLine;
            // Closed, the fold still says which routes produced the facts, so a
            // degraded acquisition is visible without opening anything.
            liveFactsSummary.Text = "UIA " + State(snapshot.UiaStatus) + " / MSAA " + State(snapshot.MsaaStatus) +
                " / Win32 " + State(snapshot.Win32Status);
            LiveValueView liveView = liveValuePresenter.Present(snapshot, session.Data.Masking);
            liveValueText.Text = liveView.Visible
                ? Text("value-label.txt", "Value") + ": " + Value(liveView.Text) + "  [" + Text("value-live-note.txt", "shown only, not recorded") + "]"
                : Text("value-label.txt", "Value") + ": " + Text("value-hidden.txt", "hidden") + " - " + liveView.Reason;
            liveValueText.Visibility = Visibility.Visible;
            if (suppressOverlay)
            {
                overlay.Hide();
                return;
            }
            overlay.ShowHighlight(view.Rect, BuildOverlaySummary(snapshot, view), x, y);
            overlay.SetSummaryVisible(Keyboard.Modifiers == ModifierKeys.None);
        }

        private string RouteText(string route)
        {
            if (route == "uia") return Text("route-uia.txt", "accessibility (UIA)");
            if (route == "msaa") return Text("route-msaa.txt", "accessibility (MSAA)");
            if (route == "win32-child") return Text("route-win32-child.txt", "window handle (child)");
            if (route == "win32-window") return Text("route-win32-window.txt", "window handle");
            return Text("route-none.txt", "nothing could be read");
        }

        private string BuildOverlaySummary(Snapshot snapshot, AcquisitionView view)
        {
            string sizeText = view.Rect == null ? "?" : view.Rect.Width + "x" + view.Rect.Height;
            return Shorten(Value(view.Title), 44) + Environment.NewLine +
                ProcessName(view.ProcessId) + " (pid " + view.ProcessId + ")  " + Shorten(Value(view.TopCaption), 24) + Environment.NewLine +
                view.Route + " / " + (view.Level == "element" ? Text("level-element.txt", "an inner part") : Text("level-window.txt", "the window itself")) + "  " + sizeText + Environment.NewLine +
                "UIA:" + State(snapshot.UiaStatus) + " MSAA:" + State(snapshot.MsaaStatus) + " Win32:" + State(snapshot.Win32Status);
        }

        // ---------- keeping a part ----------

        private void PinCurrent()
        {
            if (currentSnapshot == null)
            {
                AddDiagnostic("Pin ignored: no live element.");
                return;
            }
            Snapshot snapshot = currentSnapshot;
            string note = noteInput.Text;
            string label = snapshot.Uia == null ? null : snapshot.Uia.Name;
            AddDiagnostic("Pin started.");
            // The highlight overlay sits exactly on the element. Suppress and
            // freeze tracking for the whole pin so UI Automation resolves the
            // target instead of App Studio's own transparent frame.
            bool pinWasFrozen = frozen;
            frozen = true;
            suppressOverlay = true;
            overlay.Hide();
            Task<ElementRecord> task = Task.Factory.StartNew(delegate { return session.Pin(snapshot, label, note); });
            task.ContinueWith(delegate(Task<ElementRecord> completed)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    suppressOverlay = false;
                    frozen = pinWasFrozen;
                    if (completed.IsFaulted)
                    {
                        AddDiagnostic("Pin failed: " + completed.Exception.GetBaseException().Message);
                        return;
                    }
                    ElementRecord record = completed.Result;
                    pinnedList.Items.Add(record.ElementId + "  " + record.Label);
                    noteInput.Clear();
                    operateRecord = record;
                    AddDiagnostic("Pinned " + record.ElementId + ".");
                    log.Append("elements", KeptElementJson(record));
                    UpdateSaveChip();
                }));
            });
        }

        private JsonObject KeptElementJson(ElementRecord record)
        {
            List<object> locators = new List<object>();
            for (int index = 0; index < record.Locators.Count; index++) locators.Add(LocatorJson.Build(record.Locators[index]));
            return new JsonObject()
                .Add("kind", "kept.element")
                .Add("elementId", record.ElementId)
                .Add("label", record.Label)
                .Add("notes", record.Notes.ToArray())
                .Add("win32", record.Win32 == null ? null : new JsonObject()
                    .Add("hwnd", record.Win32.Hwnd)
                    .Add("className", record.Win32.ClassName)
                    .Add("realClassName", record.Win32.RealClass)
                    .Add("ctrlId", record.Win32.CtrlId)
                    .Add("rect", SessionLogJson.Rect(record.Win32.WindowRect))
                    .Add("visible", record.Win32.Visible)
                    .Add("enabled", record.Win32.Enabled))
                .Add("uia", record.Uia == null ? null : new JsonObject()
                    .Add("name", record.Uia.Name)
                    .Add("automationId", record.Uia.AutomationId)
                    .Add("controlType", record.Uia.ControlType)
                    .Add("frameworkId", record.Uia.FrameworkId)
                    .Add("runtimeId", SessionLogJson.RuntimeIdText(record.Uia.RuntimeId))
                    .Add("patterns", SessionLogJson.Strings(record.Uia.SupportedPatterns))
                    .Add("rect", SessionLogJson.Rect(record.Uia.BoundingRect))
                    .Add("isPassword", record.Uia.IsPassword))
                .Add("recordedValue", record.RecordedValue == null ? null : new JsonObject()
                    .Add("length", record.RecordedValue.Length)
                    .Add("kind", record.RecordedValue.Kind)
                    .Add("masked", record.RecordedValue.Masked)
                    .Add("maskRule", record.RecordedValue.MaskRule)
                    .Add("content", record.RecordedValue.Content))
                .Add("locators", locators.ToArray());
        }

        // ---------- case flow: investigate, ask, take the answer back, try it ----------

        private void StartCase()
        {
            if (selectedTarget == null) return;
            if (caseRecord == null || caseRecord.TargetProcessId != selectedTargetProcessId || caseReopened)
            {
                caseRecord = CaseStore.Create(baseDir, selectedTarget, ProcessName(selectedTargetProcessId), log.Folder);
                caseReopened = false;
                caseBundle = null;
                caseHandoff = null;
                caseScreens = null;
                premiseMismatch = false;
                answerSha256 = null;
                currentPlan = null;
                lastRun = null;
                aiAnswerInput.Clear();
                aiPlanText.Text = String.Empty;
                aiImportStatus.Text = String.Empty;
                SetCalloutTone(aiImportCallout, aiImportStatus, "Neutral");
                session.Data.RegisterWriteTarget(caseRecord.Folder, "case folder");
                log.Append("events", new JsonObject()
                    .Add("kind", "case.start")
                    .Add("caseId", caseRecord.CaseId)
                    .Add("folder", caseRecord.Folder)
                    .Add("processId", selectedTargetProcessId));
                AddDiagnostic("Case started: " + caseRecord.CaseId);
            }
            caseElements = CaseElementTable.Build(lastScan, RequestBuilder.ElementLimit);
            if (caseRecord != null) caseRecord.ElementCount = caseElements.ListedCount;
            GoTo(ShellStep.AiRequest);
            if (caseRecord != null && String.IsNullOrEmpty(caseRecord.ShotFile)) TakeCaseScreenshot(false);
            else UpdateCaseAvailability();
        }

        // The picture has to show the target, not this window sitting on top of
        // it, so the target is brought forward for the moment of the capture and
        // App Studio puts itself back afterwards.
        private void TakeCaseScreenshot(bool retake)
        {
            if (caseRecord == null || selectedTarget == null) return;
            IntPtr handle = new IntPtr(selectedTarget.Hwnd);
            if (handle == IntPtr.Zero)
            {
                aiRequestStatus.Text = Text("ai-shot-nohwnd.txt", "This target has no window handle, so no picture could be taken.");
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Caution");
                return;
            }
            string path = Path.Combine(caseRecord.ShotFolder, "target-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            aiRequestStatus.Text = Text("ai-shot-running.txt", "Taking the picture.");
            SetCalloutTone(aiRequestCallout, aiRequestStatus, "Neutral");
            bool wasTopmost = Topmost;
            Topmost = false;
            NativeMethods.SetForegroundWindow(handle);
            DispatcherTimer delay = new DispatcherTimer();
            delay.Interval = TimeSpan.FromMilliseconds(450);
            delay.Tick += delegate
            {
                delay.Stop();
                RectValue rect = WindowTools.GetPhysicalRect(handle);
                ShotResult shot = null;
                if (rect != null && rect.Width > 0 && rect.Height > 0)
                {
                    shot = Capture.Crop(rect, new MaskRect[0], path, handle);
                }
                Topmost = wasTopmost;
                Activate();
                if (shot == null || shot.Status == null || shot.Status.State != "ok")
                {
                    string reason = shot == null || shot.Status == null || shot.Status.Reasons.Count == 0
                        ? Text("ai-shot-norect.txt", "The target window has no usable rectangle.")
                        : shot.Status.Reasons[0].Code + " " + shot.Status.Reasons[0].Message;
                    aiRequestStatus.Text = Text("ai-shot-failed.txt", "The picture could not be taken.") + " " + reason;
                    SetCalloutTone(aiRequestCallout, aiRequestStatus, "Danger");
                    AddDiagnostic("Case screenshot failed: " + reason);
                    log.Append("events", new JsonObject().Add("kind", "case.shot.failed").Add("caseId", caseRecord.CaseId).Add("reason", reason));
                    UpdateCaseAvailability();
                    return;
                }
                caseRecord.ShotFile = shot.File;
                session.AddShot(shot, "case");
                CaseStore.Save(caseRecord);
                CaseStore.AppendMarkdown(caseRecord, Environment.NewLine + "## " + CaseText.Screenshot + Environment.NewLine + Environment.NewLine +
                    "![" + Path.GetFileName(shot.File) + "](shots/" + Path.GetFileName(shot.File) + ")" + Environment.NewLine + Environment.NewLine +
                    "- " + Text("case-md-shot-taken.txt", "Taken at") + ": " + shot.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "  (" + shot.CaptureMethod + ", " + shot.Rect.Width + "x" + shot.Rect.Height + ")" + Environment.NewLine);
                log.Append("events", new JsonObject().Add("kind", "case.shot").Add("caseId", caseRecord.CaseId).Add("file", shot.File).Add("method", shot.CaptureMethod));
                aiRequestStatus.Text = (retake ? Text("ai-shot-retaken.txt", "The picture was taken again.") : Text("ai-shot-taken.txt", "The picture is ready.")) +
                    " " + Path.GetFileName(shot.File);
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Success");
                AddDiagnostic("Case screenshot: " + shot.File);
                UpdateCaseAvailability();
            };
            delay.Start();
        }

        private void UpdateCaseAvailability()
        {
            if (caseRecord == null) return;
            bool hasShot = !String.IsNullOrEmpty(caseRecord.ShotFile) && File.Exists(caseRecord.ShotFile);
            int elements = caseElements == null ? 0 : caseElements.ListedCount;
            ObservationStatus observed = observation.Status;
            aiCollectedText.Text = Text("ai-collected.txt", "Collected so far") + ":  " +
                CaseText.Screenshot + " " + (hasShot ? Text("ai-yes.txt", "yes") : Text("ai-no.txt", "not yet")) + "   /   " +
                Text("ai-collected-parts.txt", "parts") + " " + elements + "   /   " +
                Text("ai-collected-observed.txt", "observed clicks") + " " + observed.ClickCount + Environment.NewLine +
                Text("ai-case-folder.txt", "Case folder") + ": " + caseRecord.Folder;
            if (elements == 0)
            {
                aiCollectedText.Text += Environment.NewLine + Text("ai-noscan.txt", "No automatic scan has run for this target, so the assistant gets no list of parts and can only aim at screen points.");
            }
            if (caseHandoff != null) aiCollectedText.Text += Environment.NewLine + AttachmentLines(caseHandoff);
            aiCollectedSummary.Text = (hasShot ? Text("ai-yes.txt", "yes") : Text("ai-no.txt", "not yet")) + " / " +
                elements + " / " + observed.ClickCount;
            bool hasGoal = aiGoalInput.Text != null && aiGoalInput.Text.Trim().Length > 0;
            // The action bar is rebuilt for each step, so these only exist while
            // that step is on screen.
            if (aiCopyButton != null) aiCopyButton.IsEnabled = hasGoal;
            // Handing the request over is only finished when the files that go
            // with it exist. Going on to paste an answer before that means the
            // assistant answered without the investigation in front of it.
            if (aiImportGoButton != null) aiImportGoButton.IsEnabled = caseBundle != null && caseHandoff != null && caseHandoff.Complete;
        }

        // Names the files to attach and says whether each one is there. A file
        // that is missing says why on its own line rather than being left off.
        private string AttachmentLines(HandoffBundle handoff)
        {
            if (handoff == null) return Text("ai-attach-none.txt", "Nothing has been prepared to attach yet.");
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append(Text("ai-attach-heading.txt", "Attach these two files")).Append(":");
            text.AppendLine();
            text.Append("  ").Append(handoff.TextName ?? HandoffBuilder.TextFileName).Append("  ")
                .Append(handoff.TextPath != null && File.Exists(handoff.TextPath)
                    ? handoff.TextBytes + " bytes"
                    : Text("ai-attach-missing.txt", "missing"));
            text.AppendLine();
            if (handoff.PdfPath != null && File.Exists(handoff.PdfPath))
            {
                text.Append("  ").Append(handoff.PdfName ?? HandoffBuilder.PdfFileName).Append("  ")
                    .Append(handoff.PageCount + " " + Text("handoff-pages.txt", "pages") + ", " + handoff.PdfBytes + " bytes");
            }
            else
            {
                text.Append("  ").Append(handoff.NoPictureReason ?? Text("ai-attach-missing.txt", "missing"));
            }
            if (handoff.Folder != null)
            {
                text.AppendLine();
                text.Append("  ").Append(handoff.Folder);
            }
            for (int index = 0; index < handoff.Problems.Count; index++)
            {
                text.AppendLine();
                text.Append("  ").Append(handoff.Problems[index]);
            }
            return text.ToString();
        }

        private void OpenHandoffFolder()
        {
            if (caseRecord == null) return;
            string folder = caseRecord.HandoffFolder;
            OpenFolder(Directory.Exists(folder) ? folder : caseRecord.Folder);
        }

        private void CopyRequestText()
        {
            if (caseRecord == null) return;
            string goal = aiGoalInput.Text == null ? String.Empty : aiGoalInput.Text.Trim();
            if (goal.Length == 0)
            {
                aiRequestStatus.Text = Text("ai-need-goal.txt", "Write what you want to do first.");
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Caution");
                return;
            }
            caseRecord.Goal = goal;
            RequestBundle bundle = RequestBuilder.Build(caseRecord, lastScan, lastScan == null ? null : ScanSummary.Build(lastScan), log.Folder, goal, lastScreens);
            caseBundle = bundle;
            caseElements = bundle.Elements;
            caseRecord.ElementCount = bundle.Elements.ListedCount;
            if (bundle.TemplateProblems.Count > 0)
            {
                // A request with a hole in it is not sent. What is missing is
                // named, because only whoever edits the wording can put it back.
                aiRequestStatus.Text = String.Join(Environment.NewLine, bundle.TemplateProblems.ToArray());
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Danger");
                log.Append("events", new JsonObject()
                    .Add("kind", "case.request.rejected")
                    .Add("caseId", caseRecord.CaseId)
                    .Add("problems", bundle.TemplateProblems.ToArray()));
                return;
            }
            // The two files the chat window is actually given. They are built
            // before anything is said to have succeeded, so the wording on
            // screen can name files that exist.
            HandoffBundle handoff = HandoffBuilder.Build(caseRecord, bundle, lastScreens, caseRecord.HandoffFolder, goal);
            caseHandoff = handoff;
            caseScreens = lastScreens;
            premiseMismatch = false;
            // The request is written a second time now that the attachments are
            // on disk, so it lists the files that exist rather than the ones the
            // wording expected to exist.
            bundle.Handoff = handoff;
            RequestBuilder.Recompose(caseRecord, bundle, goal, log.Folder);
            caseRecord.BundleId = handoff.BundleId;
            caseRecord.PremiseHash = handoff.PremiseHash;
            caseRecord.ScanId = handoff.ScanId;
            bool wroteInvestigation = CaseStore.WriteText(caseRecord, caseRecord.InvestigationPath, bundle.Investigation);
            bool wroteRequest = CaseStore.WriteText(caseRecord, caseRecord.RequestPath, bundle.Request);
            bool wroteElements = true;
            try
            {
                JsonWriter.WriteFile(Path.Combine(caseRecord.Folder, "elements.json"), bundle.Elements.ToJson());
                if (lastScreens != null) JsonWriter.WriteFile(caseRecord.ScreensPath, lastScreens.ToJson());
                JsonWriter.WriteFile(caseRecord.HandoffRecordPath, handoff.ToJson());
            }
            catch (Exception exception)
            {
                wroteElements = false;
                AddDiagnostic("The element table could not be written: " + exception.Message);
            }
            if (!wroteInvestigation || !wroteRequest || !wroteElements)
            {
                aiRequestStatus.Text = Text("ai-write-failed.txt", "The request files could not be written.") + " " + CaseStore.LastError;
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Danger");
                return;
            }
            if (!handoff.Complete)
            {
                aiRequestStatus.Text = Text("ai-handoff-failed.txt", "The files to attach could not be made, so there is nothing to send yet.") +
                    Environment.NewLine + String.Join(Environment.NewLine, handoff.Problems.ToArray());
                SetCalloutTone(aiRequestCallout, aiRequestStatus, "Danger");
                log.Append("events", new JsonObject()
                    .Add("kind", "case.handoff.failed")
                    .Add("caseId", caseRecord.CaseId)
                    .Add("bundleId", handoff.BundleId)
                    .Add("problems", handoff.Problems.ToArray()));
                UpdateCaseAvailability();
                return;
            }
            caseRecord.Status = "requested";
            CaseStore.Save(caseRecord);
            CaseStore.AppendMarkdown(caseRecord, Environment.NewLine + "## " + CaseText.Goal + Environment.NewLine + Environment.NewLine +
                goal + Environment.NewLine + Environment.NewLine +
                "## " + CaseText.Request + Environment.NewLine + Environment.NewLine +
                "- bundleId: " + handoff.BundleId + "  premiseHash: " + handoff.PremiseHash + Environment.NewLine +
                "- " + CaseText.Handoff + ": [handoff/" + handoff.TextName + "](handoff/" + handoff.TextName + ")  sha256 " + handoff.TextSha256 + Environment.NewLine +
                "- " + CaseText.Screens + ": " + (handoff.PdfPath == null
                    ? handoff.NoPictureReason
                    : "[handoff/" + handoff.PdfName + "](handoff/" + handoff.PdfName + ")  " +
                        handoff.PageCount + " " + Text("handoff-pages.txt", "pages") + "  sha256 " + handoff.PdfSha256) + Environment.NewLine +
                "- " + CaseText.Investigation + ": [investigation.md](investigation.md)  (" + CaseText.Elements + " " + bundle.Elements.ListedCount + " / " + bundle.Elements.TotalCount + ")" + Environment.NewLine +
                "- " + Text("case-md-request-file.txt", "Request text") + ": [request.txt](request.txt)" + Environment.NewLine);
            string clipboardProblem = null;
            try
            {
                Clipboard.SetText(bundle.Request);
            }
            catch (Exception exception)
            {
                clipboardProblem = exception.GetType().Name + ": " + exception.Message;
            }
            log.Append("events", new JsonObject()
                .Add("kind", "case.request")
                .Add("caseId", caseRecord.CaseId)
                .Add("bundleId", handoff.BundleId)
                .Add("premiseHash", handoff.PremiseHash)
                .Add("scanId", handoff.ScanId)
                .Add("goalLength", goal.Length)
                .Add("elements", bundle.Elements.ListedCount)
                .Add("screens", handoff.ScreenCount)
                .Add("pages", handoff.PageCount)
                .Add("textFile", handoff.TextName)
                .Add("textSha256", handoff.TextSha256)
                .Add("pdfFile", handoff.PdfName)
                .Add("pdfSha256", handoff.PdfSha256)
                .Add("clipboard", clipboardProblem == null)
                .Add("clipboardError", clipboardProblem));
            aiRequestStatus.Text = (clipboardProblem == null
                ? Text("ai-copied.txt", "The request text is on the clipboard. Paste it into the chat and attach the two files.")
                : Text("ai-copy-failed.txt", "The text was written to request.txt but the clipboard refused it.") + " " + clipboardProblem) +
                Environment.NewLine + AttachmentLines(handoff);
            SetCalloutTone(aiRequestCallout, aiRequestStatus, clipboardProblem == null ? "Success" : "Caution");
            Toast(clipboardProblem == null ? Text("toast-done.txt", "Done") : Text("toast-warning.txt", "Warning"),
                aiRequestStatus.Text, clipboardProblem == null ? "Success" : "Caution");
            AddDiagnostic("Case request built: " + caseRecord.CaseId + " elements=" + bundle.Elements.ListedCount);
            UpdateCaseAvailability();
        }

        private void ImportAnswer()
        {
            if (caseRecord == null) return;
            string paste = aiAnswerInput.Text;
            if (String.IsNullOrWhiteSpace(paste))
            {
                aiImportStatus.Text = Text("ai-need-answer.txt", "Paste the answer first.");
                SetCalloutTone(aiImportCallout, aiImportStatus, "Caution");
                currentPlan = null;
                UpdatePlanAvailability();
                return;
            }
            if (caseElements == null) caseElements = CaseElementTable.Build(lastScan, RequestBuilder.ElementLimit);
            // An answer belongs to the investigation it was written against. If
            // the ground has moved since, the ids in the answer no longer mean
            // what they meant, so the answer is read but not run.
            string currentPremise = HandoffBuilder.PremiseHash(caseScreens, caseElements);
            premiseMismatch = caseHandoff != null && caseHandoff.PremiseHash != null &&
                !String.Equals(caseHandoff.PremiseHash, currentPremise, StringComparison.OrdinalIgnoreCase);
            OperationPlan plan = PlanReader.Parse(paste, caseElements);
            currentPlan = plan;
            answerSha256 = HandoffBuilder.HashText(paste);
            RenderPlan(plan);
            log.Append("events", new JsonObject()
                .Add("kind", "case.answer")
                .Add("caseId", caseRecord.CaseId)
                .Add("bundleId", caseHandoff == null ? null : caseHandoff.BundleId)
                .Add("answerSha256", answerSha256)
                .Add("answerLength", paste.Length)
                .Add("premiseHash", caseHandoff == null ? null : caseHandoff.PremiseHash)
                .Add("premiseNow", currentPremise)
                .Add("premiseMatches", !premiseMismatch)
                .Add("accepted", plan.Accepted)
                .Add("steps", plan.Steps.Count)
                .Add("problems", plan.Problems.ToArray())
                .Add("warnings", plan.Warnings.ToArray())
                .Add("ignoredFields", plan.Ignored.ToArray()));
            UpdatePlanAvailability();
        }

        private void RenderPlan(OperationPlan plan)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            if (!plan.Accepted)
            {
                aiImportStatus.Text = Text("ai-rejected.txt", "This answer cannot be used as it stands. Nothing was run.");
                SetCalloutTone(aiImportCallout, aiImportStatus, "Danger");
                text.AppendLine(Text("ai-problems-heading.txt", "What is wrong with it"));
                for (int index = 0; index < plan.Problems.Count; index++) text.AppendLine("- " + plan.Problems[index]);
                if (plan.Steps.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine(Text("ai-partial-note.txt", "Some steps did read correctly, but a plan is run whole or not at all.") + " " + plan.Steps.Count);
                }
                aiPlanText.Text = text.ToString();
                return;
            }
            aiImportStatus.Text = Text("ai-accepted.txt", "Read successfully. Check the operations below before running them.") + "  " +
                Text("ai-step-count.txt", "steps") + " " + plan.Steps.Count;
            SetCalloutTone(aiImportCallout, aiImportStatus, "Success");
            if (!String.IsNullOrWhiteSpace(plan.Title)) text.AppendLine(plan.Title);
            if (!String.IsNullOrWhiteSpace(plan.Notes)) text.AppendLine(plan.Notes);
            if (text.Length > 0) text.AppendLine();
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                PlanStep step = plan.Steps[index];
                text.AppendLine(step.Describe());
                if (!String.IsNullOrEmpty(step.Expect)) text.AppendLine("      " + Text("plan-col-expect.txt", "expected") + ": " + step.Expect);
                if (!String.IsNullOrEmpty(step.Why)) text.AppendLine("      " + Text("ai-why.txt", "why") + ": " + step.Why);
            }
            if (plan.Warnings.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Text("plan-warn-heading.txt", "Noted before running:"));
                for (int index = 0; index < plan.Warnings.Count; index++) text.AppendLine("- " + plan.Warnings[index]);
            }
            if (plan.Ignored.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Text("plan-ignored-heading.txt", "Fields in the answer that this tool does not use:") + " " + String.Join(", ", plan.Ignored.ToArray()));
            }
            aiPlanText.Text = text.ToString();
        }

        private void UpdatePlanAvailability()
        {
            bool accepted = currentPlan != null && currentPlan.Accepted;
            bool needsWrite = accepted && currentPlan.NeedsWrite;
            bool allowed = !needsWrite || aiWriteToggle.IsChecked == true;
            if (aiRunGoButton != null) aiRunGoButton.IsEnabled = accepted && allowed && !premiseMismatch && !planRunning;
            if (accepted && premiseMismatch)
            {
                aiImportStatus.Text = Text("ai-premise-changed.txt",
                    "The target has been looked at again since these files were made, so the part ids in this answer no longer point at what they did. Build the request again and ask once more.");
                SetCalloutTone(aiImportCallout, aiImportStatus, "Caution");
                return;
            }
            if (accepted && !allowed)
            {
                aiImportStatus.Text = Text("ai-needs-write.txt", "These operations change the target, so switch on the permission below before running them.");
                SetCalloutTone(aiImportCallout, aiImportStatus, "Caution");
            }
        }

        private void StartPlanRun()
        {
            if (caseRecord == null || currentPlan == null || !currentPlan.Accepted || planRunning || premiseMismatch) return;
            int runNumber = caseRecord.RunCount + 1;
            bool writeEnabled = aiWriteToggle.IsChecked == true;
            bool stopOnFailure = aiStopOnFailure.IsChecked == true;
            // The answer is kept exactly as it arrived, and its hash is written
            // beside it, so what was run can be shown to be what was answered.
            CaseStore.WriteText(caseRecord, caseRecord.AnswerPath(runNumber), aiAnswerInput.Text);
            CaseStore.AppendMarkdown(caseRecord, Environment.NewLine + "### " + CaseText.Answer + " #" + runNumber + Environment.NewLine + Environment.NewLine +
                "- bundleId: " + (caseHandoff == null ? "-" : caseHandoff.BundleId) + Environment.NewLine +
                "- answer sha256: " + (answerSha256 ?? "-") + "  (" + (aiAnswerInput.Text == null ? 0 : aiAnswerInput.Text.Length) + " chars)" + Environment.NewLine +
                "- premiseHash: " + (caseHandoff == null ? "-" : caseHandoff.PremiseHash) + Environment.NewLine);
            try
            {
                JsonWriter.WriteFile(caseRecord.PlanPath(runNumber), PlanJson.Plan(currentPlan, runNumber));
            }
            catch (Exception exception)
            {
                AddDiagnostic("The plan could not be written: " + exception.Message);
            }
            CaseStore.AppendMarkdown(caseRecord, PlanMarkdown.Plan(currentPlan, runNumber, currentPlan.Json));
            caseRecord.Status = "imported";
            CaseStore.Save(caseRecord);
            log.Append("events", new JsonObject()
                .Add("kind", "case.run.start")
                .Add("caseId", caseRecord.CaseId)
                .Add("runNumber", runNumber)
                .Add("bundleId", caseHandoff == null ? null : caseHandoff.BundleId)
                .Add("answerSha256", answerSha256)
                .Add("premiseHash", caseHandoff == null ? null : caseHandoff.PremiseHash)
                .Add("steps", currentPlan.Steps.Count)
                .Add("writeEnabled", writeEnabled)
                .Add("stopOnFailure", stopOnFailure));

            planRunning = true;
            if (aiRunGoButton != null) aiRunGoButton.IsEnabled = false;
            aiRunList.Items.Clear();
            aiRunProgress.Text = "0 / " + currentPlan.Steps.Count;
            GoTo(ShellStep.AiRun);
            // The highlight sits exactly on whatever is under the pointer, so it
            // is taken down for the whole run rather than being resolved as the
            // target of one of these operations.
            bool wasFrozen = frozen;
            frozen = true;
            suppressOverlay = true;
            overlay.Hide();
            PlanRunner runner = new PlanRunner();
            planRunner = runner;
            OperationPlan plan = currentPlan;
            Action<PlanRunProgress> progress = delegate(PlanRunProgress value)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ShowPlanProgress(value); }));
            };
            Task<PlanRunResult> task = Task.Factory.StartNew(delegate { return runner.Run(plan, runNumber, writeEnabled, stopOnFailure, progress); });
            task.ContinueWith(delegate(Task<PlanRunResult> completed)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    planRunning = false;
                    planRunner = null;
                    frozen = wasFrozen;
                    suppressOverlay = false;
                    if (completed.IsFaulted)
                    {
                        string message = completed.Exception.GetBaseException().Message;
                        AddDiagnostic("The run failed: " + message);
                        log.Append("events", new JsonObject().Add("kind", "case.run.failed").Add("caseId", caseRecord.CaseId).Add("message", message));
                        aiResultSummary.Text = Text("ai-run-failed.txt", "The operations could not be run.") + Environment.NewLine + message;
                        GoTo(ShellStep.AiResult);
                        return;
                    }
                    FinishPlanRun(completed.Result);
                }));
            });
        }

        private void ShowPlanProgress(PlanRunProgress value)
        {
            if (value == null || currentPlan == null) return;
            if (value.Finished)
            {
                aiRunProgress.Text = Text("ai-run-wrapping.txt", "Writing the result down.");
                return;
            }
            aiRunProgress.Text = (value.Index + 1) + " / " + value.Total;
            if (value.Result == null || value.Step == null) return;
            string outcome = value.Result.Skipped ? Text("plan-run-skipped.txt", "not run") : value.Result.Outcome;
            aiRunList.Items.Add(value.Step.Id + ". " + value.Step.Action + "  " + (value.Step.ElementId ?? ("(" + value.Step.X + "," + value.Step.Y + ")")) +
                "  ->  " + outcome + (String.IsNullOrEmpty(value.Result.Method) ? String.Empty : "  [" + value.Result.Method + "]"));
            aiRunList.ScrollIntoView(aiRunList.Items[aiRunList.Items.Count - 1]);
        }

        private void CancelPlanRun()
        {
            PlanRunner runner = planRunner;
            if (runner != null)
            {
                runner.Cancel();
                AddDiagnostic("The run was stopped by the operator.");
                log.Append("events", new JsonObject().Add("kind", "case.run.cancel").Add("caseId", caseRecord == null ? null : caseRecord.CaseId));
            }
            else
            {
                GoTo(ShellStep.AiImport);
            }
        }

        private void FinishPlanRun(PlanRunResult result)
        {
            lastRun = result;
            if (caseRecord == null) return;
            for (int index = 0; index < result.Steps.Count; index++)
            {
                JsonObject record = PlanJson.StepResult(result.Steps[index], result.RunNumber);
                CaseStore.AppendLine(caseRecord, caseRecord.RunPath(result.RunNumber), JsonWriter.WriteCompact(record));
                log.Append("events", record);
            }
            CaseStore.AppendLine(caseRecord, caseRecord.RunPath(result.RunNumber), JsonWriter.WriteCompact(PlanJson.RunSummary(result)));
            log.Append("events", PlanJson.RunSummary(result));
            CaseStore.AppendMarkdown(caseRecord, PlanMarkdown.Run(result));
            caseRecord.RunCount = result.RunNumber;
            caseRecord.StepCount += result.Steps.Count;
            caseRecord.SuccessCount += result.SuccessCount;
            caseRecord.FailureCount += result.FailureCount;
            caseRecord.Status = "ran";
            CaseStore.Save(caseRecord);
            log.FlushDurable();

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.AppendLine(Text("ai-result-heading.txt", "Operation test") + " #" + result.RunNumber +
                (String.IsNullOrWhiteSpace(result.Title) ? String.Empty : "  " + result.Title));
            text.AppendLine(Text("plan-run-success.txt", "success") + " " + result.SuccessCount +
                "  /  " + Text("plan-run-failed.txt", "failed") + " " + result.FailedCount +
                "  /  " + Text("plan-run-blocked.txt", "refused") + " " + result.BlockedCount +
                "  /  " + Text("plan-run-notsupported.txt", "not supported") + " " + result.NotSupportedCount +
                "  /  " + Text("plan-run-unknown.txt", "no change seen") + " " + result.UnknownCount +
                "  /  " + Text("plan-run-skipped.txt", "not run") + " " + result.SkippedCount);
            if (result.Cancelled) text.AppendLine(Text("plan-run-cancelled.txt", "The operator stopped this run."));
            text.AppendLine();
            for (int index = 0; index < result.Steps.Count; index++)
            {
                PlanStepResult item = result.Steps[index];
                if (item.Step == null) continue;
                text.AppendLine(item.Step.Id + ". " + item.Step.Action + "  " + (item.Step.TargetLabel ?? String.Empty));
                text.AppendLine("      " + (item.Skipped
                    ? Text("plan-run-skipped.txt", "not run") + ": " + item.SkipReason
                    : item.Outcome + "  [" + item.Method + "]  " + item.DurationMs + " ms"));
                if (!String.IsNullOrEmpty(item.Reaction)) text.AppendLine("      " + Text("plan-col-reaction.txt", "reaction seen") + ": " + item.Reaction);
                if (!String.IsNullOrEmpty(item.ErrorMessage)) text.AppendLine("      " + Text("plan-run-reason.txt", "reason") + ": " + ReasonText(item));
            }
            text.AppendLine();
            text.AppendLine(Text("ai-result-saved.txt", "Written into the case folder") + ": " + caseRecord.Folder);
            aiResultSummary.Text = text.ToString();
            AddDiagnostic("Run " + result.RunNumber + ": success=" + result.SuccessCount + " failed=" + result.FailedCount +
                " blocked=" + result.BlockedCount + " notSupported=" + result.NotSupportedCount +
                " unknown=" + result.UnknownCount + " skipped=" + result.SkippedCount);
            GoTo(ShellStep.AiResult);
            UpdateSaveChip();
        }

        // A refusal from the probe layer arrives as a stable code plus an English
        // sentence, but the sentence is put in front of the operator, and every
        // other word on this screen comes from assets/messages. The code chooses
        // the wording; anything without wording of its own keeps the sentence the
        // probe wrote, so a new refusal is never silently blanked out.
        private string ReasonText(PlanStepResult item)
        {
            if (item == null || String.IsNullOrEmpty(item.ErrorMessage)) return String.Empty;
            if (String.IsNullOrEmpty(item.Method)) return item.ErrorMessage;
            return Text("probe-" + item.Method.Replace('.', '-') + ".txt", item.ErrorMessage);
        }

        private void OpenCaseFolder()
        {
            if (caseRecord == null) return;
            OpenFolder(caseRecord.Folder);
        }

        // ---------- history ----------

        private void ShowHistory()
        {
            RefreshHistory();
            GoTo(ShellStep.History);
        }

        private void RefreshHistory()
        {
            CaseRecord[] records = CaseStore.List(baseDir);
            historyList.Items.Clear();
            for (int index = 0; index < records.Length; index++) historyList.Items.Add(HistoryItem(records[index]));
            historyHint.Text = Text("history-hint.txt", "Every case recorded on this machine, newest first.") + "  (" + records.Length + ")" +
                Environment.NewLine + CaseStore.Root(baseDir);
            historyDetail.Text = String.Empty;
            historySelection = null;
        }

        private ListBoxItem HistoryItem(CaseRecord record)
        {
            StackPanel content = new StackPanel();
            TextBlock title = new TextBlock();
            title.Text = record.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "   " +
                (String.IsNullOrEmpty(record.TargetProcess) ? "?" : record.TargetProcess) +
                (String.IsNullOrEmpty(record.TargetTitle) ? String.Empty : " / " + Shorten(record.TargetTitle, 34));
            title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(title);
            TextBlock detail = new TextBlock();
            detail.FontSize = 11;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Foreground = Theme.TextMuted;
            detail.Text = Shorten(record.Goal, 60) + "   |   " + HistoryOutcome(record);
            content.Children.Add(detail);
            ListBoxItem item = new ListBoxItem();
            item.Content = content;
            item.Tag = record;
            System.Windows.Automation.AutomationProperties.SetName(item, record.CaseId + " / " +
                (String.IsNullOrEmpty(record.TargetProcess) ? "?" : record.TargetProcess) + " / " + HistoryOutcome(record));
            return item;
        }

        private string HistoryOutcome(CaseRecord record)
        {
            if (record.Status == "unreadable") return Text("history-unreadable.txt", "the record could not be read");
            if (record.RunCount == 0) return Text("history-notrun.txt", "not run yet") + " (" + HistoryStatus(record.Status) + ")";
            return Text("plan-run-success.txt", "success") + " " + record.SuccessCount +
                " / " + Text("plan-run-failed.txt", "failed") + " " + record.FailureCount +
                " / " + Text("history-steps.txt", "steps") + " " + record.StepCount;
        }

        private string HistoryStatus(string status)
        {
            if (status == "collecting") return Text("history-status-collecting.txt", "collecting");
            if (status == "requested") return Text("history-status-requested.txt", "request built");
            if (status == "imported") return Text("history-status-imported.txt", "answer taken in");
            if (status == "ran") return Text("history-status-ran.txt", "operations tried");
            return status;
        }

        private void ShowHistoryDetail()
        {
            ListBoxItem item = historyList.SelectedItem as ListBoxItem;
            historySelection = item == null ? null : item.Tag as CaseRecord;
            if (historySelection == null)
            {
                // An empty panel says nothing about why it is empty.
                historyDetail.Text = Text("history-detail-idle.txt", "Select a record in the list to see it here.");
                return;
            }
            string text = CaseStore.ReadText(historySelection.MarkdownPath);
            historyDetail.Text = text == null
                ? Text("history-nomarkdown.txt", "This case has no record file.") + Environment.NewLine + historySelection.Folder
                : text;
        }

        private void ContinueSelectedCase()
        {
            if (historySelection == null)
            {
                historyHint.Text = Text("history-select-first.txt", "Select a case in the list first.");
                return;
            }
            CaseElementTable table = CaseElementTable.Load(Path.Combine(historySelection.Folder, "elements.json"));
            caseRecord = historySelection;
            // A reopened case must never borrow the part list of whatever is
            // being investigated now: an id would then resolve to a point in a
            // different application. No stored list means point targets only.
            caseElements = table != null ? table : CaseElementTable.Build(null, RequestBuilder.ElementLimit);
            // The screens the stored case was built from, not whatever this
            // session has scanned since. The premise the answer has to match is
            // the one this case recorded.
            caseScreens = ScreenLedger.Load(historySelection.ScreensPath);
            caseBundle = null;
            caseHandoff = null;
            premiseMismatch = false;
            answerSha256 = null;
            if (!String.IsNullOrEmpty(historySelection.PremiseHash))
            {
                HandoffBundle stored = new HandoffBundle();
                stored.BundleId = historySelection.BundleId;
                stored.PremiseHash = historySelection.PremiseHash;
                stored.ScanId = historySelection.ScanId;
                stored.Folder = historySelection.HandoffFolder;
                stored.TextPath = historySelection.HandoffTextPath;
                stored.PdfPath = historySelection.HandoffPdfPath;
                caseHandoff = stored;
            }
            currentPlan = null;
            lastRun = null;
            caseReopened = true;
            aiAnswerInput.Clear();
            aiPlanText.Text = String.Empty;
            aiImportStatus.Text = String.Empty;
            SetCalloutTone(aiImportCallout, aiImportStatus, "Neutral");
            aiGoalInput.Text = historySelection.Goal ?? String.Empty;
            log.Append("events", new JsonObject().Add("kind", "case.reopen").Add("caseId", caseRecord.CaseId).Add("folder", caseRecord.Folder));
            GoTo(ShellStep.AiImport);
            aiImportStatus.Text = table == null
                ? Text("history-noelements.txt", "This case kept no part list, so an answer can only aim at screen points.")
                : Text("history-reopened.txt", "Reopened. The positions are the ones recorded during that investigation, so check the target is arranged the same way.") +
                    "  " + Text("ai-collected-parts.txt", "parts") + " " + table.ListedCount;
            aiPlanText.Text = String.Empty;
            SetCalloutTone(aiImportCallout, aiImportStatus, "Caution");
            AddDiagnostic("Case reopened: " + caseRecord.CaseId);
            UpdatePlanAvailability();
        }

        private void OpenSelectedCaseFolder()
        {
            if (historySelection == null)
            {
                historyHint.Text = Text("history-select-first.txt", "Select a case in the list first.");
                return;
            }
            OpenFolder(historySelection.Folder);
        }

        // ---------- shared plumbing ----------

        private void OpenFolder(string folder)
        {
            try
            {
                if (!String.IsNullOrEmpty(folder)) System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception exception)
            {
                AddDiagnostic("The folder could not be opened: " + exception.Message);
            }
        }

        private void DrainWinEvents()
        {
            WinEventRecord[] records = winEvents.Drain();
            for (int index = 0; index < records.Length; index++)
            {
                WinEventRecord record = records[index];
                session.AddEvent(record.Type, "winEvent", "hwnd=0x" + record.Hwnd.ToString("X") + " object=" + record.ObjectId);
                timelineList.Items.Add(record.At.ToString("HH:mm:ss.fff") + "  " + record.Type);
                if (timelineList.Items.Count > 500) timelineList.Items.RemoveAt(0);
                recentEvents.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, record.Type));
                if (recentEvents.Count > 200) recentEvents.RemoveAt(0);
                observation.OnApplicationEvent(record);
            }
            if (records.Length > 0 && step == ShellStep.Observe) UpdateObserveCounts();
        }

        private void UpdateHealth()
        {
            AcquisitionHealth health = Probe.GetHealth();
            if (health.RestartCount > lastRestartCount)
            {
                for (int index = lastRestartCount; index < health.RestartCount; index++)
                {
                    session.AddFailure("acquisition", "ACQ-RESTART", "The acquisition worker was terminated and replaced.", null);
                    log.Append("events", new JsonObject().Add("kind", "acquisition.restart").Add("restartCount", health.RestartCount));
                }
                lastRestartCount = health.RestartCount;
            }
            SetBadgeTone(headerHealthBadge, headerHealth, health.State == "ready" || health.State == "acquiring" ? "Success" : "Caution");
            if (health.State == "ready") headerHealth.Text = Text("acquisition-ready.txt", "Acquisition: ready");
            else if (health.State == "warming-spare") headerHealth.Text = Text("acquisition-warming.txt", "Acquisition: rebuilding spare") + " / " + health.RestartCount;
            else if (health.State == "disabled") headerHealth.Text = Text("acquisition-degraded.txt", "Acquisition: window handles only");
            else if (health.State == "acquiring") headerHealth.Text = Text("acquisition-busy.txt", "Acquisition: reading");
            else headerHealth.Text = "Acquisition: " + health.State;
        }

        private void UpdateSaveChip()
        {
            SessionLogStatus status = log.Status;
            if (!status.Enabled)
            {
                headerSave.Text = Text("autosave-off.txt", "Auto save unavailable");
                SetBadgeTone(headerSaveBadge, headerSave, "Danger");
                savePathText.Text = Text("autosave-off.txt", "Auto save unavailable") + ": " + status.DisabledReason;
                if (detailsSummary != null) detailsSummary.Text = Text("autosave-off.txt", "Auto save unavailable");
                return;
            }
            headerSave.Text = Text("autosave-on.txt", "Saved automatically") + " " + status.RecordCount +
                (status.WriteFailures > 0 ? " / " + Text("autosave-failures.txt", "failures") + " " + status.WriteFailures : String.Empty);
            SetBadgeTone(headerSaveBadge, headerSave, status.WriteFailures > 0 ? "Danger" : "Success");
            savePathText.Text = Text("autosave-path.txt", "Records are written here as they happen") + ":" + Environment.NewLine + status.Directory +
                (status.LastError == null ? String.Empty : Environment.NewLine + Text("autosave-lasterror.txt", "Last write problem") + ": " + status.LastError);
            // Shut, the fold reports how much is recorded and whether any write
            // has failed, which is the reason a specialist would open it.
            if (detailsSummary != null)
            {
                detailsSummary.Text = Text("details-summary-records.txt", "records") + " " + status.RecordCount +
                    (status.WriteFailures > 0 ? " / " + Text("autosave-failures.txt", "failures") + " " + status.WriteFailures : String.Empty);
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                if (log.Folder != null) System.Diagnostics.Process.Start("explorer.exe", "\"" + log.Folder + "\"");
            }
            catch (Exception exception)
            {
                AddDiagnostic("The folder could not be opened: " + exception.Message);
            }
        }

        private void OnValuePolicyChanged(object sender, SelectionChangedEventArgs args)
        {
            string selected = valuePolicy.SelectedIndex == 1 ? "full" : (valuePolicy.SelectedIndex == 2 ? "none" : "maskedOnly");
            if (selected == session.Data.ValueCapture) return;
            if (selected == "full")
            {
                MessageBoxResult answer = MessageBox.Show(Text("full-value-warning.txt", "This session will persist live value text in exports."), Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK)
                {
                    valuePolicy.SelectedIndex = session.Data.ValueCapture == "none" ? 2 : 0;
                    return;
                }
            }
            session.SetValueCapture(selected, "Explicit UI selection");
            log.Append("events", new JsonObject().Add("kind", "valuePolicy.change").Add("policy", selected));
        }

        private void TakeFullScreenshot()
        {
            MessageBoxResult answer = MessageBox.Show(
                Text("full-shot-warning.txt", "Full-screen captures cannot be masked automatically and can contain business data."),
                Title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
            string folder = Path.Combine(baseDir, "runtime", "live-session", "shots");
            string path = Path.Combine(folder, "full-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            ShotResult shot = Capture.Full(path, true);
            session.AddShot(shot, "full");
            log.Append("events", new JsonObject().Add("kind", "shot.full").Add("file", shot.File).Add("state", shot.Status == null ? null : shot.Status.State));
            AddDiagnostic(shot.Status.State == "ok" ? "Full screenshot saved: " + shot.File : "Full screenshot failed.");
        }

        private void ExportPack()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Text("export-folder.txt", "Select the folder that will contain the investigation pack.");
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string name = "APPSTUDIO_Target_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                PackResult result = PackWriter.Write(session.Data, Path.Combine(dialog.SelectedPath, name));
                if (result.Status.State == "ok")
                {
                    session.AddEvent("pack.export", "tool", result.Folder);
                    log.Append("events", new JsonObject().Add("kind", "pack.export").Add("folder", result.Folder));
                    AddDiagnostic("Pack exported: " + result.Folder);
                }
                else
                {
                    AddDiagnostic(result.Status.Reasons[0].Code + " " + result.Status.Reasons[0].Message);
                }
            }
        }

        private void OnHotkey(string action)
        {
            if (action == "toggle")
            {
                if (step == ShellStep.Observe) StopObservation();
                else if (selectedTarget != null) StartObservation();
            }
            else if (action == "freeze") ToggleFreeze();
            else if (action == "pin") PinCurrent();
            else if (action == "fullShot") TakeFullScreenshot();
            else if (action == "memo") FocusMemo();
            else if (action == "emergency")
            {
                if (observation.Active) observation.Stop();
                mouseTimer.Stop();
                StopHover();
                CancelScan();
                PlanRunner running = planRunner;
                if (running != null) running.Cancel();
                writeToggle.IsChecked = false;
                aiWriteToggle.IsChecked = false;
                headerMode.Text = Text("read-only.txt", "Read only");
                ProbeRunner.EmergencyStop();
                log.Append("events", new JsonObject().Add("kind", "emergency.stop"));
                log.FlushDurable();
                AddDiagnostic("Emergency stop: acquisition stopped and read-only mode restored.");
                GoTo(ShellStep.Menu);
            }
        }

        // The chosen window can be drawn by more than one process, so belonging
        // to the target is a question about that set, not a single id.
        private bool IsChosenTarget(int processId)
        {
            if (selectedTargetProcessId == 0) return true;
            if (processId == selectedTargetProcessId) return true;
            for (int index = 0; index < selectedContentProcessIds.Length; index++)
            {
                if (selectedContentProcessIds[index] == processId) return true;
            }
            return false;
        }

        private string[] ProcessIdText(int[] ids)
        {
            string[] text = new string[ids.Length];
            for (int index = 0; index < ids.Length; index++) text[index] = ProcessName(ids[index]) + " (pid " + ids[index] + ")";
            return text;
        }

        private bool ContainsShell(int x, int y)
        {
            RectValue rect = WindowTools.GetPhysicalRect(shellHandle);
            return rect != null && x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height;
        }

        private void AddDiagnostic(string value)
        {
            diagnosticsText.AppendText(Environment.NewLine + DateTime.Now.ToString("HH:mm:ss.fff") + " " + value);
            diagnosticsText.ScrollToEnd();
        }

        private void OnClosed(object sender, EventArgs args)
        {
            hoverTimer.Stop();
            healthTimer.Stop();
            eventTimer.Stop();
            mouseTimer.Stop();
            flushTimer.Stop();
            if (observation.Active) observation.Stop();
            ScanRunner runner = scanRunner;
            if (runner != null) runner.Cancel();
            PlanRunner plan = planRunner;
            if (plan != null) plan.Cancel();
            winEvents.Dispose();
            ProbeRunner.EmergencyStop();
            if (autoCloseTimer != null) autoCloseTimer.Stop();
            hotkeys.Dispose();
            overlay.Dispose();
            log.Append("events", new JsonObject().Add("kind", "session.end").Add("elements", session.Data.Elements.Count));
            log.FlushDurable();
            log.Dispose();
        }

        private string BuildHotkeyHint(HotkeyRegistration[] registrations)
        {
            string freeze = null;
            string pin = null;
            for (int index = 0; index < registrations.Length; index++)
            {
                if (!registrations[index].Registered) continue;
                if (registrations[index].Action == "freeze") freeze = registrations[index].Combo;
                else if (registrations[index].Action == "pin") pin = registrations[index].Combo;
            }
            string freezeLabel = Text("freeze.txt", "Hold the display");
            string pinLabel = Text("pin.txt", "Keep this part");
            string freezePart = freeze == null ? freezeLabel + ": " + Text("hotkey-none.txt", "button only") : freezeLabel + ": " + freeze;
            string pinPart = pin == null ? pinLabel + ": " + Text("hotkey-none.txt", "button only") : pinLabel + ": " + pin;
            return freezePart + " / " + pinPart;
        }

        private string ProcessName(int processId)
        {
            if (processId == 0) return "?";
            string name;
            if (processNames.TryGetValue(processId, out name)) return name;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    name = process.ProcessName;
                }
            }
            catch
            {
                name = "?";
            }
            processNames[processId] = name;
            return name;
        }

        // ---------- small view helpers ----------

        private string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        private static string Shorten(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Length <= limit ? value : value.Substring(0, limit - 1) + "...";
        }

        private static string State(ProbeStatus status)
        {
            return status == null ? "unavailable" : status.State;
        }

        private static string Value(string value)
        {
            return String.IsNullOrEmpty(value) ? "-" : value;
        }

        // ---------- shared shapes ----------
        //
        // Every one of these is the WPF twin of a design system component, so the
        // same meaning always arrives in the same shape: a section title looks
        // like a section title in every view, and a fold that hides detail
        // always carries a summary of what it hides.

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

        private static TextBlock SubHeading(string text)
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

        private TextBox ReadOnlyText(int minHeight)
        {
            TextBox box = new TextBox();
            box.SetResourceReference(StyleProperty, "AppReadOnlyText");
            box.IsReadOnly = true;
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.MinHeight = minHeight;
            box.MaxHeight = minHeight * 3;
            box.Margin = new Thickness(0, Theme.Space2, 0, 0);
            return box;
        }

        private static Border Card(UIElement content)
        {
            Border card = new Border();
            card.Background = Theme.Surface;
            card.BorderBrush = Theme.Border;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.Padding = new Thickness(Theme.Space4);
            card.Margin = new Thickness(0, 0, 0, Theme.Space3);
            card.Child = content;
            return card;
        }

        // A folded section that says what it holds while it is still shut. The
        // caption names the subject; the summary on the right says how much is
        // in there. Nothing needed for the next decision goes in here.
        private Expander Accordion(string caption, UIElement content, out TextBlock summary)
        {
            TextBlock captionBlock = new TextBlock();
            captionBlock.Text = caption;
            return Accordion(captionBlock, content, out summary);
        }

        private Expander Accordion(TextBlock captionBlock, UIElement content, out TextBlock summary)
        {
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
            // The header is a panel, so the accessible name has to be stated
            // rather than derived from it.
            System.Windows.Automation.AutomationProperties.SetName(expander, captionBlock.Text);
            return expander;
        }

        private static Border Callout(UIElement content)
        {
            Border box = new Border();
            box.CornerRadius = new CornerRadius(Theme.RadiusSm);
            box.BorderThickness = new Thickness(1);
            box.BorderBrush = Theme.BorderSubtle;
            box.Background = Theme.SurfaceSunken;
            box.Padding = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            box.Margin = new Thickness(0, Theme.Space3, 0, 0);
            box.Child = content;
            return box;
        }

        private static void SetCalloutTone(Border callout, TextBlock text, string tone)
        {
            if (callout == null) return;
            callout.Visibility = String.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (String.Equals(tone, "Danger", StringComparison.Ordinal))
            {
                callout.BorderBrush = Theme.Danger;
                callout.Background = Theme.DangerSoft;
                text.Foreground = Theme.DangerText;
            }
            else if (String.Equals(tone, "Caution", StringComparison.Ordinal))
            {
                callout.BorderBrush = Theme.Caution;
                callout.Background = Theme.CautionSoft;
                text.Foreground = Theme.CautionText;
            }
            else if (String.Equals(tone, "Success", StringComparison.Ordinal))
            {
                callout.BorderBrush = Theme.Success;
                callout.Background = Theme.SuccessSoft;
                text.Foreground = Theme.SuccessText;
            }
            else
            {
                callout.BorderBrush = Theme.BorderSubtle;
                callout.Background = Theme.SurfaceSunken;
                text.Foreground = Theme.TextSub;
            }
        }

        // A permission is always asked with the same component: a real tick box
        // with the consequence written next to it, inside a marked box so it
        // never reads as an ordinary preference.
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

        // The design system stat card: one number, one word under it. Used where a
        // count is the answer, so the answer does not have to be read out of a
        // paragraph.
        private static Border StatCard(out TextBlock value, string label)
        {
            value = new TextBlock();
            value.Text = "-";
            value.FontSize = Theme.NumSize;
            value.FontWeight = FontWeights.Bold;
            value.Foreground = Theme.Text;
            StackPanel stack = new StackPanel();
            stack.Children.Add(value);
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
            card.Margin = new Thickness(0, 0, Theme.Space2, 0);
            card.Child = stack;
            return card;
        }

        private Border Badge(TextBlock label, string text, string tone)
        {
            label.Text = text;
            label.FontSize = Theme.MicroSize;
            label.FontWeight = FontWeights.SemiBold;
            Border badge = new Border();
            badge.CornerRadius = new CornerRadius(Theme.RadiusSm);
            badge.BorderThickness = new Thickness(1);
            badge.Padding = new Thickness(Theme.Space2, 2, Theme.Space2, 2);
            badge.Margin = new Thickness(0, 0, Theme.Space2, 0);
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

        private Border Choice(string title, string note, Action action)
        {
            Border box = new Border();
            box.Background = Theme.Surface;
            box.BorderBrush = Theme.Border;
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(Theme.RadiusMd);
            box.Padding = new Thickness(Theme.Space3);
            box.Margin = new Thickness(0, Theme.Space2, 0, 0);
            DockPanel content = new DockPanel();
            content.LastChildFill = true;
            Button start = new Button();
            start.Content = Text("choice-start.txt", "Start");
            start.SetResourceReference(StyleProperty, "AppButton");
            start.VerticalAlignment = VerticalAlignment.Center;
            start.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            start.MinWidth = 84;
            // Four of these sit on the menu and they all read "start", so on
            // their own content there is nothing to tell them apart. The name
            // of the choice is what SPEC 3 calls the option, so that is what
            // the button is called in the accessibility tree, the same way the
            // other controls whose label lives outside them are named.
            System.Windows.Automation.AutomationProperties.SetName(start, title);
            start.Click += delegate { action(); };
            DockPanel.SetDock(start, Dock.Right);
            content.Children.Add(start);
            StackPanel words = new StackPanel();
            words.VerticalAlignment = VerticalAlignment.Center;
            TextBlock heading = new TextBlock();
            heading.Text = title;
            heading.FontWeight = FontWeights.Bold;
            heading.FontSize = Theme.SectionSize;
            heading.Foreground = Theme.Text;
            heading.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(heading);
            TextBlock detail = new TextBlock();
            detail.Text = note;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.FontSize = Theme.MetaSize;
            detail.LineHeight = Theme.MetaSize * Theme.BodyLine;
            detail.Foreground = Theme.TextMuted;
            detail.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(detail);
            content.Children.Add(words);
            box.Child = content;
            return box;
        }

        private Button AddButton(Panel panel, string label, RoutedEventHandler handler, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            button.SetResourceReference(StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            if (handler != null) button.Click += handler;
            panel.Children.Add(button);
            return button;
        }

        private void UpdateMemoSummary()
        {
            if (memoSummary == null || noteInput == null) return;
            string value = noteInput.Text == null ? String.Empty : noteInput.Text.Trim();
            memoSummary.Text = value.Length == 0
                ? Text("memo-empty.txt", "empty")
                : Shorten(value, 24);
        }
    }
}
