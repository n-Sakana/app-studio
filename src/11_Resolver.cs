namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    public sealed class ResolveContext
    {
        public string Context;
        public string TargetRunId;
        public Snapshot Original;
        public Snapshot[] Candidates;
        public RectValue TargetClientRect;
    }

    public static class Resolver
    {
        public static Verification Resolve(Locator locator, ResolveContext context)
        {
            if (locator == null) throw new ArgumentNullException("locator");
            if (context == null) throw new ArgumentNullException("context");
            Stopwatch watch = Stopwatch.StartNew();
            List<Snapshot> matches = new List<Snapshot>();
            Snapshot[] candidates = context.Candidates == null ? new Snapshot[0] : context.Candidates;
            for (int index = 0; index < candidates.Length; index++)
            {
                if (Matches(locator, candidates[index], context.TargetClientRect)) matches.Add(candidates[index]);
            }
            watch.Stop();
            Verification verification = new Verification();
            verification.At = DateTimeOffset.Now;
            verification.Context = String.IsNullOrWhiteSpace(context.Context) ? "immediate" : context.Context;
            verification.TargetRunId = context.TargetRunId;
            verification.MatchCount = matches.Count;
            verification.SameElement = matches.Count == 1 && SameElement(context.Original, matches[0], locator);
            verification.DurationMs = (int)watch.ElapsedMilliseconds;
            verification.Note = Note(verification);
            locator.Verifications.Add(verification);
            ApplyVerification(locator, verification);
            return verification;
        }

        public static bool Matches(Locator locator, Snapshot snapshot, RectValue targetClientRect)
        {
            if (locator == null || snapshot == null || locator.Expression == null) return false;
            LocatorExpression expression = locator.Expression;
            UiaInfo uia = snapshot.Uia;
            Win32Info win32 = snapshot.Win32;
            if (locator.Strategy == "uia.automationId")
            {
                return uia != null && Equal(expression.AutomationId, uia.AutomationId) && OptionalEqual(expression.ControlType, uia.ControlType);
            }
            if (locator.Strategy == "uia.nameControlType")
            {
                return uia != null && Equal(expression.Name, uia.Name) && Equal(expression.ControlType, uia.ControlType);
            }
            if (locator.Strategy == "uia.path")
            {
                return uia != null && PathEqual(expression.UiaPath, uia.TreePath);
            }
            if (locator.Strategy == "win32.ctrlId")
            {
                return win32 != null && expression.HasCtrlId && win32.CtrlId == expression.CtrlId && Equal(expression.ParentClass, ImmediateParentClass(win32));
            }
            if (locator.Strategy == "win32.classPath")
            {
                return win32 != null && ClassPathEqual(expression.Win32ClassPath, win32) && (!expression.ClassIndex.HasValue || expression.ClassIndex.Value == win32.ZIndex);
            }
            if (locator.Strategy == "screen.relative")
            {
                RectValue item = uia != null && uia.BoundingRect != null ? uia.BoundingRect : (win32 == null ? null : win32.WindowRect);
                if (item == null || targetClientRect == null || !expression.RelativeX.HasValue || !expression.RelativeY.HasValue) return false;
                int x = targetClientRect.X + (int)Math.Round(targetClientRect.Width * expression.RelativeX.Value);
                int y = targetClientRect.Y + (int)Math.Round(targetClientRect.Height * expression.RelativeY.Value);
                return x >= item.X && x < item.X + item.Width && y >= item.Y && y < item.Y + item.Height;
            }
            return false;
        }

        public static void ApplyVerification(Locator locator, Verification verification)
        {
            LocatorConfidence confidence = LocatorBuilder.BaseConfidence(locator);
            int score = confidence.Score;
            if (verification.MatchCount == 0)
            {
                score = Math.Min(score, 20);
                LocatorBuilder.AddReason(confidence, "Verification found no matching element.");
                confidence.Level = "low";
            }
            else if (verification.MatchCount == 1)
            {
                if (verification.SameElement)
                {
                    score += 20;
                    LocatorBuilder.AddReason(confidence, "Verification found one match and it represents the same element.");
                }
                else
                {
                    score = Math.Min(score, 40);
                    LocatorBuilder.AddReason(confidence, "Verification found one match but it did not represent the expected element.");
                }
                confidence.Level = LocatorBuilder.Level(LocatorBuilder.Clamp(score), locator.Strategy == "screen.relative");
            }
            else
            {
                score = Math.Min(score, 60);
                LocatorBuilder.AddReason(confidence, "Verification found " + verification.MatchCount + " matches, so the selector is not unique.");
                confidence.Level = locator.Strategy == "screen.relative" ? "low" : "medium";
            }
            if (verification.DurationMs > 1000)
            {
                score -= 15;
                LocatorBuilder.AddReason(confidence, "Resolution took longer than one second.");
                if (confidence.Level == "high") confidence.Level = "medium";
            }
            confidence.Score = LocatorBuilder.Clamp(score);
            if (locator.Strategy == "screen.relative") confidence.Level = "low";
            locator.Confidence = confidence;
        }

        private static bool SameElement(Snapshot expected, Snapshot actual, Locator locator)
        {
            if (expected == null || actual == null) return false;
            if (locator.Strategy.IndexOf("uia.", StringComparison.Ordinal) == 0)
            {
                if (expected.Uia == null || actual.Uia == null) return false;
                if (!String.IsNullOrWhiteSpace(expected.Uia.AutomationId)) return Equal(expected.Uia.AutomationId, actual.Uia.AutomationId) && OptionalEqual(expected.Uia.ControlType, actual.Uia.ControlType);
                return Equal(expected.Uia.Name, actual.Uia.Name) && OptionalEqual(expected.Uia.ControlType, actual.Uia.ControlType);
            }
            if (locator.Strategy.IndexOf("win32.", StringComparison.Ordinal) == 0)
            {
                return expected.Win32 != null && actual.Win32 != null && expected.Win32.CtrlId == actual.Win32.CtrlId && Equal(LocatorBuilder.StableClassName(expected.Win32.ClassName), LocatorBuilder.StableClassName(actual.Win32.ClassName));
            }
            RectValue first = ExpectedRect(expected);
            RectValue second = ExpectedRect(actual);
            return first != null && second != null && Math.Abs((first.X + first.Width / 2) - (second.X + second.Width / 2)) <= Math.Max(2, first.Width) && Math.Abs((first.Y + first.Height / 2) - (second.Y + second.Height / 2)) <= Math.Max(2, first.Height);
        }

        private static RectValue ExpectedRect(Snapshot snapshot)
        {
            return snapshot.Uia != null && snapshot.Uia.BoundingRect != null ? snapshot.Uia.BoundingRect : (snapshot.Win32 == null ? null : snapshot.Win32.WindowRect);
        }

        private static string Note(Verification verification)
        {
            if (verification.MatchCount == 0) return "No match was found.";
            if (verification.MatchCount > 1) return verification.MatchCount + " matching elements were found.";
            return verification.SameElement ? "One matching element was found and verified as the same element." : "One matching element was found but identity could not be confirmed.";
        }

        private static bool PathEqual(UiaNode[] expected, UiaNode[] actual)
        {
            if (expected == null || actual == null || expected.Length != actual.Length) return false;
            for (int index = 0; index < expected.Length; index++)
            {
                if (!OptionalEqual(expected[index].ControlType, actual[index].ControlType)) return false;
                if (!String.IsNullOrWhiteSpace(expected[index].AutomationId))
                {
                    if (!Equal(expected[index].AutomationId, actual[index].AutomationId)) return false;
                }
                else if (!String.IsNullOrWhiteSpace(expected[index].Name))
                {
                    if (!Equal(expected[index].Name, actual[index].Name)) return false;
                }
                else if (expected[index].IndexAmongSameType != actual[index].IndexAmongSameType)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ClassPathEqual(string[] expected, Win32Info actual)
        {
            if (expected == null || actual == null) return false;
            List<string> classes = new List<string>();
            if (actual.Ancestors != null)
            {
                for (int index = actual.Ancestors.Count - 1; index >= 0; index--)
                {
                    if (!String.IsNullOrWhiteSpace(actual.Ancestors[index].ClassName)) classes.Add(LocatorBuilder.StableClassName(actual.Ancestors[index].ClassName));
                }
            }
            classes.Add(LocatorBuilder.StableClassName(actual.ClassName));
            if (classes.Count != expected.Length) return false;
            for (int index = 0; index < expected.Length; index++) if (!Equal(expected[index], classes[index])) return false;
            return true;
        }

        private static string ImmediateParentClass(Win32Info info)
        {
            return info.Ancestors != null && info.Ancestors.Count != 0 ? LocatorBuilder.StableClassName(info.Ancestors[0].ClassName) : null;
        }

        private static bool OptionalEqual(string expected, string actual)
        {
            return String.IsNullOrWhiteSpace(expected) || Equal(expected, actual);
        }

        private static bool Equal(string first, string second)
        {
            return String.Equals(first, second, StringComparison.Ordinal);
        }
    }
}
