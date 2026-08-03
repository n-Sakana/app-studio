namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public sealed class PackResult
    {
        public string Folder;
        public string[] Files;
        public ProbeStatus Status;
    }

    public static class SessionSchema
    {
        public static JsonObject Build(Session session)
        {
            if (session == null) throw new ArgumentNullException("session");
            List<object> elements = new List<object>();
            for (int index = 0; index < session.Elements.Count; index++) elements.Add(Element(session, session.Elements[index], index));
            List<object> events = new List<object>();
            for (int index = 0; index < session.Events.Count; index++) events.Add(Event(session.Events[index]));
            List<object> failures = new List<object>();
            for (int index = 0; index < session.AcquisitionFailures.Count; index++) failures.Add(Failure(session.AcquisitionFailures[index]));
            List<object> shots = new List<object>();
            for (int index = 0; index < session.Shots.Count; index++) shots.Add(Shot(session.Shots[index]));
            return new JsonObject()
                .Add("schemaVersion", 1)
                .Add("tool", new JsonObject().Add("name", "App Studio").Add("version", "0.1.0").Add("buildId", "source-addtype").Add("exeSha256", Diagnostics.Unknown("The text-only Add-Type distribution has no tool executable.")))
                .Add("session", new JsonObject().Add("id", session.Id).Add("label", session.Label).Add("startedAt", session.StartedAt).Add("endedAt", session.EndedAt).Add("mode", session.Mode).Add("operatorNote", session.OperatorNote))
                .Add("environment", Environment(session))
                .Add("policy", Policy(session))
                .Add("targets", Targets(session))
                .Add("elements", elements.ToArray())
                .Add("events", events.ToArray())
                .Add("shots", shots.ToArray())
                .Add("acquisitionFailures", failures.ToArray());
        }

        private static JsonObject Policy(Session session)
        {
            List<object> masks = new List<object>();
            for (int index = 0; index < session.Masking.Count; index++)
            {
                MaskRule rule = session.Masking[index];
                masks.Add(new JsonObject().Add("ruleId", rule.RuleId).Add("kind", rule.Kind).Add("pattern", rule.Pattern).Add("appliesTo", rule.AppliesTo));
            }
            bool fullShot = false;
            for (int index = 0; index < session.Shots.Count; index++) if (session.Shots[index].Kind == "full") fullShot = true;
            return new JsonObject()
                .Add("valueCapture", session.ValueCapture)
                .Add("valueCaptureChangedAt", session.ValueCaptureChangedAt)
                .Add("valueCaptureReason", session.ValueCaptureReason)
                .Add("masking", masks.ToArray())
                .Add("screenshot", new JsonObject().Add("full", fullShot).Add("crop", true).Add("maskPasswordRects", true));
        }

        private static object Environment(Session session)
        {
            if (session.Environment == null) return Diagnostics.Unknown("Environment collection was unavailable.");
            JsonObject result = new JsonObject();
            List<object> targets = new List<object>();
            foreach (KeyValuePair<string, object> item in session.Environment)
            {
                if (item.Key != "writeTargets") result.Add(item.Key, item.Value);
                else
                {
                    System.Collections.IEnumerable existing = item.Value as System.Collections.IEnumerable;
                    if (existing != null) foreach (object value in existing) targets.Add(value);
                }
            }
            for (int index = 0; index < session.WriteTargets.Count; index++) targets.Add(new JsonObject().Add("path", session.WriteTargets[index].Path).Add("purpose", session.WriteTargets[index].Purpose));
            result.Add("writeTargets", targets.ToArray());
            return result;
        }

        private static object[] Targets(Session session)
        {
            List<object> targets = new List<object>();
            List<int> processIds = new List<int>();
            for (int index = 0; index < session.Elements.Count; index++)
            {
                int pid = session.Elements[index].Win32 == null ? 0 : session.Elements[index].Win32.ProcessId;
                if (!processIds.Contains(pid)) processIds.Add(pid);
            }
            for (int index = 0; index < processIds.Count; index++)
            {
                ElementRecord representative = null;
                for (int elementIndex = 0; elementIndex < session.Elements.Count; elementIndex++)
                {
                    int pid = session.Elements[elementIndex].Win32 == null ? 0 : session.Elements[elementIndex].Win32.ProcessId;
                    if (pid == processIds[index]) { representative = session.Elements[elementIndex]; break; }
                }
                Win32Info win32 = representative == null ? null : representative.Win32;
                UiaInfo uia = representative == null ? null : representative.Uia;
                string targetId = "tg-" + (index + 1).ToString("00", CultureInfo.InvariantCulture);
                string runId = "run-" + (index + 1).ToString("00", CultureInfo.InvariantCulture);
                JsonObject mainWindow = new JsonObject().Add("hwnd", TopHandle(win32)).Add("class", TopClass(win32)).Add("caption", TopCaption(win32)).Add("rect", Rect(win32 == null ? null : win32.WindowRect));
                object processName = processIds[index] == 0 ? (object)Diagnostics.Unknown("The target process ID was unavailable.") : "pid-" + processIds[index].ToString(CultureInfo.InvariantCulture);
                JsonObject process = new JsonObject()
                    .Add("name", processName)
                    .Add("path", Diagnostics.Unknown("The target executable path was not collected."))
                    .Add("fileVersion", Diagnostics.Unknown("The target file version was not collected."))
                    .Add("productVersion", Diagnostics.Unknown("The target product version was not collected."))
                    .Add("bitness", Diagnostics.Unknown("The target process bitness was not collected."))
                    .Add("elevated", Diagnostics.Unknown("The target elevation state was not collected."))
                    .Add("integrityLevel", Diagnostics.Unknown("The target integrity level was not collected."));
                targets.Add(new JsonObject()
                    .Add("targetId", targetId)
                    .Add("attachedAt", representative == null ? session.StartedAt : representative.PinnedAt)
                    .Add("runs", new object[] { new JsonObject().Add("targetRunId", runId).Add("startedAt", representative == null ? session.StartedAt : representative.PinnedAt).Add("pid", processIds[index]).Add("mainWindow", mainWindow) })
                    .Add("process", process)
                    .Add("frameworkFamily", uia == null || String.IsNullOrWhiteSpace(uia.FrameworkId) ? (object)Diagnostics.Unknown("UI Automation did not report a framework family.") : uia.FrameworkId)
                    .Add("automationHint", AutomationHint(win32, uia))
                    .Add("status", win32 == null ? Status(ProbeStatus.Unavailable("WIN32-NOHWND", "No target Win32 information was recorded.")) : Status(win32.Status)));
            }
            return targets.ToArray();
        }

        private static JsonObject Element(Session session, ElementRecord record, int index)
        {
            List<object> notes = new List<object>();
            for (int noteIndex = 0; noteIndex < record.Notes.Count; noteIndex++) notes.Add(new JsonObject().Add("at", record.PinnedAt).Add("text", record.Notes[noteIndex]));
            List<object> locators = new List<object>();
            for (int locatorIndex = 0; locatorIndex < record.Locators.Count; locatorIndex++) locators.Add(LocatorJson.Build(record.Locators[locatorIndex]));
            List<object> probes = new List<object>();
            for (int probeIndex = 0; probeIndex < record.Probes.Count; probeIndex++) probes.Add(Probe(session, record, record.Probes[probeIndex]));
            List<object> shots = new List<object>();
            for (int shotIndex = 0; shotIndex < record.Shots.Count; shotIndex++) shots.Add(new JsonObject().Add("shotId", record.Shots[shotIndex].ShotId).Add("kind", record.Shots[shotIndex].Kind));
            string targetId = TargetId(session, record);
            return new JsonObject()
                .Add("elementId", record.ElementId)
                .Add("pinnedAt", record.PinnedAt)
                .Add("targetId", targetId)
                .Add("targetRunId", "run-" + TargetOrdinal(session, record).ToString("00", CultureInfo.InvariantCulture))
                .Add("label", record.Label)
                .Add("notes", notes.ToArray())
                .Add("win32", Win32(session, record))
                .Add("uia", Uia(record.Uia, record.RecordedValue))
                .Add("locators", locators.ToArray())
                .Add("probes", probes.ToArray())
                .Add("shots", shots.ToArray())
                .Add("acquisitionSummary", new JsonObject().Add("win32", record.Win32 == null ? "unavailable" : record.Win32.Status.State).Add("uia", record.Uia == null ? "unavailable" : record.Uia.Status.State).Add("capture", CaptureState(record)));
        }

        private static JsonObject Win32(Session session, ElementRecord record)
        {
            Win32Info info = record == null ? null : record.Win32;
            if (info == null) return new JsonObject().Add("status", Status(ProbeStatus.Unavailable("WIN32-NOHWND", "No Win32 information was recorded.")));
            List<object> ancestors = new List<object>();
            for (int index = 0; index < info.Ancestors.Count; index++) ancestors.Add(new JsonObject().Add("hwnd", info.Ancestors[index].Hwnd).Add("class", info.Ancestors[index].ClassName).Add("caption", info.Ancestors[index].Caption).Add("ctrlId", info.Ancestors[index].CtrlId));
            return new JsonObject()
                .Add("hwnd", info.Hwnd).Add("class", info.ClassName).Add("realClass", info.RealClass).Add("caption", PersistentCaption(session, record)).Add("captionSource", info.CaptionSource)
                .Add("ctrlId", info.CtrlId).Add("style", info.Style).Add("exStyle", info.ExStyle).Add("windowRect", Rect(info.WindowRect)).Add("clientRect", Rect(info.ClientRect))
                .Add("visible", info.Visible).Add("enabled", info.Enabled).Add("zIndex", info.ZIndex).Add("threadId", info.ThreadId)
                .Add("monitorId", info.MonitorId == null ? (object)Diagnostics.Unknown("The element monitor identifier was unavailable.") : info.MonitorId)
                .Add("dpi", info.Dpi <= 0 ? (object)Diagnostics.Unknown("The element DPI was unavailable.") : info.Dpi)
                .Add("ancestors", ancestors.ToArray()).Add("childCount", Diagnostics.Unknown("Win32 child count was not collected."))
                .Add("status", Status(info.Status));
        }

        private static JsonObject Uia(UiaInfo info, RecordedValue recorded)
        {
            if (info == null) return new JsonObject().Add("status", Status(ProbeStatus.Unavailable("UIA-NOELEMENT", "No UI Automation information was recorded.")));
            List<object> tree = new List<object>();
            if (info.TreePath != null) for (int index = 0; index < info.TreePath.Length; index++) tree.Add(Node(info.TreePath[index]));
            List<object> children = new List<object>();
            if (info.Children != null) for (int index = 0; index < info.Children.Length; index++) children.Add(new JsonObject().Add("controlType", info.Children[index].ControlType).Add("name", info.Children[index].Name).Add("automationId", info.Children[index].AutomationId));
            JsonObject recordedValue = recorded == null ? new JsonObject().Add("length", 0).Add("kind", "none").Add("masked", false).Add("maskRule", null) : new JsonObject().Add("length", recorded.Length).Add("kind", recorded.Kind).Add("masked", recorded.Masked).Add("maskRule", recorded.MaskRule).Add("content", recorded.Content);
            return new JsonObject()
                .Add("name", info.Name).Add("automationId", info.AutomationId).Add("controlType", info.ControlType).Add("localizedControlType", info.LocalizedControlType).Add("className", info.ClassName).Add("frameworkId", info.FrameworkId)
                .Add("isEnabled", info.IsEnabled).Add("isOffscreen", info.IsOffscreen).Add("isKeyboardFocusable", info.IsKeyboardFocusable).Add("isPassword", info.IsPassword).Add("helpText", info.HelpText)
                .Add("acceleratorKey", info.AcceleratorKey).Add("accessKey", info.AccessKey).Add("runtimeId", info.RuntimeId).Add("boundingRect", Rect(info.BoundingRect)).Add("nativeWindowHandle", info.NativeWindowHandle)
                .Add("patterns", new JsonObject().Add("supported", info.SupportedPatterns == null ? new string[0] : info.SupportedPatterns).Add("values", new JsonObject().Add("value", recordedValue)))
                .Add("treePath", tree.ToArray()).Add("children", children.ToArray()).Add("status", Status(info.Status));
        }

        private static JsonObject Probe(Session session, ElementRecord record, ProbeResult probe)
        {
            List<object> sideEffects = new List<object>();
            for (int index = 0; index < probe.SideEffects.Count; index++) sideEffects.Add(new JsonObject().Add("type", probe.SideEffects[index].Type).Add("detail", probe.SideEffects[index].Detail));
            bool allowValue = session.ValueCapture == "full" && LiveValuePresenter.MaskReason(record.Uia, session.Masking, record.ElementId) == null;
            return new JsonObject()
                .Add("probeId", probe.ProbeId).Add("elementId", probe.ElementId).Add("kind", probe.Kind.ToString().ToLowerInvariant()).Add("requestedAt", probe.RequestedAt)
                .Add("method", probe.Method).Add("outcome", probe.Outcome).Add("durationMs", probe.DurationMs)
                .Add("error", probe.Error == null ? null : new JsonObject().Add("code", probe.Error.Code).Add("hresult", probe.Error.Hresult).Add("message", probe.Error.Message))
                .Add("before", Observation(probe.Before, allowValue)).Add("after", Observation(probe.After, allowValue)).Add("sideEffects", sideEffects.ToArray())
                .Add("shots", new JsonObject().Add("before", null).Add("after", null))
                .Add("undo", probe.Undo == null ? new JsonObject().Add("available", false).Add("performedAt", null) : new JsonObject().Add("available", probe.Undo.Available).Add("performedAt", probe.Undo.PerformedAt));
        }

        private static JsonObject Observation(ProbeObservation observation, bool allowValue)
        {
            if (observation == null) return null;
            return new JsonObject().Add("value", allowValue ? observation.Value : null).Add("state", observation.State).Add("focusedElement", observation.FocusedElement).Add("windowTitle", observation.WindowTitle).Add("childCount", observation.ChildCount).Add("rect", Rect(observation.Rect));
        }

        private static JsonObject Shot(ShotResult shot)
        {
            List<object> masks = new List<object>();
            if (shot.MaskedRects != null) for (int index = 0; index < shot.MaskedRects.Length; index++) masks.Add(new JsonObject().Add("rect", Rect(shot.MaskedRects[index].Rect)).Add("ruleId", shot.MaskedRects[index].RuleId));
            return new JsonObject().Add("shotId", shot.ShotId).Add("kind", shot.Kind).Add("file", "shots/" + Path.GetFileName(shot.File)).Add("sha256", shot.Sha256).Add("bytes", shot.Bytes).Add("at", shot.At).Add("monitorId", shot.MonitorId == null ? (object)Diagnostics.Unknown("The capture monitor was not assigned.") : shot.MonitorId).Add("rect", Rect(shot.Rect)).Add("maskedRects", masks.ToArray()).Add("captureMethod", shot.CaptureMethod).Add("status", Status(shot.Status));
        }

        private static JsonObject Event(SessionEvent item)
        {
            return new JsonObject().Add("seq", item.Seq).Add("at", item.At).Add("type", item.Type).Add("source", item.Source).Add("refs", new JsonObject()).Add("detail", item.Detail);
        }

        private static JsonObject Failure(AcquisitionFailure item)
        {
            return new JsonObject().Add("at", item.At).Add("layer", item.Layer).Add("code", item.Code).Add("detail", item.Detail).Add("elementId", item.ElementId).Add("targetId", null);
        }

        private static JsonObject Status(ProbeStatus status)
        {
            List<object> reasons = new List<object>();
            if (status != null) for (int index = 0; index < status.Reasons.Count; index++) reasons.Add(new JsonObject().Add("code", status.Reasons[index].Code).Add("message", status.Reasons[index].Message));
            return new JsonObject().Add("state", status == null ? "unavailable" : status.State).Add("reasons", reasons.ToArray());
        }

        private static JsonObject Rect(RectValue rect)
        {
            return rect == null ? null : new JsonObject().Add("x", rect.X).Add("y", rect.Y).Add("width", rect.Width).Add("height", rect.Height);
        }

        private static JsonObject Node(UiaNode node)
        {
            return new JsonObject().Add("controlType", node.ControlType).Add("name", node.Name).Add("automationId", node.AutomationId).Add("indexAmongSameType", node.IndexAmongSameType).Add("siblingCount", node.SiblingCount);
        }

        private static string CaptureState(ElementRecord record)
        {
            if (record.Shots.Count == 0) return "unavailable";
            for (int index = 0; index < record.Shots.Count; index++) if (record.Shots[index].Status == null || record.Shots[index].Status.State != "ok") return "partial";
            return "ok";
        }

        private static long TopHandle(Win32Info info) { return info == null ? 0 : (info.Ancestors.Count == 0 ? info.Hwnd : info.Ancestors[info.Ancestors.Count - 1].Hwnd); }
        private static string TopClass(Win32Info info) { return info == null ? null : (info.Ancestors.Count == 0 ? info.ClassName : info.Ancestors[info.Ancestors.Count - 1].ClassName); }
        private static string TopCaption(Win32Info info) { return info == null ? null : (info.Ancestors.Count == 0 ? info.Caption : info.Ancestors[info.Ancestors.Count - 1].Caption); }
        private static string AutomationHint(Win32Info win32, UiaInfo uia) { return uia != null && !String.IsNullOrWhiteSpace(uia.AutomationId) ? "uia" : (win32 != null && win32.CtrlId != 0 && win32.CtrlId != -1 ? "win32.ctrlId" : "screen.relative"); }

        internal static string PersistentCaption(Session session, ElementRecord record)
        {
            if (record == null || record.Win32 == null) return null;
            bool valueBearing = record.Uia != null && (record.Uia.IsPassword || record.Uia.ControlType == "Edit" || record.Uia.ControlType == "Document");
            if (!valueBearing) return record.Win32.Caption;
            bool allow = session != null && session.ValueCapture == "full" && LiveValuePresenter.MaskReason(record.Uia, session.Masking, record.ElementId) == null;
            return allow && record.RecordedValue != null ? record.RecordedValue.Content : null;
        }

        private static int TargetOrdinal(Session session, ElementRecord record)
        {
            int pid = record.Win32 == null ? 0 : record.Win32.ProcessId;
            List<int> seen = new List<int>();
            for (int index = 0; index < session.Elements.Count; index++)
            {
                int current = session.Elements[index].Win32 == null ? 0 : session.Elements[index].Win32.ProcessId;
                if (!seen.Contains(current)) seen.Add(current);
                if (current == pid) return seen.Count;
            }
            return 1;
        }

        private static string TargetId(Session session, ElementRecord record) { return "tg-" + TargetOrdinal(session, record).ToString("00", CultureInfo.InvariantCulture); }
    }

    public static class PackWriter
    {
        public static PackResult Write(Session session, string folder)
        {
            PackResult result = new PackResult();
            result.Folder = Path.GetFullPath(folder);
            try
            {
                if (File.Exists(result.Folder)) throw new IOException("The selected output path is a file, not a folder.");
                result.Folder = UniqueFolder(result.Folder);
                Directory.CreateDirectory(result.Folder);
                session.RegisterWriteTarget(result.Folder, "investigation pack export");
                string shots = Path.Combine(result.Folder, "shots");
                Directory.CreateDirectory(shots);
                for (int index = 0; index < session.Shots.Count; index++)
                {
                    ShotResult shot = session.Shots[index];
                    if (shot != null && !String.IsNullOrWhiteSpace(shot.File) && File.Exists(shot.File)) File.Copy(shot.File, Path.Combine(shots, Path.GetFileName(shot.File)), false);
                }
                string sessionPath = Path.Combine(result.Folder, "session.json");
                JsonWriter.WriteFile(sessionPath, SessionSchema.Build(session));
                File.WriteAllText(Path.Combine(result.Folder, "report.html"), Report.Build(session), new UTF8Encoding(false));
                WriteDiagnostics(Path.Combine(result.Folder, "diagnostics.log"), session);
                File.WriteAllText(Path.Combine(result.Folder, "README.txt"), Readme(), new UTF8Encoding(false));
                WriteManifest(result.Folder);
                List<string> files = new List<string>();
                foreach (string path in Directory.GetFiles(result.Folder, "*", SearchOption.AllDirectories)) files.Add(Relative(result.Folder, path));
                files.Sort(StringComparer.Ordinal);
                result.Files = files.ToArray();
                result.Status = ProbeStatus.Ok();
                return result;
            }
            catch (Exception exception)
            {
                result.Status = ProbeStatus.Unavailable("PACK-WRITE", "The selected output path could not be written: " + exception.GetType().Name + ": " + exception.Message);
                result.Files = new string[0];
                return result;
            }
        }

        private static string UniqueFolder(string requested)
        {
            if (!Directory.Exists(requested)) return requested;
            int suffix = 2;
            while (Directory.Exists(requested + "_" + suffix.ToString(CultureInfo.InvariantCulture))) suffix++;
            return requested + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        private static void WriteDiagnostics(string path, Session session)
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < session.AcquisitionFailures.Count; index++)
            {
                AcquisitionFailure item = session.AcquisitionFailures[index];
                builder.Append('[').Append(item.At.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)).Append("][WARN][").Append(item.Code).Append("] ").AppendLine(item.Detail);
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static void WriteManifest(string folder)
        {
            List<object> entries = new List<object>();
            string[] files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            for (int index = 0; index < files.Length; index++)
            {
                if (String.Equals(Path.GetFileName(files[index]), "MANIFEST.json", StringComparison.OrdinalIgnoreCase)) continue;
                FileInfo info = new FileInfo(files[index]);
                entries.Add(new JsonObject().Add("file", Relative(folder, files[index])).Add("bytes", info.Length).Add("sha256", Hash(files[index])));
            }
            object sessionHash = File.Exists(Path.Combine(folder, "session.json")) ? (object)Hash(Path.Combine(folder, "session.json")) : Diagnostics.Unknown("session.json was not generated.");
            JsonObject manifest = new JsonObject().Add("generatedAt", DateTimeOffset.Now).Add("sessionSha256", sessionHash).Add("toolSha256", Diagnostics.Unknown("The text-only Add-Type distribution has no single executable hash.")).Add("files", entries.ToArray());
            JsonWriter.WriteFile(Path.Combine(folder, "MANIFEST.json"), manifest);
        }

        private static string Hash(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", String.Empty);
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string Readme()
        {
            return "App Studio investigation pack\r\n\r\nreport.html is a self-contained human-readable report.\r\nsession.json is the machine-readable canonical record.\r\nshots contains captured image evidence.\r\ndiagnostics.log contains acquisition failures and reasons.\r\nMANIFEST.json contains byte counts and SHA-256 hashes for integrity checking.\r\n\r\nThe manifest supports change detection; it is not encryption and does not provide confidentiality.\r\n";
        }
    }
}
