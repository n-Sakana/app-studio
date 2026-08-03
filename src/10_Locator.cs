namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    // A way of finding one element again later, written only from material the
    // acquisition actually returned. Window handles, UI Automation RuntimeIds
    // and field contents never appear in one: the first two do not survive a
    // restart and the third is the user's data, not an address.
    public sealed class ElementLocator
    {
        public const string StrategyAutomationId = "uia.automationId";
        public const string StrategyNameType = "uia.nameControlType";
        public const string StrategyTreePath = "uia.treePath";
        public const string StrategyCtrlId = "win32.ctrlId";
        public const string StrategyClassIndex = "win32.classIndex";
        public const string StrategyWindowRelative = "window.relative";

        public string Strategy;
        public string AutomationId;
        public string Name;
        public string ControlType;
        public string ClassName;
        public string TreePath;
        public int CtrlId;
        public int ClassIndex = -1;
        public double RelativeX = -1;
        public double RelativeY = -1;
        public string Confidence = "low";
        public List<string> Reasons = new List<string>();

        public string Display
        {
            get
            {
                StringBuilder text = new StringBuilder();
                text.Append(Strategy).Append(" { ");
                if (!String.IsNullOrEmpty(AutomationId)) text.Append("AutomationId=").Append(AutomationId).Append(' ');
                if (!String.IsNullOrEmpty(Name)) text.Append("Name=").Append(Name).Append(' ');
                if (!String.IsNullOrEmpty(ControlType)) text.Append("ControlType=").Append(ControlType).Append(' ');
                if (!String.IsNullOrEmpty(ClassName)) text.Append("class=").Append(ClassName).Append(' ');
                if (CtrlId != 0) text.Append("ctrlId=").Append(CtrlId.ToString(CultureInfo.InvariantCulture)).Append(' ');
                if (ClassIndex >= 0) text.Append("index=").Append(ClassIndex.ToString(CultureInfo.InvariantCulture)).Append(' ');
                if (!String.IsNullOrEmpty(TreePath)) text.Append("path=").Append(TreePath).Append(' ');
                if (RelativeX >= 0) text.Append("rel=").Append(RelativeX.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(RelativeY.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
                text.Append('}');
                return text.ToString();
            }
        }

        public JsonObject ToJson()
        {
            return new JsonObject()
                .Add("strategy", Strategy)
                .Add("automationId", AutomationId)
                .Add("name", Name)
                .Add("controlType", ControlType)
                .Add("className", ClassName)
                .Add("treePath", TreePath)
                .Add("ctrlId", CtrlId)
                .Add("classIndex", ClassIndex < 0 ? null : (object)ClassIndex)
                .Add("relativeX", RelativeX < 0 ? null : (object)RelativeX)
                .Add("relativeY", RelativeY < 0 ? null : (object)RelativeY)
                .Add("confidence", Confidence)
                .Add("reasons", Reasons.ToArray());
        }

        public static ElementLocator FromJson(Dictionary<string, object> item)
        {
            ElementLocator locator = new ElementLocator();
            locator.Strategy = JsonReader.Text(item, "strategy");
            locator.AutomationId = JsonReader.Text(item, "automationId");
            locator.Name = JsonReader.Text(item, "name");
            locator.ControlType = JsonReader.Text(item, "controlType");
            locator.ClassName = JsonReader.Text(item, "className");
            locator.TreePath = JsonReader.Text(item, "treePath");
            locator.CtrlId = JsonReader.Number(item, "ctrlId", 0);
            locator.ClassIndex = JsonReader.Number(item, "classIndex", -1);
            locator.Confidence = JsonReader.Text(item, "confidence") ?? "low";
            object relativeX;
            if (item != null && item.TryGetValue("relativeX", out relativeX) && relativeX != null)
            {
                try { locator.RelativeX = Convert.ToDouble(relativeX, CultureInfo.InvariantCulture); }
                catch { locator.RelativeX = -1; }
            }
            object relativeY;
            if (item != null && item.TryGetValue("relativeY", out relativeY) && relativeY != null)
            {
                try { locator.RelativeY = Convert.ToDouble(relativeY, CultureInfo.InvariantCulture); }
                catch { locator.RelativeY = -1; }
            }
            object[] reasons = JsonReader.Items(item, "reasons");
            if (reasons != null) for (int index = 0; index < reasons.Length; index++) locator.Reasons.Add(Convert.ToString(reasons[index], CultureInfo.InvariantCulture));
            return locator;
        }
    }

    public static class LocatorBuilder
    {
        // Every locator that the material supports, strongest first. A weak one
        // is still produced and kept, because a stated weak address is worth
        // more than a silent absence, and the resolver refuses to act on an
        // ambiguous one anyway.
        public static List<ElementLocator> Build(ScanNode node, RectValue windowRect, List<ScanNode> siblingsOfSameWindow)
        {
            List<ElementLocator> locators = new List<ElementLocator>();
            if (node == null) return locators;

            if (IsStableAutomationId(node.AutomationId))
            {
                ElementLocator locator = new ElementLocator();
                locator.Strategy = ElementLocator.StrategyAutomationId;
                locator.AutomationId = node.AutomationId;
                locator.ControlType = node.ControlType;
                locator.Confidence = "high";
                locator.Reasons.Add("AutomationId is set, is not purely numeric, and is the identifier the application chose.");
                locators.Add(locator);
            }
            if (!String.IsNullOrWhiteSpace(node.Name) && !String.IsNullOrWhiteSpace(node.ControlType))
            {
                ElementLocator locator = new ElementLocator();
                locator.Strategy = ElementLocator.StrategyNameType;
                locator.Name = node.Name;
                locator.ControlType = node.ControlType;
                locator.Confidence = CountByNameType(siblingsOfSameWindow, node) == 1 ? "high" : "medium";
                locator.Reasons.Add(CountByNameType(siblingsOfSameWindow, node) == 1
                    ? "The name and control type pair was unique inside this window at record time."
                    : "The name and control type pair matched more than one element inside this window at record time.");
                locators.Add(locator);
            }
            if (!String.IsNullOrWhiteSpace(node.Path))
            {
                ElementLocator locator = new ElementLocator();
                locator.Strategy = ElementLocator.StrategyTreePath;
                locator.TreePath = node.Path;
                locator.ControlType = node.ControlType;
                locator.Confidence = "medium";
                locator.Reasons.Add("The hierarchy path holds while the application keeps the same layout; it moves when the layout does.");
                locators.Add(locator);
            }
            if (node.CtrlId != 0 && node.CtrlId != -1 && !String.IsNullOrWhiteSpace(node.ClassName))
            {
                ElementLocator locator = new ElementLocator();
                locator.Strategy = ElementLocator.StrategyCtrlId;
                locator.CtrlId = node.CtrlId;
                locator.ClassName = StableClassName(node.ClassName);
                locator.Confidence = "medium";
                locator.Reasons.Add("The control id belongs to the dialog template, so it survives a restart unless the application generates its classes.");
                locators.Add(locator);
            }
            if (!String.IsNullOrWhiteSpace(node.ClassName))
            {
                int index = ClassIndexOf(siblingsOfSameWindow, node);
                if (index >= 0)
                {
                    ElementLocator locator = new ElementLocator();
                    locator.Strategy = ElementLocator.StrategyClassIndex;
                    locator.ClassName = StableClassName(node.ClassName);
                    locator.ClassIndex = index;
                    locator.Confidence = "low";
                    locator.Reasons.Add("Position among the elements of the same window class. It moves as soon as the application adds or hides one.");
                    locators.Add(locator);
                }
            }
            if (windowRect != null && windowRect.Width > 0 && windowRect.Height > 0 && node.Rect != null && node.Rect.Width > 0)
            {
                ElementLocator locator = new ElementLocator();
                locator.Strategy = ElementLocator.StrategyWindowRelative;
                locator.RelativeX = (node.Rect.X + node.Rect.Width / 2.0 - windowRect.X) / windowRect.Width;
                locator.RelativeY = (node.Rect.Y + node.Rect.Height / 2.0 - windowRect.Y) / windowRect.Height;
                locator.Confidence = "low";
                locator.Reasons.Add("A position inside the window. It is never treated as an identification, only as a description.");
                locators.Add(locator);
            }
            return locators;
        }

        public static string BestConfidence(List<ElementLocator> locators)
        {
            if (locators == null || locators.Count == 0) return "none";
            bool medium = false;
            for (int index = 0; index < locators.Count; index++)
            {
                if (locators[index].Confidence == "high") return "high";
                if (locators[index].Confidence == "medium") medium = true;
            }
            return medium ? "medium" : "low";
        }

        public static bool IsStableAutomationId(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            bool digitsOnly = true;
            for (int index = 0; index < value.Length; index++)
            {
                if (!Char.IsDigit(value[index])) { digitsOnly = false; break; }
            }
            return !digitsOnly;
        }

        // Windows Forms and several toolkits append a per process number to the
        // window class. The number changes on every launch, so the volatile tail
        // is dropped rather than being stored as if it were stable.
        public static string StableClassName(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            int marker = value.IndexOf(".app.", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) return value.Substring(0, marker);
            marker = value.IndexOf("WindowsForms10.", StringComparison.OrdinalIgnoreCase);
            if (marker == 0)
            {
                int separator = value.IndexOf('.', "WindowsForms10.".Length);
                if (separator > 0) return value.Substring(0, separator);
            }
            return value;
        }

        private static int CountByNameType(List<ScanNode> nodes, ScanNode node)
        {
            if (nodes == null) return 1;
            int count = 0;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (String.Equals(nodes[index].Name, node.Name, StringComparison.Ordinal) &&
                    String.Equals(nodes[index].ControlType, node.ControlType, StringComparison.Ordinal)) count++;
            }
            return count == 0 ? 1 : count;
        }

        private static int ClassIndexOf(List<ScanNode> nodes, ScanNode node)
        {
            if (nodes == null) return -1;
            string wanted = StableClassName(node.ClassName);
            int seen = 0;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (!String.Equals(StableClassName(nodes[index].ClassName), wanted, StringComparison.Ordinal)) continue;
                if (ReferenceEquals(nodes[index], node)) return seen;
                seen++;
            }
            return -1;
        }
    }
}
