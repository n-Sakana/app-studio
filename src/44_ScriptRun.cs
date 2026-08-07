namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;

    public sealed class CheckResult
    {
        public bool Ok;
        public string Headline = "";
        public List<string> Problems = new List<string>();
        // What was actually done, so nobody reads a structural check as a
        // compile that never happened.
        public string Method = "";
    }

    public sealed class RunResult
    {
        public bool Started;
        public bool Ok;
        public int ExitCode = -1;
        public string Output = "";
        public string Problem;
        public string Method = "";
    }

    // Checking and running what is in the editor.
    //
    // Neither language is checked by pretending. PowerShell is parsed by
    // PowerShell itself, in a separate process, so a syntax error is the real
    // one with the real line number. VBA has no compiler on a machine where
    // nothing may be installed, so what happens here is a structural check and
    // says so in every result it returns; running it needs a VBA host and the
    // absence of one is reported rather than worked around.
    public static class ScriptRun
    {
        public static CheckResult Check(string language, string text)
        {
            return Check(language, null, text, CodeModules.Workflow);
        }

        // Which module this is matters for VBA: only the workflow is expected to
        // carry the entry point, and reporting a runtime module as missing one
        // would be reporting a fault that is not there.
        //
        // The engine language has no equivalent question. Its five modules are
        // one program - a workflow on its own is a set of calls into the other
        // four - so the whole automation is compiled and the module on screen is
        // not checked apart from it.
        public static CheckResult Check(string language, List<CodeFile> modules, string text, string moduleName)
        {
            if (String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal)) return CheckVba(text, moduleName);
            return CheckEngine(modules);
        }

        // Compiled by the same C# compiler a run uses, in a separate process, so
        // a mistake is the real one with the real line number. Nothing here
        // re-implements the grammar, and nothing here compiles a different text
        // from the one a run would: the check and the run are assembled by the
        // same builder.
        public static CheckResult CheckEngine(List<CodeFile> modules)
        {
            CheckResult result = new CheckResult();
            result.Method = "compiled by the C# compiler a run uses";
            string folder = null;
            try
            {
                List<CodeFile> mine = CodeBuild.OfLanguage(modules, ScriptLanguages.PowerShell);
                if (mine.Count == 0)
                {
                    result.Problems.Add("There are no modules to compile, so nothing is known about this automation.");
                    result.Headline = "Nothing was checked.";
                    return result;
                }
                string broken = CodeBuild.HereStringBreak(mine);
                if (broken != null)
                {
                    result.Problems.Add(broken);
                    result.Headline = "The engine could not be assembled.";
                    return result;
                }
                folder = TempFolder();
                string path = Path.Combine(folder, "check.ps1");
                // With a byte order mark. PowerShell 5.1 reads a file without one
                // as ANSI, and a window title that is not ASCII would arrive at
                // the compiler as something else.
                File.WriteAllText(path, CodeBuild.CheckScript(mine), new UTF8Encoding(true));
                ProcessResult run = Run(PowerShellPath(), "-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"" + path + "\"", folder, 60000);
                if (run.TimedOut)
                {
                    result.Problems.Add("The compiler did not finish within 60 seconds, so nothing is known about this automation.");
                    result.Headline = "The check did not finish.";
                    return result;
                }
                if (run.ExitCode == 0)
                {
                    result.Ok = true;
                    result.Headline = "The C# compiler accepted all " + mine.Count.ToString(CultureInfo.InvariantCulture) + " modules.";
                    return result;
                }
                string[] lines = (run.Output + run.Error).Replace("\r\n", "\n").Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Trim().Length > 0) result.Problems.Add(lines[index].Trim());
                }
                if (result.Problems.Count == 0) result.Problems.Add("The compiler reported a failure but said nothing about it.");
                result.Headline = result.Problems.Count.ToString(CultureInfo.InvariantCulture) + " problem(s) in the engine.";
                return result;
            }
            catch (Exception exception)
            {
                result.Problems.Add(exception.GetType().Name + ": " + exception.Message);
                result.Headline = "The check could not be carried out.";
                return result;
            }
            finally
            {
                Clean(folder);
            }
        }

        // A structural check, and it is called one everywhere it is shown. It
        // catches the things that stop a module importing at all; it does not
        // and cannot tell you the module is correct.
        public static CheckResult CheckVba(string text)
        {
            return CheckVba(text, CodeModules.Workflow);
        }

        public static CheckResult CheckVba(string text, string moduleName)
        {
            CheckResult result = new CheckResult();
            result.Method = "structural check only - there is no VBA compiler here";
            string body = text == null ? "" : text;
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            int open = 0;
            int conditional = 0;
            bool sawOptionExplicit = false;
            bool sawEntryPoint = false;
            // Exactly one arm of a conditional block reaches the compiler, so a
            // routine declared once per arm is one routine. The count is taken
            // back to where the block started whenever the next arm begins,
            // rather than adding every arm up and reporting a module that never
            // closes.
            List<int> branch = new List<int>();
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                string lower = line.ToLowerInvariant();
                if (lower.StartsWith("'", StringComparison.Ordinal)) continue;
                if (lower.StartsWith("option explicit", StringComparison.Ordinal)) sawOptionExplicit = true;
                if (lower.IndexOf("sub runrecordedprocedure", StringComparison.Ordinal) >= 0) sawEntryPoint = true;
                if (lower.StartsWith("#if", StringComparison.Ordinal))
                {
                    conditional++;
                    branch.Add(open);
                    continue;
                }
                if (lower.StartsWith("#else", StringComparison.Ordinal))
                {
                    if (branch.Count > 0) open = branch[branch.Count - 1];
                    continue;
                }
                if (lower.StartsWith("#end if", StringComparison.Ordinal))
                {
                    conditional--;
                    if (branch.Count > 0) branch.RemoveAt(branch.Count - 1);
                    continue;
                }
                if (IsBlockStart(lower)) open++;
                if (lower.StartsWith("end sub", StringComparison.Ordinal) || lower.StartsWith("end function", StringComparison.Ordinal)) open--;
                if (CountQuotes(line) % 2 != 0)
                {
                    result.Problems.Add("line " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": a string literal is not closed on this line.");
                }
                if (open < 0)
                {
                    result.Problems.Add("line " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": End Sub or End Function without anything open.");
                    open = 0;
                }
            }
            if (open > 0) result.Problems.Add(open.ToString(CultureInfo.InvariantCulture) + " Sub or Function is left open at the end of the module.");
            if (conditional != 0) result.Problems.Add("the #If and #End If lines do not balance.");
            if (!sawOptionExplicit) result.Problems.Add("Option Explicit is missing, so a mistyped name would be a silent empty variable.");
            if (!sawEntryPoint && CodeModules.IsWorkflow(moduleName))
            {
                result.Problems.Add("there is no Sub " + VbaGen.EntryPoint + " to start from.");
            }
            result.Ok = result.Problems.Count == 0;
            result.Headline = result.Ok
                ? "The module is structurally sound. This is not a compile: only a VBA host can tell you it builds."
                : result.Problems.Count.ToString(CultureInfo.InvariantCulture) + " problem(s) in the module structure.";
            return result;
        }

        private static bool IsBlockStart(string lower)
        {
            if (lower.StartsWith("declare ", StringComparison.Ordinal)) return false;
            if (lower.IndexOf("declare ", StringComparison.Ordinal) >= 0 && lower.IndexOf("lib ", StringComparison.Ordinal) >= 0) return false;
            if (lower.StartsWith("sub ", StringComparison.Ordinal)) return true;
            if (lower.StartsWith("function ", StringComparison.Ordinal)) return true;
            if (lower.StartsWith("public sub ", StringComparison.Ordinal)) return true;
            if (lower.StartsWith("public function ", StringComparison.Ordinal)) return true;
            if (lower.StartsWith("private sub ", StringComparison.Ordinal)) return true;
            if (lower.StartsWith("private function ", StringComparison.Ordinal)) return true;
            return false;
        }

        private static int CountQuotes(string line)
        {
            int count = 0;
            for (int index = 0; index < line.Length; index++)
            {
                if (line[index] == '"') count++;
                if (line[index] == '\'' && count % 2 == 0) break;
            }
            return count;
        }

        // Runs the PowerShell in a separate process, exactly the way the
        // operator would run it themselves. It drives real applications, so the
        // caller has to have asked first.
        public static RunResult RunPowerShell(string text, string folder, int timeoutMs)
        {
            RunResult result = new RunResult();
            result.Method = "Windows PowerShell 5.1, separate process";
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "run.ps1");
                File.WriteAllText(path, text == null ? "" : text, new UTF8Encoding(false));
                result.Started = true;
                ProcessResult run = Run(PowerShellPath(), "-NoProfile -ExecutionPolicy Bypass -STA -File \"" + path + "\"", folder, timeoutMs);
                result.ExitCode = run.ExitCode;
                result.Output = (run.Output + (run.Error.Length > 0 ? Environment.NewLine + run.Error : "")).Trim();
                if (run.TimedOut)
                {
                    result.Problem = "The script was still running after " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) +
                        " seconds and was stopped. Whatever it had already done to the application has been done.";
                    return result;
                }
                result.Ok = run.ExitCode == 0;
                if (!result.Ok && result.Output.Length == 0) result.Problem = "The script exited with " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + " and said nothing.";
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        // The whole automation, not one file of it.
        //
        // A workflow on its own is a list of calls to a runtime that is not
        // there. Every module of the language is written into the same folder,
        // and beside them goes the wrapper that compiles them and calls the
        // entry point - the same wrapper the build folds into the .cmd, so a run
        // proves the artefact rather than something adjacent to it.
        public static RunResult RunPowerShellProject(List<CodeFile> modules, string folder, int timeoutMs)
        {
            RunResult result = new RunResult();
            result.Method = "the C# engine, compiled and run by Windows PowerShell 5.1 in a separate process";
            try
            {
                List<CodeFile> mine = CodeBuild.OfLanguage(modules, ScriptLanguages.PowerShell);
                string written = Write(modules, folder, ScriptLanguages.PowerShell);
                if (written == null)
                {
                    result.Problem = "There is no " + CodeModules.Workflow + " module to start from, so nothing was run.";
                    return result;
                }
                string broken = CodeBuild.HereStringBreak(mine);
                if (broken != null)
                {
                    result.Problem = broken;
                    return result;
                }
                string entry = Path.Combine(folder, CodeModules.Workflow + ".ps1");
                File.WriteAllText(entry, CodeBuild.Script(mine), new UTF8Encoding(true));
                result.Started = true;
                // -Console, because this is not a person. The same script opens a
                // window when somebody starts it, and a window is exactly what a
                // run whose whole answer is its captured output and its exit code
                // cannot use: nobody would be there to press the button, and the
                // wait would end in a timeout rather than in a result.
                ProcessResult run = Run(PowerShellPath(), "-NoProfile -ExecutionPolicy Bypass -STA -File \"" + entry + "\" -Console", folder, timeoutMs);
                result.ExitCode = run.ExitCode;
                result.Output = (run.Output + (run.Error.Length > 0 ? Environment.NewLine + run.Error : "")).Trim();
                if (run.TimedOut)
                {
                    result.Problem = "The script was still running after " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) +
                        " seconds and was stopped. Whatever it had already done to the application has been done.";
                    return result;
                }
                result.Ok = run.ExitCode == 0;
                if (!result.Ok && result.Output.Length == 0) result.Problem = "The script exited with " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + " and said nothing.";
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        // Writes every module of one language side by side and answers with the
        // path of the one a run starts from, or null when it is not there.
        public static string Write(List<CodeFile> modules, string folder, string language)
        {
            Directory.CreateDirectory(folder);
            string entry = null;
            if (modules == null) return null;
            for (int index = 0; index < modules.Count; index++)
            {
                CodeFile file = modules[index];
                if (!String.Equals(file.Language, language, StringComparison.Ordinal)) continue;
                string path = Path.Combine(folder, file.FileName);
                File.WriteAllText(path, file.Text == null ? "" : file.Text, new UTF8Encoding(false));
                if (file.IsWorkflow) entry = path;
            }
            return entry;
        }

        // Running VBA needs a VBA host, and Excel is the one that is normally
        // there. It does not need anything else: the workbook is built first,
        // by the same builder that writes the artefact, and Excel is then asked
        // to open that finished workbook and call the procedure in it. Nothing
        // reaches for the VBA project model, so "Trust access to the VBA
        // project object model" is not required to run either - only Excel is,
        // and its absence is reported by name rather than worked around.
        //
        // A VBA host can stop answering - a modal dialog nobody can see, a macro
        // that never returns - and a caller with no ceiling would wait for it
        // for ever. The work runs on its own apartment thread, the wait is
        // bounded, and the host this started is closed if it runs out of time.
        public static RunResult RunVba(List<CodeFile> modules, string folder, string entryPoint, int timeoutMs)
        {
            RunResult carried = null;
            int[] hostProcess = new int[1];
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                carried = RunVbaCore(modules, folder, entryPoint, hostProcess);
            });
            worker.IsBackground = true;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if (worker.Join(timeoutMs) && carried != null) return carried;
            RunResult result = new RunResult();
            result.Method = "the built workbook, opened in Excel as the VBA host";
            result.Started = true;
            result.Problem = "the VBA host did not answer within " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) +
                " seconds. It usually means it is holding a dialog open where nobody can see it. " +
                "The host this started has been closed; whatever the module had already done to the application has been done.";
            Kill(hostProcess[0]);
            return result;
        }

        public static bool WaitForExit(int processId, int timeoutMs)
        {
            if (processId <= 0) return true;
            try
            {
                using (Process host = Process.GetProcessById(processId))
                {
                    return host.WaitForExit(timeoutMs);
                }
            }
            catch
            {
                // Already gone, which is the outcome this was waiting for.
                return true;
            }
        }

        public static void Kill(int processId)
        {
            if (processId <= 0) return;
            try
            {
                using (Process host = Process.GetProcessById(processId))
                {
                    host.Kill();
                }
            }
            catch
            {
            }
        }

        private static RunResult RunVbaCore(List<CodeFile> modules, string folder, string entryPoint, int[] hostProcess)
        {
            RunResult result = new RunResult();
            result.Method = "the built workbook, opened in Excel as the VBA host";
            object excel = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Excel.Application");
                if (type == null)
                {
                    result.Problem = "No VBA host is installed on this machine (Excel.Application is not registered), so this automation cannot be run from here. " +
                        "Build it and open the workbook on a machine that has one.";
                    return result;
                }
                // The same workbook a build hands over, made by the same
                // builder. A run that drove something assembled another way
                // would be proving something adjacent to the artefact rather
                // than the artefact.
                BuildResult built = CodeBuild.BuildVba(modules, folder);
                if (!built.Ok)
                {
                    result.Problem = built.Problem;
                    return result;
                }
                result.Started = true;
                excel = Activator.CreateInstance(type);
                hostProcess[0] = ProcessOf(excel);
                Set(excel, "Visible", false);
                Set(excel, "DisplayAlerts", false);
                object books = Get(excel, "Workbooks");
                object book = Call(books, "Open", built.Path);
                // The module reports into a file rather than onto the screen,
                // because a message box here would be a dialog behind an
                // invisible window with nobody to close it.
                string report = Path.Combine(folder, "vba-result.txt");
                if (File.Exists(report)) File.Delete(report);
                try
                {
                    Call(excel, "Run", VbaGen.ModuleName + "." + entryPoint + "To", report);
                }
                catch (Exception exception)
                {
                    result.Problem = "The workbook opened but " + entryPoint + " stopped: " + Innermost(exception);
                    return result;
                }
                finally
                {
                    try { Call(book, "Close", false); }
                    catch { }
                }
                if (!File.Exists(report))
                {
                    result.Problem = "The workbook opened and " + entryPoint + " was called but nothing was written, so what it did is unknown. " +
                        "A machine whose policy refuses to run macros at all answers this way.";
                    return result;
                }
                string[] lines = File.ReadAllLines(report);
                string state = lines.Length > 0 ? lines[0].Trim() : "";
                string detail = lines.Length > 1 ? String.Join(" ", lines, 1, lines.Length - 1).Trim() : "";
                if (!String.Equals(state, "done", StringComparison.Ordinal))
                {
                    result.Problem = detail.Length > 0 ? detail : "The automation stopped without saying why.";
                    return result;
                }
                result.Ok = true;
                result.ExitCode = 0;
                result.Output = "The built workbook was opened and " + entryPoint + " ran to the end.";
                return result;
            }
            catch (Exception exception)
            {
                result.Problem = exception.GetType().Name + ": " + Innermost(exception);
                return result;
            }
            finally
            {
                if (excel != null)
                {
                    try { Set(excel, "DisplayAlerts", false); }
                    catch { }
                    try { Call(excel, "Quit"); }
                    catch { }
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(excel); }
                    catch { }
                    // Asking a host to quit is not the same fact as the host
                    // being gone. A copy left running would hold the workbook
                    // this opened and would still be there tomorrow, so the one
                    // this started is waited for and then closed.
                    if (!WaitForExit(hostProcess[0], 5000)) Kill(hostProcess[0]);
                }
            }
        }

        // Which process this instance of the host actually is, so a host that
        // has to be closed is the one this started and not somebody's open
        // workbook.
        public static int ProcessOf(object application)
        {
            try
            {
                object handle = Get(application, "Hwnd");
                long value = Convert.ToInt64(handle, CultureInfo.InvariantCulture);
                if (value == 0) return 0;
                return WindowTools.ProcessIdOf(value);
            }
            catch
            {
                return 0;
            }
        }

        public static string Innermost(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null) current = current.InnerException;
            return current.Message;
        }

        public static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);
        }

        public static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, target, new object[] { value });
        }

        public static object Call(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, args);
        }

        public static string PowerShellPath()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windows, "System32\\WindowsPowerShell\\v1.0\\powershell.exe");
        }

        private sealed class ProcessResult
        {
            public int ExitCode = -1;
            public string Output = "";
            public string Error = "";
            public bool TimedOut;
        }

        private static ProcessResult Run(string file, string arguments, string workingDirectory, int timeoutMs)
        {
            ProcessResult result = new ProcessResult();
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = file;
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            if (!String.IsNullOrEmpty(workingDirectory)) start.WorkingDirectory = workingDirectory;
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) output.AppendLine(args.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) error.AppendLine(args.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    try { process.Kill(); }
                    catch { }
                    process.WaitForExit(3000);
                }
                result.ExitCode = result.TimedOut ? -1 : process.ExitCode;
            }
            result.Output = output.ToString();
            result.Error = error.ToString();
            return result;
        }

        private static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "app-studio-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void Clean(string folder)
        {
            if (String.IsNullOrEmpty(folder)) return;
            try { Directory.Delete(folder, true); }
            catch { }
        }
    }
}
