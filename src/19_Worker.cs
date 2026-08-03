namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Web.Script.Serialization;
    using System.Windows.Threading;

    internal sealed class AcquisitionWorkerProcess : IDisposable
    {
        private readonly Process process;
        private readonly JavaScriptSerializer serializer;
        private readonly object ioSync = new object();
        private readonly StreamWriter input;
        private string lastError;

        private AcquisitionWorkerProcess(Process value)
        {
            process = value;
            // Process.StandardInput is built with Console.InputEncoding and sets
            // AutoFlush, which writes that encoding's preamble into the pipe as
            // soon as the property is touched. Started from a code page 65001
            // console that preamble is a UTF-8 BOM, and the worker used to read
            // it in front of the first request and die on it. The worker now
            // strips leading marks, and every request is written through this
            // preamble-free UTF-8 writer so the protocol stays byte exact and
            // non-ASCII operation values survive whatever console is in use.
            input = new StreamWriter(value.StandardInput.BaseStream, new UTF8Encoding(false));
            input.AutoFlush = false;
            serializer = new JavaScriptSerializer();
            // A scan chunk carries hundreds of elements; the 2 MB default would
            // turn a large window into a serialization failure.
            serializer.MaxJsonLength = 64 * 1024 * 1024;
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (!String.IsNullOrEmpty(args.Data))
                {
                    lastError = args.Data;
                }
            };
            process.BeginErrorReadLine();
        }

        internal int ProcessId
        {
            get { return process.HasExited ? 0 : process.Id; }
        }

        internal long StartTimeUtcTicks { get; private set; }
        internal bool WarmupPerformed { get; private set; }
        internal long WarmupDurationMs { get; private set; }

        internal static AcquisitionWorkerProcess Start(string baseDir, int startupTimeoutMs)
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string workerScript = Path.Combine(baseDir, "app-studio-worker.ps1");
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = powershell;
            start.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File " + Quote(workerScript) + " -BaseDir " + Quote(baseDir);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            Process process = Process.Start(start);
            if (process == null)
            {
                throw new InvalidOperationException("The acquisition worker did not start.");
            }
            AcquisitionWorkerProcess worker = new AcquisitionWorkerProcess(process);
            worker.StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            Task<string> readyRead = process.StandardOutput.ReadLineAsync();
            if (!readyRead.Wait(startupTimeoutMs))
            {
                worker.Kill();
                worker.Dispose();
                throw new TimeoutException("The acquisition worker did not become ready within " + startupTimeoutMs + " ms.");
            }
            string line = readyRead.Result;
            if (String.IsNullOrEmpty(line))
            {
                string error = worker.lastError;
                worker.Kill();
                worker.Dispose();
                throw new InvalidOperationException("The acquisition worker exited before ready. " + error);
            }
            Dictionary<string, object> ready = worker.serializer.DeserializeObject(line) as Dictionary<string, object>;
            if (ready == null || Text(ready, "type") != "ready" || !Bool(ready, "warmupPerformed"))
            {
                worker.Kill();
                worker.Dispose();
                throw new InvalidOperationException("The acquisition worker returned an invalid ready message: " + line);
            }
            worker.WarmupPerformed = true;
            worker.WarmupDurationMs = Long(ready, "warmupDurationMs");
            return worker;
        }

        internal bool TryProbe(int x, int y, int timeoutMs, bool deep, out WorkerSnapshot info)
        {
            lock (ioSync)
            {
                string requestId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["type"] = "probe";
                request["id"] = requestId;
                request["x"] = x;
                request["y"] = y;
                request["deep"] = deep;
                input.WriteLine(serializer.Serialize(request));
                input.Flush();
                Task<string> read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(timeoutMs))
                {
                    info = null;
                    return false;
                }
                string line = read.Result;
                if (String.IsNullOrEmpty(line))
                {
                    info = Unavailable("UIA-FAIL", "Acquisition worker exited before returning a result. " + lastError);
                    return true;
                }
                Dictionary<string, object> result = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (result == null || Text(result, "type") != "result" || Text(result, "id") != requestId)
                {
                    info = Unavailable("UIA-FAIL", "Acquisition worker returned an invalid JSON Lines response.");
                    return true;
                }
                WorkerSnapshot parsed = new WorkerSnapshot();
                parsed.Uia = ParseInfo(result);
                parsed.Msaa = ParseMsaa(Object(result, "msaa"));
                info = parsed;
                return true;
            }
        }

        // A scan answers with any number of chunk lines and then one done line.
        // Returns false when the worker stopped answering, in which case the
        // caller terminates it and keeps the chunks that already arrived.
        internal bool TryScan(long hwnd, ScanLimits limits, RectValue area, int targetProcessId, int excludeProcessId, int timeoutMs, Dictionary<string, List<ScanNode>> byProvider, List<ScanCoverage> coverage, out string failure)
        {
            failure = null;
            lock (ioSync)
            {
                string requestId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["type"] = "scan";
                request["id"] = requestId;
                request["hwnd"] = hwnd;
                request["targetProcessId"] = targetProcessId;
                request["excludeProcessId"] = excludeProcessId;
                request["maxNodes"] = limits.MaxNodes;
                request["maxDepth"] = limits.MaxDepth;
                request["uiaBudgetMs"] = limits.UiaBudgetMs;
                request["msaaBudgetMs"] = limits.MsaaBudgetMs;
                request["hitTestBudgetMs"] = limits.HitTestBudgetMs;
                request["includeOffscreen"] = limits.IncludeOffscreen;
                request["hitTest"] = limits.HitTest;
                if (area != null)
                {
                    Dictionary<string, object> rect = new Dictionary<string, object>();
                    rect["x"] = area.X;
                    rect["y"] = area.Y;
                    rect["width"] = area.Width;
                    rect["height"] = area.Height;
                    request["rect"] = rect;
                }
                try
                {
                    input.WriteLine(serializer.Serialize(request));
                    input.Flush();
                }
                catch (Exception exception)
                {
                    failure = "The scan request could not be sent: " + exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
                Stopwatch watch = Stopwatch.StartNew();
                while (true)
                {
                    int remaining = timeoutMs - (int)watch.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        failure = "The scan worker exceeded its " + timeoutMs + " ms budget and was terminated.";
                        return false;
                    }
                    Task<string> read = process.StandardOutput.ReadLineAsync();
                    if (!read.Wait(remaining))
                    {
                        failure = "The scan worker stopped answering after " + watch.ElapsedMilliseconds + " ms and was terminated.";
                        return false;
                    }
                    string line = read.Result;
                    if (String.IsNullOrEmpty(line))
                    {
                        failure = "The scan worker exited before finishing. " + lastError;
                        return false;
                    }
                    Dictionary<string, object> message = serializer.DeserializeObject(line) as Dictionary<string, object>;
                    if (message == null || Text(message, "id") != requestId) continue;
                    string type = Text(message, "type");
                    if (type == "scanChunk")
                    {
                        string provider = Text(message, "provider");
                        List<ScanNode> target;
                        if (!byProvider.TryGetValue(provider ?? "unknown", out target))
                        {
                            target = new List<ScanNode>();
                            byProvider[provider ?? "unknown"] = target;
                        }
                        object nodes;
                        if (message.TryGetValue("nodes", out nodes))
                        {
                            object[] items = nodes as object[];
                            if (items != null)
                            {
                                for (int index = 0; index < items.Length; index++)
                                {
                                    Dictionary<string, object> item = items[index] as Dictionary<string, object>;
                                    if (item != null) target.Add(ScanNodeWire.FromWire(item));
                                }
                            }
                        }
                        continue;
                    }
                    if (type == "scanDone")
                    {
                        object reported;
                        if (message.TryGetValue("coverage", out reported))
                        {
                            object[] items = reported as object[];
                            if (items != null)
                            {
                                for (int index = 0; index < items.Length; index++)
                                {
                                    Dictionary<string, object> item = items[index] as Dictionary<string, object>;
                                    if (item != null) coverage.Add(ScanService.CoverageFromWire(item));
                                }
                            }
                        }
                        return true;
                    }
                }
            }
        }

        internal bool TryOperation(int x, int y, string kind, string value, int timeoutMs, out UiaActionResult action)
        {
            lock (ioSync)
            {
                string requestId = Guid.NewGuid().ToString("N");
                Dictionary<string, object> request = new Dictionary<string, object>();
                request["type"] = "operation";
                request["id"] = requestId;
                request["x"] = x;
                request["y"] = y;
                request["kind"] = kind;
                request["value"] = value;
                input.WriteLine(serializer.Serialize(request));
                input.Flush();
                Task<string> read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(timeoutMs))
                {
                    action = null;
                    return false;
                }
                string line = read.Result;
                if (String.IsNullOrEmpty(line))
                {
                    action = FailedAction("UIA-FAIL", "Acquisition worker exited before returning an operation result. " + lastError);
                    return true;
                }
                Dictionary<string, object> result = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (result == null || Text(result, "type") != "operationResult" || Text(result, "id") != requestId)
                {
                    action = FailedAction("UIA-FAIL", "Acquisition worker returned an invalid operation response.");
                    return true;
                }
                action = ParseAction(result);
                return true;
            }
        }

        internal void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    input.WriteLine("{\"type\":\"exit\"}");
                    input.Flush();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                }
            }
            catch
            {
                Kill();
            }
            process.Dispose();
        }

        private static UiaInfo ParseInfo(Dictionary<string, object> result)
        {
            UiaInfo info = new UiaInfo();
            string state = Text(result, "state");
            string code = Text(result, "code");
            string message = Text(result, "message");
            if (state == "ok")
            {
                info.Status = ProbeStatus.Ok();
            }
            else if (state == "partial")
            {
                info.Status = ProbeStatus.Ok();
                info.Status.AddPartial(String.IsNullOrEmpty(code) ? "UIA-FAIL" : code, message);
            }
            else
            {
                info.Status = ProbeStatus.Unavailable(String.IsNullOrEmpty(code) ? "UIA-FAIL" : code, message);
            }
            info.Name = Text(result, "name");
            info.AutomationId = Text(result, "automationId");
            info.ControlType = Text(result, "controlType");
            info.LocalizedControlType = Text(result, "localizedControlType");
            info.ClassName = Text(result, "className");
            info.FrameworkId = Text(result, "frameworkId");
            info.IsEnabled = Bool(result, "isEnabled");
            info.IsOffscreen = Bool(result, "isOffscreen");
            info.IsKeyboardFocusable = Bool(result, "isKeyboardFocusable");
            info.IsPassword = Bool(result, "isPassword");
            info.HelpText = Text(result, "helpText");
            info.AcceleratorKey = Text(result, "acceleratorKey");
            info.AccessKey = Text(result, "accessKey");
            info.NativeWindowHandle = Int(result, "nativeWindowHandle");
            info.LiveValue = Text(result, "liveValue");
            info.RawRefined = Bool(result, "rawRefined");
            info.RuntimeId = IntArray(result, "runtimeId");
            info.SupportedPatterns = StringArray(result, "supportedPatterns");
            info.TreePath = NodeArray(result, "treePath");
            info.Children = NodeArray(result, "children");
            Dictionary<string, object> rectangle = Object(result, "boundingRect");
            if (rectangle != null)
            {
                info.BoundingRect = new RectValue();
                info.BoundingRect.X = Int(rectangle, "x");
                info.BoundingRect.Y = Int(rectangle, "y");
                info.BoundingRect.Width = Int(rectangle, "width");
                info.BoundingRect.Height = Int(rectangle, "height");
            }
            return info;
        }

        private static WorkerSnapshot Unavailable(string code, string message)
        {
            WorkerSnapshot snapshot = new WorkerSnapshot();
            snapshot.Uia = new UiaInfo();
            snapshot.Uia.Status = ProbeStatus.Unavailable(code, message);
            snapshot.Msaa = new MsaaInfo();
            snapshot.Msaa.Status = ProbeStatus.Unavailable(code, message);
            return snapshot;
        }

        private static MsaaInfo ParseMsaa(Dictionary<string, object> value)
        {
            if (value == null)
            {
                MsaaInfo missing = new MsaaInfo();
                missing.Status = ProbeStatus.Unavailable("MSAA-INIT", "The acquisition worker returned no MSAA block.");
                return missing;
            }
            MsaaInfo info = new MsaaInfo();
            string state = Text(value, "state");
            string code = Text(value, "code");
            string message = Text(value, "message");
            if (state == "ok")
            {
                info.Status = ProbeStatus.Ok();
            }
            else if (state == "partial")
            {
                info.Status = ProbeStatus.Ok();
                info.Status.AddPartial(String.IsNullOrEmpty(code) ? "MSAA-FAIL" : code, message);
            }
            else
            {
                info.Status = ProbeStatus.Unavailable(String.IsNullOrEmpty(code) ? "MSAA-FAIL" : code, message);
            }
            info.Role = Text(value, "role");
            info.Name = Text(value, "name");
            info.Value = Text(value, "value");
            info.State = Long(value, "msaaState");
            info.StateText = Text(value, "stateText");
            info.ChildId = Int(value, "childId");
            info.Hwnd = Long(value, "hwnd");
            Dictionary<string, object> rectangle = Object(value, "rect");
            if (rectangle != null)
            {
                info.Rect = new RectValue();
                info.Rect.X = Int(rectangle, "x");
                info.Rect.Y = Int(rectangle, "y");
                info.Rect.Width = Int(rectangle, "width");
                info.Rect.Height = Int(rectangle, "height");
            }
            return info;
        }

        private static UiaActionResult ParseAction(Dictionary<string, object> result)
        {
            UiaActionResult action = new UiaActionResult();
            action.Outcome = Text(result, "outcome");
            action.Method = Text(result, "method");
            action.ErrorCode = Text(result, "errorCode");
            action.ErrorHresult = Int(result, "errorHresult");
            action.ErrorMessage = Text(result, "errorMessage");
            action.BeforeValue = Text(result, "beforeValue");
            action.AfterValue = Text(result, "afterValue");
            action.BeforeState = Text(result, "beforeState");
            action.AfterState = Text(result, "afterState");
            action.BeforeFocused = Bool(result, "beforeFocused");
            action.AfterFocused = Bool(result, "afterFocused");
            action.BeforeName = Text(result, "beforeName");
            action.AfterName = Text(result, "afterName");
            return action;
        }

        private static UiaActionResult FailedAction(string code, string message)
        {
            UiaActionResult action = new UiaActionResult();
            action.Outcome = "failed";
            action.Method = "uia.worker";
            action.ErrorCode = code;
            action.ErrorMessage = message;
            return action;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        internal static string Text(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : null;
        }

        internal static bool Bool(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null && Convert.ToBoolean(item, CultureInfo.InvariantCulture);
        }

        internal static int Int(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToInt32(item, CultureInfo.InvariantCulture) : 0;
        }

        internal static long Long(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToInt64(item, CultureInfo.InvariantCulture) : 0;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) ? item as Dictionary<string, object> : null;
        }

        private static int[] IntArray(Dictionary<string, object> value, string key)
        {
            object item;
            object[] source = value.TryGetValue(key, out item) ? item as object[] : null;
            if (source == null) return null;
            int[] result = new int[source.Length];
            for (int index = 0; index < source.Length; index++) result[index] = Convert.ToInt32(source[index], CultureInfo.InvariantCulture);
            return result;
        }

        private static string[] StringArray(Dictionary<string, object> value, string key)
        {
            object item;
            object[] source = value.TryGetValue(key, out item) ? item as object[] : null;
            if (source == null) return null;
            string[] result = new string[source.Length];
            for (int index = 0; index < source.Length; index++) result[index] = Convert.ToString(source[index], CultureInfo.InvariantCulture);
            return result;
        }

        private static UiaNode[] NodeArray(Dictionary<string, object> value, string key)
        {
            object item;
            object[] source = value.TryGetValue(key, out item) ? item as object[] : null;
            if (source == null) return null;
            UiaNode[] result = new UiaNode[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                Dictionary<string, object> nodeValue = source[index] as Dictionary<string, object>;
                UiaNode node = new UiaNode();
                if (nodeValue != null)
                {
                    node.ControlType = Text(nodeValue, "controlType");
                    node.Name = Text(nodeValue, "name");
                    node.AutomationId = Text(nodeValue, "automationId");
                    node.IndexAmongSameType = Int(nodeValue, "indexAmongSameType");
                    node.SiblingCount = Int(nodeValue, "siblingCount");
                }
                result[index] = node;
            }
            return result;
        }
    }

    public sealed class AcquisitionWorkerManager : IDisposable
    {
        private readonly object sync = new object();
        private readonly string baseDir;
        private readonly List<WorkerIdentity> terminated = new List<WorkerIdentity>();
        private AcquisitionWorkerProcess active;
        private AcquisitionWorkerProcess spare;
        private Task<AcquisitionWorkerProcess> pendingSpare;
        private int restartCount;
        private int promotionCount;
        private bool disposed;
        private volatile AcquisitionHealth lastHealth;

        public AcquisitionWorkerManager(string baseDirectory)
        {
            baseDir = Path.GetFullPath(baseDirectory);
            Task<AcquisitionWorkerProcess> first = Task.Factory.StartNew(delegate { return AcquisitionWorkerProcess.Start(baseDir, 15000); });
            Task<AcquisitionWorkerProcess> second = Task.Factory.StartNew(delegate { return AcquisitionWorkerProcess.Start(baseDir, 15000); });
            try
            {
                Task.WaitAll(new Task[] { first, second }, 20000);
                active = first.Result;
                spare = second.Result;
            }
            catch
            {
                if (first.Status == TaskStatus.RanToCompletion) first.Result.Dispose();
                if (second.Status == TaskStatus.RanToCompletion) second.Result.Dispose();
                throw;
            }
        }

        public WorkerSnapshot ProbeAt(int x, int y, int timeoutMs, bool deep)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                AdoptPendingIfReady();
                EnsureActive();
                WorkerSnapshot result;
                if (active.TryProbe(x, y, timeoutMs, deep, out result))
                {
                    AdoptPendingIfReady();
                    return result;
                }
                TerminateActive();
                TryPromoteReadySpare();
                BeginSpareStart();
                WorkerSnapshot timeout = new WorkerSnapshot();
                timeout.Uia = new UiaInfo();
                timeout.Uia.Status = ProbeStatus.Unavailable("UIA-TIMEOUT", "The acquisition worker exceeded the process watchdog and was terminated after " + timeoutMs + " ms.");
                timeout.Msaa = new MsaaInfo();
                timeout.Msaa.Status = ProbeStatus.Unavailable("MSAA-TIMEOUT", "The acquisition worker exceeded the process watchdog and was terminated after " + timeoutMs + " ms.");
                return timeout;
            }
        }

        public UiaActionResult RunOperation(int x, int y, string kind, string value, int timeoutMs)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                AdoptPendingIfReady();
                EnsureActive();
                UiaActionResult result;
                if (active.TryOperation(x, y, kind, value, timeoutMs, out result))
                {
                    AdoptPendingIfReady();
                    return result;
                }
                TerminateActive();
                TryPromoteReadySpare();
                BeginSpareStart();
                UiaActionResult timeout = new UiaActionResult();
                timeout.Outcome = "failed";
                timeout.Method = "uia.worker";
                timeout.ErrorCode = "UIA-TIMEOUT";
                timeout.ErrorMessage = "The operation worker exceeded the process watchdog and was terminated after " + timeoutMs + " ms.";
                return timeout;
            }
        }

        public AcquisitionHealth GetHealth()
        {
            // The manager lock is held for the full duration of a worker probe.
            // Health readers run on the UI thread, so a blocking lock here would
            // freeze the interface for the length of the slowest acquisition.
            if (!System.Threading.Monitor.TryEnter(sync, 0))
            {
                AcquisitionHealth busy = lastHealth;
                if (busy != null) return busy;
                AcquisitionHealth probing = new AcquisitionHealth();
                probing.State = "acquiring";
                return probing;
            }
            try
            {
                AdoptPendingIfReady();
                AcquisitionHealth health = new AcquisitionHealth();
                health.State = disposed ? "stopped" : (pendingSpare == null ? "ready" : "warming-spare");
                health.RestartCount = restartCount;
                health.PromotionCount = promotionCount;
                health.QueueDepth = 0;
                health.ActiveProcessId = active == null ? 0 : active.ProcessId;
                health.SpareProcessId = spare == null ? 0 : spare.ProcessId;
                health.ActiveWarmupPerformed = active != null && active.WarmupPerformed;
                health.SpareWarmupPerformed = spare != null && spare.WarmupPerformed;
                health.OrphanProcessCount = CountOrphans();
                lastHealth = health;
                return health;
            }
            finally
            {
                System.Threading.Monitor.Exit(sync);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (active != null) { active.Dispose(); active = null; }
                if (spare != null) { spare.Dispose(); spare = null; }
                if (pendingSpare != null)
                {
                    try
                    {
                        if (pendingSpare.Wait(15000)) pendingSpare.Result.Dispose();
                    }
                    catch
                    {
                    }
                    pendingSpare = null;
                }
            }
        }

        private void TerminateActive()
        {
            WorkerIdentity identity = new WorkerIdentity();
            identity.ProcessId = active.ProcessId;
            identity.StartTimeUtcTicks = active.StartTimeUtcTicks;
            active.Kill();
            active.Dispose();
            terminated.Add(identity);
            active = null;
            restartCount++;
        }

        // Takes over the spare only when it is already warm, and never waits.
        // A hung target is answered on the watchdog it was given, so the time a
        // replacement still needs to warm up must not be added to that answer.
        // When no warm spare is at hand the manager is deliberately left with no
        // active worker and the next caller pays for the replacement instead.
        private bool TryPromoteReadySpare()
        {
            AdoptPendingIfReady();
            if (spare == null || !spare.WarmupPerformed) return false;
            active = spare;
            spare = null;
            promotionCount++;
            return true;
        }

        // Run before a worker is used. This is the one place that is allowed to
        // wait for a replacement, because here the wait is the cost of the call
        // that needs the worker rather than of the hang that discarded it.
        private void EnsureActive()
        {
            if (active != null) return;
            if (!TryPromoteReadySpare())
            {
                if (pendingSpare != null)
                {
                    if (!pendingSpare.Wait(15000))
                    {
                        throw new TimeoutException("No warmed spare worker became ready after the active worker was terminated.");
                    }
                    // Adopting rather than reading the task drops a spare whose
                    // start failed, so the fresh start below covers that case.
                    AdoptPendingIfReady();
                }
                if (spare == null)
                {
                    spare = AcquisitionWorkerProcess.Start(baseDir, 15000);
                }
                if (!spare.WarmupPerformed)
                {
                    throw new InvalidOperationException("A worker cannot be promoted before UIA warm-up completes.");
                }
                active = spare;
                spare = null;
                promotionCount++;
            }
            // Keep the one-active-one-spare shape the manager is built around.
            BeginSpareStart();
        }

        private void BeginSpareStart()
        {
            if (spare != null || pendingSpare != null || disposed) return;
            pendingSpare = Task.Factory.StartNew(delegate { return AcquisitionWorkerProcess.Start(baseDir, 15000); });
        }

        private void AdoptPendingIfReady()
        {
            if (pendingSpare == null || !pendingSpare.IsCompleted) return;
            if (pendingSpare.IsFaulted)
            {
                pendingSpare = null;
                return;
            }
            spare = pendingSpare.Result;
            pendingSpare = null;
        }

        private DateTime lastOrphanCheck = DateTime.MinValue;
        private int cachedOrphanCount;

        private int CountOrphans()
        {
            if ((DateTime.UtcNow - lastOrphanCheck).TotalMilliseconds < 2000) return cachedOrphanCount;
            lastOrphanCheck = DateTime.UtcNow;
            int count = 0;
            for (int index = terminated.Count - 1; index >= 0; index--)
            {
                try
                {
                    Process process = Process.GetProcessById(terminated[index].ProcessId);
                    if (process.StartTime.ToUniversalTime().Ticks == terminated[index].StartTimeUtcTicks && !process.HasExited)
                    {
                        count++;
                    }
                    else
                    {
                        terminated.RemoveAt(index);
                    }
                    process.Dispose();
                }
                catch
                {
                    terminated.RemoveAt(index);
                }
            }
            cachedOrphanCount = count;
            return count;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("AcquisitionWorkerManager");
        }

        private sealed class WorkerIdentity
        {
            internal int ProcessId;
            internal long StartTimeUtcTicks;
        }
    }

    public static class WorkerServer
    {
        public static void Run()
        {
            // Without process DPI awareness the OS virtualizes the screen
            // coordinates given to AccessibleObjectFromPoint, so MSAA would
            // probe a scaled, wrong point on high-DPI displays.
            DpiAwareness.Enable();
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 64 * 1024 * 1024;
            long warmupMs = UiaProbe.WarmUp();
            Dictionary<string, object> ready = new Dictionary<string, object>();
            ready["type"] = "ready";
            ready["warmupPerformed"] = true;
            ready["warmupDurationMs"] = warmupMs;
            Console.Out.WriteLine(serializer.Serialize(ready));
            Console.Out.Flush();

            string tracePath = Environment.GetEnvironmentVariable("APPSTUDIO_WORKER_TRACE");
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (!String.IsNullOrEmpty(tracePath)) Trace(tracePath, "in len=" + line.Length + " first=U+" + (line.Length == 0 ? "none" : ((int)line[0]).ToString("X4")) + " head=" + Head(line));
                // A byte order mark can still arrive from an older host build or
                // a differently configured console; it is not part of a request.
                line = StripMarks(line);
                // A malformed or empty line must not take the worker down: the
                // host would only see "exited before returning a result".
                if (String.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> request;
                try
                {
                    request = serializer.DeserializeObject(line) as Dictionary<string, object>;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("worker: unreadable request line (" + exception.GetType().Name + "): " + Head(line));
                    continue;
                }
                if (request == null) continue;
                string type = AcquisitionWorkerProcess.Text(request, "type");
                if (type == "exit") return;
                string requestId = AcquisitionWorkerProcess.Text(request, "id");
                if (type == "probe")
                {
                    int x = AcquisitionWorkerProcess.Int(request, "x");
                    int y = AcquisitionWorkerProcess.Int(request, "y");
                    UiaInfo info = UiaProbe.AtPoint(x, y, AcquisitionWorkerProcess.Bool(request, "deep"));
                    MsaaInfo msaa = MsaaProbe.AtPoint(x, y);
                    Dictionary<string, object> reply = BuildResult(requestId, info);
                    reply["msaa"] = BuildMsaaResult(msaa);
                    Console.Out.WriteLine(serializer.Serialize(reply));
                }
                else if (type == "operation")
                {
                    UiaActionResult action = UiaOperation.AtPoint(AcquisitionWorkerProcess.Int(request, "x"), AcquisitionWorkerProcess.Int(request, "y"), AcquisitionWorkerProcess.Text(request, "kind"), AcquisitionWorkerProcess.Text(request, "value"));
                    Console.Out.WriteLine(serializer.Serialize(BuildActionResult(requestId, action)));
                }
                else if (type == "scan")
                {
                    try
                    {
                        ScanService.Handle(serializer, requestId, request);
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine("worker: scan failed: " + exception.ToString());
                        Dictionary<string, object> failure = new Dictionary<string, object>();
                        failure["type"] = "scanDone";
                        failure["id"] = requestId;
                        Dictionary<string, object> reason = new Dictionary<string, object>();
                        reason["code"] = "SCAN-FAIL";
                        reason["message"] = exception.GetType().Name + ": " + exception.Message;
                        Dictionary<string, object> coverage = new Dictionary<string, object>();
                        coverage["provider"] = "uia";
                        coverage["state"] = "unavailable";
                        coverage["nodeCount"] = 0;
                        coverage["durationMs"] = 0;
                        coverage["truncated"] = true;
                        coverage["reasons"] = new object[] { reason };
                        failure["coverage"] = new object[] { coverage };
                        Console.Out.WriteLine(serializer.Serialize(failure));
                        Console.Out.Flush();
                    }
                    continue;
                }
                else
                {
                    continue;
                }
                Console.Out.Flush();
            }
        }

        // Diagnostic aid for the worker protocol, active only when the
        // APPSTUDIO_WORKER_TRACE environment variable names a file.
        private static void Trace(string path, string text)
        {
            try
            {
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        // Byte order and zero width marks are not part of a request line.
        private static string StripMarks(string value)
        {
            if (value == null) return null;
            int start = 0;
            while (start < value.Length && (value[start] == (char)0xFEFF || value[start] == (char)0x200B)) start++;
            return start == 0 ? value : value.Substring(start);
        }

        private static string Head(string value)
        {
            if (value == null) return "(null)";
            return value.Length <= 120 ? value : value.Substring(0, 120) + "...";
        }

        private static Dictionary<string, object> BuildActionResult(string requestId, UiaActionResult action)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["type"] = "operationResult";
            result["id"] = requestId;
            result["outcome"] = action.Outcome;
            result["method"] = action.Method;
            result["errorCode"] = action.ErrorCode;
            result["errorHresult"] = action.ErrorHresult;
            result["errorMessage"] = action.ErrorMessage;
            result["beforeValue"] = action.BeforeValue;
            result["afterValue"] = action.AfterValue;
            result["beforeState"] = action.BeforeState;
            result["afterState"] = action.AfterState;
            result["beforeFocused"] = action.BeforeFocused;
            result["afterFocused"] = action.AfterFocused;
            result["beforeName"] = action.BeforeName;
            result["afterName"] = action.AfterName;
            return result;
        }

        private static Dictionary<string, object> BuildResult(string requestId, UiaInfo info)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["type"] = "result";
            result["id"] = requestId;
            result["state"] = info.Status.State;
            result["code"] = info.Status.Reasons.Count == 0 ? null : info.Status.Reasons[0].Code;
            result["message"] = info.Status.Reasons.Count == 0 ? null : info.Status.Reasons[0].Message;
            result["name"] = info.Name;
            result["automationId"] = info.AutomationId;
            result["controlType"] = info.ControlType;
            result["localizedControlType"] = info.LocalizedControlType;
            result["className"] = info.ClassName;
            result["frameworkId"] = info.FrameworkId;
            result["isEnabled"] = info.IsEnabled;
            result["isOffscreen"] = info.IsOffscreen;
            result["isKeyboardFocusable"] = info.IsKeyboardFocusable;
            result["isPassword"] = info.IsPassword;
            result["helpText"] = info.HelpText;
            result["acceleratorKey"] = info.AcceleratorKey;
            result["accessKey"] = info.AccessKey;
            result["runtimeId"] = info.RuntimeId;
            result["nativeWindowHandle"] = info.NativeWindowHandle;
            result["supportedPatterns"] = info.SupportedPatterns;
            result["treePath"] = Nodes(info.TreePath);
            result["children"] = Nodes(info.Children);
            result["liveValue"] = info.LiveValue;
            result["rawRefined"] = info.RawRefined;
            if (info.BoundingRect != null)
            {
                Dictionary<string, object> rectangle = new Dictionary<string, object>();
                rectangle["x"] = info.BoundingRect.X;
                rectangle["y"] = info.BoundingRect.Y;
                rectangle["width"] = info.BoundingRect.Width;
                rectangle["height"] = info.BoundingRect.Height;
                result["boundingRect"] = rectangle;
            }
            return result;
        }

        private static Dictionary<string, object> BuildMsaaResult(MsaaInfo info)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["state"] = info.Status.State;
            result["code"] = info.Status.Reasons.Count == 0 ? null : info.Status.Reasons[0].Code;
            result["message"] = info.Status.Reasons.Count == 0 ? null : info.Status.Reasons[0].Message;
            result["role"] = info.Role;
            result["name"] = info.Name;
            result["value"] = info.Value;
            result["msaaState"] = info.State;
            result["stateText"] = info.StateText;
            result["childId"] = info.ChildId;
            result["hwnd"] = info.Hwnd;
            if (info.Rect != null)
            {
                Dictionary<string, object> rectangle = new Dictionary<string, object>();
                rectangle["x"] = info.Rect.X;
                rectangle["y"] = info.Rect.Y;
                rectangle["width"] = info.Rect.Width;
                rectangle["height"] = info.Rect.Height;
                result["rect"] = rectangle;
            }
            return result;
        }

        private static object[] Nodes(UiaNode[] nodes)
        {
            if (nodes == null) return null;
            object[] result = new object[nodes.Length];
            for (int index = 0; index < nodes.Length; index++)
            {
                Dictionary<string, object> node = new Dictionary<string, object>();
                node["controlType"] = nodes[index].ControlType;
                node["name"] = nodes[index].Name;
                node["automationId"] = nodes[index].AutomationId;
                node["indexAmongSameType"] = nodes[index].IndexAmongSameType;
                node["siblingCount"] = nodes[index].SiblingCount;
                result[index] = node;
            }
            return result;
        }
    }

    public sealed class UiResponsivenessProbe : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Dispatcher dispatcher;
        private DispatcherTimer timer;
        private Stopwatch watch;
        private long previous;
        private long maxUnresponsive;

        public UiResponsivenessProbe(int intervalMs)
        {
            int interval = Math.Max(10, intervalMs);
            thread = new Thread(delegate()
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                watch = Stopwatch.StartNew();
                previous = watch.ElapsedMilliseconds;
                timer = new DispatcherTimer(DispatcherPriority.Send);
                timer.Interval = TimeSpan.FromMilliseconds(interval);
                timer.Tick += delegate
                {
                    long now = watch.ElapsedMilliseconds;
                    long unresponsive = Math.Max(0, now - previous - interval);
                    long current;
                    do
                    {
                        current = Interlocked.Read(ref maxUnresponsive);
                        if (unresponsive <= current) break;
                    }
                    while (Interlocked.CompareExchange(ref maxUnresponsive, unresponsive, current) != current);
                    previous = now;
                };
                timer.Start();
                ready.Set();
                Dispatcher.Run();
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(5000)) throw new TimeoutException("UI responsiveness probe did not start.");
        }

        public long MaxUnresponsiveMs
        {
            get { return Interlocked.Read(ref maxUnresponsive); }
        }

        public void Dispose()
        {
            if (dispatcher != null) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(5000);
            ready.Dispose();
        }
    }
}
