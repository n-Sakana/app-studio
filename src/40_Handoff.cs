namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One of the two files the operator attaches to the chat.
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

        // Whether both files the request tells the assistant to read are
        // actually on disk. A request that names an attachment that was never
        // written is a request that cannot be answered, and saying so before
        // the operator pastes it is the whole point of looking.
        public bool AttachmentsReady
        {
            get
            {
                if (Attachments.Count != 2) return false;
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

    // The text that goes to an assistant, and the one shape an answer may come
    // back in.
    //
    // It is short on purpose, and it is one copy. What the assistant has to read
    // - the machine, the recording, the ledger, what could not be obtained and
    // the automation as it stands - is in the two files that are attached with
    // it, which is where all of that already lives. Putting it in here as well
    // made the request long enough to need cutting into numbered pieces, and a
    // request the operator has to paste four times is a request three of whose
    // pastes can go wrong.
    //
    // So there is no cutting here and there is nothing to cut. The only thing
    // that is ever split is the answer, by the assistant, one file at a time,
    // and that is the assistant's side of the protocol.
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
            HandoffResult result = new HandoffResult();
            result.RequestId = IsRequestId(requestId) ? requestId : NewRequestId();
            Look(result, session);
            StringBuilder text = new StringBuilder();
            Head(text, result);
            Ask(text, requestText);
            Modules(text, project);
            ReturnFormat(text, result.RequestId);
            result.Text = text.ToString();
            return result;
        }

        // What is attached, and whether it is really there. Nothing here creates
        // the files; it reports what the session actually wrote.
        private static void Look(HandoffResult result, StudioSession session)
        {
            result.Folder = session == null ? null : session.AiFolder;
            result.Attachments.Add(Attachment(result.Folder, SessionFile,
                "the machine and the coordinate system, what was and was not written down, the applications, " +
                "the screens, the elements, what the operator did with the intervals and what held the keyboard, " +
                "the replay results, what could not be obtained, and the automation as it stands"));
            result.Attachments.Add(Attachment(result.Folder, ScreensFile,
                "one page per screen, labelled with the screen id that " + SessionFile + " uses"));
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
            text.AppendLine("## The two files attached with this message");
            text.AppendLine();
            text.AppendLine("Everything you need is in them. Read both before you answer. Do not ask for the");
            text.AppendLine("code, the log or the screenshots to be pasted into the chat: they are attached,");
            text.AppendLine("in full, with nothing left out.");
            text.AppendLine();
            for (int index = 0; index < result.Attachments.Count; index++)
            {
                HandoffAttachment attachment = result.Attachments[index];
                text.AppendLine("- **`" + attachment.Name + "`** - " + attachment.What + ".");
            }
            text.AppendLine();
            text.AppendLine("The automation you are being asked to change is section 10 of `" + SessionFile + "`,");
            text.AppendLine("written out module by module. That section also states the operations both");
            text.AppendLine("languages share and the rules the code may not trade away. Read it before you");
            text.AppendLine("change anything, and keep those rules.");
            text.AppendLine();
            text.AppendLine("If either file is missing from this conversation, say so and stop. Do not");
            text.AppendLine("reconstruct what you cannot see.");
            text.AppendLine();
        }

        private static void Ask(StringBuilder text, string requestText)
        {
            text.AppendLine("## What is being asked");
            text.AppendLine();
            string ask = requestText == null ? "" : requestText.Trim();
            text.AppendLine(ask.Length == 0
                ? "Improve the automation so it carries out the recorded procedure reliably. Keep every safety rule stated in section 10 of the attached file."
                : ask);
            text.AppendLine();
        }

        // The names and sizes of what is there, so the answer can say which
        // module it is returning. No code: the code is in the attachment.
        private static void Modules(StringBuilder text, CodeProject project)
        {
            text.AppendLine("## The modules");
            text.AppendLine();
            if (project == null)
            {
                text.AppendLine("No automation has been generated for this session yet.");
                text.AppendLine();
                return;
            }
            List<CodeFile> files = project.All();
            if (files.Count == 0)
            {
                text.AppendLine("No automation has been generated for this session yet.");
                text.AppendLine();
                return;
            }
            text.AppendLine("Both languages are first class here. Answer in the one the request is about,");
            text.AppendLine("and return only the modules you actually changed.");
            text.AppendLine();
            text.AppendLine("| language | module | name to use in the answer | lines |");
            text.AppendLine("|---|---|---|---|");
            for (int index = 0; index < files.Count; index++)
            {
                CodeFile file = files[index];
                text.AppendLine("| " + file.Language + " | " + file.FileName + " | `" + file.Name + "` | " +
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

        // The only shape an answer may come back in.
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
            text.AppendLine("- The language is `powershell` or `vba`. Both are first class here; do not answer in one and");
            text.AppendLine("  describe the other in prose.");
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
        // put in the assistant folder: that folder is exactly two files and
        // saying "attach what is in ai/" has to stay a complete instruction.
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
