namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    // One entry of an OLE2 directory: a storage, a stream, or an unused slot.
    public sealed class Ole2Entry
    {
        public int Id;
        public string Name = "";
        // 1 storage, 2 stream, 5 root, 0 unused. The numbers are the format's.
        public byte ObjectType;
        public byte ColorFlag;
        public uint LeftSiblingId;
        public uint RightSiblingId;
        public uint ChildId;
        public Guid ClassId;
        public uint StateBits;
        public long CreationTimeRaw;
        public long ModifiedTimeRaw;
        public int StartSector;
        public long Size;
        public int ParentId = -1;
        public List<int> Children = new List<int>();
    }

    // A stream that does not exist in the source yet and is to be created
    // inside one of its storages.
    public sealed class Ole2Addition
    {
        public int ParentEntryId;
        public string Name;
        public byte[] Data;

        public Ole2Addition(int parentEntryId, string name, byte[] data)
        {
            ParentEntryId = parentEntryId;
            Name = name;
            Data = data;
        }
    }

    // The compound file that a vbaProject.bin is.
    //
    // A workbook's VBA project is not text with a header on it: it is an OLE2
    // container holding the project streams, and the only way to add a module
    // to one without a VBA host is to read that container, put the new streams
    // in, and write it back. This is the reader.
    //
    // It is deliberately strict. Everything it is ever asked to read is either
    // the seed workbook this product ships or a workbook this product wrote, so
    // anything that does not hold is a defect here or a damaged seed - never
    // something to work around quietly. A guess would produce a workbook that
    // Excel refuses to open, which is worse than a build that stops and says
    // which part of the container did not hold.
    public sealed class Ole2File
    {
        public const int FreeSector = -1;
        public const int EndOfChain = -2;
        public const int FatSector = -3;
        public const int DifatSector = -4;
        public const uint NoStream = 0xFFFFFFFFU;

        private readonly byte[] bytes;
        private int[] fat = new int[0];
        private int[] miniFat = new int[0];
        private byte[] miniStream = new byte[0];
        private int sectorCount;

        public int SectorSize;
        public int MiniSectorSize;
        public long MiniStreamCutoff;
        public List<Ole2Entry> Entries = new List<Ole2Entry>();
        public Ole2Entry RootEntry;

        private Ole2File(byte[] source)
        {
            bytes = source;
        }

        public static Ole2File Parse(byte[] source)
        {
            if (source == null) throw new ArgumentNullException("source");
            Ole2File file = new Ole2File(source);
            file.ReadHeader();
            file.ReadFat();
            file.ReadDirectory();
            file.ReadMiniFat();
            return file;
        }

        public Ole2Entry FindChild(Ole2Entry parent, string name, byte objectType)
        {
            if (parent == null) throw new ArgumentNullException("parent");
            Ole2Entry found = null;
            for (int index = 0; index < parent.Children.Count; index++)
            {
                Ole2Entry entry = Entries[parent.Children[index]];
                if (entry.ObjectType != objectType) continue;
                if (!String.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) throw new InvalidDataException("The container holds more than one " + name + ".");
                found = entry;
            }
            return found;
        }

        public byte[] ReadStream(Ole2Entry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.ObjectType != 2) throw new InvalidDataException("This is not a stream: " + entry.Name);
            if (entry.Size == 0) return new byte[0];
            if (entry.Size < MiniStreamCutoff) return ReadFromMini(entry);
            return ReadFromSectors(entry.StartSector, (int)entry.Size, SectorSize, bytes, true, entry.Name);
        }

        private byte[] ReadFromMini(Ole2Entry entry)
        {
            if (miniStream.Length == 0) throw new InvalidDataException("The container has no mini stream to read " + entry.Name + " from.");
            return ReadChain(entry.StartSector, (int)entry.Size, MiniSectorSize, miniStream, miniFat, false, entry.Name);
        }

        private byte[] ReadFromSectors(int startSector, int length, int size, byte[] data, bool sectorOffsets, string label)
        {
            return ReadChain(startSector, length, size, data, fat, sectorOffsets, label);
        }

        // One chain, walked exactly as far as the recorded size needs and no
        // further. A chain that ends early, revisits a sector, or runs past the
        // data is a broken container, and reading what happens to be there
        // would hand back somebody else's bytes as this stream's content.
        private byte[] ReadChain(int startSector, int length, int size, byte[] data, int[] table, bool sectorOffsets, string label)
        {
            byte[] result = new byte[length];
            int written = 0;
            int current = startSector;
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            while (written < length)
            {
                if (current < 0 || current >= table.Length) throw new InvalidDataException("The chain of " + label + " ends before its recorded size.");
                if (seen.ContainsKey(current)) throw new InvalidDataException("The chain of " + label + " loops back on itself.");
                seen.Add(current, true);
                long offset = sectorOffsets ? ((long)current + 1L) * (long)size : (long)current * (long)size;
                if (offset < 0 || offset + size > data.Length) throw new InvalidDataException("The chain of " + label + " points outside the container.");
                int take = Math.Min(size, length - written);
                Buffer.BlockCopy(data, (int)offset, result, written, take);
                written += take;
                current = table[current];
            }
            return result;
        }

        private void ReadHeader()
        {
            byte[] signature = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
            if (bytes.Length < 512) throw new InvalidDataException("The container is shorter than an OLE2 header.");
            for (int index = 0; index < signature.Length; index++)
            {
                if (bytes[index] != signature[index]) throw new InvalidDataException("This is not an OLE2 container.");
            }
            ushort byteOrder = ReadUInt16(bytes, 28);
            ushort sectorShift = ReadUInt16(bytes, 30);
            ushort miniSectorShift = ReadUInt16(bytes, 32);
            if (byteOrder != 0xFFFE) throw new InvalidDataException("The container is not little endian.");
            if (sectorShift != 9) throw new InvalidDataException("Only 512 byte sector containers are handled here.");
            if (miniSectorShift != 6) throw new InvalidDataException("Only 64 byte mini sector containers are handled here.");
            SectorSize = 1 << sectorShift;
            MiniSectorSize = 1 << miniSectorShift;
            if (bytes.Length % SectorSize != 0) throw new InvalidDataException("The container does not end on a sector boundary.");
            sectorCount = bytes.Length / SectorSize - 1;
            MiniStreamCutoff = ReadUInt32(bytes, 56);
            if (MiniStreamCutoff != 0x1000) throw new InvalidDataException("The container uses an unexpected mini stream cutoff.");
        }

        private void ReadFat()
        {
            int fatSectorCount = (int)ReadUInt32(bytes, 44);
            int difatSectorCount = (int)ReadUInt32(bytes, 72);
            List<int> fatSectors = new List<int>();
            for (int index = 0; index < 109 && fatSectors.Count < fatSectorCount; index++)
            {
                int sector = ReadInt32(bytes, 76 + index * 4);
                if (sector == FreeSector) continue;
                fatSectors.Add(Valid(sector, "FAT"));
            }
            int perDifat = SectorSize / 4 - 1;
            int current = ReadInt32(bytes, 68);
            for (int index = 0; index < difatSectorCount; index++)
            {
                int offset = SectorOffset(Valid(current, "DIFAT"));
                for (int item = 0; item < perDifat && fatSectors.Count < fatSectorCount; item++)
                {
                    int sector = ReadInt32(bytes, offset + item * 4);
                    if (sector == FreeSector) continue;
                    fatSectors.Add(Valid(sector, "FAT"));
                }
                current = ReadInt32(bytes, offset + perDifat * 4);
            }
            if (fatSectors.Count != fatSectorCount) throw new InvalidDataException("The container names fewer FAT sectors than it says it has.");

            int perSector = SectorSize / 4;
            fat = new int[fatSectors.Count * perSector];
            int written = 0;
            for (int index = 0; index < fatSectors.Count; index++)
            {
                int offset = SectorOffset(fatSectors[index]);
                for (int item = 0; item < perSector; item++)
                {
                    fat[written] = ReadInt32(bytes, offset + item * 4);
                    written++;
                }
            }
        }

        private void ReadDirectory()
        {
            byte[] directory = ReadFromSectors(ReadInt32(bytes, 48), ChainLength(ReadInt32(bytes, 48)) * SectorSize, SectorSize, bytes, true, "the directory");
            if (directory.Length % 128 != 0) throw new InvalidDataException("The directory does not divide into entries.");
            int count = directory.Length / 128;
            for (int index = 0; index < count; index++)
            {
                int offset = index * 128;
                Ole2Entry entry = new Ole2Entry();
                entry.Id = index;
                ushort nameLength = ReadUInt16(directory, offset + 64);
                byte objectType = directory[offset + 66];
                if (objectType != 0 && objectType != 1 && objectType != 2 && objectType != 5)
                {
                    throw new InvalidDataException("A directory entry has an object type this reader does not know: " + objectType.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (nameLength > 64 || (nameLength & 1) != 0) throw new InvalidDataException("A directory entry has an impossible name length.");
                entry.Name = nameLength >= 2 ? Encoding.Unicode.GetString(directory, offset, nameLength - 2) : "";
                entry.ObjectType = objectType;
                entry.ColorFlag = directory[offset + 67];
                entry.LeftSiblingId = ReadUInt32(directory, offset + 68);
                entry.RightSiblingId = ReadUInt32(directory, offset + 72);
                entry.ChildId = ReadUInt32(directory, offset + 76);
                byte[] classId = new byte[16];
                Buffer.BlockCopy(directory, offset + 80, classId, 0, classId.Length);
                entry.ClassId = new Guid(classId);
                entry.StateBits = ReadUInt32(directory, offset + 96);
                entry.CreationTimeRaw = ReadInt64(directory, offset + 100);
                entry.ModifiedTimeRaw = ReadInt64(directory, offset + 108);
                entry.StartSector = ReadInt32(directory, offset + 116);
                // Version 3 records a 32 bit size in a 64 bit field.
                entry.Size = (long)(ReadUInt64(directory, offset + 120) & 0xFFFFFFFFUL);
                Entries.Add(entry);
            }
            if (Entries.Count == 0 || Entries[0].ObjectType != 5) throw new InvalidDataException("The container has no root entry.");
            RootEntry = Entries[0];
            for (int index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].ObjectType == 1 || Entries[index].ObjectType == 5) LinkChildren(Entries[index]);
            }
        }

        // The directory is a red-black tree per storage. Nothing here cares
        // about its shape, only about which entries live under which storage,
        // so it is walked once and flattened into a list of children.
        private void LinkChildren(Ole2Entry storage)
        {
            if (storage.ChildId == NoStream) return;
            Stack<uint> pending = new Stack<uint>();
            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            pending.Push(storage.ChildId);
            while (pending.Count > 0)
            {
                uint id = pending.Pop();
                if (id == NoStream) continue;
                if (id >= Entries.Count) throw new InvalidDataException("The directory names an entry that is not there.");
                if (seen.ContainsKey(id)) throw new InvalidDataException("The directory tree loops back on itself.");
                seen.Add(id, true);
                Ole2Entry entry = Entries[(int)id];
                if (entry.ObjectType == 0 || entry.ObjectType == 5) throw new InvalidDataException("The directory tree holds an entry that cannot be in it.");
                if (entry.ParentId != -1) throw new InvalidDataException("An entry is in two storages at once: " + entry.Name);
                entry.ParentId = storage.Id;
                storage.Children.Add(entry.Id);
                if (entry.LeftSiblingId != NoStream) pending.Push(entry.LeftSiblingId);
                if (entry.RightSiblingId != NoStream) pending.Push(entry.RightSiblingId);
            }
        }

        private void ReadMiniFat()
        {
            int first = ReadInt32(bytes, 60);
            int miniFatSectorCount = (int)ReadUInt32(bytes, 64);
            if (miniFatSectorCount > 0)
            {
                byte[] table = ReadFromSectors(first, miniFatSectorCount * SectorSize, SectorSize, bytes, true, "the mini FAT");
                miniFat = new int[table.Length / 4];
                for (int index = 0; index < miniFat.Length; index++) miniFat[index] = ReadInt32(table, index * 4);
            }
            if (RootEntry.Size > 0)
            {
                miniStream = ReadFromSectors(RootEntry.StartSector, (int)RootEntry.Size, SectorSize, bytes, true, "the mini stream");
            }
        }

        private int ChainLength(int startSector)
        {
            int length = 0;
            int current = startSector;
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            while (current != EndOfChain)
            {
                if (current < 0 || current >= fat.Length) throw new InvalidDataException("A sector chain leaves the container.");
                if (seen.ContainsKey(current)) throw new InvalidDataException("A sector chain loops back on itself.");
                seen.Add(current, true);
                length++;
                current = fat[current];
            }
            return length;
        }

        private int Valid(int sector, string label)
        {
            if (sector < 0 || sector >= sectorCount) throw new InvalidDataException("The container names a " + label + " sector that is not in it.");
            return sector;
        }

        private int SectorOffset(int sector)
        {
            return (sector + 1) * SectorSize;
        }

        internal static ushort ReadUInt16(byte[] data, int offset)
        {
            Require(data, offset, 2);
            return BitConverter.ToUInt16(data, offset);
        }

        internal static uint ReadUInt32(byte[] data, int offset)
        {
            Require(data, offset, 4);
            return BitConverter.ToUInt32(data, offset);
        }

        internal static int ReadInt32(byte[] data, int offset)
        {
            Require(data, offset, 4);
            return BitConverter.ToInt32(data, offset);
        }

        internal static ulong ReadUInt64(byte[] data, int offset)
        {
            Require(data, offset, 8);
            return BitConverter.ToUInt64(data, offset);
        }

        internal static long ReadInt64(byte[] data, int offset)
        {
            Require(data, offset, 8);
            return BitConverter.ToInt64(data, offset);
        }

        private static void Require(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + (long)count > data.Length)
            {
                throw new InvalidDataException("The container ends in the middle of a field.");
            }
        }
    }

    // Writing the container back out.
    //
    // Nothing is patched in place. The whole file is laid out again from the
    // entries and their contents, because a stream that grows moves everything
    // after it and a container half rewritten is a container Excel refuses to
    // open. Laying it out from scratch is also what makes adding a stream
    // possible at all: a new directory entry changes the tree, the tree changes
    // the directory size, and the directory size changes the allocation.
    public static class Ole2Writer
    {
        private const int SectorSize = 512;
        private const int MiniSectorSize = 64;
        private const int MiniStreamCutoff = 4096;
        private const int FatEntriesPerSector = 128;
        private const int DifatEntriesPerSector = 127;

        private sealed class Plan
        {
            public Ole2Entry Entry;
            public byte[] Data;
            public bool IsMini;
            public int SectorCount;
            public int StartSector;
            public int MiniSectorCount;
            public int MiniStartSector;
        }

        public static byte[] Rebuild(Ole2File source, IDictionary<int, byte[]> changes, IList<Ole2Addition> additions)
        {
            if (source == null) throw new ArgumentNullException("source");
            List<Ole2Entry> entries = Clone(source);
            IDictionary<int, byte[]> effective = AddEntries(source, entries, changes, additions);
            BuildTrees(entries);

            List<Plan> streams = BuildPlans(source, entries, effective);
            int miniSectorCount = AssignMiniSectors(streams);
            byte[] miniStream = BuildMiniStream(streams, miniSectorCount);
            int[] miniFat = BuildMiniFat(streams, miniSectorCount);
            int miniFatSectorCount = miniFat.Length / FatEntriesPerSector;

            int directorySectorCount = RoundUp(entries.Count * 128, SectorSize);
            int miniStreamSectorCount = RoundUp(miniStream.Length, SectorSize);
            int baseSectors = directorySectorCount + miniStreamSectorCount + miniFatSectorCount;
            for (int index = 0; index < streams.Count; index++)
            {
                if (!streams[index].IsMini) baseSectors += streams[index].SectorCount;
            }

            int fatSectorCount;
            int difatSectorCount;
            AllocationTableCounts(baseSectors, out fatSectorCount, out difatSectorCount);

            int next = 0;
            for (int index = 0; index < streams.Count; index++)
            {
                Plan stream = streams[index];
                if (stream.Data.Length == 0)
                {
                    stream.StartSector = Ole2File.EndOfChain;
                    stream.Entry.StartSector = Ole2File.EndOfChain;
                }
                else if (stream.IsMini)
                {
                    stream.Entry.StartSector = stream.MiniStartSector;
                }
                else
                {
                    stream.StartSector = next;
                    stream.Entry.StartSector = next;
                    next += stream.SectorCount;
                }
                stream.Entry.Size = stream.Data.Length;
            }

            int miniStreamStart = Ole2File.EndOfChain;
            if (miniStreamSectorCount > 0)
            {
                miniStreamStart = next;
                next += miniStreamSectorCount;
            }
            int miniFatStart = Ole2File.EndOfChain;
            if (miniFatSectorCount > 0)
            {
                miniFatStart = next;
                next += miniFatSectorCount;
            }
            int directoryStart = next;
            next += directorySectorCount;

            List<int> fatSectors = new List<int>();
            for (int index = 0; index < fatSectorCount; index++)
            {
                fatSectors.Add(next);
                next++;
            }
            List<int> difatSectors = new List<int>();
            for (int index = 0; index < difatSectorCount; index++)
            {
                difatSectors.Add(next);
                next++;
            }

            int[] fat = Filled(fatSectorCount * FatEntriesPerSector, Ole2File.FreeSector);
            for (int index = 0; index < streams.Count; index++)
            {
                Plan stream = streams[index];
                if (!stream.IsMini && stream.SectorCount > 0) Chain(fat, stream.StartSector, stream.SectorCount);
            }
            Chain(fat, miniStreamStart, miniStreamSectorCount);
            Chain(fat, miniFatStart, miniFatSectorCount);
            Chain(fat, directoryStart, directorySectorCount);
            for (int index = 0; index < fatSectors.Count; index++) fat[fatSectors[index]] = Ole2File.FatSector;
            for (int index = 0; index < difatSectors.Count; index++) fat[difatSectors[index]] = Ole2File.DifatSector;

            entries[0].StartSector = miniStreamStart;
            entries[0].Size = miniStream.Length;

            long length = ((long)next + 1L) * (long)SectorSize;
            if (length > Int32.MaxValue) throw new InvalidDataException("The rebuilt container would be too large.");
            byte[] output = new byte[(int)length];
            WriteHeader(output, fatSectors, directoryStart, miniFatStart, miniFatSectorCount, difatSectors);
            for (int index = 0; index < streams.Count; index++)
            {
                Plan stream = streams[index];
                if (!stream.IsMini && stream.SectorCount > 0) WriteAt(output, stream.StartSector, stream.Data);
            }
            if (miniStreamSectorCount > 0) WriteAt(output, miniStreamStart, miniStream);
            if (miniFatSectorCount > 0) WriteInts(output, miniFatStart, miniFat);
            WriteAt(output, directoryStart, BuildDirectory(entries, directorySectorCount * SectorSize));
            WriteFat(output, fatSectors, fat);
            WriteDifat(output, fatSectors, difatSectors);
            return output;
        }

        private static List<Ole2Entry> Clone(Ole2File source)
        {
            List<Ole2Entry> result = new List<Ole2Entry>();
            for (int index = 0; index < source.Entries.Count; index++)
            {
                Ole2Entry original = source.Entries[index];
                Ole2Entry entry = new Ole2Entry();
                entry.Id = original.Id;
                entry.Name = original.Name;
                entry.ObjectType = original.ObjectType;
                entry.ColorFlag = original.ColorFlag;
                entry.LeftSiblingId = original.LeftSiblingId;
                entry.RightSiblingId = original.RightSiblingId;
                entry.ChildId = original.ChildId;
                entry.ClassId = original.ClassId;
                entry.StateBits = original.StateBits;
                entry.CreationTimeRaw = original.CreationTimeRaw;
                entry.ModifiedTimeRaw = original.ModifiedTimeRaw;
                entry.StartSector = original.StartSector;
                entry.Size = original.Size;
                entry.ParentId = original.ParentId;
                result.Add(entry);
            }
            return result;
        }

        private static IDictionary<int, byte[]> AddEntries(Ole2File source, List<Ole2Entry> entries,
            IDictionary<int, byte[]> changes, IList<Ole2Addition> additions)
        {
            Dictionary<int, byte[]> result = new Dictionary<int, byte[]>();
            if (changes != null)
            {
                foreach (KeyValuePair<int, byte[]> pair in changes)
                {
                    if (pair.Key < 0 || pair.Key >= entries.Count || entries[pair.Key].ObjectType != 2)
                    {
                        throw new InvalidDataException("A stream was to be replaced that is not in the container.");
                    }
                    if (pair.Value == null) throw new InvalidDataException("A stream was to be replaced with nothing.");
                    result.Add(pair.Key, pair.Value);
                }
            }
            if (additions == null) return result;
            for (int index = 0; index < additions.Count; index++)
            {
                Ole2Addition addition = additions[index];
                if (addition == null || addition.Data == null || String.IsNullOrEmpty(addition.Name))
                {
                    throw new InvalidDataException("A stream was to be added without a name or without content.");
                }
                if (addition.ParentEntryId < 0 || addition.ParentEntryId >= entries.Count)
                {
                    throw new InvalidDataException("A stream was to be added to a storage that is not in the container.");
                }
                byte parentType = entries[addition.ParentEntryId].ObjectType;
                if (parentType != 1 && parentType != 5) throw new InvalidDataException("A stream was to be added to something that is not a storage.");
                Ole2Entry entry = new Ole2Entry();
                entry.Id = entries.Count;
                entry.Name = addition.Name;
                entry.ObjectType = 2;
                entry.ColorFlag = 1;
                entry.LeftSiblingId = Ole2File.NoStream;
                entry.RightSiblingId = Ole2File.NoStream;
                entry.ChildId = Ole2File.NoStream;
                entry.ClassId = Guid.Empty;
                entry.StartSector = Ole2File.EndOfChain;
                entry.Size = addition.Data.Length;
                entry.ParentId = addition.ParentEntryId;
                entries.Add(entry);
                result.Add(entry.Id, addition.Data);
            }
            return result;
        }

        // Every storage's children are sorted the way the format sorts them and
        // laid out as a balanced tree. Colouring every node black is allowed:
        // the rules are about the longest path against the shortest, and a tree
        // built by halving satisfies them however it is coloured.
        private static void BuildTrees(List<Ole2Entry> entries)
        {
            Dictionary<int, List<int>> children = new Dictionary<int, List<int>>();
            for (int index = 0; index < entries.Count; index++)
            {
                Ole2Entry entry = entries[index];
                entry.LeftSiblingId = Ole2File.NoStream;
                entry.RightSiblingId = Ole2File.NoStream;
                entry.ChildId = Ole2File.NoStream;
                if (entry.ObjectType != 0) entry.ColorFlag = 1;
                if (index == 0 || entry.ObjectType == 0) continue;
                if (entry.ParentId < 0 || entry.ParentId >= entries.Count)
                {
                    throw new InvalidDataException("An entry is in no storage, so where to write it back is unknown: " + entry.Name);
                }
                List<int> list;
                if (!children.TryGetValue(entry.ParentId, out list))
                {
                    list = new List<int>();
                    children.Add(entry.ParentId, list);
                }
                list.Add(index);
            }
            foreach (KeyValuePair<int, List<int>> pair in children)
            {
                List<int> list = pair.Value;
                list.Sort(delegate(int left, int right) { return CompareNames(entries[left].Name, entries[right].Name); });
                for (int index = 1; index < list.Count; index++)
                {
                    if (CompareNames(entries[list[index - 1]].Name, entries[list[index]].Name) == 0)
                    {
                        throw new InvalidDataException("Two entries in one storage have the same name: " + entries[list[index]].Name);
                    }
                }
                entries[pair.Key].ChildId = (uint)Balanced(entries, list, 0, list.Count);
            }
        }

        private static int Balanced(List<Ole2Entry> entries, List<int> sorted, int start, int count)
        {
            if (count == 0) return -1;
            int leftCount = count / 2;
            int middle = start + leftCount;
            int id = sorted[middle];
            int left = Balanced(entries, sorted, start, leftCount);
            int right = Balanced(entries, sorted, middle + 1, count - leftCount - 1);
            entries[id].LeftSiblingId = left < 0 ? Ole2File.NoStream : (uint)left;
            entries[id].RightSiblingId = right < 0 ? Ole2File.NoStream : (uint)right;
            entries[id].ColorFlag = 1;
            return id;
        }

        // Shorter names sort first, then by upper cased UTF-16 code unit. This
        // is the format's own order, not the one a reader would expect.
        private static int CompareNames(string left, string right)
        {
            int leftLength = Encoding.Unicode.GetByteCount(left) + 2;
            int rightLength = Encoding.Unicode.GetByteCount(right) + 2;
            if (leftLength != rightLength) return leftLength < rightLength ? -1 : 1;
            for (int index = 0; index < left.Length; index++)
            {
                int a = Char.ToUpperInvariant(left[index]);
                int b = Char.ToUpperInvariant(right[index]);
                if (a != b) return a < b ? -1 : 1;
            }
            return 0;
        }

        private static List<Plan> BuildPlans(Ole2File source, List<Ole2Entry> entries, IDictionary<int, byte[]> changes)
        {
            List<Plan> result = new List<Plan>();
            for (int index = 0; index < entries.Count; index++)
            {
                Ole2Entry entry = entries[index];
                if (entry.ObjectType != 2) continue;
                byte[] data;
                if (!changes.TryGetValue(index, out data)) data = source.ReadStream(source.Entries[index]);
                Plan plan = new Plan();
                plan.Entry = entry;
                plan.Data = data;
                plan.IsMini = data.Length > 0 && data.Length < MiniStreamCutoff;
                plan.SectorCount = plan.IsMini ? 0 : RoundUp(data.Length, SectorSize);
                plan.MiniSectorCount = plan.IsMini ? RoundUp(data.Length, MiniSectorSize) : 0;
                plan.StartSector = Ole2File.EndOfChain;
                plan.MiniStartSector = Ole2File.EndOfChain;
                result.Add(plan);
            }
            return result;
        }

        private static int AssignMiniSectors(List<Plan> streams)
        {
            int next = 0;
            for (int index = 0; index < streams.Count; index++)
            {
                if (!streams[index].IsMini) continue;
                streams[index].MiniStartSector = next;
                next += streams[index].MiniSectorCount;
            }
            return next;
        }

        private static byte[] BuildMiniStream(List<Plan> streams, int miniSectorCount)
        {
            byte[] result = new byte[miniSectorCount * MiniSectorSize];
            for (int index = 0; index < streams.Count; index++)
            {
                Plan stream = streams[index];
                if (!stream.IsMini) continue;
                Buffer.BlockCopy(stream.Data, 0, result, stream.MiniStartSector * MiniSectorSize, stream.Data.Length);
            }
            return result;
        }

        private static int[] BuildMiniFat(List<Plan> streams, int miniSectorCount)
        {
            if (miniSectorCount == 0) return new int[0];
            int[] result = Filled(RoundUp(miniSectorCount, FatEntriesPerSector) * FatEntriesPerSector, Ole2File.FreeSector);
            for (int index = 0; index < streams.Count; index++)
            {
                if (streams[index].IsMini) Chain(result, streams[index].MiniStartSector, streams[index].MiniSectorCount);
            }
            return result;
        }

        // The FAT has to describe itself, so adding a FAT sector can need
        // another one. The count is settled by asking again until the answer
        // stops changing.
        private static void AllocationTableCounts(int baseSectors, out int fatSectorCount, out int difatSectorCount)
        {
            fatSectorCount = RoundUp(baseSectors, FatEntriesPerSector);
            difatSectorCount = DifatCount(fatSectorCount);
            while (true)
            {
                int total = baseSectors + fatSectorCount + difatSectorCount;
                int nextFat = RoundUp(total, FatEntriesPerSector);
                int nextDifat = DifatCount(nextFat);
                if (nextFat == fatSectorCount && nextDifat == difatSectorCount) return;
                fatSectorCount = nextFat;
                difatSectorCount = nextDifat;
            }
        }

        private static int DifatCount(int fatSectorCount)
        {
            if (fatSectorCount <= 109) return 0;
            return RoundUp(fatSectorCount - 109, DifatEntriesPerSector);
        }

        private static int[] Filled(int length, int value)
        {
            int[] result = new int[length];
            for (int index = 0; index < result.Length; index++) result[index] = value;
            return result;
        }

        private static void Chain(int[] table, int start, int count)
        {
            if (count == 0) return;
            if (start < 0 || (long)start + (long)count > table.Length) throw new InvalidDataException("A chain would leave its allocation table.");
            for (int index = 0; index < count; index++)
            {
                table[start + index] = index + 1 < count ? start + index + 1 : Ole2File.EndOfChain;
            }
        }

        private static byte[] BuildDirectory(List<Ole2Entry> entries, int byteCount)
        {
            byte[] result = new byte[byteCount];
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index].ObjectType != 0) WriteEntry(result, index * 128, entries[index]);
            }
            return result;
        }

        private static void WriteEntry(byte[] output, int offset, Ole2Entry entry)
        {
            byte[] name = Encoding.Unicode.GetBytes(entry.Name + "\0");
            if (name.Length < 2 || name.Length > 64) throw new InvalidDataException("A directory name does not fit: " + entry.Name);
            Buffer.BlockCopy(name, 0, output, offset, name.Length);
            WriteUInt16(output, offset + 64, (ushort)name.Length);
            output[offset + 66] = entry.ObjectType;
            output[offset + 67] = entry.ColorFlag;
            WriteUInt32(output, offset + 68, entry.LeftSiblingId);
            WriteUInt32(output, offset + 72, entry.RightSiblingId);
            WriteUInt32(output, offset + 76, entry.ChildId);
            byte[] classId = entry.ClassId.ToByteArray();
            Buffer.BlockCopy(classId, 0, output, offset + 80, classId.Length);
            WriteUInt32(output, offset + 96, entry.StateBits);
            WriteUInt64(output, offset + 100, unchecked((ulong)entry.CreationTimeRaw));
            WriteUInt64(output, offset + 108, unchecked((ulong)entry.ModifiedTimeRaw));
            WriteInt32(output, offset + 116, entry.StartSector);
            WriteUInt64(output, offset + 120, (ulong)entry.Size);
        }

        private static void WriteHeader(byte[] output, List<int> fatSectors, int directoryStart,
            int miniFatStart, int miniFatSectorCount, List<int> difatSectors)
        {
            byte[] signature = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
            Buffer.BlockCopy(signature, 0, output, 0, signature.Length);
            WriteUInt16(output, 24, 0x003E);
            WriteUInt16(output, 26, 0x0003);
            WriteUInt16(output, 28, 0xFFFE);
            WriteUInt16(output, 30, 0x0009);
            WriteUInt16(output, 32, 0x0006);
            WriteUInt32(output, 40, 0);
            WriteUInt32(output, 44, (uint)fatSectors.Count);
            WriteInt32(output, 48, directoryStart);
            WriteUInt32(output, 52, 0);
            WriteUInt32(output, 56, MiniStreamCutoff);
            WriteInt32(output, 60, miniFatStart);
            WriteUInt32(output, 64, (uint)miniFatSectorCount);
            WriteInt32(output, 68, difatSectors.Count == 0 ? Ole2File.EndOfChain : difatSectors[0]);
            WriteUInt32(output, 72, (uint)difatSectors.Count);
            for (int index = 0; index < 109; index++)
            {
                WriteInt32(output, 76 + index * 4, index < fatSectors.Count ? fatSectors[index] : Ole2File.FreeSector);
            }
        }

        private static void WriteAt(byte[] output, int startSector, byte[] data)
        {
            if (data.Length == 0) return;
            int offset = (startSector + 1) * SectorSize;
            if ((long)offset + (long)data.Length > output.Length) throw new InvalidDataException("A stream would be written outside the container.");
            Buffer.BlockCopy(data, 0, output, offset, data.Length);
        }

        private static void WriteInts(byte[] output, int startSector, int[] values)
        {
            int offset = (startSector + 1) * SectorSize;
            for (int index = 0; index < values.Length; index++) WriteInt32(output, offset + index * 4, values[index]);
        }

        private static void WriteFat(byte[] output, List<int> fatSectors, int[] fat)
        {
            for (int index = 0; index < fatSectors.Count; index++)
            {
                int offset = (fatSectors[index] + 1) * SectorSize;
                for (int item = 0; item < FatEntriesPerSector; item++)
                {
                    WriteInt32(output, offset + item * 4, fat[index * FatEntriesPerSector + item]);
                }
            }
        }

        private static void WriteDifat(byte[] output, List<int> fatSectors, List<int> difatSectors)
        {
            for (int index = 0; index < difatSectors.Count; index++)
            {
                int offset = (difatSectors[index] + 1) * SectorSize;
                for (int item = 0; item < DifatEntriesPerSector; item++)
                {
                    int fatIndex = 109 + index * DifatEntriesPerSector + item;
                    WriteInt32(output, offset + item * 4, fatIndex < fatSectors.Count ? fatSectors[fatIndex] : Ole2File.FreeSector);
                }
                WriteInt32(output, offset + SectorSize - 4,
                    index + 1 < difatSectors.Count ? difatSectors[index + 1] : Ole2File.EndOfChain);
            }
        }

        private static int RoundUp(int value, int divisor)
        {
            if (value <= 0) return 0;
            return (value + divisor - 1) / divisor;
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        internal static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset] = (byte)(value & 0xFF);
            output[offset + 1] = (byte)((value >> 8) & 0xFF);
            output[offset + 2] = (byte)((value >> 16) & 0xFF);
            output[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteInt32(byte[] output, int offset, int value)
        {
            WriteUInt32(output, offset, unchecked((uint)value));
        }

        private static void WriteUInt64(byte[] output, int offset, ulong value)
        {
            for (int index = 0; index < 8; index++) output[offset + index] = (byte)((value >> (index * 8)) & 0xFF);
        }
    }
}
