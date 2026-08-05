namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One file of the automation. A PowerShell script and a VBA module are the
    // same kind of thing here, so neither is stored as an afterthought of the
    // other.
    public sealed class CodeFile
    {
        public string Language;
        public string Name;
        public string Text = "";

        public string FileName
        {
            get { return Name + "." + ScriptLanguages.Extension(Language); }
        }

        public CodeFile Copy()
        {
            CodeFile copy = new CodeFile();
            copy.Language = Language;
            copy.Name = Name;
            copy.Text = Text;
            return copy;
        }
    }

    // Everything the code screen holds for one session.
    //
    // Three versions are kept at all times: the one the recording produced, the
    // one on screen, and the one that was on screen before the last change was
    // taken in. Nothing an assistant returns can therefore leave the operator
    // without a way back, which is the whole point of never overwriting
    // silently.
    public sealed class CodeProject
    {
        public const string GeneratedName = "RecordedProcedure";

        private readonly string folder;
        private readonly List<CodeFile> baseline = new List<CodeFile>();
        private readonly List<CodeFile> current = new List<CodeFile>();
        private readonly List<CodeFile> previous = new List<CodeFile>();

        public string Language = ScriptLanguages.PowerShell;
        public string RequestId;
        public ScriptPlan Plan;
        public bool HasPrevious;

        private CodeProject(string codeFolder)
        {
            folder = codeFolder;
        }

        public string Folder { get { return folder; } }

        public static string FolderFor(StudioSession session)
        {
            if (session == null || session.Folder == null) return null;
            return Path.Combine(session.Folder, "code");
        }

        // Builds both languages from the recording. Called once per session;
        // afterwards what is on disk is what the operator has been editing.
        public static CodeProject Open(StudioSession session)
        {
            CodeProject project = new CodeProject(FolderFor(session));
            project.Plan = ScriptModel.Build(session);
            project.baseline.Add(Generated(ScriptLanguages.PowerShell, PowerShellGen.Build(project.Plan, session)));
            project.baseline.Add(Generated(ScriptLanguages.Vba, VbaGen.Build(project.Plan, session)));
            for (int index = 0; index < project.baseline.Count; index++) project.current.Add(project.baseline[index].Copy());
            project.Load();
            return project;
        }

        private static CodeFile Generated(string language, string text)
        {
            CodeFile file = new CodeFile();
            file.Language = language;
            file.Name = GeneratedName;
            file.Text = text;
            return file;
        }

        public List<CodeFile> Files(string language)
        {
            List<CodeFile> found = new List<CodeFile>();
            for (int index = 0; index < current.Count; index++)
            {
                if (String.Equals(current[index].Language, language, StringComparison.Ordinal)) found.Add(current[index]);
            }
            return found;
        }

        public List<CodeFile> All()
        {
            List<CodeFile> copy = new List<CodeFile>();
            for (int index = 0; index < current.Count; index++) copy.Add(current[index]);
            return copy;
        }

        public CodeFile Find(string language, string name)
        {
            for (int index = 0; index < current.Count; index++)
            {
                if (String.Equals(current[index].Language, language, StringComparison.Ordinal) &&
                    String.Equals(current[index].Name, name, StringComparison.OrdinalIgnoreCase)) return current[index];
            }
            return null;
        }

        public CodeFile BaselineOf(string language, string name)
        {
            for (int index = 0; index < baseline.Count; index++)
            {
                if (String.Equals(baseline[index].Language, language, StringComparison.Ordinal) &&
                    String.Equals(baseline[index].Name, name, StringComparison.OrdinalIgnoreCase)) return baseline[index];
            }
            return null;
        }

        public void SetText(string language, string name, string text)
        {
            CodeFile file = Find(language, name);
            if (file == null)
            {
                file = new CodeFile();
                file.Language = language;
                file.Name = name;
                current.Add(file);
            }
            file.Text = text == null ? "" : text;
        }

        // Takes an accepted answer in. The version that was on screen is kept
        // first, so the operator can put it back if the change turns out to be
        // wrong.
        public void Apply(List<CodeFile> incoming)
        {
            if (incoming == null || incoming.Count == 0) return;
            Snapshot();
            for (int index = 0; index < incoming.Count; index++)
            {
                CodeFile file = incoming[index];
                SetText(file.Language, file.Name, file.Text);
            }
            HasPrevious = true;
        }

        private void Snapshot()
        {
            previous.Clear();
            for (int index = 0; index < current.Count; index++) previous.Add(current[index].Copy());
        }

        // Back to what was on screen before the last change was taken in.
        public bool UndoApply()
        {
            if (!HasPrevious || previous.Count == 0) return false;
            current.Clear();
            for (int index = 0; index < previous.Count; index++) current.Add(previous[index].Copy());
            previous.Clear();
            HasPrevious = false;
            return true;
        }

        // Back to what the recording produced. Files that only ever came from an
        // assistant are dropped, because the recording never made them.
        public void RestoreBaseline(string language)
        {
            Snapshot();
            HasPrevious = true;
            List<CodeFile> kept = new List<CodeFile>();
            for (int index = 0; index < current.Count; index++)
            {
                if (!String.Equals(current[index].Language, language, StringComparison.Ordinal)) kept.Add(current[index]);
            }
            for (int index = 0; index < baseline.Count; index++)
            {
                if (String.Equals(baseline[index].Language, language, StringComparison.Ordinal)) kept.Add(baseline[index].Copy());
            }
            current.Clear();
            for (int index = 0; index < kept.Count; index++) current.Add(kept[index]);
        }

        public bool DiffersFromBaseline(string language)
        {
            List<CodeFile> now = Files(language);
            int generated = 0;
            for (int index = 0; index < baseline.Count; index++)
            {
                if (String.Equals(baseline[index].Language, language, StringComparison.Ordinal)) generated++;
            }
            if (now.Count != generated) return true;
            for (int index = 0; index < now.Count; index++)
            {
                CodeFile original = BaselineOf(language, now[index].Name);
                if (original == null) return true;
                if (!String.Equals(original.Text, now[index].Text, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Saving is not a separate act the operator has to remember, the same
        // way a recording is not. Whatever is on screen is written when it
        // changes, and a failure to write says so instead of being swallowed.
        public string Save()
        {
            if (folder == null) return "This session has no folder on disk.";
            try
            {
                Directory.CreateDirectory(folder);
                WriteSet(Path.Combine(folder, "current"), current);
                WriteSet(Path.Combine(folder, "baseline"), baseline);
                JsonObject meta = new JsonObject()
                    .Add("kind", "codeProject")
                    .Add("language", Language)
                    .Add("requestId", RequestId)
                    .Add("files", Names());
                JsonWriter.WriteFile(Path.Combine(folder, "code.json"), meta);
                return null;
            }
            catch (Exception exception)
            {
                return exception.GetType().Name + ": " + exception.Message;
            }
        }

        private object[] Names()
        {
            List<object> names = new List<object>();
            for (int index = 0; index < current.Count; index++)
            {
                names.Add(current[index].Language + "/" + current[index].FileName);
            }
            return names.ToArray();
        }

        private static void WriteSet(string into, List<CodeFile> files)
        {
            Directory.CreateDirectory(into);
            for (int index = 0; index < files.Count; index++)
            {
                File.WriteAllText(Path.Combine(into, files[index].FileName), files[index].Text, new UTF8Encoding(false));
            }
        }

        // Reads back whatever was on screen last time. The baseline on disk is
        // ignored on purpose: it is regenerated from the recording every time,
        // so a change to the generator reaches an old session too.
        private void Load()
        {
            if (folder == null) return;
            try
            {
                Dictionary<string, object> meta = SessionStore.ReadJsonFile(Path.Combine(folder, "code.json"));
                if (meta != null)
                {
                    string language = JsonReader.Text(meta, "language");
                    if (ScriptLanguages.IsKnown(language)) Language = language;
                    RequestId = JsonReader.Text(meta, "requestId");
                }
                string saved = Path.Combine(folder, "current");
                if (!Directory.Exists(saved)) return;
                string[] paths = Directory.GetFiles(saved);
                if (paths.Length == 0) return;
                current.Clear();
                Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < paths.Length; index++)
                {
                    string extension = Path.GetExtension(paths[index]);
                    string language = String.Equals(extension, ".bas", StringComparison.OrdinalIgnoreCase)
                        ? ScriptLanguages.Vba : ScriptLanguages.PowerShell;
                    CodeFile file = new CodeFile();
                    file.Language = language;
                    file.Name = Path.GetFileNameWithoutExtension(paths[index]);
                    file.Text = File.ReadAllText(paths[index]);
                    current.Add(file);
                }
            }
            catch
            {
                // A code folder that cannot be read is not a reason to lose the
                // freshly generated version, which is already in place.
            }
        }

        public string Summary(string language)
        {
            List<CodeFile> files = Files(language);
            int lines = 0;
            for (int index = 0; index < files.Count; index++) lines += LineCount(files[index].Text);
            return files.Count.ToString(CultureInfo.InvariantCulture) + " / " + lines.ToString(CultureInfo.InvariantCulture);
        }

        public static int LineCount(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            int count = 1;
            for (int index = 0; index < text.Length; index++) if (text[index] == '\n') count++;
            return count;
        }
    }
}
