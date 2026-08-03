namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // Japanese wording lives in assets/messages/*.txt so the C# sources stay
    // ASCII. The fallback is only used when a message file is missing.
    public static class Messages
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string directory;

        public static void Init(string baseDir)
        {
            lock (Sync)
            {
                directory = String.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, "assets", "messages");
                Cache.Clear();
            }
        }

        public static string Text(string name, string fallback)
        {
            lock (Sync)
            {
                string cached;
                if (Cache.TryGetValue(name, out cached)) return cached;
                string value = fallback;
                if (directory != null)
                {
                    try
                    {
                        string path = Path.Combine(directory, name);
                        if (File.Exists(path)) value = File.ReadAllText(path).Trim();
                    }
                    catch
                    {
                    }
                }
                Cache[name] = value;
                return value;
            }
        }
    }
}
