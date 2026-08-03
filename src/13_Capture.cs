namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;

    public sealed class MaskRect
    {
        public RectValue Rect;
        public string RuleId;
    }

    public sealed class ShotResult
    {
        public string ShotId;
        public string Kind;
        public string File;
        public string Sha256;
        public long Bytes;
        public string CaptureMethod;
        public RectValue Rect;
        public MaskRect[] MaskedRects;
        public ProbeStatus Status;
        public string Warning;
        public DateTimeOffset At;
        public string MonitorId;
    }

    public static class Capture
    {
        private const uint PW_RENDERFULLCONTENT = 2;

        public static ShotResult Crop(RectValue rectangle, MaskRect[] masks, string path, IntPtr sourceWindow)
        {
            return CaptureRectangle(rectangle, masks, path, sourceWindow, true);
        }

        public static ShotResult Full(string path, bool explicitlyRequested)
        {
            if (!explicitlyRequested)
            {
                ShotResult blocked = new ShotResult();
                blocked.Status = ProbeStatus.Unavailable("CAP-EXPLICIT", "A full-screen capture requires an explicit user action.");
                blocked.Warning = "Full-screen captures cannot be masked automatically and can contain business data.";
                return blocked;
            }
            System.Drawing.Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
            RectValue rectangle = new RectValue();
            rectangle.X = virtualScreen.Left;
            rectangle.Y = virtualScreen.Top;
            rectangle.Width = virtualScreen.Width;
            rectangle.Height = virtualScreen.Height;
            ShotResult result = CaptureRectangle(rectangle, new MaskRect[0], path, IntPtr.Zero, false);
            result.Warning = "Full-screen captures cannot be masked automatically and can contain business data.";
            return result;
        }

        public static ShotResult AddMasks(ShotResult shot, MaskRect[] additionalMasks)
        {
            if (shot == null || String.IsNullOrWhiteSpace(shot.File) || !File.Exists(shot.File)) throw new ArgumentException("The source shot is unavailable.", "shot");
            List<MaskRect> masks = new List<MaskRect>();
            if (shot.MaskedRects != null) masks.AddRange(shot.MaskedRects);
            if (additionalMasks != null) masks.AddRange(additionalMasks);
            string temporary = shot.File + ".remask.tmp.png";
            using (Bitmap source = new Bitmap(shot.File))
            using (Bitmap bitmap = new Bitmap(source))
            {
                ApplyMasks(bitmap, shot.Rect, additionalMasks);
                bitmap.Save(temporary, ImageFormat.Png);
            }
            File.Copy(temporary, shot.File, true);
            File.Delete(temporary);
            shot.MaskedRects = masks.ToArray();
            RefreshFileMetadata(shot);
            return shot;
        }

        private static ShotResult CaptureRectangle(RectValue rectangle, MaskRect[] masks, string path, IntPtr sourceWindow, bool allowPrintWindow)
        {
            if (rectangle == null || rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                ShotResult invalid = new ShotResult();
                invalid.Status = ProbeStatus.Unavailable("CAP-OFFSCREEN", "The requested capture rectangle is empty.");
                return invalid;
            }
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            Bitmap bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
            string method = "BitBlt";
            ProbeStatus status = ProbeStatus.Ok();
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(rectangle.X, rectangle.Y, 0, 0, new Size(rectangle.Width, rectangle.Height), CopyPixelOperation.SourceCopy);
                }
                if (allowPrintWindow && sourceWindow != IntPtr.Zero && IsBlack(bitmap))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try
                        {
                            if (PrintWindow(sourceWindow, dc, PW_RENDERFULLCONTENT))
                            {
                                method = "PrintWindow";
                            }
                            else
                            {
                                status.AddPartial("CAP-BLACK", "BitBlt was black and PrintWindow did not return content.");
                            }
                        }
                        finally
                        {
                            graphics.ReleaseHdc(dc);
                        }
                    }
                }
                ApplyMasks(bitmap, rectangle, masks);
                bitmap.Save(fullPath, ImageFormat.Png);
            }
            catch (Exception exception)
            {
                status = ProbeStatus.Unavailable("CAP-BLACK", exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                bitmap.Dispose();
            }
            ShotResult result = new ShotResult();
            result.File = fullPath;
            result.CaptureMethod = method;
            result.Rect = rectangle;
            result.MaskedRects = masks ?? new MaskRect[0];
            result.Status = status;
            result.At = DateTimeOffset.Now;
            result.MonitorId = DpiTools.MonitorIdAt(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
            RefreshFileMetadata(result);
            return result;
        }

        private static void RefreshFileMetadata(ShotResult result)
        {
            if (!File.Exists(result.File)) return;
            FileInfo file = new FileInfo(result.File);
            result.Bytes = file.Length;
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(result.File))
            {
                result.Sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", String.Empty);
            }
        }

        private static void ApplyMasks(Bitmap bitmap, RectValue capture, MaskRect[] masks)
        {
            if (masks == null || masks.Length == 0) return;
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Brush brush = new SolidBrush(Color.Black))
            {
                for (int index = 0; index < masks.Length; index++)
                {
                    if (masks[index] == null || masks[index].Rect == null) continue;
                    int x = masks[index].Rect.X - capture.X;
                    int y = masks[index].Rect.Y - capture.Y;
                    graphics.FillRectangle(brush, x, y, masks[index].Rect.Width, masks[index].Rect.Height);
                }
            }
        }

        private static bool IsBlack(Bitmap bitmap)
        {
            int tested = 0;
            int black = 0;
            int stepX = Math.Max(1, bitmap.Width / 12);
            int stepY = Math.Max(1, bitmap.Height / 12);
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    Color color = bitmap.GetPixel(x, y);
                    tested++;
                    if (color.R < 4 && color.G < 4 && color.B < 4) black++;
                }
            }
            return tested > 0 && black == tested;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    }
}
