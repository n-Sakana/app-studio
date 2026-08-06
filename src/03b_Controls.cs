namespace AppStudio
{
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;

    // The parts every screen in this product is assembled from.
    //
    // They are here rather than repeated per screen so that a control means the
    // same thing wherever it appears: the gear is in the same place and does the
    // same thing on both shapes of the window, a setting that is on or off is
    // always a switch and never a button whose caption states its own state, and
    // a control carrying a drawing always carries the words as well - as its
    // accessible name and as its tooltip - because a picture is a shorthand for
    // people who already know, not a replacement for saying what a thing does.
    public static class Ui
    {
        // A control whose whole face is a drawing. The words are not dropped;
        // they are moved to the two places that state them without taking room
        // on the bar: what a screen reader announces, and what appears when the
        // pointer rests on it.
        public static Button IconButton(string icon, string label, string tooltip, Action action)
        {
            Button button = new Button();
            button.SetResourceReference(FrameworkElement.StyleProperty, "AppIconButton");
            button.Content = Icons.Make(icon, 18, Theme.TextSub);
            Name(button, label, tooltip);
            if (action != null) button.Click += delegate { action(); };
            return button;
        }

        // A drawing and a word together. Used where the operation is the point of
        // the row it is in - record, replay, build - and the drawing alone would
        // be a guess rather than a convention.
        public static Button IconTextButton(string icon, string label, string tooltip, Action action, bool primary)
        {
            Button button = new Button();
            button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AppButtonPrimary" : "AppButtonCompact");
            button.Content = Row(icon, label, primary ? Theme.TextOnAccent : Theme.TextSub);
            Name(button, label, tooltip);
            if (action != null) button.Click += delegate { action(); };
            return button;
        }

        public static System.Windows.Controls.Primitives.ToggleButton IconToggle(string icon, string label, string tooltip)
        {
            System.Windows.Controls.Primitives.ToggleButton toggle = new System.Windows.Controls.Primitives.ToggleButton();
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "AppIconToggle");
            toggle.Content = Icons.Make(icon, 18, Theme.TextSub);
            Name(toggle, label, tooltip);
            return toggle;
        }

        private static UIElement Row(string icon, string label, Brush brush)
        {
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.VerticalAlignment = VerticalAlignment.Center;
            if (Icons.Has(icon))
            {
                UIElement drawing = Icons.Make(icon, 16, brush);
                FrameworkElement framed = drawing as FrameworkElement;
                if (framed != null) framed.Margin = new Thickness(0, 0, Theme.Space2, 0);
                row.Children.Add(drawing);
            }
            TextBlock text = new TextBlock();
            text.Text = label;
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            return row;
        }

        // A setting that is either on or off, and a sentence saying what it is
        // about. Never a button, and never a caption that states the state - the
        // knob states it.
        public static System.Windows.Controls.Primitives.ToggleButton Switch(string label, string note, bool value)
        {
            System.Windows.Controls.Primitives.ToggleButton toggle = new System.Windows.Controls.Primitives.ToggleButton();
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "AppSwitch");
            TextBlock text = new TextBlock();
            text.Text = label;
            text.TextWrapping = TextWrapping.Wrap;
            toggle.Content = text;
            toggle.IsChecked = value;
            Name(toggle, label, note);
            return toggle;
        }

        // The switch with its sentence underneath, as one block. The sentence is
        // the thing that says what the setting acts on, which a label alone
        // cannot: "show what is under the pointer" is a name, "it draws an
        // outline round the control under the pointer and never appears in the
        // recording" is what the operator needs before deciding.
        public static UIElement SwitchBlock(System.Windows.Controls.Primitives.ToggleButton toggle, string note)
        {
            StackPanel stack = new StackPanel();
            stack.Children.Add(toggle);
            if (!String.IsNullOrEmpty(note))
            {
                TextBlock text = new TextBlock();
                text.Text = note;
                text.FontSize = Theme.MetaSize;
                text.LineHeight = Theme.MetaSize * Theme.BodyLine;
                text.Foreground = Theme.TextMuted;
                text.TextWrapping = TextWrapping.Wrap;
                text.Margin = new Thickness(48, Theme.Space1, 0, 0);
                stack.Children.Add(text);
            }
            stack.Margin = new Thickness(0, Theme.Space3, 0, 0);
            return stack;
        }

        // One item of a list being picked from, and the sentence that says what
        // it is.
        //
        // A checkbox rather than a switch: a switch is a setting that changes how
        // the product behaves, and these are things being chosen for one act. Each
        // one stands alone - nothing here reads another one to decide its own
        // state, and none of them is ever disabled because of what it was combined
        // with, because "you would not want that combination" is a guess about
        // somebody else's work.
        public static CheckBox Check(string label, string note, bool value)
        {
            CheckBox box = new CheckBox();
            box.SetResourceReference(FrameworkElement.StyleProperty, "AppCheckBox");
            TextBlock text = new TextBlock();
            text.Text = label;
            text.TextWrapping = TextWrapping.Wrap;
            text.FontWeight = FontWeights.SemiBold;
            box.Content = text;
            box.IsChecked = value;
            Name(box, label, note);
            return box;
        }

        // The checkbox with its sentence underneath, as one block, indented to
        // start where the label starts. The sentence is what says what the item
        // actually is; a list of fourteen bare labels is a list nobody can choose
        // from.
        public static UIElement CheckBlock(CheckBox box, string note)
        {
            StackPanel stack = new StackPanel();
            stack.Children.Add(box);
            if (!String.IsNullOrEmpty(note))
            {
                TextBlock text = new TextBlock();
                text.Text = note;
                text.FontSize = Theme.MicroSize;
                text.LineHeight = Theme.MicroSize * Theme.BodyLine;
                text.Foreground = Theme.TextMuted;
                text.TextWrapping = TextWrapping.Wrap;
                text.Margin = new Thickness(26, 0, 0, 0);
                stack.Children.Add(text);
            }
            stack.Margin = new Thickness(0, 0, 0, Theme.Space2);
            return stack;
        }

        // One position of a control with two. Used where the choice is between
        // two ways of looking at the same thing rather than between two places to
        // go: both positions are on screen, the chosen one is filled in, and
        // pressing the other changes what is underneath rather than where the
        // operator is. A tab would have said "another screen"; a button whose
        // caption is the state would have said nothing until it was pressed.
        public static System.Windows.Controls.Primitives.ToggleButton Segment(string label, string tooltip)
        {
            System.Windows.Controls.Primitives.ToggleButton segment = new System.Windows.Controls.Primitives.ToggleButton();
            segment.SetResourceReference(FrameworkElement.StyleProperty, "AppSegment");
            TextBlock text = new TextBlock();
            text.Text = label;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            segment.Content = text;
            Name(segment, label, tooltip);
            return segment;
        }

        public static System.Windows.Controls.Primitives.ToggleButton Tab(string icon, string label, string tooltip)
        {
            System.Windows.Controls.Primitives.ToggleButton tab = new System.Windows.Controls.Primitives.ToggleButton();
            tab.SetResourceReference(FrameworkElement.StyleProperty, "AppTab");
            tab.Content = Row(icon, label, Theme.TextSub);
            Name(tab, label, tooltip);
            return tab;
        }

        // What an empty box is for, drawn behind it rather than typed into it.
        //
        // Text put into the box as a starting point is text the product wrote and
        // the operator is credited with; it also has to be deleted before
        // anything else can be typed. A prompt that disappears the moment there
        // is something to read says the same thing and is never mistaken for an
        // answer.
        public static Grid Placeholder(TextBox box, string prompt)
        {
            TextBlock hint = new TextBlock();
            hint.Text = prompt;
            hint.FontSize = box.FontSize > 0 ? box.FontSize : Theme.BodySize;
            hint.Foreground = Theme.TextMuted;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.IsHitTestVisible = false;
            hint.Margin = new Thickness(Theme.Space3, Theme.Space3, Theme.Space3, Theme.Space2);
            hint.VerticalAlignment = VerticalAlignment.Top;
            hint.HorizontalAlignment = HorizontalAlignment.Left;
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += delegate
            {
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            Grid holder = new Grid();
            holder.Children.Add(box);
            holder.Children.Add(hint);
            return holder;
        }

        public static Slider Speed(double value, double min, double max, double step)
        {
            Slider slider = new Slider();
            slider.SetResourceReference(FrameworkElement.StyleProperty, "AppSlider");
            slider.Minimum = min;
            slider.Maximum = max;
            slider.SmallChange = step;
            slider.LargeChange = step;
            slider.TickFrequency = step;
            slider.IsSnapToTickEnabled = true;
            slider.Value = value;
            return slider;
        }

        public static TextBlock Heading(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.SectionSize;
            block.FontWeight = FontWeights.Bold;
            block.Foreground = Theme.Text;
            block.TextWrapping = TextWrapping.Wrap;
            block.VerticalAlignment = VerticalAlignment.Center;
            return block;
        }

        public static TextBlock Note(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.MetaSize;
            block.LineHeight = Theme.MetaSize * Theme.BodyLine;
            block.Foreground = Theme.TextMuted;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        public static TextBlock Label(string text)
        {
            TextBlock block = new TextBlock();
            block.Text = text;
            block.FontSize = Theme.LabelSize;
            block.FontWeight = FontWeights.SemiBold;
            block.Foreground = Theme.TextSub;
            block.TextWrapping = TextWrapping.Wrap;
            return block;
        }

        // What a pane says when it has nothing in it yet. An empty pane with no
        // words in it reads as a product that has failed to load; this says which
        // of the two it is, and what would put something there.
        public static UIElement Empty(string headline, string note)
        {
            StackPanel stack = new StackPanel();
            stack.VerticalAlignment = VerticalAlignment.Center;
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Margin = new Thickness(Theme.Space5);
            TextBlock head = new TextBlock();
            head.Text = headline;
            head.FontSize = Theme.BodySize;
            head.FontWeight = FontWeights.SemiBold;
            head.Foreground = Theme.TextMuted;
            head.TextWrapping = TextWrapping.Wrap;
            head.TextAlignment = TextAlignment.Center;
            stack.Children.Add(head);
            if (!String.IsNullOrEmpty(note))
            {
                TextBlock body = Note(note);
                body.TextAlignment = TextAlignment.Center;
                body.MaxWidth = 380;
                body.Margin = new Thickness(0, Theme.Space3, 0, 0);
                stack.Children.Add(body);
            }
            return stack;
        }

        // A card that fills the cell it is in, and stays inside it.
        //
        // A pane is one column of a grid, and what is in it can want more room
        // than the column has - a row of four buttons is as wide as four buttons
        // whatever it is put in. Without this the surplus is painted over the
        // pane next door and, at the last pane, off the side of the window. It is
        // held here, on the one part every pane is made from, rather than
        // remembered separately at each place that could overflow.
        public static Border Panel(UIElement content)
        {
            Border card = new Border();
            card.Background = Theme.Surface;
            card.BorderBrush = Theme.Border;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(Theme.RadiusMd);
            card.ClipToBounds = true;
            card.Child = content;
            return card;
        }

        // A row of controls that becomes two rows rather than running off the
        // side. Used wherever several operations sit together in a pane whose
        // width the operator sets.
        public static WrapPanel Row()
        {
            WrapPanel row = new WrapPanel();
            row.Orientation = Orientation.Horizontal;
            return row;
        }

        // The head of a pane: what the pane is, and the operations that belong to
        // that pane rather than to the window. Replay belongs beside the workflow
        // and build belongs beside the code, so neither is on the top bar.
        public static Grid PaneHead(string icon, string title)
        {
            Grid head = new Grid();
            head.ColumnDefinitions.Add(AutoColumn());
            head.ColumnDefinitions.Add(StarColumn());
            head.ColumnDefinitions.Add(AutoColumn());
            head.MinHeight = Theme.PaneHeaderHeight;
            head.Margin = new Thickness(Theme.Space4, Theme.Space3, Theme.Space3, Theme.Space2);
            StackPanel name = new StackPanel();
            name.Orientation = Orientation.Horizontal;
            name.VerticalAlignment = VerticalAlignment.Center;
            if (Icons.Has(icon))
            {
                UIElement drawing = Icons.Make(icon, 16, Theme.TextMuted);
                FrameworkElement framed = drawing as FrameworkElement;
                if (framed != null) framed.Margin = new Thickness(0, 0, Theme.Space2, 0);
                name.Children.Add(drawing);
            }
            name.Children.Add(Heading(title));
            Grid.SetColumn(name, 0);
            head.Children.Add(name);
            return head;
        }

        public static void Name(DependencyObject element, string label, string tooltip)
        {
            if (!String.IsNullOrEmpty(label)) System.Windows.Automation.AutomationProperties.SetName(element, label);
            FrameworkElement framed = element as FrameworkElement;
            if (framed == null) return;
            string help = String.IsNullOrEmpty(tooltip) ? label : (String.IsNullOrEmpty(label) ? tooltip : label + " - " + tooltip);
            if (!String.IsNullOrEmpty(help))
            {
                framed.ToolTip = help;
                System.Windows.Automation.AutomationProperties.SetHelpText(framed, help);
            }
        }

        public static RowDefinition AutoRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = GridLength.Auto;
            return row;
        }

        public static RowDefinition StarRow()
        {
            RowDefinition row = new RowDefinition();
            row.Height = new GridLength(1, GridUnitType.Star);
            return row;
        }

        public static RowDefinition FixedRow(double height)
        {
            RowDefinition row = new RowDefinition();
            row.Height = new GridLength(height);
            return row;
        }

        public static ColumnDefinition AutoColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = GridLength.Auto;
            return column;
        }

        public static ColumnDefinition StarColumn()
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(1, GridUnitType.Star);
            return column;
        }

        public static ColumnDefinition ShareColumn(double share)
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(share, GridUnitType.Star);
            return column;
        }

        public static ColumnDefinition FixedColumn(double width)
        {
            ColumnDefinition column = new ColumnDefinition();
            column.Width = new GridLength(width);
            return column;
        }

        public static Border Separator()
        {
            Border rule = new Border();
            rule.Width = 1;
            rule.Background = Theme.BorderSubtle;
            rule.Margin = new Thickness(Theme.Space3, Theme.Space2, Theme.Space3, Theme.Space2);
            return rule;
        }

        public static string Seconds(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
