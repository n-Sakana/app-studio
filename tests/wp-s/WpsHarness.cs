using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Interop = Microsoft.UIAutomation;

namespace AppStudio.Wps
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                ArgMap options = new ArgMap(args);
                Scenario scenario = new Scenario(options);
                scenario.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
    }

    internal sealed class ArgMap
    {
        private readonly Dictionary<string, string> values;

        internal ArgMap(string[] args)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i;
            for (i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException("Expected --name value arguments.");
                }
                values[args[i].Substring(2)] = args[++i];
            }
        }

        internal string Require(string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || String.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Missing --" + name + ".");
            }
            return value;
        }

        internal string Get(string name, string fallback)
        {
            string value;
            return values.TryGetValue(name, out value) ? value : fallback;
        }

        internal int GetInt(string name, int fallback)
        {
            return Int32.Parse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        }
    }

    internal sealed class Scenario
    {
        private readonly string strategyName;
        private readonly string hangMode;
        private readonly int iterations;
        private readonly int requestTimeoutMs;
        private readonly int toleranceMs;
        private readonly int internalTimeoutMs;
        private readonly int temporarySeconds;
        private readonly int win32MessageTimeoutMs;
        private readonly int uiThresholdMs;
        private readonly string fixtureExe;
        private readonly string workerScript;
        private readonly string workerSource;
        private readonly string powershellExe;
        private readonly string outputDir;
        private readonly string runtimeDir;

        internal Scenario(ArgMap options)
        {
            strategyName = options.Require("strategy").ToUpperInvariant();
            hangMode = options.Require("hang-mode").ToLowerInvariant();
            iterations = options.GetInt("iterations", 20);
            requestTimeoutMs = options.GetInt("request-timeout-ms", 1500);
            toleranceMs = options.GetInt("tolerance-ms", 300);
            internalTimeoutMs = options.GetInt("internal-timeout-ms", 1000);
            temporarySeconds = options.GetInt("temporary-seconds", 15);
            win32MessageTimeoutMs = options.GetInt("win32-message-timeout-ms", 150);
            uiThresholdMs = options.GetInt("ui-threshold-ms", 250);
            fixtureExe = Path.GetFullPath(options.Require("fixture-exe"));
            workerScript = Path.GetFullPath(options.Require("worker-script"));
            workerSource = Path.GetFullPath(options.Require("worker-source"));
            powershellExe = Path.GetFullPath(options.Require("powershell-exe"));
            outputDir = Path.GetFullPath(options.Require("output-dir"));
            runtimeDir = Path.Combine(outputDir, "runtime-" + strategyName + "-" + hangMode);

            if (strategyName != "A" && strategyName != "B" && strategyName != "B2" && strategyName != "C")
            {
                throw new ArgumentException("Unknown strategy " + strategyName + ".");
            }
            if (hangMode != "temporary" && hangMode != "permanent")
            {
                throw new ArgumentException("Unknown hang mode " + hangMode + ".");
            }
        }

        internal void Run()
        {
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(runtimeDir);
            DeleteRuntimeSignals();

            string healthyPrefix = "healthy";
            string hangPrefix = "hang";
            FixtureProcess healthy = null;
            FixtureProcess hang = null;
            IProbeStrategy strategy = null;
            UiHeartbeat heartbeat = null;
            List<MeasurementRow> rows = new List<MeasurementRow>();
            Metrics baselineMetrics = null;
            Metrics afterReleaseMetrics = null;
            BApiMatrix bApiMatrix = null;
            List<long> healthyBaselines = new List<long>();
            string permanentToken = "permanent";

            try
            {
                heartbeat = new UiHeartbeat();
                healthy = FixtureProcess.Start(fixtureExe, "healthy", hangMode, runtimeDir, healthyPrefix, temporarySeconds, 80);
                hang = FixtureProcess.Start(fixtureExe, "hang", hangMode, runtimeDir, hangPrefix, temporarySeconds, 520);

                strategy = CreateStrategy();
                if (strategyName == "B")
                {
                    CompositeResult prime = null;
                    DisposableManagedStrategy managedPrime = new DisposableManagedStrategy();
                    int primeAttempt;
                    try
                    {
                        for (primeAttempt = 0; primeAttempt < 10; primeAttempt++)
                        {
                            prime = CompositeProbe.Run(hang.Info, managedPrime, requestTimeoutMs, win32MessageTimeoutMs);
                            if (prime.HealthySuccess)
                            {
                                break;
                            }
                            Thread.Sleep(100);
                        }
                    }
                    finally
                    {
                        managedPrime.Dispose();
                    }
                    if (prime == null || !prime.HealthySuccess)
                    {
                        throw new InvalidOperationException("B API matrix target could not be primed.");
                    }
                    bApiMatrix = new BApiMatrix(hang.Info.X, hang.Info.Y, internalTimeoutMs);
                }

                int warm;
                for (warm = 0; warm < 3; warm++)
                {
                    CompositeResult warmResult = CompositeProbe.Run(healthy.Info, strategy, requestTimeoutMs, win32MessageTimeoutMs);
                    if (!warmResult.HealthySuccess)
                    {
                        throw new InvalidOperationException("Warm healthy probe failed: " + warmResult.Uia.Reason + " pid=" + warmResult.Uia.ProcessId.ToString(CultureInfo.InvariantCulture) + " automationId=" + warmResult.Uia.AutomationId + " name=" + warmResult.Uia.Name);
                    }
                }

                for (warm = 0; warm < 5; warm++)
                {
                    CompositeResult healthyResult = CompositeProbe.Run(healthy.Info, strategy, requestTimeoutMs, win32MessageTimeoutMs);
                    if (!healthyResult.HealthySuccess)
                    {
                        throw new InvalidOperationException("Healthy baseline failed: " + healthyResult.Uia.Reason + " pid=" + healthyResult.Uia.ProcessId.ToString(CultureInfo.InvariantCulture) + " automationId=" + healthyResult.Uia.AutomationId + " name=" + healthyResult.Uia.Name);
                    }
                    healthyBaselines.Add(healthyResult.TotalMs);
                }

                strategy.PrepareNextIteration(0);
                Thread.Sleep(250);
                ForceCollection();
                baselineMetrics = Metrics.Capture(strategy);
                long healthyMedian = Median(healthyBaselines);

                if (hangMode == "permanent")
                {
                    TriggerHang(hangPrefix, permanentToken);
                    WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".hung"), permanentToken, 5000);
                }

                int iteration;
                for (iteration = 1; iteration <= iterations; iteration++)
                {
                    string token = hangMode == "permanent" ? permanentToken : "iteration-" + iteration.ToString("D2", CultureInfo.InvariantCulture);
                    if (hangMode == "temporary")
                    {
                        TriggerHang(hangPrefix, token);
                        WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".hung"), token, 5000);
                    }

                    Metrics before = Metrics.Capture(strategy);
                    heartbeat.ResetMaxDelay();
                    int restartBefore = strategy.RestartCount;

                    CompositeResult hungResult = CompositeProbe.Run(hang.Info, strategy, requestTimeoutMs, win32MessageTimeoutMs);
                    bool switched = strategy.RestartCount > restartBefore;
                    CompositeResult healthyResult = CompositeProbe.Run(healthy.Info, strategy, requestTimeoutMs, win32MessageTimeoutMs);
                    long uiMax = heartbeat.ConsumeMaxDelay();

                    if (hangMode == "temporary")
                    {
                        WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".recovered"), token, (temporarySeconds + 5) * 1000);
                    }

                    strategy.PrepareNextIteration(iteration);
                    Thread.Sleep(250);
                    strategy.CleanupRecovered();
                    ForceCollection();
                    Metrics after = Metrics.Capture(strategy);

                    MeasurementRow row = MeasurementRow.Create(
                        strategyName, hangMode, iteration, requestTimeoutMs, toleranceMs,
                        internalTimeoutMs, temporarySeconds, uiThresholdMs, hungResult,
                        healthyResult, healthyMedian, switched, baselineMetrics, before, after, uiMax, strategy);
                    rows.Add(row);
                    Console.Out.WriteLine("PROGRESS|" + strategyName + "|" + hangMode + "|" + iteration.ToString(CultureInfo.InvariantCulture) + "|" + hungResult.Outcome + "|" + hungResult.TotalMs.ToString(CultureInfo.InvariantCulture) + "|" + healthyResult.TotalMs.ToString(CultureInfo.InvariantCulture) + "|" + after.OrphanThreads.ToString(CultureInfo.InvariantCulture) + "|" + after.OrphanProcesses.ToString(CultureInfo.InvariantCulture));
                    Console.Out.Flush();
                }

                string apiToken = "api-matrix";
                if (bApiMatrix != null)
                {
                    if (hangMode == "temporary")
                    {
                        TriggerHang(hangPrefix, apiToken);
                        WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".hung"), apiToken, 5000);
                    }
                    bApiMatrix.Execute(requestTimeoutMs + toleranceMs);
                }

                if (hangMode == "permanent")
                {
                    File.WriteAllText(Path.Combine(runtimeDir, hangPrefix + ".release"), permanentToken, Encoding.ASCII);
                    WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".recovered"), permanentToken, 5000);
                }
                else if (bApiMatrix != null)
                {
                    WaitForToken(Path.Combine(runtimeDir, hangPrefix + ".recovered"), apiToken, (temporarySeconds + 5) * 1000);
                }

                if (bApiMatrix != null)
                {
                    bApiMatrix.WaitAfterRelease(5000);
                    WriteBApiMatrix(bApiMatrix.Results);
                }

                Thread.Sleep(750);
                strategy.CleanupRecovered();
                ForceCollection();
                afterReleaseMetrics = Metrics.Capture(strategy);

                WriteRows(rows);
                WriteSummary(healthyMedian, baselineMetrics, afterReleaseMetrics, rows, strategy);
                WriteWorkerStartup(strategy);
            }
            finally
            {
                if (hangMode == "permanent")
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(runtimeDir, hangPrefix + ".release"), permanentToken, Encoding.ASCII);
                    }
                    catch
                    {
                    }
                }
                if (strategy != null)
                {
                    strategy.Dispose();
                }
                if (bApiMatrix != null)
                {
                    bApiMatrix.Dispose();
                }
                if (hang != null)
                {
                    hang.Dispose();
                }
                if (healthy != null)
                {
                    healthy.Dispose();
                }
                if (heartbeat != null)
                {
                    heartbeat.Dispose();
                }
            }
        }

        private IProbeStrategy CreateStrategy()
        {
            if (strategyName == "A")
            {
                return new DisposableManagedStrategy();
            }
            if (strategyName == "B")
            {
                return new PersistentInProcessStrategy("B", internalTimeoutMs);
            }
            if (strategyName == "B2")
            {
                return new PersistentInProcessStrategy("B2", internalTimeoutMs);
            }
            return new ChildProcessStrategy(powershellExe, workerScript, workerSource, outputDir);
        }

        private void TriggerHang(string prefix, string token)
        {
            File.WriteAllText(Path.Combine(runtimeDir, prefix + ".trigger"), token, Encoding.ASCII);
        }

        private static void WaitForToken(string path, string token, int timeoutMs)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (File.Exists(path) && String.Equals(ReadSharedText(path).Trim(), token, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("Timed out waiting for " + path + " token " + token + ".");
        }

        private static string ReadSharedText(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private void DeleteRuntimeSignals()
        {
            string[] files = Directory.GetFiles(runtimeDir);
            int i;
            for (i = 0; i < files.Length; i++)
            {
                File.Delete(files[i]);
            }
        }

        private void WriteRows(List<MeasurementRow> rows)
        {
            string path = Path.Combine(outputDir, "raw-" + strategyName + "-" + hangMode + ".csv");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(MeasurementRow.Header);
                int i;
                for (i = 0; i < rows.Count; i++)
                {
                    writer.WriteLine(rows[i].ToCsv());
                }
            }
        }

        private void WriteSummary(long healthyMedian, Metrics baseline, Metrics afterRelease, List<MeasurementRow> rows, IProbeStrategy strategy)
        {
            string path = Path.Combine(outputDir, "summary-" + strategyName + "-" + hangMode + ".csv");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("strategy,hang_mode,iterations,healthy_baseline_median_ms,baseline_threads,baseline_handles,baseline_child_processes,baseline_working_set_bytes,after_release_threads,after_release_handles,after_release_child_processes,after_release_working_set_bytes,after_release_orphan_threads,after_release_orphan_processes,restart_count,max_t_return_ms,max_healthy_ms,max_ui_unresponsive_ms");
                writer.WriteLine(String.Join(",", new string[]
                {
                    Csv.Value(strategyName), Csv.Value(hangMode), rows.Count.ToString(CultureInfo.InvariantCulture),
                    healthyMedian.ToString(CultureInfo.InvariantCulture), baseline.Threads.ToString(CultureInfo.InvariantCulture),
                    baseline.Handles.ToString(CultureInfo.InvariantCulture), baseline.ChildProcesses.ToString(CultureInfo.InvariantCulture),
                    baseline.WorkingSet.ToString(CultureInfo.InvariantCulture), afterRelease.Threads.ToString(CultureInfo.InvariantCulture),
                    afterRelease.Handles.ToString(CultureInfo.InvariantCulture), afterRelease.ChildProcesses.ToString(CultureInfo.InvariantCulture),
                    afterRelease.WorkingSet.ToString(CultureInfo.InvariantCulture), afterRelease.OrphanThreads.ToString(CultureInfo.InvariantCulture),
                    afterRelease.OrphanProcesses.ToString(CultureInfo.InvariantCulture), strategy.RestartCount.ToString(CultureInfo.InvariantCulture),
                    rows.Max(delegate(MeasurementRow row) { return row.HangReturnMs; }).ToString(CultureInfo.InvariantCulture),
                    rows.Max(delegate(MeasurementRow row) { return row.HealthyDurationMs; }).ToString(CultureInfo.InvariantCulture),
                    rows.Max(delegate(MeasurementRow row) { return row.UiMaxMs; }).ToString(CultureInfo.InvariantCulture)
                }));
            }
        }

        private void WriteWorkerStartup(IProbeStrategy strategy)
        {
            string path = Path.Combine(outputDir, "worker-startup-" + strategyName + "-" + hangMode + ".csv");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("strategy,hang_mode,sequence,reason,pid,startup_ms,ready");
                int i;
                for (i = 0; i < strategy.WorkerStartups.Count; i++)
                {
                    WorkerStartup start = strategy.WorkerStartups[i];
                    writer.WriteLine(String.Join(",", new string[]
                    {
                        Csv.Value(strategyName), Csv.Value(hangMode), (i + 1).ToString(CultureInfo.InvariantCulture),
                        Csv.Value(start.Reason), start.Pid.ToString(CultureInfo.InvariantCulture),
                        start.StartupMs.ToString(CultureInfo.InvariantCulture), Csv.Bool(start.Ready)
                    }));
                }
            }
        }

        private void WriteBApiMatrix(List<BApiResult> results)
        {
            string path = Path.Combine(outputDir, "b-api-" + strategyName + "-" + hangMode + ".csv");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("strategy,hang_mode,operation,state,reason,duration_ms,internal_timeout_ms,outer_timeout_ms,thread_alive_before_release,thread_alive_after_release");
                int i;
                for (i = 0; i < results.Count; i++)
                {
                    BApiResult result = results[i];
                    writer.WriteLine(String.Join(",", new string[]
                    {
                        "B", Csv.Value(hangMode), Csv.Value(result.Operation), Csv.Value(result.State),
                        Csv.Value(result.Reason), result.DurationMs.ToString(CultureInfo.InvariantCulture),
                        internalTimeoutMs.ToString(CultureInfo.InvariantCulture),
                        (requestTimeoutMs + toleranceMs).ToString(CultureInfo.InvariantCulture),
                        Csv.Bool(result.ThreadAliveBeforeRelease), Csv.Bool(result.ThreadAliveAfterRelease)
                    }));
                }
            }
        }

        private static long Median(List<long> values)
        {
            List<long> sorted = new List<long>(values);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        private static void ForceCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    internal sealed class FixtureInfo
    {
        internal int Pid;
        internal IntPtr Window;
        internal IntPtr Target;
        internal int X;
        internal int Y;
    }

    internal sealed class FixtureProcess : IDisposable
    {
        private readonly Process process;
        internal readonly FixtureInfo Info;

        private FixtureProcess(Process processValue, FixtureInfo infoValue)
        {
            process = processValue;
            Info = infoValue;
        }

        internal static FixtureProcess Start(string fixtureExe, string kind, string hangMode, string runDir, string prefix, int temporarySeconds, int left)
        {
            string readyPath = Path.Combine(runDir, prefix + ".ready");
            if (File.Exists(readyPath))
            {
                File.Delete(readyPath);
            }
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fixtureExe;
            startInfo.Arguments = "--kind " + Quote(kind) + " --hang-mode " + Quote(hangMode) + " --run-dir " + Quote(runDir) + " --prefix " + Quote(prefix) + " --temporary-seconds " + temporarySeconds.ToString(CultureInfo.InvariantCulture) + " --left " + left.ToString(CultureInfo.InvariantCulture);
            startInfo.UseShellExecute = false;
            Process process = Process.Start(startInfo);

            Stopwatch wait = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && wait.ElapsedMilliseconds < 10000)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("Fixture exited before ready: " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
                }
                Thread.Sleep(20);
            }
            if (!File.Exists(readyPath))
            {
                throw new TimeoutException("Fixture ready file not created: " + readyPath);
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(readyPath, Encoding.UTF8);
            int i;
            for (i = 0; i < lines.Length; i++)
            {
                int split = lines[i].IndexOf('=');
                if (split > 0)
                {
                    values[lines[i].Substring(0, split)] = lines[i].Substring(split + 1);
                }
            }
            FixtureInfo info = new FixtureInfo();
            info.Pid = Int32.Parse(values["pid"], CultureInfo.InvariantCulture);
            info.Window = new IntPtr(Int64.Parse(values["window"], CultureInfo.InvariantCulture));
            info.Target = new IntPtr(Int64.Parse(values["target"], CultureInfo.InvariantCulture));
            info.X = Int32.Parse(values["x"], CultureInfo.InvariantCulture);
            info.Y = Int32.Parse(values["y"], CultureInfo.InvariantCulture);
            return new FixtureProcess(process, info);
        }

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
            }
            process.Dispose();
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class NativeResult
    {
        internal string CoreState;
        internal string MessageState;
        internal long DurationMs;
        internal string ClassName;
        internal int ControlId;
    }

    internal static class NativeProbe
    {
        private const uint WmGetTextLength = 0x000E;
        private const uint SmtoNormal = 0x0000;
        private const uint SmtoAbortIfHung = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        internal static NativeResult Run(FixtureInfo target, int messageTimeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            NativeResult result = new NativeResult();
            result.CoreState = "ok";
            result.MessageState = "ok";
            StringBuilder className = new StringBuilder(256);
            Rect rect;
            uint pid;
            try
            {
                if (GetClassName(target.Target, className, className.Capacity) == 0 || !GetWindowRect(target.Target, out rect))
                {
                    result.CoreState = "unavailable";
                }
                result.ControlId = GetDlgCtrlID(target.Target);
                GetWindowThreadProcessId(target.Target, out pid);
                result.ClassName = className.ToString();

                IntPtr messageResult;
                IntPtr sendResult = SendMessageTimeout(target.Target, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung | SmtoNormal, (uint)messageTimeoutMs, out messageResult);
                if (sendResult == IntPtr.Zero)
                {
                    result.MessageState = "timeout";
                }
            }
            catch
            {
                result.CoreState = "unavailable";
                result.MessageState = "error";
            }
            watch.Stop();
            result.DurationMs = watch.ElapsedMilliseconds;
            return result;
        }
    }

    internal sealed class CompositeResult
    {
        internal NativeResult Win32;
        internal UiaResult Uia;
        internal long TotalMs;
        internal string Outcome;
        internal bool HealthySuccess;
    }

    internal static class CompositeProbe
    {
        internal static CompositeResult Run(FixtureInfo target, IProbeStrategy strategy, int requestTimeoutMs, int win32MessageTimeoutMs)
        {
            Stopwatch total = Stopwatch.StartNew();
            NativeResult native = NativeProbe.Run(target, win32MessageTimeoutMs);
            int remaining = Math.Max(1, requestTimeoutMs - (int)total.ElapsedMilliseconds);
            UiaResult uia = strategy.Execute(target.X, target.Y, remaining);
            if (uia.State == "ok" && (uia.ProcessId != target.Pid || !String.Equals(uia.AutomationId, "TargetText", StringComparison.Ordinal)))
            {
                uia.State = "unavailable";
                uia.Reason = "UIA-WRONGELEMENT";
            }
            total.Stop();

            CompositeResult result = new CompositeResult();
            result.Win32 = native;
            result.Uia = uia;
            result.TotalMs = total.ElapsedMilliseconds;
            if (native.CoreState == "ok" && uia.State == "ok")
            {
                result.Outcome = native.MessageState == "ok" ? "ok" : "partial";
            }
            else if (native.CoreState == "ok" || uia.State == "ok")
            {
                result.Outcome = "partial";
            }
            else
            {
                result.Outcome = "unavailable";
            }
            result.HealthySuccess = result.Outcome == "ok" && uia.State == "ok";
            return result;
        }
    }

    internal sealed class UiaResult
    {
        internal string State;
        internal string Reason;
        internal string Stage;
        internal long TotalMs;
        internal long ElementMs;
        internal long PropertyMs;
        internal long PatternMs;
        internal string Name;
        internal string AutomationId;
        internal string ControlType;
        internal string Value;
        internal int ProcessId;

        internal static UiaResult Timeout(string stage, long elapsedMs)
        {
            UiaResult result = new UiaResult();
            result.State = "unavailable";
            result.Reason = "UIA-TIMEOUT";
            result.Stage = stage;
            result.TotalMs = elapsedMs;
            result.ElementMs = -1;
            result.PropertyMs = -1;
            result.PatternMs = -1;
            return result;
        }
    }

    internal sealed class ProbeWork
    {
        internal readonly int X;
        internal readonly int Y;
        internal readonly int BudgetMs;
        internal readonly ManualResetEventSlim Done;
        internal volatile string Stage;
        internal UiaResult Result;

        internal ProbeWork(int x, int y, int budgetMs)
        {
            X = x;
            Y = y;
            BudgetMs = budgetMs;
            Stage = "Queued";
            Done = new ManualResetEventSlim(false);
        }
    }

    internal sealed class WorkerStartup
    {
        internal string Reason;
        internal int Pid;
        internal long StartupMs;
        internal bool Ready;
    }

    internal sealed class ProcessIdentity
    {
        internal int Pid;
        internal long StartTimeUtcTicks;
    }

    internal interface IProbeStrategy : IDisposable
    {
        UiaResult Execute(int x, int y, int budgetMs);
        void PrepareNextIteration(int iteration);
        void CleanupRecovered();
        int QueueDepth { get; }
        int OrphanThreadCount { get; }
        int ChildProcessCount { get; }
        int OrphanProcessCount { get; }
        int RestartCount { get; }
        List<WorkerStartup> WorkerStartups { get; }
    }

    internal static class ManagedUiaProbe
    {
        internal static UiaResult Run(ProbeWork work)
        {
            Stopwatch total = Stopwatch.StartNew();
            UiaResult result = NewResult();
            try
            {
                work.Stage = "ElementFromPoint";
                Stopwatch stage = Stopwatch.StartNew();
                AutomationElement element = AutomationElement.FromPoint(new System.Windows.Point(work.X, work.Y));
                stage.Stop();
                result.ElementMs = stage.ElapsedMilliseconds;
                if (element == null)
                {
                    result.State = "unavailable";
                    result.Reason = "UIA-NOELEMENT";
                    return Finish(result, work.Stage, total);
                }

                work.Stage = "Property";
                stage.Restart();
                result.Name = element.Current.Name;
                result.AutomationId = element.Current.AutomationId;
                result.ControlType = element.Current.ControlType.ProgrammaticName;
                result.ProcessId = element.Current.ProcessId;
                stage.Stop();
                result.PropertyMs = stage.ElapsedMilliseconds;

                work.Stage = "Pattern";
                stage.Restart();
                object patternObject;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObject))
                {
                    ValuePattern pattern = (ValuePattern)patternObject;
                    result.Value = pattern.Current.Value;
                }
                stage.Stop();
                result.PatternMs = stage.ElapsedMilliseconds;
                result.State = "ok";
                result.Reason = String.Empty;
                return Finish(result, work.Stage, total);
            }
            catch (Exception ex)
            {
                result.State = "unavailable";
                result.Reason = ErrorReason(ex);
                return Finish(result, work.Stage, total);
            }
        }

        internal static UiaResult NewResult()
        {
            UiaResult result = new UiaResult();
            result.State = "unavailable";
            result.Reason = String.Empty;
            result.Stage = "Initialization";
            result.ElementMs = -1;
            result.PropertyMs = -1;
            result.PatternMs = -1;
            result.Name = String.Empty;
            result.AutomationId = String.Empty;
            result.ControlType = String.Empty;
            result.Value = String.Empty;
            result.ProcessId = -1;
            return result;
        }

        internal static UiaResult Finish(UiaResult result, string stage, Stopwatch total)
        {
            total.Stop();
            result.Stage = stage;
            result.TotalMs = total.ElapsedMilliseconds;
            return result;
        }

        internal static string ErrorReason(Exception ex)
        {
            return "UIA-FAIL:0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ":" + ex.GetType().Name;
        }
    }

    internal sealed class DisposableManagedStrategy : IProbeStrategy
    {
        private readonly List<Thread> abandoned;
        private readonly List<WorkerStartup> startups;

        internal DisposableManagedStrategy()
        {
            abandoned = new List<Thread>();
            startups = new List<WorkerStartup>();
        }

        public UiaResult Execute(int x, int y, int budgetMs)
        {
            CleanupRecovered();
            ProbeWork work = new ProbeWork(x, y, budgetMs);
            Thread thread = new Thread(delegate()
            {
                work.Result = ManagedUiaProbe.Run(work);
                work.Done.Set();
            });
            thread.IsBackground = true;
            thread.Name = "WP-S A disposable UIA worker";
            thread.SetApartmentState(ApartmentState.MTA);
            Stopwatch wait = Stopwatch.StartNew();
            thread.Start();
            if (!work.Done.Wait(budgetMs))
            {
                wait.Stop();
                lock (abandoned)
                {
                    abandoned.Add(thread);
                }
                return UiaResult.Timeout(work.Stage, wait.ElapsedMilliseconds);
            }
            wait.Stop();
            return work.Result;
        }

        public void PrepareNextIteration(int iteration)
        {
        }

        public void CleanupRecovered()
        {
            lock (abandoned)
            {
                abandoned.RemoveAll(delegate(Thread thread) { return !thread.IsAlive; });
            }
        }

        public int QueueDepth { get { return 0; } }
        public int OrphanThreadCount { get { CleanupRecovered(); lock (abandoned) { return abandoned.Count; } } }
        public int ChildProcessCount { get { return 0; } }
        public int OrphanProcessCount { get { return 0; } }
        public int RestartCount { get { return 0; } }
        public List<WorkerStartup> WorkerStartups { get { return startups; } }
        public void Dispose() { CleanupRecovered(); }
    }

    internal sealed class PersistentInProcessStrategy : IProbeStrategy
    {
        private readonly string kind;
        private readonly int internalTimeoutMs;
        private readonly List<InProcessExecutor> abandoned;
        private readonly List<WorkerStartup> startups;
        private InProcessExecutor current;
        private int restartCount;

        internal PersistentInProcessStrategy(string kindValue, int internalTimeoutMsValue)
        {
            kind = kindValue;
            internalTimeoutMs = internalTimeoutMsValue;
            abandoned = new List<InProcessExecutor>();
            startups = new List<WorkerStartup>();
            current = new InProcessExecutor(kind, internalTimeoutMs);
        }

        public UiaResult Execute(int x, int y, int budgetMs)
        {
            UiaResult result = current.Execute(x, y, budgetMs);
            if (result.Reason == "UIA-TIMEOUT")
            {
                current.Abandon();
                abandoned.Add(current);
                current = new InProcessExecutor(kind, internalTimeoutMs);
                restartCount++;
            }
            return result;
        }

        public void PrepareNextIteration(int iteration)
        {
        }

        public void CleanupRecovered()
        {
            abandoned.RemoveAll(delegate(InProcessExecutor executor)
            {
                if (!executor.IsAlive)
                {
                    executor.Dispose();
                    return true;
                }
                return false;
            });
        }

        public int QueueDepth { get { return current.QueueDepth; } }
        public int OrphanThreadCount { get { CleanupRecovered(); return abandoned.Count(delegate(InProcessExecutor executor) { return executor.IsAlive; }); } }
        public int ChildProcessCount { get { return 0; } }
        public int OrphanProcessCount { get { return 0; } }
        public int RestartCount { get { return restartCount; } }
        public List<WorkerStartup> WorkerStartups { get { return startups; } }

        public void Dispose()
        {
            current.Dispose();
            int i;
            for (i = 0; i < abandoned.Count; i++)
            {
                abandoned[i].Abandon();
            }
        }
    }

    internal sealed class InProcessExecutor : IDisposable
    {
        private readonly string kind;
        private readonly int internalTimeoutMs;
        private readonly AutoResetEvent requestReady;
        private readonly object sync;
        private readonly Thread thread;
        private ProbeWork pending;
        private volatile bool stop;
        private volatile int queueDepth;

        internal InProcessExecutor(string kindValue, int internalTimeoutMsValue)
        {
            kind = kindValue;
            internalTimeoutMs = internalTimeoutMsValue;
            requestReady = new AutoResetEvent(false);
            sync = new object();
            thread = new Thread(WorkerLoop);
            thread.IsBackground = true;
            thread.Name = "WP-S " + kind + " persistent UIA worker";
            thread.SetApartmentState(kind == "B2" ? ApartmentState.STA : ApartmentState.MTA);
            thread.Start();
        }

        internal UiaResult Execute(int x, int y, int budgetMs)
        {
            ProbeWork work = new ProbeWork(x, y, budgetMs);
            lock (sync)
            {
                if (pending != null)
                {
                    throw new InvalidOperationException("In-process queue is not empty.");
                }
                pending = work;
                queueDepth = 1;
            }
            requestReady.Set();
            Stopwatch wait = Stopwatch.StartNew();
            if (!work.Done.Wait(budgetMs))
            {
                wait.Stop();
                return UiaResult.Timeout(work.Stage, wait.ElapsedMilliseconds);
            }
            wait.Stop();
            return work.Result;
        }

        private void WorkerLoop()
        {
            Interop.IUIAutomation2 rawAutomation = null;
            Interop.IUIAutomation rawBaseAutomation = null;
            OleMessageFilter filter = null;
            try
            {
                if (kind == "B")
                {
                    rawAutomation = (Interop.IUIAutomation2)new Interop.CUIAutomation8Class();
                    rawBaseAutomation = (Interop.IUIAutomation)rawAutomation;
                    rawAutomation.AutoSetFocus = 0;
                    rawAutomation.ConnectionTimeout = (uint)internalTimeoutMs;
                    rawAutomation.TransactionTimeout = (uint)internalTimeoutMs;
                }
                else
                {
                    filter = new OleMessageFilter();
                    OleMessageFilter.Register(filter);
                }

                while (!stop)
                {
                    requestReady.WaitOne();
                    ProbeWork work;
                    lock (sync)
                    {
                        work = pending;
                        pending = null;
                        queueDepth = 0;
                    }
                    if (work == null)
                    {
                        continue;
                    }

                    if (filter != null)
                    {
                        filter.SetBudget(work.BudgetMs);
                    }
                    work.Result = kind == "B" ? RawComProbe.Run(work, rawBaseAutomation) : ManagedUiaProbe.Run(work);
                    work.Done.Set();
                }
            }
            catch (Exception ex)
            {
                ProbeWork work;
                lock (sync)
                {
                    work = pending;
                    pending = null;
                    queueDepth = 0;
                }
                if (work != null)
                {
                    UiaResult failed = ManagedUiaProbe.NewResult();
                    failed.Reason = ManagedUiaProbe.ErrorReason(ex);
                    failed.Stage = work.Stage;
                    work.Result = failed;
                    work.Done.Set();
                }
            }
            finally
            {
                if (filter != null)
                {
                    OleMessageFilter.Register(null);
                }
                if (rawAutomation != null && Marshal.IsComObject(rawAutomation))
                {
                    Marshal.FinalReleaseComObject(rawAutomation);
                }
            }
        }

        internal void Abandon()
        {
            stop = true;
            requestReady.Set();
        }

        internal bool IsAlive { get { return thread.IsAlive; } }
        internal int QueueDepth { get { return queueDepth; } }

        public void Dispose()
        {
            stop = true;
            requestReady.Set();
            if (!thread.Join(250))
            {
                return;
            }
            requestReady.Dispose();
        }
    }

    internal static class RawComProbe
    {
        internal static UiaResult Run(ProbeWork work, Interop.IUIAutomation automation)
        {
            Stopwatch total = Stopwatch.StartNew();
            UiaResult result = ManagedUiaProbe.NewResult();
            Interop.IUIAutomationElement element = null;
            Interop.IUIAutomationElement pointElement = null;
            Interop.IUIAutomationCondition condition = null;
            object patternObject = null;
            try
            {
                work.Stage = "ElementFromPoint";
                Stopwatch stage = Stopwatch.StartNew();
                Interop.tagPOINT point = new Interop.tagPOINT();
                point.x = work.X;
                point.y = work.Y;
                pointElement = automation.ElementFromPoint(point);
                element = pointElement;
                if (element != null && !String.Equals(element.CurrentAutomationId, "TargetText", StringComparison.Ordinal))
                {
                    work.Stage = "FindDescendant";
                    condition = automation.CreatePropertyCondition(30011, "TargetText");
                    element = pointElement.FindFirst(Interop.TreeScope.TreeScope_Descendants, condition);
                }
                stage.Stop();
                result.ElementMs = stage.ElapsedMilliseconds;
                if (element == null)
                {
                    result.State = "unavailable";
                    result.Reason = "UIA-NOELEMENT";
                    return ManagedUiaProbe.Finish(result, work.Stage, total);
                }

                work.Stage = "Property";
                stage.Restart();
                result.Name = element.CurrentName;
                result.AutomationId = element.CurrentAutomationId;
                result.ControlType = element.CurrentControlType.ToString(CultureInfo.InvariantCulture);
                result.ProcessId = element.CurrentProcessId;
                stage.Stop();
                result.PropertyMs = stage.ElapsedMilliseconds;

                work.Stage = "Pattern";
                stage.Restart();
                patternObject = element.GetCurrentPattern(10002);
                Interop.IUIAutomationValuePattern valuePattern = patternObject as Interop.IUIAutomationValuePattern;
                if (valuePattern != null)
                {
                    result.Value = valuePattern.CurrentValue;
                }
                stage.Stop();
                result.PatternMs = stage.ElapsedMilliseconds;
                result.State = "ok";
                result.Reason = String.Empty;
                return ManagedUiaProbe.Finish(result, work.Stage, total);
            }
            catch (Exception ex)
            {
                result.State = "unavailable";
                result.Reason = ManagedUiaProbe.ErrorReason(ex);
                return ManagedUiaProbe.Finish(result, work.Stage, total);
            }
            finally
            {
                if (patternObject != null && Marshal.IsComObject(patternObject))
                {
                    Marshal.FinalReleaseComObject(patternObject);
                }
                if (element != null && Marshal.IsComObject(element))
                {
                    Marshal.FinalReleaseComObject(element);
                }
                if (pointElement != null && !Object.ReferenceEquals(pointElement, element) && Marshal.IsComObject(pointElement))
                {
                    Marshal.FinalReleaseComObject(pointElement);
                }
                if (condition != null && Marshal.IsComObject(condition))
                {
                    Marshal.FinalReleaseComObject(condition);
                }
            }
        }
    }

    internal sealed class BApiResult
    {
        internal string Operation;
        internal string State;
        internal string Reason;
        internal long DurationMs;
        internal bool ThreadAliveBeforeRelease;
        internal bool ThreadAliveAfterRelease;
    }

    internal sealed class BApiMatrix : IDisposable
    {
        private readonly List<BApiExecutor> executors;
        private readonly List<BApiResult> results;

        internal BApiMatrix(int x, int y, int internalTimeoutMs)
        {
            executors = new List<BApiExecutor>();
            results = new List<BApiResult>();
            executors.Add(new BApiExecutor("Property", x, y, internalTimeoutMs));
            executors.Add(new BApiExecutor("Pattern", x, y, internalTimeoutMs));
        }

        internal void Execute(int outerTimeoutMs)
        {
            int i;
            for (i = 0; i < executors.Count; i++)
            {
                executors[i].Begin();
            }
            for (i = 0; i < executors.Count; i++)
            {
                BApiResult result = executors[i].Wait(outerTimeoutMs);
                result.ThreadAliveBeforeRelease = executors[i].IsAlive;
                results.Add(result);
            }
        }

        internal void WaitAfterRelease(int timeoutMs)
        {
            Stopwatch wait = Stopwatch.StartNew();
            int i;
            for (i = 0; i < executors.Count; i++)
            {
                int remaining = Math.Max(0, timeoutMs - (int)wait.ElapsedMilliseconds);
                executors[i].Join(remaining);
            }
            for (i = 0; i < results.Count; i++)
            {
                results[i].ThreadAliveAfterRelease = executors[i].IsAlive;
            }
        }

        internal List<BApiResult> Results { get { return results; } }

        public void Dispose()
        {
            int i;
            for (i = 0; i < executors.Count; i++)
            {
                executors[i].Dispose();
            }
        }
    }

    internal sealed class BApiExecutor : IDisposable
    {
        private readonly string operation;
        private readonly int x;
        private readonly int y;
        private readonly int internalTimeoutMs;
        private readonly ManualResetEventSlim initialized;
        private readonly ManualResetEventSlim execute;
        private readonly ManualResetEventSlim complete;
        private readonly Thread thread;
        private BApiResult result;
        private string initializationError;

        internal BApiExecutor(string operationValue, int xValue, int yValue, int internalTimeoutMsValue)
        {
            operation = operationValue;
            x = xValue;
            y = yValue;
            internalTimeoutMs = internalTimeoutMsValue;
            initialized = new ManualResetEventSlim(false);
            execute = new ManualResetEventSlim(false);
            complete = new ManualResetEventSlim(false);
            thread = new Thread(ThreadLoop);
            thread.IsBackground = true;
            thread.Name = "WP-S B API matrix " + operation;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!initialized.Wait(5000))
            {
                throw new TimeoutException("B API matrix initialization timed out for " + operation + ".");
            }
            if (!String.IsNullOrEmpty(initializationError))
            {
                throw new InvalidOperationException("B API matrix initialization failed for " + operation + ": " + initializationError);
            }
        }

        internal void Begin()
        {
            execute.Set();
        }

        internal BApiResult Wait(int timeoutMs)
        {
            if (complete.Wait(timeoutMs))
            {
                return result;
            }
            BApiResult timeout = new BApiResult();
            timeout.Operation = operation;
            timeout.State = "outer-timeout";
            timeout.Reason = "UIA-TIMEOUT";
            timeout.DurationMs = timeoutMs;
            result = timeout;
            return timeout;
        }

        internal bool IsAlive { get { return thread.IsAlive; } }
        internal void Join(int timeoutMs) { thread.Join(timeoutMs); }

        private void ThreadLoop()
        {
            Interop.IUIAutomation2 automation2 = null;
            Interop.IUIAutomation automation = null;
            Interop.IUIAutomationElement pointElement = null;
            Interop.IUIAutomationElement targetElement = null;
            Interop.IUIAutomationCondition condition = null;
            object patternObject = null;
            try
            {
                automation2 = (Interop.IUIAutomation2)new Interop.CUIAutomation8Class();
                automation = (Interop.IUIAutomation)automation2;
                automation2.AutoSetFocus = 0;
                automation2.ConnectionTimeout = (uint)internalTimeoutMs;
                automation2.TransactionTimeout = (uint)internalTimeoutMs;

                Interop.tagPOINT point = new Interop.tagPOINT();
                point.x = x;
                point.y = y;
                pointElement = automation.ElementFromPoint(point);
                if (pointElement != null && String.Equals(pointElement.CurrentAutomationId, "TargetText", StringComparison.Ordinal))
                {
                    targetElement = pointElement;
                }
                else if (pointElement != null)
                {
                    condition = automation.CreatePropertyCondition(30011, "TargetText");
                    targetElement = pointElement.FindFirst(Interop.TreeScope.TreeScope_Descendants, condition);
                }
                if (targetElement == null)
                {
                    initializationError = "UIA-NOELEMENT";
                    return;
                }
                initialized.Set();
                execute.Wait();

                Stopwatch watch = Stopwatch.StartNew();
                BApiResult local = new BApiResult();
                local.Operation = operation;
                try
                {
                    if (operation == "Property")
                    {
                        string name = targetElement.CurrentName;
                        string automationId = targetElement.CurrentAutomationId;
                        if (name == null || automationId == null)
                        {
                            throw new InvalidOperationException("Property values were null.");
                        }
                    }
                    else
                    {
                        patternObject = targetElement.GetCurrentPattern(10002);
                        Interop.IUIAutomationValuePattern pattern = patternObject as Interop.IUIAutomationValuePattern;
                        if (pattern == null)
                        {
                            throw new InvalidOperationException("ValuePattern unavailable.");
                        }
                        string value = pattern.CurrentValue;
                        if (value == null)
                        {
                            throw new InvalidOperationException("Pattern value was null.");
                        }
                    }
                    watch.Stop();
                    local.State = "returned";
                    local.Reason = String.Empty;
                    local.DurationMs = watch.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    watch.Stop();
                    local.State = "error";
                    local.Reason = ManagedUiaProbe.ErrorReason(ex);
                    local.DurationMs = watch.ElapsedMilliseconds;
                }
                result = local;
                complete.Set();
            }
            catch (Exception ex)
            {
                initializationError = ManagedUiaProbe.ErrorReason(ex);
            }
            finally
            {
                initialized.Set();
                complete.Set();
                if (patternObject != null && Marshal.IsComObject(patternObject))
                {
                    Marshal.FinalReleaseComObject(patternObject);
                }
                if (targetElement != null && !Object.ReferenceEquals(targetElement, pointElement) && Marshal.IsComObject(targetElement))
                {
                    Marshal.FinalReleaseComObject(targetElement);
                }
                if (pointElement != null && Marshal.IsComObject(pointElement))
                {
                    Marshal.FinalReleaseComObject(pointElement);
                }
                if (condition != null && Marshal.IsComObject(condition))
                {
                    Marshal.FinalReleaseComObject(condition);
                }
                if (automation2 != null && Marshal.IsComObject(automation2))
                {
                    Marshal.FinalReleaseComObject(automation2);
                }
            }
        }

        public void Dispose()
        {
            execute.Set();
            thread.Join(250);
            initialized.Dispose();
            execute.Dispose();
            complete.Dispose();
        }
    }

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
    }

    internal sealed class OleMessageFilter : IOleMessageFilter
    {
        private Stopwatch budget;
        private int budgetMs;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        internal void SetBudget(int value)
        {
            budgetMs = value;
            budget = Stopwatch.StartNew();
        }

        internal static void Register(IOleMessageFilter filter)
        {
            IOleMessageFilter oldFilter;
            int hr = CoRegisterMessageFilter(filter, out oldFilter);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo)
        {
            return 0;
        }

        public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
        {
            if (rejectType == 2 && budget != null && budget.ElapsedMilliseconds < budgetMs)
            {
                return 50;
            }
            return -1;
        }

        public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType)
        {
            if (budget != null && budget.ElapsedMilliseconds >= budgetMs)
            {
                return 0;
            }
            return 2;
        }
    }

    internal sealed class ChildProcessStrategy : IProbeStrategy
    {
        private readonly string powershellExe;
        private readonly string workerScript;
        private readonly string workerSource;
        private readonly string outputDir;
        private readonly List<ChildWorker> allWorkers;
        private readonly List<ProcessIdentity> killedProcesses;
        private readonly List<WorkerStartup> startups;
        private ChildWorker active;
        private ChildWorker spare;
        private int restartCount;

        internal ChildProcessStrategy(string powershellExeValue, string workerScriptValue, string workerSourceValue, string outputDirValue)
        {
            powershellExe = powershellExeValue;
            workerScript = workerScriptValue;
            workerSource = workerSourceValue;
            outputDir = outputDirValue;
            allWorkers = new List<ChildWorker>();
            killedProcesses = new List<ProcessIdentity>();
            startups = new List<WorkerStartup>();
            active = StartWorker("initial-active");
            spare = StartWorker("initial-spare");
        }

        public UiaResult Execute(int x, int y, int budgetMs)
        {
            if (active == null)
            {
                active = spare != null ? spare : StartWorker("cold-recovery-active");
                spare = null;
            }

            UiaResult result = active.Probe(x, y, budgetMs);
            if (result.Reason == "UIA-TIMEOUT")
            {
                ChildWorker timedOutWorker = active;
                ProcessIdentity killedIdentity = timedOutWorker.Identity;
                timedOutWorker.KillAndWait();
                timedOutWorker.Dispose();
                allWorkers.Remove(timedOutWorker);
                killedProcesses.Add(killedIdentity);
                active = spare;
                spare = null;
                restartCount++;
            }
            return result;
        }

        public void PrepareNextIteration(int iteration)
        {
            if (active == null)
            {
                active = StartWorker("replacement-active-" + iteration.ToString(CultureInfo.InvariantCulture));
            }
            if (spare == null)
            {
                spare = StartWorker("replacement-spare-" + iteration.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void CleanupRecovered()
        {
        }

        private ChildWorker StartWorker(string reason)
        {
            ChildWorker worker = ChildWorker.Start(powershellExe, workerScript, workerSource, outputDir);
            allWorkers.Add(worker);
            WorkerStartup startup = new WorkerStartup();
            startup.Reason = reason;
            startup.Pid = worker.Pid;
            startup.StartupMs = worker.StartupMs;
            startup.Ready = worker.Ready;
            startups.Add(startup);
            return worker;
        }

        public int QueueDepth { get { return active == null ? 0 : active.QueueDepth; } }
        public int OrphanThreadCount { get { return 0; } }
        public int ChildProcessCount { get { return allWorkers.Count(delegate(ChildWorker worker) { return worker.IsAlive; }); } }
        public int OrphanProcessCount
        {
            get
            {
                int count = 0;
                int i;
                for (i = 0; i < killedProcesses.Count; i++)
                {
                    if (ProcessExists(killedProcesses[i]))
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        public int RestartCount { get { return restartCount; } }
        public List<WorkerStartup> WorkerStartups { get { return startups; } }

        public void Dispose()
        {
            int i;
            for (i = 0; i < allWorkers.Count; i++)
            {
                allWorkers[i].Dispose();
            }
        }

        private static bool ProcessExists(ProcessIdentity identity)
        {
            try
            {
                using (Process process = Process.GetProcessById(identity.Pid))
                {
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class ChildWorker : IDisposable
    {
        private readonly Process process;
        private readonly BlockingCollection<string> output;
        private readonly StringBuilder errors;
        private int queueDepth;
        private bool disposed;
        internal readonly long StartupMs;
        internal readonly bool Ready;

        private ChildWorker(Process processValue, BlockingCollection<string> outputValue, StringBuilder errorsValue, long startupMsValue, bool readyValue)
        {
            process = processValue;
            output = outputValue;
            errors = errorsValue;
            StartupMs = startupMsValue;
            Ready = readyValue;
        }

        internal static ChildWorker Start(string powershellExe, string workerScript, string workerSource, string outputDir)
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = powershellExe;
            info.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(workerScript) + " -Source " + Quote(workerSource);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.WorkingDirectory = outputDir;

            BlockingCollection<string> lines = new BlockingCollection<string>();
            StringBuilder errors = new StringBuilder();
            Process process = new Process();
            process.StartInfo = info;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    lines.Add(e.Data);
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    lock (errors)
                    {
                        errors.AppendLine(e.Data);
                    }
                }
            };

            Stopwatch startup = Stopwatch.StartNew();
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            bool ready = false;
            long childReported = -1;
            string line;
            while (startup.ElapsedMilliseconds < 30000)
            {
                if (lines.TryTake(out line, 100))
                {
                    Dictionary<string, object> message;
                    try
                    {
                        message = JsonLine.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }
                    if (JsonLine.String(message, "type") == "ready")
                    {
                        ready = true;
                        childReported = JsonLine.Long(message, "startupMs");
                        break;
                    }
                }
                if (process.HasExited)
                {
                    break;
                }
            }
            startup.Stop();
            if (!ready)
            {
                string errorText;
                lock (errors)
                {
                    errorText = errors.ToString();
                }
                try { process.Kill(); } catch { }
                throw new InvalidOperationException("Child worker failed to become ready. " + errorText);
            }
            return new ChildWorker(process, lines, errors, startup.ElapsedMilliseconds, true);
        }

        internal UiaResult Probe(int x, int y, int budgetMs)
        {
            string requestId = Guid.NewGuid().ToString("N");
            Stopwatch wait = Stopwatch.StartNew();
            string stage = "Queued";
            queueDepth = 1;
            process.StandardInput.WriteLine(JsonLine.ProbeRequest(requestId, x, y));
            process.StandardInput.Flush();
            queueDepth = 0;

            string line;
            while (wait.ElapsedMilliseconds < budgetMs)
            {
                int remaining = Math.Max(1, budgetMs - (int)wait.ElapsedMilliseconds);
                if (!output.TryTake(out line, Math.Min(50, remaining)))
                {
                    if (!IsAlive)
                    {
                        UiaResult dead = ManagedUiaProbe.NewResult();
                        dead.Reason = "UIA-FAIL:WORKER-EXIT";
                        dead.Stage = stage;
                        dead.TotalMs = wait.ElapsedMilliseconds;
                        return dead;
                    }
                    continue;
                }
                Dictionary<string, object> message;
                try
                {
                    message = JsonLine.Parse(line);
                }
                catch
                {
                    continue;
                }
                string messageType = JsonLine.String(message, "type");
                if (messageType == "stage" && JsonLine.String(message, "id") == requestId)
                {
                    stage = JsonLine.String(message, "stage");
                    continue;
                }
                if (messageType == "result" && JsonLine.String(message, "id") == requestId)
                {
                    wait.Stop();
                    return ParseResult(message, stage);
                }
            }
            wait.Stop();
            return UiaResult.Timeout(stage, wait.ElapsedMilliseconds);
        }

        private static UiaResult ParseResult(Dictionary<string, object> message, string stage)
        {
            UiaResult result = ManagedUiaProbe.NewResult();
            result.State = JsonLine.String(message, "state");
            result.Reason = JsonLine.String(message, "reason");
            result.TotalMs = JsonLine.Long(message, "totalMs");
            result.ElementMs = JsonLine.Long(message, "elementMs");
            result.PropertyMs = JsonLine.Long(message, "propertyMs");
            result.PatternMs = JsonLine.Long(message, "patternMs");
            result.ProcessId = (int)JsonLine.Long(message, "processId");
            result.Name = JsonLine.String(message, "name");
            result.AutomationId = JsonLine.String(message, "automationId");
            result.ControlType = JsonLine.String(message, "controlType");
            result.Value = JsonLine.String(message, "value");
            result.Stage = stage;
            return result;
        }

        internal void KillAndWait()
        {
            if (disposed)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    if (process.WaitForExit(5000))
                    {
                        process.WaitForExit();
                    }
                }
            }
            catch
            {
            }
        }

        internal int Pid { get { return process.Id; } }
        internal ProcessIdentity Identity
        {
            get
            {
                ProcessIdentity identity = new ProcessIdentity();
                identity.Pid = process.Id;
                identity.StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                return identity;
            }
        }
        internal int QueueDepth { get { return queueDepth; } }
        internal bool IsAlive
        {
            get
            {
                try { return !process.HasExited; }
                catch { return false; }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("{\"type\":\"exit\"}");
                    process.StandardInput.Flush();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    process.WaitForExit();
                }
            }
            catch
            {
                KillAndWait();
            }
            process.Dispose();
            output.Dispose();
            disposed = true;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

    }

    internal sealed class UiHeartbeat : IDisposable
    {
        private readonly ManualResetEvent ready;
        private readonly Thread uiThread;
        private System.Threading.Timer timer;
        private Form form;
        private long maxDelayMs;
        private volatile bool disposed;

        internal UiHeartbeat()
        {
            ready = new ManualResetEvent(false);
            uiThread = new Thread(UiLoop);
            uiThread.IsBackground = true;
            uiThread.Name = "WP-S UI heartbeat";
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            if (!ready.WaitOne(5000))
            {
                throw new TimeoutException("UI heartbeat did not start.");
            }
            timer = new System.Threading.Timer(Ping, null, 0, 50);
        }

        private void UiLoop()
        {
            form = new Form();
            form.ShowInTaskbar = false;
            form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);
            form.Size = new System.Drawing.Size(1, 1);
            form.Opacity = 0.0;
            form.Shown += delegate { ready.Set(); };
            Application.Run(form);
        }

        private void Ping(object state)
        {
            if (disposed || form == null || !form.IsHandleCreated)
            {
                return;
            }
            long sent = Stopwatch.GetTimestamp();
            try
            {
                form.BeginInvoke((MethodInvoker)delegate
                {
                    long elapsed = (Stopwatch.GetTimestamp() - sent) * 1000 / Stopwatch.Frequency;
                    long current;
                    do
                    {
                        current = Interlocked.Read(ref maxDelayMs);
                        if (elapsed <= current)
                        {
                            return;
                        }
                    }
                    while (Interlocked.CompareExchange(ref maxDelayMs, elapsed, current) != current);
                });
            }
            catch
            {
            }
        }

        internal void ResetMaxDelay()
        {
            Interlocked.Exchange(ref maxDelayMs, 0);
        }

        internal long ConsumeMaxDelay()
        {
            return Interlocked.Exchange(ref maxDelayMs, 0);
        }

        public void Dispose()
        {
            disposed = true;
            if (timer != null)
            {
                timer.Dispose();
            }
            if (form != null && form.IsHandleCreated)
            {
                try { form.BeginInvoke((MethodInvoker)delegate { form.Close(); }); }
                catch { }
            }
            uiThread.Join(1000);
            ready.Dispose();
        }
    }

    internal sealed class Metrics
    {
        internal int Threads;
        internal int Handles;
        internal int ChildProcesses;
        internal int OrphanThreads;
        internal int OrphanProcesses;
        internal long WorkingSet;
        internal int QueueDepth;

        internal static Metrics Capture(IProbeStrategy strategy)
        {
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            Metrics metrics = new Metrics();
            metrics.Threads = process.Threads.Count;
            metrics.Handles = process.HandleCount;
            metrics.ChildProcesses = strategy.ChildProcessCount;
            metrics.OrphanThreads = strategy.OrphanThreadCount;
            metrics.OrphanProcesses = strategy.OrphanProcessCount;
            metrics.WorkingSet = process.WorkingSet64;
            metrics.QueueDepth = strategy.QueueDepth;
            return metrics;
        }
    }

    internal sealed class MeasurementRow
    {
        internal const string Header = "strategy,hang_mode,iteration,request_timeout_ms,tolerance_ms,internal_timeout_ms,temporary_hang_seconds,hang_t_return_ms,hang_outcome,hang_win32_core_state,hang_win32_message_state,hang_win32_ms,hang_uia_state,hang_uia_reason,hang_uia_stage,hang_uia_process_id,hang_uia_name,hang_uia_automation_id,hang_element_ms,hang_property_ms,hang_pattern_ms,healthy_success,healthy_duration_ms,healthy_uia_ms,healthy_uia_process_id,healthy_uia_automation_id,healthy_baseline_median_ms,healthy_ratio,tool_restart_required,worker_restart_count,worker_switched,thread_count,thread_delta,orphan_thread_count,handle_count,handle_delta,child_process_count,process_delta,orphan_process_count,working_set_bytes,working_set_delta_bytes,queue_depth,ui_max_unresponsive_ms,ui_threshold_ms,within_return_limit";
        internal string Strategy;
        internal string HangMode;
        internal int Iteration;
        internal int RequestTimeoutMs;
        internal int ToleranceMs;
        internal int InternalTimeoutMs;
        internal int TemporarySeconds;
        internal long HangReturnMs;
        internal string HangOutcome;
        internal string Win32CoreState;
        internal string Win32MessageState;
        internal long Win32Ms;
        internal string UiaState;
        internal string UiaReason;
        internal string UiaStage;
        internal int UiaProcessId;
        internal string UiaName;
        internal string UiaAutomationId;
        internal long ElementMs;
        internal long PropertyMs;
        internal long PatternMs;
        internal bool HealthySuccess;
        internal long HealthyDurationMs;
        internal long HealthyUiaMs;
        internal int HealthyUiaProcessId;
        internal string HealthyUiaAutomationId;
        internal long HealthyBaselineMedianMs;
        internal double HealthyRatio;
        internal bool ToolRestartRequired;
        internal int WorkerRestartCount;
        internal bool WorkerSwitched;
        internal int ThreadCount;
        internal int ThreadDelta;
        internal int OrphanThreadCount;
        internal int HandleCount;
        internal int HandleDelta;
        internal int ChildProcessCount;
        internal int ProcessDelta;
        internal int OrphanProcessCount;
        internal long WorkingSetBytes;
        internal long WorkingSetDeltaBytes;
        internal int QueueDepth;
        internal long UiMaxMs;
        internal int UiThresholdMs;
        internal bool WithinReturnLimit;

        internal static MeasurementRow Create(string strategy, string hangMode, int iteration, int requestTimeoutMs, int toleranceMs, int internalTimeoutMs, int temporarySeconds, int uiThresholdMs, CompositeResult hung, CompositeResult healthy, long healthyMedian, bool switched, Metrics baseline, Metrics before, Metrics after, long uiMax, IProbeStrategy probeStrategy)
        {
            MeasurementRow row = new MeasurementRow();
            row.Strategy = strategy;
            row.HangMode = hangMode;
            row.Iteration = iteration;
            row.RequestTimeoutMs = requestTimeoutMs;
            row.ToleranceMs = toleranceMs;
            row.InternalTimeoutMs = internalTimeoutMs;
            row.TemporarySeconds = temporarySeconds;
            row.HangReturnMs = hung.TotalMs;
            row.HangOutcome = hung.Outcome;
            row.Win32CoreState = hung.Win32.CoreState;
            row.Win32MessageState = hung.Win32.MessageState;
            row.Win32Ms = hung.Win32.DurationMs;
            row.UiaState = hung.Uia.State;
            row.UiaReason = hung.Uia.Reason;
            row.UiaStage = hung.Uia.Stage;
            row.UiaProcessId = hung.Uia.ProcessId;
            row.UiaName = hung.Uia.Name;
            row.UiaAutomationId = hung.Uia.AutomationId;
            row.ElementMs = hung.Uia.ElementMs;
            row.PropertyMs = hung.Uia.PropertyMs;
            row.PatternMs = hung.Uia.PatternMs;
            row.HealthySuccess = healthy.HealthySuccess;
            row.HealthyDurationMs = healthy.TotalMs;
            row.HealthyUiaMs = healthy.Uia.TotalMs;
            row.HealthyUiaProcessId = healthy.Uia.ProcessId;
            row.HealthyUiaAutomationId = healthy.Uia.AutomationId;
            row.HealthyBaselineMedianMs = healthyMedian;
            row.HealthyRatio = healthyMedian == 0 ? healthy.TotalMs : (double)healthy.TotalMs / healthyMedian;
            row.ToolRestartRequired = false;
            row.WorkerRestartCount = probeStrategy.RestartCount;
            row.WorkerSwitched = switched;
            row.ThreadCount = after.Threads;
            row.ThreadDelta = after.Threads - baseline.Threads;
            row.OrphanThreadCount = after.OrphanThreads;
            row.HandleCount = after.Handles;
            row.HandleDelta = after.Handles - baseline.Handles;
            row.ChildProcessCount = after.ChildProcesses;
            row.ProcessDelta = after.ChildProcesses - baseline.ChildProcesses;
            row.OrphanProcessCount = after.OrphanProcesses;
            row.WorkingSetBytes = after.WorkingSet;
            row.WorkingSetDeltaBytes = after.WorkingSet - baseline.WorkingSet;
            row.QueueDepth = after.QueueDepth;
            row.UiMaxMs = uiMax;
            row.UiThresholdMs = uiThresholdMs;
            row.WithinReturnLimit = hung.TotalMs <= requestTimeoutMs + toleranceMs;
            return row;
        }

        internal string ToCsv()
        {
            return String.Join(",", new string[]
            {
                Csv.Value(Strategy), Csv.Value(HangMode), Iteration.ToString(CultureInfo.InvariantCulture),
                RequestTimeoutMs.ToString(CultureInfo.InvariantCulture), ToleranceMs.ToString(CultureInfo.InvariantCulture),
                InternalTimeoutMs.ToString(CultureInfo.InvariantCulture), TemporarySeconds.ToString(CultureInfo.InvariantCulture),
                HangReturnMs.ToString(CultureInfo.InvariantCulture), Csv.Value(HangOutcome), Csv.Value(Win32CoreState),
                Csv.Value(Win32MessageState), Win32Ms.ToString(CultureInfo.InvariantCulture), Csv.Value(UiaState),
                Csv.Value(UiaReason), Csv.Value(UiaStage), UiaProcessId.ToString(CultureInfo.InvariantCulture),
                Csv.Value(UiaName), Csv.Value(UiaAutomationId), ElementMs.ToString(CultureInfo.InvariantCulture),
                PropertyMs.ToString(CultureInfo.InvariantCulture), PatternMs.ToString(CultureInfo.InvariantCulture),
                Csv.Bool(HealthySuccess), HealthyDurationMs.ToString(CultureInfo.InvariantCulture),
                HealthyUiaMs.ToString(CultureInfo.InvariantCulture), HealthyUiaProcessId.ToString(CultureInfo.InvariantCulture),
                Csv.Value(HealthyUiaAutomationId), HealthyBaselineMedianMs.ToString(CultureInfo.InvariantCulture),
                HealthyRatio.ToString("0.000", CultureInfo.InvariantCulture), Csv.Bool(ToolRestartRequired),
                WorkerRestartCount.ToString(CultureInfo.InvariantCulture), Csv.Bool(WorkerSwitched),
                ThreadCount.ToString(CultureInfo.InvariantCulture), ThreadDelta.ToString(CultureInfo.InvariantCulture),
                OrphanThreadCount.ToString(CultureInfo.InvariantCulture), HandleCount.ToString(CultureInfo.InvariantCulture),
                HandleDelta.ToString(CultureInfo.InvariantCulture), ChildProcessCount.ToString(CultureInfo.InvariantCulture),
                ProcessDelta.ToString(CultureInfo.InvariantCulture), OrphanProcessCount.ToString(CultureInfo.InvariantCulture),
                WorkingSetBytes.ToString(CultureInfo.InvariantCulture), WorkingSetDeltaBytes.ToString(CultureInfo.InvariantCulture),
                QueueDepth.ToString(CultureInfo.InvariantCulture), UiMaxMs.ToString(CultureInfo.InvariantCulture),
                UiThresholdMs.ToString(CultureInfo.InvariantCulture), Csv.Bool(WithinReturnLimit)
            });
        }
    }

    internal static class Csv
    {
        internal static string Value(string value)
        {
            string safe = value ?? String.Empty;
            if (safe.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + safe.Replace("\"", "\"\"") + "\"";
            }
            return safe;
        }

        internal static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }

    internal static class JsonLine
    {
        internal static Dictionary<string, object> Parse(string line)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            return serializer.Deserialize<Dictionary<string, object>>(line);
        }

        internal static string String(Dictionary<string, object> value, string key)
        {
            object item;
            if (!value.TryGetValue(key, out item) || item == null)
            {
                return System.String.Empty;
            }
            return Convert.ToString(item, CultureInfo.InvariantCulture);
        }

        internal static long Long(Dictionary<string, object> value, string key)
        {
            object item;
            if (!value.TryGetValue(key, out item) || item == null)
            {
                return -1;
            }
            return Convert.ToInt64(item, CultureInfo.InvariantCulture);
        }

        internal static string ProbeRequest(string requestId, int x, int y)
        {
            return "{\"type\":\"probe\",\"id\":\"" + requestId + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }
}
