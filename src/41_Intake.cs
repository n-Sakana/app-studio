namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    public sealed class IntakeFile
    {
        public string Language;
        public string Name;
        public string Text = "";
    }

    // What one paste turned out to be. Either files, or a refusal with a reason,
    // or a statement of what is wrong with it - never silence, and never a
    // partial application of something that did not add up.
    public sealed class IntakeResult
    {
        public bool Ok;
        public string Reason = "";
        public string Message = "";
        public string RequestId;
        public string Summary = "";
        public string NoChange;
        public int PartIndex = -1;
        public int PartTotal = -1;
        public bool Added;
        public List<IntakeFile> Files = new List<IntakeFile>();

        public bool HasPart { get { return PartIndex >= 0 && PartTotal > 0; } }
    }

    // The parts of an answer that arrives one file per message.
    public sealed class IntakeParts
    {
        public int Total;
        public List<int> Order = new List<int>();
        public List<IntakeFile> Files = new List<IntakeFile>();
        public List<string> Summaries = new List<string>();

        public List<int> Missing()
        {
            List<int> missing = new List<int>();
            if (Total <= 0) return missing;
            for (int index = 0; index < Total; index++)
            {
                if (!Order.Contains(index)) missing.Add(index);
            }
            return missing;
        }

        public bool Complete
        {
            get { return Total > 0 && Order.Count == Total && Missing().Count == 0; }
        }

        public string MissingText()
        {
            List<int> missing = Missing();
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < missing.Count; index++)
            {
                if (index != 0) text.Append(", ");
                text.Append((missing[index] + 1).ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }
    }

    // Reads what came back from an assistant.
    //
    // One answer, one code block. Every file in it is wrapped in sentinel lines
    // carrying the request id this application issued, so a stale answer or an
    // answer to somebody else's request can never be applied by accident.
    //
    // Nothing here is lenient about shape. An answer that is cut off, that
    // counts its files wrongly, that sends the same file twice or that asks a
    // question instead of answering is refused with the reason, because a
    // half-read answer applied to somebody's automation is worse than no answer.
    public static class Intake
    {
        public const string ReasonEmpty = "empty";
        public const string ReasonNoSentinel = "noSentinel";
        public const string ReasonOtherRequest = "otherRequest";
        public const string ReasonTruncated = "truncated";
        public const string ReasonMismatch = "mismatch";
        public const string ReasonDuplicate = "duplicate";
        public const string ReasonUnknownLanguage = "unknownLanguage";
        public const string ReasonBadName = "badName";
        public const string ReasonEmptyFile = "emptyFile";
        public const string ReasonPartShape = "partShape";
        public const string ReasonPartMissing = "partMissing";
        public const string ReasonPartMultipleFiles = "partMultipleFiles";
        public const string ReasonPartTotalMismatch = "partTotalMismatch";
        public const string ReasonPartConflict = "partConflict";
        public const string ReasonPartDuplicateFile = "partDuplicateFile";
        public const string ReasonNoChangeVerdict = "noChangeVerdict";
        public const string ReasonNoChangeReason = "noChangeReason";
        public const string ReasonNoChangeContradiction = "noChangeContradiction";
        public const string ReasonQuestionNotAllowed = "questionNotAllowed";

        private static readonly string[] Verdicts = new string[] { "UNNECESSARY", "IMPOSSIBLE", "UNCLEAR" };

        // A Japanese keyboard produces these where an ASCII one produces a
        // space, a hash and an at sign. A chat client that substitutes them has
        // not made the answer wrong, so they are read as what they stand for.
        private const char IdeographicSpace = '\u3000';
        private const string FullwidthHash = "\uFF03";
        private const string FullwidthAt = "\uFF20";

        public static IntakeResult Parse(string text, string requestId)
        {
            if (!Handoff.IsRequestId(requestId)) return Fail(ReasonOtherRequest);
            if (text == null || Blank(text)) return Fail(ReasonEmpty);

            List<string> lines = Normalize(Split(text));
            IntakeResult result = new IntakeResult();
            result.RequestId = requestId;
            List<string> body = new List<string>();
            List<string> summary = new List<string>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string openName = null;
            string openLanguage = null;
            bool inSummary = false;
            bool sawMarker = false;
            string completed = null;

            for (int index = 0; index < lines.Count; index++)
            {
                string[] parts = Sentinel(lines[index]);
                if (parts == null)
                {
                    if (openName != null) body.Add(lines[index]);
                    else if (inSummary) summary.Add(lines[index]);
                    continue;
                }
                sawMarker = true;
                if (!String.Equals(parts[0], requestId, StringComparison.OrdinalIgnoreCase)) return Fail(ReasonOtherRequest);
                string directive = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";

                // The reply may not ask anything back. A request that cannot be
                // settled comes back as a refusal with a reason, not as a
                // question, so a question is refused here rather than turned
                // into a conversation.
                if (String.Equals(directive, "DECISION", StringComparison.Ordinal) ||
                    String.Equals(directive, "QUESTION", StringComparison.Ordinal) ||
                    String.Equals(directive, "OPTIONS", StringComparison.Ordinal))
                {
                    return Fail(ReasonQuestionNotAllowed);
                }
                if (String.Equals(directive, "REQUEST", StringComparison.Ordinal))
                {
                    // The request this application sent, pasted back. It is not
                    // an answer and is not read as one.
                    return Fail(ReasonNoSentinel);
                }
                if (String.Equals(directive, "SUMMARY", StringComparison.Ordinal))
                {
                    if (openName != null || parts.Length != 3) return Fail(ReasonMismatch);
                    string which = parts[2].ToUpperInvariant();
                    if (String.Equals(which, "BEGIN", StringComparison.Ordinal))
                    {
                        if (inSummary) return Fail(ReasonMismatch);
                        inSummary = true;
                        continue;
                    }
                    if (String.Equals(which, "END", StringComparison.Ordinal))
                    {
                        if (!inSummary) return Fail(ReasonMismatch);
                        inSummary = false;
                        continue;
                    }
                    return Fail(ReasonMismatch);
                }
                if (inSummary) return Fail(ReasonMismatch);
                if (String.Equals(directive, "PART", StringComparison.Ordinal))
                {
                    if (openName != null || result.HasPart) return Fail(ReasonMismatch);
                    if (parts.Length != 5 || !String.Equals(parts[3].ToUpperInvariant(), "OF", StringComparison.Ordinal)) return Fail(ReasonPartShape);
                    int partIndex = Count(parts[2]);
                    int partTotal = Count(parts[4]);
                    if (partIndex < 0 || partTotal < 1 || partIndex >= partTotal) return Fail(ReasonPartShape);
                    result.PartIndex = partIndex;
                    result.PartTotal = partTotal;
                    continue;
                }
                if (String.Equals(directive, "NOCHANGE", StringComparison.Ordinal))
                {
                    if (result.NoChange != null) return Fail(ReasonMismatch);
                    if (parts.Length != 3) return Fail(ReasonNoChangeVerdict);
                    string verdict = parts[2].ToUpperInvariant();
                    if (Array.IndexOf(Verdicts, verdict) < 0) return Fail(ReasonNoChangeVerdict);
                    result.NoChange = verdict;
                    continue;
                }
                if (String.Equals(directive, "BEGIN", StringComparison.Ordinal))
                {
                    if (openName != null || parts.Length != 4) return Fail(ReasonMismatch);
                    string language = parts[2].ToLowerInvariant();
                    string name = parts[3];
                    if (!ScriptLanguages.IsKnown(language)) return Fail(ReasonUnknownLanguage);
                    if (!IsName(name)) return Fail(ReasonBadName);
                    string key = language + "/" + name;
                    if (seen.ContainsKey(key)) return Fail(ReasonDuplicate);
                    openLanguage = language;
                    openName = name;
                    body.Clear();
                    continue;
                }
                if (String.Equals(directive, "END", StringComparison.Ordinal))
                {
                    if (openName == null || parts.Length != 4) return Fail(ReasonMismatch);
                    if (!String.Equals(parts[2].ToLowerInvariant(), openLanguage, StringComparison.Ordinal) ||
                        !String.Equals(parts[3], openName, StringComparison.Ordinal)) return Fail(ReasonMismatch);
                    if (Blank(Join(body))) return Fail(ReasonEmptyFile);
                    seen[openLanguage + "/" + openName] = true;
                    IntakeFile file = new IntakeFile();
                    file.Language = openLanguage;
                    file.Name = openName;
                    file.Text = Join(body);
                    result.Files.Add(file);
                    openName = null;
                    openLanguage = null;
                    body.Clear();
                    continue;
                }
                if (String.Equals(directive, "COMPLETE", StringComparison.Ordinal))
                {
                    if (openName != null) return Fail(ReasonTruncated);
                    if (completed != null || parts.Length != 3 || Count(parts[2]) < 0) return Fail(ReasonMismatch);
                    completed = parts[2];
                    continue;
                }
                return Fail(ReasonMismatch);
            }

            if (openName != null || inSummary) return Fail(ReasonTruncated);
            if (!sawMarker) return Fail(ReasonNoSentinel);
            result.Summary = Trim(summary);
            if (result.NoChange != null)
            {
                // Saying "nothing to change" and then sending files is a
                // contradiction, and a verdict with no reason behind it is not
                // something to show anyone.
                if (result.Files.Count > 0) return Fail(ReasonNoChangeContradiction);
                if (result.HasPart) return Fail(ReasonMismatch);
                if (Blank(result.Summary)) return Fail(ReasonNoChangeReason);
            }
            else if (result.Files.Count == 0)
            {
                return Fail(ReasonTruncated);
            }
            if (completed == null) return Fail(ReasonTruncated);
            if (Count(completed) != result.Files.Count) return Fail(ReasonMismatch);
            result.Ok = true;
            return result;
        }

        // ---------- one file per answer ----------

        public static IntakeResult AddPart(IntakeParts collection, IntakeResult parsed)
        {
            if (collection == null) collection = new IntakeParts();
            if (parsed == null || !parsed.Ok) return Fail(parsed == null || parsed.Reason.Length == 0 ? ReasonNoSentinel : parsed.Reason);
            if (!parsed.HasPart) return Fail(ReasonPartMissing);
            if (parsed.Files.Count != 1) return Fail(ReasonPartMultipleFiles);
            if (collection.Total > 0 && parsed.PartTotal != collection.Total) return Fail(ReasonPartTotalMismatch);

            IntakeFile incoming = parsed.Files[0];
            int existing = collection.Order.IndexOf(parsed.PartIndex);
            if (existing >= 0)
            {
                // The same number arriving again is only accepted when it says
                // exactly the same thing. Different content under a number that
                // is already in is a contradiction, not a correction.
                IntakeFile held = collection.Files[existing];
                if (String.Equals(held.Language, incoming.Language, StringComparison.Ordinal) &&
                    String.Equals(held.Name, incoming.Name, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(held.Text, incoming.Text, StringComparison.Ordinal))
                {
                    return Accepted(collection, false);
                }
                return Fail(ReasonPartConflict);
            }
            for (int index = 0; index < collection.Files.Count; index++)
            {
                if (String.Equals(collection.Files[index].Language, incoming.Language, StringComparison.Ordinal) &&
                    String.Equals(collection.Files[index].Name, incoming.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(ReasonPartDuplicateFile);
                }
            }
            collection.Total = parsed.PartTotal;
            int at = 0;
            while (at < collection.Order.Count && collection.Order[at] < parsed.PartIndex) at++;
            collection.Order.Insert(at, parsed.PartIndex);
            collection.Files.Insert(at, incoming);
            collection.Summaries.Insert(at, parsed.Summary == null ? "" : parsed.Summary);
            return Accepted(collection, true);
        }

        // The collected parts, read back as the one answer the rest of the flow
        // already knows how to handle.
        public static IntakeResult Merge(IntakeParts collection)
        {
            if (collection == null || !collection.Complete) return Fail(ReasonTruncated);
            IntakeResult result = new IntakeResult();
            result.Ok = true;
            result.PartIndex = -1;
            result.PartTotal = collection.Total;
            StringBuilder summary = new StringBuilder();
            for (int index = 0; index < collection.Files.Count; index++)
            {
                result.Files.Add(collection.Files[index]);
                string part = collection.Summaries[index];
                if (Blank(part)) continue;
                if (summary.Length != 0) summary.Append(Environment.NewLine);
                summary.Append(part);
            }
            result.Summary = summary.ToString();
            return result;
        }

        private static IntakeResult Accepted(IntakeParts collection, bool added)
        {
            IntakeResult result = new IntakeResult();
            result.Ok = true;
            result.PartTotal = collection.Total;
            result.Added = added;
            return result;
        }

        // ---------- shape ----------

        public static IntakeResult Fail(string reason)
        {
            IntakeResult result = new IntakeResult();
            result.Ok = false;
            result.Reason = reason;
            result.Message = Explain(reason);
            return result;
        }

        // Every refusal says what to do about it. A code on its own tells the
        // operator nothing they can act on.
        public static string Explain(string reason)
        {
            return Messages.Text("intake-" + reason + ".txt", Fallback(reason));
        }

        private static string Fallback(string reason)
        {
            if (reason == ReasonEmpty) return "The clipboard was empty. Copy the code block of the answer and try again.";
            if (reason == ReasonNoSentinel) return "Nothing in this paste is in the shape an answer has to be in. Copy the whole code block of the answer and try again.";
            if (reason == ReasonOtherRequest) return "This is an answer to a different request. Copy the current request again and send it to the assistant.";
            if (reason == ReasonTruncated) return "The answer is cut off. Copy the whole code block again and try once more.";
            if (reason == ReasonMismatch) return "The markers around the files do not line up. Copy the whole code block again and try once more.";
            if (reason == ReasonDuplicate) return "The same file arrived twice. Ask the assistant to return each file once.";
            if (reason == ReasonUnknownLanguage) return "The answer names a language that is not powershell or vba.";
            if (reason == ReasonBadName) return "A file name in the answer cannot be used. Ask the assistant to keep the names it was given.";
            if (reason == ReasonEmptyFile) return "A file in the answer has nothing in it. Ask the assistant to return the whole file.";
            if (reason == ReasonPartShape) return "The line saying which part this is could not be read.";
            if (reason == ReasonPartMissing) return "The line saying which part this is is missing.";
            if (reason == ReasonPartMultipleFiles) return "One message carried more than one file. Ask the assistant for one file per message.";
            if (reason == ReasonPartTotalMismatch) return "This part says there are a different number of parts than the earlier ones did.";
            if (reason == ReasonPartConflict) return "A part with a number already taken arrived with different content. Start the intake again.";
            if (reason == ReasonPartDuplicateFile) return "The same file arrived under two different part numbers.";
            if (reason == ReasonNoChangeVerdict) return "The answer says nothing was changed but does not say whether that is because it was unnecessary, impossible or unclear.";
            if (reason == ReasonNoChangeReason) return "The answer says nothing was changed but gives no reason.";
            if (reason == ReasonNoChangeContradiction) return "The answer says nothing was changed and then sends files.";
            if (reason == ReasonQuestionNotAllowed) return "The assistant asked a question instead of answering. Ask it to return the code, or a refusal with a reason.";
            return "This paste could not be read.";
        }

        private static bool IsName(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 31) return false;
            char first = value[0];
            if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z'))) return false;
            for (int index = 1; index < value.Length; index++)
            {
                char character = value[index];
                bool letter = (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z');
                bool digit = character >= '0' && character <= '9';
                if (!letter && !digit && character != '_') return false;
            }
            return true;
        }

        private static int Count(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 4) return -1;
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9') return -1;
            }
            return Int32.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string[] Split(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        private static string Join(List<string> lines)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < lines.Count; index++)
            {
                if (index != 0) text.Append(Environment.NewLine);
                text.Append(lines[index]);
            }
            return text.ToString();
        }

        private static string Trim(List<string> lines)
        {
            int start = 0;
            int end = lines.Count;
            while (start < end && Blank(lines[start])) start++;
            while (end > start && Blank(lines[end - 1])) end--;
            StringBuilder text = new StringBuilder();
            for (int index = start; index < end; index++)
            {
                if (index != start) text.Append(Environment.NewLine);
                text.Append(lines[index]);
            }
            return text.ToString();
        }

        public static bool Blank(string value)
        {
            if (String.IsNullOrEmpty(value)) return true;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == ' ' || character == '\t' || character == '\r' || character == '\n') continue;
                if (character == IdeographicSpace) continue;
                return false;
            }
            return true;
        }

        // ---------- decoration a chat client added on the way out ----------
        //
        // Quoting the block, turning it into a bullet, escaping it as HTML or
        // swapping the hash for a fullwidth one are all things the client did,
        // not things the answer got wrong, and asking again does not fix any of
        // them.
        //
        // Only a line that was not a sentinel and becomes one is replaced. A
        // file body is code and is taken exactly as it arrived; nothing here can
        // turn a line of somebody's script into a sentinel, because it only ever
        // removes decoration and never invents the marker.
        private static List<string> Normalize(string[] lines)
        {
            List<string> result = new List<string>();
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (IsSentinelLine(line)) { result.Add(line); continue; }
                string repaired = Undecorate(line);
                result.Add(IsSentinelLine(repaired) ? repaired : line);
            }
            return result;
        }

        private static bool IsSentinelLine(string line)
        {
            return TrimBoth(line).StartsWith(Handoff.Marker, StringComparison.Ordinal);
        }

        public static string Undecorate(string line)
        {
            string value = TrimBoth(line);
            // A blockquote, possibly nested.
            while (value.StartsWith(">", StringComparison.Ordinal)) value = TrimBoth(value.Substring(1));
            // A bullet or a numbered item.
            if (value.Length > 1 && (value[0] == '-' || value[0] == '*' || value[0] == '+') &&
                (value[1] == ' ' || value[1] == '\t' || value[1] == IdeographicSpace))
            {
                value = TrimBoth(value.Substring(1));
            }
            value = StripTags(value);
            while (value.StartsWith("`", StringComparison.Ordinal)) value = value.Substring(1);
            while (value.EndsWith("`", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
            value = Entities(value);
            value = TrimBoth(value);
            // A fullwidth hash or at sign, which some clients substitute.
            if (value.StartsWith(FullwidthHash, StringComparison.Ordinal)) value = "#" + value.Substring(1);
            if (value.StartsWith("#" + FullwidthAt, StringComparison.Ordinal)) value = "#@" + value.Substring(2);
            if (value.StartsWith("@APPSTUDIO", StringComparison.Ordinal)) value = "#" + value;
            return value;
        }

        private static string StripTags(string value)
        {
            string[] tags = new string[] { "code", "pre", "span", "p", "div", "strong", "em", "b", "i" };
            string result = value;
            for (int index = 0; index < tags.Length; index++)
            {
                result = Remove(result, "<" + tags[index] + ">");
                result = Remove(result, "</" + tags[index] + ">");
            }
            return result;
        }

        private static string Remove(string value, string tag)
        {
            int at = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                value = value.Substring(0, at) + value.Substring(at + tag.Length);
                at = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            }
            return value;
        }

        private static string Entities(string value)
        {
            string result = value;
            result = Swap(result, "&lt;", "<");
            result = Swap(result, "&gt;", ">");
            result = Swap(result, "&quot;", "\"");
            result = Swap(result, "&apos;", "'");
            result = Swap(result, "&#39;", "'");
            result = Swap(result, "&#x27;", "'");
            result = Swap(result, "&nbsp;", " ");
            result = Swap(result, "&amp;", "&");
            return result;
        }

        private static string Swap(string value, string from, string to)
        {
            int at = value.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                value = value.Substring(0, at) + to + value.Substring(at + from.Length);
                int next = at + to.Length;
                if (next >= value.Length) break;
                at = value.IndexOf(from, next, StringComparison.OrdinalIgnoreCase);
            }
            return value;
        }

        private static string TrimBoth(string value)
        {
            if (value == null) return "";
            int start = 0;
            int end = value.Length;
            while (start < end && IsSpace(value[start])) start++;
            while (end > start && IsSpace(value[end - 1])) end--;
            return value.Substring(start, end - start);
        }

        private static bool IsSpace(char character)
        {
            return character == ' ' || character == '\t' || character == '\r' || character == '\n' || character == '\u3000';
        }

        // A sentinel is recognised on its own line only, so a marker looking
        // string inside real code cannot end a file.
        private static string[] Sentinel(string line)
        {
            string trimmed = TrimBoth(line);
            if (!trimmed.StartsWith(Handoff.Marker, StringComparison.Ordinal)) return null;
            string rest = trimmed.Substring(Handoff.Marker.Length);
            List<string> parts = new List<string>();
            int index = 0;
            while (index < rest.Length)
            {
                while (index < rest.Length && IsSpace(rest[index])) index++;
                int start = index;
                while (index < rest.Length && !IsSpace(rest[index])) index++;
                if (index > start) parts.Add(rest.Substring(start, index - start));
            }
            if (parts.Count == 0) return new string[] { "" };
            return parts.ToArray();
        }
    }
}
