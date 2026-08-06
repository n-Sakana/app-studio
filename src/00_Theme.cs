namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Markup;
    using System.Windows.Media;

    // The visual language follows a shared design system: the same palette, the
    // same spacing steps, the same type scale, the same card / button /
    // accordion shapes. That system states them as CSS custom properties in a
    // stylesheet; here they are brushes and styles, because this
    // product draws with WPF instead of a web view. A web view would mean
    // shipping WebView2 binaries, and this tool has to travel as text files.
    //
    // Every colour is a live SolidColorBrush that is kept, not replaced. The
    // theme switch writes new Color values into those same instances, so every
    // control that already holds one repaints without being rebuilt.
    public static class Theme
    {
        public const string Light = "light";
        public const string Dark = "dark";

        // ---- spacing (design system --space-*) ----
        public const double Space1 = 4;
        public const double Space2 = 8;
        public const double Space3 = 12;
        public const double Space4 = 16;
        public const double Space5 = 20;
        public const double Space6 = 24;
        public const double Space7 = 32;
        public const double Space8 = 40;

        // ---- radius ----
        public const double RadiusSm = 4;
        public const double RadiusMd = 6;
        public const double RadiusLg = 10;
        public const double RadiusPill = 999;

        // ---- type scale (design system --type-*) ----
        public const double TitleSize = 17;
        public const double SectionSize = 14;
        public const double BodySize = 14;
        public const double LabelSize = 13;
        public const double MetaSize = 12;
        public const double MicroSize = 11;
        public const double NumSize = 22;
        public const double BodyLine = 1.65;
        // Code is read line by line and compared column by column, so it gets a
        // size and a leading of its own rather than borrowing the body ones.
        public const double CodeSize = 13;
        public const double CodeLine = 20;

        // ---- fixed window metrics ----
        // These are the design system's own numbers rather than numbers chosen
        // per screen. A window whose rows are each a little shorter than the
        // system says is what "cheap" looks like from across a desk.
        public const double TopbarHeight = 48;
        public const double ProgressTrackHeight = 4;
        public const double ActionBarHeight = 68;
        public const double ScreenHeaderHeight = 52;
        public const double RowHeight = 40;
        public const double PaneHeaderHeight = 28;
        public const double ButtonHeight = 40;
        public const double ButtonHeightSmall = 34;
        public const double LineNumberWidth = 46;
        // An editor is a place to read a file in. Below this it is a viewport on
        // a file rather than a place, so no layout is allowed to take it lower;
        // it takes a scrollbar instead.
        public const double EditorMinHeight = 240;
        public const double ModulePaneWidth = 248;
        public const double ModulePaneMinWidth = 220;
        public const double AssistantPaneWidth = 300;

        // ---- the three panes of the full window ----
        // Stated as proportions rather than as widths, because the window is
        // resizable and a pane fixed at 248 is a fifth of one desktop and a
        // twelfth of another. These are the shares the design asks for; the
        // minimums below are what each pane stops being usable under, and the
        // splitters are free to move anywhere between them.
        public const double PaneLeftShare = 20;
        public const double PaneCentreShare = 50;
        public const double PaneRightShare = 30;
        public const double PaneLeftMin = 180;
        public const double PaneCentreMin = 320;
        public const double PaneRightMin = 260;
        // A collapsed pane is not gone: it leaves the rail that brings it back.
        public const double PaneRailWidth = 34;
        public const double SplitterWidth = 6;
        // The small window. It is a bar with the recording controls on it, so it
        // is measured by what those controls need rather than by a shape chosen
        // for a launcher.
        // Wide enough for everything the small bar has to carry at once: the two
        // ways to start a session, the recording settings, which session, replay
        // and its speed, the theme, the settings and the way back. Narrower than
        // this and the session picker - the only route to a past recording - is
        // the thing that gets squeezed off the end.
        public const double MiniWidth = 940;
        public const double MiniBarHeight = 56;
        public const double MiniListHeight = 260;

        private static readonly Dictionary<string, SolidColorBrush> Brushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        private static readonly List<ResourceDictionary> Installed = new List<ResourceDictionary>();
        private static string mode = Light;
        private static string settingsPath;

        public static string Mode { get { return mode; } }
        public static bool IsDark { get { return String.Equals(mode, Dark, StringComparison.Ordinal); } }

        public static FontFamily UiFont
        {
            // Noto Sans first, as the design system asks for.
            //
            // A text-only tool cannot carry a font file, so this is stated as a
            // preference rather than as a bundled face: where Noto Sans JP or
            // Noto Sans is installed - it ships with several editors, with
            // Office, and with anything that has pulled Google Fonts down - it is
            // what the product draws with. Where it is not, WPF walks the rest of
            // this list and lands on the Japanese UI faces Windows ships with,
            // which is what the product used before. Naming a face that is not
            // present costs nothing at all; naming it second would mean never
            // using it on the machines that do have it.
            get { return new FontFamily("Noto Sans JP, Noto Sans CJK JP, Noto Sans, Yu Gothic UI, Meiryo, Segoe UI, MS UI Gothic"); }
        }

        public static FontFamily CodeFont
        {
            get { return new FontFamily("Consolas, Cascadia Mono, MS Gothic, Courier New"); }
        }

        // ---- named colours -------------------------------------------------

        public static SolidColorBrush Get(string key)
        {
            SolidColorBrush brush;
            if (Brushes.TryGetValue(key, out brush)) return brush;
            throw new InvalidOperationException("Unknown theme colour: " + key);
        }

        public static SolidColorBrush SurfaceCanvas { get { return Get("SurfaceCanvas"); } }
        public static SolidColorBrush Surface { get { return Get("Surface"); } }
        public static SolidColorBrush SurfaceSunken { get { return Get("SurfaceSunken"); } }
        public static SolidColorBrush SurfaceHover { get { return Get("SurfaceHover"); } }
        public static SolidColorBrush SurfaceSelected { get { return Get("SurfaceSelected"); } }
        public static SolidColorBrush Text { get { return Get("Text"); } }
        public static SolidColorBrush TextSub { get { return Get("TextSub"); } }
        public static SolidColorBrush TextMuted { get { return Get("TextMuted"); } }
        public static SolidColorBrush TextDisabled { get { return Get("TextDisabled"); } }
        public static SolidColorBrush TextOnAccent { get { return Get("TextOnAccent"); } }
        public static SolidColorBrush Border { get { return Get("Border"); } }
        public static SolidColorBrush BorderSubtle { get { return Get("BorderSubtle"); } }
        public static SolidColorBrush BorderStrong { get { return Get("BorderStrong"); } }
        public static SolidColorBrush Accent { get { return Get("Accent"); } }
        public static SolidColorBrush AccentHover { get { return Get("AccentHover"); } }
        public static SolidColorBrush AccentSoft { get { return Get("AccentSoft"); } }
        public static SolidColorBrush AccentText { get { return Get("AccentText"); } }
        public static SolidColorBrush Danger { get { return Get("Danger"); } }
        public static SolidColorBrush DangerText { get { return Get("DangerText"); } }
        public static SolidColorBrush DangerSoft { get { return Get("DangerSoft"); } }
        public static SolidColorBrush Caution { get { return Get("Caution"); } }
        public static SolidColorBrush CautionText { get { return Get("CautionText"); } }
        public static SolidColorBrush CautionSoft { get { return Get("CautionSoft"); } }
        public static SolidColorBrush Success { get { return Get("Success"); } }
        public static SolidColorBrush SuccessText { get { return Get("SuccessText"); } }
        public static SolidColorBrush SuccessSoft { get { return Get("SuccessSoft"); } }
        public static SolidColorBrush Focus { get { return Get("Focus"); } }
        // ---- code ----
        // Reading code is a different job from reading a report, so it gets its
        // own few colours rather than reusing the status ones. They are the same
        // hues the rest of the window uses, so a screen full of code still looks
        // like the same application.
        public static SolidColorBrush CodeComment { get { return Get("CodeComment"); } }
        public static SolidColorBrush CodeString { get { return Get("CodeString"); } }
        public static SolidColorBrush CodeKeyword { get { return Get("CodeKeyword"); } }
        public static SolidColorBrush CodeNumber { get { return Get("CodeNumber"); } }
        public static SolidColorBrush CodeVariable { get { return Get("CodeVariable"); } }
        public static SolidColorBrush CodeGutter { get { return Get("CodeGutter"); } }
        public static SolidColorBrush CodeGutterText { get { return Get("CodeGutterText"); } }
        public static SolidColorBrush CodeFindMark { get { return Get("CodeFindMark"); } }
        public static SolidColorBrush SurfaceCode { get { return Get("SurfaceCode"); } }

        // ---- install / switch ----------------------------------------------

        public static void Init(string baseDir)
        {
            settingsPath = String.IsNullOrEmpty(baseDir)
                ? null
                : Path.Combine(baseDir, "runtime", "settings", "theme.txt");
            string stored = ReadStoredMode();
            if (stored != null) mode = stored;
        }

        private static string ReadStoredMode()
        {
            if (settingsPath == null) return null;
            try
            {
                if (!File.Exists(settingsPath)) return null;
                string value = File.ReadAllText(settingsPath).Trim().ToLowerInvariant();
                if (String.Equals(value, Dark, StringComparison.Ordinal)) return Dark;
                if (String.Equals(value, Light, StringComparison.Ordinal)) return Light;
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // The chosen theme is remembered the way the design system remembers it. A
        // failure to write is reported to the caller instead of being swallowed,
        // because a setting that silently does not stick is worse than none.
        public static string Persist()
        {
            if (settingsPath == null) return "no settings folder";
            try
            {
                string folder = Path.GetDirectoryName(settingsPath);
                if (!String.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(settingsPath, mode);
                return null;
            }
            catch (IOException error)
            {
                return error.Message;
            }
            catch (UnauthorizedAccessException error)
            {
                return error.Message;
            }
        }

        public static void Install(ResourceDictionary target)
        {
            if (target == null) return;
            if (Brushes.Count == 0) BuildBrushes();
            if (!Installed.Contains(target)) Installed.Add(target);
            PublishBrushes(target);
            InstallStyles(target);
        }

        // WPF seals whatever a Style setter or a template resolves out of a
        // resource dictionary, and a sealed brush throws when its colour is
        // written. Measured on this theme, 12 of the 30 brushes end up sealed
        // once the styles are realised. So the dictionary never receives the
        // shared instances that elements hold directly: it gets its own copies,
        // and a theme change replaces those entries instead of writing to them.
        // Elements that took a brush directly repaint from the colour change,
        // elements that took it through DynamicResource repaint from the swap.
        private static void PublishBrushes(ResourceDictionary target)
        {
            foreach (KeyValuePair<string, SolidColorBrush> pair in Brushes)
            {
                target[pair.Key] = new SolidColorBrush(pair.Value.Color);
            }
        }

        public static void SetMode(string next)
        {
            string wanted = String.Equals(next, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
            if (String.Equals(wanted, mode, StringComparison.Ordinal)) return;
            mode = wanted;
            ApplyColors();
        }

        public static void Toggle()
        {
            SetMode(IsDark ? Light : Dark);
        }

        private static void BuildBrushes()
        {
            string[] keys = ColorKeys();
            for (int index = 0; index < keys.Length; index++)
            {
                SolidColorBrush brush = new SolidColorBrush(Colors.Transparent);
                Brushes[keys[index]] = brush;
            }
            ApplyColors();
        }

        private static string[] ColorKeys()
        {
            return new string[]
            {
                "SurfaceCanvas", "Surface", "SurfaceSunken", "SurfaceHover", "SurfaceSelected", "SurfaceCode",
                "Text", "TextSub", "TextMuted", "TextDisabled", "TextOnAccent",
                "Border", "BorderSubtle", "BorderStrong",
                "Accent", "AccentHover", "AccentSoft", "AccentText",
                "Danger", "DangerText", "DangerSoft",
                "Caution", "CautionText", "CautionSoft",
                "Success", "SuccessText", "SuccessSoft",
                "Focus", "Shadow", "TopbarBackground",
                "CodeComment", "CodeString", "CodeKeyword", "CodeNumber", "CodeVariable",
                "CodeGutter", "CodeGutterText", "CodeFindMark"
            };
        }

        private static void ApplyColors()
        {
            if (Brushes.Count == 0) return;
            bool dark = IsDark;

            // Light column mirrors the design system :root, dark column mirrors
            // :root[data-theme="dark"].
            Set("SurfaceCanvas", dark ? "#14171B" : "#F4F6F8");
            Set("Surface", dark ? "#1C2127" : "#FFFFFF");
            Set("SurfaceSunken", dark ? "#101317" : "#ECEFF2");
            Set("SurfaceHover", dark ? "#232931" : "#FAFBFC");
            Set("SurfaceSelected", dark ? "#1E2A3A" : "#F2F6FB");
            Set("SurfaceCode", dark ? "#191E24" : "#FBFCFD");
            Set("Text", dark ? "#E9EDF2" : "#1F2A37");
            Set("TextSub", dark ? "#C2CBD6" : "#3C4B5C");
            Set("TextMuted", dark ? "#97A3B1" : "#5D6B7A");
            Set("TextDisabled", dark ? "#5C6874" : "#9FACB9");
            Set("TextOnAccent", "#FFFFFF");
            Set("Border", dark ? "#333B45" : "#CFD7DE");
            Set("BorderSubtle", dark ? "#272E37" : "#E2E7EC");
            Set("BorderStrong", dark ? "#46515D" : "#B4BFCA");
            Set("Accent", dark ? "#3D72B4" : "#2B5C96");
            Set("AccentHover", dark ? "#4A80C4" : "#234C7D");
            Set("AccentSoft", dark ? "#223650" : "#E5EDF7");
            Set("AccentText", dark ? "#8FB8E8" : "#24507F");
            Set("Danger", dark ? "#C25560" : "#B03E48");
            Set("DangerText", dark ? "#ED9AA3" : "#93343C");
            Set("DangerSoft", dark ? "#3A2226" : "#FBE9EA");
            Set("Caution", dark ? "#B58A3C" : "#A2701C");
            Set("CautionText", dark ? "#E4C179" : "#7A5314");
            Set("CautionSoft", dark ? "#2E2718" : "#FBF3E2");
            Set("Success", dark ? "#37804A" : "#3A7A47");
            Set("SuccessText", dark ? "#8FCB99" : "#2F6339");
            Set("SuccessSoft", dark ? "#22321F" : "#E4F1E4");
            Set("Focus", dark ? "#79A9DC" : "#9DBBDE");
            Set("Shadow", dark ? "#00000073" : "#17202C1A");
            Set("TopbarBackground", dark ? "#1C2127" : "#FFFFFF");

            // Code colours. Muted on purpose: the point is to tell a comment
            // from a string at a glance, not to make the editor the loudest
            // thing on the screen.
            Set("CodeComment", dark ? "#6E7C88" : "#7A8791");
            Set("CodeString", dark ? "#C08A5E" : "#9A5B28");
            Set("CodeKeyword", dark ? "#7FA8D8" : "#2B5C96");
            Set("CodeNumber", dark ? "#8FBF9B" : "#33704A");
            Set("CodeVariable", dark ? "#C7A0CE" : "#7A4A85");
            Set("CodeGutter", dark ? "#171B21" : "#F1F4F6");
            Set("CodeGutterText", dark ? "#5A646E" : "#9AA4AC");
            Set("CodeFindMark", dark ? "#4A4320" : "#FBEFC0");

            for (int index = 0; index < Installed.Count; index++) PublishBrushes(Installed[index]);
        }

        private static void Set(string key, string hex)
        {
            SolidColorBrush brush;
            if (!Brushes.TryGetValue(key, out brush)) return;
            brush.Color = Parse(hex);
        }

        public static Color Parse(string hex)
        {
            string value = hex.TrimStart('#');
            byte a = 255;
            int offset = 0;
            if (value.Length == 8)
            {
                a = Byte.Parse(value.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            byte r = Byte.Parse(value.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = Byte.Parse(value.Substring(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = Byte.Parse(value.Substring(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(a, r, g, b);
        }

        // ---- control styles -------------------------------------------------

        private static void InstallStyles(ResourceDictionary target)
        {
            string[] fragments = StyleXaml();
            for (int index = 0; index < fragments.Length; index++)
            {
                ResourceDictionary parsed;
                try
                {
                    parsed = (ResourceDictionary)XamlReader.Parse(Wrap(fragments[index]));
                }
                catch (Exception error)
                {
                    // A parse failure here is a build mistake, not a runtime
                    // condition. Say which fragment and where, so it is one
                    // read rather than a hunt.
                    throw new InvalidOperationException(
                        "Theme style fragment " + index + " (" + FragmentNames()[index] + ") failed to parse: " + error.Message, error);
                }
                foreach (object key in parsed.Keys)
                {
                    target[key] = parsed[key];
                }
            }
        }

        private static string[] FragmentNames()
        {
            return new string[] { "scrollbar", "button", "textbox", "list", "accordion", "checkbox", "combobox", "wraptemplate", "tree", "controls" };
        }

        private static string Wrap(string body)
        {
            return "<ResourceDictionary " +
                "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                "xmlns:sys='clr-namespace:System;assembly=mscorlib'>" + body + "</ResourceDictionary>";
        }

        // The templates are written as XAML text rather than assembled from
        // FrameworkElementFactory, because a template is a shape and reads far
        // better as one. Nothing here contains display text, so the sources stay
        // ASCII and the Japanese wording stays in assets/messages.
        private static string[] StyleXaml()
        {
            List<string> parts = new List<string>();

            parts.Add(ScrollBarXaml());
            parts.Add(ButtonXaml());
            parts.Add(TextBoxXaml());
            parts.Add(ListXaml());
            parts.Add(ExpanderXaml());
            parts.Add(CheckBoxXaml());
            parts.Add(ComboBoxXaml());
            parts.Add(WrapTemplateXaml());
            parts.Add(TreeXaml());
            parts.Add(ControlsXaml());

            return parts.ToArray();
        }

        // The parts the restructured window is assembled from: a switch that
        // looks like a switch, a button that carries a drawing instead of a
        // sentence, a tab, and a slider.
        //
        // A setting that is either on or off is a switch. It used to be a button
        // whose label said which state it was in - "Pointer info: on" - which
        // asks the reader to work out from the caption whether they are looking
        // at the current state or at the offer. A switch answers that by its
        // shape: the knob is on the side the state is, and the sentence beside it
        // says what the setting is about rather than what pressing it does.
        private static string ControlsXaml()
        {
            return
"<Style x:Key='AppSwitch' TargetType='ToggleButton'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Cursor' Value='Hand'/>" +
"  <Setter Property='HorizontalAlignment' Value='Left'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='Focusable' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Border Background='Transparent' Padding='0,3,0,3'>" +
"          <StackPanel Orientation='Horizontal'>" +
"            <Border x:Name='track' Width='38' Height='22' CornerRadius='11' VerticalAlignment='Center' " +
"                    Background='{DynamicResource SurfaceSunken}' BorderBrush='{DynamicResource Border}' BorderThickness='1'>" +
"              <Border x:Name='knob' Width='16' Height='16' CornerRadius='8' HorizontalAlignment='Left' Margin='2,0,0,0' " +
"                      Background='{DynamicResource TextMuted}'/>" +
"            </Border>" +
"            <ContentPresenter x:Name='label' Margin='10,0,0,0' VerticalAlignment='Center' " +
"                              TextBlock.Foreground='{DynamicResource Text}'/>" +
"          </StackPanel>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='track' Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='track' Property='Background' Value='{DynamicResource Accent}'/>" +
"            <Setter TargetName='track' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"            <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>" +
"            <Setter TargetName='knob' Property='Margin' Value='0,0,2,0'/>" +
"            <Setter TargetName='knob' Property='Background' Value='{DynamicResource TextOnAccent}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='track' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='track' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='label' Property='TextBlock.Foreground' Value='{DynamicResource TextDisabled}'/>" +
"            <Setter TargetName='track' Property='Opacity' Value='0.5'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppIconButton' TargetType='Button'>" +
"  <Setter Property='Width' Value='40'/>" +
"  <Setter Property='Height' Value='40'/>" +
"  <Setter Property='Cursor' Value='Hand'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Button'>" +
"        <Border x:Name='shell' CornerRadius='4' Background='Transparent' BorderBrush='Transparent' BorderThickness='1'>" +
"          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Border}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='shell' Property='Opacity' Value='0.35'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppIconToggle' TargetType='ToggleButton'>" +
"  <Setter Property='Width' Value='40'/>" +
"  <Setter Property='Height' Value='40'/>" +
"  <Setter Property='Cursor' Value='Hand'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Border x:Name='shell' CornerRadius='4' Background='Transparent' BorderBrush='Transparent' BorderThickness='1'>" +
"          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Border}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource AccentSoft}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='shell' Property='Opacity' Value='0.35'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppTab' TargetType='ToggleButton'>" +
"  <Setter Property='MinHeight' Value='34'/>" +
"  <Setter Property='Cursor' Value='Hand'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='FontWeight' Value='SemiBold'/>" +
"  <Setter Property='Padding' Value='12,0,12,0'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Grid Background='Transparent'>" +
"          <Grid.RowDefinitions>" +
"            <RowDefinition Height='*'/>" +
"            <RowDefinition Height='2'/>" +
"          </Grid.RowDefinitions>" +
"          <ContentPresenter x:Name='label' Grid.Row='0' HorizontalAlignment='Center' VerticalAlignment='Center' " +
"                            Margin='{TemplateBinding Padding}' TextBlock.Foreground='{DynamicResource TextMuted}'/>" +
"          <Border x:Name='rule' Grid.Row='1' Background='Transparent' CornerRadius='1'/>" +
"        </Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='label' Property='TextBlock.Foreground' Value='{DynamicResource TextSub}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='label' Property='TextBlock.Foreground' Value='{DynamicResource Text}'/>" +
"            <Setter TargetName='rule' Property='Background' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='rule' Property='Background' Value='{DynamicResource Focus}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppSliderThumb' TargetType='Thumb'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Width' Value='16'/>" +
"  <Setter Property='Height' Value='16'/>" +
"  <Setter Property='Cursor' Value='Hand'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Thumb'>" +
"        <Border CornerRadius='8' Background='{DynamicResource Accent}' " +
"                BorderBrush='{DynamicResource Surface}' BorderThickness='2'/>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppSliderRepeat' TargetType='RepeatButton'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Focusable' Value='False'/>" +
"  <Setter Property='IsTabStop' Value='False'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='RepeatButton'>" +
"        <Border Background='Transparent'/>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppSlider' TargetType='Slider'>" +
"  <Setter Property='Height' Value='24'/>" +
"  <Setter Property='IsMoveToPointEnabled' Value='True'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Slider'>" +
"        <Grid Background='Transparent'>" +
"          <Border Height='4' CornerRadius='2' VerticalAlignment='Center' " +
"                  Background='{DynamicResource SurfaceSunken}' BorderBrush='{DynamicResource Border}' BorderThickness='1'/>" +
"          <Track x:Name='PART_Track'>" +
"            <Track.DecreaseRepeatButton>" +
"              <RepeatButton Style='{StaticResource AppSliderRepeat}' Command='Slider.DecreaseLarge'/>" +
"            </Track.DecreaseRepeatButton>" +
"            <Track.Thumb>" +
"              <Thumb Style='{StaticResource AppSliderThumb}'/>" +
"            </Track.Thumb>" +
"            <Track.IncreaseRepeatButton>" +
"              <RepeatButton Style='{StaticResource AppSliderRepeat}' Command='Slider.IncreaseLarge'/>" +
"            </Track.IncreaseRepeatButton>" +
"          </Track>" +
"        </Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter Property='Opacity' Value='0.4'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppSplitter' TargetType='GridSplitter'>" +
"  <Setter Property='Background' Value='Transparent'/>" +
"  <Setter Property='Width' Value='6'/>" +
"  <Setter Property='HorizontalAlignment' Value='Center'/>" +
"  <Setter Property='VerticalAlignment' Value='Stretch'/>" +
"  <Setter Property='Cursor' Value='SizeWE'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='GridSplitter'>" +
"        <Border Background='Transparent'>" +
"          <Border x:Name='rule' Width='1' Background='{DynamicResource BorderSubtle}' HorizontalAlignment='Center'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='rule' Property='Width' Value='3'/>" +
"            <Setter TargetName='rule' Property='Background' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        // The project tree. The stock TreeViewItem paints the system highlight
        // behind a selected row and expects the header to take its foreground
        // from the template; a header that sets its own colours - which any
        // header with two lines in two weights has to - therefore disappears
        // into the highlight when it is chosen. This template marks a chosen row
        // the way the rest of the product does, with a tinted surface and an
        // accent rail, so a header keeps its own colours and stays readable.
        private static string TreeXaml()
        {
            return
"<Style x:Key='AppTreeToggle' TargetType='ToggleButton'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Focusable' Value='False'/>" +
"  <Setter Property='Width' Value='18'/>" +
"  <Setter Property='Height' Value='18'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Grid Background='Transparent'>" +
"          <Path x:Name='shut' HorizontalAlignment='Center' VerticalAlignment='Center' " +
"                Data='M 0.5,0.5 L 4.5,4.5 L 0.5,8.5' Stroke='{DynamicResource TextMuted}' " +
"                StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>" +
"          <Path x:Name='open' HorizontalAlignment='Center' VerticalAlignment='Center' " +
"                Data='M 0.5,1.5 L 4.5,5.5 L 8.5,1.5' Stroke='{DynamicResource AccentText}' " +
"                StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' " +
"                Visibility='Collapsed'/>" +
"        </Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='shut' Property='Visibility' Value='Collapsed'/>" +
"            <Setter TargetName='open' Property='Visibility' Value='Visible'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppTreeItem' TargetType='TreeViewItem'>" +
"  <Setter Property='Padding' Value='0'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
"  <Setter Property='VerticalContentAlignment' Value='Center'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='TreeViewItem'>" +
"        <StackPanel>" +
"          <Border x:Name='row' MinHeight='34' CornerRadius='4' Margin='0,1,0,1' Background='Transparent'>" +
"            <Grid>" +
"              <Grid.ColumnDefinitions>" +
"                <ColumnDefinition Width='3'/>" +
"                <ColumnDefinition Width='20'/>" +
"                <ColumnDefinition Width='*'/>" +
"              </Grid.ColumnDefinitions>" +
"              <Border x:Name='rail' Grid.Column='0' CornerRadius='2' Margin='0,3,0,3' Background='Transparent'/>" +
"              <ToggleButton x:Name='Expander' Grid.Column='1' ClickMode='Press' " +
"                            Style='{StaticResource AppTreeToggle}' " +
"                            IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>" +
"              <ContentPresenter x:Name='PART_Header' Grid.Column='2' ContentSource='Header' " +
"                                Margin='2,5,8,5' VerticalAlignment='Center'/>" +
"            </Grid>" +
"          </Border>" +
"          <ItemsPresenter x:Name='ItemsHost' Margin='16,0,0,0' Visibility='Collapsed'/>" +
"        </StackPanel>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsExpanded' Value='True'>" +
"            <Setter TargetName='ItemsHost' Property='Visibility' Value='Visible'/>" +
"          </Trigger>" +
"          <Trigger Property='HasItems' Value='False'>" +
"            <Setter TargetName='Expander' Property='Visibility' Value='Hidden'/>" +
"          </Trigger>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsSelected' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceSelected}'/>" +
"            <Setter TargetName='rail' Property='Background' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppTree' TargetType='TreeView'>" +
"  <Setter Property='Background' Value='Transparent'/>" +
"  <Setter Property='BorderThickness' Value='0'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='ItemContainerStyle' Value='{StaticResource AppTreeItem}'/>" +
"  <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='TreeView'>" +
"        <ScrollViewer Focusable='False' Padding='0' " +
"                      VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>" +
"          <ItemsPresenter SnapsToDevicePixels='True'/>" +
"        </ScrollViewer>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        private static string ScrollBarXaml()
        {
            return
"<Style x:Key='AppScrollThumb' TargetType='Thumb'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='IsTabStop' Value='False'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Thumb'>" +
"        <Border x:Name='bar' Background='{DynamicResource BorderStrong}' CornerRadius='3' Margin='3,2,3,2'/>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='bar' Property='Background' Value='{DynamicResource TextMuted}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +
"<Style TargetType='ScrollBar'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Background' Value='Transparent'/>" +
"  <Setter Property='Width' Value='11'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ScrollBar'>" +
"        <Grid Background='Transparent'>" +
"          <Track x:Name='PART_Track' IsDirectionReversed='True'>" +
"            <Track.Thumb><Thumb Style='{StaticResource AppScrollThumb}'/></Track.Thumb>" +
"            <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False' IsTabStop='False'/></Track.IncreaseRepeatButton>" +
"            <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False' IsTabStop='False'/></Track.DecreaseRepeatButton>" +
"          </Track>" +
"        </Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='Orientation' Value='Horizontal'>" +
"            <Setter Property='Height' Value='11'/>" +
"            <Setter Property='Width' Value='Auto'/>" +
"            <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        private static string ButtonXaml()
        {
            // One normal size and one compact size, exactly as the design system
            // states it. Colour, border and offset move together so
            // a press reads as a press.
            string body =
"          <Border x:Name='shell' CornerRadius='4' Background='{TemplateBinding Background}' " +
"                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' SnapsToDevicePixels='True'>" +
"            <ContentPresenter x:Name='label' HorizontalAlignment='Center' VerticalAlignment='Center' " +
"                              Margin='{TemplateBinding Padding}' RecognizesAccessKey='True'/>" +
"          </Border>";

            return
"<Style x:Key='AppButton' TargetType='Button'>" +
"  <Setter Property='Height' Value='40'/>" +
"  <Setter Property='MinWidth' Value='96'/>" +
"  <Setter Property='Padding' Value='16,0,16,0'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='FontWeight' Value='SemiBold'/>" +
"  <Setter Property='Background' Value='{DynamicResource Surface}'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource Border}'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource TextSub}'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Button'>" +
"        <Grid>" + body + "</Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsPressed' Value='True'>" +
"            <Setter TargetName='label' Property='RenderTransform'>" +
"              <Setter.Value><TranslateTransform Y='1'/></Setter.Value>" +
"            </Setter>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceSunken}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderSubtle}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppButtonPrimary' TargetType='Button' BasedOn='{StaticResource AppButton}'>" +
"  <Setter Property='Background' Value='{DynamicResource Accent}'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource TextOnAccent}'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Button'>" +
"        <Grid>" + body + "</Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource AccentHover}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource AccentHover}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsPressed' Value='True'>" +
"            <Setter TargetName='label' Property='RenderTransform'>" +
"              <Setter.Value><TranslateTransform Y='1'/></Setter.Value>" +
"            </Setter>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceSunken}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderSubtle}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppButtonDanger' TargetType='Button' BasedOn='{StaticResource AppButton}'>" +
"  <Setter Property='Background' Value='Transparent'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource Danger}'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource DangerText}'/>" +
"</Style>" +

// One button, one height, everywhere. This used to be a second size, and the
// result was rows in which the two buttons that do the same kind of thing were
// visibly different objects - "replay" and "edit as code" at forty units beside
// "open the report" at thirty four, and, worse, the PowerShell and VBA buttons
// at two different sizes while the product's own rule is that the two languages
// are treated identically. Importance is said with colour. It is never said
// with size, because size is what tells a reader that two things are different
// kinds of thing.
"<Style x:Key='AppButtonCompact' TargetType='Button' BasedOn='{StaticResource AppButton}'>" +
"  <Setter Property='MinWidth' Value='0'/>" +
"</Style>" +

// AppIconButton is defined once, in the controls fragment, because a control
// that carries a drawing is now a shape of its own rather than a small text
// button with the text left out. Two styles under one key is a rule nobody can
// read off the screen: whichever fragment happens to be applied last wins.

// A toggle that reads as a toggle: the same size and shape as a compact
// button, but it says which of two states it is in rather than what
// pressing it will do. Used where a feature has to show that it exists
// and whether it is on, in one control.
"<Style x:Key='AppToggleButton' TargetType='ToggleButton'>" +
"  <Setter Property='Height' Value='40'/>" +
"  <Setter Property='Padding' Value='16,0,16,0'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='FontWeight' Value='SemiBold'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Border x:Name='shell' CornerRadius='4' Background='{DynamicResource Surface}' " +
"                BorderBrush='{DynamicResource Border}' BorderThickness='1' SnapsToDevicePixels='True'>" +
"          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' " +
"                            Margin='{TemplateBinding Padding}' " +
"                            TextBlock.Foreground='{DynamicResource TextSub}'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource AccentSoft}'/>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        private static string TextBoxXaml()
        {
            return
"<Style x:Key='AppTextBox' TargetType='TextBox'>" +
"  <Setter Property='Background' Value='{DynamicResource Surface}'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='CaretBrush' Value='{DynamicResource Text}'/>" +
"  <Setter Property='SelectionBrush' Value='{DynamicResource Focus}'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource Border}'/>" +
"  <Setter Property='BorderThickness' Value='1'/>" +
"  <Setter Property='Padding' Value='10,8,10,8'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='TextBox'>" +
"        <Border x:Name='shell' CornerRadius='4' Background='{TemplateBinding Background}' " +
"                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
"          <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' " +
"                        VerticalScrollBarVisibility='{TemplateBinding VerticalScrollBarVisibility}' " +
"                        HorizontalScrollBarVisibility='{TemplateBinding HorizontalScrollBarVisibility}' " +
"                        Focusable='False'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocusWithin' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"            <Setter TargetName='shell' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='shell' Property='Background' Value='{DynamicResource SurfaceSunken}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppReadOnlyText' TargetType='TextBox' BasedOn='{StaticResource AppTextBox}'>" +
"  <Setter Property='Background' Value='{DynamicResource SurfaceCode}'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource TextSub}'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource BorderSubtle}'/>" +
"</Style>";
        }

        private static string ListXaml()
        {
            // A selected row is marked twice: a tinted background and an accent
            // rail on the leading edge. One of the two survives every contrast
            // setting, so "which row am I on" never depends on colour alone.
            return
"<Style x:Key='AppListBox' TargetType='ListBox'>" +
"  <Setter Property='Background' Value='{DynamicResource Surface}'/>" +
"  <Setter Property='BorderBrush' Value='{DynamicResource Border}'/>" +
"  <Setter Property='BorderThickness' Value='1'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='Padding' Value='0'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Disabled'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ListBox'>" +
"        <Border CornerRadius='6' Background='{TemplateBinding Background}' " +
"                BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
"                SnapsToDevicePixels='True'>" +
"          <ScrollViewer Focusable='False' Padding='{TemplateBinding Padding}'>" +
"            <ItemsPresenter SnapsToDevicePixels='True'/>" +
"          </ScrollViewer>" +
"        </Border>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppListItem' TargetType='ListBoxItem'>" +
"  <Setter Property='Padding' Value='0'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ListBoxItem'>" +
"        <Border x:Name='row' MinHeight='40' Background='Transparent' BorderBrush='{DynamicResource BorderSubtle}' BorderThickness='0,0,0,1'>" +
"          <Grid>" +
"            <Grid.ColumnDefinitions>" +
"              <ColumnDefinition Width='3'/>" +
"              <ColumnDefinition Width='*'/>" +
"            </Grid.ColumnDefinitions>" +
"            <Border x:Name='rail' Grid.Column='0' Background='Transparent'/>" +
"            <ContentPresenter Grid.Column='1' Margin='12,9,12,9' VerticalAlignment='Center'/>" +
"          </Grid>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsSelected' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceSelected}'/>" +
"            <Setter TargetName='rail' Property='Background' Value='{DynamicResource Accent}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        private static string ExpanderXaml()
        {
            // The accordion. Closed, it still says what is inside: a caption on
            // the left and a summary on the right, both supplied by the caller.
            // The chevron turns; nothing else moves.
            return
"<Style x:Key='AppAccordionToggle' TargetType='ToggleButton'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='Focusable' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Border x:Name='head' MinHeight='40' Background='{DynamicResource SurfaceHover}' CornerRadius='5' Padding='12,8,12,8'>" +
"          <Grid>" +
"            <Grid.ColumnDefinitions>" +
"              <ColumnDefinition Width='14'/>" +
"              <ColumnDefinition Width='*'/>" +
"            </Grid.ColumnDefinitions>" +
"            <Grid Grid.Column='0' VerticalAlignment='Center' HorizontalAlignment='Left' Width='9' Height='9'>" +
"              <Path x:Name='shut' Data='M 0.5,0.5 L 4.5,4.5 L 0.5,8.5' Stroke='{DynamicResource TextMuted}' " +
"                    StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>" +
"              <Path x:Name='open' Data='M 0.5,1.5 L 4.5,5.5 L 8.5,1.5' Stroke='{DynamicResource AccentText}' " +
"                    StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' " +
"                    Visibility='Collapsed'/>" +
"            </Grid>" +
"            <ContentPresenter Grid.Column='1' VerticalAlignment='Center'/>" +
"          </Grid>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='shut' Property='Visibility' Value='Collapsed'/>" +
"            <Setter TargetName='open' Property='Visibility' Value='Visible'/>" +
"          </Trigger>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='head' Property='Background' Value='{DynamicResource SurfaceSelected}'/>" +
"            <Setter TargetName='shut' Property='Stroke' Value='{DynamicResource AccentText}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='head' Property='Background' Value='{DynamicResource SurfaceSelected}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppAccordion' TargetType='Expander'>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='Expander'>" +
"        <Border CornerRadius='6' Background='{DynamicResource Surface}' " +
"                BorderBrush='{DynamicResource BorderSubtle}' BorderThickness='1' SnapsToDevicePixels='True'>" +
"          <DockPanel>" +
// The header is a panel, so this button is what keyboard focus actually lands
// on, and its content gives it no name. The Expander already carries the stated
// name, so the button borrows it rather than being reached as an unnamed button.
"            <ToggleButton x:Name='HeaderSite' DockPanel.Dock='Top' " +
"                          Style='{StaticResource AppAccordionToggle}' " +
"                          Content='{TemplateBinding Header}' " +
"                          AutomationProperties.Name='{Binding Path=(AutomationProperties.Name), RelativeSource={RelativeSource TemplatedParent}}' " +
"                          IsChecked='{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>" +
"            <Border x:Name='body' Visibility='Collapsed' Padding='10,2,10,10'>" +
"              <ContentPresenter/>" +
"            </Border>" +
"          </DockPanel>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsExpanded' Value='True'>" +
"            <Setter TargetName='body' Property='Visibility' Value='Visible'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        private static string CheckBoxXaml()
        {
            // A box that is unmistakably a box, with a tick that is unmistakably
            // a tick. Same component wherever a yes / no permission is asked.
            return
"<Style x:Key='AppCheckBox' TargetType='CheckBox'>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='CheckBox'>" +
"        <Border x:Name='hit' Background='Transparent' Padding='0,3,0,3'>" +
"          <Grid>" +
"            <Grid.ColumnDefinitions>" +
"              <ColumnDefinition Width='Auto'/>" +
"              <ColumnDefinition Width='*'/>" +
"            </Grid.ColumnDefinitions>" +
"            <Border x:Name='box' Grid.Column='0' Width='17' Height='17' CornerRadius='4' " +
"                    Background='{DynamicResource Surface}' BorderBrush='{DynamicResource BorderStrong}' " +
"                    BorderThickness='1.4' VerticalAlignment='Top' Margin='0,1,9,0'>" +
"              <Path x:Name='tick' Data='M 2,6 L 5.5,9.5 L 11,3' Stroke='{DynamicResource TextOnAccent}' " +
"                    StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' " +
"                    StrokeLineJoin='Round' Visibility='Collapsed'/>" +
"            </Border>" +
"            <ContentPresenter Grid.Column='1' VerticalAlignment='Center' " +
"                              TextBlock.Foreground='{TemplateBinding Foreground}'/>" +
"          </Grid>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='box' Property='Background' Value='{DynamicResource Accent}'/>" +
"            <Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"            <Setter TargetName='tick' Property='Visibility' Value='Visible'/>" +
"          </Trigger>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsKeyboardFocused' Value='True'>" +
"            <Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource Focus}'/>" +
"            <Setter TargetName='box' Property='BorderThickness' Value='2'/>" +
"          </Trigger>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter TargetName='box' Property='Background' Value='{DynamicResource SurfaceSunken}'/>" +
"            <Setter TargetName='box' Property='BorderBrush' Value='{DynamicResource BorderSubtle}'/>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }

        // Rows that hold a sentence rather than a label have to wrap, or the
        // sentence is silently cut in half at the right edge.
        private static string WrapTemplateXaml()
        {
            return
"<DataTemplate x:Key='AppWrapRow'>" +
"  <TextBlock Text='{Binding}' TextWrapping='Wrap' Foreground='{DynamicResource Text}' FontSize='13'/>" +
"</DataTemplate>";
        }

        private static string ComboBoxXaml()
        {
            return
"<Style x:Key='AppComboToggle' TargetType='ToggleButton'>" +
"  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
"  <Setter Property='IsTabStop' Value='False'/>" +
"  <Setter Property='Focusable' Value='False'/>" +
"  <Setter Property='ClickMode' Value='Press'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ToggleButton'>" +
"        <Border x:Name='shell' CornerRadius='4' Background='{DynamicResource Surface}' " +
"                BorderBrush='{DynamicResource Border}' BorderThickness='1' SnapsToDevicePixels='True'>" +
"          <Path x:Name='arrow' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0' " +
"                Data='M 0,0 L 4,4 L 8,0' Stroke='{DynamicResource TextMuted}' StrokeThickness='1.6' " +
"                StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource BorderStrong}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsChecked' Value='True'>" +
"            <Setter TargetName='shell' Property='BorderBrush' Value='{DynamicResource Accent}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppComboBox' TargetType='ComboBox'>" +
"  <Setter Property='Height' Value='36'/>" +
"  <Setter Property='FontSize' Value='13'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ComboBox'>" +
"        <Grid>" +
"          <ToggleButton x:Name='PART_Toggle' Style='{StaticResource AppComboToggle}' " +
"                        IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>" +
"          <ContentPresenter Content='{TemplateBinding SelectionBoxItem}' " +
"                            ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' " +
"                            Margin='10,0,26,0' VerticalAlignment='Center' IsHitTestVisible='False' " +
"                            TextBlock.Foreground='{TemplateBinding Foreground}'/>" +
"          <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' " +
"                 IsOpen='{TemplateBinding IsDropDownOpen}' Focusable='False' PopupAnimation='None'>" +
"            <Border Background='{DynamicResource Surface}' BorderBrush='{DynamicResource BorderStrong}' " +
"                    BorderThickness='1' CornerRadius='6' Margin='0,3,0,0' " +
"                    MinWidth='{TemplateBinding ActualWidth}' SnapsToDevicePixels='True'>" +
"              <ScrollViewer MaxHeight='240'><ItemsPresenter/></ScrollViewer>" +
"            </Border>" +
"          </Popup>" +
"        </Grid>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsEnabled' Value='False'>" +
"            <Setter Property='Foreground' Value='{DynamicResource TextDisabled}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>" +

"<Style x:Key='AppComboItem' TargetType='ComboBoxItem'>" +
"  <Setter Property='Padding' Value='10,7,10,7'/>" +
"  <Setter Property='Foreground' Value='{DynamicResource Text}'/>" +
"  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>" +
"  <Setter Property='Template'>" +
"    <Setter.Value>" +
"      <ControlTemplate TargetType='ComboBoxItem'>" +
"        <Border x:Name='row' Background='Transparent' Padding='{TemplateBinding Padding}' CornerRadius='4' Margin='3,2,3,2'>" +
"          <ContentPresenter/>" +
"        </Border>" +
"        <ControlTemplate.Triggers>" +
"          <Trigger Property='IsMouseOver' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceHover}'/>" +
"          </Trigger>" +
"          <Trigger Property='IsHighlighted' Value='True'>" +
"            <Setter TargetName='row' Property='Background' Value='{DynamicResource SurfaceSelected}'/>" +
"          </Trigger>" +
"        </ControlTemplate.Triggers>" +
"      </ControlTemplate>" +
"    </Setter.Value>" +
"  </Setter>" +
"</Style>";
        }
    }
}
