namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    // Turns a window into the record of what it is made of. Both the chooser
    // and the recorder come through here, so the same window is described the
    // same way whichever of the two asked for it.
    public static class Acquire
    {
        public interface ISurfaceGuard
        {
            // Takes everything this product draws off the screen and puts it
            // back afterwards, so a picture only ever contains the target.
            void Suppress(bool value);

            // Whether that actually happened. Asking to hide a window is not the
            // same fact as the window being gone, and the difference is a frame
            // of this product's own drawing inside somebody's evidence.
            bool Hidden { get; }
        }

        public sealed class NullGuard : ISurfaceGuard
        {
            public void Suppress(bool value)
            {
            }

            public bool Hidden { get { return true; } }
        }

        // Called after suppressing and before the shutter. A guard that could not
        // get out of the way does not stop the picture - it annotates it, because
        // a stated contamination is worth more than a missing screen.
        public static void CheckSuppressed(ScreenRecord screen, ISurfaceGuard guard)
        {
            if (screen == null || guard == null || guard.Hidden) return;
            string note = "this product's own recording frame could not be taken off the screen before this picture, so it may appear in it";
            screen.Note = String.IsNullOrEmpty(screen.Note) ? note : screen.Note + "; " + note;
        }

        // Acquires one window and folds the result into the session. The scan
        // itself runs in a separate process, so an application that stops
        // answering cannot take this one down with it.
        public static ScanResult Window(ScanRunner runner, StudioSession session, TargetWindowInfo target, ScanLimits limits, Action<ScanProgress> progress)
        {
            if (runner == null || session == null || target == null) return null;
            session.Register(target.ProcessId);
            ScanResult result = runner.RunWindows(new TargetWindowInfo[] { target }, target.ProcessId, limits, progress);
            Fold(session, result);
            return result;
        }

        // Adds one scan to the session: its screens, its elements, its coverage
        // and everything it could not obtain. Ids are made unique across the
        // whole session, because a recording acquires many windows and two of
        // them would otherwise both be called S1.
        public static void Fold(StudioSession session, ScanResult result)
        {
            if (session == null || result == null) return;
            ScreenLedger ledger = ScreenLedger.FromScan(result);
            for (int index = 0; index < ledger.Screens.Count; index++)
            {
                ScreenRecord screen = ledger.Screens[index];
                string original = screen.ScreenId;
                screen.ScreenId = "S" + (session.Screens.Screens.Count + 1).ToString(CultureInfo.InvariantCulture);
                for (int node = 0; node < result.Nodes.Count; node++)
                {
                    if (String.Equals(result.Nodes[node].ScreenId, original, StringComparison.Ordinal)) result.Nodes[node].ScreenId = screen.ScreenId;
                }
                session.Screens.Screens.Add(screen);
            }
            int offset = session.Elements.Count;
            for (int index = 0; index < result.Nodes.Count; index++)
            {
                ScanNode node = result.Nodes[index];
                node.NodeId = offset + index;
                session.Elements.Add(node);
            }
            // Component ids are handed out only once the node ids are final.
            for (int index = 0; index < ledger.Screens.Count; index++) ledger.Screens[index].ComponentIds.Clear();
            for (int index = 0; index < result.Nodes.Count; index++)
            {
                ScreenRecord screen = session.Screens.Find(result.Nodes[index].ScreenId);
                if (screen != null) screen.ComponentIds.Add("E" + result.Nodes[index].NodeId.ToString(CultureInfo.InvariantCulture));
            }
            for (int index = 0; index < result.Coverage.Count; index++) session.Coverage.Add(result.Coverage[index]);
            for (int index = 0; index < result.Unknowns.Count; index++) session.AddLimit(result.Unknowns[index]);
            for (int index = 0; index < result.Coverage.Count; index++)
            {
                ScanCoverage coverage = result.Coverage[index];
                for (int reason = 0; reason < coverage.Reasons.Count; reason++)
                {
                    session.AddLimit("[" + coverage.Provider + "] " + coverage.Reasons[reason].Code + ": " + coverage.Reasons[reason].Message);
                }
            }
            if (result.Cancelled) session.AddLimit("The acquisition was stopped before it finished, so this list is partial.");
            for (int index = 0; index < result.Nodes.Count; index++) SessionStore.Append(session, "elements", ScanJson.Node(result.Nodes[index], result.ScanId, 0));
        }

        // Takes the picture that belongs to one screen. Every surface this
        // product owns is off the display for the duration, and the target is
        // raised first because a covered window photographs as whatever covers
        // it.
        public static void Shoot(StudioSession session, ScreenRecord screen, ISurfaceGuard guard, int settleMs)
        {
            if (session == null || screen == null) return;
            if (guard == null) guard = new NullGuard();
            MaskRect[] masks = SecretMasks(session, screen);
            guard.Suppress(true);
            try
            {
                if (screen.Hwnd != 0)
                {
                    ScreenCapture.Raise(screen.Hwnd);
                    System.Threading.Thread.Sleep(Math.Max(60, settleMs));
                }
                CheckSuppressed(screen, guard);
                ScreenCapture.Shoot(screen, session.ShotsFolder, masks);
            }
            finally
            {
                guard.Suppress(false);
            }
            NoteMasking(screen, masks);
            if (!String.IsNullOrEmpty(screen.ShotProblem)) session.AddLimit("Screen " + screen.ScreenId + " has no picture: " + screen.ShotProblem);
            SessionStore.Append(session, "screens", screen.ToJson());
        }

        // A picture taken during a recording, tied to the step that follows it.
        // It is a screen of the session like any other, so it appears in the
        // ledger, in the document and in the report under the same id.
        public static ScreenRecord Keyframe(StudioSession session, TargetWindowInfo window, string note, ISurfaceGuard guard)
        {
            if (session == null || window == null) return null;
            ScreenRecord screen = new ScreenRecord();
            screen.ScanId = "kf";
            screen.ScreenId = "S" + (session.Screens.Screens.Count + 1).ToString(CultureInfo.InvariantCulture);
            screen.Hwnd = window.Hwnd;
            screen.Title = window.Title;
            screen.ClassName = window.ClassName;
            screen.Rect = window.Rect;
            screen.Note = note;
            session.Screens.Screens.Add(screen);
            if (guard == null) guard = new NullGuard();
            MaskRect[] masks = SecretMasks(session, screen);
            guard.Suppress(true);
            try
            {
                // The window is already in front during a recording; raising it
                // again would itself be an action on the target.
                CheckSuppressed(screen, guard);
                ScreenCapture.Shoot(screen, session.ShotsFolder, masks);
            }
            finally
            {
                guard.Suppress(false);
            }
            NoteMasking(screen, masks);
            if (!String.IsNullOrEmpty(screen.ShotProblem)) session.AddLimit("Keyframe " + screen.ScreenId + " has no picture: " + screen.ShotProblem);
            SessionStore.Append(session, "screens", screen.ToJson());
            return screen;
        }

        // The rectangles that get blacked out before a picture is written. A
        // password box normally shows dots, but an application with a "show the
        // characters" switch, or a field holding a key rather than a password,
        // shows them in full - and a picture of that would carry the secret out
        // of here even though the value itself was never written down.
        public static MaskRect[] SecretMasks(StudioSession session, ScreenRecord screen)
        {
            List<MaskRect> masks = new List<MaskRect>();
            if (session == null || screen == null) return masks.ToArray();
            for (int index = 0; index < session.Elements.Count; index++)
            {
                ScanNode node = session.Elements[index];
                if (!String.Equals(node.ScreenId, screen.ScreenId, StringComparison.Ordinal)) continue;
                if (node.Rect == null || node.Rect.Width <= 0 || node.Rect.Height <= 0) continue;
                bool secret = Privacy.IsSecretElement(node) || Privacy.HasPasswordStyle(node.Style, node.ClassName);
                if (!secret) continue;
                MaskRect mask = new MaskRect();
                mask.Rect = node.Rect;
                mask.RuleId = Privacy.SecretRuleFor(node) ?? Privacy.SecretRuleIsPassword;
                masks.Add(mask);
            }
            return masks.ToArray();
        }

        // Whether anything was blacked out is part of what the picture is, so it
        // is written next to the picture rather than left to be guessed.
        public static void NoteMasking(ScreenRecord screen, MaskRect[] masks)
        {
            if (screen == null || !screen.HasShot) return;
            string statement = masks == null || masks.Length == 0
                ? "no secret field was found on this screen, so nothing was blacked out"
                : masks.Length.ToString(CultureInfo.InvariantCulture) + " secret field(s) were blacked out in this picture";
            screen.Note = String.IsNullOrEmpty(screen.Note) ? statement : screen.Note + "; " + statement;
        }

        // Everything acquired for one window, used both as the material for
        // locators and as the candidate list when a step is resolved again.
        public static List<ScanNode> NodesForScreen(StudioSession session, string screenId)
        {
            List<ScanNode> nodes = new List<ScanNode>();
            if (session == null || String.IsNullOrEmpty(screenId)) return nodes;
            for (int index = 0; index < session.Elements.Count; index++)
            {
                if (String.Equals(session.Elements[index].ScreenId, screenId, StringComparison.Ordinal)) nodes.Add(session.Elements[index]);
            }
            return nodes;
        }

        // The element of a freshly acquired list that sits under one point. The
        // smallest containing rectangle wins, which is the element a person
        // would say they clicked on.
        public static ScanNode NodeAt(List<ScanNode> nodes, int x, int y)
        {
            ScanNode best = null;
            long bestArea = Int64.MaxValue;
            if (nodes == null) return null;
            for (int index = 0; index < nodes.Count; index++)
            {
                RectValue rect = nodes[index].Rect;
                if (rect == null || rect.Width <= 0 || rect.Height <= 0) continue;
                if (x < rect.X || y < rect.Y || x >= rect.X + rect.Width || y >= rect.Y + rect.Height) continue;
                long area = (long)rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = nodes[index];
                }
            }
            return best;
        }

        // True when nothing inside any window could be obtained: every screen
        // came back with at most the window itself. This is the honest answer
        // for an application that paints its own surface, and both outputs have
        // to say it out loud - a table with one row in it otherwise reads like
        // a complete description of a window that has one control.
        public static bool PublishesNoStructure(StudioSession session)
        {
            if (session == null || session.Screens.Screens.Count == 0) return false;
            for (int index = 0; index < session.Screens.Screens.Count; index++)
            {
                if (session.Screens.Screens[index].ComponentIds.Count > 1) return false;
            }
            return true;
        }

        public static bool LooksEditable(ScanNode node)
        {
            if (node == null) return false;
            if (node.Patterns != null)
            {
                for (int index = 0; index < node.Patterns.Length; index++)
                {
                    if (node.Patterns[index] != null && node.Patterns[index].IndexOf("Value", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            if (String.Equals(node.ControlType, "Edit", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(node.ControlType, "Document", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(node.ControlType, "ComboBox", StringComparison.OrdinalIgnoreCase)) return true;
            if (!String.IsNullOrEmpty(node.ClassName) && node.ClassName.IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!String.IsNullOrEmpty(node.ClassName) && node.ClassName.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static string ShotRelativePath(StudioSession session, ScreenRecord screen)
        {
            if (session == null || screen == null || String.IsNullOrEmpty(screen.ShotFile)) return null;
            try
            {
                return "shots/" + Path.GetFileName(screen.ShotFile);
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class OutputSet
    {
        public ScreensPdfResult Pdf;
        public SessionMdResult Markdown;
        public ReportResult Report;

        public bool Complete
        {
            get { return Pdf != null && Pdf.Written && Markdown != null && Markdown.Written && Report != null && Report.Written; }
        }

        public string Problems
        {
            get
            {
                List<string> parts = new List<string>();
                if (Pdf != null && !Pdf.Written && Pdf.Problem != null) parts.Add("screens.pdf: " + Pdf.Problem);
                if (Markdown != null && !Markdown.Written && Markdown.Problem != null) parts.Add("session.md: " + Markdown.Problem);
                if (Report != null && !Report.Written && Report.Problem != null) parts.Add("report.html: " + Report.Problem);
                if (parts.Count == 0) return null;
                System.Text.StringBuilder text = new System.Text.StringBuilder();
                for (int index = 0; index < parts.Count; index++)
                {
                    if (index != 0) text.Append("  /  ");
                    text.Append(parts[index]);
                }
                return text.ToString();
            }
        }
    }

    // Everything a finished session produces, written in one place so no path
    // and no order is invented anywhere else.
    //
    // The handover to an assistant is exactly two files and they live in their
    // own folder, so "attach what is in ai/" is a complete instruction and
    // cannot accidentally grow a third attachment.
    public static class Outputs
    {
        public static OutputSet WriteAll(StudioSession session, int pdfBudgetBytes)
        {
            return WriteAll(session, pdfBudgetBytes, null);
        }

        // The automation, when there is one, goes into `session.md` with
        // everything else. It is written again whenever the operator asks an
        // assistant, so what is attached is what is on screen: an attachment
        // that describes an older version of the code would have the assistant
        // answering about something nobody has any more.
        public static OutputSet WriteAll(StudioSession session, int pdfBudgetBytes, CodeProject project)
        {
            OutputSet outputs = new OutputSet();
            if (session == null) return outputs;
            session.Omissions.Clear();
            // The picture document is written first because it decides which
            // screen sits on which page, and the other two state those page
            // numbers.
            outputs.Pdf = ScreensPdf.Write(session, session.ScreensPdfPath, pdfBudgetBytes);
            for (int index = 0; index < outputs.Pdf.OmittedScreens.Count; index++)
            {
                session.Omissions.Add("screens.pdf omits " + outputs.Pdf.OmittedScreens[index]);
            }
            for (int index = 0; index < outputs.Pdf.Notes.Count; index++) session.AddLimit(outputs.Pdf.Notes[index]);
            if (!outputs.Pdf.Written && outputs.Pdf.Problem != null) session.AddLimit(outputs.Pdf.Problem);
            outputs.Markdown = SessionMd.Write(session, session.SessionMdPath, outputs.Pdf, project);
            if (!outputs.Markdown.Written && outputs.Markdown.Problem != null) session.AddLimit("session.md: " + outputs.Markdown.Problem);
            outputs.Report = Report.Write(session, session.ReportPath, outputs.Pdf);
            if (!outputs.Report.Written && outputs.Report.Problem != null) session.AddLimit("report.html: " + outputs.Report.Problem);
            for (int index = 0; index < outputs.Report.Notes.Count; index++) session.AddLimit(outputs.Report.Notes[index]);
            SessionStore.WriteMeta(session);
            return outputs;
        }

        // What one request to an assistant actually produced.
        //
        // A null here means "not selected", which is a different thing from a
        // result that says it could not be written. Every place that reports to
        // the operator or to an assistant has to be able to tell those apart.
        public sealed class RequestOutputs
        {
            public ScreensPdfResult Pdf;
            public SessionMdResult Markdown;
            // Files that were in the assistant folder from an earlier request and
            // are not part of this one. They are taken out rather than left
            // there: the folder is what gets attached, so a document nobody
            // selected sitting in it is a document that gets attached.
            public List<string> Removed = new List<string>();
        }

        // The assistant folder, written for one request, from what the operator
        // selected and the code as it is on screen right now.
        //
        // It is written again on every request. An attachment describing an older
        // version of the code, or a selection the operator has since changed,
        // would have an assistant answering about something nobody has any more -
        // so nothing here is reused and nothing is kept because it happens to
        // still be on disk.
        //
        // The session's own report is not touched: it describes the recording,
        // not this request, and rewriting it to match a selection would make the
        // record of the session depend on who was asked what afterwards.
        public static RequestOutputs WriteForRequest(StudioSession session, int pdfBudgetBytes, CodeProject project, AiPicks picks)
        {
            RequestOutputs outputs = new RequestOutputs();
            if (session == null) return outputs;
            if (picks == null) picks = AiPicks.Default();
            session.Omissions.Clear();
            if (picks.Has(AiItems.Pdf))
            {
                outputs.Pdf = ScreensPdf.Write(session, session.ScreensPdfPath, pdfBudgetBytes);
                for (int index = 0; index < outputs.Pdf.OmittedScreens.Count; index++)
                {
                    session.Omissions.Add("screens.pdf omits " + outputs.Pdf.OmittedScreens[index]);
                }
                for (int index = 0; index < outputs.Pdf.Notes.Count; index++) session.AddLimit(outputs.Pdf.Notes[index]);
                if (!outputs.Pdf.Written && outputs.Pdf.Problem != null) session.AddLimit(outputs.Pdf.Problem);
            }
            else
            {
                Remove(session.ScreensPdfPath, outputs);
            }
            if (picks.AnyDocument)
            {
                outputs.Markdown = SessionMd.Write(session, session.SessionMdPath, outputs.Pdf, project, picks);
                if (!outputs.Markdown.Written && outputs.Markdown.Problem != null) session.AddLimit("session.md: " + outputs.Markdown.Problem);
                if (!outputs.Markdown.Written) Remove(session.SessionMdPath, outputs);
            }
            else
            {
                Remove(session.SessionMdPath, outputs);
            }
            SessionStore.WriteMeta(session);
            return outputs;
        }

        private static void Remove(string path, RequestOutputs outputs)
        {
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                outputs.Removed.Add(Path.GetFileName(path));
            }
            catch
            {
                // A file that will not delete is not a reason to refuse to build
                // the request. It is still not named as an attachment, which is
                // what decides whether an assistant is told to read it.
            }
        }

        // Copies the finished files somewhere the operator chose. The assistant
        // folder keeps its own name, so whatever it holds stays identifiable as
        // the set that was handed over.
        public static string CopyTo(StudioSession session, string folder)
        {
            if (session == null || String.IsNullOrEmpty(folder)) return "No folder was given.";
            try
            {
                string target = Path.Combine(folder, "app-studio-" + session.Id);
                int suffix = 2;
                while (Directory.Exists(target))
                {
                    target = Path.Combine(folder, "app-studio-" + session.Id + "_" + suffix.ToString(CultureInfo.InvariantCulture));
                    suffix++;
                    if (suffix > 99) return "Too many folders with that name already exist.";
                }
                Directory.CreateDirectory(Path.Combine(target, "ai"));
                if (File.Exists(session.ReportPath)) File.Copy(session.ReportPath, Path.Combine(target, "report.html"));
                if (File.Exists(session.SessionMdPath)) File.Copy(session.SessionMdPath, Path.Combine(target, "ai", "session.md"));
                if (File.Exists(session.ScreensPdfPath)) File.Copy(session.ScreensPdfPath, Path.Combine(target, "ai", "screens.pdf"));
                return null;
            }
            catch (Exception exception)
            {
                return exception.GetType().Name + ": " + exception.Message;
            }
        }
    }
}
