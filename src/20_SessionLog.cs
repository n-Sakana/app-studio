namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // Reading back a record that is still being written to. The session store
    // appends and closes per record, but a reader can arrive while a handle is
    // open, and File.ReadAllLines refuses that by default - so anything reading
    // a live record has to say so explicitly, which is what this is for.
    public static class SessionLog
    {
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
