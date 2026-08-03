namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    public sealed class ResolveResult
    {
        public ScanNode Node;
        public ElementLocator UsedLocator;
        public int MatchCount;
        public string State;
        public string Reason;
        public List<string> Trace = new List<string>();

        public bool Resolved { get { return String.Equals(State, "resolved", StringComparison.Ordinal); } }
    }

    // Finds a recorded element again in a freshly acquired list. Ambiguity is
    // never resolved by falling back to the coordinates the element happened to
    // have when it was recorded: an address that matches two things is not an
    // address, and acting on the first of them would be a guess presented as a
    // fact.
    public static class LocatorResolver
    {
        public static ResolveResult Resolve(List<ElementLocator> locators, List<ScanNode> candidates)
        {
            ResolveResult result = new ResolveResult();
            if (locators == null || locators.Count == 0)
            {
                result.State = "no-locator";
                result.Reason = "This step carries no locator, so there is nothing to look for.";
                return result;
            }
            if (candidates == null || candidates.Count == 0)
            {
                result.State = "no-elements";
                result.Reason = "The window exposed no elements at all when it was re-acquired, so nothing could be matched.";
                return result;
            }
            string ambiguous = null;
            int identifying = 0;
            for (int index = 0; index < locators.Count; index++)
            {
                ElementLocator locator = locators[index];
                // Only a locator that says *which* element this is may decide
                // what gets acted on. Where an element sits - its position in
                // the window, its place among the siblings of its class - is a
                // description of the recording, not an identification of the
                // thing. Both are written down and both are shown to the
                // reader, because they are useful material for whoever writes
                // the automation afterwards; neither is ever allowed to answer
                // "act here". Otherwise a changed layout would be replayed as
                // "the first button of that class", which is a guess wearing an
                // answer's clothes.
                if (!Identifies(locator.Strategy))
                {
                    result.Trace.Add(locator.Strategy + ": recorded as a description, never used to decide what to act on");
                    continue;
                }
                identifying++;
                List<ScanNode> matches = Match(locator, candidates);
                result.Trace.Add(locator.Strategy + ": " + matches.Count.ToString(CultureInfo.InvariantCulture) + " match(es)");
                if (matches.Count == 1)
                {
                    result.Node = matches[0];
                    result.UsedLocator = locator;
                    result.MatchCount = 1;
                    result.State = "resolved";
                    result.Reason = "Resolved by " + locator.Strategy + ".";
                    return result;
                }
                if (matches.Count > 1 && ambiguous == null)
                {
                    ambiguous = locator.Strategy + " matched " + matches.Count.ToString(CultureInfo.InvariantCulture) + " elements";
                    result.MatchCount = matches.Count;
                }
            }
            if (ambiguous != null)
            {
                result.State = "ambiguous";
                result.Reason = "The recorded element could not be told apart from others: " + ambiguous +
                    ". Nothing was done, because acting on a position here would be a guess.";
                return result;
            }
            if (identifying == 0)
            {
                result.State = "no-locator";
                result.Reason = "This element exposed nothing that identifies it - only where it happened to sit. " +
                    "A position is not an address, so nothing was acted on.";
                return result;
            }
            result.State = "not-found";
            result.Reason = "None of the recorded identifying locators matched anything in the window as it is now.";
            return result;
        }

        // What may decide which element is acted on. Everything else is kept
        // and shown, but never resolves.
        public static bool Identifies(string strategy)
        {
            return strategy == ElementLocator.StrategyAutomationId ||
                strategy == ElementLocator.StrategyNameType ||
                strategy == ElementLocator.StrategyTreePath ||
                strategy == ElementLocator.StrategyCtrlId;
        }

        public static List<ScanNode> Match(ElementLocator locator, List<ScanNode> candidates)
        {
            List<ScanNode> matches = new List<ScanNode>();
            if (locator == null || candidates == null) return matches;
            int classSeen = 0;
            for (int index = 0; index < candidates.Count; index++)
            {
                ScanNode node = candidates[index];
                bool hit = false;
                if (locator.Strategy == ElementLocator.StrategyAutomationId)
                {
                    hit = Same(locator.AutomationId, node.AutomationId) && Optional(locator.ControlType, node.ControlType);
                }
                else if (locator.Strategy == ElementLocator.StrategyNameType)
                {
                    hit = Same(locator.Name, node.Name) && Same(locator.ControlType, node.ControlType);
                }
                else if (locator.Strategy == ElementLocator.StrategyTreePath)
                {
                    hit = Same(locator.TreePath, node.Path);
                }
                else if (locator.Strategy == ElementLocator.StrategyCtrlId)
                {
                    hit = locator.CtrlId != 0 && locator.CtrlId == node.CtrlId &&
                        Same(locator.ClassName, LocatorBuilder.StableClassName(node.ClassName));
                }
                else if (locator.Strategy == ElementLocator.StrategyClassIndex)
                {
                    if (Same(locator.ClassName, LocatorBuilder.StableClassName(node.ClassName)))
                    {
                        hit = classSeen == locator.ClassIndex;
                        classSeen++;
                    }
                }
                if (hit) matches.Add(node);
            }
            return matches;
        }

        private static bool Same(string first, string second)
        {
            if (String.IsNullOrEmpty(first) || String.IsNullOrEmpty(second)) return false;
            return String.Equals(first, second, StringComparison.Ordinal);
        }

        private static bool Optional(string expected, string actual)
        {
            return String.IsNullOrEmpty(expected) || String.Equals(expected, actual, StringComparison.Ordinal);
        }
    }
}
