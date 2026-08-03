namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One case is one folder. The screenshot, the investigation log handed to the
    // assistant, the request, the answer and every operation result live side by
    // side, and case.md is the record that ties them together for a reader.
    public sealed class CaseRecord
    {
        public string CaseId;
        public string Folder;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset UpdatedAt;
        public string TargetTitle;
        public string TargetProcess;
        public int TargetProcessId;
        public string Goal;
        public string SessionFolder;
        public string ShotFile;
        public int ElementCount;
        public int RunCount;
        public int StepCount;
        public int SuccessCount;
        public int FailureCount;
        // Which handoff bundle the assistant was given, and what it was allowed
        // to assume while writing its answer.
        public string BundleId;
        public string PremiseHash;
        public string ScanId;
        // collecting -> requested -> imported -> ran
        public string Status = "collecting";

        public string MarkdownPath { get { return Folder == null ? null : Path.Combine(Folder, "case.md"); } }
        public string IndexPath { get { return Folder == null ? null : Path.Combine(Folder, "case.json"); } }
        public string InvestigationPath { get { return Folder == null ? null : Path.Combine(Folder, "investigation.md"); } }
        public string RequestPath { get { return Folder == null ? null : Path.Combine(Folder, "request.txt"); } }
        public string ShotFolder { get { return Folder == null ? null : Path.Combine(Folder, "shots"); } }
        // One folder holding exactly the files to attach, so "attach these" has
        // a single unambiguous answer.
        public string HandoffFolder { get { return Folder == null ? null : Path.Combine(Folder, "handoff"); } }
        public string HandoffTextPath { get { return HandoffFolder == null ? null : Path.Combine(HandoffFolder, HandoffBuilder.TextFileName); } }
        public string HandoffPdfPath { get { return HandoffFolder == null ? null : Path.Combine(HandoffFolder, HandoffBuilder.PdfFileName); } }
        public string ScreensPath { get { return Folder == null ? null : Path.Combine(Folder, HandoffBuilder.LedgerFileName); } }
        public string HandoffRecordPath { get { return Folder == null ? null : Path.Combine(Folder, HandoffBuilder.RecordFileName); } }

        public string AnswerPath(int run) { return Folder == null ? null : Path.Combine(Folder, "answer-" + run.ToString("00", CultureInfo.InvariantCulture) + ".txt"); }
        public string PlanPath(int run) { return Folder == null ? null : Path.Combine(Folder, "plan-" + run.ToString("00", CultureInfo.InvariantCulture) + ".json"); }
        public string RunPath(int run) { return Folder == null ? null : Path.Combine(Folder, "run-" + run.ToString("00", CultureInfo.InvariantCulture) + ".jsonl"); }
    }

    public static class CaseStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static string lastError;

        public static string LastError { get { return lastError; } }

        public static string Root(string baseDir)
        {
            return Path.Combine(baseDir, "runtime", "cases");
        }

        public static CaseRecord Create(string baseDir, TargetWindowInfo target, string processName, string sessionFolder)
        {
            CaseRecord record = new CaseRecord();
            record.CreatedAt = DateTimeOffset.Now;
            record.UpdatedAt = record.CreatedAt;
            record.CaseId = "case-" + record.CreatedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            record.TargetTitle = target == null ? null : target.Title;
            record.TargetProcess = processName;
            record.TargetProcessId = target == null ? 0 : target.ProcessId;
            record.SessionFolder = sessionFolder;
            string folder = Path.Combine(Root(baseDir), record.CaseId);
            // A second case inside the same second keeps its own folder rather
            // than writing into the first one.
            int suffix = 2;
            while (Directory.Exists(folder))
            {
                folder = Path.Combine(Root(baseDir), record.CaseId + "_" + suffix.ToString(CultureInfo.InvariantCulture));
                suffix++;
            }
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "shots"));
            record.Folder = folder;
            record.CaseId = Path.GetFileName(folder);

            StringBuilder head = new StringBuilder();
            head.AppendLine("# " + record.CaseId);
            head.AppendLine();
            head.AppendLine("| | |");
            head.AppendLine("|---|---|");
            head.AppendLine("| " + CaseText.Started + " | " + record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " |");
            head.AppendLine("| " + CaseText.Target + " | " + Cell(record.TargetProcess) + " / " + Cell(record.TargetTitle) + " (pid " + record.TargetProcessId + ") |");
            head.AppendLine("| " + CaseText.SessionFolder + " | " + Cell(record.SessionFolder) + " |");
            head.AppendLine();
            AppendMarkdown(record, head.ToString());
            Save(record);
            return record;
        }

        public static bool AppendMarkdown(CaseRecord record, string text)
        {
            if (record == null || record.Folder == null || String.IsNullOrEmpty(text)) return false;
            try
            {
                File.AppendAllText(record.MarkdownPath, text, Utf8);
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public static bool WriteText(CaseRecord record, string path, string text)
        {
            if (record == null || path == null) return false;
            try
            {
                File.WriteAllText(path, text ?? String.Empty, Utf8);
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public static bool AppendLine(CaseRecord record, string path, string line)
        {
            if (record == null || path == null) return false;
            try
            {
                File.AppendAllText(path, line + "\n", Utf8);
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public static string ReadText(string path)
        {
            try
            {
                return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        public static bool Save(CaseRecord record)
        {
            if (record == null || record.Folder == null) return false;
            record.UpdatedAt = DateTimeOffset.Now;
            try
            {
                JsonWriter.WriteFile(record.IndexPath, new JsonObject()
                    .Add("kind", "case")
                    .Add("caseId", record.CaseId)
                    .Add("createdAt", record.CreatedAt)
                    .Add("updatedAt", record.UpdatedAt)
                    .Add("status", record.Status)
                    .Add("targetTitle", record.TargetTitle)
                    .Add("targetProcess", record.TargetProcess)
                    .Add("targetProcessId", record.TargetProcessId)
                    .Add("goal", record.Goal)
                    .Add("sessionFolder", record.SessionFolder)
                    .Add("shotFile", record.ShotFile)
                    .Add("elementCount", record.ElementCount)
                    .Add("bundleId", record.BundleId)
                    .Add("premiseHash", record.PremiseHash)
                    .Add("scanId", record.ScanId)
                    .Add("runCount", record.RunCount)
                    .Add("stepCount", record.StepCount)
                    .Add("successCount", record.SuccessCount)
                    .Add("failureCount", record.FailureCount));
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        // Newest first. A folder whose index cannot be read is still listed, so a
        // damaged case is visible instead of quietly missing from the history.
        public static CaseRecord[] List(string baseDir)
        {
            List<CaseRecord> records = new List<CaseRecord>();
            string root = Root(baseDir);
            if (!Directory.Exists(root)) return records.ToArray();
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception exception)
            {
                lastError = exception.GetType().Name + ": " + exception.Message;
                return records.ToArray();
            }
            for (int index = 0; index < folders.Length; index++)
            {
                records.Add(Load(folders[index]));
            }
            records.Sort(delegate(CaseRecord first, CaseRecord second)
            {
                return second.CreatedAt.CompareTo(first.CreatedAt);
            });
            return records.ToArray();
        }

        public static CaseRecord Load(string folder)
        {
            CaseRecord record = new CaseRecord();
            record.Folder = folder;
            record.CaseId = Path.GetFileName(folder);
            record.Status = "unreadable";
            try
            {
                record.CreatedAt = Directory.GetCreationTime(folder);
            }
            catch
            {
            }
            record.UpdatedAt = record.CreatedAt;
            string text = ReadText(Path.Combine(folder, "case.json"));
            Dictionary<string, object> item = text == null ? null : JsonReader.ReadObject(text);
            if (item == null) return record;
            record.Status = JsonReader.Text(item, "status") ?? "unknown";
            record.TargetTitle = JsonReader.Text(item, "targetTitle");
            record.TargetProcess = JsonReader.Text(item, "targetProcess");
            record.TargetProcessId = JsonReader.Number(item, "targetProcessId", 0);
            record.Goal = JsonReader.Text(item, "goal");
            record.SessionFolder = JsonReader.Text(item, "sessionFolder");
            record.ShotFile = JsonReader.Text(item, "shotFile");
            record.ElementCount = JsonReader.Number(item, "elementCount", 0);
            record.BundleId = JsonReader.Text(item, "bundleId");
            record.PremiseHash = JsonReader.Text(item, "premiseHash");
            record.ScanId = JsonReader.Text(item, "scanId");
            record.RunCount = JsonReader.Number(item, "runCount", 0);
            record.StepCount = JsonReader.Number(item, "stepCount", 0);
            record.SuccessCount = JsonReader.Number(item, "successCount", 0);
            record.FailureCount = JsonReader.Number(item, "failureCount", 0);
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(JsonReader.Text(item, "createdAt"), null, DateTimeStyles.None, out parsed)) record.CreatedAt = parsed;
            if (DateTimeOffset.TryParse(JsonReader.Text(item, "updatedAt"), null, DateTimeStyles.None, out parsed)) record.UpdatedAt = parsed;
            return record;
        }

        private static string Cell(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }

    // Wording used inside the written record. Kept beside the writer because a
    // saved file has to stay readable even after the message assets change.
    public static class CaseText
    {
        public static string Started { get { return Messages.Text("case-md-started.txt", "Started"); } }
        public static string Target { get { return Messages.Text("case-md-target.txt", "Target"); } }
        public static string SessionFolder { get { return Messages.Text("case-md-session.txt", "Investigation log folder"); } }
        public static string Screenshot { get { return Messages.Text("case-md-shot.txt", "Screenshot"); } }
        public static string Goal { get { return Messages.Text("case-md-goal.txt", "What the operator wants to do"); } }
        public static string Investigation { get { return Messages.Text("case-md-investigation.txt", "Investigation handed over"); } }
        public static string Handoff { get { return Messages.Text("case-md-handoff.txt", "Text attached to the request"); } }
        public static string Screens { get { return Messages.Text("case-md-screens.txt", "Pictures attached to the request"); } }
        public static string Request { get { return Messages.Text("case-md-request.txt", "Request text"); } }
        public static string Answer { get { return Messages.Text("case-md-answer.txt", "Answer taken in"); } }
        public static string Plan { get { return Messages.Text("case-md-plan.txt", "Operations to try"); } }
        public static string Run { get { return Messages.Text("case-md-run.txt", "Operation test"); } }
        public static string Elements { get { return Messages.Text("case-md-elements.txt", "Parts found"); } }
    }
}
