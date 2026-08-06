namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;

    // What was read out of a workbook, or why it could not be.
    public sealed class VbaBookRead
    {
        public bool Ok;
        public string Problem;
        public VbaProject Project;
        public int VbaProjectBytes;
    }

    // The workbook an automation is handed over as.
    //
    // A macro enabled workbook is a zip holding, among the sheets and the
    // styles, one part that is the VBA project. So a workbook with five modules
    // in it does not have to be written by Excel: it is this product's seed
    // workbook with that one part replaced by the same part with five modules
    // added to it.
    //
    // That is the whole reason this path exists. Asking Excel to write the
    // workbook meant the operator had to have Excel, and had to have turned on
    // "Trust access to the VBA project object model" - a setting that exists to
    // stop programs writing macros into workbooks, which is exactly what that
    // build was. Building here needs neither: no Excel, no trust setting, and
    // nothing installed. Excel is still what runs the result, because running
    // VBA needs a VBA host, but it is not what makes it.
    public static class VbaWorkbook
    {
        // Where the VBA project lives inside a macro enabled workbook. The
        // seed is this product's own, so this is not a guess about the format
        // in general - it is where the seed keeps it.
        public const string PartName = "xl/vbaProject.bin";

        // How the artefact was made, said once so the build and the screen
        // cannot describe it differently.
        public const string Method = "the seed workbook with its VBA project rebuilt around the modules - no Excel, no VBA project trust";

        private static readonly object Sync = new object();
        private static string seedPath;

        // Same shape as Messages.Init: the product knows where it was started
        // from and says so once, rather than every layer working it out again.
        public static void Init(string baseDir)
        {
            lock (Sync)
            {
                seedPath = String.IsNullOrEmpty(baseDir) ? null : Path.Combine(Path.Combine(baseDir, "assets"), "vba", "seed.xlsm");
            }
        }

        public static string SeedPath()
        {
            lock (Sync)
            {
                return seedPath;
            }
        }

        // Reads the VBA project out of a workbook. Used on the seed before
        // anything is built from it, and on the artefact afterwards, so what is
        // checked after a build is the file rather than what the builder
        // believes it did.
        public static VbaBookRead Read(string bookPath)
        {
            VbaBookRead result = new VbaBookRead();
            try
            {
                if (String.IsNullOrEmpty(bookPath) || !File.Exists(bookPath))
                {
                    result.Problem = "There is no workbook at " + (bookPath == null ? "" : bookPath) + ".";
                    return result;
                }
                byte[] part = ReadPart(bookPath);
                if (part == null)
                {
                    result.Problem = "The workbook at " + bookPath + " carries no " + PartName + ", so it holds no VBA project.";
                    return result;
                }
                result.VbaProjectBytes = part.Length;
                result.Project = VbaProjectReader.Read(part);
                result.Ok = true;
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = Say(exception);
                return result;
            }
        }

        // The workbook somebody is given. The seed is copied, its VBA project
        // is replaced by the same project with these modules added, and the
        // result is read back before it is put in place.
        //
        // The copy is built beside the output rather than at it, so a build
        // that fails leaves whatever was there before rather than a workbook
        // that is half of this one.
        public static BuildResult Build(string seed, List<VbaModuleSource> modules, string outputPath)
        {
            BuildResult result = new BuildResult();
            result.Method = Method;
            string working = null;
            try
            {
                if (modules == null || modules.Count == 0)
                {
                    result.Problem = "There are no modules to put into a workbook, so nothing was built.";
                    return result;
                }
                if (String.IsNullOrEmpty(seed))
                {
                    result.Problem = "App Studio does not know where its seed workbook is, so no workbook can be written. It is started from the folder that holds assets/vba/seed.xlsm.";
                    return result;
                }
                if (!File.Exists(seed))
                {
                    result.Problem = "The seed workbook is missing from " + seed + ", so no workbook can be written. It ships with App Studio and is rebuilt by tests/make-vba-seed.ps1.";
                    return result;
                }

                byte[] part = ReadPart(seed);
                if (part == null)
                {
                    result.Problem = "The seed workbook at " + seed + " carries no " + PartName + ", so it is not a seed.";
                    return result;
                }
                VbaProject project = VbaProjectReader.Read(part);
                byte[] rebuilt = VbaProjectWriter.AddModules(project, modules);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                working = outputPath + ".building";
                if (File.Exists(working)) File.Delete(working);
                File.Copy(seed, working);
                WritePart(working, rebuilt);

                string wrong = Verify(working, modules);
                if (wrong != null)
                {
                    result.Problem = wrong;
                    return result;
                }

                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(working, outputPath);
                working = null;
                result.Ok = true;
                result.Path = outputPath;
                result.Bytes = new FileInfo(outputPath).Length;
                for (int index = 0; index < modules.Count; index++) result.Modules.Add(modules[index].Name);
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = Say(exception);
                return result;
            }
            finally
            {
                if (working != null)
                {
                    try { if (File.Exists(working)) File.Delete(working); }
                    catch { }
                }
            }
        }

        // What was written is read back out of the file and compared with what
        // was asked for. A build that agrees only with itself is how a workbook
        // gets handed over holding something other than what was on screen.
        private static string Verify(string bookPath, List<VbaModuleSource> modules)
        {
            byte[] part = ReadPart(bookPath);
            if (part == null) return "The workbook was written but carries no " + PartName + ".";
            VbaProject project = VbaProjectReader.Read(part);
            for (int index = 0; index < modules.Count; index++)
            {
                VbaModuleSource wanted = modules[index];
                VbaProjectModule found = project.Find(wanted.Name);
                if (found == null) return "The workbook was written but " + wanted.Name + " is not in it.";
                if (found.Kind != VbaModuleKind.Standard) return "The workbook was written but " + wanted.Name + " is not a standard module in it.";
                if (found.SourceOffset != 0) return "The workbook was written but " + wanted.Name + " still carries a compiled form in front of its source.";
                if (!String.Equals(Lines(found.FullCode), Lines(VbaProjectWriter.FullCode(wanted.Name, wanted.Code)), StringComparison.Ordinal))
                {
                    return "The workbook was written but the " + wanted.Name + " in it is not the " + wanted.Name + " on screen.";
                }
            }
            return null;
        }

        private static string Lines(string text)
        {
            return (text == null ? "" : text).Replace("\r\n", "\n");
        }

        public static byte[] ReadPart(string bookPath)
        {
            using (FileStream file = new FileStream(bookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry entry = FindPart(archive);
                if (entry == null) return null;
                using (MemoryStream copy = new MemoryStream())
                using (Stream reading = entry.Open())
                {
                    byte[] buffer = new byte[8192];
                    int read = reading.Read(buffer, 0, buffer.Length);
                    while (read > 0)
                    {
                        copy.Write(buffer, 0, read);
                        read = reading.Read(buffer, 0, buffer.Length);
                    }
                    return copy.ToArray();
                }
            }
        }

        private static void WritePart(string bookPath, byte[] data)
        {
            using (FileStream file = new FileStream(bookPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Update, false))
            {
                ZipArchiveEntry entry = FindPart(archive);
                if (entry == null) throw new InvalidDataException("The workbook being written carries no " + PartName + ".");
                using (Stream writing = entry.Open())
                {
                    writing.SetLength(0);
                    writing.Write(data, 0, data.Length);
                    writing.Flush();
                }
            }
        }

        private static ZipArchiveEntry FindPart(ZipArchive archive)
        {
            for (int index = 0; index < archive.Entries.Count; index++)
            {
                if (String.Equals(archive.Entries[index].FullName, PartName, StringComparison.OrdinalIgnoreCase)) return archive.Entries[index];
            }
            return null;
        }

        // A reader that stops says which part of the container did not hold, and
        // that sentence is the useful one. The type name in front of it is not,
        // except when nothing else was said.
        private static string Say(Exception exception)
        {
            Exception innermost = exception;
            while (innermost.InnerException != null) innermost = innermost.InnerException;
            if (innermost is InvalidDataException) return innermost.Message;
            return innermost.GetType().Name + ": " + innermost.Message;
        }

        // The five modules of one language as the workbook writer wants them:
        // the module name it is filed under, and the text that is in it.
        public static List<VbaModuleSource> ModulesOf(List<CodeFile> files)
        {
            List<VbaModuleSource> result = new List<VbaModuleSource>();
            if (files == null) return result;
            string[] order = CodeModules.Order();
            for (int index = 0; index < order.Length; index++)
            {
                CodeFile found = Find(files, order[index]);
                if (found != null) result.Add(new VbaModuleSource(found.Name, found.Text == null ? "" : found.Text));
            }
            // Anything an assistant added that is not one of the five known
            // modules goes in too. Leaving it out would hand over a workbook
            // holding something other than what is on screen.
            for (int index = 0; index < files.Count; index++)
            {
                if (CodeModules.Rank(files[index].Name) < order.Length) continue;
                result.Add(new VbaModuleSource(files[index].Name, files[index].Text == null ? "" : files[index].Text));
            }
            return result;
        }

        private static CodeFile Find(List<CodeFile> files, string name)
        {
            for (int index = 0; index < files.Count; index++)
            {
                if (String.Equals(files[index].Name, name, StringComparison.OrdinalIgnoreCase)) return files[index];
            }
            return null;
        }
    }
}
