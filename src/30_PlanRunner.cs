namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Threading;

    public sealed class PlanStepResult
    {
        public PlanStep Step;
        public ProbeResult Probe;
        public string Outcome = "unknown";
        public string Method;
        public int DurationMs;
        public string ErrorCode;
        public string ErrorMessage;
        public bool Skipped;
        public string SkipReason;
        public string UsedIdentity;
        public string Reaction;

        public bool Succeeded { get { return !Skipped && Outcome == "success"; } }
    }

    public sealed class PlanRunProgress
    {
        public int Index;
        public int Total;
        public PlanStep Step;
        public PlanStepResult Result;
        public bool Finished;
    }

    public sealed class PlanRunResult
    {
        public int RunNumber;
        public DateTimeOffset StartedAt;
        public DateTimeOffset EndedAt;
        public bool WriteEnabled;
        public bool StopOnFailure;
        public bool Cancelled;
        public List<PlanStepResult> Steps = new List<PlanStepResult>();
        public string Title;

        public int SuccessCount
        {
            get { int count = 0; for (int index = 0; index < Steps.Count; index++) if (Steps[index].Succeeded) count++; return count; }
        }

        public int FailureCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Steps.Count; index++)
                {
                    PlanStepResult item = Steps[index];
                    if (item.Skipped) continue;
                    if (item.Outcome == "failed" || item.Outcome == "blocked" || item.Outcome == "notSupported") count++;
                }
                return count;
            }
        }

        public int UnknownCount
        {
            get { int count = 0; for (int index = 0; index < Steps.Count; index++) if (!Steps[index].Skipped && Steps[index].Outcome == "unknown") count++; return count; }
        }

        // FailureCount above is what decides whether the run stops, so it stays
        // an aggregate. The record and the screen have to keep the five outcome
        // values apart, because a refusal by a safety policy and a step that
        // actually failed are different facts to the person reading them.
        public int OutcomeCount(string outcome)
        {
            int count = 0;
            for (int index = 0; index < Steps.Count; index++)
            {
                if (Steps[index].Skipped) continue;
                if (String.Equals(Steps[index].Outcome, outcome, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        public int FailedCount { get { return OutcomeCount("failed"); } }
        public int BlockedCount { get { return OutcomeCount("blocked"); } }
        public int NotSupportedCount { get { return OutcomeCount("notSupported"); } }

        public int SkippedCount
        {
            get { int count = 0; for (int index = 0; index < Steps.Count; index++) if (Steps[index].Skipped) count++; return count; }
        }
    }

    // Runs an accepted plan one step at a time through the operation probe that
    // already exists. Nothing new is sent to the target: every step goes through
    // the same read-only default, the same covered-point refusal and the same
    // five outcome values as a hand driven operation test.
    public sealed class PlanRunner
    {
        private const int SettleMs = 500;
        // Wider than the single operation test's 5,000 ms on purpose. After an
        // automatic scan the first deep probe of a step regularly spends its
        // whole 3,000 ms allowance and restarts the acquisition worker, which
        // leaves too little of a 5,000 ms budget for the operation itself.
        private const int StepBudgetMs = 8000;
        private volatile bool cancelled;

        public void Cancel()
        {
            cancelled = true;
        }

        public PlanRunResult Run(OperationPlan plan, int runNumber, bool writeEnabled, bool stopOnFailure, Action<PlanRunProgress> progress)
        {
            PlanRunResult result = new PlanRunResult();
            result.RunNumber = runNumber;
            result.StartedAt = DateTimeOffset.Now;
            result.WriteEnabled = writeEnabled;
            result.StopOnFailure = stopOnFailure;
            result.Title = plan == null ? null : plan.Title;
            if (plan == null || !plan.Accepted)
            {
                result.EndedAt = DateTimeOffset.Now;
                return result;
            }
            bool stopped = false;
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                PlanStep step = plan.Steps[index];
                PlanStepResult item = new PlanStepResult();
                item.Step = step;
                if (cancelled || stopped)
                {
                    item.Skipped = true;
                    item.SkipReason = cancelled
                        ? Messages.Text("plan-skip-cancelled.txt", "Stopped by the operator before this step.")
                        : Messages.Text("plan-skip-earlier.txt", "An earlier step did not succeed and the run was set to stop there.");
                    result.Steps.Add(item);
                    Report(progress, index, plan.Steps.Count, step, item, false);
                    continue;
                }
                Execute(step, item, writeEnabled);
                result.Steps.Add(item);
                Report(progress, index, plan.Steps.Count, step, item, false);
                if (stopOnFailure && !item.Succeeded && item.Outcome != "unknown") stopped = true;
                if (index + 1 < plan.Steps.Count && !cancelled && !stopped) Thread.Sleep(SettleMs);
            }
            result.Cancelled = cancelled;
            result.EndedAt = DateTimeOffset.Now;
            Report(progress, plan.Steps.Count, plan.Steps.Count, null, null, true);
            return result;
        }

        private static void Execute(PlanStep step, PlanStepResult item, bool writeEnabled)
        {
            ProbeArgs arguments = new ProbeArgs();
            arguments.WriteEnabled = writeEnabled;
            arguments.Value = step.Value;
            arguments.BudgetMs = StepBudgetMs;
            ProbeResult probe;
            try
            {
                probe = ProbeRunner.Run(step.Element, step.Kind, arguments);
            }
            catch (Exception exception)
            {
                item.Outcome = "failed";
                item.Method = "exception";
                item.ErrorCode = exception.GetType().Name;
                item.ErrorMessage = exception.Message;
                return;
            }
            item.Probe = probe;
            item.Outcome = probe.Outcome;
            item.Method = probe.Method;
            item.DurationMs = probe.DurationMs;
            if (probe.Error != null)
            {
                item.ErrorCode = probe.Error.Code;
                item.ErrorMessage = probe.Error.Message;
            }
            item.UsedIdentity = Identity(step);
            item.Reaction = Reaction(probe);
        }

        // What actually identified the part, so a later reader knows which
        // material survived and is worth building an automation on.
        private static string Identity(PlanStep step)
        {
            if (step.Node == null) return "point " + step.X + "," + step.Y;
            List<string> parts = new List<string>();
            if (!String.IsNullOrEmpty(step.Node.AutomationId)) parts.Add("AutomationId=" + step.Node.AutomationId);
            if (!String.IsNullOrEmpty(step.Node.Name)) parts.Add("Name=" + step.Node.Name);
            if (!String.IsNullOrEmpty(step.Node.ControlType)) parts.Add("ControlType=" + step.Node.ControlType);
            if (!String.IsNullOrEmpty(step.Node.ClassName)) parts.Add("class=" + step.Node.ClassName);
            if (step.Node.CtrlId != 0) parts.Add("ctrlId=" + step.Node.CtrlId.ToString(CultureInfo.InvariantCulture));
            if (step.Node.Hwnd != 0) parts.Add("hwnd=0x" + step.Node.Hwnd.ToString("X"));
            parts.Add("point=" + step.X + "," + step.Y);
            return String.Join(" ", parts.ToArray());
        }

        private static string Reaction(ProbeResult probe)
        {
            if (probe == null) return null;
            List<string> parts = new List<string>();
            if (probe.Before != null && probe.After != null)
            {
                if (!String.Equals(probe.Before.Value, probe.After.Value, StringComparison.Ordinal)) parts.Add("value");
                if (!String.Equals(probe.Before.State, probe.After.State, StringComparison.Ordinal)) parts.Add("state");
                if (!String.Equals(probe.Before.WindowTitle, probe.After.WindowTitle, StringComparison.Ordinal)) parts.Add("windowTitle");
                if (probe.Before.ChildCount != probe.After.ChildCount) parts.Add("childCount");
            }
            for (int index = 0; index < probe.SideEffects.Count; index++) parts.Add(probe.SideEffects[index].Type);
            return parts.Count == 0 ? null : String.Join(", ", parts.ToArray());
        }

        private static void Report(Action<PlanRunProgress> progress, int index, int total, PlanStep step, PlanStepResult item, bool finished)
        {
            if (progress == null) return;
            PlanRunProgress value = new PlanRunProgress();
            value.Index = index;
            value.Total = total;
            value.Step = step;
            value.Result = item;
            value.Finished = finished;
            progress(value);
        }
    }

    public static class PlanJson
    {
        public static JsonObject Plan(OperationPlan plan, int runNumber)
        {
            List<object> steps = new List<object>();
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                PlanStep step = plan.Steps[index];
                steps.Add(new JsonObject()
                    .Add("id", step.Id)
                    .Add("action", step.Action)
                    .Add("elementId", step.ElementId)
                    .Add("targetLabel", step.TargetLabel)
                    .Add("x", step.X)
                    .Add("y", step.Y)
                    .Add("hwnd", step.Element == null ? 0 : step.Element.Hwnd)
                    .Add("value", step.Value)
                    .Add("expect", step.Expect)
                    .Add("why", step.Why));
            }
            return new JsonObject()
                .Add("kind", "plan")
                .Add("runNumber", runNumber)
                .Add("format", PlanFormat.Marker)
                .Add("version", PlanFormat.Version)
                .Add("title", plan.Title)
                .Add("notes", plan.Notes)
                .Add("warnings", plan.Warnings.ToArray())
                .Add("ignoredFields", plan.Ignored.ToArray())
                .Add("steps", steps.ToArray());
        }

        public static JsonObject StepResult(PlanStepResult item, int runNumber)
        {
            return new JsonObject()
                .Add("kind", "plan.step")
                .Add("runNumber", runNumber)
                .Add("stepId", item.Step == null ? 0 : item.Step.Id)
                .Add("action", item.Step == null ? null : item.Step.Action)
                .Add("elementId", item.Step == null ? null : item.Step.ElementId)
                .Add("targetLabel", item.Step == null ? null : item.Step.TargetLabel)
                .Add("value", item.Step == null ? null : item.Step.Value)
                .Add("expect", item.Step == null ? null : item.Step.Expect)
                .Add("skipped", item.Skipped)
                .Add("skipReason", item.SkipReason)
                .Add("outcome", item.Skipped ? null : item.Outcome)
                .Add("method", item.Method)
                .Add("durationMs", item.DurationMs)
                .Add("usedIdentity", item.UsedIdentity)
                .Add("reaction", item.Reaction)
                .Add("probeId", item.Probe == null ? null : item.Probe.ProbeId)
                .Add("error", item.ErrorCode == null && item.ErrorMessage == null
                    ? null
                    : new JsonObject().Add("code", item.ErrorCode).Add("message", item.ErrorMessage));
        }

        public static JsonObject RunSummary(PlanRunResult result)
        {
            return new JsonObject()
                .Add("kind", "plan.run")
                .Add("runNumber", result.RunNumber)
                .Add("title", result.Title)
                .Add("startedAt", result.StartedAt)
                .Add("endedAt", result.EndedAt)
                .Add("writeEnabled", result.WriteEnabled)
                .Add("stopOnFailure", result.StopOnFailure)
                .Add("cancelled", result.Cancelled)
                .Add("steps", result.Steps.Count)
                .Add("success", result.SuccessCount)
                .Add("failed", result.FailedCount)
                .Add("blocked", result.BlockedCount)
                .Add("notSupported", result.NotSupportedCount)
                .Add("unknown", result.UnknownCount)
                .Add("skipped", result.SkippedCount);
        }
    }

    // The written record. A case is meant to become the source material for a
    // real automation later, so each step keeps the identity that was used and
    // the reaction that was seen, not just a pass or fail mark.
    public static class PlanMarkdown
    {
        public static string Plan(OperationPlan plan, int runNumber, string rawAnswer)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine();
            text.AppendLine("## " + CaseText.Plan + " #" + runNumber.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
            if (!String.IsNullOrWhiteSpace(plan.Title)) text.AppendLine("**" + Flat(plan.Title) + "**");
            if (!String.IsNullOrWhiteSpace(plan.Notes))
            {
                text.AppendLine();
                text.AppendLine(Flat(plan.Notes));
            }
            text.AppendLine();
            text.AppendLine("| # | " + Messages.Text("plan-col-action.txt", "operation") + " | " + Messages.Text("plan-col-target.txt", "target") +
                " | " + Messages.Text("plan-col-value.txt", "value") + " | " + Messages.Text("plan-col-expect.txt", "expected") + " |");
            text.AppendLine("|---|---|---|---|---|");
            for (int index = 0; index < plan.Steps.Count; index++)
            {
                PlanStep step = plan.Steps[index];
                text.AppendLine("| " + step.Id + " | " + step.Action +
                    " | " + Cell(step.ElementId == null ? "(" + step.X + "," + step.Y + ")" : step.ElementId + " " + step.TargetLabel) +
                    " | " + Cell(step.Value) + " | " + Cell(step.Expect) + " |");
            }
            if (plan.Warnings.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Messages.Text("plan-warn-heading.txt", "Noted before running:"));
                for (int index = 0; index < plan.Warnings.Count; index++) text.AppendLine("- " + Flat(plan.Warnings[index]));
            }
            if (plan.Ignored.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(Messages.Text("plan-ignored-heading.txt", "Fields in the answer that this tool does not use:") + " " +
                    Flat(String.Join(", ", plan.Ignored.ToArray())));
            }
            text.AppendLine();
            text.AppendLine("<details><summary>" + CaseText.Answer + " (answer-" + runNumber.ToString("00", CultureInfo.InvariantCulture) + ".txt)</summary>");
            text.AppendLine();
            text.AppendLine("```json");
            text.AppendLine((rawAnswer ?? String.Empty).TrimEnd());
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("</details>");
            text.AppendLine();
            return text.ToString();
        }

        public static string Run(PlanRunResult result)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine();
            text.AppendLine("## " + CaseText.Run + " #" + result.RunNumber.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("- " + Messages.Text("plan-run-when.txt", "Run at") + ": " +
                result.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("- " + Messages.Text("plan-run-mode.txt", "Changing operations") + ": " +
                (result.WriteEnabled ? Messages.Text("plan-run-write-on.txt", "allowed") : Messages.Text("plan-run-write-off.txt", "not allowed (read only)")));
            text.AppendLine("- " + Messages.Text("plan-run-counts.txt", "Result") + ": " +
                Messages.Text("plan-run-success.txt", "success") + " " + result.SuccessCount +
                " / " + Messages.Text("plan-run-failed.txt", "failed") + " " + result.FailedCount +
                " / " + Messages.Text("plan-run-blocked.txt", "refused") + " " + result.BlockedCount +
                " / " + Messages.Text("plan-run-notsupported.txt", "not supported") + " " + result.NotSupportedCount +
                " / " + Messages.Text("plan-run-unknown.txt", "no change seen") + " " + result.UnknownCount +
                " / " + Messages.Text("plan-run-skipped.txt", "not run") + " " + result.SkippedCount);
            if (result.Cancelled) text.AppendLine("- " + Messages.Text("plan-run-cancelled.txt", "The operator stopped this run."));
            text.AppendLine();
            text.AppendLine("| # | " + Messages.Text("plan-col-action.txt", "operation") + " | " + Messages.Text("plan-col-target.txt", "target") +
                " | " + Messages.Text("plan-col-outcome.txt", "outcome") + " | " + Messages.Text("plan-col-method.txt", "route used") +
                " | " + Messages.Text("plan-col-reaction.txt", "reaction seen") + " | ms |");
            text.AppendLine("|---|---|---|---|---|---|---|");
            for (int index = 0; index < result.Steps.Count; index++)
            {
                PlanStepResult item = result.Steps[index];
                PlanStep step = item.Step;
                text.AppendLine("| " + (step == null ? 0 : step.Id) +
                    " | " + (step == null ? "-" : step.Action) +
                    " | " + Cell(step == null ? null : (step.ElementId == null ? "(" + step.X + "," + step.Y + ")" : step.ElementId + " " + step.TargetLabel)) +
                    " | " + (item.Skipped ? Messages.Text("plan-run-skipped.txt", "not run") : item.Outcome) +
                    " | " + Cell(item.Method) +
                    " | " + Cell(item.Reaction) +
                    " | " + (item.Skipped ? "-" : item.DurationMs.ToString(CultureInfo.InvariantCulture)) + " |");
            }
            text.AppendLine();
            text.AppendLine("### " + Messages.Text("plan-run-detail.txt", "What identified each part"));
            text.AppendLine();
            for (int index = 0; index < result.Steps.Count; index++)
            {
                PlanStepResult item = result.Steps[index];
                if (item.Step == null) continue;
                text.AppendLine("- **" + item.Step.Id + " " + item.Step.Action + "** " +
                    (item.Skipped ? Messages.Text("plan-run-skipped.txt", "not run") + ": " + Flat(item.SkipReason) : item.Outcome));
                if (!String.IsNullOrEmpty(item.UsedIdentity)) text.AppendLine("    - " + Messages.Text("plan-run-identity.txt", "identified by") + ": `" + Flat(item.UsedIdentity) + "`");
                if (!String.IsNullOrEmpty(item.Method)) text.AppendLine("    - " + Messages.Text("plan-col-method.txt", "route used") + ": `" + Flat(item.Method) + "`");
                if (!String.IsNullOrEmpty(item.Reaction)) text.AppendLine("    - " + Messages.Text("plan-col-reaction.txt", "reaction seen") + ": " + Flat(item.Reaction));
                if (!String.IsNullOrEmpty(item.Step.Expect)) text.AppendLine("    - " + Messages.Text("plan-col-expect.txt", "expected") + ": " + Flat(item.Step.Expect));
                if (!String.IsNullOrEmpty(item.ErrorMessage)) text.AppendLine("    - " + Messages.Text("plan-run-reason.txt", "reason") + ": " + Flat(item.ErrorCode) + " " + Flat(item.ErrorMessage));
            }
            text.AppendLine();
            return text.ToString();
        }

        private static string Flat(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static string Cell(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            string flat = value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 70 ? flat : flat.Substring(0, 69) + "...";
        }
    }
}
