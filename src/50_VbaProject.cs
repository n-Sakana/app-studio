namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public enum VbaModuleKind
    {
        Document = 0,
        Form = 1,
        Standard = 2,
        Class = 3
    }

    // One module as the project's directory records it.
    public sealed class VbaDirRecord
    {
        public string Name = "";
        public string StreamName = "";
        public uint SourceOffset;
        // Where this module's source offset sits inside the decompressed
        // directory. Dropping a compiled cache means writing a zero there, and
        // walking the directory a second time with a looser parser to find it
        // would be a second parser to keep right.
        public int SourceOffsetPosition = -1;
        public ushort TypeId;
    }

    public sealed class VbaProjectModule
    {
        public string Name = "";
        public VbaModuleKind Kind;
        public uint SourceOffset;
        public byte[] StreamData = new byte[0];
        // The whole of what the module stream holds: the attribute lines the
        // format keeps in front of the code, and then the code.
        public string FullCode = "";
        public string Code = "";
        public Ole2Entry StreamEntry;
    }

    // A module to be put into a project that does not have it yet.
    public sealed class VbaModuleSource
    {
        public string Name;
        public string Code;

        public VbaModuleSource(string name, string code)
        {
            Name = name;
            Code = code;
        }
    }

    // Everything the writer needs to know about the project it is adding to.
    public sealed class VbaProject
    {
        public Ole2File Ole2;
        public int CodePage;
        public Encoding Encoding;
        public string ProjectText = "";
        public byte[] ProjectWmBytes = new byte[0];
        public byte[] DirDecompressed = new byte[0];
        public int ProjectModulesOffset = -1;
        public Ole2Entry ProjectEntry;
        public Ole2Entry ProjectWmEntry;
        public Ole2Entry DirEntry;
        public Ole2Entry VbaStorage;
        public List<VbaProjectModule> Modules = new List<VbaProjectModule>();
        public List<VbaDirRecord> DirRecords = new List<VbaDirRecord>();

        public VbaProjectModule Find(string name)
        {
            for (int index = 0; index < Modules.Count; index++)
            {
                if (String.Equals(Modules[index].Name, name, StringComparison.OrdinalIgnoreCase)) return Modules[index];
            }
            return null;
        }
    }

    // Reading a vbaProject.bin.
    //
    // A VBA project says the same thing in three places: PROJECT lists the
    // modules as text, PROJECTwm lists their names again in two encodings, and
    // dir records them as binary with the offset at which each module's source
    // starts inside its own stream. Adding a module means appending to all
    // three, so all three are read and all three have to agree. Where they do
    // not, this stops and says so: a project written from a half understood one
    // is a workbook Excel refuses to open, and finding that out at the operator
    // is worse than finding it out here.
    public static class VbaProjectReader
    {
        public static VbaProject Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            VbaProject project = new VbaProject();
            project.Ole2 = Ole2File.Parse(bytes);
            Ole2File ole2 = project.Ole2;

            project.VbaStorage = ole2.FindChild(ole2.RootEntry, "VBA", 1);
            project.ProjectEntry = ole2.FindChild(ole2.RootEntry, "PROJECT", 2);
            project.ProjectWmEntry = ole2.FindChild(ole2.RootEntry, "PROJECTwm", 2);
            if (project.VbaStorage == null) throw new InvalidDataException("The workbook has no VBA storage.");
            if (project.ProjectEntry == null) throw new InvalidDataException("The workbook has no PROJECT stream.");
            if (project.ProjectWmEntry == null) throw new InvalidDataException("The workbook has no PROJECTwm stream.");
            project.DirEntry = ole2.FindChild(project.VbaStorage, "dir", 2);
            if (project.DirEntry == null) throw new InvalidDataException("The workbook has no VBA dir stream.");

            project.DirDecompressed = VbaCompress.Decompress(ole2.ReadStream(project.DirEntry));
            int codePage;
            int modulesOffset;
            project.DirRecords = ReadDirRecords(project.DirDecompressed, out codePage, out modulesOffset);
            project.CodePage = codePage;
            project.ProjectModulesOffset = modulesOffset;
            project.Encoding = StrictEncoding(codePage);

            project.ProjectText = project.Encoding.GetString(ole2.ReadStream(project.ProjectEntry));
            project.ProjectWmBytes = ole2.ReadStream(project.ProjectWmEntry);
            Dictionary<string, VbaModuleKind> declared = ReadProjectTypes(project.ProjectText);
            List<string> wmNames = ReadProjectWmNames(project.ProjectWmBytes, project.Encoding);

            if (declared.Count != project.DirRecords.Count)
            {
                throw new InvalidDataException("PROJECT lists " + declared.Count.ToString(CultureInfo.InvariantCulture) +
                    " modules and dir records " + project.DirRecords.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            if (wmNames.Count != project.DirRecords.Count) throw new InvalidDataException("PROJECTwm and dir do not name the same modules.");
            for (int index = 0; index < project.DirRecords.Count; index++)
            {
                if (!String.Equals(wmNames[index], project.DirRecords[index].Name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("PROJECTwm and dir disagree about a module name: " + project.DirRecords[index].Name);
                }
            }

            Dictionary<int, bool> usedStreams = new Dictionary<int, bool>();
            for (int index = 0; index < project.DirRecords.Count; index++)
            {
                VbaDirRecord record = project.DirRecords[index];
                VbaModuleKind kind;
                if (!declared.TryGetValue(record.Name, out kind))
                {
                    throw new InvalidDataException("dir records a module PROJECT does not list: " + record.Name);
                }
                ushort expected = kind == VbaModuleKind.Standard ? (ushort)0x0021 : (ushort)0x0022;
                if (record.TypeId != expected) throw new InvalidDataException("PROJECT and dir disagree about what kind of module " + record.Name + " is.");
                project.Modules.Add(ReadModule(project, record, kind, usedStreams));
            }
            return project;
        }

        private static VbaProjectModule ReadModule(VbaProject project, VbaDirRecord record, VbaModuleKind kind, Dictionary<int, bool> usedStreams)
        {
            string streamName = String.IsNullOrEmpty(record.StreamName) ? record.Name : record.StreamName;
            Ole2Entry entry = project.Ole2.FindChild(project.VbaStorage, streamName, 2);
            if (entry == null) throw new InvalidDataException("The module stream is missing: " + record.Name);
            if (usedStreams.ContainsKey(entry.Id)) throw new InvalidDataException("Two modules share one stream: " + record.Name);
            usedStreams.Add(entry.Id, true);

            byte[] streamData = project.Ole2.ReadStream(entry);
            if (record.SourceOffset > Int32.MaxValue || record.SourceOffset >= (uint)streamData.Length)
            {
                throw new InvalidDataException("The recorded start of the source is outside the stream of " + record.Name + ".");
            }
            byte[] sourceBytes = VbaCompress.Decompress(Tail(streamData, (int)record.SourceOffset));
            string fullCode = project.Encoding.GetString(sourceBytes);
            VbaProjectModule module = new VbaProjectModule();
            module.Name = record.Name;
            module.Kind = kind;
            module.SourceOffset = record.SourceOffset;
            module.StreamData = streamData;
            module.FullCode = fullCode;
            module.Code = fullCode.Substring(AttributeHeaderLength(fullCode));
            module.StreamEntry = entry;
            return module;
        }

        private static byte[] Tail(byte[] data, int offset)
        {
            byte[] result = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, result, 0, result.Length);
            return result;
        }

        // The attribute lines the format keeps in front of a module's code. They
        // are part of the stream and not part of what anybody wrote, so they are
        // measured rather than guessed at.
        public static int AttributeHeaderLength(string text)
        {
            int position = 0;
            int headerEnd = 0;
            while (position < text.Length)
            {
                int contentEnd = position;
                while (contentEnd < text.Length && text[contentEnd] != '\r' && text[contentEnd] != '\n') contentEnd++;
                if (!IsAttributeLine(text.Substring(position, contentEnd - position))) break;
                int next = contentEnd;
                if (next < text.Length && text[next] == '\r')
                {
                    next++;
                    if (next < text.Length && text[next] == '\n') next++;
                }
                else if (next < text.Length && text[next] == '\n')
                {
                    next++;
                }
                headerEnd = next;
                position = next;
            }
            return headerEnd;
        }

        private static bool IsAttributeLine(string line)
        {
            int position = 0;
            while (position < line.Length && (line[position] == ' ' || line[position] == '\t')) position++;
            return line.IndexOf("Attribute VB_", position, StringComparison.OrdinalIgnoreCase) == position;
        }

        public static Dictionary<string, VbaModuleKind> ReadProjectTypes(string projectText)
        {
            Dictionary<string, VbaModuleKind> result = new Dictionary<string, VbaModuleKind>(StringComparer.OrdinalIgnoreCase);
            string[] lines = projectText.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.StartsWith("Document=", StringComparison.Ordinal))
                {
                    string value = line.Substring("Document=".Length);
                    int slash = value.IndexOf('/');
                    if (slash >= 0) value = value.Substring(0, slash);
                    AddType(result, value, VbaModuleKind.Document);
                }
                else if (line.StartsWith("BaseClass=", StringComparison.Ordinal))
                {
                    AddType(result, line.Substring("BaseClass=".Length), VbaModuleKind.Form);
                }
                else if (line.StartsWith("Module=", StringComparison.Ordinal))
                {
                    AddType(result, line.Substring("Module=".Length), VbaModuleKind.Standard);
                }
                else if (line.StartsWith("Class=", StringComparison.Ordinal))
                {
                    AddType(result, line.Substring("Class=".Length), VbaModuleKind.Class);
                }
            }
            return result;
        }

        private static void AddType(Dictionary<string, VbaModuleKind> types, string name, VbaModuleKind kind)
        {
            if (name.Length == 0) throw new InvalidDataException("PROJECT names a module with no name.");
            if (types.ContainsKey(name)) throw new InvalidDataException("PROJECT names one module twice: " + name);
            types.Add(name, kind);
        }

        // PROJECTwm is every module name twice - once in the project's code page
        // and once in UTF-16 - and then two zero bytes. Adding a module appends
        // to it, so it has to be understood exactly rather than approximately.
        public static List<string> ReadProjectWmNames(byte[] data, Encoding encoding)
        {
            if (data.Length < 2 || data[data.Length - 2] != 0 || data[data.Length - 1] != 0)
            {
                throw new InvalidDataException("PROJECTwm does not end the way it must.");
            }
            List<string> result = new List<string>();
            int limit = data.Length - 2;
            int position = 0;
            while (position < limit)
            {
                int ansiStart = position;
                while (position < limit && data[position] != 0) position++;
                if (position == ansiStart || position >= limit) throw new InvalidDataException("PROJECTwm holds a name that is not terminated.");
                string ansiName = encoding.GetString(data, ansiStart, position - ansiStart);
                position++;
                int wideStart = position;
                while (position + 1 < limit && (data[position] != 0 || data[position + 1] != 0)) position += 2;
                if (position == wideStart || position + 1 >= limit || ((position - wideStart) & 1) != 0)
                {
                    throw new InvalidDataException("PROJECTwm holds a wide name that is not terminated.");
                }
                string wideName = Encoding.Unicode.GetString(data, wideStart, position - wideStart);
                position += 2;
                if (!String.Equals(ansiName, wideName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("PROJECTwm spells one module two different ways: " + wideName);
                }
                result.Add(wideName);
            }
            if (position != limit) throw new InvalidDataException("PROJECTwm does not divide into names.");
            return result;
        }

        // dir is a sequence of identified records rather than a document with a
        // table of contents, so the module list is found by looking for the
        // record that starts it. More than one place in the stream can look like
        // that start, so every candidate is parsed and exactly one has to hold:
        // taking the first would be reading a coincidence as the project.
        public static List<VbaDirRecord> ReadDirRecords(byte[] dir, out int codePage, out int modulesOffset)
        {
            List<int> offsets = new List<int>();
            for (int position = 0; position + 16 <= dir.Length; position++)
            {
                if (Ole2File.ReadUInt16(dir, position) != 0x000F) continue;
                if (Ole2File.ReadUInt32(dir, position + 2) != 2) continue;
                if (Ole2File.ReadUInt16(dir, position + 8) != 0x0013) continue;
                if (Ole2File.ReadUInt32(dir, position + 10) != 2) continue;
                offsets.Add(position);
            }
            if (offsets.Count == 0) throw new InvalidDataException("dir has no module list.");

            int successes = 0;
            int chosenCodePage = 0;
            int chosenOffset = -1;
            List<VbaDirRecord> chosen = null;
            InvalidDataException lastFailure = null;
            for (int index = 0; index < offsets.Count; index++)
            {
                List<int> codePages = CodePageCandidates(dir, offsets[index]);
                for (int page = 0; page < codePages.Count; page++)
                {
                    try
                    {
                        List<VbaDirRecord> records = ParseDirRecords(dir, offsets[index], StrictEncoding(codePages[page]));
                        successes++;
                        if (successes == 1)
                        {
                            chosenCodePage = codePages[page];
                            chosenOffset = offsets[index];
                            chosen = records;
                        }
                    }
                    catch (InvalidDataException failure)
                    {
                        lastFailure = failure;
                    }
                    catch (DecoderFallbackException)
                    {
                        lastFailure = new InvalidDataException("dir does not read as the code page it declares.");
                    }
                }
            }
            if (successes > 1) throw new InvalidDataException("dir can be read more than one way, so which one is the project is unknown.");
            if (successes == 0)
            {
                if (lastFailure != null) throw lastFailure;
                throw new InvalidDataException("dir has no module list that reads.");
            }
            codePage = chosenCodePage;
            modulesOffset = chosenOffset;
            return chosen;
        }

        private static List<int> CodePageCandidates(byte[] dir, int modulesOffset)
        {
            List<int> result = new List<int>();
            for (int position = 0; position + 8 <= modulesOffset; position++)
            {
                if (Ole2File.ReadUInt16(dir, position) == 0x0003 && Ole2File.ReadUInt32(dir, position + 2) == 2)
                {
                    result.Add(Ole2File.ReadUInt16(dir, position + 6));
                }
            }
            if (result.Count == 0) throw new InvalidDataException("dir does not say which code page it is written in.");
            return result;
        }

        private static List<VbaDirRecord> ParseDirRecords(byte[] dir, int modulesOffset, Encoding encoding)
        {
            int position = modulesOffset;
            RequireId(dir, ref position, 0x000F, "the module count");
            if (ReadUInt32At(dir, ref position) != 2) throw new InvalidDataException("The module count record is the wrong size.");
            ushort count = ReadUInt16At(dir, ref position);
            RequireId(dir, ref position, 0x0013, "the project cookie");
            if (ReadUInt32At(dir, ref position) != 2) throw new InvalidDataException("The project cookie record is the wrong size.");
            ReadUInt16At(dir, ref position);

            List<VbaDirRecord> records = new List<VbaDirRecord>();
            for (int index = 0; index < count; index++) records.Add(ParseDirRecord(dir, ref position, encoding));
            RequireId(dir, ref position, 0x0010, "the end of the module list");
            ReadUInt32At(dir, ref position);
            if (position != dir.Length) throw new InvalidDataException("dir carries data after the end of the module list.");
            return records;
        }

        private static VbaDirRecord ParseDirRecord(byte[] dir, ref int position, Encoding encoding)
        {
            string name = encoding.GetString(ReadSized(dir, ref position, 0x0019, "a module name"));
            string wideName = "";
            if (PeekId(dir, position) == 0x0047)
            {
                byte[] wide = ReadSized(dir, ref position, 0x0047, "a wide module name");
                if ((wide.Length & 1) != 0) throw new InvalidDataException("A wide module name has an odd length.");
                wideName = Encoding.Unicode.GetString(wide);
            }
            RequireId(dir, ref position, 0x001A, "a module stream name");
            string streamName = encoding.GetString(ReadBytes(dir, ref position, "a module stream name"));
            RequireId(dir, ref position, 0x0032, "a wide module stream name");
            byte[] wideStream = ReadBytes(dir, ref position, "a wide module stream name");
            if ((wideStream.Length & 1) != 0) throw new InvalidDataException("A wide module stream name has an odd length.");
            string wideStreamName = Encoding.Unicode.GetString(wideStream);
            RequireId(dir, ref position, 0x001C, "a module description");
            ReadBytes(dir, ref position, "a module description");
            RequireId(dir, ref position, 0x0048, "a wide module description");
            byte[] wideDescription = ReadBytes(dir, ref position, "a wide module description");
            if ((wideDescription.Length & 1) != 0) throw new InvalidDataException("A wide module description has an odd length.");

            RequireId(dir, ref position, 0x0031, "the start of a module's source");
            if (ReadUInt32At(dir, ref position) != 4) throw new InvalidDataException("The source offset record is the wrong size.");
            int sourceOffsetPosition = position;
            uint sourceOffset = ReadUInt32At(dir, ref position);

            SkipFixed(dir, ref position, 0x001E, 4, "a module help context");
            SkipFixed(dir, ref position, 0x002C, 2, "a module cookie");
            ushort typeId = ReadUInt16At(dir, ref position);
            if (typeId != 0x0021 && typeId != 0x0022) throw new InvalidDataException("A module does not say what kind of module it is.");
            Skip(dir, ref position, 4, "a module type");
            while (PeekId(dir, position) == 0x0025 || PeekId(dir, position) == 0x0028)
            {
                ReadUInt16At(dir, ref position);
                Skip(dir, ref position, 4, "a module flag");
            }
            RequireId(dir, ref position, 0x002B, "the end of a module record");
            Skip(dir, ref position, 4, "the end of a module record");

            if (name.Length == 0 || streamName.Length == 0) throw new InvalidDataException("A module record has no name.");
            if (wideName.Length > 0 && !String.Equals(name, wideName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A module name is spelled two different ways: " + name);
            }
            if (wideStreamName.Length > 0 && !String.Equals(streamName, wideStreamName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A module stream name is spelled two different ways: " + streamName);
            }

            VbaDirRecord record = new VbaDirRecord();
            record.Name = wideName.Length > 0 ? wideName : name;
            record.StreamName = wideStreamName.Length > 0 ? wideStreamName : streamName;
            record.SourceOffset = sourceOffset;
            record.SourceOffsetPosition = sourceOffsetPosition;
            record.TypeId = typeId;
            return record;
        }

        public static Encoding StrictEncoding(int codePage)
        {
            try
            {
                return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("This machine has no code page " + codePage.ToString(CultureInfo.InvariantCulture) + ", which the VBA project is written in.");
            }
            catch (NotSupportedException)
            {
                throw new InvalidDataException("This machine has no code page " + codePage.ToString(CultureInfo.InvariantCulture) + ", which the VBA project is written in.");
            }
        }

        private static int PeekId(byte[] dir, int position)
        {
            if (position + 2 > dir.Length) return -1;
            return Ole2File.ReadUInt16(dir, position);
        }

        private static void RequireId(byte[] dir, ref int position, ushort id, string label)
        {
            if (ReadUInt16At(dir, ref position) != id) throw new InvalidDataException("dir does not hold " + label + " where it must.");
        }

        private static byte[] ReadSized(byte[] dir, ref int position, ushort id, string label)
        {
            RequireId(dir, ref position, id, label);
            return ReadBytes(dir, ref position, label);
        }

        private static byte[] ReadBytes(byte[] dir, ref int position, string label)
        {
            uint length = ReadUInt32At(dir, ref position);
            if (length > (uint)(dir.Length - position)) throw new InvalidDataException("dir says " + label + " is longer than what is left of it.");
            byte[] result = new byte[(int)length];
            Buffer.BlockCopy(dir, position, result, 0, result.Length);
            position += result.Length;
            return result;
        }

        private static void SkipFixed(byte[] dir, ref int position, ushort id, uint size, string label)
        {
            RequireId(dir, ref position, id, label);
            if (ReadUInt32At(dir, ref position) != size) throw new InvalidDataException("dir records " + label + " at the wrong size.");
            Skip(dir, ref position, (int)size, label);
        }

        private static void Skip(byte[] dir, ref int position, int count, string label)
        {
            if (position + count > dir.Length) throw new InvalidDataException("dir ends in the middle of " + label + ".");
            position += count;
        }

        private static ushort ReadUInt16At(byte[] dir, ref int position)
        {
            ushort value = Ole2File.ReadUInt16(dir, position);
            position += 2;
            return value;
        }

        private static uint ReadUInt32At(byte[] dir, ref int position)
        {
            uint value = Ole2File.ReadUInt32(dir, position);
            position += 4;
            return value;
        }
    }

    // Putting modules into a project.
    //
    // Only additions. This product never rewrites a module that is already in
    // the seed - the five it writes are all new - so there is no path here for
    // replacing existing code, and there is nothing to get wrong about one.
    public static class VbaProjectWriter
    {
        // A project whose modules Excel has compiled keeps that compiled form in
        // three places, and Excel runs it in preference to the source whenever
        // it believes the cache is its own. A project where some modules carry a
        // cache and others do not is the one shape Excel refuses to open, and
        // added modules can never carry one because there is no VBA compiler
        // here. So all three go together: every module keeps only its source,
        // dir agrees the source starts at zero, and the project stamp is set to
        // the value that means "nothing here is compiled". Any two without the
        // third produce a workbook that opens wrong or does not open at all.
        private const ushort UncompiledProjectVersion = 0xFFFF;

        public static byte[] AddModules(VbaProject project, IList<VbaModuleSource> additions)
        {
            if (project == null) throw new ArgumentNullException("project");
            if (additions == null) throw new ArgumentNullException("additions");
            if (project.ProjectModulesOffset < 0) throw new InvalidDataException("The module list in dir was not located, so no module can be added.");

            Dictionary<int, byte[]> changes = new Dictionary<int, byte[]>();
            byte[] dirBytes = DropCompiledState(project, changes);
            string projectText = project.ProjectText;
            byte[] projectWmBytes = project.ProjectWmBytes;
            List<Ole2Addition> streams = new List<Ole2Addition>();

            Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < project.Modules.Count; index++) names[project.Modules[index].Name] = true;

            for (int index = 0; index < additions.Count; index++)
            {
                VbaModuleSource addition = additions[index];
                if (addition == null || addition.Code == null) throw new InvalidDataException("A module was to be added with no code.");
                ValidateModuleName(addition.Name);
                if (names.ContainsKey(addition.Name) || project.Ole2.FindChild(project.VbaStorage, addition.Name, 2) != null)
                {
                    throw new InvalidDataException("The workbook already has a module called " + addition.Name + ".");
                }
                names[addition.Name] = true;
                streams.Add(new Ole2Addition(project.VbaStorage.Id, addition.Name,
                    VbaCompress.Compress(Write(project, FullCode(addition.Name, addition.Code)))));
                dirBytes = AddDirRecord(project, dirBytes, addition.Name, project.Modules.Count + index);
                projectText = AddProjectLine(projectText, addition.Name);
                projectWmBytes = AddProjectWmName(project, projectWmBytes, addition.Name);
            }

            changes.Add(project.DirEntry.Id, VbaCompress.Compress(dirBytes));
            changes.Add(project.ProjectEntry.Id, Write(project, projectText));
            changes.Add(project.ProjectWmEntry.Id, projectWmBytes);
            return Ole2Writer.Rebuild(project.Ole2, changes, streams);
        }

        // The attribute line the format keeps in front of a module's code.
        //
        // A .bas file carries its own - that line is part of what makes it a
        // module rather than a fragment - so text that already declares this
        // module goes in as it stands, and the line is added only to text that
        // has none. Adding a second one would put an attribute line into the
        // code somebody reads.
        //
        // Text that declares a different module stops the build. The workbook
        // would file the code under the name in the attribute while the module
        // list, the editor and every call in the workflow used the other one.
        public static string FullCode(string name, string code)
        {
            ValidateModuleName(name);
            if (code == null) throw new ArgumentNullException("code");
            string declared = DeclaredName(code);
            if (declared == null) return "Attribute VB_Name = \"" + name + "\"\r\n" + code;
            if (!String.Equals(declared, name, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The " + name + " module declares itself as " + declared +
                    ", so the workbook would hold it under a name nothing calls.");
            }
            return code;
        }

        // The module name the text declares for itself, or null when it
        // declares none. Only the attribute lines at the very top are read: an
        // Attribute line further down is not a header, and a string in the code
        // that happens to look like one is not either.
        public static string DeclaredName(string code)
        {
            string header = code.Substring(0, VbaProjectReader.AttributeHeaderLength(code));
            string[] lines = header.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (!line.StartsWith("Attribute VB_Name", StringComparison.OrdinalIgnoreCase)) continue;
                int equals = line.IndexOf('=');
                if (equals < 0) continue;
                string value = line.Substring(equals + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                {
                    return value.Substring(1, value.Length - 2);
                }
            }
            return null;
        }

        public static void ValidateModuleName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 31 || !Char.IsLetter(name[0]))
            {
                throw new InvalidDataException("This is not a name a VBA module can have: " + (name == null ? "" : name));
            }
            for (int index = 1; index < name.Length; index++)
            {
                if (!Char.IsLetterOrDigit(name[index]) && name[index] != '_')
                {
                    throw new InvalidDataException("This is not a name a VBA module can have: " + name);
                }
            }
        }

        // Reading tolerates a byte the declared code page cannot map. Writing
        // must not: a character that cannot be encoded would reach the workbook
        // as a question mark, and the automation would aim at something other
        // than what is on screen. It stops instead, and says which character.
        private static byte[] Write(VbaProject project, string text)
        {
            try
            {
                return project.Encoding.GetBytes(text);
            }
            catch (EncoderFallbackException failure)
            {
                throw new InvalidDataException("A VBA project is written in one code page, and code page " +
                    project.CodePage.ToString(CultureInfo.InvariantCulture) + " has no room for " +
                    Describe(failure.CharUnknown) + ". Nothing was built, because the workbook would have carried a different character from the one on screen.");
            }
        }

        private static string Describe(char value)
        {
            return "U+" + ((int)value).ToString("X4", CultureInfo.InvariantCulture);
        }

        private static byte[] DropCompiledState(VbaProject project, Dictionary<int, byte[]> changes)
        {
            Dictionary<string, VbaDirRecord> byName = new Dictionary<string, VbaDirRecord>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < project.DirRecords.Count; index++)
            {
                VbaDirRecord record = project.DirRecords[index];
                if (record.SourceOffsetPosition < 0 || record.SourceOffsetPosition + 4 > project.DirDecompressed.Length) continue;
                if (byName.ContainsKey(record.Name)) continue;
                byName.Add(record.Name, record);
            }

            for (int index = 0; index < project.Modules.Count; index++)
            {
                VbaProjectModule module = project.Modules[index];
                if (module.SourceOffset == 0) continue;
                if (!byName.ContainsKey(module.Name))
                {
                    throw new InvalidDataException("dir does not record where the source of " + module.Name +
                        " starts, so its compiled form cannot be dropped safely.");
                }
                int prefix = (int)module.SourceOffset;
                byte[] sourceOnly = new byte[module.StreamData.Length - prefix];
                Buffer.BlockCopy(module.StreamData, prefix, sourceOnly, 0, sourceOnly.Length);
                changes.Add(module.StreamEntry.Id, sourceOnly);
            }

            byte[] dirBytes = new byte[project.DirDecompressed.Length];
            Buffer.BlockCopy(project.DirDecompressed, 0, dirBytes, 0, dirBytes.Length);
            foreach (KeyValuePair<string, VbaDirRecord> pair in byName)
            {
                Ole2Writer.WriteUInt32(dirBytes, pair.Value.SourceOffsetPosition, 0);
            }

            bool stamped = false;
            for (int index = 0; index < project.VbaStorage.Children.Count; index++)
            {
                Ole2Entry child = project.Ole2.Entries[project.VbaStorage.Children[index]];
                if (child.ObjectType != 2 || changes.ContainsKey(child.Id)) continue;
                if (child.Name.StartsWith("__SRP_", StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(child.Id, new byte[0]);
                    continue;
                }
                if (String.Equals(child.Name, "_VBA_PROJECT", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] stamp = project.Ole2.ReadStream(child);
                    if (stamp.Length < 4) throw new InvalidDataException("The project stamp stream is too short to be one.");
                    byte[] copy = new byte[stamp.Length];
                    Buffer.BlockCopy(stamp, 0, copy, 0, stamp.Length);
                    copy[2] = (byte)(UncompiledProjectVersion & 0xFF);
                    copy[3] = (byte)((UncompiledProjectVersion >> 8) & 0xFF);
                    changes.Add(child.Id, copy);
                    stamped = true;
                }
            }
            // Without the stamp the other two are worse than doing nothing:
            // Excel would believe a compiled state that is no longer there.
            if (!stamped) throw new InvalidDataException("The workbook has no _VBA_PROJECT stream, so it cannot be told that nothing in it is compiled.");
            return dirBytes;
        }

        private static byte[] AddDirRecord(VbaProject project, byte[] dir, string name, int expectedCount)
        {
            int countOffset = project.ProjectModulesOffset + 6;
            if (countOffset + 2 > dir.Length) throw new InvalidDataException("The module count is not where dir says it is.");
            ushort count = Ole2File.ReadUInt16(dir, countOffset);
            if (count != expectedCount) throw new InvalidDataException("dir counts a different number of modules from the project.");
            if (count == UInt16.MaxValue) throw new InvalidDataException("The workbook already holds as many modules as a VBA project can.");
            int terminator = dir.Length - 6;
            if (terminator < 0 || Ole2File.ReadUInt16(dir, terminator) != 0x0010) throw new InvalidDataException("dir does not end with the record it must end with.");

            byte[] record = BuildDirRecord(project, name);
            byte[] result = new byte[dir.Length + record.Length];
            Buffer.BlockCopy(dir, 0, result, 0, terminator);
            Buffer.BlockCopy(record, 0, result, terminator, record.Length);
            Buffer.BlockCopy(dir, terminator, result, terminator + record.Length, 6);
            result[countOffset] = (byte)((count + 1) & 0xFF);
            result[countOffset + 1] = (byte)(((count + 1) >> 8) & 0xFF);
            return result;
        }

        private static byte[] BuildDirRecord(VbaProject project, string name)
        {
            byte[] ansi = Write(project, name);
            byte[] wide = Encoding.Unicode.GetBytes(name);
            using (MemoryStream output = new MemoryStream())
            {
                Sized(output, 0x0019, ansi);
                Sized(output, 0x0047, wide);
                Sized(output, 0x001A, ansi);
                Sized(output, 0x0032, wide);
                Sized(output, 0x001C, new byte[0]);
                Sized(output, 0x0048, new byte[0]);
                // The source starts at byte zero, because a module written here
                // is source and nothing else.
                UInt16At(output, 0x0031);
                UInt32At(output, 4);
                UInt32At(output, 0);
                UInt16At(output, 0x001E);
                UInt32At(output, 4);
                UInt32At(output, 0);
                UInt16At(output, 0x002C);
                UInt32At(output, 2);
                UInt16At(output, 0xFFFF);
                UInt16At(output, 0x0021);
                UInt32At(output, 0);
                UInt16At(output, 0x002B);
                UInt32At(output, 0);
                return output.ToArray();
            }
        }

        private static string AddProjectLine(string text, string name)
        {
            string newLine = FirstNewLine(text);
            int position = 0;
            int lastModuleEnd = -1;
            int lastDeclarationEnd = -1;
            while (position < text.Length)
            {
                int contentEnd = position;
                while (contentEnd < text.Length && text[contentEnd] != '\r' && text[contentEnd] != '\n') contentEnd++;
                int next = contentEnd;
                if (next < text.Length && text[next] == '\r')
                {
                    next++;
                    if (next < text.Length && text[next] == '\n') next++;
                }
                else if (next < text.Length && text[next] == '\n')
                {
                    next++;
                }
                string line = text.Substring(position, contentEnd - position);
                if (line.StartsWith("Module=", StringComparison.Ordinal)) lastModuleEnd = next;
                if (IsDeclaration(line)) lastDeclarationEnd = next;
                position = next;
            }
            int insertion = lastModuleEnd >= 0 ? lastModuleEnd : lastDeclarationEnd;
            if (insertion < 0) throw new InvalidDataException("PROJECT lists no modules, so there is nowhere to add one.");
            string added = "Module=" + name + newLine;
            if (insertion > 0 && text[insertion - 1] != '\r' && text[insertion - 1] != '\n') added = newLine + added;
            return text.Insert(insertion, added);
        }

        private static bool IsDeclaration(string line)
        {
            return line.StartsWith("Document=", StringComparison.Ordinal) ||
                line.StartsWith("BaseClass=", StringComparison.Ordinal) ||
                line.StartsWith("Module=", StringComparison.Ordinal) ||
                line.StartsWith("Class=", StringComparison.Ordinal);
        }

        private static string FirstNewLine(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r') return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
                if (text[index] == '\n') return "\n";
            }
            return "\r\n";
        }

        private static byte[] AddProjectWmName(VbaProject project, byte[] source, string name)
        {
            if (source.Length < 2 || source[source.Length - 2] != 0 || source[source.Length - 1] != 0)
            {
                throw new InvalidDataException("PROJECTwm does not end the way it must.");
            }
            byte[] ansi = Write(project, name);
            byte[] wide = Encoding.Unicode.GetBytes(name);
            int added = ansi.Length + 1 + wide.Length + 2;
            byte[] result = new byte[source.Length + added];
            int insertion = source.Length - 2;
            Buffer.BlockCopy(source, 0, result, 0, insertion);
            Buffer.BlockCopy(ansi, 0, result, insertion, ansi.Length);
            Buffer.BlockCopy(wide, 0, result, insertion + ansi.Length + 1, wide.Length);
            Buffer.BlockCopy(source, insertion, result, insertion + added, 2);
            return result;
        }

        private static void Sized(Stream output, ushort id, byte[] data)
        {
            UInt16At(output, id);
            UInt32At(output, (uint)data.Length);
            output.Write(data, 0, data.Length);
        }

        private static void UInt16At(Stream output, ushort value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void UInt32At(Stream output, uint value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)((value >> 8) & 0xFF));
            output.WriteByte((byte)((value >> 16) & 0xFF));
            output.WriteByte((byte)((value >> 24) & 0xFF));
        }
    }
}
