namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    // What kind of thing one item of the handover is. The window groups by this;
    // nothing else depends on it, and in particular nothing here decides that one
    // kind implies another.
    public static class AiKinds
    {
        // A part of session.md.
        public const string Section = "section";
        // A body of code written into session.md in full.
        public const string Code = "code";
        // A file of its own, attached beside session.md.
        public const string Attachment = "attachment";
        // What the request asks an answer to look like.
        public const string Protocol = "protocol";
    }

    // One thing the operator can decide to hand over, or not.
    //
    // The id is stable and is what everything else refers to. The label and the
    // note are words for a person and may be rewritten without breaking a
    // selection that was saved under the old wording.
    public sealed class AiItem
    {
        public string Id;
        public string Kind;
        public string Label;
        public string Note;
    }

    // The catalogue of what can be handed over.
    //
    // It is a flat list on purpose. There are no bundles, no presets and no
    // "recommended" set: a preset is the product deciding what the operator
    // wanted, and the whole point here is that it does not know.
    public static class AiItems
    {
        public const string Environment = "environment";
        public const string Privacy = "privacy";
        public const string Apps = "apps";
        public const string Screens = "screens";
        public const string Elements = "elements";
        public const string Actions = "actions";
        public const string Replay = "replay";
        public const string Coverage = "coverage";
        public const string Guidance = "guidance";
        public const string Engine = "engine";
        public const string Vba = "vba";
        public const string Wrapper = "wrapper";
        public const string Pdf = "pdf";
        public const string Protocol = "protocol";

        // The order they are listed in, which is also the order the sections are
        // written in. It never changes with the selection, so an item does not
        // move about the screen as neighbours are switched on and off.
        public static string[] Order()
        {
            return new string[]
            {
                Environment, Privacy, Apps, Screens, Elements, Actions, Replay, Coverage, Guidance,
                Engine, Vba, Wrapper, Pdf, Protocol
            };
        }

        // The nine that describe the recording rather than the code. Used only to
        // notice that most of them have been left out, never to change any of
        // them.
        public static string[] Context()
        {
            return new string[] { Environment, Privacy, Apps, Screens, Elements, Actions, Replay, Coverage, Guidance };
        }

        public static bool IsKnown(string id)
        {
            string[] order = Order();
            for (int index = 0; index < order.Length; index++)
            {
                if (String.Equals(order[index], id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static string KindOf(string id)
        {
            if (String.Equals(id, Pdf, StringComparison.Ordinal)) return AiKinds.Attachment;
            if (String.Equals(id, Protocol, StringComparison.Ordinal)) return AiKinds.Protocol;
            if (String.Equals(id, Engine, StringComparison.Ordinal) ||
                String.Equals(id, Vba, StringComparison.Ordinal) ||
                String.Equals(id, Wrapper, StringComparison.Ordinal)) return AiKinds.Code;
            return AiKinds.Section;
        }

        // The heading a section is written under. Kept beside the catalogue so
        // the window and the file cannot drift apart about what a part is called.
        public static string Title(string id)
        {
            if (String.Equals(id, Environment, StringComparison.Ordinal)) return "Machine, screens and coordinates";
            if (String.Equals(id, Privacy, StringComparison.Ordinal)) return "What was and was not written down";
            if (String.Equals(id, Apps, StringComparison.Ordinal)) return "Applications";
            if (String.Equals(id, Screens, StringComparison.Ordinal)) return "Screens";
            if (String.Equals(id, Elements, StringComparison.Ordinal)) return "Elements";
            if (String.Equals(id, Actions, StringComparison.Ordinal)) return "What the operator did";
            if (String.Equals(id, Replay, StringComparison.Ordinal)) return "Replay results";
            if (String.Equals(id, Coverage, StringComparison.Ordinal)) return "Coverage and limits";
            if (String.Equals(id, Guidance, StringComparison.Ordinal)) return "Writing an automation from this";
            if (String.Equals(id, Engine, StringComparison.Ordinal)) return "The C# automation as it stands";
            if (String.Equals(id, Vba, StringComparison.Ordinal)) return "The VBA automation as it stands";
            if (String.Equals(id, Wrapper, StringComparison.Ordinal)) return "The PowerShell launch wrapper";
            return id;
        }

        public static AiItem Of(string id)
        {
            AiItem item = new AiItem();
            item.Id = id;
            item.Kind = KindOf(id);
            if (String.Equals(id, Environment, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-environment.txt", "Machine, screens and coordinates");
                item.Note = Messages.Text("ai-pick-environment-note.txt", "The displays, the scaling and the fact that every rectangle is in physical pixels.");
            }
            else if (String.Equals(id, Privacy, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-privacy.txt", "What was and was not written down");
                item.Note = Messages.Text("ai-pick-privacy-note.txt", "The value policy in force, and which steps hold a secret that was deliberately never kept.");
            }
            else if (String.Equals(id, Apps, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-apps.txt", "The applications");
                item.Note = Messages.Text("ai-pick-apps-note.txt", "Which processes were recorded, and where their executables are.");
            }
            else if (String.Equals(id, Screens, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-screens.txt", "The screen and window ledger");
                item.Note = Messages.Text("ai-pick-screens-note.txt", "One row per window: title, class, rectangle, and which page of the picture document shows it.");
            }
            else if (String.Equals(id, Elements, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-elements.txt", "The element ledger");
                item.Note = Messages.Text("ai-pick-elements-note.txt", "Every control that was found, with its addresses. The largest part of the file.");
            }
            else if (String.Equals(id, Actions, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-actions.txt", "What the operator did");
                item.Note = Messages.Text("ai-pick-actions-note.txt", "The recorded steps in order, with their locators, intervals and observed effects.");
            }
            else if (String.Equals(id, Replay, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-replay.txt", "The replay results");
                item.Note = Messages.Text("ai-pick-replay-note.txt", "Which route actually worked for each step the last time it was played back.");
            }
            else if (String.Equals(id, Coverage, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-coverage.txt", "Coverage, limits and diagnostics");
                item.Note = Messages.Text("ai-pick-coverage-note.txt", "What each acquisition layer reached, what could not be obtained, and the tool's own diagnostics.");
            }
            else if (String.Equals(id, Guidance, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-guidance.txt", "Guidance and the safety rules");
                item.Note = Messages.Text("ai-pick-guidance-note.txt", "How to address an element, and the rules the code may not trade away - never press a remembered coordinate, always ask for a secret.");
            }
            else if (String.Equals(id, Engine, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-engine.txt", "The C# automation code");
                item.Note = Messages.Text("ai-pick-engine-note.txt", "The five C# modules as they are in the editor now, in full. This is the main thing that gets edited.");
            }
            else if (String.Equals(id, Vba, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-vba.txt", "The VBA code");
                item.Note = Messages.Text("ai-pick-vba-note.txt", "The five VBA modules as they are in the editor now, in full. Independent of the C# above.");
            }
            else if (String.Equals(id, Wrapper, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-wrapper.txt", "The PowerShell launch wrapper");
                item.Note = Messages.Text("ai-pick-wrapper-note.txt", "Generated by the build, not edited by hand: it only compiles the C# and calls it. Include it when the question is about how the built file starts, logs or fails - not to have it rewritten.");
            }
            else if (String.Equals(id, Pdf, StringComparison.Ordinal))
            {
                item.Label = Messages.Text("ai-pick-pdf.txt", "The screen picture document");
                item.Note = Messages.Text("ai-pick-pdf-note.txt", "screens.pdf, one page per captured screen. A second attachment; only written when this is on.");
            }
            else
            {
                item.Label = Messages.Text("ai-pick-protocol.txt", "The answer format and the take-back protocol");
                item.Note = Messages.Text("ai-pick-protocol-note.txt", "The marked block an answer has to come back in. Without it an answer cannot be read back into the editor automatically.");
            }
            return item;
        }
    }

    // Something worth saying about a selection, that is not a reason to refuse
    // it. Every one of these is a consequence stated out loud; none of them
    // changes a single pick, and none of them stops anything being generated.
    public sealed class AiWarning
    {
        public string Id;
        public string Text;
    }

    // What the operator chose to hand over.
    //
    // Every item stands on its own. Nothing here reads one pick to decide
    // another, nothing is disabled because of a combination, and no combination
    // is refused - including the empty one, which produces a request with no
    // attachments and says so.
    public sealed class AiPicks
    {
        private readonly Dictionary<string, bool> chosen = new Dictionary<string, bool>(StringComparer.Ordinal);

        public bool Has(string id)
        {
            bool on;
            return chosen.TryGetValue(id, out on) && on;
        }

        public void Set(string id, bool on)
        {
            if (!AiItems.IsKnown(id)) return;
            chosen[id] = on;
        }

        // The starting point, which is what this product handed over before any
        // of it could be chosen: everything that used to be fixed is on, and the
        // wrapper - which was never in the handover - is off. It is a starting
        // point and nothing more; every item can be changed from here and
        // nothing puts it back.
        public static AiPicks Default()
        {
            AiPicks picks = new AiPicks();
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++)
            {
                picks.chosen[order[index]] = !String.Equals(order[index], AiItems.Wrapper, StringComparison.Ordinal);
            }
            return picks;
        }

        public static AiPicks Nothing()
        {
            AiPicks picks = new AiPicks();
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++) picks.chosen[order[index]] = false;
            return picks;
        }

        public static AiPicks Everything()
        {
            AiPicks picks = new AiPicks();
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++) picks.chosen[order[index]] = true;
            return picks;
        }

        public int Count
        {
            get
            {
                int count = 0;
                string[] order = AiItems.Order();
                for (int index = 0; index < order.Length; index++) if (Has(order[index])) count++;
                return count;
            }
        }

        public bool AnyCode
        {
            get { return Has(AiItems.Engine) || Has(AiItems.Vba) || Has(AiItems.Wrapper); }
        }

        // Whether session.md has anything at all to hold. When it does not, no
        // session.md is written and the request says there is no attachment,
        // rather than naming a file that is empty or stale.
        public bool AnyDocument
        {
            get
            {
                string[] order = AiItems.Order();
                for (int index = 0; index < order.Length; index++)
                {
                    string id = order[index];
                    if (String.Equals(id, AiItems.Pdf, StringComparison.Ordinal)) continue;
                    if (String.Equals(id, AiItems.Protocol, StringComparison.Ordinal)) continue;
                    if (Has(id)) return true;
                }
                return false;
            }
        }

        // The chosen ids, in catalogue order, as one line. Saved with the code so
        // a selection survives closing the window; a file written before any of
        // this existed has no such line and gets the starting point.
        public string Store()
        {
            StringBuilder text = new StringBuilder();
            string[] order = AiItems.Order();
            for (int index = 0; index < order.Length; index++)
            {
                if (!Has(order[index])) continue;
                if (text.Length != 0) text.Append(',');
                text.Append(order[index]);
            }
            return text.ToString();
        }

        // An empty selection is a selection. It is written as a single mark so it
        // can be told apart from nothing having been saved at all, which is what
        // gets the starting point instead.
        public const string EmptyMark = "-";

        public static AiPicks Restore(string text)
        {
            if (text == null) return Default();
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return Default();
            AiPicks picks = Nothing();
            if (String.Equals(trimmed, EmptyMark, StringComparison.Ordinal)) return picks;
            string[] parts = trimmed.Split(',');
            for (int index = 0; index < parts.Length; index++) picks.Set(parts[index].Trim(), true);
            return picks;
        }

        public string StoreLine()
        {
            string stored = Store();
            return stored.Length == 0 ? EmptyMark : stored;
        }

        // What is worth saying about this selection before anything is generated.
        //
        // These are consequences, not objections. Each one names what will be
        // missing from the handover and stops there: nothing is switched on to
        // repair it, nothing is greyed out, and the request is generated either
        // way.
        public List<AiWarning> Warnings()
        {
            List<AiWarning> warnings = new List<AiWarning>();
            if (!AnyCode)
            {
                Add(warnings, "no-code", Messages.Text("ai-warn-no-code.txt",
                    "No code is selected, so the handover contains no automation to change. An answer can only describe what to do, not return a module."));
            }
            if (!Has(AiItems.Pdf))
            {
                Add(warnings, "no-pdf", Messages.Text("ai-warn-no-pdf.txt",
                    "The picture document is off, so no screenshot is handed over. Nothing will be able to describe what a window looked like."));
            }
            if (!Has(AiItems.Protocol))
            {
                Add(warnings, "no-protocol", Messages.Text("ai-warn-no-protocol.txt",
                    "The answer format is off, so the request does not ask for a marked answer. Whatever comes back cannot be taken into the editor automatically."));
            }
            if (!Has(AiItems.Guidance) && AnyCode)
            {
                Add(warnings, "no-guidance", Messages.Text("ai-warn-no-guidance.txt",
                    "The safety rules are off while code is being handed over. Nothing in the handover states that a remembered coordinate must never be pressed or that a secret must be asked for."));
            }
            string[] context = AiItems.Context();
            int off = 0;
            for (int index = 0; index < context.Length; index++) if (!Has(context[index])) off++;
            if (off > context.Length / 2)
            {
                Add(warnings, "thin-context", Messages.Text("ai-warn-thin-context.txt",
                    "Most of what describes the recording is left out, so there may not be enough here to judge a change by.") +
                    " (" + off.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                    context.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            }
            return warnings;
        }

        private static void Add(List<AiWarning> into, string id, string text)
        {
            AiWarning warning = new AiWarning();
            warning.Id = id;
            warning.Text = text;
            into.Add(warning);
        }
    }
}
