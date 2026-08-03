namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Text;
    using System.Web.Script.Serialization;

    public sealed class ScanProgress
    {
        public string Phase;
        public string WindowTitle;
        public int WindowIndex;
        public int WindowCount;
        public int NodeCount;
        public bool Finished;
    }

    // Worker side of the scan. Results are streamed back in chunks so a large
    // application does not produce one unreadable line and so the operator sees
    // the count grow while the walk is still running.
    public static class ScanService
    {
        private const int ChunkSize = 200;

        public static void Handle(JavaScriptSerializer serializer, string requestId, Dictionary<string, object> request)
        {
            long handle = WireValue.Long(request, "hwnd");
            IntPtr window = new IntPtr(handle);
            int excludeProcessId = WireValue.Int(request, "excludeProcessId");
            ScanLimits limits = new ScanLimits();
            limits.MaxNodes = WireValue.IntOr(request, "maxNodes", limits.MaxNodes);
            limits.MaxDepth = WireValue.IntOr(request, "maxDepth", limits.MaxDepth);
            limits.UiaBudgetMs = WireValue.IntOr(request, "uiaBudgetMs", limits.UiaBudgetMs);
            limits.MsaaBudgetMs = WireValue.IntOr(request, "msaaBudgetMs", limits.MsaaBudgetMs);
            limits.HitTestBudgetMs = WireValue.IntOr(request, "hitTestBudgetMs", limits.HitTestBudgetMs);
            limits.IncludeOffscreen = WireValue.Bool(request, "includeOffscreen");
            limits.HitTest = WireValue.Text(request, "hitTest") ?? "auto";
            RectValue area = null;
            Dictionary<string, object> rect = WireValue.Object(request, "rect");
            if (rect != null)
            {
                area = new RectValue();
                area.X = WireValue.Int(rect, "x");
                area.Y = WireValue.Int(rect, "y");
                area.Width = WireValue.Int(rect, "width");
                area.Height = WireValue.Int(rect, "height");
            }

            List<ScanCoverage> coverage = new List<ScanCoverage>();
            ScanCoverage uiaCoverage = new ScanCoverage();
            List<ScanNode> uiaNodes = Guarded(delegate { return UiaScan.Walk(window, limits, excludeProcessId, uiaCoverage); }, uiaCoverage, "uia");
            coverage.Add(uiaCoverage);
            Emit(serializer, requestId, "uia", uiaNodes);

            ScanCoverage msaaCoverage = new ScanCoverage();
            List<ScanNode> msaaNodes = Guarded(delegate { return MsaaScan.Walk(window, limits, msaaCoverage); }, msaaCoverage, "msaa");
            coverage.Add(msaaCoverage);
            Emit(serializer, requestId, "msaa", msaaNodes);

            bool sparse = uiaNodes.Count + msaaNodes.Count < 8;
            bool sample = limits.HitTest == "always" || (limits.HitTest == "auto" && sparse);
            ScanCoverage hitCoverage = new ScanCoverage();
            hitCoverage.Provider = "hit-test";
            if (sample)
            {
                List<ScanNode> hitNodes = Guarded(delegate { return HitTestScan.Sample(window, area, limits, hitCoverage); }, hitCoverage, "hit-test");
                Emit(serializer, requestId, "hit-test", hitNodes);
            }
            else
            {
                hitCoverage.State = "skipped";
                hitCoverage.AddReason("HITTEST-SKIPPED", "Coordinate sampling was not needed because the accessibility tree already returned " + (uiaNodes.Count + msaaNodes.Count) + " element(s).");
            }
            coverage.Add(hitCoverage);

            Dictionary<string, object> done = new Dictionary<string, object>();
            done["type"] = "scanDone";
            done["id"] = requestId;
            List<object> coverageWire = new List<object>();
            for (int index = 0; index < coverage.Count; index++) coverageWire.Add(CoverageWire(coverage[index]));
            done["coverage"] = coverageWire.ToArray();
            Console.Out.WriteLine(serializer.Serialize(done));
            Console.Out.Flush();
        }

        private static List<ScanNode> Guarded(Func<List<ScanNode>> action, ScanCoverage coverage, string provider)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                coverage.Provider = provider;
                coverage.State = "unavailable";
                coverage.AddReason("SCAN-FAIL", exception.GetType().Name + ": " + exception.Message);
                return new List<ScanNode>();
            }
        }

        private static void Emit(JavaScriptSerializer serializer, string requestId, string provider, List<ScanNode> nodes)
        {
            if (nodes == null) return;
            int offset = 0;
            while (offset < nodes.Count)
            {
                int size = Math.Min(ChunkSize, nodes.Count - offset);
                object[] wire = new object[size];
                for (int index = 0; index < size; index++) wire[index] = ScanNodeWire.ToWire(nodes[offset + index]);
                Dictionary<string, object> chunk = new Dictionary<string, object>();
                chunk["type"] = "scanChunk";
                chunk["id"] = requestId;
                chunk["provider"] = provider;
                chunk["nodes"] = wire;
                Console.Out.WriteLine(serializer.Serialize(chunk));
                Console.Out.Flush();
                offset += size;
            }
        }

        private static Dictionary<string, object> CoverageWire(ScanCoverage coverage)
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            value["provider"] = coverage.Provider;
            value["state"] = coverage.State;
            value["nodeCount"] = coverage.NodeCount;
            value["durationMs"] = coverage.DurationMs;
            value["truncated"] = coverage.Truncated;
            object[] reasons = new object[coverage.Reasons.Count];
            for (int index = 0; index < coverage.Reasons.Count; index++)
            {
                Dictionary<string, object> reason = new Dictionary<string, object>();
                reason["code"] = coverage.Reasons[index].Code;
                reason["message"] = coverage.Reasons[index].Message;
                reasons[index] = reason;
            }
            value["reasons"] = reasons;
            return value;
        }

        public static ScanCoverage CoverageFromWire(Dictionary<string, object> value)
        {
            ScanCoverage coverage = new ScanCoverage();
            coverage.Provider = WireValue.Text(value, "provider");
            coverage.State = WireValue.Text(value, "state") ?? "unknown";
            coverage.NodeCount = WireValue.Int(value, "nodeCount");
            coverage.DurationMs = WireValue.Int(value, "durationMs");
            coverage.Truncated = WireValue.Bool(value, "truncated");
            object reasons;
            if (value.TryGetValue("reasons", out reasons))
            {
                object[] items = reasons as object[];
                if (items != null)
                {
                    for (int index = 0; index < items.Length; index++)
                    {
                        Dictionary<string, object> reason = items[index] as Dictionary<string, object>;
                        if (reason == null) continue;
                        coverage.AddReason(WireValue.Text(reason, "code"), WireValue.Text(reason, "message"));
                    }
                }
            }
            return coverage;
        }
    }

    // Host side. A dedicated worker process is used so a long scan never blocks
    // the hover acquisition workers that keep the live view responsive.
    public sealed class ScanRunner : IDisposable
    {
        private readonly object sync = new object();
        private readonly string baseDir;
        private AcquisitionWorkerProcess worker;
        private bool cancelled;
        private bool disposed;
        private int sequence;

        public ScanRunner(string baseDirectory)
        {
            baseDir = baseDirectory;
        }

        public void Cancel()
        {
            AcquisitionWorkerProcess current;
            lock (sync)
            {
                cancelled = true;
                current = worker;
                worker = null;
            }
            // Killing the worker unblocks a walk that is already in flight; the
            // partial nodes already received are kept.
            if (current != null)
            {
                current.Kill();
                current.Dispose();
            }
        }

        public ScanResult Run(int processId, long preferredHwnd, ScanLimits limits, Action<ScanProgress> progress)
        {
            if (limits == null) limits = new ScanLimits();
            lock (sync) { cancelled = false; }
            Stopwatch watch = Stopwatch.StartNew();
            ScanResult result = new ScanResult();
            sequence++;
            result.ScanId = "sc-" + sequence.ToString("0000", CultureInfo.InvariantCulture);
            result.StartedAt = DateTimeOffset.Now;
            result.ProcessId = processId;
            result.ProcessName = ProcessName(processId);

            TargetWindowInfo[] windows = WindowTools.ListProcessWindows(processId, preferredHwnd);
            if (windows.Length == 0)
            {
                result.AddUnknown("no-visible-window");
                result.CoverageFor("target").State = "unavailable";
                result.CoverageFor("target").AddReason("SCAN-NOWINDOW", "The selected application has no visible top level window right now.");
                Finish(result, watch);
                Report(progress, "done", null, 0, 0, 0, true);
                return result;
            }

            AcquisitionWorkerProcess current = null;
            try
            {
                try
                {
                    current = AcquisitionWorkerProcess.Start(baseDir, 20000);
                    lock (sync)
                    {
                        if (cancelled)
                        {
                            current.Kill();
                            current.Dispose();
                            current = null;
                        }
                        else
                        {
                            worker = current;
                        }
                    }
                }
                catch (Exception exception)
                {
                    ScanCoverage coverage = result.CoverageFor("uia");
                    coverage.State = "unavailable";
                    coverage.AddReason("UIA-INIT", "A scan worker could not be started: " + exception.GetType().Name + ": " + exception.Message);
                    ScanCoverage msaa = result.CoverageFor("msaa");
                    msaa.State = "unavailable";
                    msaa.AddReason("MSAA-INIT", "A scan worker could not be started, so no MSAA walk was attempted.");
                    result.AddUnknown("accessibility-providers-unavailable");
                }

                for (int index = 0; index < windows.Length && index < 12; index++)
                {
                    bool stop;
                    lock (sync) { stop = cancelled; }
                    if (stop)
                    {
                        result.Cancelled = true;
                        result.AddUnknown("cancelled-by-operator");
                        break;
                    }
                    TargetWindowInfo target = windows[index];
                    Report(progress, "window", target.Title, index, windows.Length, result.Nodes.Count, false);
                    ScanWindowResult window = ScanWindow(current, target, processId, limits, result, progress, index, windows.Length);
                    // The screen id is handed out in the order the windows were
                    // walked and every part found inside carries it, so the
                    // picture of that screen and the parts on it stay tied
                    // together for the rest of the run.
                    window.ScreenId = "S" + (result.Windows.Count + 1).ToString(CultureInfo.InvariantCulture);
                    result.Windows.Add(window);
                    for (int nodeIndex = 0; nodeIndex < window.Nodes.Count; nodeIndex++)
                    {
                        window.Nodes[nodeIndex].ScreenId = window.ScreenId;
                        result.Nodes.Add(window.Nodes[nodeIndex]);
                    }
                    Report(progress, "window-done", target.Title, index, windows.Length, result.Nodes.Count, false);
                }
                if (windows.Length > 12)
                {
                    result.AddUnknown("window-limit=12 of " + windows.Length + " windows were scanned");
                }
            }
            finally
            {
                AcquisitionWorkerProcess finished;
                lock (sync)
                {
                    finished = worker;
                    worker = null;
                }
                if (finished != null) finished.Dispose();
            }

            for (int index = 0; index < result.Nodes.Count; index++) result.Nodes[index].NodeId = index;
            Finish(result, watch);
            Report(progress, "done", null, windows.Length, windows.Length, result.Nodes.Count, true);
            return result;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            Cancel();
        }

        private ScanWindowResult ScanWindow(AcquisitionWorkerProcess current, TargetWindowInfo target, int processId, ScanLimits limits, ScanResult result, Action<ScanProgress> progress, int windowIndex, int windowCount)
        {
            ScanWindowResult window = new ScanWindowResult();
            window.Hwnd = target.Hwnd;
            window.Title = target.Title;
            window.ClassName = target.ClassName;
            window.Rect = WindowTools.GetPhysicalRect(new IntPtr(target.Hwnd));

            ScanCoverage win32Coverage = new ScanCoverage();
            List<ScanNode> win32Nodes = Win32Scan.Enumerate(new IntPtr(target.Hwnd), processId, limits, win32Coverage);
            Accumulate(result.CoverageFor("win32"), win32Coverage);
            window.Coverage.Add(win32Coverage);

            List<ScanNode> uiaNodes = new List<ScanNode>();
            List<ScanNode> msaaNodes = new List<ScanNode>();
            List<ScanNode> hitNodes = new List<ScanNode>();
            if (current != null)
            {
                List<ScanCoverage> coverage = new List<ScanCoverage>();
                Dictionary<string, List<ScanNode>> byProvider = new Dictionary<string, List<ScanNode>>(StringComparer.Ordinal);
                string failure;
                int budget = limits.UiaBudgetMs + limits.MsaaBudgetMs + limits.HitTestBudgetMs + 15000;
                bool completed = current.TryScan(target.Hwnd, limits, window.Rect, processId, System.Diagnostics.Process.GetCurrentProcess().Id, budget, byProvider, coverage, out failure);
                if (byProvider.ContainsKey("uia")) uiaNodes = byProvider["uia"];
                if (byProvider.ContainsKey("msaa")) msaaNodes = byProvider["msaa"];
                if (byProvider.ContainsKey("hit-test")) hitNodes = byProvider["hit-test"];
                for (int index = 0; index < coverage.Count; index++)
                {
                    window.Coverage.Add(coverage[index]);
                    Accumulate(result.CoverageFor(coverage[index].Provider), coverage[index]);
                }
                if (!completed)
                {
                    ScanCoverage timeout = result.CoverageFor("uia");
                    timeout.State = "partial";
                    timeout.Truncated = true;
                    timeout.AddReason("SCAN-WORKERSTOP", failure ?? "The scan worker stopped responding and was terminated.");
                    result.AddUnknown("scan-worker-stopped");
                    Cancel();
                }
            }

            List<ScanNode> merged = ScanMerge.Merge(uiaNodes, msaaNodes, win32Nodes, hitNodes, result);
            window.Nodes = merged;
            Report(progress, "window-merged", target.Title, windowIndex, windowCount, result.Nodes.Count + merged.Count, false);
            return window;
        }

        private static void Accumulate(ScanCoverage total, ScanCoverage part)
        {
            if (part == null) return;
            total.Provider = part.Provider;
            total.NodeCount += part.NodeCount;
            total.DurationMs += part.DurationMs;
            if (part.Truncated) total.Truncated = true;
            if (total.State == "unknown" || total.State == "ok")
            {
                if (part.State == "unavailable" && total.NodeCount == 0) total.State = "unavailable";
                else if (part.State == "partial" || part.State == "unavailable") total.State = "partial";
                else if (part.State == "ok") total.State = "ok";
                else if (total.State == "unknown") total.State = part.State;
            }
            for (int index = 0; index < part.Reasons.Count; index++) total.AddReason(part.Reasons[index].Code, part.Reasons[index].Message);
        }

        private static void Finish(ScanResult result, Stopwatch watch)
        {
            watch.Stop();
            result.DurationMs = (int)watch.ElapsedMilliseconds;
            result.EndedAt = DateTimeOffset.Now;
        }

        private static void Report(Action<ScanProgress> progress, string phase, string title, int windowIndex, int windowCount, int nodeCount, bool finished)
        {
            if (progress == null) return;
            ScanProgress value = new ScanProgress();
            value.Phase = phase;
            value.WindowTitle = title;
            value.WindowIndex = windowIndex;
            value.WindowCount = windowCount;
            value.NodeCount = nodeCount;
            value.Finished = finished;
            try
            {
                progress(value);
            }
            catch
            {
            }
        }

        private static string ProcessName(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId)) return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }
    }

    public static class ScanSummary
    {
        public static string Build(ScanResult result)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(Messages.Text("scan-summary-title.txt", "Automatic scan result"));
            text.AppendLine();
            text.AppendLine(Messages.Text("scan-summary-target.txt", "Target") + ": " + (result.ProcessName ?? "?") + " (pid " + result.ProcessId + ")");
            text.AppendLine(Messages.Text("scan-summary-started.txt", "Started") + ": " + result.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "  " + Messages.Text("scan-summary-duration.txt", "Duration") + ": " + result.DurationMs + " ms");
            int decorations = 0;
            for (int index = 0; index < result.Nodes.Count; index++) if (result.Nodes[index].Decoration) decorations++;
            int mainCount = result.Nodes.Count - decorations;
            text.AppendLine(Messages.Text("scan-summary-count.txt", "Elements found") + ": " + mainCount +
                "  " + Messages.Text("scan-summary-windows.txt", "Windows") + ": " + result.Windows.Count);
            text.AppendLine(Messages.Text("scan-summary-decoration.txt", "Frame parts such as title bars and scroll bars, kept in the detailed record") + ": " + decorations);
            if (result.Cancelled) text.AppendLine(Messages.Text("scan-summary-cancelled.txt", "The scan was stopped before it finished."));
            text.AppendLine();
            text.AppendLine(Messages.Text("scan-summary-breakdown.txt", "By control type"));
            Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);
            int withHandle = 0;
            int withAutomationId = 0;
            int withName = 0;
            int actionable = 0;
            for (int index = 0; index < result.Nodes.Count; index++)
            {
                ScanNode node = result.Nodes[index];
                if (node.Decoration) continue;
                string type = node.DisplayType;
                int count;
                byType.TryGetValue(type, out count);
                byType[type] = count + 1;
                if (node.Hwnd != 0) withHandle++;
                if (!String.IsNullOrEmpty(node.AutomationId)) withAutomationId++;
                if (!String.IsNullOrEmpty(node.Name)) withName++;
                if (node.Patterns != null && node.Patterns.Length > 0) actionable++;
            }
            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(byType);
            ordered.Sort(delegate(KeyValuePair<string, int> left, KeyValuePair<string, int> right) { return right.Value.CompareTo(left.Value); });
            for (int index = 0; index < ordered.Count && index < 20; index++)
            {
                text.AppendLine("  " + ordered[index].Key + ": " + ordered[index].Value);
            }
            if (ordered.Count > 20) text.AppendLine("  ... " + (ordered.Count - 20));
            text.AppendLine();
            text.AppendLine(Messages.Text("scan-summary-identity.txt", "Re-identification material"));
            text.AppendLine("  " + Messages.Text("scan-summary-withname.txt", "with a name") + ": " + withName);
            text.AppendLine("  " + Messages.Text("scan-summary-withid.txt", "with an AutomationId") + ": " + withAutomationId);
            text.AppendLine("  " + Messages.Text("scan-summary-withhwnd.txt", "with a window handle") + ": " + withHandle +
                " / " + Messages.Text("scan-summary-nohwnd.txt", "without one") + ": " + (mainCount - withHandle));
            text.AppendLine("  " + Messages.Text("scan-summary-actionable.txt", "with an operable pattern") + ": " + actionable);
            if (mainCount <= 2)
            {
                text.AppendLine();
                text.AppendLine(Messages.Text("scan-summary-opaque.txt", "This application exposes almost no inner parts. Only window level facts and whatever coordinate sampling could reach are listed."));
            }
            text.AppendLine();
            text.AppendLine(Messages.Text("scan-summary-coverage.txt", "Coverage and what could not be obtained"));
            for (int index = 0; index < result.Coverage.Count; index++)
            {
                ScanCoverage coverage = result.Coverage[index];
                text.AppendLine("  [" + coverage.Provider + "] " + coverage.State + " " + coverage.NodeCount + " " +
                    Messages.Text("scan-summary-items.txt", "element(s)") + " " + coverage.DurationMs + " ms" + (coverage.Truncated ? " (" + Messages.Text("scan-summary-truncated.txt", "truncated") + ")" : String.Empty));
                for (int reason = 0; reason < coverage.Reasons.Count; reason++)
                {
                    text.AppendLine("      - " + coverage.Reasons[reason].Code + ": " + coverage.Reasons[reason].Message);
                }
            }
            if (result.Unknowns.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Messages.Text("scan-summary-unknown.txt", "Undetermined"));
                for (int index = 0; index < result.Unknowns.Count; index++) text.AppendLine("  - " + result.Unknowns[index]);
            }
            text.AppendLine();
            text.AppendLine(Messages.Text("scan-summary-honesty.txt", "This list covers what the providers above exposed while the scan ran. Areas drawn by the application itself can be missing, and nothing here proves the list is complete."));
            return text.ToString();
        }
    }
}
