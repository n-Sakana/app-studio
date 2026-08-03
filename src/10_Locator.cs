namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;

    public sealed class TargetInfo
    {
        public string TargetRunId;
        public string ProcessName;
        public string TopLevelClass;
        public string TopLevelCaption;
        public RectValue ClientRect;

        public static TargetInfo FromSnapshot(Snapshot snapshot, string targetRunId)
        {
            TargetInfo target = new TargetInfo();
            target.TargetRunId = targetRunId;
            if (snapshot != null && snapshot.Win32 != null)
            {
                target.ProcessName = "pid-" + snapshot.Win32.ProcessId.ToString(CultureInfo.InvariantCulture);
                target.ClientRect = snapshot.Win32.ClientRect;
                if (snapshot.Win32.Ancestors != null && snapshot.Win32.Ancestors.Count != 0)
                {
                    Win32Ancestor top = snapshot.Win32.Ancestors[snapshot.Win32.Ancestors.Count - 1];
                    target.TopLevelClass = top.ClassName;
                    target.TopLevelCaption = top.Caption;
                }
                else
                {
                    target.TopLevelClass = snapshot.Win32.ClassName;
                    target.TopLevelCaption = snapshot.Win32.Caption;
                }
            }
            return target;
        }
    }

    public sealed class LocatorScope
    {
        public string Kind;
        public string ProcessName;
        public string TopLevelClass;
        public string TopLevelCaption;
    }

    public sealed class LocatorExpression
    {
        public string AutomationId;
        public string Name;
        public string ControlType;
        public string ParentClass;
        public string ParentCaption;
        public bool HasCtrlId;
        public int CtrlId;
        public string[] Win32ClassPath;
        public int? ClassIndex;
        public UiaNode[] UiaPath;
        public double? RelativeX;
        public double? RelativeY;
        public int? OffsetX;
        public int? OffsetY;
        public int? Width;
        public int? Height;
    }

    public sealed class LocatorConfidence
    {
        public string Level;
        public int Score;
        public List<string> Reasons = new List<string>();
    }

    public sealed class Verification
    {
        public DateTimeOffset At;
        public string Context;
        public string TargetRunId;
        public int MatchCount;
        public bool SameElement;
        public int DurationMs;
        public string Note;
    }

    public sealed class Locator
    {
        public string LocatorId;
        public string Strategy;
        public LocatorScope Scope;
        public LocatorExpression Expression;
        public LocatorConfidence Confidence;
        public List<Verification> Verifications = new List<Verification>();
    }

    public static class LocatorBuilder
    {
        public static Locator[] Build(Snapshot snapshot, TargetInfo target)
        {
            List<Locator> locators = new List<Locator>();
            if (snapshot == null) return locators.ToArray();
            int sequence = 0;
            UiaInfo uia = snapshot.Uia;
            Win32Info win32 = snapshot.Win32;

            if (uia != null && IsStableAutomationId(uia.AutomationId))
            {
                LocatorExpression expression = new LocatorExpression();
                expression.AutomationId = uia.AutomationId;
                expression.ControlType = EmptyToNull(uia.ControlType);
                locators.Add(Create(++sequence, "uia.automationId", target, expression));
            }
            if (uia != null && !String.IsNullOrWhiteSpace(uia.Name) && !String.IsNullOrWhiteSpace(uia.ControlType))
            {
                LocatorExpression expression = new LocatorExpression();
                expression.Name = uia.Name;
                expression.ControlType = uia.ControlType;
                locators.Add(Create(++sequence, "uia.nameControlType", target, expression));
            }
            if (uia != null && !HasReason(uia.Status, "UIA-EMPTYTREE") && HasUsablePath(uia.TreePath))
            {
                LocatorExpression expression = new LocatorExpression();
                expression.UiaPath = ClonePath(uia.TreePath);
                locators.Add(Create(++sequence, "uia.path", target, expression));
            }
            if (win32 != null && win32.CtrlId != 0 && win32.CtrlId != -1)
            {
                string parentClass = ImmediateParentClass(win32, target);
                if (!String.IsNullOrWhiteSpace(parentClass))
                {
                    LocatorExpression expression = new LocatorExpression();
                    expression.HasCtrlId = true;
                    expression.CtrlId = win32.CtrlId;
                    expression.ParentClass = parentClass;
                    expression.ParentCaption = ImmediateParentCaption(win32, target);
                    expression.ControlType = EmptyToNull(StableClassName(win32.ClassName));
                    locators.Add(Create(++sequence, "win32.ctrlId", target, expression));
                }
            }
            if (win32 != null && !String.IsNullOrWhiteSpace(win32.ClassName) && win32.Ancestors != null && win32.Ancestors.Count != 0 && win32.ZIndex >= 0)
            {
                LocatorExpression expression = new LocatorExpression();
                List<string> classes = new List<string>();
                for (int index = win32.Ancestors.Count - 1; index >= 0; index--)
                {
                    if (!String.IsNullOrWhiteSpace(win32.Ancestors[index].ClassName)) classes.Add(StableClassName(win32.Ancestors[index].ClassName));
                }
                classes.Add(StableClassName(win32.ClassName));
                expression.Win32ClassPath = classes.ToArray();
                expression.ClassIndex = win32.ZIndex;
                locators.Add(Create(++sequence, "win32.classPath", target, expression));
            }
            RectValue itemRect = uia != null && uia.BoundingRect != null ? uia.BoundingRect : (win32 == null ? null : win32.WindowRect);
            if (itemRect != null && target != null && target.ClientRect != null && target.ClientRect.Width > 0 && target.ClientRect.Height > 0)
            {
                LocatorExpression expression = new LocatorExpression();
                int centerX = itemRect.X + itemRect.Width / 2;
                int centerY = itemRect.Y + itemRect.Height / 2;
                expression.RelativeX = (double)(centerX - target.ClientRect.X) / target.ClientRect.Width;
                expression.RelativeY = (double)(centerY - target.ClientRect.Y) / target.ClientRect.Height;
                expression.OffsetX = centerX - target.ClientRect.X;
                expression.OffsetY = centerY - target.ClientRect.Y;
                expression.Width = itemRect.Width;
                expression.Height = itemRect.Height;
                locators.Add(Create(++sequence, "screen.relative", target, expression));
            }
            return locators.ToArray();
        }

        public static bool ContainsForbiddenPersistentMaterial(Locator locator)
        {
            if (locator == null || locator.Expression == null) return false;
            string text = JsonWriter.Write(LocatorJson.Build(locator));
            return text.IndexOf("hwnd", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("runtimeId", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("liveValue", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("recordedValue", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static LocatorConfidence BaseConfidence(Locator locator)
        {
            LocatorConfidence confidence = new LocatorConfidence();
            int score = 45;
            LocatorExpression expression = locator.Expression;
            if (IsStableAutomationId(expression.AutomationId))
            {
                score += 25;
                AddReason(confidence, "AutomationId is non-empty and is not numeric-only.");
            }
            if (!String.IsNullOrWhiteSpace(expression.ControlType))
            {
                score += 10;
                AddReason(confidence, "ControlType narrows the candidate set.");
            }
            if (expression.HasCtrlId && expression.CtrlId != 0 && expression.CtrlId != -1)
            {
                score += 25;
                AddReason(confidence, "The non-default control ID is scoped by its parent class.");
            }
            if (!String.IsNullOrWhiteSpace(expression.Name))
            {
                if (LooksDynamic(expression.Name))
                {
                    score -= 25;
                    AddReason(confidence, "Name contains changing numeric, date, currency, count, or percent-like text.");
                }
                else
                {
                    score += 5;
                    AddReason(confidence, "Name supplies a readable selector component.");
                }
            }
            if (expression.UiaPath != null)
            {
                bool indexDependent = false;
                for (int index = 0; index < expression.UiaPath.Length; index++)
                {
                    if (expression.UiaPath[index].SiblingCount > 1 && String.IsNullOrWhiteSpace(expression.UiaPath[index].AutomationId)) indexDependent = true;
                }
                if (indexDependent)
                {
                    score -= 15;
                    AddReason(confidence, "The UI Automation path depends on a sibling index.");
                }
                else
                {
                    score += 5;
                    AddReason(confidence, "The UI Automation path contains named or identified steps.");
                }
            }
            if (locator.Scope != null && locator.Scope.Kind == "process")
            {
                score -= 10;
                AddReason(confidence, "Process-wide scope can include unrelated matching elements.");
            }
            if (locator.Strategy == "screen.relative")
            {
                score = Math.Min(score, 35);
                AddReason(confidence, "Relative screen position is capped at low confidence.");
            }
            confidence.Score = Clamp(score);
            confidence.Level = Level(confidence.Score, locator.Strategy == "screen.relative");
            if (confidence.Reasons.Count == 0) AddReason(confidence, "Only the captured material available for this strategy was used.");
            return confidence;
        }

        internal static bool LooksDynamic(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(name, "[0-9]|[$%]|\\b(?:items?|records?)\\b", RegexOptions.IgnoreCase);
        }

        internal static string Level(int score, bool forceLow)
        {
            if (forceLow) return "low";
            if (score >= 75) return "high";
            if (score >= 45) return "medium";
            return "low";
        }

        internal static void AddReason(LocatorConfidence confidence, string reason)
        {
            if (!String.IsNullOrWhiteSpace(reason) && reason.IndexOf('\n') < 0 && reason.IndexOf('\r') < 0) confidence.Reasons.Add(reason);
        }

        internal static int Clamp(int score)
        {
            return Math.Max(0, Math.Min(100, score));
        }

        internal static string StableClassName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return value;
            int marker = value.IndexOf(".app.", StringComparison.OrdinalIgnoreCase);
            if (value.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase) && marker > 0)
            {
                return value.Substring(0, marker + 4);
            }
            return value;
        }

        private static Locator Create(int sequence, string strategy, TargetInfo target, LocatorExpression expression)
        {
            Locator locator = new Locator();
            locator.LocatorId = "loc-" + sequence.ToString("0000", CultureInfo.InvariantCulture);
            locator.Strategy = strategy;
            locator.Scope = new LocatorScope();
            locator.Scope.Kind = String.IsNullOrWhiteSpace(target == null ? null : target.TopLevelClass) ? "process" : "topLevelWindow";
            locator.Scope.ProcessName = target == null ? null : target.ProcessName;
            locator.Scope.TopLevelClass = target == null ? null : target.TopLevelClass;
            locator.Scope.TopLevelCaption = target == null ? null : target.TopLevelCaption;
            locator.Expression = expression;
            locator.Confidence = BaseConfidence(locator);
            return locator;
        }

        private static bool IsStableAutomationId(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return !Regex.IsMatch(value, "^[0-9]+\\z");
        }

        private static bool HasUsablePath(UiaNode[] path)
        {
            if (path == null || path.Length == 0) return false;
            for (int index = 0; index < path.Length; index++)
            {
                if (!String.IsNullOrWhiteSpace(path[index].ControlType) || !String.IsNullOrWhiteSpace(path[index].AutomationId) || !String.IsNullOrWhiteSpace(path[index].Name)) return true;
            }
            return false;
        }

        private static bool HasReason(ProbeStatus status, string code)
        {
            if (status == null) return false;
            for (int index = 0; index < status.Reasons.Count; index++) if (status.Reasons[index].Code == code) return true;
            return false;
        }

        private static UiaNode[] ClonePath(UiaNode[] path)
        {
            UiaNode[] result = new UiaNode[path.Length];
            for (int index = 0; index < path.Length; index++)
            {
                result[index] = new UiaNode();
                result[index].ControlType = path[index].ControlType;
                result[index].Name = path[index].Name;
                result[index].AutomationId = path[index].AutomationId;
                result[index].IndexAmongSameType = path[index].IndexAmongSameType;
                result[index].SiblingCount = path[index].SiblingCount;
            }
            return result;
        }

        private static string ImmediateParentClass(Win32Info info, TargetInfo target)
        {
            if (info.Ancestors != null && info.Ancestors.Count != 0) return StableClassName(info.Ancestors[0].ClassName);
            return target == null ? null : StableClassName(target.TopLevelClass);
        }

        private static string ImmediateParentCaption(Win32Info info, TargetInfo target)
        {
            if (info.Ancestors != null && info.Ancestors.Count != 0) return info.Ancestors[0].Caption;
            return target == null ? null : target.TopLevelCaption;
        }

        private static string EmptyToNull(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    public static class LocatorJson
    {
        public static JsonObject Build(Locator locator)
        {
            List<object> reasons = new List<object>();
            for (int index = 0; index < locator.Confidence.Reasons.Count; index++) reasons.Add(locator.Confidence.Reasons[index]);
            List<object> verifications = new List<object>();
            for (int index = 0; index < locator.Verifications.Count; index++)
            {
                Verification verification = locator.Verifications[index];
                verifications.Add(new JsonObject().Add("at", verification.At).Add("context", verification.Context).Add("targetRunId", verification.TargetRunId).Add("matchCount", verification.MatchCount).Add("sameElement", verification.SameElement).Add("durationMs", verification.DurationMs).Add("note", verification.Note));
            }
            return new JsonObject()
                .Add("locatorId", locator.LocatorId)
                .Add("strategy", locator.Strategy)
                .Add("scope", Scope(locator.Scope))
                .Add("expression", Expression(locator.Expression))
                .Add("confidence", new JsonObject().Add("level", locator.Confidence.Level).Add("score", locator.Confidence.Score).Add("reasons", reasons.ToArray()))
                .Add("verifications", verifications.ToArray());
        }

        private static JsonObject Scope(LocatorScope scope)
        {
            return new JsonObject().Add("kind", scope.Kind).Add("processName", scope.ProcessName).Add("topLevelClass", scope.TopLevelClass).Add("topLevelCaption", scope.TopLevelCaption);
        }

        private static JsonObject Expression(LocatorExpression expression)
        {
            List<object> path = new List<object>();
            if (expression.UiaPath != null)
            {
                for (int index = 0; index < expression.UiaPath.Length; index++)
                {
                    UiaNode node = expression.UiaPath[index];
                    path.Add(new JsonObject().Add("controlType", node.ControlType).Add("name", node.Name).Add("automationId", node.AutomationId).Add("indexAmongSameType", node.IndexAmongSameType).Add("siblingCount", node.SiblingCount));
                }
            }
            return new JsonObject()
                .Add("automationId", expression.AutomationId)
                .Add("name", expression.Name)
                .Add("controlType", expression.ControlType)
                .Add("parentClass", expression.ParentClass)
                .Add("parentCaption", expression.ParentCaption)
                .Add("ctrlId", expression.HasCtrlId ? (object)expression.CtrlId : null)
                .Add("win32ClassPath", expression.Win32ClassPath)
                .Add("classIndex", expression.ClassIndex)
                .Add("uiaPath", path.ToArray())
                .Add("relativeX", expression.RelativeX)
                .Add("relativeY", expression.RelativeY)
                .Add("offsetX", expression.OffsetX)
                .Add("offsetY", expression.OffsetY)
                .Add("width", expression.Width)
                .Add("height", expression.Height);
        }
    }
}
