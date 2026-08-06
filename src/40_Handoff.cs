namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One file the operator attaches to the chat. There is a row here only for a
    // file this request actually had written for it.
    public sealed class HandoffAttachment
    {
        public string Name;
        public string Path;
        public string What;
        public bool Exists;
        public long Bytes;
    }

    public sealed class HandoffResult
    {
        public string RequestId;
        public string Text;
        public string Path;
        public string Problem;
        public string Folder;
        public List<HandoffAttachment> Attachments = new List<HandoffAttachment>();
        // What was chosen, and what came of choosing it. Carried here so the
        // window can show the same account the file gives, rather than a second
        // one assembled from the same parts.
        public AiPicks Picks;
        public SessionMdResult Markdown;
        public ScreensPdfResult Pdf;
        public List<AiWarning> Warnings = new List<AiWarning>();

        // Whether every file this request tells an assistant to read is on disk.
        //
        // It is no longer "are there two of them". A request that names no
        // attachment is a complete request - it asks a question and says there is
        // nothing attached - and calling that unready would refuse to send the
        // one thing the operator chose to send. What is checked is the promise:
        // nothing is named that is not there.
        public bool AttachmentsReady
        {
            get
            {
                for (int index = 0; index < Attachments.Count; index++)
                {
                    if (!Attachments[index].Exists) return false;
                }
                return true;
            }
        }

        public string MissingText()
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < Attachments.Count; index++)
            {
                if (Attachments[index].Exists) continue;
                if (text.Length != 0) text.Append(", ");
                text.Append(Attachments[index].Name);
            }
            return text.ToString();
        }
    }

    // The text that goes to an assistant, and - when the operator asked for one -
    // the one shape an answer may come back in.
    //
    // It is short on purpose, and it is one copy. What an assistant has to read is
    // in the files attached with it. Putting it in here as well made the request
    // long enough to need cutting into numbered pieces, and a request the operator
    // has to paste four times is a request three of whose pastes can go wrong.
    //
    // Nothing here is fixed any more. It does not say there are two files, it does
    // not say the code is in a particular numbered section, and it does not say
    // everything is included: it says what was actually generated this time, by
    // reading the result of generating it.
    public static class Handoff
    {
        public const string Marker = "#@APPSTUDIO";
        public const string SessionFile = "session.md";
        public const string ScreensFile = "screens.pdf";

        public static string NewRequestId()
        {
            return Guid.NewGuid().ToString("D");
        }

        public static bool IsRequestId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 36) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (index == 8 || index == 13 || index == 18 || index == 23)
                {
                    if (character != '-') return false;
                    continue;
                }
                bool digit = character >= '0' && character <= '9';
                bool lower = character >= 'a' && character <= 'f';
                bool upper = character >= 'A' && character <= 'F';
                if (!digit && !lower && !upper) return false;
            }
            return true;
        }

        public static HandoffResult Build(StudioSession session, CodeProject project, string requestText, string requestId)
        {
            return Build(session, project, requestText, requestId, null, null, null);
        }

        public static HandoffResult Build(StudioSession session, CodeProject project, string requestText, string requestId,
            AiPicks picks, SessionMdResult markdown, ScreensPdfResult pdf)
        {
            HandoffResult result = new HandoffResult();
            result.RequestId = IsRequestId(requestId) ? requestId : NewRequestId();
            result.Picks = picks == null ? AiPicks.Default() : picks;
            result.Markdown = markdown;
            result.Pdf = pdf;
            result.Warnings = result.Picks.Warnings();
            Look(result, session);
            StringBuilder text = new StringBuilder();
            Head(text, result);
            Ask(text, requestText);
            Modules(text, project, result);
            if (result.Picks.Has(AiItems.Protocol)) ReturnFormat(text, result.RequestId);
            else NoProtocol(text);
            result.Text = text.ToString();
            return result;
        }

        // What is attached, and whether it is really there.
        //
        // Nothing here creates a file. A file is listed only when this request had
        // it written, so a document left over from an earlier request with a
        // different selection is never presented as part of this one.
        private static void Look(HandoffResult result, StudioSession session)
        {
            result.Folder = session == null ? null : session.AiFolder;
            bool wantsDocument = result.Picks.AnyDocument;
            bool documentWritten = result.Markdown == null ? wantsDocument : result.Markdown.Written;
            if (wantsDocument && documentWritten)
            {
                result.Attachments.Add(Attachment(result.Folder, SessionFile, Describe(result)));
            }
            if (result.Picks.Has(AiItems.Pdf))
            {
                bool pictureWritten = result.Pdf == null || result.Pdf.Written;
                if (pictureWritten)
                {
                    result.Attachments.Add(Attachment(result.Folder, ScreensFile,
                        "one page per screen, labelled with the screen id that " + SessionFile + " uses"));
                }
            }
        }

        // What session.md holds this time, in the words of the parts that are
        // actually in it.
        private static string Describe(HandoffResult result)
        {
            if (result.Markdown == null || result.Markdown.Sections.Count == 0)
            {
                return "what was recorded and what the automation looks like";
            }
            // The titles as they are written at the top of each part. They are not
            // folded to lower case to fit the sentence: "the C# automation" is a
            // name, and "the c# automation" is a different string from the one the
            // reader is being sent to look for.
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < result.Markdown.Sections.Count; index++)
            {
                if (index != 0) text.Append(index == result.Markdown.Sections.Count - 1 ? " and " : ", ");
                text.Append('"').Append(result.Markdown.Sections[index].Title).Append('"');
            }
            return text.ToString();
        }

        private static HandoffAttachment Attachment(string folder, string name, string what)
        {
            HandoffAttachment attachment = new HandoffAttachment();
            attachment.Name = name;
            attachment.What = what;
            if (folder != null) attachment.Path = Path.Combine(folder, name);
            try
            {
                if (attachment.Path != null && File.Exists(attachment.Path))
                {
                    attachment.Exists = true;
                    attachment.Bytes = new FileInfo(attachment.Path).Length;
                }
            }
            catch
            {
                attachment.Exists = false;
            }
            return attachment;
        }

        private static void Head(StringBuilder text, HandoffResult result)
        {
            text.AppendLine("# " + App.Name + " " + App.Version + " - request to an assistant");
            text.AppendLine();
            text.AppendLine("Request id: `" + result.RequestId + "`");
            text.AppendLine();
            if (result.Attachments.Count == 0)
            {
                // Said plainly, because the alternative is a request that tells an
                // assistant to go and read files that were never written.
                text.AppendLine("## Nothing is attached to this message");
                text.AppendLine();
                text.AppendLine("There are no attachments. The operator chose to send the question on its own, so");
                text.AppendLine("there is no recording, no ledger and no code here to read. Do not ask for an");
                text.AppendLine("attachment and do not assume one was lost: answer from what is written below, or");
                text.AppendLine("say what you would need.");
                text.AppendLine();
                return;
            }
            text.AppendLine(result.Attachments.Count == 1
                ? "## The file attached with this message"
                : "## The " + Count(result.Attachments.Count) + " files attached with this message");
            text.AppendLine();
            for (int index = 0; index < result.Attachments.Count; index++)
            {
                HandoffAttachment attachment = result.Attachments[index];
                text.AppendLine("- **`" + attachment.Name + "`** - " + attachment.What + ".");
            }
            text.AppendLine();
            text.AppendLine(result.Attachments.Count == 1
                ? "Read it before you answer."
                : "Read them before you answer.");
            text.AppendLine();
            // What is in the attachment is stated from the attachment, so a part
            // the operator left out is never claimed here.
            if (result.Markdown != null && result.Markdown.Sections.Count > 0)
            {
                text.AppendLine("`" + SessionFile + "` has these parts, and only these:");
                text.AppendLine();
                for (int index = 0; index < result.Markdown.Sections.Count; index++)
                {
                    SessionMdSection section = result.Markdown.Sections[index];
                    text.AppendLine("- " + section.Number.ToString(CultureInfo.InvariantCulture) + ". " + section.Title);
                }
                text.AppendLine();
                string code = CodeReference(result);
                if (code != null)
                {
                    text.AppendLine("The automation you are being asked about is in " + code + ", written out module by");
                    text.AppendLine("module and in full.");
                    text.AppendLine();
                }
                string guidance = result.Markdown.Reference(AiItems.Guidance);
                if (guidance != null)
                {
                    text.AppendLine("The operations both languages share, and the rules the code may not trade away, are in " +
                        guidance + ".");
                    text.AppendLine("Read it before you change anything, and keep those rules.");
                    text.AppendLine();
                }
                text.AppendLine("Anything not in that list was left out deliberately by the operator. It is not missing");
                text.AppendLine("by accident, so do not reconstruct it and do not answer as though you had seen it. If");
                text.AppendLine("what is there is not enough to answer, say what is missing.");
                text.AppendLine();
            }
            text.AppendLine("If a file named above is missing from this conversation, say so and stop. Do not");
            text.AppendLine("reconstruct what you cannot see.");
            text.AppendLine();
        }

        // Where the code is, named by whichever parts of it were included.
        private static string CodeReference(HandoffResult result)
        {
            List<string> parts = new List<string>();
            string engine = result.Markdown.Reference(AiItems.Engine);
            if (engine != null) parts.Add(engine);
            string vba = result.Markdown.Reference(AiItems.Vba);
            if (vba != null) parts.Add(vba);
            string wrapper = result.Markdown.Reference(AiItems.Wrapper);
            if (wrapper != null) parts.Add(wrapper);
            if (parts.Count == 0) return null;
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < parts.Count; index++)
            {
                if (index != 0) text.Append(index == parts.Count - 1 ? " and " : ", ");
                text.Append(parts[index]);
            }
            return text.ToString();
        }

        private static string Count(int value)
        {
            if (value == 2) return "two";
            if (value == 3) return "three";
            if (value == 4) return "four";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static void Ask(StringBuilder text, string requestText)
        {
            text.AppendLine("## What is being asked");
            text.AppendLine();
            string ask = requestText == null ? "" : requestText.Trim();
            // Nothing is invented here. A request nobody wrote is a request that
            // gets sent unread, so an empty box says it is empty rather than
            // having the product state what the operator wanted.
            text.AppendLine(ask.Length == 0
                ? "*(The operator did not write a request. Nothing is being asked for yet - do not guess at one.)*"
                : ask);
            text.AppendLine();
        }

        // The names of the code that is actually in the attachment, so an answer
        // can say which module it is returning. No code here: the code is in the
        // attachment, when there is any.
        private static void Modules(StringBuilder text, CodeProject project, HandoffResult result)
        {
            text.AppendLine("## The modules");
            text.AppendLine();
            bool engine = result.Picks.Has(AiItems.Engine);
            bool vba = result.Picks.Has(AiItems.Vba);
            bool wrapper = result.Picks.Has(AiItems.Wrapper);
            if (!engine && !vba && !wrapper)
            {
                text.AppendLine("No code was handed over. The operator did not include any, so there is no module here");
                text.AppendLine("to change and none to return. Answer about what to do rather than with code you have");
                text.AppendLine("not been shown.");
                text.AppendLine();
                return;
            }
            List<CodeFile> files = new List<CodeFile>();
            if (project != null)
            {
                if (engine) files.AddRange(project.Files(ScriptLanguages.PowerShell));
                if (vba) files.AddRange(project.Files(ScriptLanguages.Vba));
            }
            if (files.Count == 0 && !wrapper)
            {
                text.AppendLine("No automation has been generated for this session yet.");
                text.AppendLine();
                return;
            }
            if (engine && vba)
            {
                text.AppendLine("Both languages were handed over. Answer in the one the request is about, and return");
                text.AppendLine("only the modules you actually changed.");
            }
            else if (engine)
            {
                text.AppendLine("Only the C# was handed over. The VBA spelling of this automation exists but was not");
                text.AppendLine("included, so do not return VBA and do not describe changes to it as though you had");
                text.AppendLine("read it.");
            }
            else if (vba)
            {
                text.AppendLine("Only the VBA was handed over. The C# engine exists but was not included, so do not");
                text.AppendLine("return C# and do not describe changes to it as though you had read it.");
            }
            text.AppendLine();
            if (files.Count > 0)
            {
                text.AppendLine("| language | module | name to use in the answer | lines |");
                text.AppendLine("|---|---|---|---|");
                for (int index = 0; index < files.Count; index++)
                {
                    CodeFile file = files[index];
                    text.AppendLine("| " + Spoken(file.Language) + " | " + file.FileName + " | `" + file.Name + "` | " +
                        CodeProject.LineCount(file.Text).ToString(CultureInfo.InvariantCulture) + " |");
                }
                text.AppendLine();
                text.AppendLine("`" + CodeModules.Workflow + "` is the module a person edits: one line is one step of the");
                text.AppendLine("recording. `" + CodeModules.RecordedFacts + "` holds the addresses and intervals that");
                text.AppendLine("recording produced. The three `Runtime` modules are the machinery. Change the");
                text.AppendLine("smallest set that does what was asked, and keep the split: a change that puts");
                text.AppendLine("the machinery back into the workflow will not be taken in.");
                text.AppendLine();
            }
            if (wrapper)
            {
                text.AppendLine("The PowerShell launch wrapper is included as well. It is **generated by the build on");
                text.AppendLine("every build and is not an edit target** - a replacement for it would be overwritten");
                text.AppendLine("without anyone being told. It is there to be read, so questions about how the built");
                text.AppendLine("file starts, logs and fails can be answered; do not return it as a module.");
                text.AppendLine();
            }
        }

        // What a language is called when talking to a person. The token the
        // protocol uses is a different thing and is not changed here: `powershell`
        // is what an answer writes, because that is what the reader on this side
        // matches, and what it holds is C#.
        private static string Spoken(string language)
        {
            return String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal) ? "VBA" : "C#";
        }

        // Said when the operator turned the answer format off.
        //
        // The request then asks a question like any other message, and nothing
        // that comes back can be read into the editor by the machinery. Claiming
        // otherwise would be the one lie this file exists to avoid.
        private static void NoProtocol(StringBuilder text)
        {
            text.AppendLine("## How to answer");
            text.AppendLine();
            text.AppendLine("Answer in plain words, however suits the question.");
            text.AppendLine();
            text.AppendLine("There is no machine readable format for this request: the operator turned it off. Do");
            text.AppendLine("not wrap the answer in markers and do not treat it as something that will be applied");
            text.AppendLine("automatically - it will be read by a person, who will decide what to do with it.");
            text.AppendLine();
        }

        // The only shape an answer may come back in, when one was asked for.
        private static void ReturnFormat(StringBuilder text, string requestId)
        {
            text.AppendLine("## How to answer");
            text.AppendLine();
            text.AppendLine("Answer in the body of the chat, in one code block. Do not answer with a file to");
            text.AppendLine("download, an attachment or a link.");
            text.AppendLine();
            text.AppendLine("Every module in the answer is wrapped in the lines below, which carry the");
            text.AppendLine("request id this request was issued with, so an answer to a different request can");
            text.AppendLine("never be applied by accident. These lines are protocol, not code: they are");
            text.AppendLine("removed before anything is compiled or run, so do not put them inside a");
            text.AppendLine("function.");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " SUMMARY BEGIN");
            text.AppendLine("what was changed, in plain words");
            text.AppendLine(Marker + " " + requestId + " SUMMARY END");
            text.AppendLine(Marker + " " + requestId + " BEGIN powershell " + CodeModules.Workflow);
            text.AppendLine("...the whole module...");
            text.AppendLine(Marker + " " + requestId + " END powershell " + CodeModules.Workflow);
            text.AppendLine(Marker + " " + requestId + " COMPLETE 1");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("Rules:");
            text.AppendLine();
            text.AppendLine("- The language token is `powershell` or `vba`. `powershell` is the token for the C# modules -");
            text.AppendLine("  it is what the reader on this side matches, and it is not a request for PowerShell code.");
            text.AppendLine("  Write C# under it. Only answer in a language that was actually handed over to you.");
            text.AppendLine("- The name after the language is the module name from the table above, without the extension.");
            text.AppendLine("- Return the **whole** module, not a patch and not an excerpt. A module that is cut off is");
            text.AppendLine("  refused. Do not write `# unchanged` or anything like it in place of code.");
            text.AppendLine("- `COMPLETE` carries the number of modules in the answer and has to agree with them.");
            text.AppendLine("- A name may be letters, digits and underscores, and has to start with a letter.");
            text.AppendLine("- The same module may appear only once.");
            text.AppendLine("- Several modules in one answer are fine: repeat BEGIN..END for each and count them all in");
            text.AppendLine("  COMPLETE. Leave out the modules you did not change.");
            text.AppendLine("- Do not write anything outside the code block except, if you are sending the answer in");
            text.AppendLine("  parts, the one line asking whether to send the next one.");
            text.AppendLine();
            text.AppendLine("If the answer is too long for one message, send **one module per message** and put this");
            text.AppendLine("line above it, inside the same code block:");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " PART 00 OF 03");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("Numbering starts at 00, every part carries the same total, and every part ends with");
            text.AppendLine("`COMPLETE 1` because it carries one module. Decide the total before you send the first");
            text.AppendLine("part and do not change it. Do not skip a number and do not go back to one already sent.");
            text.AppendLine();
            text.AppendLine("There are exactly two kinds of answer. The other one is a refusal, which is a result");
            text.AppendLine("rather than a failure, so it says which refusal it is and why. It never asks anything");
            text.AppendLine("back: a request that cannot be settled from what is here comes back as UNCLEAR with the");
            text.AppendLine("reason. Do not offer choices and do not ask a question.");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(Marker + " " + requestId + " SUMMARY BEGIN");
            text.AppendLine("why, and what was looked at");
            text.AppendLine(Marker + " " + requestId + " SUMMARY END");
            text.AppendLine(Marker + " " + requestId + " NOCHANGE UNNECESSARY");
            text.AppendLine(Marker + " " + requestId + " COMPLETE 0");
            text.AppendLine("```");
            text.AppendLine();
            text.AppendLine("`UNNECESSARY` the code already does what was asked. `IMPOSSIBLE` it could be done, but not by");
            text.AppendLine("changing these modules. `UNCLEAR` it cannot be settled from what was given, and the summary");
            text.AppendLine("says what is missing. All four lines are required in a refusal too.");
            text.AppendLine();
            text.AppendLine("The request id is `" + requestId + "`. Do not change it and do not leave it out.");
            text.AppendLine();
        }

        // Writes the request beside the code it is about. It is deliberately not
        // put in the assistant folder: that folder holds exactly the files this
        // request tells an assistant to read, so "attach what is in ai/" stays a
        // complete instruction whatever was selected.
        public static string Write(CodeProject project, HandoffResult result)
        {
            if (project == null || project.Folder == null) return "This session has no folder on disk.";
            try
            {
                Directory.CreateDirectory(project.Folder);
                string path = Path.Combine(project.Folder, "request.md");
                File.WriteAllText(path, result.Text, new UTF8Encoding(false));
                result.Path = path;
                return null;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result.Problem;
            }
        }
    }
}
