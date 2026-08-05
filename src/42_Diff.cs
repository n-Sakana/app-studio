namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    public sealed class DiffLine
    {
        public const string Same = "same";
        public const string Added = "added";
        public const string Removed = "removed";

        public string Kind = Same;
        public string Text = "";
        public int LeftNumber = -1;
        public int RightNumber = -1;
    }

    public sealed class FileDiff
    {
        public string Language;
        public string Name;
        public bool IsNew;
        public List<DiffLine> Lines = new List<DiffLine>();

        public int AddedCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Lines.Count; index++) if (Lines[index].Kind == DiffLine.Added) count++;
                return count;
            }
        }

        public int RemovedCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Lines.Count; index++) if (Lines[index].Kind == DiffLine.Removed) count++;
                return count;
            }
        }

        public bool Changed { get { return AddedCount > 0 || RemovedCount > 0; } }

        public string Summary
        {
            get
            {
                return "+" + AddedCount.ToString(CultureInfo.InvariantCulture) + " / -" +
                    RemovedCount.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    // What would change if an answer were taken in.
    //
    // The point of showing this is that nothing is applied without it. A
    // longest common subsequence is enough here: the files are one automation
    // each, not a repository, and a reader has to be able to see every line
    // that would go and every line that would arrive.
    public static class Diff
    {
        // Above this the table is what changed plus a little of what did not.
        // A whole regenerated file otherwise reads as thousands of identical
        // rows with the change buried in them.
        public const int Context = 3;

        public static List<FileDiff> Compare(CodeProject project, List<IntakeFile> incoming)
        {
            List<FileDiff> diffs = new List<FileDiff>();
            if (incoming == null) return diffs;
            for (int index = 0; index < incoming.Count; index++)
            {
                IntakeFile file = incoming[index];
                CodeFile held = project == null ? null : project.Find(file.Language, file.Name);
                FileDiff diff = Of(held == null ? "" : held.Text, file.Text);
                diff.Language = file.Language;
                diff.Name = file.Name;
                diff.IsNew = held == null;
                diffs.Add(diff);
            }
            return diffs;
        }

        public static FileDiff Of(string before, string after)
        {
            FileDiff diff = new FileDiff();
            string[] left = Lines(before);
            string[] right = Lines(after);
            int[,] table = new int[left.Length + 1, right.Length + 1];
            for (int a = left.Length - 1; a >= 0; a--)
            {
                for (int b = right.Length - 1; b >= 0; b--)
                {
                    if (String.Equals(left[a], right[b], StringComparison.Ordinal)) table[a, b] = table[a + 1, b + 1] + 1;
                    else table[a, b] = Math.Max(table[a + 1, b], table[a, b + 1]);
                }
            }
            int x = 0;
            int y = 0;
            while (x < left.Length && y < right.Length)
            {
                if (String.Equals(left[x], right[y], StringComparison.Ordinal))
                {
                    diff.Lines.Add(Line(DiffLine.Same, left[x], x + 1, y + 1));
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    diff.Lines.Add(Line(DiffLine.Removed, left[x], x + 1, -1));
                    x++;
                }
                else
                {
                    diff.Lines.Add(Line(DiffLine.Added, right[y], -1, y + 1));
                    y++;
                }
            }
            while (x < left.Length)
            {
                diff.Lines.Add(Line(DiffLine.Removed, left[x], x + 1, -1));
                x++;
            }
            while (y < right.Length)
            {
                diff.Lines.Add(Line(DiffLine.Added, right[y], -1, y + 1));
                y++;
            }
            return diff;
        }

        // The changed lines with a little of what surrounds them, so a reader
        // can see where a change landed. Everything left out is unchanged, and
        // the caller says how much that was rather than hiding it.
        public static List<DiffLine> Interesting(FileDiff diff, out int hidden)
        {
            hidden = 0;
            List<DiffLine> kept = new List<DiffLine>();
            if (diff == null) return kept;
            bool[] keep = new bool[diff.Lines.Count];
            for (int index = 0; index < diff.Lines.Count; index++)
            {
                if (diff.Lines[index].Kind == DiffLine.Same) continue;
                int from = Math.Max(0, index - Context);
                int to = Math.Min(diff.Lines.Count - 1, index + Context);
                for (int near = from; near <= to; near++) keep[near] = true;
            }
            for (int index = 0; index < diff.Lines.Count; index++)
            {
                if (keep[index]) kept.Add(diff.Lines[index]);
                else hidden++;
            }
            return kept;
        }

        private static DiffLine Line(string kind, string text, int left, int right)
        {
            DiffLine line = new DiffLine();
            line.Kind = kind;
            line.Text = text;
            line.LeftNumber = left;
            line.RightNumber = right;
            return line;
        }

        private static string[] Lines(string text)
        {
            if (String.IsNullOrEmpty(text)) return new string[0];
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }
    }
}
