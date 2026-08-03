namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    // One screen of the target application: the window a scan walked, the
    // picture taken of it at that moment, and the components found inside it.
    // The screen id and the component ids are what the assistant is given, so
    // both ends of the handover point at the same thing.
    public sealed class ScreenRecord
    {
        public string ScreenId;
        public string ScanId;
        public long Hwnd;
        public string Title;
        public string ClassName;
        public RectValue Rect;
        public int NodeCount;
        public List<string> ComponentIds = new List<string>();
        public string ShotFile;
        public string Sha256;
        public string CaptureMethod;
        public DateTimeOffset? CapturedAt;
        // Why there is no picture. Never left blank when ShotFile is null, so a
        // missing page in the attachment is always accounted for.
        public string ShotProblem;
        // Anything a reader has to know about this screen beyond its numbers,
        // such as that it was never scanned and only photographed.
        public string Note;
        // Which page of the attached document shows this screen. 0 until the
        // document has been written, and stated as "not in the document" rather
        // than printed as page zero.
        public int PdfPage;

        public bool HasShot { get { return !String.IsNullOrEmpty(ShotFile) && File.Exists(ShotFile); } }

        public string Size { get { return Rect == null ? "-" : Rect.Width + "x" + Rect.Height; } }

        public JsonObject ToJson()
        {
            List<object> components = new List<object>();
            for (int index = 0; index < ComponentIds.Count; index++) components.Add(ComponentIds[index]);
            return new JsonObject()
                .Add("kind", "screen")
                .Add("scanId", ScanId)
                .Add("screenId", ScreenId)
                .Add("hwnd", Hwnd)
                .Add("title", Title)
                .Add("className", ClassName)
                .Add("rect", SessionLogJson.Rect(Rect))
                .Add("nodeCount", NodeCount)
                .Add("componentIds", components.ToArray())
                .Add("shotFile", ShotFile)
                .Add("sha256", Sha256)
                .Add("captureMethod", CaptureMethod)
                .Add("capturedAt", CapturedAt.HasValue ? (object)CapturedAt.Value : null)
                .Add("shotProblem", ShotProblem)
                .Add("note", Note)
                .Add("pdfPage", PdfPage == 0 ? null : (object)PdfPage);
        }
    }

    public sealed class ScreenLedger
    {
        public string ScanId;
        public List<ScreenRecord> Screens = new List<ScreenRecord>();

        public int ShotCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Screens.Count; index++) if (Screens[index].HasShot) count++;
                return count;
            }
        }

        public ScreenRecord Find(string screenId)
        {
            if (String.IsNullOrEmpty(screenId)) return null;
            for (int index = 0; index < Screens.Count; index++)
            {
                if (String.Equals(Screens[index].ScreenId, screenId, StringComparison.OrdinalIgnoreCase)) return Screens[index];
            }
            return null;
        }

        // Built from the scan itself, before any picture is taken, so the ledger
        // exists even when the capture fails and the failure has a row to sit in.
        public static ScreenLedger FromScan(ScanResult result)
        {
            ScreenLedger ledger = new ScreenLedger();
            if (result == null) return ledger;
            ledger.ScanId = result.ScanId;
            for (int index = 0; index < result.Windows.Count; index++)
            {
                ScanWindowResult window = result.Windows[index];
                ScreenRecord screen = new ScreenRecord();
                screen.ScanId = result.ScanId;
                screen.ScreenId = String.IsNullOrEmpty(window.ScreenId)
                    ? "S" + (index + 1).ToString(CultureInfo.InvariantCulture)
                    : window.ScreenId;
                screen.Hwnd = window.Hwnd;
                screen.Title = window.Title;
                screen.ClassName = window.ClassName;
                screen.Rect = window.Rect;
                screen.NodeCount = window.Nodes.Count;
                ledger.Screens.Add(screen);
            }
            // Component ids are the node ids the merge handed out, which are only
            // final once the whole scan is over, so they are read off the merged
            // list rather than off the per window lists.
            for (int index = 0; index < result.Nodes.Count; index++)
            {
                ScanNode node = result.Nodes[index];
                ScreenRecord screen = ledger.Find(node.ScreenId);
                if (screen == null) continue;
                screen.ComponentIds.Add("E" + node.NodeId.ToString(CultureInfo.InvariantCulture));
            }
            return ledger;
        }

        public JsonObject ToJson()
        {
            List<object> items = new List<object>();
            for (int index = 0; index < Screens.Count; index++) items.Add(Screens[index].ToJson());
            return new JsonObject()
                .Add("kind", "screenLedger")
                .Add("scanId", ScanId)
                .Add("screenCount", Screens.Count)
                .Add("shotCount", ShotCount)
                .Add("screens", items.ToArray());
        }

    }

    // Takes the picture that belongs to one screen. The window has to be in
    // front of whatever else is on the desktop for the picture to show it, so
    // raising it is part of the capture and is recorded as such; the caller
    // owns the waiting because only it knows what else is on screen.
    public static class ScreenCapture
    {
        public static string FileNameFor(string scanId, string screenId)
        {
            return "screen-" + (scanId ?? "scan") + "-" + (screenId ?? "S") + ".png";
        }

        public static void Raise(long hwnd)
        {
            if (hwnd == 0) return;
            WindowTools.BringToFront(hwnd);
        }

        // Fills the picture fields of one screen. Everything that goes wrong is
        // written into ShotProblem instead of throwing, because a screen with no
        // picture still has to appear in the ledger and in the attachment.
        public static void Shoot(ScreenRecord screen, string folder)
        {
            Shoot(screen, folder, null);
        }

        // The masks are the rectangles of whatever the session decided is a
        // secret. A password box normally shows dots, but an application with a
        // "show the characters" switch, or a field holding a key rather than a
        // password, shows them in full, and a picture of that would carry it out
        // of here.
        public static void Shoot(ScreenRecord screen, string folder, MaskRect[] masks)
        {
            if (screen == null) return;
            if (screen.Hwnd == 0)
            {
                screen.ShotProblem = "SHOT-NOHWND: this screen has no window handle.";
                return;
            }
            if (String.IsNullOrEmpty(folder))
            {
                screen.ShotProblem = "SHOT-NOFOLDER: there is no folder to write pictures into.";
                return;
            }
            IntPtr handle = new IntPtr(screen.Hwnd);
            RectValue rect = WindowTools.GetPhysicalRect(handle);
            if (rect == null || rect.Width <= 0 || rect.Height <= 0)
            {
                screen.ShotProblem = "SHOT-NORECT: this window has no usable rectangle right now.";
                return;
            }
            screen.Rect = rect;
            string path;
            try
            {
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, FileNameFor(screen.ScanId, screen.ScreenId));
            }
            catch (Exception exception)
            {
                screen.ShotProblem = "SHOT-FOLDER: " + exception.GetType().Name + ": " + exception.Message;
                return;
            }
            ShotResult shot;
            try
            {
                shot = Capture.Crop(rect, masks == null ? new MaskRect[0] : masks, path, handle);
            }
            catch (Exception exception)
            {
                screen.ShotProblem = "SHOT-FAILED: " + exception.GetType().Name + ": " + exception.Message;
                return;
            }
            if (shot == null || shot.Status == null || shot.Status.State != "ok")
            {
                string reason = shot == null || shot.Status == null || shot.Status.Reasons.Count == 0
                    ? "SHOT-FAILED: the capture returned nothing."
                    : shot.Status.Reasons[0].Code + ": " + shot.Status.Reasons[0].Message;
                screen.ShotProblem = reason;
                return;
            }
            screen.ShotFile = shot.File;
            screen.Sha256 = shot.Sha256;
            screen.CaptureMethod = shot.CaptureMethod;
            screen.CapturedAt = shot.At;
            screen.ShotProblem = null;
        }
    }

    public static class ScreenText
    {
        // The document that carries the pictures uses one of the fonts every
        // reader already has, and those fonts only cover Latin text. A title in
        // another script is kept intact in the text attachment and reduced here,
        // with the reduction stated rather than passed off as the title.
        public static string Ascii(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character >= ' ' && character <= '~') builder.Append(character);
                else if (character == '\t') builder.Append(' ');
                else builder.Append('?');
            }
            return builder.ToString();
        }
    }
}
