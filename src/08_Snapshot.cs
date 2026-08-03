namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    public sealed class StatusReason
    {
        public string Code;
        public string Message;

        public StatusReason(string code, string message)
        {
            Code = code;
            Message = message;
        }
    }

    public sealed class ProbeStatus
    {
        public string State;
        public List<StatusReason> Reasons = new List<StatusReason>();

        public static ProbeStatus Ok()
        {
            ProbeStatus status = new ProbeStatus();
            status.State = "ok";
            return status;
        }

        public static ProbeStatus Unavailable(string code, string message)
        {
            ProbeStatus status = new ProbeStatus();
            status.State = "unavailable";
            status.Reasons.Add(new StatusReason(code, message));
            return status;
        }

        public void AddPartial(string code, string message)
        {
            if (State == "ok")
            {
                State = "partial";
            }
            Reasons.Add(new StatusReason(code, message));
        }
    }

    public sealed class RectValue
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    public sealed class Win32Ancestor
    {
        public long Hwnd;
        public string ClassName;
        public string Caption;
        public int CtrlId;
    }

    public sealed class Win32Info
    {
        public long Hwnd;
        public int ProcessId;
        public int ThreadId;
        public string ClassName;
        public string RealClass;
        public string Caption;
        public string CaptionSource;
        public int CtrlId;
        public long Style;
        public long ExStyle;
        public RectValue WindowRect;
        public RectValue ClientRect;
        public bool Visible;
        public bool Enabled;
        public int ZIndex;
        public string MonitorId;
        public int Dpi;
        public long TopHwnd;
        public string TopCaption;
        public string TopClass;
        public RectValue TopRect;
        public List<Win32Ancestor> Ancestors = new List<Win32Ancestor>();
        public ProbeStatus Status;
    }

    public sealed class MsaaInfo
    {
        public string Role;
        public string Name;
        public string Value;
        public long State;
        public string StateText;
        public int ChildId;
        public long Hwnd;
        public RectValue Rect;
        public ProbeStatus Status;
    }

    public sealed class UiaInfo
    {
        public string Name;
        public string AutomationId;
        public string ControlType;
        public string LocalizedControlType;
        public string ClassName;
        public string FrameworkId;
        public bool IsEnabled;
        public bool IsOffscreen;
        public bool IsKeyboardFocusable;
        public bool IsPassword;
        public string HelpText;
        public string AcceleratorKey;
        public string AccessKey;
        public int[] RuntimeId;
        public RectValue BoundingRect;
        public int NativeWindowHandle;
        public string[] SupportedPatterns;
        public UiaNode[] TreePath;
        public UiaNode[] Children;
        public string LiveValue;
        public bool RawRefined;
        public ProbeStatus Status;
    }

    public sealed class UiaNode
    {
        public string ControlType;
        public string Name;
        public string AutomationId;
        public int IndexAmongSameType;
        public int SiblingCount;
    }

    public sealed class ElementRef
    {
        public int X;
        public int Y;
        public long Hwnd;

        public static ElementRef FromSnapshot(Snapshot snapshot)
        {
            RectValue rect = snapshot != null && snapshot.Uia != null && snapshot.Uia.BoundingRect != null ? snapshot.Uia.BoundingRect : (snapshot == null || snapshot.Win32 == null ? null : snapshot.Win32.WindowRect);
            if (rect == null) return null;
            ElementRef value = new ElementRef();
            value.X = rect.X + rect.Width / 2;
            value.Y = rect.Y + rect.Height / 2;
            value.Hwnd = snapshot.Win32 == null ? 0 : snapshot.Win32.Hwnd;
            return value;
        }
    }

    public sealed class AcquisitionHealth
    {
        public string State;
        public int RestartCount;
        public int PromotionCount;
        public int QueueDepth;
        public int ActiveProcessId;
        public int SpareProcessId;
        public bool ActiveWarmupPerformed;
        public bool SpareWarmupPerformed;
        public int OrphanProcessCount;
    }

    public sealed class Snapshot
    {
        public Win32Info Win32;
        public UiaInfo Uia;
        public MsaaInfo Msaa;
        public DateTime CapturedAt;
        public int DurationMs;
        public ProbeStatus Win32Status;
        public ProbeStatus UiaStatus;
        public ProbeStatus MsaaStatus;
    }

    public sealed class WorkerSnapshot
    {
        public UiaInfo Uia;
        public MsaaInfo Msaa;
    }

    public sealed class AcquisitionView
    {
        public string Route;
        public string Level;
        public RectValue Rect;
        public string Title;
        public bool IsSelf;
        public bool IsShellSurface;
        public int ProcessId;
        public string TopCaption;
    }

    public static class SnapshotAnalysis
    {
        public static AcquisitionView Analyze(Snapshot snapshot, int px, int py, int ownProcessId)
        {
            AcquisitionView view = new AcquisitionView();
            Win32Info win32 = snapshot == null ? null : snapshot.Win32;
            view.ProcessId = win32 == null ? 0 : win32.ProcessId;
            view.IsSelf = ownProcessId != 0 && view.ProcessId == ownProcessId;
            view.IsShellSurface = IsDesktopShell(win32);
            view.TopCaption = win32 == null ? null : (String.IsNullOrEmpty(win32.TopCaption) ? win32.Caption : win32.TopCaption);

            RectValue uiaRect = null;
            if (snapshot != null && snapshot.Uia != null && snapshot.Uia.Status != null && snapshot.Uia.Status.State != "unavailable" && ContainsPoint(snapshot.Uia.BoundingRect, px, py))
                uiaRect = snapshot.Uia.BoundingRect;
            RectValue msaaRect = null;
            if (snapshot != null && snapshot.Msaa != null && snapshot.Msaa.Status != null && snapshot.Msaa.Status.State != "unavailable" && ContainsPoint(snapshot.Msaa.Rect, px, py))
                msaaRect = snapshot.Msaa.Rect;
            RectValue winRect = null;
            if (win32 != null && win32.Status != null && win32.Status.State != "unavailable" && ContainsPoint(win32.WindowRect, px, py))
                winRect = win32.WindowRect;

            view.Route = "none";
            view.Rect = null;
            Consider(view, uiaRect, "uia");
            Consider(view, msaaRect, "msaa");
            Consider(view, winRect, win32 != null && win32.Hwnd != win32.TopHwnd ? "win32-child" : "win32-window");
            if (view.Rect == null && win32 != null && win32.TopRect != null)
            {
                view.Rect = win32.TopRect;
                view.Route = "win32-window";
            }

            RectValue top = win32 == null ? null : (win32.TopRect != null ? win32.TopRect : win32.WindowRect);
            view.Level = "window";
            if (view.Rect != null && top != null)
            {
                long chosenArea = (long)view.Rect.Width * view.Rect.Height;
                long topArea = Math.Max(1L, (long)top.Width * top.Height);
                if (chosenArea > 0 && chosenArea < topArea * 8 / 10) view.Level = "element";
            }

            view.Title = BuildTitle(snapshot);
            return view;
        }

        private static void Consider(AcquisitionView view, RectValue candidate, string route)
        {
            if (candidate == null || candidate.Width <= 0 || candidate.Height <= 0) return;
            if (view.Rect == null)
            {
                view.Rect = candidate;
                view.Route = route;
                return;
            }
            long current = (long)view.Rect.Width * view.Rect.Height;
            long offered = (long)candidate.Width * candidate.Height;
            if (offered < current)
            {
                view.Rect = candidate;
                view.Route = route;
            }
        }

        private static bool ContainsPoint(RectValue rect, int px, int py)
        {
            return rect != null && rect.Width > 0 && rect.Height > 0 &&
                px >= rect.X && py >= rect.Y && px < rect.X + rect.Width && py < rect.Y + rect.Height;
        }

        private static bool IsDesktopShell(Win32Info win32)
        {
            if (win32 == null) return false;
            string top = win32.TopClass;
            return String.Equals(top, "Progman", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(top, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(top, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(top, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildTitle(Snapshot snapshot)
        {
            if (snapshot == null) return null;
            string type = snapshot.Uia == null ? null : snapshot.Uia.ControlType;
            string name = snapshot.Uia == null ? null : snapshot.Uia.Name;
            if (!String.IsNullOrEmpty(type) || !String.IsNullOrEmpty(name))
                return (String.IsNullOrEmpty(type) ? "?" : type) + "  " + (String.IsNullOrEmpty(name) ? "-" : name);
            if (snapshot.Msaa != null && !String.IsNullOrEmpty(snapshot.Msaa.Role))
                return snapshot.Msaa.Role + "  " + (String.IsNullOrEmpty(snapshot.Msaa.Name) ? "-" : snapshot.Msaa.Name);
            if (snapshot.Win32 != null)
                return snapshot.Win32.ClassName + "  " + (String.IsNullOrEmpty(snapshot.Win32.Caption) ? "-" : snapshot.Win32.Caption);
            return null;
        }
    }

    public static class Probe
    {
        private static readonly object Sync = new object();
        private static AcquisitionWorkerManager manager;
        private static bool uiaDisabled;

        public static void Configure(string baseDir, bool disableUia)
        {
            lock (Sync)
            {
                if (manager != null)
                {
                    manager.Dispose();
                    manager = null;
                }
                uiaDisabled = disableUia;
                if (!disableUia)
                {
                    manager = new AcquisitionWorkerManager(baseDir);
                }
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (manager != null)
                {
                    manager.Dispose();
                    manager = null;
                }
            }
        }

        public static AcquisitionHealth GetHealth()
        {
            lock (Sync)
            {
                if (manager == null)
                {
                    AcquisitionHealth disabled = new AcquisitionHealth();
                    disabled.State = uiaDisabled ? "disabled" : "not-configured";
                    return disabled;
                }
                return manager.GetHealth();
            }
        }

        internal static UiaActionResult RunUiaOperation(ElementRef element, string kind, string value, int budgetMs)
        {
            if (element == null) throw new ArgumentNullException("element");
            lock (Sync)
            {
                if (manager == null)
                {
                    UiaActionResult unavailable = new UiaActionResult();
                    unavailable.Outcome = "failed";
                    unavailable.Method = "uia.worker";
                    unavailable.ErrorCode = "UIA-INIT";
                    unavailable.ErrorMessage = "The acquisition worker is unavailable.";
                    return unavailable;
                }
                return manager.RunOperation(element.X, element.Y, kind, value, budgetMs);
            }
        }

        public static Snapshot At(int px, int py, int budgetMs)
        {
            return Acquire(px, py, budgetMs, false, Math.Min(budgetMs, 60));
        }

        public static Snapshot Deep(ElementRef element, int budgetMs)
        {
            if (element == null) throw new ArgumentNullException("element");
            if (element.Hwnd == 0) return Acquire(element.X, element.Y, budgetMs, true, Math.Min(budgetMs, 150));
            Stopwatch watch = Stopwatch.StartNew();
            Win32Info win32 = Win32Probe.AtHandle(new IntPtr(element.Hwnd), Math.Min(150, budgetMs));
            WorkerSnapshot worker = ProbeWorker(element.X, element.Y, Math.Max(1, budgetMs - (int)watch.ElapsedMilliseconds), true);
            return Compose(win32, worker, watch);
        }

        private static Snapshot Acquire(int px, int py, int budgetMs, bool deep, int win32TimeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Win32Info win32 = Win32Probe.AtPoint(px, py, win32TimeoutMs);
            WorkerSnapshot worker = ProbeWorker(px, py, Math.Max(1, budgetMs - (int)watch.ElapsedMilliseconds), deep);
            return Compose(win32, worker, watch);
        }

        private static WorkerSnapshot ProbeWorker(int px, int py, int timeoutMs, bool deep)
        {
            lock (Sync)
            {
                if (manager == null) return null;
                return manager.ProbeAt(px, py, timeoutMs, deep);
            }
        }

        private static Snapshot Compose(Win32Info win32, WorkerSnapshot worker, Stopwatch watch)
        {
            UiaInfo uia = worker == null ? null : worker.Uia;
            MsaaInfo msaa = worker == null ? null : worker.Msaa;
            if (uia == null)
            {
                uia = new UiaInfo();
                uia.Status = ProbeStatus.Unavailable("UIA-INIT", uiaDisabled ? "UI Automation was deliberately disabled for degraded-mode verification." : "The acquisition worker is not configured.");
            }
            if (msaa == null)
            {
                msaa = new MsaaInfo();
                msaa.Status = ProbeStatus.Unavailable("MSAA-INIT", uiaDisabled ? "The acquisition worker was deliberately disabled for degraded-mode verification." : "The acquisition worker is not configured.");
            }
            Snapshot snapshot = new Snapshot();
            snapshot.Win32 = win32;
            snapshot.Uia = uia;
            snapshot.Msaa = msaa;
            snapshot.CapturedAt = DateTime.Now;
            snapshot.DurationMs = (int)watch.ElapsedMilliseconds;
            snapshot.Win32Status = win32.Status;
            snapshot.UiaStatus = uia.Status;
            snapshot.MsaaStatus = msaa.Status;
            return snapshot;
        }
    }

    public static class SnapshotJson
    {
        public static JsonObject Build(Snapshot snapshot)
        {
            return new JsonObject()
                .Add("capturedAt", new DateTimeOffset(snapshot.CapturedAt))
                .Add("durationMs", snapshot.DurationMs)
                .Add("win32", BuildWin32(snapshot.Win32))
                .Add("uia", BuildUia(snapshot.Uia))
                .Add("msaa", BuildMsaa(snapshot.Msaa));
        }

        public static void WriteDegradedEvidence(Snapshot snapshot, string path)
        {
            JsonObject evidence = new JsonObject()
                .Add("kind", "degraded-snapshot")
                .Add("fixed", true)
                .Add("recorded", true)
                .Add("snapshot", Build(snapshot));
            JsonWriter.WriteFile(path, evidence);
        }

        private static JsonObject BuildWin32(Win32Info info)
        {
            if (info == null)
            {
                return new JsonObject().Add("status", BuildStatus(ProbeStatus.Unavailable("WIN32-NOHWND", "No Win32 result.")));
            }
            return new JsonObject()
                .Add("hwnd", info.Hwnd)
                .Add("processId", info.ProcessId)
                .Add("threadId", info.ThreadId)
                .Add("class", info.ClassName)
                .Add("realClass", info.RealClass)
                .Add("caption", info.Caption)
                .Add("captionSource", info.CaptionSource)
                .Add("ctrlId", info.CtrlId)
                .Add("windowRect", BuildRect(info.WindowRect))
                .Add("status", BuildStatus(info.Status));
        }

        private static JsonObject BuildUia(UiaInfo info)
        {
            if (info == null)
            {
                return new JsonObject().Add("status", BuildStatus(ProbeStatus.Unavailable("UIA-INIT", "No UIA result.")));
            }
            return new JsonObject()
                .Add("name", info.Name)
                .Add("automationId", info.AutomationId)
                .Add("controlType", info.ControlType)
                .Add("frameworkId", info.FrameworkId)
                .Add("status", BuildStatus(info.Status));
        }

        private static JsonObject BuildMsaa(MsaaInfo info)
        {
            if (info == null)
            {
                return new JsonObject().Add("status", BuildStatus(ProbeStatus.Unavailable("MSAA-INIT", "No MSAA result.")));
            }
            return new JsonObject()
                .Add("role", info.Role)
                .Add("name", info.Name)
                .Add("stateText", info.StateText)
                .Add("childId", info.ChildId)
                .Add("rect", BuildRect(info.Rect))
                .Add("status", BuildStatus(info.Status));
        }

        private static object BuildRect(RectValue rect)
        {
            if (rect == null)
            {
                return null;
            }
            return new JsonObject().Add("x", rect.X).Add("y", rect.Y).Add("width", rect.Width).Add("height", rect.Height);
        }

        private static JsonObject BuildStatus(ProbeStatus status)
        {
            List<object> reasons = new List<object>();
            if (status != null)
            {
                for (int index = 0; index < status.Reasons.Count; index++)
                {
                    reasons.Add(new JsonObject().Add("code", status.Reasons[index].Code).Add("message", status.Reasons[index].Message));
                }
            }
            return new JsonObject().Add("state", status == null ? "unavailable" : status.State).Add("reasons", reasons.ToArray());
        }
    }
}
