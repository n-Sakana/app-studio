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

        // Whether the wording is actually available, as opposed to the fallback
        // that stands in for a file somebody deleted.
        //
        // On screen the difference is small: a missing file shows an English
        // line in a Japanese window and is obvious. In a built artefact it is
        // not small - the words are written into the file and stay there - so
        // the build asks this before it writes one, rather than handing over an
        // automation that speaks a different language from the product that
        // made it and saying nothing about it.
        public static bool Ready
        {
            get
            {
                lock (Sync)
                {
                    return directory != null && Directory.Exists(directory);
                }
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
