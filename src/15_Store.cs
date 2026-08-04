namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    // One application seen during a session. Recording crosses applications, so
    // the session keeps a list of them instead of a single chosen target.
    public sealed class AppRef
    {
        public string Key;
        public int ProcessId;
        public string ProcessName;
        public string ExecutablePath;
        public string PathProblem;

        public string Display
        {
            get { return String.IsNullOrEmpty(ProcessName) ? ("pid " + ProcessId) : ProcessName; }
        }

        public JsonObject ToJson()
        {
            return new JsonObject()
                .Add("key", Key)
                .Add("processId", ProcessId)
                .Add("processName", ProcessName)
                .Add("executablePath", ExecutablePath)
                .Add("pathProblem", PathProblem);
        }
    }

    // One attempt at carrying an action out through one route. The list of these
    // is the record of what was tried and what happened, in order, so a reader
    // never has to guess which route actually moved the application.
    public sealed class RouteAttempt
    {
        public string Route;
        public string Method;
        public string Outcome;
        public int DurationMs;
        public string ErrorCode;
        public string ErrorMessage;
        public string Effect;

        public string Line
        {
            get { return (Method == null ? Route : Method) + ":" + (Outcome == null ? "unknown" : Outcome); }
        }

        public JsonObject ToJson()
        {
            return new JsonObject()
                .Add("route", Route)
                .Add("method", Method)
                .Add("outcome", Outcome)
                .Add("durationMs", DurationMs)
                .Add("errorCode", ErrorCode)
                .Add("errorMessage", ErrorMessage)
                .Add("effect", Effect);
        }

        public static RouteAttempt FromJson(Dictionary<string, object> item)
        {
            RouteAttempt attempt = new RouteAttempt();
            attempt.Route = JsonReader.Text(item, "route");
            attempt.Method = JsonReader.Text(item, "method");
            attempt.Outcome = JsonReader.Text(item, "outcome");
            attempt.DurationMs = JsonReader.Number(item, "durationMs", 0);
            attempt.ErrorCode = JsonReader.Text(item, "errorCode");
            attempt.ErrorMessage = JsonReader.Text(item, "errorMessage");
            attempt.Effect = JsonReader.Text(item, "effect");
            return attempt;
        }
    }

    public sealed class ReplayOutcome
    {
        public DateTimeOffset At;
        public string State;
        public string Reason;
        public string ResolvedBy;
        public int MatchCount;
        public int DurationMs;
        public List<RouteAttempt> Attempts = new List<RouteAttempt>();

        public string AttemptLine
        {
            get
            {
                if (Attempts.Count == 0) return "(no route was attempted)";
                System.Text.StringBuilder text = new System.Text.StringBuilder();
                for (int index = 0; index < Attempts.Count; index++)
                {
                    if (index != 0) text.Append(" -> ");
                    text.Append(Attempts[index].Line);
                }
                return text.ToString();
            }
        }

        public JsonObject ToJson()
        {
            List<object> attempts = new List<object>();
            for (int index = 0; index < Attempts.Count; index++) attempts.Add(Attempts[index].ToJson());
            return new JsonObject()
                .Add("at", At)
                .Add("state", State)
                .Add("reason", Reason)
                .Add("resolvedBy", ResolvedBy)
                .Add("matchCount", MatchCount)
                .Add("durationMs", DurationMs)
                .Add("attempts", attempts.ToArray());
        }

        public static ReplayOutcome FromJson(Dictionary<string, object> item)
        {
            if (item == null) return null;
            ReplayOutcome outcome = new ReplayOutcome();
            outcome.State = JsonReader.Text(item, "state");
            outcome.Reason = JsonReader.Text(item, "reason");
            outcome.ResolvedBy = JsonReader.Text(item, "resolvedBy");
            outcome.MatchCount = JsonReader.Number(item, "matchCount", 0);
            outcome.DurationMs = JsonReader.Number(item, "durationMs", 0);
            object[] attempts = JsonReader.Items(item, "attempts");
            if (attempts != null)
            {
                for (int index = 0; index < attempts.Length; index++)
                {
                    Dictionary<string, object> attempt = attempts[index] as Dictionary<string, object>;
                    if (attempt != null) outcome.Attempts.Add(RouteAttempt.FromJson(attempt));
                }
            }
            return outcome;
        }
    }

    // One thing the operator did, described by meaning rather than by the raw
    // pointer or key traffic that produced it.
    public sealed class StepRecord
    {
        public const string KindClick = "click";
        public const string KindKeyChord = "keyChord";
        public const string KindTextInput = "textInput";
        public const string KindSecretInput = "secretInput";
        public const string KindAppSwitch = "appSwitch";

        public int Index;
        public DateTimeOffset At;
        public int OffsetMs;
        public string Kind;

        public string AppKey;
        public string AppName;
        public int ProcessId;
        public long Hwnd;
        public long TopHwnd;
        public string WindowTitle;
        public string WindowClass;

        public string ScreenBefore;
        public string ScreenAfter;

        public string ElementLabel;
        public string ControlType;
        public string LocalizedControlType;
        public string Role;
        public string Name;
        public string AutomationId;
        public string ClassName;
        public string RuntimeId;
        public int CtrlId;
        public string TreePath;
        public RectValue Rect;
        public PointValue Point;
        public List<string> Sources = new List<string>();
        public string Confidence = "none";
        public List<ElementLocator> Locators = new List<ElementLocator>();

        public string KeyChord;
        public string ValueKind = "none";
        public int ValueLength = -1;
        public string Value;
        public string MaskRule;

        public string WindowTitleAfter;
        public string EffectSummary;
        public List<string> Diagnostics = new List<string>();
        public List<string> Unavailable = new List<string>();
        public ReplayOutcome LastReplay;

        public string StepId
        {
            get { return "A" + Index.ToString(CultureInfo.InvariantCulture); }
        }

        public bool ChangesTarget
        {
            get { return Kind != KindAppSwitch; }
        }

        public string Headline
        {
            get
            {
                if (Kind == KindAppSwitch) return "switch to " + (AppName == null ? "?" : AppName);
                if (Kind == KindKeyChord) return "key " + (KeyChord == null ? "?" : KeyChord);
                if (Kind == KindTextInput) return "type into " + Describe();
                if (Kind == KindSecretInput) return "secret entry into " + Describe();
                return "click " + Describe();
            }
        }

        private string Describe()
        {
            if (!String.IsNullOrEmpty(ElementLabel)) return ElementLabel;
            if (!String.IsNullOrEmpty(Name)) return Name;
            if (!String.IsNullOrEmpty(ControlType)) return ControlType;
            if (!String.IsNullOrEmpty(ClassName)) return ClassName;
            return "(unidentified element)";
        }

        public JsonObject ToJson()
        {
            List<object> locators = new List<object>();
            for (int index = 0; index < Locators.Count; index++) locators.Add(Locators[index].ToJson());
            List<object> sources = new List<object>();
            for (int index = 0; index < Sources.Count; index++) sources.Add(Sources[index]);
            return new JsonObject()
                .Add("kind", "step")
                .Add("stepId", StepId)
                .Add("index", Index)
                .Add("at", At)
                .Add("offsetMs", OffsetMs)
                .Add("action", Kind)
                .Add("appKey", AppKey)
                .Add("appName", AppName)
                .Add("processId", ProcessId)
                .Add("hwnd", Hwnd)
                .Add("topHwnd", TopHwnd)
                .Add("windowTitle", WindowTitle)
                .Add("windowClass", WindowClass)
                .Add("screenBefore", ScreenBefore)
                .Add("screenAfter", ScreenAfter)
                .Add("elementLabel", ElementLabel)
                .Add("controlType", ControlType)
                .Add("localizedControlType", LocalizedControlType)
                .Add("role", Role)
                .Add("name", Name)
                .Add("automationId", AutomationId)
                .Add("className", ClassName)
                .Add("runtimeId", RuntimeId)
                .Add("ctrlId", CtrlId)
                .Add("treePath", TreePath)
                .Add("rect", SessionLogJson.Rect(Rect))
                .Add("point", Point == null ? null : new JsonObject().Add("x", Point.X).Add("y", Point.Y))
                .Add("sources", sources.ToArray())
                .Add("confidence", Confidence)
                .Add("locators", locators.ToArray())
                .Add("keyChord", KeyChord)
                .Add("valueKind", ValueKind)
                .Add("valueLength", ValueLength < 0 ? null : (object)ValueLength)
                .Add("value", Value)
                .Add("maskRule", MaskRule)
                .Add("windowTitleAfter", WindowTitleAfter)
                .Add("effect", EffectSummary)
                .Add("diagnostics", Diagnostics.ToArray())
                .Add("unavailable", Unavailable.ToArray())
                .Add("replay", LastReplay == null ? null : LastReplay.ToJson());
        }

        public static StepRecord FromJson(Dictionary<string, object> item)
        {
            StepRecord step = new StepRecord();
            step.Index = JsonReader.Number(item, "index", 0);
            step.OffsetMs = JsonReader.Number(item, "offsetMs", 0);
            step.Kind = JsonReader.Text(item, "action");
            step.AppKey = JsonReader.Text(item, "appKey");
            step.AppName = JsonReader.Text(item, "appName");
            step.ProcessId = JsonReader.Number(item, "processId", 0);
            step.Hwnd = JsonReader.Number64(item, "hwnd", 0);
            step.TopHwnd = JsonReader.Number64(item, "topHwnd", 0);
            step.WindowTitle = JsonReader.Text(item, "windowTitle");
            step.WindowClass = JsonReader.Text(item, "windowClass");
            step.ScreenBefore = JsonReader.Text(item, "screenBefore");
            step.ScreenAfter = JsonReader.Text(item, "screenAfter");
            step.ElementLabel = JsonReader.Text(item, "elementLabel");
            step.ControlType = JsonReader.Text(item, "controlType");
            step.LocalizedControlType = JsonReader.Text(item, "localizedControlType");
            step.Role = JsonReader.Text(item, "role");
            step.Name = JsonReader.Text(item, "name");
            step.AutomationId = JsonReader.Text(item, "automationId");
            step.ClassName = JsonReader.Text(item, "className");
            step.RuntimeId = JsonReader.Text(item, "runtimeId");
            step.CtrlId = JsonReader.Number(item, "ctrlId", 0);
            step.TreePath = JsonReader.Text(item, "treePath");
            step.Confidence = JsonReader.Text(item, "confidence") ?? "none";
            step.KeyChord = JsonReader.Text(item, "keyChord");
            step.ValueKind = JsonReader.Text(item, "valueKind") ?? "none";
            step.ValueLength = JsonReader.Number(item, "valueLength", -1);
            step.Value = JsonReader.Text(item, "value");
            step.MaskRule = JsonReader.Text(item, "maskRule");
            step.WindowTitleAfter = JsonReader.Text(item, "windowTitleAfter");
            step.EffectSummary = JsonReader.Text(item, "effect");
            step.Rect = SessionStore.ReadRect(JsonReader.Child(item, "rect"));
            Dictionary<string, object> point = JsonReader.Child(item, "point");
            if (point != null)
            {
                step.Point = new PointValue();
                step.Point.X = JsonReader.Number(point, "x", 0);
                step.Point.Y = JsonReader.Number(point, "y", 0);
            }
            object[] sources = JsonReader.Items(item, "sources");
            if (sources != null) for (int index = 0; index < sources.Length; index++) step.Sources.Add(Convert.ToString(sources[index], CultureInfo.InvariantCulture));
            object[] diagnostics = JsonReader.Items(item, "diagnostics");
            if (diagnostics != null) for (int index = 0; index < diagnostics.Length; index++) step.Diagnostics.Add(Convert.ToString(diagnostics[index], CultureInfo.InvariantCulture));
            object[] unavailable = JsonReader.Items(item, "unavailable");
            if (unavailable != null) for (int index = 0; index < unavailable.Length; index++) step.Unavailable.Add(Convert.ToString(unavailable[index], CultureInfo.InvariantCulture));
            object[] locators = JsonReader.Items(item, "locators");
            if (locators != null)
            {
                for (int index = 0; index < locators.Length; index++)
                {
                    Dictionary<string, object> locator = locators[index] as Dictionary<string, object>;
                    if (locator != null) step.Locators.Add(ElementLocator.FromJson(locator));
                }
            }
            step.LastReplay = ReplayOutcome.FromJson(JsonReader.Child(item, "replay"));
            return step;
        }
    }

    // The whole of one investigation. It is written to disk as it happens, and
    // it is what every output is generated from.
    public sealed class StudioSession
    {
        public const string KindSnap = "snap";
        public const string KindRecord = "record";

        public string Id;
        public string Kind = KindSnap;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? EndedAt;
        public string Title;
        public string Folder;
        public string ValuePolicy = Privacy.PolicyRecordText;
        public JsonObject Environment;

        public List<AppRef> Apps = new List<AppRef>();
        public ScreenLedger Screens = new ScreenLedger();
        public List<ScanNode> Elements = new List<ScanNode>();
        public List<ScanCoverage> Coverage = new List<ScanCoverage>();
        public List<StepRecord> Steps = new List<StepRecord>();
        public List<string> Limits = new List<string>();
        public List<string> Diagnostics = new List<string>();
        public List<string> Omissions = new List<string>();

        public string ShotsFolder { get { return Folder == null ? null : Path.Combine(Folder, "shots"); } }
        public string OutFolder { get { return Folder == null ? null : Path.Combine(Folder, "out"); } }
        public string AiFolder { get { return OutFolder == null ? null : Path.Combine(OutFolder, "ai"); } }
        public string ReportPath { get { return OutFolder == null ? null : Path.Combine(OutFolder, "report.html"); } }
        public string SessionMdPath { get { return AiFolder == null ? null : Path.Combine(AiFolder, "session.md"); } }
        public string ScreensPdfPath { get { return AiFolder == null ? null : Path.Combine(AiFolder, "screens.pdf"); } }

        public AppRef AppFor(int processId)
        {
            for (int index = 0; index < Apps.Count; index++) if (Apps[index].ProcessId == processId) return Apps[index];
            return null;
        }

        public AppRef Register(int processId)
        {
            AppRef existing = AppFor(processId);
            if (existing != null) return existing;
            AppRef app = new AppRef();
            app.ProcessId = processId;
            app.Key = "P" + (Apps.Count + 1).ToString(CultureInfo.InvariantCulture);
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    app.ProcessName = process.ProcessName;
                    try
                    {
                        app.ExecutablePath = process.MainModule.FileName;
                    }
                    catch (Exception exception)
                    {
                        // A 32/64 bit mismatch or a protected process refuses the
                        // module list. The name is still known, so the row stays
                        // and says what is missing rather than disappearing.
                        app.PathProblem = exception.GetType().Name + ": " + exception.Message;
                    }
                }
            }
            catch (Exception exception)
            {
                app.PathProblem = exception.GetType().Name + ": " + exception.Message;
            }
            Apps.Add(app);
            return app;
        }

        public void AddLimit(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            for (int index = 0; index < Limits.Count; index++) if (Limits[index] == text) return;
            Limits.Add(text);
        }

        public void AddDiagnostic(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            Diagnostics.Add(text);
        }

        public ScanNode ElementByComponentId(string componentId)
        {
            if (String.IsNullOrEmpty(componentId)) return null;
            for (int index = 0; index < Elements.Count; index++)
            {
                if (String.Equals("E" + Elements[index].NodeId.ToString(CultureInfo.InvariantCulture), componentId, StringComparison.OrdinalIgnoreCase)) return Elements[index];
            }
            return null;
        }

        public JsonObject MetaJson()
        {
            List<object> apps = new List<object>();
            for (int index = 0; index < Apps.Count; index++) apps.Add(Apps[index].ToJson());
            return new JsonObject()
                .Add("kind", "sessionMeta")
                .Add("schema", SessionStore.Schema)
                .Add("id", Id)
                .Add("sessionKind", Kind)
                .Add("startedAt", StartedAt)
                .Add("endedAt", EndedAt.HasValue ? (object)EndedAt.Value : null)
                .Add("title", Title)
                .Add("valuePolicy", ValuePolicy)
                .Add("apps", apps.ToArray())
                .Add("screenCount", Screens.Screens.Count)
                .Add("elementCount", Elements.Count)
                .Add("stepCount", Steps.Count)
                .Add("limits", Limits.ToArray())
                .Add("omissions", Omissions.ToArray());
        }
    }

    // Where sessions live and how they are found again. There is no explicit
    // save anywhere: each record is appended as it happens and the index is
    // rewritten after every change, so a forced exit keeps whatever was written.
    public static class SessionStore
    {
        public const string Schema = "app-studio/session/2";

        public static string Root(string baseDir)
        {
            return Path.Combine(baseDir, "runtime", "sessions");
        }

        public static StudioSession Create(string baseDir, string kind, string title)
        {
            StudioSession session = new StudioSession();
            // The listing is ordered by folder name, so the name has to order by
            // time. Two sessions started inside the same second used to fall back
            // to the order of their random tail, which is no order at all.
            session.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            session.Kind = kind;
            session.StartedAt = DateTimeOffset.Now;
            session.Title = title;
            session.Folder = Path.Combine(Root(baseDir), session.Id);
            Directory.CreateDirectory(session.Folder);
            return session;
        }

        public static void WriteMeta(StudioSession session)
        {
            if (session == null || session.Folder == null) return;
            JsonWriter.WriteFile(Path.Combine(session.Folder, "meta.json"), session.MetaJson());
        }

        public static string[] List(string baseDir)
        {
            string root = Root(baseDir);
            if (!Directory.Exists(root)) return new string[0];
            string[] folders = Directory.GetDirectories(root);
            // Newest first. The name carries the time down to the millisecond, so
            // ordering by name is ordering by when the session was started.
            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(folders);
            return folders;
        }

        // Reads back a session from its folder. Everything unreadable is turned
        // into a stated problem on the session rather than an empty list.
        public static StudioSession Load(string folder)
        {
            if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            StudioSession session = new StudioSession();
            session.Folder = folder;
            session.Id = Path.GetFileName(folder);
            Dictionary<string, object> meta = ReadJsonFile(Path.Combine(folder, "meta.json"));
            if (meta == null)
            {
                session.AddLimit("meta.json could not be read for this session.");
                session.Title = session.Id;
                return session;
            }
            session.Kind = JsonReader.Text(meta, "sessionKind") ?? StudioSession.KindSnap;
            session.Title = JsonReader.Text(meta, "title");
            session.ValuePolicy = JsonReader.Text(meta, "valuePolicy") ?? Privacy.PolicyRecordText;
            DateTimeOffset started;
            if (DateTimeOffset.TryParse(JsonReader.Text(meta, "startedAt"), out started)) session.StartedAt = started;
            DateTimeOffset ended;
            if (DateTimeOffset.TryParse(JsonReader.Text(meta, "endedAt"), out ended)) session.EndedAt = ended;
            object[] apps = JsonReader.Items(meta, "apps");
            if (apps != null)
            {
                for (int index = 0; index < apps.Length; index++)
                {
                    Dictionary<string, object> item = apps[index] as Dictionary<string, object>;
                    if (item == null) continue;
                    AppRef app = new AppRef();
                    app.Key = JsonReader.Text(item, "key");
                    app.ProcessId = JsonReader.Number(item, "processId", 0);
                    app.ProcessName = JsonReader.Text(item, "processName");
                    app.ExecutablePath = JsonReader.Text(item, "executablePath");
                    app.PathProblem = JsonReader.Text(item, "pathProblem");
                    session.Apps.Add(app);
                }
            }
            object[] limits = JsonReader.Items(meta, "limits");
            if (limits != null) for (int index = 0; index < limits.Length; index++) session.AddLimit(Convert.ToString(limits[index], CultureInfo.InvariantCulture));
            object[] omissions = JsonReader.Items(meta, "omissions");
            if (omissions != null) for (int index = 0; index < omissions.Length; index++) session.Omissions.Add(Convert.ToString(omissions[index], CultureInfo.InvariantCulture));

            LoadScreens(session);
            LoadElements(session);
            LoadSteps(session);
            return session;
        }

        private static void LoadScreens(StudioSession session)
        {
            string path = Path.Combine(session.Folder, "screens.jsonl");
            string[] lines = SessionLog.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                Dictionary<string, object> item = JsonReader.ReadObject(lines[index]);
                if (item == null) continue;
                ScreenRecord screen = new ScreenRecord();
                screen.ScanId = JsonReader.Text(item, "scanId");
                screen.ScreenId = JsonReader.Text(item, "screenId");
                screen.Hwnd = JsonReader.Number64(item, "hwnd", 0);
                screen.Title = JsonReader.Text(item, "title");
                screen.ClassName = JsonReader.Text(item, "className");
                screen.NodeCount = JsonReader.Number(item, "nodeCount", 0);
                screen.ShotFile = JsonReader.Text(item, "shotFile");
                screen.Sha256 = JsonReader.Text(item, "sha256");
                screen.CaptureMethod = JsonReader.Text(item, "captureMethod");
                screen.ShotProblem = JsonReader.Text(item, "shotProblem");
                screen.Note = JsonReader.Text(item, "note");
                screen.PdfPage = JsonReader.Number(item, "pdfPage", 0);
                screen.Rect = ReadRect(JsonReader.Child(item, "rect"));
                object[] components = JsonReader.Items(item, "componentIds");
                if (components != null)
                {
                    for (int part = 0; part < components.Length; part++) screen.ComponentIds.Add(Convert.ToString(components[part], CultureInfo.InvariantCulture));
                }
                session.Screens.Screens.Add(screen);
            }
        }

        private static void LoadElements(StudioSession session)
        {
            string path = Path.Combine(session.Folder, "elements.jsonl");
            string[] lines = SessionLog.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                Dictionary<string, object> item = JsonReader.ReadObject(lines[index]);
                if (item == null) continue;
                ScanNode node = new ScanNode();
                node.NodeId = JsonReader.Number(item, "nodeId", index);
                node.ParentId = JsonReader.Number(item, "parentId", -1);
                node.Depth = JsonReader.Number(item, "depth", 0);
                node.ScreenId = JsonReader.Text(item, "screenId");
                node.Hwnd = JsonReader.Number64(item, "hwnd", 0);
                node.TopHwnd = JsonReader.Number64(item, "topHwnd", 0);
                node.ProcessId = JsonReader.Number(item, "processId", 0);
                node.Name = JsonReader.Text(item, "name");
                node.AutomationId = JsonReader.Text(item, "automationId");
                node.ControlType = JsonReader.Text(item, "controlType");
                node.LocalizedControlType = JsonReader.Text(item, "localizedControlType");
                node.ClassName = JsonReader.Text(item, "className");
                node.RealClassName = JsonReader.Text(item, "realClassName");
                node.FrameworkId = JsonReader.Text(item, "frameworkId");
                node.Role = JsonReader.Text(item, "role");
                node.RuntimeId = JsonReader.Text(item, "runtimeId");
                node.CtrlId = JsonReader.Number(item, "ctrlId", 0);
                node.Style = JsonReader.Number64(item, "style", 0);
                node.ExStyle = JsonReader.Number64(item, "exStyle", 0);
                node.Path = JsonReader.Text(item, "path");
                node.ValueKind = JsonReader.Text(item, "valueKind");
                node.ValueLength = JsonReader.Number(item, "valueLength", -1);
                node.Rect = ReadRect(JsonReader.Child(item, "rect"));
                node.Visible = ReadFlag(item, "visible");
                node.Enabled = ReadFlag(item, "enabled");
                node.Offscreen = ReadFlag(item, "offscreen");
                node.KeyboardFocusable = ReadFlag(item, "keyboardFocusable");
                node.IsPassword = ReadFlag(item, "isPassword");
                node.Decoration = JsonReader.Flag(item, "decoration", false);
                object[] sources = JsonReader.Items(item, "sources");
                if (sources != null) for (int part = 0; part < sources.Length; part++) node.AddSource(Convert.ToString(sources[part], CultureInfo.InvariantCulture));
                object[] patterns = JsonReader.Items(item, "patterns");
                if (patterns != null)
                {
                    string[] values = new string[patterns.Length];
                    for (int part = 0; part < patterns.Length; part++) values[part] = Convert.ToString(patterns[part], CultureInfo.InvariantCulture);
                    node.Patterns = values;
                }
                object[] notes = JsonReader.Items(item, "notes");
                if (notes != null) for (int part = 0; part < notes.Length; part++) node.AddNote(Convert.ToString(notes[part], CultureInfo.InvariantCulture));
                session.Elements.Add(node);
            }
        }

        // A step is written the moment it happens, so a forced exit keeps it,
        // and written again once its consequence has been observed. Both lines
        // carry the same step id and the later one is the complete record, so
        // the reader keeps the last of each and the order of first appearance.
        private static void LoadSteps(StudioSession session)
        {
            string path = Path.Combine(session.Folder, "steps.jsonl");
            string[] lines = SessionLog.ReadAllLines(path);
            List<string> order = new List<string>();
            Dictionary<string, StepRecord> byId = new Dictionary<string, StepRecord>(StringComparer.Ordinal);
            for (int index = 0; index < lines.Length; index++)
            {
                Dictionary<string, object> item = JsonReader.ReadObject(lines[index]);
                if (item == null) continue;
                StepRecord step = StepRecord.FromJson(item);
                string id = JsonReader.Text(item, "stepId");
                if (String.IsNullOrEmpty(id)) id = "line-" + index.ToString(CultureInfo.InvariantCulture);
                if (!byId.ContainsKey(id)) order.Add(id);
                byId[id] = step;
            }
            for (int index = 0; index < order.Count; index++) session.Steps.Add(byId[order[index]]);
        }

        public static RectValue ReadRect(Dictionary<string, object> item)
        {
            if (item == null) return null;
            RectValue rect = new RectValue();
            rect.X = JsonReader.Number(item, "x", 0);
            rect.Y = JsonReader.Number(item, "y", 0);
            rect.Width = JsonReader.Number(item, "width", 0);
            rect.Height = JsonReader.Number(item, "height", 0);
            return rect;
        }

        private static bool? ReadFlag(Dictionary<string, object> item, string key)
        {
            if (!JsonReader.Has(item, key)) return null;
            object value;
            item.TryGetValue(key, out value);
            if (value == null) return null;
            return JsonReader.Flag(item, key, false);
        }

        // Appends one record and reports whether it reached the disk. A caller
        // that gets false shows the reason; nothing counts a record it could
        // not persist.
        public static bool Append(StudioSession session, string stream, JsonObject record)
        {
            if (session == null || session.Folder == null || record == null) return false;
            try
            {
                string path = Path.Combine(session.Folder, stream + ".jsonl");
                using (FileStream file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] payload = new System.Text.UTF8Encoding(false).GetBytes(JsonWriter.WriteCompact(record) + "\n");
                    file.Write(payload, 0, payload.Length);
                    file.Flush(true);
                }
                return true;
            }
            catch (Exception exception)
            {
                session.AddDiagnostic("STORE-WRITE " + stream + ": " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        public static Dictionary<string, object> ReadJsonFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string[] lines = SessionLog.ReadAllLines(path);
                System.Text.StringBuilder text = new System.Text.StringBuilder();
                for (int index = 0; index < lines.Length; index++) text.Append(lines[index]);
                return JsonReader.ReadObject(text.ToString());
            }
            catch
            {
                return null;
            }
        }
    }
}
