namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Documents;
    using System.Windows.Media;

    // A place to read and write code in, rather than a box with code in it.
    //
    // Line numbers, colour and a find bar are not decoration here. The automation
    // is several modules now, and a person looking for the one line that presses
    // the wrong button needs to be able to say which line it is, tell a comment
    // from a string without reading it, and find a name without scrolling.
    //
    // The colour is painted behind, not into, the text. The thing being typed in
    // is an ordinary TextBox with a transparent foreground; a TextBlock under it
    // holds the same characters in colour, and the two are kept at the same
    // scroll offset. Editing therefore behaves exactly like a plain text box -
    // no caret jumping when the colours are recomputed, no undo history that
    // belongs to a document rather than to the operator - and if the painting
    // were ever wrong the text underneath would still be right.
    // A text block that is not there as far as anything reading the screen is
    // concerned.
    //
    // The colour layer holds the same characters as the box being typed into,
    // and the gutter holds a column of numbers. Both are decoration for the eye.
    // Left in the automation tree they are announced as text, so a reader of the
    // screen hears the whole file twice and then hears its line numbers, and
    // anything measuring the window sees a text element hanging out past the
    // bottom of it - because these layers are deliberately taller than the box
    // that clips them.
    public sealed class DecorationText : TextBlock
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        {
            return null;
        }
    }

    public sealed class CodeEditor
    {
        private readonly TextBox input = new TextBox();
        private readonly DecorationText paint = new DecorationText();
        private readonly DecorationText gutter = new DecorationText();
        private readonly TextBox findBox = new TextBox();
        private readonly TextBlock findCount = new TextBlock();
        private readonly TranslateTransform paintShift = new TranslateTransform();
        private readonly TranslateTransform gutterShift = new TranslateTransform();
        // A whole line of room under the last one. Without it, scrolling to the
        // end of a file leaves the final line flush against the bottom edge with
        // the row above it sliced in half, which reads - correctly - as "this
        // does not go all the way down". The colour layer, the numbers and the
        // box being typed into all take the same inset, so they scroll together.
        private readonly Thickness inset = new Thickness(Theme.Space2, Theme.Space2, Theme.Space2, Theme.Space2 + Theme.CodeLine);

        private Grid root;
        private FrameworkElement findBar;
        private Border numberColumn;
        private string language = ScriptLanguages.PowerShell;
        private bool painting;
        private bool suppress;
        // On by default. A line of generated code is long, and a reader who has
        // to scroll sideways to see the end of it is a reader who does not.
        private bool wrap = true;
        private System.Windows.Threading.DispatcherTimer repaint;

        // Raised after the operator changed the text, never while the text is
        // being put in from outside.
        public Action Changed;

        public string Text
        {
            get { return input.Text; }
            set
            {
                suppress = true;
                input.Text = value == null ? "" : value;
                suppress = false;
                Repaint();
            }
        }

        public string Language { get { return language; } }

        public int CaretIndex
        {
            get { return input.CaretIndex; }
            set
            {
                int at = value;
                if (at < 0) at = 0;
                if (at > input.Text.Length) at = input.Text.Length;
                input.CaretIndex = at;
            }
        }

        public double VerticalOffset { get { return input.VerticalOffset; } }
        public double HorizontalOffset { get { return input.HorizontalOffset; } }

        public void ScrollTo(double vertical, double horizontal)
        {
            input.ScrollToVerticalOffset(vertical);
            input.ScrollToHorizontalOffset(horizontal);
            Sync();
        }

        public void FocusEditor()
        {
            input.Focus();
        }

        public void ShowFind()
        {
            if (findBar == null) return;
            findBar.Visibility = Visibility.Visible;
            findBox.Focus();
            findBox.SelectAll();
        }

        private void HideFind()
        {
            if (findBar == null) return;
            findBar.Visibility = Visibility.Collapsed;
            findBox.Text = "";
            Repaint();
            Count();
            input.Focus();
        }

        public void SetLanguage(string value)
        {
            language = ScriptLanguages.IsKnown(value) ? value : ScriptLanguages.PowerShell;
            Repaint();
        }

        // Wrapping, so that reading a file does not require driving it sideways.
        //
        // A generated workflow line names a window, an element and a value, and
        // is routinely past a hundred characters. Without wrapping the only way
        // to read the end of it is a horizontal scrollbar, which also means the
        // line the operator is looking for can be off the right hand edge with
        // nothing on screen saying so.
        //
        // The colour is painted on a layer behind the box being typed into, and
        // the two only line up if they break their lines in the same places. So
        // this sets both, and the layer behind is pinned to the width of the
        // viewport rather than being allowed to size to its longest line; a
        // wrapping layer that is a thousand units wide wraps in different places
        // from a box that is four hundred wide.
        public void SetWrap(bool value)
        {
            wrap = value;
            input.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            input.HorizontalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            paint.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            // The gutter counts lines of the file, and a wrapped line is still
            // one line of the file. Numbering what is on screen instead would
            // make the numbers disagree with every error message the compiler
            // ever produces, so when wrapping is on the column is put away
            // rather than made to lie.
            if (numberColumn != null) numberColumn.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            SizePaint();
            Repaint();
        }

        public bool Wrapped { get { return wrap; } }

        // A file that is shown but not edited. The caret is left visible so the
        // text can still be selected and copied; only writing is refused.
        public void SetReadOnly(bool value)
        {
            input.IsReadOnly = value;
            input.IsReadOnlyCaretVisible = true;
        }

        public bool IsReadOnly { get { return input.IsReadOnly; } }

        // The colour layer has to break its lines exactly where the box above it
        // does, so while wrapping is on it is given the box's own width.
        private void SizePaint()
        {
            if (!wrap)
            {
                paint.Width = Double.NaN;
                return;
            }
            double room = input.ActualWidth - input.Padding.Left - input.Padding.Right - 2;
            paint.Width = room > 40 ? room : 40;
        }

        // Built once and handed back afterwards, so moving the editor between
        // the ordinary screen and the full width one keeps the text, the caret
        // and the selection: it is the same control, not a new one holding a
        // copy.
        public UIElement Build()
        {
            if (root != null)
            {
                Detach(root);
                return root;
            }
            root = new Grid();
            root.RowDefinitions.Add(Auto());
            root.RowDefinitions.Add(Star());
            // Find is a thing you ask for, not a thing that sits above every
            // file taking up the top of the editor. It appears on Ctrl+F, where
            // it is in every editor, and Escape puts it away.
            findBar = FindBar();
            findBar.Visibility = Visibility.Collapsed;
            Grid.SetRow(findBar, 0);
            root.Children.Add(findBar);
            UIElement area = Area();
            Grid.SetRow(area, 1);
            root.Children.Add(area);
            return root;
        }

        private static void Detach(UIElement child)
        {
            DependencyObject parent = LogicalTreeHelper.GetParent(child);
            Panel panel = parent as Panel;
            if (panel != null) { panel.Children.Remove(child); return; }
            Decorator decorator = parent as Decorator;
            if (decorator != null) { decorator.Child = null; return; }
            ContentControl holder = parent as ContentControl;
            if (holder != null) holder.Content = null;
        }

        private FrameworkElement FindBar()
        {
            DockPanel bar = new DockPanel();
            bar.LastChildFill = false;
            bar.HorizontalAlignment = HorizontalAlignment.Right;
            bar.Margin = new Thickness(0, 0, 0, Theme.Space2);

            TextBlock label = new TextBlock();
            label.Text = Messages.Text("code-find.txt", "Find");
            label.FontSize = Theme.MetaSize;
            label.Foreground = Theme.TextMuted;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, Theme.Space2, 0);
            DockPanel.SetDock(label, Dock.Left);
            bar.Children.Add(label);

            findBox.SetResourceReference(FrameworkElement.StyleProperty, "AppTextBox");
            findBox.Width = 200;
            findBox.FontSize = Theme.MetaSize;
            findBox.VerticalAlignment = VerticalAlignment.Center;
            findBox.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key != System.Windows.Input.Key.Escape) return;
                HideFind();
                args.Handled = true;
            };
            System.Windows.Automation.AutomationProperties.SetName(findBox, Messages.Text("code-find.txt", "Find"));
            findBox.TextChanged += delegate { Repaint(); Count(); };
            findBox.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                if (args.Key != System.Windows.Input.Key.Enter) return;
                Step(1);
                args.Handled = true;
            };
            DockPanel.SetDock(findBox, Dock.Left);
            bar.Children.Add(findBox);

            DockPanel.SetDock(Small(bar, Messages.Text("code-find-prev.txt", "Previous"), delegate { Step(-1); }), Dock.Left);
            DockPanel.SetDock(Small(bar, Messages.Text("code-find-next.txt", "Next"), delegate { Step(1); }), Dock.Left);

            findCount.FontSize = Theme.MetaSize;
            findCount.Foreground = Theme.TextMuted;
            findCount.VerticalAlignment = VerticalAlignment.Center;
            findCount.Margin = new Thickness(Theme.Space3, 0, 0, 0);
            bar.Children.Add(findCount);
            return bar;
        }

        private Button Small(Panel into, string label, Action action)
        {
            Button button = new Button();
            button.Content = label;
            button.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonCompact");
            button.Margin = new Thickness(Theme.Space2, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += delegate { action(); };
            into.Children.Add(button);
            return button;
        }

        private UIElement Area()
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(AutoColumn());
            grid.ColumnDefinitions.Add(StarColumn());

            Border numbers = new Border();
            numbers.Background = Theme.CodeGutter;
            numbers.BorderBrush = Theme.BorderSubtle;
            numbers.BorderThickness = new Thickness(0, 0, 1, 0);
            numbers.ClipToBounds = true;
            numbers.MinWidth = Theme.LineNumberWidth;
            // No LineHeight here. The numbers, the colour layer and the box being
            // typed into have to advance by exactly the same amount per line, and
            // the only value all three agree on is the font's own.
            gutter.FontFamily = Theme.CodeFont;
            gutter.FontSize = Theme.CodeSize;
            gutter.Foreground = Theme.CodeGutterText;
            gutter.TextAlignment = TextAlignment.Right;
            gutter.Margin = new Thickness(Theme.Space2, inset.Top, Theme.Space2, inset.Bottom);
            // Top, not stretched. A stretched child is arranged at the height of
            // the box it is in, and a text block arranged shorter than its text
            // simply stops drawing the rest. Scrolled far enough down, the
            // numbers ran out and the column went blank below the last one that
            // happened to fit the viewport when the file was opened.
            gutter.VerticalAlignment = VerticalAlignment.Top;
            gutter.RenderTransform = gutterShift;
            TextOptions.SetTextFormattingMode(gutter, TextFormattingMode.Display);
            numbers.Child = Unbounded(gutter);
            numberColumn = numbers;
            Grid.SetColumn(numbers, 0);
            grid.Children.Add(numbers);

            Grid stack = new Grid();
            stack.ClipToBounds = true;
            stack.Background = Theme.SurfaceCode;

            paint.FontFamily = Theme.CodeFont;
            paint.FontSize = Theme.CodeSize;
            paint.Foreground = Theme.Text;
            paint.TextWrapping = TextWrapping.NoWrap;
            paint.Margin = inset;
            // The same reason as the gutter, and the one that mattered: this is
            // the layer the code itself is drawn on. Stretched, it was arranged
            // at the viewport's height, so a file longer than the viewport lost
            // its lower half - scroll down and the code disappeared, leaving the
            // caret and the selection moving over an empty box. Top alignment
            // lets it take the height its text needs; the box around it clips.
            paint.VerticalAlignment = VerticalAlignment.Top;
            paint.HorizontalAlignment = HorizontalAlignment.Left;
            paint.RenderTransform = paintShift;
            paint.IsHitTestVisible = false;
            TextOptions.SetTextFormattingMode(paint, TextFormattingMode.Display);
            stack.Children.Add(Unbounded(paint));

            input.FontFamily = Theme.CodeFont;
            input.FontSize = Theme.CodeSize;
            input.AcceptsReturn = true;
            input.AcceptsTab = true;
            input.TextWrapping = TextWrapping.NoWrap;
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            input.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            input.VerticalContentAlignment = VerticalAlignment.Top;
            input.BorderThickness = new Thickness(0);
            input.Background = System.Windows.Media.Brushes.Transparent;
            // The characters are drawn by the layer underneath. Only the caret
            // and the selection come from the box itself, and the selection is
            // deliberately see through so the colour is not painted over.
            input.Foreground = System.Windows.Media.Brushes.Transparent;
            input.CaretBrush = Theme.Text;
            input.SelectionBrush = new SolidColorBrush(Color.FromArgb(70, 90, 140, 200));
            input.Padding = inset;
            TextOptions.SetTextFormattingMode(input, TextFormattingMode.Display);
            System.Windows.Automation.AutomationProperties.SetName(input,
                Messages.Text("code-editor-name.txt", "The automation, as code"));
            input.TextChanged += delegate
            {
                Repaint();
                if (suppress) return;
                if (Changed != null) Changed();
            };
            input.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(delegate { Sync(); }));
            input.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
            {
                bool control = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                if (control && args.Key == System.Windows.Input.Key.F) { ShowFind(); args.Handled = true; return; }
                if (args.Key == System.Windows.Input.Key.Escape && findBar != null && findBar.Visibility == Visibility.Visible)
                {
                    HideFind();
                    args.Handled = true;
                }
            };
            stack.Children.Add(input);

            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
            // Wrapping depends on how wide the box actually is, which is not
            // known until it has been laid out and changes again every time a
            // pane beside it is folded or a splitter is dragged.
            input.SizeChanged += delegate { SizePaint(); };
            SetWrap(wrap);

            Border frame = new Border();
            frame.BorderBrush = Theme.Border;
            frame.BorderThickness = new Thickness(1);
            frame.CornerRadius = new CornerRadius(Theme.RadiusSm);
            frame.ClipToBounds = true;
            frame.Child = grid;
            return frame;
        }

        // A layer that is allowed to be taller than the box it is drawn in.
        //
        // The colour and the line numbers are painted behind a scrolling text
        // box and moved with a transform, so both have to hold the whole file
        // however little of it is on screen. A panel measures its children with
        // the room it has, and a text block measured with the viewport's height
        // is arranged at the viewport's height and stops drawing after that -
        // so scrolling past the first screenful left the caret moving over an
        // empty box with no code under it at all. A canvas measures its children
        // with no limit, which is the one thing needed here; the box around it
        // still clips what hangs out.
        private static UIElement Unbounded(UIElement layer)
        {
            Canvas host = new Canvas();
            host.IsHitTestVisible = false;
            Canvas.SetLeft(layer, 0);
            Canvas.SetTop(layer, 0);
            host.Children.Add(layer);
            return host;
        }

        private void Sync()
        {
            paintShift.X = -input.HorizontalOffset;
            paintShift.Y = -input.VerticalOffset;
            gutterShift.Y = -input.VerticalOffset;
        }

        // Recolouring on every keystroke of a long module would be felt while
        // typing, so it is done once the typing pauses. The text is never
        // waiting on it: only the colour is.
        private void Repaint()
        {
            if (repaint == null)
            {
                repaint = new System.Windows.Threading.DispatcherTimer();
                repaint.Interval = TimeSpan.FromMilliseconds(90);
                repaint.Tick += delegate
                {
                    repaint.Stop();
                    PaintNow();
                };
            }
            repaint.Stop();
            repaint.Start();
        }

        private void PaintNow()
        {
            if (painting) return;
            painting = true;
            try
            {
                string text = input.Text == null ? "" : input.Text;
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                StringBuilder numbers = new StringBuilder();
                for (int index = 0; index < lines.Length; index++)
                {
                    if (index != 0) numbers.Append(Environment.NewLine);
                    numbers.Append((index + 1).ToString(CultureInfo.InvariantCulture));
                }
                gutter.Text = numbers.ToString();

                string needle = findBox.Text;
                paint.Inlines.Clear();
                for (int index = 0; index < lines.Length; index++)
                {
                    if (index != 0) paint.Inlines.Add(new LineBreak());
                    PaintLine(lines[index], needle);
                }
                Sync();
            }
            finally
            {
                painting = false;
            }
        }

        private void PaintLine(string line, string needle)
        {
            List<CodeSpan> spans = CodeSyntax.Scan(line, language);
            spans = CodeSyntax.Mark(spans, line, needle);
            for (int index = 0; index < spans.Count; index++)
            {
                CodeSpan span = spans[index];
                Run run = new Run(line.Substring(span.Start, span.Length));
                run.Foreground = Brush(span.Kind);
                if (span.Found) run.Background = Theme.CodeFindMark;
                paint.Inlines.Add(run);
            }
        }

        private static Brush Brush(int kind)
        {
            if (kind == CodeSyntax.KindComment) return Theme.CodeComment;
            if (kind == CodeSyntax.KindString) return Theme.CodeString;
            if (kind == CodeSyntax.KindKeyword) return Theme.CodeKeyword;
            if (kind == CodeSyntax.KindNumber) return Theme.CodeNumber;
            if (kind == CodeSyntax.KindVariable) return Theme.CodeVariable;
            return Theme.Text;
        }

        // ---------- find ----------

        private void Count()
        {
            string needle = findBox.Text;
            if (String.IsNullOrEmpty(needle))
            {
                findCount.Text = "";
                return;
            }
            int total = 0;
            int at = input.Text.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                total++;
                if (at + 1 > input.Text.Length - 1) break;
                at = input.Text.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase);
            }
            findCount.Text = total == 0
                ? Messages.Text("code-find-none.txt", "not found")
                : total.ToString(CultureInfo.InvariantCulture) + " " + Messages.Text("code-find-hits.txt", "match(es)");
            findCount.Foreground = total == 0 ? Theme.CautionText : Theme.TextMuted;
        }

        private void Step(int direction)
        {
            string needle = findBox.Text;
            if (String.IsNullOrEmpty(needle)) return;
            string text = input.Text;
            int found;
            if (direction >= 0)
            {
                int from = input.CaretIndex;
                if (from > text.Length) from = text.Length;
                found = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (found < 0) found = text.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                int before = input.SelectionStart - 1;
                if (before < 0) before = -1;
                found = before < 0 ? -1 : text.LastIndexOf(needle, Math.Min(before, text.Length - 1), StringComparison.OrdinalIgnoreCase);
                if (found < 0 && text.Length > 0) found = text.LastIndexOf(needle, text.Length - 1, StringComparison.OrdinalIgnoreCase);
            }
            if (found < 0)
            {
                Count();
                return;
            }
            input.Focus();
            input.Select(found, needle.Length);
            input.ScrollToLine(input.GetLineIndexFromCharacterIndex(found));
            Sync();
            Count();
        }

        private static RowDefinition Auto()
        {
            RowDefinition row = new RowDefinition();
            row.Height = GridLength.Auto;
            return row;
        }

        private static RowDefinition Star()
        {
            RowDefinition row = new RowDefinition();
            row.Height = new GridLength(1, GridUnitType.Star);
            return row;
        }

        private static ColumnDefinition StarColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(1, GridUnitType.Star);
            return column;
        }

        private static ColumnDefinition AutoColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = GridLength.Auto;
            return column;
        }
    }

    public sealed class CodeSpan
    {
        public int Start;
        public int Length;
        public int Kind;
        public bool Found;
    }

    // Enough of each language to tell one kind of text from another at a glance.
    //
    // It is deliberately not a parser. It reads one line at a time and never
    // carries state between lines, so nothing it gets wrong can make the rest of
    // the file look wrong, and a module that is being typed into - and is
    // therefore not valid code at that moment - still reads normally.
    public static class CodeSyntax
    {
        public const int KindPlain = 0;
        public const int KindComment = 1;
        public const int KindString = 2;
        public const int KindKeyword = 3;
        public const int KindNumber = 4;
        public const int KindVariable = 5;

        private static readonly string[] PowerShellWords = new string[]
        {
            "function", "param", "if", "else", "elseif", "foreach", "for", "while", "do", "switch",
            "return", "break", "continue", "try", "catch", "finally", "throw", "begin", "process", "end",
            "in", "not", "and", "or", "true", "false", "null", "requires"
        };

        private static readonly string[] VbaWords = new string[]
        {
            "public", "private", "sub", "function", "end", "dim", "as", "if", "then", "else", "elseif",
            "for", "next", "do", "while", "loop", "select", "case", "exit", "declare", "ptrsafe", "lib",
            "alias", "byval", "byref", "option", "explicit", "set", "const", "type", "on", "error",
            "goto", "resume", "with", "and", "or", "not", "true", "false", "nothing", "long", "longptr",
            "string", "double", "boolean", "integer", "variant", "to", "step", "each", "call", "let"
        };

        public static List<CodeSpan> Scan(string line, string language)
        {
            List<CodeSpan> spans = new List<CodeSpan>();
            if (line == null || line.Length == 0) return spans;
            bool vba = String.Equals(language, ScriptLanguages.Vba, StringComparison.Ordinal);
            int index = 0;
            while (index < line.Length)
            {
                char character = line[index];
                if (vba && character == '\'')
                {
                    Add(spans, index, line.Length - index, KindComment);
                    return spans;
                }
                if (!vba && character == '#')
                {
                    Add(spans, index, line.Length - index, KindComment);
                    return spans;
                }
                if (character == '"' || (!vba && character == '\''))
                {
                    int end = index + 1;
                    while (end < line.Length)
                    {
                        if (line[end] == character)
                        {
                            // A doubled quote is one quote inside the string, in
                            // both languages, so it does not end it.
                            if (end + 1 < line.Length && line[end + 1] == character) { end += 2; continue; }
                            end++;
                            break;
                        }
                        end++;
                    }
                    Add(spans, index, end - index, KindString);
                    index = end;
                    continue;
                }
                if (!vba && character == '$')
                {
                    int end = index + 1;
                    while (end < line.Length && (Char.IsLetterOrDigit(line[end]) || line[end] == '_' || line[end] == ':')) end++;
                    Add(spans, index, end - index, KindVariable);
                    index = end;
                    continue;
                }
                if (Char.IsDigit(character) && (index == 0 || !IsWord(line[index - 1])))
                {
                    int end = index;
                    while (end < line.Length && (Char.IsLetterOrDigit(line[end]) || line[end] == '.')) end++;
                    Add(spans, index, end - index, KindNumber);
                    index = end;
                    continue;
                }
                if (IsWord(character) && (index == 0 || !IsWord(line[index - 1])))
                {
                    int end = index;
                    while (end < line.Length && IsWord(line[end])) end++;
                    string word = line.Substring(index, end - index);
                    Add(spans, index, end - index, IsKeyword(word, vba) ? KindKeyword : KindPlain);
                    index = end;
                    continue;
                }
                Add(spans, index, 1, KindPlain);
                index++;
            }
            return spans;
        }

        // Merges the runs a find matched into the colouring, splitting a span
        // where a match starts or ends so a hit inside a string is still shown.
        public static List<CodeSpan> Mark(List<CodeSpan> spans, string line, string needle)
        {
            if (String.IsNullOrEmpty(needle) || line == null || line.Length == 0) return spans;
            List<int[]> hits = new List<int[]>();
            int at = line.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                hits.Add(new int[] { at, needle.Length });
                int next = at + needle.Length;
                if (next >= line.Length) break;
                at = line.IndexOf(needle, next, StringComparison.OrdinalIgnoreCase);
            }
            if (hits.Count == 0) return spans;
            List<CodeSpan> marked = new List<CodeSpan>();
            for (int index = 0; index < spans.Count; index++)
            {
                CodeSpan span = spans[index];
                int cursor = span.Start;
                int stop = span.Start + span.Length;
                while (cursor < stop)
                {
                    int hitStart = -1;
                    int hitEnd = -1;
                    for (int hit = 0; hit < hits.Count; hit++)
                    {
                        int start = hits[hit][0];
                        int end = start + hits[hit][1];
                        if (end <= cursor || start >= stop) continue;
                        if (start <= cursor) { hitStart = cursor; hitEnd = Math.Min(end, stop); break; }
                        if (hitStart < 0 || start < hitStart) { hitStart = start; hitEnd = Math.Min(end, stop); }
                    }
                    if (hitStart < 0)
                    {
                        Add(marked, cursor, stop - cursor, span.Kind);
                        break;
                    }
                    if (hitStart > cursor) Add(marked, cursor, hitStart - cursor, span.Kind);
                    CodeSpan piece = new CodeSpan();
                    piece.Start = hitStart;
                    piece.Length = hitEnd - hitStart;
                    piece.Kind = span.Kind;
                    piece.Found = true;
                    marked.Add(piece);
                    cursor = hitEnd;
                }
            }
            return marked;
        }

        private static void Add(List<CodeSpan> spans, int start, int length, int kind)
        {
            if (length <= 0) return;
            if (spans.Count > 0)
            {
                CodeSpan last = spans[spans.Count - 1];
                if (last.Kind == kind && !last.Found && last.Start + last.Length == start)
                {
                    last.Length += length;
                    return;
                }
            }
            CodeSpan span = new CodeSpan();
            span.Start = start;
            span.Length = length;
            span.Kind = kind;
            spans.Add(span);
        }

        private static bool IsWord(char character)
        {
            return Char.IsLetterOrDigit(character) || character == '_' || character == '-';
        }

        private static bool IsKeyword(string word, bool vba)
        {
            string lower = word.ToLowerInvariant();
            if (!vba && lower.Length > 1 && lower[0] == '-') lower = lower.Substring(1);
            string[] words = vba ? VbaWords : PowerShellWords;
            for (int index = 0; index < words.Length; index++)
            {
                if (String.Equals(words[index], lower, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
