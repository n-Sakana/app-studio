namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    public sealed class ScreensPdfResult
    {
        public string Path;
        public long Bytes;
        public int PageCount;
        public int BudgetBytes;
        public string Quality;
        public bool Written;
        public string Problem;
        public List<string> OmittedScreens = new List<string>();
        public List<string> Notes = new List<string>();

        public string SizeText
        {
            get { return (Bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB"; }
        }
    }

    // The picture attachment. One screen per page, each page naming the screen
    // it shows so a reader can put a picture and a list of parts side by side.
    //
    // An assistant chat has a size limit, so this file has a stated budget. When
    // the pictures do not fit, they are reduced in defined steps and, only if
    // that is still not enough, screens are left out - and every reduction and
    // every omission is written into the document, into session.md and into the
    // report. Nothing shrinks quietly.
    public static class ScreensPdf
    {
        public const int DefaultBudgetBytes = 6 * 1024 * 1024;

        private sealed class Step
        {
            public int MaxPixels;
            public int Quality;
        }

        private static Step[] Ladder()
        {
            List<Step> steps = new List<Step>();
            steps.Add(Make(1800, 0));
            steps.Add(Make(1600, 85));
            steps.Add(Make(1400, 78));
            steps.Add(Make(1100, 70));
            steps.Add(Make(900, 60));
            return steps.ToArray();
        }

        private static Step Make(int pixels, int quality)
        {
            Step step = new Step();
            step.MaxPixels = pixels;
            step.Quality = quality;
            return step;
        }

        public static ScreensPdfResult Write(StudioSession session, string path, int budgetBytes)
        {
            ScreensPdfResult result = new ScreensPdfResult();
            result.Path = path;
            result.BudgetBytes = budgetBytes <= 0 ? DefaultBudgetBytes : budgetBytes;
            // Page numbers from an earlier build are wrong until this one has
            // decided which screens it keeps, so none is carried over.
            if (session != null)
            {
                for (int index = 0; index < session.Screens.Screens.Count; index++) session.Screens.Screens[index].PdfPage = 0;
            }
            List<ScreenRecord> ordered = Ordered(session);
            if (ordered.Count == 0)
            {
                result.Problem = "PDF-NOSCREEN: this session has no screen with a picture, so no picture document was written.";
                return result;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            }
            catch (Exception exception)
            {
                result.Problem = "PDF-FOLDER: " + exception.GetType().Name + ": " + exception.Message;
                return result;
            }

            List<ScreenRecord> kept = new List<ScreenRecord>(ordered);
            Step[] ladder = Ladder();
            int ladderIndex = 0;
            bool ladderDone = false;
            // The smallest form seen so far, and the settings that produced it.
            // A reduction step is not assumed to help: a screenshot of an
            // ordinary window is mostly flat colour, which the lossless form
            // stores better than a photographic one, so a "reduction" can make
            // the file larger. Degrading the pictures and then dropping screens
            // because of a step that made things worse would be a loss with
            // nothing bought, so the best form is kept and the fact is stated.
            PdfOptions best = null;
            long bestBytes = Int64.MaxValue;
            int guard = 0;
            while (guard < 64)
            {
                guard++;
                Step step = ladder[Math.Min(ladderIndex, ladder.Length - 1)];
                PdfOptions options = ladderDone && best != null
                    ? best
                    : (step.Quality <= 0 ? PdfOptions.Lossless(step.MaxPixels) : PdfOptions.Compressed(step.MaxPixels, step.Quality));
                PdfPage[] pages = BuildPages(session, kept, options);
                try
                {
                    PdfDocument.Write(path, pages, options);
                }
                catch (Exception exception)
                {
                    result.Problem = "PDF-WRITE: " + exception.GetType().Name + ": " + exception.Message;
                    return result;
                }
                long bytes = new FileInfo(path).Length;
                if (!ladderDone)
                {
                    if (bytes < bestBytes)
                    {
                        bestBytes = bytes;
                        best = options;
                    }
                    else
                    {
                        result.Notes.Add("Reducing the pictures to " + options.Describe() + " produced a larger file (" +
                            (bytes / 1024) + " KB against " + (bestBytes / 1024) + " KB), because these screens are mostly flat colour. " +
                            "The smaller form was kept.");
                    }
                }
                if (bytes <= result.BudgetBytes)
                {
                    return Accept(result, kept, pages.Length, bytes, options);
                }
                if (!ladderDone && ladderIndex < ladder.Length - 1)
                {
                    ladderIndex++;
                    result.Notes.Add("The document was " + (bytes / 1024) + " KB against a " + (result.BudgetBytes / 1024) +
                        " KB budget, so the pictures were tried at " +
                        (ladder[ladderIndex].Quality <= 0
                            ? ladder[ladderIndex].MaxPixels + " px lossless"
                            : ladder[ladderIndex].MaxPixels + " px at quality " + ladder[ladderIndex].Quality) + ".");
                    continue;
                }
                if (!ladderDone)
                {
                    // Every step has been tried. Go back to whichever produced
                    // the smallest file and start leaving screens out from
                    // there.
                    ladderDone = true;
                    if (best != null && bytes > bestBytes) continue;
                }
                if (kept.Count <= 1)
                {
                    result.Notes.Add("The document is " + (bytes / 1024) + " KB, above the " + (result.BudgetBytes / 1024) +
                        " KB budget. Every reduction was already tried and one page is the minimum, so it was written as it is rather than emptied.");
                    return Accept(result, kept, pages.Length, bytes, options);
                }
                // A screen has to go. The one that no recorded action points at
                // is dropped first, and which one it was is written down.
                ScreenRecord victim = Sacrifice(session, kept);
                if (victim == null) victim = kept[kept.Count - 1];
                kept.Remove(victim);
                result.OmittedScreens.Add(victim.ScreenId);
                result.Notes.Add("Screen " + victim.ScreenId + " (" + Short(victim.Title) +
                    ") was left out of the picture document to stay inside the " + (result.BudgetBytes / 1024) +
                    " KB budget. Its row is still in the ledger and its original picture is in the session folder.");
            }
            result.Problem = "PDF-BUDGET: the budget could not be met after 64 attempts.";
            return result;
        }

        private static ScreensPdfResult Accept(ScreensPdfResult result, List<ScreenRecord> kept, int pageCount, long bytes, PdfOptions options)
        {
            result.Written = true;
            result.Bytes = bytes;
            result.PageCount = pageCount;
            result.Quality = options.Describe();
            for (int index = 0; index < kept.Count; index++) kept[index].PdfPage = index + 1;
            return result;
        }

        private static List<ScreenRecord> Ordered(StudioSession session)
        {
            List<ScreenRecord> ordered = new List<ScreenRecord>();
            if (session == null) return ordered;
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                if (session.Screens.Screens[index].HasShot) ordered.Add(session.Screens.Screens[index]);
            }
            return ordered;
        }

        // The least useful page: one that no step names, taken from the middle
        // of the run so the beginning and the end of the procedure survive.
        private static ScreenRecord Sacrifice(StudioSession session, List<ScreenRecord> kept)
        {
            if (kept.Count <= 2) return null;
            Dictionary<string, bool> referenced = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int index = 0; index < session.Steps.Count; index++)
            {
                if (!String.IsNullOrEmpty(session.Steps[index].ScreenBefore)) referenced[session.Steps[index].ScreenBefore] = true;
                if (!String.IsNullOrEmpty(session.Steps[index].ScreenAfter)) referenced[session.Steps[index].ScreenAfter] = true;
            }
            for (int index = kept.Count - 2; index >= 1; index--)
            {
                if (!referenced.ContainsKey(kept[index].ScreenId)) return kept[index];
            }
            return kept[kept.Count / 2];
        }

        private static PdfPage[] BuildPages(StudioSession session, List<ScreenRecord> screens, PdfOptions options)
        {
            List<PdfPage> pages = new List<PdfPage>();
            for (int index = 0; index < screens.Count; index++)
            {
                ScreenRecord screen = screens[index];
                PdfPage page = new PdfPage();
                page.ImagePath = screen.ShotFile;
                page.Caption.Add("Screen " + screen.ScreenId + "   page " + (index + 1) + " of " + screens.Count +
                    "   session " + (session.Id == null ? "?" : session.Id));
                page.Caption.Add("window: " + ScreenText.Ascii(Short(screen.Title)) + "   class: " + ScreenText.Ascii(screen.ClassName == null ? "?" : screen.ClassName));
                page.Caption.Add("size: " + screen.Size + "   parts listed: " + screen.ComponentIds.Count +
                    "   captured: " + (screen.CapturedAt.HasValue ? screen.CapturedAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-"));
                string actions = ActionsFor(session, screen.ScreenId);
                if (actions != null) page.Caption.Add("actions: " + actions);
                if (!String.IsNullOrEmpty(screen.Note)) page.Caption.Add("note: " + ScreenText.Ascii(screen.Note));
                page.Caption.Add("non-ASCII text is replaced by ? on this page; the original wording is in session.md");
                pages.Add(page);
            }
            return pages.ToArray();
        }

        private static string ActionsFor(StudioSession session, string screenId)
        {
            List<string> ids = new List<string>();
            for (int index = 0; index < session.Steps.Count; index++)
            {
                StepRecord step = session.Steps[index];
                if (String.Equals(step.ScreenBefore, screenId, StringComparison.Ordinal) ||
                    String.Equals(step.ScreenAfter, screenId, StringComparison.Ordinal)) ids.Add(step.StepId);
                if (ids.Count >= 14) break;
            }
            if (ids.Count == 0) return null;
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int index = 0; index < ids.Count; index++)
            {
                if (index != 0) text.Append(' ');
                text.Append(ids[index]);
            }
            return text.ToString();
        }

        private static string Short(string value)
        {
            if (String.IsNullOrEmpty(value)) return "(no title)";
            return value.Length <= 70 ? value : value.Substring(0, 69) + "...";
        }
    }
}
