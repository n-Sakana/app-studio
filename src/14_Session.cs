namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class RecordedValue
    {
        public int Length;
        public string Kind;
        public bool Masked;
        public string MaskRule;
        public string Content;
    }

    public sealed class ElementRecord
    {
        public string ElementId;
        public DateTimeOffset PinnedAt;
        public string Label;
        public List<string> Notes = new List<string>();
        public Win32Info Win32;
        public UiaInfo Uia;
        public RecordedValue RecordedValue;
        public List<Locator> Locators = new List<Locator>();
        public List<ProbeResult> Probes = new List<ProbeResult>();
        public List<ShotResult> Shots = new List<ShotResult>();
    }

    public sealed class AcquisitionFailure
    {
        public DateTimeOffset At;
        public string Layer;
        public string Code;
        public string Detail;
        public string ElementId;
    }

    public sealed class SessionEvent
    {
        public int Seq;
        public DateTimeOffset At;
        public string Type;
        public string Source;
        public string Detail;
    }

    public class SessionData
    {
        public string Id;
        public DateTimeOffset StartedAt;
        public string ValueCapture = "maskedOnly";
        public DateTimeOffset? ValueCaptureChangedAt;
        public string ValueCaptureReason;
        public string Label;
        public string Mode = "readOnly";
        public string OperatorNote;
        public DateTimeOffset? EndedAt;
        public JsonObject Environment;
        public List<WriteTargetRecord> WriteTargets = new List<WriteTargetRecord>();
        public List<MaskRule> Masking = new List<MaskRule>();
        public List<ElementRecord> Elements = new List<ElementRecord>();
        public List<ShotResult> Shots = new List<ShotResult>();
        public List<SessionEvent> Events = new List<SessionEvent>();
        public List<AcquisitionFailure> AcquisitionFailures = new List<AcquisitionFailure>();
    }

    public sealed class Session : SessionData
    {
        public void RegisterWriteTarget(string path, string purpose)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string fullPath = Path.GetFullPath(path);
            for (int index = 0; index < WriteTargets.Count; index++) if (String.Equals(WriteTargets[index].Path, fullPath, StringComparison.OrdinalIgnoreCase) && WriteTargets[index].Purpose == purpose) return;
            WriteTargetRecord item = new WriteTargetRecord();
            item.Path = fullPath;
            item.Purpose = purpose;
            WriteTargets.Add(item);
        }
    }

    public sealed class WriteTargetRecord
    {
        public string Path;
        public string Purpose;
    }

    public sealed class MaskRule
    {
        public string RuleId;
        public string Kind;
        public string Pattern;
        public string AppliesTo;
    }

    public sealed class LiveValueView
    {
        public string Text;
        public bool Visible;
        public string Reason;
        public string Notice;
    }

    public sealed class LiveValuePresenter
    {
        public LiveValueView Current { get; private set; }

        public LiveValueView Present(Snapshot snapshot, IEnumerable<MaskRule> rules)
        {
            LiveValueView view = new LiveValueView();
            view.Notice = "Display only - not recorded";
            UiaInfo info = snapshot == null ? null : snapshot.Uia;
            if (info == null || info.IsPassword)
            {
                view.Visible = false;
                view.Reason = info != null && info.IsPassword ? "Hidden because IsPassword is true." : "No UI Automation value is available.";
            }
            else
            {
                string reason = MaskReason(info, rules, null);
                view.Visible = reason == null;
                view.Reason = reason;
                view.Text = view.Visible ? info.LiveValue : null;
            }
            Current = view;
            return view;
        }

        public void Clear()
        {
            Current = null;
        }

        internal static string MaskReason(UiaInfo info, IEnumerable<MaskRule> rules, string elementId)
        {
            if (info != null && info.IsPassword) return "Hidden because IsPassword is true.";
            if (rules == null) return null;
            foreach (MaskRule rule in rules)
            {
                if (rule == null || rule.Kind == "isPassword") continue;
                if (rule.Kind == "nameRegex" && info != null && !String.IsNullOrEmpty(info.Name) && !String.IsNullOrEmpty(rule.Pattern) && System.Text.RegularExpressions.Regex.IsMatch(info.Name, rule.Pattern)) return "Hidden by mask rule " + rule.RuleId + ".";
                if (rule.Kind == "controlType" && info != null && String.Equals(info.ControlType, rule.Pattern, StringComparison.OrdinalIgnoreCase)) return "Hidden by mask rule " + rule.RuleId + ".";
                if (rule.Kind == "manual" && String.Equals(rule.AppliesTo, elementId, StringComparison.Ordinal)) return "Hidden by manual mask rule " + rule.RuleId + ".";
            }
            return null;
        }
    }

    public sealed class SessionRecorder
    {
        private readonly string shotsDirectory;
        private int elementSequence;
        private int eventSequence;

        public SessionRecorder(string shotsDir)
            : this(shotsDir, null)
        {
        }

        public SessionRecorder(string shotsDir, JsonObject environment)
        {
            shotsDirectory = shotsDir;
            Data = new Session();
            Data.Id = "ss-" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            Data.StartedAt = DateTimeOffset.Now;
            string liveDiagnostics = Path.Combine(Path.GetDirectoryName(shotsDirectory), "diagnostics.log");
            Data.Environment = environment ?? Diagnostics.Collect(AppDomain.CurrentDomain.BaseDirectory, liveDiagnostics);
            Data.RegisterWriteTarget(shotsDirectory, "temporary capture images");
            Data.RegisterWriteTarget(liveDiagnostics, "live session diagnostics");
            MaskRule passwordRule = new MaskRule();
            passwordRule.RuleId = "isPassword";
            passwordRule.Kind = "isPassword";
            passwordRule.Pattern = "true";
            passwordRule.AppliesTo = "all";
            Data.Masking.Add(passwordRule);
            AddEvent("session.start", "tool", "Session started.");
        }

        public Session Data { get; private set; }

        public ElementRecord Pin(Snapshot hoverSnapshot, string label, string note)
        {
            if (hoverSnapshot == null) throw new InvalidOperationException("There is no live element to pin.");
            ElementRef reference = ElementRef.FromSnapshot(hoverSnapshot);
            Snapshot deep = reference == null ? hoverSnapshot : Probe.Deep(reference, 3000);
            // The deep re-probe resolves the element again from screen point and
            // is occasionally intercepted by a transient window at that pixel.
            // The live hover snapshot is a valid prior observation of the same
            // element, so its richer UI Automation identity is kept when the
            // re-probe comes back without one.
            if (HasIdentity(hoverSnapshot.Uia) && !HasIdentity(deep.Uia)) deep.Uia = hoverSnapshot.Uia;
            elementSequence++;
            ElementRecord record = new ElementRecord();
            record.ElementId = "el-" + elementSequence.ToString("0000", CultureInfo.InvariantCulture);
            record.PinnedAt = DateTimeOffset.Now;
            record.Label = String.IsNullOrWhiteSpace(label) ? DefaultLabel(deep) : label;
            if (!String.IsNullOrWhiteSpace(note)) record.Notes.Add(note);
            record.Win32 = deep.Win32;
            record.Uia = CloneForRecord(deep.Uia);
            record.RecordedValue = RecordValueFor(deep.Uia, record.ElementId);
            TargetInfo target = TargetInfo.FromSnapshot(deep, "run-current");
            Locator[] locators = LocatorBuilder.Build(deep, target);
            ResolveContext resolveContext = new ResolveContext();
            resolveContext.Context = "immediate";
            resolveContext.TargetRunId = target.TargetRunId;
            resolveContext.Original = deep;
            resolveContext.Candidates = new Snapshot[] { deep };
            resolveContext.TargetClientRect = target.ClientRect;
            for (int locatorIndex = 0; locatorIndex < locators.Length; locatorIndex++)
            {
                Resolver.Resolve(locators[locatorIndex], resolveContext);
                record.Locators.Add(locators[locatorIndex]);
            }

            RectValue rectangle = deep.Uia != null && deep.Uia.BoundingRect != null ? deep.Uia.BoundingRect : (deep.Win32 == null ? null : deep.Win32.WindowRect);
            List<MaskRect> masks = new List<MaskRect>();
            if (deep.Uia != null && deep.Uia.IsPassword && rectangle != null)
            {
                MaskRect mask = new MaskRect();
                mask.Rect = rectangle;
                mask.RuleId = "isPassword";
                masks.Add(mask);
            }
            if (rectangle != null)
            {
                string shotPath = Path.Combine(shotsDirectory, record.ElementId + "-crop.png");
                ShotResult shot = Capture.Crop(rectangle, masks.ToArray(), shotPath, new IntPtr(record.Win32 == null ? 0 : record.Win32.Hwnd));
                record.Shots.Add(shot);
                shot.ShotId = "sh-" + elementSequence.ToString("0000", CultureInfo.InvariantCulture) + "-crop";
                shot.Kind = "crop";
                Data.Shots.Add(shot);
                if (shot.Status.State != "ok") AddFailure("capture", shot.Status.Reasons.Count == 0 ? "CAP-BLACK" : shot.Status.Reasons[0].Code, shot.Status.Reasons.Count == 0 ? "Capture failed." : shot.Status.Reasons[0].Message, record.ElementId);
            }
            if (record.Win32 != null && (String.Equals(record.Win32.ClassName, "SysListView32", StringComparison.OrdinalIgnoreCase) || String.Equals(record.Win32.ClassName, "SysTreeView32", StringComparison.OrdinalIgnoreCase)))
            {
                AddFailure("win32", "NEEDS-B3", "Row data requires the conditional B3 acquisition path.", record.ElementId);
            }
            Data.Elements.Add(record);
            AddEvent("element.pin", "tool", record.ElementId);
            return record;
        }

        public void AddNote(ElementRecord record, string text)
        {
            if (record == null || String.IsNullOrWhiteSpace(text)) return;
            record.Notes.Add(text);
            AddEvent("note.add", "tool", record.ElementId);
        }

        public void AddFailure(string layer, string code, string detail, string elementId)
        {
            AcquisitionFailure failure = new AcquisitionFailure();
            failure.At = DateTimeOffset.Now;
            failure.Layer = layer;
            failure.Code = code;
            failure.Detail = detail;
            failure.ElementId = elementId;
            Data.AcquisitionFailures.Add(failure);
            AddEvent("acquisition.fail", "tool", code);
        }

        public void AddProbe(ElementRecord record, ProbeResult probe)
        {
            if (record == null || probe == null) return;
            probe.ElementId = record.ElementId;
            record.Probes.Add(probe);
            AddEvent("probe." + probe.Kind.ToString().ToLowerInvariant(), "tool", probe.ProbeId + " " + probe.Method + " " + probe.Outcome);
        }

        public void AddShot(ShotResult shot, string kind)
        {
            if (shot == null) return;
            shot.Kind = kind;
            shot.ShotId = "sh-" + (Data.Shots.Count + 1).ToString("0000", CultureInfo.InvariantCulture) + "-" + kind;
            Data.Shots.Add(shot);
            AddEvent("shot.take", "tool", shot.ShotId);
        }

        public void SetValueCapture(string mode, string reason)
        {
            if (mode != "maskedOnly" && mode != "full" && mode != "none") throw new ArgumentOutOfRangeException("mode");
            if (Data.ValueCapture == mode) return;
            Data.ValueCapture = mode;
            Data.ValueCaptureChangedAt = DateTimeOffset.Now;
            Data.ValueCaptureReason = reason;
            AddEvent("mode.change", "tool", "valueCapture=" + mode + " reason=" + (reason ?? String.Empty));
        }

        public void AddMaskRule(MaskRule rule)
        {
            if (rule == null || String.IsNullOrWhiteSpace(rule.RuleId) || String.IsNullOrWhiteSpace(rule.Kind)) throw new ArgumentException("A mask rule requires an ID and kind.", "rule");
            if (rule.Kind == "isPassword") throw new InvalidOperationException("The built-in IsPassword rule cannot be replaced or removed.");
            Data.Masking.Add(rule);
            AddEvent("mask.add", "tool", rule.RuleId + " " + rule.Kind);
        }

        public void AddManualMask(ElementRecord record, RectValue rectangle)
        {
            if (record == null || rectangle == null) throw new ArgumentNullException(record == null ? "record" : "rectangle");
            MaskRule rule = new MaskRule();
            rule.RuleId = "manual-" + record.ElementId + "-" + (Data.Masking.Count + 1).ToString("00", CultureInfo.InvariantCulture);
            rule.Kind = "manual";
            rule.Pattern = "rectangle";
            rule.AppliesTo = record.ElementId;
            AddMaskRule(rule);
            MaskRect mask = new MaskRect();
            mask.Rect = rectangle;
            mask.RuleId = rule.RuleId;
            for (int index = 0; index < record.Shots.Count; index++) Capture.AddMasks(record.Shots[index], new MaskRect[] { mask });
        }

        public void AddEvent(string type, string source, string detail)
        {
            eventSequence++;
            SessionEvent item = new SessionEvent();
            item.Seq = eventSequence;
            item.At = DateTimeOffset.Now;
            item.Type = type;
            item.Source = source;
            item.Detail = detail;
            Data.Events.Add(item);
        }

        public JsonObject ToJson()
        {
            return SessionSchema.Build(Data);
        }

        public void WritePreview(string path)
        {
            JsonWriter.WriteFile(path, ToJson());
        }

        public RecordedValue RecordValueFor(UiaInfo info, string elementId)
        {
            RecordedValue value = new RecordedValue();
            string live = info == null ? null : info.LiveValue;
            value.Length = live == null ? 0 : live.Length;
            value.Kind = live == null ? "none" : "string";
            if (info != null && info.IsPassword)
            {
                value.Masked = true;
                value.MaskRule = "isPassword";
                return value;
            }
            if (Data.ValueCapture == "none")
            {
                value.Length = 0;
                value.Kind = "none";
                value.Masked = true;
                value.MaskRule = "policy.none";
                return value;
            }
            string maskReason = LiveValuePresenter.MaskReason(info, Data.Masking, elementId);
            if (maskReason != null)
            {
                value.Masked = true;
                value.MaskRule = "configured.mask";
                return value;
            }
            if (Data.ValueCapture == "full")
            {
                value.Content = live;
                value.Masked = false;
                return value;
            }
            value.Masked = true;
            value.MaskRule = "policy.maskedOnly";
            return value;
        }

        private static bool HasIdentity(UiaInfo info)
        {
            return info != null && (!String.IsNullOrEmpty(info.Name) || !String.IsNullOrEmpty(info.ControlType) || !String.IsNullOrEmpty(info.AutomationId));
        }

        private static UiaInfo CloneForRecord(UiaInfo source)
        {
            if (source == null) return null;
            UiaInfo clone = new UiaInfo();
            clone.Name = source.Name;
            clone.AutomationId = source.AutomationId;
            clone.ControlType = source.ControlType;
            clone.LocalizedControlType = source.LocalizedControlType;
            clone.ClassName = source.ClassName;
            clone.FrameworkId = source.FrameworkId;
            clone.IsEnabled = source.IsEnabled;
            clone.IsOffscreen = source.IsOffscreen;
            clone.IsKeyboardFocusable = source.IsKeyboardFocusable;
            clone.IsPassword = source.IsPassword;
            clone.HelpText = source.HelpText;
            clone.AcceleratorKey = source.AcceleratorKey;
            clone.AccessKey = source.AccessKey;
            clone.RuntimeId = source.RuntimeId;
            clone.BoundingRect = source.BoundingRect;
            clone.NativeWindowHandle = source.NativeWindowHandle;
            clone.SupportedPatterns = source.SupportedPatterns;
            clone.TreePath = source.TreePath;
            clone.Children = source.Children;
            clone.LiveValue = null;
            clone.Status = source.Status;
            return clone;
        }

        private static string DefaultLabel(Snapshot snapshot)
        {
            if (snapshot != null && snapshot.Uia != null && !String.IsNullOrWhiteSpace(snapshot.Uia.Name)) return snapshot.Uia.Name;
            if (snapshot != null && snapshot.Win32 != null && !String.IsNullOrWhiteSpace(snapshot.Win32.Caption)) return snapshot.Win32.Caption;
            return "Element";
        }

        private JsonObject ElementJson(ElementRecord record)
        {
            List<object> shots = new List<object>();
            for (int index = 0; index < record.Shots.Count; index++) shots.Add(ShotJson(record.Shots[index]));
            List<object> locators = new List<object>();
            for (int index = 0; index < record.Locators.Count; index++) locators.Add(LocatorJson.Build(record.Locators[index]));
            List<object> probes = new List<object>();
            for (int index = 0; index < record.Probes.Count; index++) probes.Add(ProbeJson(record, record.Probes[index]));
            return new JsonObject()
                .Add("elementId", record.ElementId)
                .Add("pinnedAt", record.PinnedAt)
                .Add("label", record.Label)
                .Add("notes", record.Notes.ToArray())
                .Add("win32", record.Win32 == null ? null : new JsonObject().Add("hwnd", record.Win32.Hwnd).Add("class", record.Win32.ClassName).Add("ctrlId", record.Win32.CtrlId))
                .Add("uia", record.Uia == null ? null : new JsonObject().Add("name", record.Uia.Name).Add("automationId", record.Uia.AutomationId).Add("controlType", record.Uia.ControlType).Add("isPassword", record.Uia.IsPassword))
                .Add("recordedValue", new JsonObject().Add("length", record.RecordedValue.Length).Add("kind", record.RecordedValue.Kind).Add("masked", record.RecordedValue.Masked).Add("maskRule", record.RecordedValue.MaskRule).Add("content", record.RecordedValue.Content))
                .Add("locators", locators.ToArray())
                .Add("probes", probes.ToArray())
                .Add("shots", shots.ToArray());
        }

        private JsonObject ProbeJson(ElementRecord record, ProbeResult probe)
        {
            List<object> sideEffects = new List<object>();
            for (int index = 0; index < probe.SideEffects.Count; index++) sideEffects.Add(new JsonObject().Add("type", probe.SideEffects[index].Type).Add("detail", probe.SideEffects[index].Detail));
            JsonObject error = probe.Error == null ? null : new JsonObject().Add("code", probe.Error.Code).Add("hresult", probe.Error.Hresult).Add("message", probe.Error.Message);
            JsonObject undo = probe.Undo == null ? new JsonObject().Add("available", false).Add("performedAt", null) : new JsonObject().Add("available", probe.Undo.Available).Add("performedAt", probe.Undo.PerformedAt);
            return new JsonObject()
                .Add("probeId", probe.ProbeId)
                .Add("elementId", probe.ElementId)
                .Add("kind", probe.Kind.ToString().ToLowerInvariant())
                .Add("requestedAt", probe.RequestedAt)
                .Add("method", probe.Method)
                .Add("outcome", probe.Outcome)
                .Add("durationMs", probe.DurationMs)
                .Add("error", error)
                .Add("before", ProbeObservationJson(record, probe.Before))
                .Add("after", ProbeObservationJson(record, probe.After))
                .Add("sideEffects", sideEffects.ToArray())
                .Add("undo", undo);
        }

        private JsonObject ProbeObservationJson(ElementRecord record, ProbeObservation observation)
        {
            if (observation == null) return null;
            bool allowValue = Data.ValueCapture == "full" && (record.Uia == null || !record.Uia.IsPassword);
            return new JsonObject()
                .Add("value", allowValue ? observation.Value : null)
                .Add("state", observation.State)
                .Add("focusedElement", observation.FocusedElement)
                .Add("windowTitle", observation.WindowTitle)
                .Add("childCount", observation.ChildCount)
                .Add("rect", observation.Rect == null ? null : new JsonObject().Add("x", observation.Rect.X).Add("y", observation.Rect.Y).Add("width", observation.Rect.Width).Add("height", observation.Rect.Height));
        }

        private static JsonObject ShotJson(ShotResult shot)
        {
            List<object> masks = new List<object>();
            if (shot.MaskedRects != null)
            {
                for (int index = 0; index < shot.MaskedRects.Length; index++) masks.Add(new JsonObject().Add("ruleId", shot.MaskedRects[index].RuleId));
            }
            return new JsonObject().Add("file", shot.File).Add("sha256", shot.Sha256).Add("bytes", shot.Bytes).Add("captureMethod", shot.CaptureMethod).Add("maskedRects", masks.ToArray());
        }

        private static JsonObject FailureJson(AcquisitionFailure failure)
        {
            return new JsonObject().Add("at", failure.At).Add("layer", failure.Layer).Add("code", failure.Code).Add("detail", failure.Detail).Add("elementId", failure.ElementId);
        }

        private static JsonObject EventJson(SessionEvent item)
        {
            return new JsonObject().Add("seq", item.Seq).Add("at", item.At).Add("type", item.Type).Add("source", item.Source).Add("detail", item.Detail);
        }
    }

    public static class DiagnosticProjection
    {
        public static string Screen(SessionData session)
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < session.AcquisitionFailures.Count; index++)
            {
                AcquisitionFailure failure = session.AcquisitionFailures[index];
                builder.AppendLine(failure.Code + " " + failure.Detail);
            }
            return builder.ToString();
        }

        public static string Html(SessionData session)
        {
            return "<section id=\"diagnostics\"><pre>" + Escape(Screen(session)) + "</pre></section>";
        }

        private static string Escape(string value)
        {
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
