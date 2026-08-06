namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class BuildResult
    {
        public bool Ok;
        // The single file somebody is given. Empty when nothing was produced.
        public string Path = "";
        public string Method = "";
        public string Problem;
        // What went into it, so the screen can say what was folded together
        // rather than only that something was.
        public List<string> Modules = new List<string>();
        public long Bytes;
    }

    // Folding the modules into the one file somebody is actually given.
    //
    // Editing happens in modules because that is how a person reads and changes
    // an automation. Handing it over happens in one file because that is how a
    // person receives one. Those are different jobs and this is the seam between
    // them: nothing here changes what the automation does.
    //
    // The PowerShell side becomes a single .cmd. It is a batch file and a
    // PowerShell script and a C# program at the same time: cmd reads the first
    // few ASCII lines at the top and hands the rest to Windows PowerShell, which
    // compiles the C# below it and calls the entry point. Nothing is installed,
    // nothing is unpacked beside it, and no unsigned executable is produced -
    // which is the only shape that satisfies both "no bundled runtime" and "no
    // unsigned EXE in the folder somebody is handed".
    public static class CodeBuild
    {
        // The batch header. cmd parses these lines; PowerShell sees them inside a
        // block comment and skips them.
        //
        // The file is read back with UTF-8 named explicitly. A window title or an
        // element label is often not ASCII, and PowerShell 5.1 reads a file with
        // no byte order mark as ANSI unless it is told otherwise - which would
        // turn what a step aims at into question marks. A byte order mark cannot
        // be used instead: cmd would read it as part of the first line.
        private static string[] BatchHeader()
        {
            return new string[]
            {
                "<# :",
                "@echo off",
                "setlocal",
                "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -STA -Command \"$script = [ScriptBlock]::Create((Get-Content -LiteralPath '%~f0' -Raw -Encoding UTF8)); & $script @args\" %*",
                "set APPSTUDIO_EXIT=%errorlevel%",
                "endlocal & exit /b %APPSTUDIO_EXIT%",
                ": end batch #>"
            };
        }

        // The wrapper. This is the whole of the PowerShell in a built artefact:
        // name the assemblies, compile the engine, call the entry point, and say
        // what happened. Everything the automation actually does is in the C#
        // below it.
        private static string[] Wrapper(bool standalone)
        {
            List<string> lines = new List<string>();
            if (standalone) lines.Add("#requires -Version 5.1");
            lines.Add("[CmdletBinding()]");
            lines.Add("param([int]$SettleMs = 2500)");
            lines.Add("");
            lines.Add("Set-StrictMode -Version 2.0");
            lines.Add("$ErrorActionPreference = 'Stop'");
            lines.Add("");
            lines.Add("# The engine is C#. PowerShell's whole job here is to name the assemblies it");
            lines.Add("# needs, compile it, and call the entry point.");
            lines.Add("$referenceNames = @(");
            lines.Add("    'System',");
            lines.Add("    'System.Core',");
            lines.Add("    'System.Drawing',");
            lines.Add("    'System.Windows.Forms',");
            lines.Add("    'WindowsBase',");
            lines.Add("    'UIAutomationClient',");
            lines.Add("    'UIAutomationTypes'");
            lines.Add(")");
            lines.Add("$references = @()");
            lines.Add("foreach ($referenceName in $referenceNames) {");
            lines.Add("    $assembly = [Reflection.Assembly]::LoadWithPartialName($referenceName)");
            lines.Add("    if ($null -eq $assembly -or [string]::IsNullOrWhiteSpace($assembly.Location)) {");
            lines.Add("        throw ('Required .NET Framework assembly was not found: ' + $referenceName)");
            lines.Add("    }");
            lines.Add("    $references += $assembly.Location");
            lines.Add("}");
            lines.Add("");
            return lines.ToArray();
        }

        private static string[] Tail()
        {
            return new string[]
            {
                "",
                "if ($null -eq ('" + EngineGen.Namespace + "." + CodeModules.Workflow + "' -as [type])) {",
                "    Add-Type -TypeDefinition $engine -Language CSharp -ReferencedAssemblies $references",
                "}",
                "",
                "try {",
                "    [" + EngineGen.Namespace + "." + CodeModules.Workflow + "]::" + EngineGen.EntryPoint + "($SettleMs)",
                "    exit 0",
                "} catch {",
                "    # The reason a run stopped is the innermost one. The layers above it",
                "    # are how it reached here, which nobody asked about.",
                "    $reason = $_.Exception",
                "    while ($null -ne $reason.InnerException) { $reason = $reason.InnerException }",
                "    [Console]::Error.WriteLine($reason.Message)",
                "    exit 1",
                "}"
            };
        }

        // The modules, in reading order, inside the here-string the wrapper
        // compiles. A #region per module keeps the boundary visible, so the one
        // file can still be read - and split again - as the five it was made
        // from.
        private static string Engine(List<CodeFile> modules, List<string> named)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("$engine = @'");
            string[] order = CodeModules.Order();
            List<CodeFile> ordered = new List<CodeFile>();
            for (int index = 0; index < order.Length; index++)
            {
                CodeFile found = Find(modules, order[index]);
                if (found != null) ordered.Add(found);
            }
            // Anything an assistant added that is not one of the five known
            // modules still goes in. Leaving it out would be compiling something
            // other than what is on screen.
            for (int index = 0; index < modules.Count; index++)
            {
                if (CodeModules.Rank(modules[index].Name) < order.Length) continue;
                ordered.Add(modules[index]);
            }
            // Each module is a whole C# file on its own, so each carries its own
            // using directives - which is what makes it readable by itself. C#
            // will not take a using after a namespace, so they are gathered here
            // and stated once at the top. This is the only thing the build does
            // to the text: no module's code is rewritten, only its imports are
            // lifted out of it.
            List<string> imports = new List<string>();
            List<string> bodies = new List<string>();
            for (int index = 0; index < ordered.Count; index++)
            {
                StringBuilder body = new StringBuilder();
                SplitImports(ordered[index].Text, imports, body);
                bodies.Add(body.ToString());
            }
            for (int index = 0; index < imports.Count; index++) text.AppendLine(imports[index]);
            if (imports.Count > 0) text.AppendLine();
            for (int index = 0; index < ordered.Count; index++)
            {
                named.Add(ordered[index].FileName);
                if (index != 0) text.AppendLine();
                text.AppendLine("#region " + ordered[index].FileName);
                text.Append(bodies[index]);
                text.AppendLine("#endregion " + ordered[index].FileName);
            }
            text.AppendLine("'@");
            return text.ToString();
        }

        // The using directives of one module, and what is left of it. A using
        // statement inside a method is not one of these and is left where it is,
        // which is what the brackets rule out.
        private static void SplitImports(string text, List<string> imports, StringBuilder body)
        {
            string[] lines = (text == null ? "" : text).Replace("\r\n", "\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed.StartsWith("using ", StringComparison.Ordinal) &&
                    trimmed.EndsWith(";", StringComparison.Ordinal) &&
                    trimmed.IndexOf('(') < 0 && trimmed.IndexOf('{') < 0)
                {
                    if (!imports.Contains(trimmed)) imports.Add(trimmed);
                    continue;
                }
                body.AppendLine(line);
            }
        }

        private static CodeFile Find(List<CodeFile> modules, string name)
        {
            for (int index = 0; index < modules.Count; index++)
            {
                if (String.Equals(modules[index].Name, name, StringComparison.OrdinalIgnoreCase)) return modules[index];
            }
            return null;
        }

        // A single quoted here-string ends at a line that starts with '@. No C#
        // line does, and a module that somehow held one would end the literal
        // early and compile as something nobody wrote, so it is refused rather
        // than quietly repaired.
        public static string HereStringBreak(List<CodeFile> modules)
        {
            for (int index = 0; index < modules.Count; index++)
            {
                string[] lines = (modules[index].Text == null ? "" : modules[index].Text).Replace("\r\n", "\n").Split('\n');
                for (int line = 0; line < lines.Length; line++)
                {
                    if (lines[line].StartsWith("'@", StringComparison.Ordinal))
                    {
                        return modules[index].FileName + " line " + (line + 1).ToString(CultureInfo.InvariantCulture) +
                            " starts with '@, which ends the literal the engine is carried in. Nothing was built, because the file would have compiled as something other than what is on screen.";
                    }
                }
            }
            return null;
        }

        // The wrapper as a script somebody - or a run - can start directly. Same
        // wrapper, same engine, no batch header: this is what a run uses, so what
        // is verified and what is handed over are compiled from the same text.
        public static string Script(List<CodeFile> modules)
        {
            List<string> named = new List<string>();
            StringBuilder text = new StringBuilder();
            Append(text, Wrapper(true));
            text.Append(Engine(modules, named));
            Append(text, Tail());
            return text.ToString();
        }

        // The same wrapper and the same engine, compiled and then not run. A
        // check that compiled something other than what a run compiles would be
        // checking the wrong thing.
        public static string CheckScript(List<CodeFile> modules)
        {
            List<string> named = new List<string>();
            StringBuilder text = new StringBuilder();
            Append(text, Wrapper(true));
            text.Append(Engine(modules, named));
            Append(text, new string[]
            {
                "",
                "Add-Type -TypeDefinition $engine -Language CSharp -ReferencedAssemblies $references",
                "exit 0"
            });
            return text.ToString();
        }

        // The single file. Everything above, with the batch header on top.
        public static BuildResult BuildPowerShell(List<CodeFile> modules, string folder)
        {
            BuildResult result = new BuildResult();
            result.Method = "one .cmd holding the batch header, the PowerShell wrapper and the C# engine";
            try
            {
                List<CodeFile> mine = OfLanguage(modules, ScriptLanguages.PowerShell);
                if (Find(mine, CodeModules.Workflow) == null)
                {
                    result.Problem = "There is no " + CodeModules.Workflow + " module to start from, so nothing was built.";
                    return result;
                }
                string broken = HereStringBreak(mine);
                if (broken != null)
                {
                    result.Problem = broken;
                    return result;
                }
                List<string> named = new List<string>();
                StringBuilder text = new StringBuilder();
                Append(text, BatchHeader());
                Append(text, Wrapper(false));
                text.Append(Engine(mine, named));
                Append(text, Tail());
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, CodeModules.Workflow + "." + ScriptLanguages.ArtefactExtension(ScriptLanguages.PowerShell));
                // No byte order mark: cmd reads the first line of this file and a
                // mark in front of it is not a comment to cmd.
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                result.Ok = true;
                result.Path = path;
                result.Modules = named;
                result.Bytes = new FileInfo(path).Length;
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        // The VBA side becomes a single .xlsm.
        //
        // A workbook is a binary container rather than text with a header on it,
        // but it does not follow that Excel has to write it. A macro enabled
        // workbook is a zip, and the whole of its VBA project is one part inside
        // that zip. So App Studio ships a seed workbook - a valid, empty VBA
        // project - and the artefact is that seed with the part replaced by the
        // same part with these five modules added to it, byte by byte.
        //
        // Asking Excel to do it meant the operator needed Excel to build at all,
        // and needed "Trust access to the VBA project object model" turned on -
        // a setting whose whole purpose is to stop programs writing macros into
        // workbooks, which is what that build was. Neither is required now. The
        // build runs on a machine with no Office on it.
        //
        // What cannot be worked around is said by name: a seed that is not
        // there or does not hold, a module name VBA will not take, a character
        // the project's code page cannot carry. None of those is repaired
        // quietly, because a workbook that opens holding something other than
        // what is on screen is worse than a build that stopped.
        public static BuildResult BuildVba(List<CodeFile> modules, string folder)
        {
            BuildResult result = new BuildResult();
            result.Method = VbaWorkbook.Method;
            try
            {
                List<CodeFile> mine = OfLanguage(modules, ScriptLanguages.Vba);
                if (Find(mine, CodeModules.Workflow) == null)
                {
                    result.Problem = "There is no " + CodeModules.Workflow + " module to start from, so nothing was built.";
                    return result;
                }
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, CodeModules.Workflow + "." + ScriptLanguages.ArtefactExtension(ScriptLanguages.Vba));
                return VbaWorkbook.Build(VbaWorkbook.SeedPath(), VbaWorkbook.ModulesOf(mine), path);
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        public static List<CodeFile> OfLanguage(List<CodeFile> modules, string language)
        {
            List<CodeFile> mine = new List<CodeFile>();
            if (modules == null) return mine;
            for (int index = 0; index < modules.Count; index++)
            {
                if (String.Equals(modules[index].Language, language, StringComparison.Ordinal)) mine.Add(modules[index]);
            }
            return mine;
        }

        private static void Append(StringBuilder text, string[] lines)
        {
            for (int index = 0; index < lines.Length; index++) text.AppendLine(lines[index]);
        }
    }
}
