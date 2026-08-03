namespace AppStudio
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class JsonObject : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();

        public JsonObject Add(string key, object value)
        {
            items.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public static class JsonWriter
    {
        public static string Write(object value)
        {
            StringBuilder builder = new StringBuilder();
            WriteValue(builder, value, 0, true);
            builder.Append('\n');
            return builder.ToString();
        }

        // JSON Lines needs one record per physical line, so the same writer is
        // reused without indentation or embedded newlines.
        public static string WriteCompact(object value)
        {
            StringBuilder builder = new StringBuilder();
            WriteValue(builder, value, 0, false);
            return builder.ToString();
        }

        public static void WriteFile(string path, object value)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(path, Write(value), new UTF8Encoding(false));
        }

        private static void WriteValue(StringBuilder builder, object value, int depth, bool pretty)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }
            string text = value as string;
            if (text != null)
            {
                WriteString(builder, text);
                return;
            }
            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is DateTimeOffset)
            {
                WriteString(builder, ((DateTimeOffset)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                return;
            }
            if (IsNumber(value))
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            JsonObject jsonObject = value as JsonObject;
            if (jsonObject != null)
            {
                WriteObject(builder, jsonObject, depth, pretty);
                return;
            }
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                WriteArray(builder, sequence, depth, pretty);
                return;
            }
            throw new InvalidOperationException("Unsupported JSON value type: " + value.GetType().FullName);
        }

        private static void WriteObject(StringBuilder builder, JsonObject value, int depth, bool pretty)
        {
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> item in value)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                if (pretty)
                {
                    builder.Append('\n');
                    Indent(builder, depth + 1);
                }
                WriteString(builder, item.Key);
                builder.Append(pretty ? ": " : ":");
                WriteValue(builder, item.Value, depth + 1, pretty);
                first = false;
            }
            if (!first && pretty)
            {
                builder.Append('\n');
                Indent(builder, depth);
            }
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable value, int depth, bool pretty)
        {
            builder.Append('[');
            bool first = true;
            foreach (object item in value)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                if (pretty)
                {
                    builder.Append('\n');
                    Indent(builder, depth + 1);
                }
                WriteValue(builder, item, depth + 1, pretty);
                first = false;
            }
            if (!first && pretty)
            {
                builder.Append('\n');
                Indent(builder, depth);
            }
            builder.Append(']');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private static bool IsNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong ||
                value is float || value is double || value is decimal;
        }

        private static void Indent(StringBuilder builder, int depth)
        {
            builder.Append(' ', depth * 2);
        }
    }
}

