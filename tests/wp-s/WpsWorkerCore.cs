using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace AppStudio.WpsWorker
{
    public static class ProbeWorker
    {
        public static string Probe(string requestId, int x, int y)
        {
            Stopwatch total = Stopwatch.StartNew();
            long elementMs = -1;
            long propertyMs = -1;
            long patternMs = -1;
            AutomationElement element = null;
            string name = String.Empty;
            string automationId = String.Empty;
            string controlType = String.Empty;
            string value = String.Empty;
            int processId = -1;

            try
            {
                WriteStage(requestId, "ElementFromPoint");
                Stopwatch stage = Stopwatch.StartNew();
                element = AutomationElement.FromPoint(new Point(x, y));
                stage.Stop();
                elementMs = stage.ElapsedMilliseconds;
                if (element == null)
                {
                    return Result(requestId, "unavailable", "UIA-NOELEMENT", total.ElapsedMilliseconds, elementMs, propertyMs, patternMs, processId, name, automationId, controlType, value);
                }

                WriteStage(requestId, "Property");
                stage.Restart();
                name = element.Current.Name;
                automationId = element.Current.AutomationId;
                controlType = element.Current.ControlType.ProgrammaticName;
                processId = element.Current.ProcessId;
                stage.Stop();
                propertyMs = stage.ElapsedMilliseconds;

                WriteStage(requestId, "Pattern");
                stage.Restart();
                object patternObject;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObject))
                {
                    ValuePattern valuePattern = (ValuePattern)patternObject;
                    value = valuePattern.Current.Value;
                }
                stage.Stop();
                patternMs = stage.ElapsedMilliseconds;

                total.Stop();
                return Result(requestId, "ok", String.Empty, total.ElapsedMilliseconds, elementMs, propertyMs, patternMs, processId, name, automationId, controlType, value);
            }
            catch (Exception ex)
            {
                total.Stop();
                return Result(requestId, "unavailable", HResult(ex), total.ElapsedMilliseconds, elementMs, propertyMs, patternMs, processId, name, automationId, controlType, value);
            }
        }

        private static void WriteStage(string requestId, string stage)
        {
            Console.Out.WriteLine("{\"type\":\"stage\",\"id\":\"" + Escape(requestId) + "\",\"stage\":\"" + Escape(stage) + "\"}");
            Console.Out.Flush();
        }

        private static string Result(string requestId, string state, string reason, long totalMs, long elementMs, long propertyMs, long patternMs, int processId, string name, string automationId, string controlType, string value)
        {
            return "{\"type\":\"result\",\"id\":\"" + Escape(requestId) +
                "\",\"state\":\"" + Escape(state) +
                "\",\"reason\":\"" + Escape(reason) +
                "\",\"totalMs\":" + totalMs.ToString(CultureInfo.InvariantCulture) +
                ",\"elementMs\":" + elementMs.ToString(CultureInfo.InvariantCulture) +
                ",\"propertyMs\":" + propertyMs.ToString(CultureInfo.InvariantCulture) +
                ",\"patternMs\":" + patternMs.ToString(CultureInfo.InvariantCulture) +
                ",\"processId\":" + processId.ToString(CultureInfo.InvariantCulture) +
                ",\"name\":\"" + Escape(name) +
                "\",\"automationId\":\"" + Escape(automationId) +
                "\",\"controlType\":\"" + Escape(controlType) +
                "\",\"value\":\"" + Escape(value) + "\"}";
        }

        private static string HResult(Exception ex)
        {
            return "UIA-FAIL:0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ":" + ex.GetType().Name;
        }

        private static string Escape(string value)
        {
            string source = value ?? String.Empty;
            StringBuilder result = new StringBuilder();
            int i;
            for (i = 0; i < source.Length; i++)
            {
                char c = source[i];
                switch (c)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            result.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            result.Append(c);
                        }
                        break;
                }
            }
            return result.ToString();
        }
    }
}
