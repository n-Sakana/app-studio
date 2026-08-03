namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class SessionLogStatus
    {
        public bool Enabled;
        public string Directory;
        public string DisabledReason;
        public long RecordCount;
        public int WriteFailures;
        public string LastError;
        public DateTimeOffset? LastWriteAt;
        public DateTimeOffset? LastDurableAt;
    }

    // Records are appended as they happen so that closing, crashing, or losing
    // power leaves the investigation evidence collected so far on disk. There is
    // no explicit save action anywhere in the product.
    public sealed class SessionLog : IDisposable
    {
        private const int DurableFlushIntervalMs = 2000;
        private readonly object sync = new object();
        private readonly Dictionary<string, FileStream> streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        private readonly UTF8Encoding encoding = new UTF8Encoding(false);
        private readonly string folder;
        private bool enabled;
        private string disabledReason;
        private long sequence;
        private int writeFailures;
        private string lastError;
        private DateTimeOffset? lastWriteAt;
        private DateTimeOffset? lastDurableAt;
        private DateTime lastDurableUtc = DateTime.MinValue;
        private bool disposed;

        public SessionLog(string logDirectory)
        {
            folder = String.IsNullOrWhiteSpace(logDirectory) ? null : Path.GetFullPath(logDirectory);
            if (folder == null)
            {
                enabled = false;
                disabledReason = "No log folder was supplied.";
                return;
            }
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                enabled = true;
            }
            catch (Exception exception)
            {
                enabled = false;
                disabledReason = exception.GetType().Name + ": " + exception.Message;
            }
        }

        public string Folder { get { return folder; } }

        public bool Enabled { get { lock (sync) { return enabled; } } }

        public SessionLogStatus Status
        {
            get
            {
                lock (sync)
                {
                    SessionLogStatus status = new SessionLogStatus();
                    status.Enabled = enabled;
                    status.Directory = folder;
                    status.DisabledReason = disabledReason;
                    status.RecordCount = sequence;
                    status.WriteFailures = writeFailures;
                    status.LastError = lastError;
                    status.LastWriteAt = lastWriteAt;
                    status.LastDurableAt = lastDurableAt;
                    return status;
                }
            }
        }

        public string PathFor(string stream)
        {
            return folder == null ? null : Path.Combine(folder, SafeName(stream) + ".jsonl");
        }

        // Returns the sequence number that was written, or 0 when the record
        // could not be persisted. Callers surface a zero to the operator instead
        // of assuming the record was kept.
        public long Append(string stream, JsonObject record)
        {
            if (record == null) return 0;
            lock (sync)
            {
                if (!enabled || disposed) return 0;
                sequence++;
                JsonObject line = new JsonObject().Add("seq", sequence).Add("at", DateTimeOffset.Now);
                foreach (KeyValuePair<string, object> item in record) line.Add(item.Key, item.Value);
                try
                {
                    FileStream file = OpenStream(stream);
                    byte[] payload = encoding.GetBytes(JsonWriter.WriteCompact(line) + "\n");
                    file.Write(payload, 0, payload.Length);
                    file.Flush();
                    lastWriteAt = DateTimeOffset.Now;
                    if ((DateTime.UtcNow - lastDurableUtc).TotalMilliseconds >= DurableFlushIntervalMs) FlushDurableCore();
                    return sequence;
                }
                catch (Exception exception)
                {
                    sequence--;
                    writeFailures++;
                    lastError = exception.GetType().Name + ": " + exception.Message;
                    return 0;
                }
            }
        }

        // Forces the operating system to commit what has been written. Called on
        // shutdown and on a timer so an abrupt power loss cannot silently lose a
        // whole session.
        public void FlushDurable()
        {
            lock (sync)
            {
                if (!enabled || disposed) return;
                FlushDurableCore();
            }
        }

        public bool WriteText(string name, string content)
        {
            lock (sync)
            {
                if (!enabled || disposed || folder == null) return false;
                string target = Path.Combine(folder, SafeName(name));
                string temporary = target + ".tmp";
                try
                {
                    File.WriteAllText(temporary, content ?? String.Empty, encoding);
                    File.Copy(temporary, target, true);
                    File.Delete(temporary);
                    lastWriteAt = DateTimeOffset.Now;
                    return true;
                }
                catch (Exception exception)
                {
                    writeFailures++;
                    lastError = exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
            }
        }

        // The log is still open for writing while the session runs, so a plain
        // File.ReadAllLines is refused: its default sharing does not tolerate
        // the writer. Anything reading a live log has to say so explicitly.
        public static string[] ReadAllLines(string path)
        {
            List<string> lines = new List<string>();
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return lines.ToArray();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length != 0) lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                foreach (KeyValuePair<string, FileStream> item in streams)
                {
                    try
                    {
                        item.Value.Flush(true);
                        item.Value.Dispose();
                    }
                    catch
                    {
                    }
                }
                streams.Clear();
            }
        }

        private void FlushDurableCore()
        {
            foreach (KeyValuePair<string, FileStream> item in streams)
            {
                try
                {
                    item.Value.Flush(true);
                }
                catch (Exception exception)
                {
                    writeFailures++;
                    lastError = exception.GetType().Name + ": " + exception.Message;
                }
            }
            lastDurableUtc = DateTime.UtcNow;
            lastDurableAt = DateTimeOffset.Now;
        }

        private FileStream OpenStream(string stream)
        {
            string key = SafeName(stream);
            FileStream existing;
            if (streams.TryGetValue(key, out existing)) return existing;
            // FileShare.ReadWrite lets the operator open the growing log in an
            // editor while the investigation is still running.
            FileStream created = new FileStream(Path.Combine(folder, key + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            streams[key] = created;
            return created;
        }

        private static string SafeName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "log";
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < value.Length && builder.Length < 64; index++)
            {
                char character = value[index];
                bool allowed = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') || character == '-' || character == '_' || character == '.';
                if (allowed) builder.Append(character);
            }
            return builder.Length == 0 ? "log" : builder.ToString();
        }
    }

    public static class SessionLogJson
    {
        public static JsonObject Rect(RectValue rect)
        {
            if (rect == null) return null;
            return new JsonObject().Add("x", rect.X).Add("y", rect.Y).Add("width", rect.Width).Add("height", rect.Height);
        }

        public static object[] Strings(string[] values)
        {
            if (values == null) return null;
            object[] result = new object[values.Length];
            for (int index = 0; index < values.Length; index++) result[index] = values[index];
            return result;
        }

        public static object[] Numbers(int[] values)
        {
            if (values == null) return null;
            object[] result = new object[values.Length];
            for (int index = 0; index < values.Length; index++) result[index] = values[index];
            return result;
        }

        public static string RuntimeIdText(int[] values)
        {
            if (values == null || values.Length == 0) return null;
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < values.Length; index++)
            {
                if (index != 0) builder.Append('.');
                builder.Append(values[index].ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        public static JsonObject Status(ProbeStatus status)
        {
            if (status == null) return new JsonObject().Add("state", "unavailable").Add("reasons", new object[0]);
            List<object> reasons = new List<object>();
            for (int index = 0; index < status.Reasons.Count; index++)
            {
                reasons.Add(new JsonObject().Add("code", status.Reasons[index].Code).Add("message", status.Reasons[index].Message));
            }
            return new JsonObject().Add("state", status.State).Add("reasons", reasons.ToArray());
        }
    }
}
