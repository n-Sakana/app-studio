namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    // Everything that leaves the tool for the assistant, in the two files a chat
    // window will take: one text file with every fact, and one document with
    // every picture. The pair is called a handoff bundle and carries an id of
    // its own, because an answer written against one bundle must not be run
    // against a different one that happens to share a case folder.
    public sealed class HandoffBundle
    {
        public string BundleId;
        public DateTimeOffset CreatedAt;
        public string Folder;
        public string TextPath;
        public string PdfPath;
        public string TextSha256;
        public string PdfSha256;
        public long TextBytes;
        public long PdfBytes;
        public int PageCount;
        public int ScreenCount;
        public int ComponentCount;
        public int ComponentTotal;
        public string ScanId;
        // What the answer is allowed to assume. Recomputed when the answer comes
        // back; a different value means the ground moved and the answer is not
        // run against it.
        public string PremiseHash;
        public List<string> Problems = new List<string>();
        // Set when there was nothing to picture at all. Not a failure, but never
        // left silent: the request has to say what it is not carrying.
        public string NoPictureReason;

        // The request can go out as soon as the text attachment exists. The
        // picture document is part of it whenever there is anything to picture,
        // and its absence is stated rather than blocking the operator.
        public bool Complete { get { return Problems.Count == 0 && TextPath != null && (PdfPath != null || NoPictureReason != null); } }

        public string TextName { get { return TextPath == null ? null : Path.GetFileName(TextPath); } }
        public string PdfName { get { return PdfPath == null ? null : Path.GetFileName(PdfPath); } }

        public JsonObject ToJson()
        {
            return new JsonObject()
                .Add("kind", "handoff")
                .Add("bundleId", BundleId)
                .Add("createdAt", CreatedAt)
                .Add("scanId", ScanId)
                .Add("premiseHash", PremiseHash)
                .Add("folder", Folder)
                .Add("textFile", TextName)
                .Add("textSha256", TextSha256)
                .Add("textBytes", TextBytes)
                .Add("pdfFile", PdfName)
                .Add("pdfSha256", PdfSha256)
                .Add("pdfBytes", PdfBytes)
                .Add("pageCount", PageCount)
                .Add("screenCount", ScreenCount)
                .Add("componentCount", ComponentCount)
                .Add("componentTotal", ComponentTotal)
                .Add("noPictureReason", NoPictureReason)
                .Add("problems", Problems.ToArray());
        }
    }

    public static class HandoffBuilder
    {
        public const string TextFileName = "handoff.txt";
        public const string PdfFileName = "screens.pdf";
        public const string LedgerFileName = "screens.json";
        public const string RecordFileName = "handoff.json";

        // Entering the assistant route without running the automatic scan is a
        // documented way to work: the assistant gets no part list and can only
        // aim at points on the screen. It is still given a picture, because one
        // was taken of the target when this step opened, so the pair of
        // attachments stays a pair instead of the request naming a file that
        // was never made.
        private static ScreenLedger OrScreenshotOnly(ScreenLedger ledger, CaseRecord record)
        {
            if (ledger != null && ledger.Screens.Count > 0) return ledger;
            if (record == null || String.IsNullOrEmpty(record.ShotFile) || !File.Exists(record.ShotFile)) return ledger;
            ScreenLedger made = new ScreenLedger();
            ScreenRecord screen = new ScreenRecord();
            screen.ScreenId = "S1";
            screen.Title = record.TargetTitle;
            screen.ShotFile = record.ShotFile;
            screen.CaptureMethod = "case";
            screen.Note = Messages.Text("handoff-unscanned.txt",
                "This screen was photographed but never scanned, so no component ids exist for it. Aim at points on the screen.");
            made.Screens.Add(screen);
            return made;
        }

        // A chat window takes a small number of attachments, so the whole
        // investigation is written into one text file and every picture into one
        // document. Nothing is left in a third file that the reader would have
        // to be told about separately.
        public static HandoffBundle Build(CaseRecord record, RequestBundle request, ScreenLedger ledger, string folder, string goal)
        {
            ledger = OrScreenshotOnly(ledger, record);
            HandoffBundle bundle = new HandoffBundle();
            bundle.CreatedAt = DateTimeOffset.Now;
            bundle.BundleId = "hb-" + bundle.CreatedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            bundle.Folder = folder;
            bundle.ScanId = ledger == null ? null : ledger.ScanId;
            bundle.ScreenCount = ledger == null ? 0 : ledger.Screens.Count;
            bundle.ComponentCount = request == null || request.Elements == null ? 0 : request.Elements.ListedCount;
            bundle.ComponentTotal = request == null || request.Elements == null ? 0 : request.Elements.TotalCount;
            bundle.PremiseHash = PremiseHash(ledger, request == null ? null : request.Elements);

            if (String.IsNullOrWhiteSpace(folder))
            {
                bundle.Problems.Add("There is no folder to write the attachments into.");
                return bundle;
            }
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception)
            {
                bundle.Problems.Add("The attachment folder could not be made: " + exception.GetType().Name + ": " + exception.Message);
                return bundle;
            }

            // The document is written first, because the text file states which
            // page shows which screen and cannot be finished before that is
            // known.
            string pdfPath = Path.Combine(folder, PdfFileName);
            WritePdf(bundle, ledger, record, pdfPath);

            string textPath = Path.Combine(folder, TextFileName);
            string text = BuildText(record, request, ledger, bundle, goal);
            try
            {
                WriteAtomic(textPath, text);
                bundle.TextPath = textPath;
                Measure(textPath, out bundle.TextBytes, out bundle.TextSha256);
            }
            catch (Exception exception)
            {
                bundle.Problems.Add("The text attachment could not be written: " + exception.GetType().Name + ": " + exception.Message);
            }
            return bundle;
        }

        private static void WritePdf(HandoffBundle bundle, ScreenLedger ledger, CaseRecord record, string path)
        {
            List<PdfPage> pages = new List<PdfPage>();
            if (ledger != null)
            {
                for (int index = 0; index < ledger.Screens.Count; index++)
                {
                    ScreenRecord screen = ledger.Screens[index];
                    PdfPage page = new PdfPage();
                    page.ImagePath = screen.HasShot ? screen.ShotFile : null;
                    pages.Add(page);
                    screen.PdfPage = pages.Count;
                }
            }
            // With no screen and no picture of the target there is nothing to put
            // in a document. That is stated, not treated as a failure: the route
            // that skips the automatic scan is a documented way to work, and the
            // text attachment on its own is still an answerable request.
            if (pages.Count == 0)
            {
                bundle.NoPictureReason = Messages.Text("handoff-nopdf.txt",
                    "No picture of the target could be taken, so only the text attachment was made.");
                return;
            }
            // The caption is filled in once the page numbers are settled, so it
            // can state "page 2 of 5" rather than only its own number.
            for (int index = 0; index < pages.Count; index++)
            {
                ScreenRecord screen = ledger.Screens[index];
                pages[index].Caption.Add("Screen " + screen.ScreenId + "   page " + (index + 1) + " of " + pages.Count +
                    "   scan " + (screen.ScanId ?? "-"));
                pages[index].Caption.Add("window " + (screen.Hwnd == 0 ? "-" : "0x" + screen.Hwnd.ToString("X")) +
                    "   size " + screen.Size + "   components " + screen.ComponentIds.Count +
                    "   class " + (screen.ClassName ?? "-"));
                pages[index].Caption.Add("title: " + ScreenText.Ascii(screen.Title ?? "-") +
                    "   (non Latin characters shown as ? here; the text attachment has them intact)");
                if (!screen.HasShot && !String.IsNullOrEmpty(screen.ShotProblem))
                {
                    pages[index].Caption.Add("reason: " + screen.ShotProblem);
                }
            }
            try
            {
                PdfDocument.Write(path, pages.ToArray());
                bundle.PdfPath = path;
                bundle.PageCount = pages.Count;
                Measure(path, out bundle.PdfBytes, out bundle.PdfSha256);
            }
            catch (Exception exception)
            {
                bundle.Problems.Add("The picture document could not be written: " + exception.GetType().Name + ": " + exception.Message);
                for (int index = 0; index < ledger.Screens.Count; index++) ledger.Screens[index].PdfPage = 0;
            }
        }

        private static string BuildText(CaseRecord record, RequestBundle request, ScreenLedger ledger, HandoffBundle bundle, string goal)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# " + Messages.Text("handoff-title.txt", "Investigation handed to the assistant"));
            text.AppendLine();
            text.AppendLine("- bundleId: " + bundle.BundleId);
            text.AppendLine("- " + Messages.Text("handoff-created.txt", "made at") + ": " +
                bundle.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("- " + CaseText.Target + ": " + Flat(record == null ? null : record.TargetProcess) + " / " +
                Flat(record == null ? null : record.TargetTitle) + " (pid " + (record == null ? 0 : record.TargetProcessId) + ")");
            text.AppendLine("- " + Messages.Text("handoff-case.txt", "case") + ": " + Flat(record == null ? null : record.CaseId));
            text.AppendLine("- scanId: " + Flat(bundle.ScanId));
            text.AppendLine("- premiseHash: " + Flat(bundle.PremiseHash));
            text.AppendLine("- " + CaseText.Goal + ": " + Flat(String.IsNullOrWhiteSpace(goal) ? null : goal));
            text.AppendLine();
            text.AppendLine("## " + Messages.Text("handoff-attachments.txt", "The two attachments"));
            text.AppendLine();
            text.Append(Attachments(bundle));
            text.AppendLine();
            text.AppendLine(Messages.Text("handoff-ids.txt",
                "Screen ids are S1, S2, ... and component ids are E0, E1, ... Every component belongs to exactly one screen. Quote a component by its id; do not invent ids."));
            text.AppendLine();

            text.AppendLine("## " + Messages.Text("handoff-screens.txt", "Screens"));
            text.AppendLine();
            if (ledger == null || ledger.Screens.Count == 0)
            {
                text.AppendLine(Messages.Text("handoff-noscreens.txt", "No screen was recorded for this case."));
                text.AppendLine();
            }
            else
            {
                text.AppendLine("| Screen ID | " + Messages.Text("handoff-col-page.txt", "page in " + PdfFileName) + " | " +
                    Messages.Text("handoff-col-title.txt", "window title") + " | class | HWND | " +
                    Messages.Text("request-col-rect.txt", "position") + " | " +
                    Messages.Text("handoff-col-components.txt", "components") + " | " +
                    Messages.Text("handoff-col-picture.txt", "picture") + " |");
                text.AppendLine("|---|---|---|---|---|---|---|---|");
                for (int index = 0; index < ledger.Screens.Count; index++)
                {
                    ScreenRecord screen = ledger.Screens[index];
                    string picture = screen.HasShot
                        ? screen.CaptureMethod + " " + (screen.CapturedAt.HasValue
                            ? screen.CapturedAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-")
                        : Messages.Text("handoff-nopicture.txt", "none") + ": " + (screen.ShotProblem ?? "-");
                    text.AppendLine("| " + screen.ScreenId +
                        " | " + (screen.PdfPage == 0 ? Messages.Text("handoff-nopage.txt", "not in the document") : screen.PdfPage.ToString(CultureInfo.InvariantCulture)) +
                        " | " + Cell(screen.Title) +
                        " | " + Cell(screen.ClassName) +
                        " | " + (screen.Hwnd == 0 ? "-" : "0x" + screen.Hwnd.ToString("X")) +
                        " | " + (screen.Rect == null ? "-" : screen.Rect.X + "," + screen.Rect.Y + " " + screen.Rect.Width + "x" + screen.Rect.Height) +
                        " | " + screen.ComponentIds.Count +
                        " | " + Cell(picture) + " |");
                }
                text.AppendLine();
                for (int index = 0; index < ledger.Screens.Count; index++)
                {
                    ScreenRecord screen = ledger.Screens[index];
                    if (String.IsNullOrEmpty(screen.Note)) continue;
                    text.AppendLine(screen.ScreenId + ": " + screen.Note);
                    text.AppendLine();
                }
            }

            // The body without its own heading block: the target, the case and
            // the folders are already stated above, and saying them twice in one
            // file invites the reader to wonder which one is current.
            if (request != null && !String.IsNullOrEmpty(request.InvestigationBody))
            {
                text.Append(request.InvestigationBody);
                if (!request.InvestigationBody.EndsWith("\n", StringComparison.Ordinal)) text.AppendLine();
            }
            text.AppendLine();
            text.AppendLine("## " + Messages.Text("handoff-where.txt", "Where this came from"));
            text.AppendLine();
            text.AppendLine("- " + CaseText.SessionFolder + ": " + Flat(record == null ? null : record.SessionFolder));
            text.AppendLine("- " + Messages.Text("handoff-casefolder.txt", "case folder") + ": " + Flat(record == null ? null : record.Folder));
            return text.ToString();
        }

        // The list of what is really attached. It is written from the files that
        // were actually produced, so a request never names a document that was
        // not made, and never leaves its absence to be noticed.
        public static string Attachments(HandoffBundle bundle)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("- " + TextFileName + " - " + Messages.Text("handoff-att-text.txt", "this file. Every fact the tool collected."));
            if (bundle != null && bundle.PdfPath != null)
            {
                text.AppendLine("- " + PdfFileName + " - " + Messages.Text("handoff-att-pdf.txt", "one page per screen, in the order of the table below.") +
                    " (" + bundle.PageCount + " " + Messages.Text("handoff-pages.txt", "pages") + ")");
            }
            else
            {
                text.AppendLine("- " + (bundle == null || bundle.NoPictureReason == null
                    ? Messages.Text("handoff-nopdf.txt", "No picture of the target could be taken, so only the text attachment was made.")
                    : bundle.NoPictureReason));
            }
            return text.ToString();
        }

        // What the assistant is allowed to assume: which screens exist, and
        // where each addressable component sits. Anything that would change how
        // a step resolves belongs in here.
        public static string PremiseHash(ScreenLedger ledger, CaseElementTable table)
        {
            StringBuilder text = new StringBuilder();
            text.Append("scan=").Append(ledger == null ? "-" : (ledger.ScanId ?? "-")).Append('\n');
            if (ledger != null)
            {
                for (int index = 0; index < ledger.Screens.Count; index++)
                {
                    ScreenRecord screen = ledger.Screens[index];
                    text.Append("screen=").Append(screen.ScreenId).Append('|').Append(screen.Hwnd).Append('|')
                        .Append(screen.Rect == null ? "-" : screen.Rect.X + "," + screen.Rect.Y + "," + screen.Rect.Width + "," + screen.Rect.Height)
                        .Append('|').Append(screen.ComponentIds.Count).Append('\n');
                }
            }
            if (table != null)
            {
                ScanNode[] listed = table.Listed;
                for (int index = 0; index < listed.Length; index++)
                {
                    ScanNode node = listed[index];
                    text.Append("component=").Append(CaseElementTable.IdOf(node)).Append('|')
                        .Append(node.ScreenId ?? "-").Append('|')
                        .Append(node.Name ?? "-").Append('|')
                        .Append(node.AutomationId ?? "-").Append('|')
                        .Append(node.ControlType ?? "-").Append('|')
                        .Append(node.Rect == null ? "-" : node.Rect.X + "," + node.Rect.Y + "," + node.Rect.Width + "," + node.Rect.Height)
                        .Append('\n');
                }
            }
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false).GetBytes(text.ToString()))).Replace("-", String.Empty);
            }
        }

        public static string HashText(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(new UTF8Encoding(false).GetBytes(value ?? String.Empty))).Replace("-", String.Empty);
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content ?? String.Empty, new UTF8Encoding(false));
            File.Copy(temporary, path, true);
            File.Delete(temporary);
        }

        private static void Measure(string path, out long bytes, out string sha256)
        {
            bytes = 0;
            sha256 = null;
            if (!File.Exists(path)) return;
            bytes = new FileInfo(path).Length;
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", String.Empty);
            }
        }

        private static string Flat(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static string Cell(string value)
        {
            if (String.IsNullOrEmpty(value)) return "-";
            string flat = value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 80 ? flat : flat.Substring(0, 79) + "...";
        }
    }
}
