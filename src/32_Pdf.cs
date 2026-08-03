namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Globalization;
    using System.IO;
    using System.IO.Compression;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class PdfPage
    {
        public string ImagePath;
        // Lines printed above the picture. They are what ties a page to a screen
        // id, so they are written even when the picture itself is missing.
        public List<string> Caption = new List<string>();
    }

    // A picture document with no libraries beyond what the framework already
    // carries. Only the parts of the format these pages need are written: one
    // page per picture, a caption in a font every reader has, and the picture
    // stored losslessly so the attachment is evidence rather than an impression
    // of it.
    public static class PdfDocument
    {
        // A4 at 72 units per inch, which is what the format counts in.
        private const double PageWidth = 595.28;
        private const double PageHeight = 841.89;
        private const double Margin = 36;
        private const double CaptionSize = 9.5;
        private const double CaptionLead = 12.5;
        // Beyond this the picture is scaled down before it is stored. A whole
        // desktop at full size makes a file nobody can send, and the reduction
        // is written into the caption instead of happening silently.
        private const int MaxPixels = 2000;

        public static void Write(string path, PdfPage[] pages)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", "path");
            if (pages == null || pages.Length == 0) throw new ArgumentException("A document needs at least one page.", "pages");
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            List<byte[]> objects = new List<byte[]>();
            // 1 catalog, 2 pages, 3 font. The rest are filled in below.
            objects.Add(null);
            objects.Add(null);
            objects.Add(Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

            List<int> pageObjects = new List<int>();
            for (int index = 0; index < pages.Length; index++)
            {
                PdfPage page = pages[index];
                PdfImage image = LoadImage(page.ImagePath);
                int imageNumber = 0;
                if (image != null)
                {
                    objects.Add(ImageObject(image));
                    imageNumber = objects.Count;
                }
                byte[] content = Latin1(ContentStream(page, image, imageNumber != 0));
                objects.Add(Stream(new StringBuilder(), content));
                int contentNumber = objects.Count;

                StringBuilder resources = new StringBuilder();
                resources.Append("<< /Font << /F1 3 0 R >>");
                if (imageNumber != 0) resources.Append(" /XObject << /Im0 " + imageNumber + " 0 R >>");
                resources.Append(" >>");
                objects.Add(Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " +
                    Number(PageWidth) + " " + Number(PageHeight) + "] /Resources " + resources.ToString() +
                    " /Contents " + contentNumber.ToString(CultureInfo.InvariantCulture) + " 0 R >>"));
                pageObjects.Add(objects.Count);
            }

            StringBuilder kids = new StringBuilder();
            for (int index = 0; index < pageObjects.Count; index++)
            {
                if (index != 0) kids.Append(' ');
                kids.Append(pageObjects[index]).Append(" 0 R");
            }
            objects[0] = Latin1("<< /Type /Catalog /Pages 2 0 R >>");
            objects[1] = Latin1("<< /Type /Pages /Kids [" + kids.ToString() + "] /Count " +
                pageObjects.Count.ToString(CultureInfo.InvariantCulture) + " >>");

            using (FileStream file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                List<long> offsets = new List<long>();
                Append(file, Latin1("%PDF-1.4\n"));
                // A comment of high bytes is what tells anything moving the file
                // that it is not text and must not be line ending converted.
                Append(file, new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
                for (int index = 0; index < objects.Count; index++)
                {
                    offsets.Add(file.Position);
                    Append(file, Latin1((index + 1) + " 0 obj\n"));
                    Append(file, objects[index]);
                    Append(file, Latin1("\nendobj\n"));
                }
                long xref = file.Position;
                StringBuilder table = new StringBuilder();
                table.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
                table.Append("0000000000 65535 f \n");
                for (int index = 0; index < offsets.Count; index++)
                {
                    table.Append(offsets[index].ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
                }
                table.Append("trailer\n").Append("<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
                    .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
                Append(file, Latin1(table.ToString()));
                file.Flush(true);
            }
        }

        private sealed class PdfImage
        {
            public int Width;
            public int Height;
            public byte[] Data;
            public int SourceWidth;
            public int SourceHeight;
            public bool Scaled { get { return Width != SourceWidth || Height != SourceHeight; } }
        }

        private static string ContentStream(PdfPage page, PdfImage image, bool hasImage)
        {
            StringBuilder text = new StringBuilder();
            double top = PageHeight - Margin;
            List<string> caption = new List<string>();
            for (int index = 0; index < page.Caption.Count; index++) caption.Add(ScreenText.Ascii(page.Caption[index]));
            if (image != null && image.Scaled)
            {
                caption.Add("picture stored at " + image.Width + "x" + image.Height +
                    " (reduced from " + image.SourceWidth + "x" + image.SourceHeight + ")");
            }
            if (!hasImage) caption.Add("no picture for this screen - see the text attachment for the reason");
            text.Append("BT /F1 ").Append(Number(CaptionSize)).Append(" Tf ").Append(Number(CaptionLead)).Append(" TL 1 0 0 1 ")
                .Append(Number(Margin)).Append(' ').Append(Number(top - CaptionSize)).Append(" Tm\n");
            for (int index = 0; index < caption.Count; index++)
            {
                text.Append('(').Append(Escape(caption[index])).Append(") Tj T*\n");
            }
            text.Append("ET\n");
            if (!hasImage) return text.ToString();

            double captionHeight = CaptionLead * caption.Count + 6;
            double availableWidth = PageWidth - Margin * 2;
            double availableHeight = PageHeight - Margin * 2 - captionHeight;
            double scale = Math.Min(availableWidth / image.Width, availableHeight / image.Height);
            // A picture smaller than the page is left at one unit per pixel
            // rather than blown up, so a small window is not printed blurred.
            if (scale > 1) scale = 1;
            double drawWidth = image.Width * scale;
            double drawHeight = image.Height * scale;
            double x = Margin;
            double y = top - captionHeight - drawHeight;
            if (y < Margin) y = Margin;
            text.Append("q ").Append(Number(drawWidth)).Append(" 0 0 ").Append(Number(drawHeight)).Append(' ')
                .Append(Number(x)).Append(' ').Append(Number(y)).Append(" cm /Im0 Do Q\n");
            return text.ToString();
        }

        private static byte[] ImageObject(PdfImage image)
        {
            byte[] compressed = Deflate(image.Data);
            StringBuilder header = new StringBuilder();
            header.Append("<< /Type /XObject /Subtype /Image /Width ").Append(image.Width)
                .Append(" /Height ").Append(image.Height)
                .Append(" /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode");
            return Stream(header, compressed);
        }

        private static byte[] Stream(StringBuilder header, byte[] payload)
        {
            StringBuilder start = new StringBuilder();
            if (header.Length == 0) start.Append("<< /Length ").Append(payload.Length).Append(" >>");
            else start.Append(header.ToString()).Append(" /Length ").Append(payload.Length).Append(" >>");
            byte[] prefix = Latin1(start.ToString() + "\nstream\n");
            byte[] suffix = Latin1("\nendstream");
            byte[] result = new byte[prefix.Length + payload.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, result, prefix.Length, payload.Length);
            Buffer.BlockCopy(suffix, 0, result, prefix.Length + payload.Length, suffix.Length);
            return result;
        }

        private static PdfImage LoadImage(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using (Bitmap source = new Bitmap(path))
            {
                PdfImage image = new PdfImage();
                image.SourceWidth = source.Width;
                image.SourceHeight = source.Height;
                int width = source.Width;
                int height = source.Height;
                if (width <= 0 || height <= 0) return null;
                if (width > MaxPixels || height > MaxPixels)
                {
                    double factor = Math.Min(MaxPixels / (double)width, MaxPixels / (double)height);
                    width = Math.Max(1, (int)Math.Round(width * factor));
                    height = Math.Max(1, (int)Math.Round(height * factor));
                }
                image.Width = width;
                image.Height = height;
                image.Data = Rgb(source, width, height);
                return image;
            }
        }

        private static byte[] Rgb(Bitmap source, int width, int height)
        {
            byte[] rgb = new byte[width * height * 3];
            using (Bitmap copy = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                }
                BitmapData data = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[Math.Abs(data.Stride)];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                        int target = y * width * 3;
                        for (int x = 0; x < width; x++)
                        {
                            // The bitmap rows are blue first; the document wants
                            // red first.
                            rgb[target + x * 3] = row[x * 3 + 2];
                            rgb[target + x * 3 + 1] = row[x * 3 + 1];
                            rgb[target + x * 3 + 2] = row[x * 3];
                        }
                    }
                }
                finally
                {
                    copy.UnlockBits(data);
                }
            }
            return rgb;
        }

        // The framework writes the bare compressed form; the document format
        // asks for the wrapped one, which is two bytes in front and a checksum
        // behind.
        private static byte[] Deflate(byte[] raw)
        {
            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (DeflateStream deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                uint checksum = Adler32(raw);
                output.WriteByte((byte)((checksum >> 24) & 0xFF));
                output.WriteByte((byte)((checksum >> 16) & 0xFF));
                output.WriteByte((byte)((checksum >> 8) & 0xFF));
                output.WriteByte((byte)(checksum & 0xFF));
                return output.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1;
            uint b = 0;
            for (int index = 0; index < data.Length; index++)
            {
                a = (a + data[index]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static void Append(FileStream file, byte[] payload)
        {
            file.Write(payload, 0, payload.Length);
        }

        private static byte[] Latin1(string value)
        {
            return Encoding.GetEncoding(28591).GetBytes(value);
        }

        private static string Number(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return (value ?? String.Empty).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        }
    }
}
