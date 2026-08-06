namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    // The compression a VBA project stores its source in.
    //
    // It is not deflate and not anything a framework has: it is the run length
    // scheme the VBA file format defines, a signature byte followed by chunks of
    // at most 4096 decompressed bytes, each chunk a series of literal bytes and
    // back references whose offset and length fields change width as the chunk
    // fills. Both directions are here because a build has to write the modules
    // it adds and read back what it wrote.
    public static class VbaCompress
    {
        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length == 0 || data[0] != 0x01) throw new InvalidDataException("This is not a compressed VBA container.");
            List<byte> output = new List<byte>();
            int position = 1;
            while (position < data.Length)
            {
                if (position + 2 > data.Length) throw new InvalidDataException("A compressed chunk header is cut short.");
                int chunkStart = position;
                ushort header = Ole2File.ReadUInt16(data, position);
                position += 2;
                int chunkSize = (header & 0x0FFF) + 3;
                int signature = (header >> 12) & 0x0007;
                bool compressed = (header & 0x8000) != 0;
                if (signature != 3) throw new InvalidDataException("A compressed chunk has the wrong signature.");
                long chunkEndLong = (long)chunkStart + (long)chunkSize;
                if (chunkEndLong > data.Length) throw new InvalidDataException("A compressed chunk runs past the end of the stream.");
                int chunkEnd = (int)chunkEndLong;
                int chunkOutputStart = output.Count;
                if (!compressed)
                {
                    if (chunkSize != 4098) throw new InvalidDataException("A stored chunk is not 4096 bytes.");
                    for (int index = 0; index < 4096; index++) output.Add(data[position + index]);
                    position = chunkEnd;
                    continue;
                }
                DecompressChunk(data, ref position, chunkEnd, chunkOutputStart, output);
                if (position != chunkEnd) throw new InvalidDataException("A compressed chunk was not consumed exactly.");
            }
            return output.ToArray();
        }

        private static void DecompressChunk(byte[] data, ref int position, int chunkEnd, int chunkOutputStart, List<byte> output)
        {
            while (position < chunkEnd)
            {
                byte flags = data[position];
                position++;
                for (int bit = 0; bit < 8 && position < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        Capacity(output.Count - chunkOutputStart, 1);
                        output.Add(data[position]);
                        position++;
                        continue;
                    }
                    if (position + 2 > chunkEnd) throw new InvalidDataException("A back reference is cut short.");
                    ushort token = Ole2File.ReadUInt16(data, position);
                    position += 2;
                    int at = output.Count - chunkOutputStart;
                    if (at <= 0) throw new InvalidDataException("A back reference has nothing to point back at.");
                    int bitCount = OffsetBitCount(at);
                    int length = (token & (0xFFFF >> bitCount)) + 3;
                    int offset = (token >> (16 - bitCount)) + 1;
                    if (offset > at) throw new InvalidDataException("A back reference points before its chunk.");
                    Capacity(at, length);
                    for (int index = 0; index < length; index++) output.Add(output[output.Count - offset]);
                }
            }
        }

        public static byte[] Compress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x01);
                int chunkStart = 0;
                while (chunkStart < data.Length)
                {
                    int chunkLength = Math.Min(4096, data.Length - chunkStart);
                    byte[] compressed = CompressChunk(data, chunkStart, chunkLength);
                    if (compressed.Length > 4096 && chunkLength < 4096)
                    {
                        // 3640 literals and the 455 flag bytes that go with them
                        // are the most that always fit, so a tail that grew
                        // under compression is retried short enough to store.
                        chunkLength = Math.Min(chunkLength, 3640);
                        compressed = CompressChunk(data, chunkStart, chunkLength);
                    }
                    if (compressed.Length > 4096)
                    {
                        WriteUInt16(output, 0x3FFF);
                        output.Write(data, chunkStart, chunkLength);
                    }
                    else
                    {
                        WriteUInt16(output, (ushort)(0xB000 | (compressed.Length + 2 - 3)));
                        output.Write(compressed, 0, compressed.Length);
                    }
                    chunkStart += chunkLength;
                }
                return output.ToArray();
            }
        }

        private static byte[] CompressChunk(byte[] data, int chunkStart, int chunkLength)
        {
            int chunkEnd = chunkStart + chunkLength;
            using (MemoryStream output = new MemoryStream())
            {
                int position = chunkStart;
                while (position < chunkEnd)
                {
                    long flagPosition = output.Position;
                    output.WriteByte(0);
                    byte flags = 0;
                    for (int bit = 0; bit < 8 && position < chunkEnd; bit++)
                    {
                        int length;
                        int offset;
                        FindMatch(data, chunkStart, chunkEnd, position, out length, out offset);
                        if (length >= 3)
                        {
                            int bitCount = OffsetBitCount(position - chunkStart);
                            flags |= (byte)(1 << bit);
                            WriteUInt16(output, (ushort)(((offset - 1) << (16 - bitCount)) | (length - 3)));
                            position += length;
                        }
                        else
                        {
                            output.WriteByte(data[position]);
                            position++;
                        }
                    }
                    long end = output.Position;
                    output.Position = flagPosition;
                    output.WriteByte(flags);
                    output.Position = end;
                }
                return output.ToArray();
            }
        }

        // The longest run that can be named from here. A match may overlap the
        // position it is copied to - that is how a repeated byte is written -
        // so the source index wraps inside the match rather than running ahead.
        private static void FindMatch(byte[] data, int chunkStart, int chunkEnd, int position, out int bestLength, out int bestOffset)
        {
            bestLength = 0;
            bestOffset = 0;
            int at = position - chunkStart;
            if (at == 0) return;
            int bitCount = OffsetBitCount(at);
            int maximumOffset = Math.Min(1 << bitCount, at);
            int maximumLength = Math.Min((0xFFFF >> bitCount) + 3, chunkEnd - position);
            for (int offset = 1; offset <= maximumOffset; offset++)
            {
                if (data[position - offset] != data[position]) continue;
                int length = 1;
                while (length < maximumLength && data[position - offset + (length % offset)] == data[position + length]) length++;
                if (length >= 3 && length > bestLength)
                {
                    bestLength = length;
                    bestOffset = offset;
                    if (bestLength == maximumLength) return;
                }
            }
        }

        private static int OffsetBitCount(int at)
        {
            int bitCount = 4;
            while ((1 << bitCount) < at && bitCount < 12) bitCount++;
            return bitCount;
        }

        private static void Capacity(int at, int more)
        {
            if (at < 0 || more < 0 || at + more > 4096) throw new InvalidDataException("A chunk decompresses to more than 4096 bytes.");
        }

        private static void WriteUInt16(Stream output, ushort value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
        }
    }
}
