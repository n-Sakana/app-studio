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
            if (String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal)) return CheckVba(text);
            return CheckPowerShell(text);
        }

        // Parsed by Windows PowerShell 5.1 itself. Nothing here re-implements
        // its grammar.
        public static CheckResult CheckPowerShell(string text)
        {
            CheckResult result = new CheckResult();
            result.Method = "parsed by Windows PowerShell 5.1";
            string folder = null;
            try
            {
                folder = TempFolder();
                string path = Path.Combine(folder, "check.ps1");
                File.WriteAllText(path, text == null ? "" : text, new UTF8Encoding(false));
                string command =
                    "$errors = $null; " +
                    "[void][System.Management.Automation.Language.Parser]::ParseFile('" + path.Replace("'", "''") + "', [ref]$null, [ref]$errors); " +
                    "if ($errors -and $errors.Count -gt 0) { foreach ($e in $errors) { Write-Output ('line ' + $e.Extent.StartLineNumber + ': ' + $e.Message) }; exit 1 } else { exit 0 }";
                ProcessResult run = Run(PowerShellPath(), "-NoProfile -ExecutionPolicy Bypass -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"", null, 30000);
                if (run.TimedOut)
                {
                    result.Problems.Add("The parser did not finish within 30 seconds, so nothing is known about this script.");
                    result.Headline = "The check did not finish.";
                    return result;
                }
                if (run.ExitCode == 0)
                {
                    result.Ok = true;
                    result.Headline = "PowerShell parsed this without an error.";
                    return result;
                }
                string[] lines = (run.Output + run.Error).Replace("\r\n", "\n").Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Trim().Length > 0) result.Problems.Add(lines[index].Trim());
                }
                if (result.Problems.Count == 0) result.Problems.Add("The parser reported a failure but said nothing about it.");
                result.Headline = result.Problems.Count.ToString(CultureInfo.InvariantCulture) + " problem(s) in the PowerShell.";
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
            if (!sawEntryPoint) result.Problems.Add("there is no Sub RunRecordedProcedure to start from.");
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

        // Running VBA needs a VBA host. Excel is the one that is normally
        // there, and it will only accept a module when the operator has trusted
        // access to the VBA project model. Every one of those conditions is
        // reported by name when it is not met; none of them is quietly worked
        // around, and a check that could not be carried out is never returned
        // as a pass.
        // A VBA host can stop answering - a modal dialog nobody can see, a macro
        // that never returns - and a caller with no ceiling would wait for it
        // for ever. The work runs on its own apartment thread, the wait is
        // bounded, and the host this started is closed if it runs out of time.
        public static RunResult RunVba(string text, string folder, string entryPoint, int timeoutMs)
        {
            RunResult carried = null;
            int[] hostProcess = new int[1];
            System.Threading.Thread worker = new System.Threading.Thread(delegate()
            {
                carried = RunVbaCore(text, folder, entryPoint, hostProcess);
            });
            worker.IsBackground = true;
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if (worker.Join(timeoutMs) && carried != null) return carried;
            RunResult result = new RunResult();
            result.Method = "Excel as the VBA host, through late binding";
            result.Started = true;
            result.Problem = "the VBA host did not answer within " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) +
                " seconds. It usually means it is holding a dialog open where nobody can see it. " +
                "The host this started has been closed; whatever the module had already done to the application has been done.";
            Kill(hostProcess[0]);
            return result;
        }

        private static bool WaitForExit(int processId, int timeoutMs)
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

        private static void Kill(int processId)
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

        private static RunResult RunVbaCore(string text, string folder, string entryPoint, int[] hostProcess)
        {
            RunResult result = new RunResult();
            result.Method = "Excel as the VBA host, through late binding";
            object excel = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Excel.Application");
                if (type == null)
                {
                    result.Problem = "No VBA host is installed on this machine (Excel.Application is not registered), so this module cannot be run from here. " +
                        "Import it into a VBA project on a machine that has one.";
                    return result;
                }
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, VbaGen.ModuleName + ".bas");
                File.WriteAllText(path, text == null ? "" : text, new UTF8Encoding(false));
                result.Started = true;
                excel = Activator.CreateInstance(type);
                hostProcess[0] = ProcessOf(excel);
                Set(excel, "Visible", false);
                Set(excel, "DisplayAlerts", false);
                object books = Get(excel, "Workbooks");
                object book = Call(books, "Add");
                object project;
                try
                {
                    project = Get(book, "VBProject");
                }
                catch (Exception)
                {
                    result.Problem = "Excel refused access to the VBA project. Turn on " +
                        "\"Trust access to the VBA project object model\" in Trust Center > Macro Settings, or import the module by hand. " +
                        "Nothing was run.";
                    return result;
                }
                object components = Get(project, "VBComponents");
                Call(components, "Import", path);
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
                    result.Problem = "The module was imported but " + entryPoint + " stopped: " + Innermost(exception);
                    return result;
                }
                if (!File.Exists(report))
                {
                    result.Problem = "The module was imported and run but wrote no result, so what it did is unknown.";
                    return result;
                }
                string[] lines = File.ReadAllLines(report);
                string state = lines.Length > 0 ? lines[0].Trim() : "";
                string detail = lines.Length > 1 ? String.Join(" ", lines, 1, lines.Length - 1).Trim() : "";
                if (!String.Equals(state, "done", StringComparison.Ordinal))
                {
                    result.Problem = detail.Length > 0 ? detail : "The module stopped without saying why.";
                    return result;
                }
                result.Ok = true;
                result.ExitCode = 0;
                result.Output = "The module was imported into a temporary workbook and " + entryPoint + " ran to the end.";
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
                    // this created and would still be there tomorrow, so the
                    // one this started is waited for and then closed.
                    if (!WaitForExit(hostProcess[0], 5000)) Kill(hostProcess[0]);
                }
            }
        }

        // Which process this instance of the host actually is, so a host that
        // has to be closed is the one this started and not somebody's open
        // workbook.
        private static int ProcessOf(object application)
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

        private static string Innermost(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null) current = current.InnerException;
            return current.Message;
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, target, new object[] { value });
        }

        private static object Call(object target, string name, params object[] args)
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
