namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    // The third shape of the window: the recording as code.
    //
    // It opens with the recording already written out as something that runs.
    // PowerShell and VBA are the same size on this screen, in the same place,
    // with the same buttons; neither is the real one with the other offered as
    // an export.
    //
    // Talking to an assistant is part of this screen rather than a mode of its
    // own. One button copies the request, one button takes an answer in, and
    // what comes back is shown as a difference before anything is replaced.
    public sealed class CodeScreen
    {
        private readonly Window owner;
        private readonly StudioSession session;
        private readonly CodeProject project;
        private readonly Action<string, string> say;
        private readonly Func<bool> askRunConsent;

        private readonly TextBox editor = new TextBox();
        private readonly TextBox requestBox = new TextBox();
        private readonly TextBlock stateLine = new TextBlock();
        private readonly TextBlock languageNote = new TextBlock();
        private readonly TextBlock intakeLine = new TextBlock();
        private readonly StackPanel diffHost = new StackPanel();
        private readonly ComboBox fileBox = new ComboBox();
        private readonly Button copyButton = new Button();
        private Button psButton;
        private Button vbaButton;

        private HandoffResult handoff;
        private int nextChunk;
        private IntakeParts parts = new IntakeParts();
        private List<IntakeFile> pending;
        private List<FileDiff> pendingDiff;
        private string currentFile = CodeProject.GeneratedName;
        private bool loading;
        private Grid root;
        // Nothing is written back from the editor until the editor has been
        // filled at least once. Without this the first pass writes an empty box
        // over the code that was just generated, and the screen opens blank.
        private bool ready;

        public CodeScreen(Window ownerWindow, StudioSession studioSession, CodeProject codeProject,
            Action<string, string> status, Func<bool> runConsent)
        {
            owner = ownerWindow;
            session = studioSession;
            project = codeProject;
            say = status;
            askRunConsent = runConsent;
        }

        public CodeProject Project { get { return project; } }

        // There is no save button here, the same way there is none for a
        // recording. Whatever is in the editor is written when the screen is
        // left, so leaving it and coming back - or closing the window - cannot
        // lose what was typed.
        public void Persist()
        {
            Remember();
            if (!ready) return;
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        private static string Text(string name, string fallback)
        {
            return Messages.Text(name, fallback);
        }

        // The body is built once and handed back on every later visit. WPF
        // refuses to give an element a second parent, so it is taken out of the
        // tree it was in first; rebuilding it instead would throw away whatever
        // the operator had typed and not yet checked.
        public UIElement Build()
        {
            if (root != null)
            {
                Detach(root);
                return root;
            }
            root = BuildBody();
            return root;
        }

        private static void Detach(UIElement child)
        {
            DependencyObject parent = System.Windows.LogicalTreeHelper.GetParent(child);
            Panel panel = parent as Panel;
            if (panel != null)
            {
                panel.Children.Remove(child);
                return;
            }
            Decorator decorator = parent as Decorator;
            if (decorator != null)
            {
                decorator.Child = null;
                return;
            }
            ContentControl holder = parent as ContentControl;
            if (holder != null) holder.Content = null;
        }

        private Grid BuildBody()
        {
            Grid body = new Grid();
            body.RowDefinitions.Add(AutoRow());
            body.RowDefinitions.Add(StarRow());
            body.RowDefinitions.Add(AutoRow());
            body.Margin = new Thickness(Theme.Space5, Theme.Space4, Theme.Space5, Theme.Space4);

            UIElement head = Head();
            Grid.SetRow(head, 0);
            body.Children.Add(head);

            UIElement editorCard = Editor();
            Grid.SetRow(editorCard, 1);
            body.Children.Add(editorCard);

            // The editor may never be squeezed out of existence by whatever the
            // assistant sent back. The editor keeps a floor and the assistant
            // keeps a ceiling and scrolls inside it, so both are always on
            // screen whatever arrives.
            body.RowDefinitions[1].MinHeight = 200;
            ScrollViewer assistantScroll = new ScrollViewer();
            assistantScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            assistantScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            assistantScroll.MaxHeight = 340;
            assistantScroll.Content = Assistant();
            Grid.SetRow(assistantScroll, 2);
            body.Children.Add(assistantScroll);

            ShowLanguage(project.Language);
            return body;
        }

        // ---------- the top: what this is, in which language, and what can be done to it ----------

        private UIElement Head()
        {
            StackPanel stack = new StackPanel();

            DockPanel line = new DockPanel();
            line.LastChildFill = true;
            StackPanel switcher = new StackPanel();
            switcher.Orientation = Orientation.Horizontal;
            psButton = LanguageButton(Text("code-lang-ps.txt", "PowerShell"), ScriptLanguages.PowerShell);
            vbaButton = LanguageButton(Text("code-lang-vba.txt", "VBA"), ScriptLanguages.Vba);
            switcher.Children.Add(psButton);
            switcher.Children.Add(vbaButton);
            DockPanel.SetDock(switcher, Dock.Left);
            line.Children.Add(switcher);

            stateLine.FontSize = Theme.BodySize;
            stateLine.FontWeight = FontWeights.SemiBold;
            stateLine.Foreground = Theme.Text;
            stateLine.VerticalAlignment = VerticalAlignment.Center;
            stateLine.Margin = new Thickness(Theme.Space4, 0, 0, 0);
            stateLine.TextWrapping = TextWrapping.Wrap;
            line.Children.Add(stateLine);
            stack.Children.Add(line);

            languageNote.FontSize = Theme.MetaSize;
            languageNote.Foreground = Theme.TextMuted;
            languageNote.TextWrapping = TextWrapping.Wrap;
            languageNote.Margin = new Thickness(0, Theme.Space2, 0, 0);
            stack.Children.Add(languageNote);

            WrapPanel actions = new WrapPanel();
            actions.Margin = new Thickness(0, Theme.Space3, 0, 0);
            Button(actions, Text("code-check.txt", "Check"), delegate { Check(); }, true);
            Button(actions, Text("code-run.txt", "Run"), delegate { RunIt(); }, false);
            Button(actions, Text("code-baseline.txt", "Back to the generated version"), delegate { Baseline(); }, false);
            Button(actions, Text("code-undo.txt", "Undo the last change taken in"), delegate { Undo(); }, false);
            Button(actions, Text("code-folder.txt", "Code folder"), delegate { Open(project.Folder); }, false);
            stack.Children.Add(actions);

            return Card(stack);
        }

        private Button LanguageButton(string label, string language)
        {
            Button button = new Button();
            button.Content = label;
            button.MinWidth = 132;
            button.Margin = new Thickness(0, 0, Theme.Space2, 0);
            button.Click += delegate { ShowLanguage(language); };
            return button;
        }

        // Neither language is styled as the lesser one. The one on screen is
        // shown as chosen, the other as available, and nothing else differs.
        private void PaintSwitcher()
        {
            bool ps = String.Equals(project.Language, ScriptLanguages.PowerShell, StringComparison.Ordinal);
            psButton.SetResourceReference(FrameworkElement.StyleProperty, ps ? "AppButtonPrimary" : "AppButtonCompact");
            vbaButton.SetResourceReference(FrameworkElement.StyleProperty, ps ? "AppButtonCompact" : "AppButtonPrimary");
        }

        // ---------- the editor ----------

        private UIElement Editor()
        {
            Grid grid = new Grid();
            grid.RowDefinitions.Add(AutoRow());
            grid.RowDefinitions.Add(StarRow());

            DockPanel header = new DockPanel();
            header.Margin = new Thickness(0, 0, 0, Theme.Space2);
            fileBox.SetResourceReference(FrameworkElement.StyleProperty, "AppComboBox");
            fileBox.Width = 320;
            fileBox.HorizontalAlignment = HorizontalAlignment.Left;
            fileBox.SelectionChanged += delegate
            {
                if (loading) return;
                ComboBoxItem item = fileBox.SelectedItem as ComboBoxItem;
                if (item == null) return;
                Remember();
                currentFile = Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
                LoadEditor();
            };
            DockPanel.SetDock(fileBox, Dock.Left);
            header.Children.Add(fileBox);
            grid.Children.Add(header);

            editor.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            editor.FontFamily = Theme.CodeFont;
            editor.FontSize = Theme.MetaSize;
            editor.AcceptsReturn = true;
            editor.AcceptsTab = true;
            editor.TextWrapping = TextWrapping.NoWrap;
            editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            editor.VerticalContentAlignment = VerticalAlignment.Top;
            editor.MinHeight = 240;
            editor.TextChanged += delegate
            {
                if (loading) return;
                Remember();
                PaintState();
            };
            System.Windows.Automation.AutomationProperties.SetName(editor, Text("code-editor-name.txt", "The automation, as code"));
            Grid.SetRow(editor, 1);
            grid.Children.Add(editor);

            return Card(grid);
        }

        // ---------- the assistant ----------

        private UIElement Assistant()
        {
            StackPanel stack = new StackPanel();
            stack.Children.Add(Heading(Text("code-ai-title.txt", "Ask an assistant")));
            stack.Children.Add(Note(Text("code-ai-note.txt",
                "One text and one picture document go out. The text carries the request, the recording, the ledger and the code as it stands.")));

            requestBox.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            requestBox.AcceptsReturn = true;
            requestBox.TextWrapping = TextWrapping.Wrap;
            requestBox.Height = 56;
            requestBox.Margin = new Thickness(0, Theme.Space2, 0, 0);
            requestBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            requestBox.Text = Text("code-ai-request-default.txt",
                "Make this run reliably against the recorded application. Keep every safety rule in section 3.");
            System.Windows.Automation.AutomationProperties.SetName(requestBox, Text("code-ai-request-name.txt", "What to ask for"));
            stack.Children.Add(requestBox);

            WrapPanel row = new WrapPanel();
            row.Margin = new Thickness(0, Theme.Space3, 0, 0);
            copyButton.Content = Text("code-ai-copy.txt", "Copy the request");
            copyButton.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonPrimary");
            copyButton.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            copyButton.Click += delegate { CopyRequest(); };
            row.Children.Add(copyButton);
            Button(row, Text("code-ai-paste.txt", "Take the answer in from the clipboard"), delegate { TakeIn(); }, false);
            Button(row, Text("code-ai-restart.txt", "Start the intake again"), delegate { RestartIntake(); }, false);
            Button(row, Text("code-ai-pdf.txt", "The picture document"), delegate { Open(session == null ? null : session.ScreensPdfPath); }, false);
            stack.Children.Add(row);

            intakeLine.FontSize = Theme.MetaSize;
            intakeLine.TextWrapping = TextWrapping.Wrap;
            intakeLine.Foreground = Theme.TextMuted;
            intakeLine.Margin = new Thickness(0, Theme.Space1, 0, 0);
            stack.Children.Add(intakeLine);

            diffHost.Margin = new Thickness(0, Theme.Space2, 0, 0);
            stack.Children.Add(diffHost);

            return Card(stack);
        }

        // ---------- language and file state ----------

        // Redraws from what the project now holds. Switching language keeps
        // whatever was typed, but putting a different version in place must not:
        // the editor is still showing the old text at that moment, and writing
        // it back would undo the change that was just made.
        private void Refresh()
        {
            ready = false;
            ShowLanguage(project.Language);
        }

        private void ShowLanguage(string language)
        {
            Remember();
            project.Language = language;
            PaintSwitcher();
            loading = true;
            fileBox.Items.Clear();
            List<CodeFile> files = project.Files(language);
            int selected = 0;
            for (int index = 0; index < files.Count; index++)
            {
                ComboBoxItem item = new ComboBoxItem();
                item.Content = files[index].FileName;
                item.Tag = files[index].Name;
                fileBox.Items.Add(item);
                if (String.Equals(files[index].Name, currentFile, StringComparison.OrdinalIgnoreCase)) selected = index;
            }
            if (fileBox.Items.Count > 0)
            {
                fileBox.SelectedIndex = selected;
                ComboBoxItem chosen = fileBox.SelectedItem as ComboBoxItem;
                if (chosen != null) currentFile = Convert.ToString(chosen.Tag, CultureInfo.InvariantCulture);
            }
            fileBox.Visibility = fileBox.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            loading = false;
            LoadEditor();
        }

        private void LoadEditor()
        {
            loading = true;
            CodeFile file = project.Find(project.Language, currentFile);
            editor.Text = file == null ? "" : file.Text;
            loading = false;
            ready = true;
            PaintState();
        }

        private void Remember()
        {
            if (loading || !ready) return;
            project.SetText(project.Language, currentFile, editor.Text);
        }

        private void PaintState()
        {
            bool changed = project.DiffersFromBaseline(project.Language);
            stateLine.Text = changed
                ? Text("code-state-edited.txt", "Edited since it was generated from the recording.")
                : Text("code-state-generated.txt", "Exactly as it was generated from the recording.");
            stateLine.Foreground = changed ? Theme.CautionText : Theme.TextSub;
            StringBuilder note = new StringBuilder();
            note.Append(Text("code-files.txt", "files / lines")).Append(": ").Append(project.Summary(project.Language));
            if (project.Plan != null)
            {
                if (project.Plan.Unsupported > 0)
                {
                    note.Append("   ").Append(Text("code-unsupported.txt", "steps with no address that survives a restart"))
                        .Append(": ").Append(project.Plan.Unsupported.ToString(CultureInfo.InvariantCulture));
                }
                if (String.Equals(project.Language, ScriptLanguages.Vba, StringComparison.Ordinal) && project.Plan.UnreachableFromVba > 0)
                {
                    note.Append("   ").Append(Text("code-vba-unreachable.txt", "steps VBA has no Win32 address for"))
                        .Append(": ").Append(project.Plan.UnreachableFromVba.ToString(CultureInfo.InvariantCulture));
                }
                if (project.Plan.SecretCount > 0)
                {
                    note.Append("   ").Append(Text("code-secrets.txt", "steps that ask the operator for a value"))
                        .Append(": ").Append(project.Plan.SecretCount.ToString(CultureInfo.InvariantCulture));
                }
            }
            languageNote.Text = note.ToString();
        }

        // ---------- check and run ----------

        private void Check()
        {
            Remember();
            string language = project.Language;
            string text = editor.Text;
            Say(Text("code-checking.txt", "Checking..."), null);
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                CheckResult result = ScriptRun.Check(language, text);
                owner.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ShowCheck(result);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowCheck(CheckResult result)
        {
            StringBuilder text = new StringBuilder();
            text.Append(result.Headline).Append("  (").Append(result.Method).Append(")");
            for (int index = 0; index < result.Problems.Count && index < 8; index++)
            {
                text.Append(Environment.NewLine).Append("- ").Append(result.Problems[index]);
            }
            if (result.Problems.Count > 8)
            {
                text.Append(Environment.NewLine).Append("- ...")
                    .Append((result.Problems.Count - 8).ToString(CultureInfo.InvariantCulture));
            }
            intakeLine.Text = text.ToString();
            intakeLine.Foreground = result.Ok ? Theme.SuccessText : Theme.DangerText;
            Say(result.Headline, result.Ok ? "Success" : "Danger");
        }

        private void RunIt()
        {
            Remember();
            if (askRunConsent != null && !askRunConsent())
            {
                Say(Text("code-run-declined.txt", "The script was not started."), "Caution");
                return;
            }
            string language = project.Language;
            string text = editor.Text;
            string folder = Path.Combine(project.Folder == null ? Path.GetTempPath() : project.Folder, "run");
            Say(Text("code-running.txt", "Running..."), null);
            System.Threading.Thread work = new System.Threading.Thread(delegate()
            {
                RunResult result = String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal)
                    ? ScriptRun.RunVba(text, folder, "RunRecordedProcedure", 180000)
                    : ScriptRun.RunPowerShell(text, folder, 180000);
                owner.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ShowRun(result);
                }));
            });
            work.IsBackground = true;
            work.SetApartmentState(System.Threading.ApartmentState.STA);
            work.Start();
        }

        private void ShowRun(RunResult result)
        {
            StringBuilder text = new StringBuilder();
            if (!result.Started)
            {
                text.Append(Text("code-run-nostart.txt", "It was not run.")).Append("  ").Append(result.Problem);
            }
            else if (result.Problem != null)
            {
                text.Append(Text("code-run-stopped.txt", "It stopped.")).Append("  ").Append(result.Problem);
            }
            else
            {
                text.Append(result.Ok
                    ? Text("code-run-done.txt", "It ran to the end.")
                    : Text("code-run-failed.txt", "It ended with a failure."));
                text.Append("  (").Append(result.Method).Append(", exit ")
                    .Append(result.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(")");
            }
            if (result.Output.Length > 0)
            {
                string output = result.Output.Length > 1200 ? result.Output.Substring(0, 1200) + " ..." : result.Output;
                text.Append(Environment.NewLine).Append(output);
            }
            intakeLine.Text = text.ToString();
            bool good = result.Started && result.Problem == null && result.Ok;
            intakeLine.Foreground = good ? Theme.SuccessText : Theme.DangerText;
            Say(good ? Text("code-run-done.txt", "It ran to the end.") : Text("code-run-failed.txt", "It ended with a failure."),
                good ? "Success" : "Danger");
        }

        // ---------- versions ----------

        private void Baseline()
        {
            Remember();
            project.RestoreBaseline(project.Language);
            Save();
            Refresh();
            Say(Text("code-baseline-done.txt", "The generated version is back. The version before it can still be brought back."), "Success");
        }

        private void Undo()
        {
            Remember();
            if (!project.UndoApply())
            {
                Say(Text("code-undo-none.txt", "There is nothing to undo: nothing has been taken in yet."), "Caution");
                return;
            }
            Save();
            Refresh();
            Say(Text("code-undo-done.txt", "The version from before the last change is back."), "Success");
        }

        private void Save()
        {
            string problem = project.Save();
            if (problem != null) Say(Text("code-save-failed.txt", "The code folder could not be written") + ": " + problem, "Danger");
        }

        // ---------- out to the assistant ----------

        private void CopyRequest()
        {
            Remember();
            // Copying again re-copies the same request. Changing the id here
            // would quietly invalidate an answer the operator is already
            // waiting for; a new request is started deliberately, with the
            // button that says so.
            if (handoff == null)
            {
                project.RequestId = Handoff.NewRequestId();
                handoff = Handoff.Build(session, project, requestBox.Text, project.RequestId);
                Handoff.Write(project, handoff);
                parts = new IntakeParts();
                nextChunk = 0;
                Save();
            }
            if (nextChunk >= handoff.Chunks.Count) nextChunk = 0;
            string chunk = handoff.Chunks[nextChunk];
            try
            {
                Clipboard.SetText(chunk);
            }
            catch (Exception exception)
            {
                Say(Text("code-copy-failed.txt", "The clipboard refused the request") + ": " + exception.Message, "Danger");
                return;
            }
            nextChunk++;
            PaintCopy();
            if (handoff.Split)
            {
                Say(Text("code-copy-part.txt", "Part copied. Paste it, then press this again for the next one.") +
                    "  " + nextChunk.ToString(CultureInfo.InvariantCulture) + " / " +
                    handoff.Chunks.Count.ToString(CultureInfo.InvariantCulture), "Success");
                return;
            }
            Say(Text("code-copy-done.txt", "The request is on the clipboard. Attach the picture document with it."), "Success");
        }

        private void PaintCopy()
        {
            if (handoff == null || !handoff.Split)
            {
                copyButton.Content = Text("code-ai-copy.txt", "Copy the request");
                return;
            }
            if (nextChunk >= handoff.Chunks.Count)
            {
                copyButton.Content = Text("code-ai-copy-again.txt", "Copy the request again from the start");
                return;
            }
            copyButton.Content = Text("code-ai-copy-next.txt", "Copy part") + " " +
                (nextChunk + 1).ToString(CultureInfo.InvariantCulture) + " / " +
                handoff.Chunks.Count.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- back in from the assistant ----------

        private void RestartIntake()
        {
            parts = new IntakeParts();
            pending = null;
            pendingDiff = null;
            // Starting the intake again means the next copy is a new request.
            // This is the one place the id is deliberately let go of.
            handoff = null;
            nextChunk = 0;
            PaintCopy();
            diffHost.Children.Clear();
            intakeLine.Text = Text("code-intake-restart.txt", "The intake starts again. Nothing on screen was changed.");
            intakeLine.Foreground = Theme.TextMuted;
            Say(intakeLine.Text, "Caution");
        }

        private void TakeIn()
        {
            Remember();
            if (String.IsNullOrEmpty(project.RequestId))
            {
                Say(Text("code-intake-norequest.txt", "Copy the request first: an answer is only accepted against the request it was asked for."), "Caution");
                return;
            }
            string pasted;
            try
            {
                pasted = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception exception)
            {
                Say(Text("code-paste-failed.txt", "The clipboard could not be read") + ": " + exception.Message, "Danger");
                return;
            }
            IntakeResult parsed = Intake.Parse(pasted, project.RequestId);
            if (!parsed.Ok)
            {
                Refused(parsed);
                return;
            }
            if (parsed.HasPart)
            {
                IntakeResult added = Intake.AddPart(parts, parsed);
                if (!added.Ok)
                {
                    Refused(added);
                    return;
                }
                if (!parts.Complete)
                {
                    intakeLine.Text = Text("code-intake-partial.txt", "Part taken in. Still needed") + ": " + parts.MissingText();
                    intakeLine.Foreground = Theme.CautionText;
                    Say(intakeLine.Text, "Caution");
                    return;
                }
                parsed = Intake.Merge(parts);
                if (!parsed.Ok)
                {
                    Refused(parsed);
                    return;
                }
            }
            if (parsed.NoChange != null)
            {
                intakeLine.Text = Refusal(parsed.NoChange) + Environment.NewLine + parsed.Summary;
                intakeLine.Foreground = Theme.CautionText;
                diffHost.Children.Clear();
                Say(Refusal(parsed.NoChange), "Caution");
                return;
            }
            pending = parsed.Files;
            pendingDiff = Diff.Compare(project, pending);
            ShowDiff(parsed.Summary);
        }

        private string Refusal(string verdict)
        {
            if (String.Equals(verdict, "UNNECESSARY", StringComparison.Ordinal))
            {
                return Text("code-nochange-unnecessary.txt", "The assistant answered that nothing needs changing. Nothing on screen was touched.");
            }
            if (String.Equals(verdict, "IMPOSSIBLE", StringComparison.Ordinal))
            {
                return Text("code-nochange-impossible.txt", "The assistant answered that this cannot be done by changing these files. Nothing on screen was touched.");
            }
            return Text("code-nochange-unclear.txt", "The assistant answered that the request cannot be settled from what it was given. Nothing on screen was touched.");
        }

        private void Refused(IntakeResult result)
        {
            intakeLine.Text = Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message +
                "  [" + result.Reason + "]";
            intakeLine.Foreground = Theme.DangerText;
            diffHost.Children.Clear();
            Say(Text("code-intake-refused.txt", "The answer was not taken in.") + " " + result.Message, "Danger");
        }

        // Nothing is replaced until this has been read and accepted.
        private void ShowDiff(string summary)
        {
            diffHost.Children.Clear();
            int changed = 0;
            for (int index = 0; index < pendingDiff.Count; index++) if (pendingDiff[index].Changed) changed++;
            StringBuilder head = new StringBuilder();
            head.Append(Text("code-intake-ready.txt", "An answer is ready to be looked at.")).Append("  ")
                .Append(pendingDiff.Count.ToString(CultureInfo.InvariantCulture)).Append(" ")
                .Append(Text("code-intake-files.txt", "file(s)")).Append(", ")
                .Append(changed.ToString(CultureInfo.InvariantCulture)).Append(" ")
                .Append(Text("code-intake-changed.txt", "changed"));
            intakeLine.Text = head.ToString() + (String.IsNullOrEmpty(summary) ? "" : Environment.NewLine + summary);
            intakeLine.Foreground = Theme.Text;
            // The status line still holds whatever happened last. Leaving it
            // there while a difference is waiting puts two answers on one
            // screen, and the older one is the wrong one.
            Say(head.ToString(), null);

            for (int index = 0; index < pendingDiff.Count; index++)
            {
                FileDiff diff = pendingDiff[index];
                int hidden;
                List<DiffLine> lines = Diff.Interesting(diff, out hidden);
                StackPanel rows = new StackPanel();
                for (int line = 0; line < lines.Count && line < 400; line++)
                {
                    rows.Children.Add(DiffRow(lines[line]));
                }
                if (lines.Count > 400)
                {
                    rows.Children.Add(Note(Text("code-diff-more.txt", "further changed lines are not shown here") + ": " +
                        (lines.Count - 400).ToString(CultureInfo.InvariantCulture)));
                }
                if (hidden > 0)
                {
                    rows.Children.Add(Note(Text("code-diff-same.txt", "unchanged lines left out") + ": " +
                        hidden.ToString(CultureInfo.InvariantCulture)));
                }
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.MaxHeight = 180;
                scroll.Content = rows;
                TextBlock summaryBlock;
                Expander fold = Accordion(diff.Language + " / " + diff.Name + "." + ScriptLanguages.Extension(diff.Language) +
                    (diff.IsNew ? "  " + Text("code-diff-new.txt", "(new file)") : ""), scroll, out summaryBlock);
                summaryBlock.Text = diff.Summary;
                fold.IsExpanded = diff.Changed && pendingDiff.Count == 1;
                diffHost.Children.Add(fold);
            }

            WrapPanel row = new WrapPanel();
            row.Margin = new Thickness(0, Theme.Space2, 0, 0);
            Button(row, Text("code-diff-apply.txt", "Take this in"), delegate { ApplyPending(); }, true);
            Button(row, Text("code-diff-drop.txt", "Leave it"), delegate { DropPending(); }, false);
            diffHost.Children.Add(row);
        }

        private UIElement DiffRow(DiffLine line)
        {
            TextBlock block = new TextBlock();
            string mark = line.Kind == DiffLine.Added ? "+ " : (line.Kind == DiffLine.Removed ? "- " : "  ");
            block.Text = mark + line.Text;
            block.FontFamily = Theme.CodeFont;
            block.FontSize = Theme.MicroSize;
            block.TextWrapping = TextWrapping.NoWrap;
            if (line.Kind == DiffLine.Added)
            {
                block.Foreground = Theme.SuccessText;
                block.Background = Theme.SuccessSoft;
            }
            else if (line.Kind == DiffLine.Removed)
            {
                block.Foreground = Theme.DangerText;
                block.Background = Theme.DangerSoft;
            }
            else
            {
                block.Foreground = Theme.TextMuted;
            }
            return block;
        }

        private void ApplyPending()
        {
            if (pending == null) return;
            List<CodeFile> incoming = new List<CodeFile>();
            for (int index = 0; index < pending.Count; index++)
            {
                CodeFile file = new CodeFile();
                file.Language = pending[index].Language;
                file.Name = pending[index].Name;
                file.Text = pending[index].Text;
                incoming.Add(file);
            }
            project.Apply(incoming);
            pending = null;
            pendingDiff = null;
            parts = new IntakeParts();
            diffHost.Children.Clear();
            Save();
            Refresh();
            intakeLine.Text = Text("code-diff-applied.txt", "Taken in. The version from before it can be brought back with the undo button above.");
            intakeLine.Foreground = Theme.SuccessText;
            Say(intakeLine.Text, "Success");
        }

        private void DropPending()
        {
            pending = null;
            pendingDiff = null;
            diffHost.Children.Clear();
            intakeLine.Text = Text("code-diff-dropped.txt", "Left alone. Nothing on screen was changed.");
            intakeLine.Foreground = Theme.TextMuted;
            Say(intakeLine.Text, "Caution");
        }

        // ---------- odds and ends ----------

        private void Open(string path)
        {
            if (String.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                Say(Text("open-missing.txt", "That is not on disk yet") + ": " + (path == null ? "-" : path), "Caution");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception exception)
            {
                Say(Text("open-failed.txt", "It could not be opened") + ": " + exception.Message, "Danger");
            }
        }

        private void Say(string message, string tone)
        {
            if (say != null) say(message, tone);
        }

        private static RowDefinition AutoRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = GridLength.Auto;
            return row;
        }

        private static RowDefinition StarRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = new GridLength(1, GridUnitType.Star);
            return row;
        }

        private static Border Card(UIElement content)
        {
            Border card = new Border();
            card.Background = Theme.Surface;
            card.BorderBrush = Theme.Border;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.Padding = new Thickness(Theme.Space4);
            card.Margin = new Thickness(0, 0, 0, Theme.Space3);
            card.Child = content;
            return card;
        }

        private static TextBlock Heading(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.SectionSize;
            block.FontWeight = FontWeights.Bold;
            block.Foreground = Theme.Text;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        private static TextBlock Note(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.MetaSize;
            block.Foreground = Theme.TextMuted;
            block.TextWrapping = TextWrapping.Wrap;
            block.Margin = new Thickness(0, Theme.Space1, 0, 0);
            return block;
        }

        private static Button Button(Panel panel, string label, RoutedEventHandler handler, bool primary)
        {
            Button button = new Button();
            button.Content = label;
            button.Margin = new Thickness(0, 0, Theme.Space2, Theme.Space2);
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            if (handler != null) button.Click += handler;
            panel.Children.Add(button);
            return button;
        }

        private static Expander Accordion(string caption, UIElement content, out TextBlock summary)
        {
            TextBlock captionBlock = new TextBlock();
            captionBlock.Text = caption;
            captionBlock.FontSize = Theme.LabelSize;
            captionBlock.FontWeight = FontWeights.SemiBold;
            captionBlock.Foreground = Theme.TextSub;
            captionBlock.VerticalAlignment = VerticalAlignment.Center;

            summary = new TextBlock();
            summary.FontSize = Theme.MetaSize;
            summary.Foreground = Theme.TextMuted;
            summary.VerticalAlignment = VerticalAlignment.Center;
            summary.HorizontalAlignment = HorizontalAlignment.Right;
            summary.Margin = new Thickness(Theme.Space2, 0, 0, 0);

            DockPanel header = new DockPanel();
            header.LastChildFill = true;
            DockPanel.SetDock(summary, Dock.Right);
            header.Children.Add(summary);
            header.Children.Add(captionBlock);

            Expander expander = new Expander();
            expander.SetResourceReference(FrameworkElement.StyleProperty, "AppAccordion");
            expander.Header = header;
            expander.Content = content;
            System.Windows.Automation.AutomationProperties.SetName(expander, caption);
            return expander;
        }
    }
}
