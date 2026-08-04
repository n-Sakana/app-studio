namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.Text.RegularExpressions;

    // What may be written down and what may not. There is one rule set, it is
    // stated in the outputs, and nothing anywhere else is allowed to decide this
    // on its own.
    //
    // The product does not read the keyboard stream. Typed text is never taken
    // from key traffic; it is read back from the element that received it, which
    // means a field the accessibility layer marks as a secret simply has no
    // readable value and none is invented.
    public static class Privacy
    {
        // Text that was typed is written down so a recording can be replayed.
        public const string PolicyRecordText = "recordText";
        // Only the length is written down. Replay stops and asks the operator.
        public const string PolicyLengthOnly = "lengthOnly";

        public const string SecretRuleIsPassword = "isPassword";
        public const string SecretRuleName = "secretName";
        public const string SecretRuleClass = "secretClass";

        // Matched against the element name, its automation id and its help text.
        // Deliberately conservative: a false positive costs a manual entry at
        // replay, a false negative would write a credential into a file.
        private const string SecretPattern =
            "(?i)(pass\\s*word|passwd|pwd|passphrase|secret|credential|token|api[-_ ]?key|private[-_ ]?key|\\bpin\\b|security\\s*code|\\bcvv\\b|\\bcvc\\b|one[-_ ]?time|\\botp\\b|mfa\\s*code|auth\\w*\\s*code)";

        public static bool IsSecretElement(ScanNode node)
        {
            if (node == null) return false;
            if (node.IsPassword.HasValue && node.IsPassword.Value) return true;
            if (IsSecretClass(node.ClassName) || IsSecretClass(node.RealClassName)) return true;
            return MatchesSecretWords(node.Name) || MatchesSecretWords(node.AutomationId) || MatchesSecretWords(node.HelpText);
        }

        public static string SecretRuleFor(ScanNode node)
        {
            if (node == null) return null;
            if (node.IsPassword.HasValue && node.IsPassword.Value) return SecretRuleIsPassword;
            if (IsSecretClass(node.ClassName) || IsSecretClass(node.RealClassName)) return SecretRuleClass;
            if (MatchesSecretWords(node.Name) || MatchesSecretWords(node.AutomationId) || MatchesSecretWords(node.HelpText)) return SecretRuleName;
            return null;
        }

        // Win32 edit controls carry the password style rather than a property,
        // and the accessibility layer does not always surface it.
        public static bool HasPasswordStyle(long style, string className)
        {
            const long ES_PASSWORD = 0x0020L;
            if (String.IsNullOrEmpty(className)) return false;
            if (className.IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return (style & ES_PASSWORD) != 0;
        }

        public static bool MatchesSecretWords(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;
            try
            {
                return Regex.IsMatch(value, SecretPattern);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSecretClass(string className)
        {
            if (String.IsNullOrEmpty(className)) return false;
            // The credential dialogs Windows itself shows.
            return className.IndexOf("PASSWORD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("Credential", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Applies the session policy to one observed value. The returned step
        // fields never carry a secret, and the reason is always filled in so a
        // reader is told why a value is absent.
        public static void ApplyValue(StepRecord step, string policy, string observed, bool secret, string secretRule)
        {
            if (step == null) return;
            if (secret)
            {
                step.Kind = StepRecord.KindSecretInput;
                step.ValueKind = "secret";
                step.Value = null;
                step.ValueLength = -1;
                step.MaskRule = secretRule ?? SecretRuleIsPassword;
                step.Unavailable.Add("value-not-recorded: this field is treated as a secret (" + step.MaskRule + "). Replay asks the operator to type it.");
                return;
            }
            if (observed == null)
            {
                step.ValueKind = "unknown";
                step.ValueLength = -1;
                step.Value = null;
                step.Unavailable.Add("value-unreadable: the element exposed no readable value, so what was typed is not known.");
                return;
            }
            step.ValueKind = "text";
            step.ValueLength = observed.Length;
            if (String.Equals(policy, PolicyLengthOnly, StringComparison.Ordinal))
            {
                step.Value = null;
                step.MaskRule = PolicyLengthOnly;
                step.Unavailable.Add("value-not-recorded: the session records lengths only, so replay asks the operator to type this.");
                return;
            }
            step.Value = observed;
        }

        // One line stating the rules in force, put into every human and machine
        // readable output so the reader never has to guess.
        public static string PolicyStatement(string policy)
        {
            string head = "No keyboard stream is captured. The state of a fixed list of keys is read to notice a shortcut " +
                "or that typing is happening; a key name is written down only for a command key on that list, and " +
                "typed text is read back from the target element, never from key traffic. " +
                "Clipboard contents are never read. " +
                "The pointer is watched at the event level, so a press, a release, a drag and a wheel turn are recorded " +
                "with their positions; pointer movement on its own is not.";
            if (String.Equals(policy, PolicyLengthOnly, StringComparison.Ordinal))
            {
                return head + " Text field values are recorded as a length only, so replay pauses and asks the operator for every entry.";
            }
            return head + " Text field values are recorded so the recording can be replayed. " +
                "Fields identified as secrets (password property, password window style, or a name such as password / PIN / token) " +
                "have no value written at all and replay pauses for the operator.";
        }

        public static string DescribeLength(int length)
        {
            return length < 0 ? "unknown" : length.ToString(CultureInfo.InvariantCulture) + " character(s)";
        }
    }
}
