namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Web.Script.Serialization;

    // The only JSON ever read back is what this program wrote itself: the index
    // of a session and the records appended beside it. JavaScriptSerializer
    // ships with the .NET Framework and the launcher already references
    // System.Web.Extensions, so no parser is carried onto the target machine.
    public static class JsonReader
    {
        public static Dictionary<string, object> ReadObject(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 32 * 1024 * 1024;
            serializer.RecursionLimit = 64;
            return serializer.DeserializeObject(text) as Dictionary<string, object>;
        }

        public static bool Has(Dictionary<string, object> item, string key)
        {
            return item != null && item.ContainsKey(key) && item[key] != null;
        }

        public static string Text(Dictionary<string, object> item, string key)
        {
            if (!Has(item, key)) return null;
            object value = item[key];
            string text = value as string;
            if (text != null) return text;
            if (value is bool) return (bool)value ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // Returns fallback when the key is missing or is not a number. Callers
        // that must tell "absent" from "present but wrong" check Has first.
        public static int Number(Dictionary<string, object> item, string key, int fallback)
        {
            if (!Has(item, key)) return fallback;
            try
            {
                return Convert.ToInt32(item[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public static long Number64(Dictionary<string, object> item, string key, long fallback)
        {
            if (!Has(item, key)) return fallback;
            try
            {
                return Convert.ToInt64(item[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public static bool IsNumber(Dictionary<string, object> item, string key)
        {
            if (!Has(item, key)) return false;
            object value = item[key];
            if (value is string || value is bool) return false;
            try
            {
                Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // A whole number that also fits the int the readers use. Rounding 1.5 or
        // wrapping a value that overflows would change what the answer asked
        // for, so both are reported as "not a whole number" instead.
        public static bool IsWholeNumber(Dictionary<string, object> item, string key)
        {
            if (!IsNumber(item, key)) return false;
            try
            {
                double value = Convert.ToDouble(item[key], CultureInfo.InvariantCulture);
                if (value != Math.Floor(value)) return false;
                if (value < Int32.MinValue || value > Int32.MaxValue) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool Flag(Dictionary<string, object> item, string key, bool fallback)
        {
            if (!Has(item, key)) return fallback;
            object value = item[key];
            if (value is bool) return (bool)value;
            string text = value as string;
            if (text == null) return fallback;
            if (String.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static Dictionary<string, object> Child(Dictionary<string, object> item, string key)
        {
            return Has(item, key) ? item[key] as Dictionary<string, object> : null;
        }

        public static object[] Items(Dictionary<string, object> item, string key)
        {
            return Has(item, key) ? item[key] as object[] : null;
        }

        public static string[] Keys(Dictionary<string, object> item)
        {
            if (item == null) return new string[0];
            List<string> keys = new List<string>();
            foreach (KeyValuePair<string, object> entry in item) keys.Add(entry.Key);
            return keys.ToArray();
        }
    }
}
