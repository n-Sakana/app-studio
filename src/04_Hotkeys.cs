namespace AppStudio
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Windows;
    using System.Windows.Interop;

    public sealed class HotkeyRegistration
    {
        public string Action;
        public string Combo;
        public bool Registered;
        public string Reason;
    }

    public sealed class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private readonly IntPtr window;
        private readonly HwndSource source;
        private readonly Dictionary<int, string> actions = new Dictionary<int, string>();
        private readonly List<HotkeyRegistration> registrations = new List<HotkeyRegistration>();
        private readonly string settingsPath;

        public event Action<string> Pressed;

        public HotkeyManager(Window owner)
            : this(owner, null)
        {
        }

        public HotkeyManager(Window owner, string path)
        {
            window = new WindowInteropHelper(owner).EnsureHandle();
            source = HwndSource.FromHwnd(window);
            source.AddHook(Hook);
            settingsPath = path;
            Dictionary<string, string> saved = Load(path);
            RegisterWithFallback(1, "toggle", Saved(saved, "toggle", "F8"));
            RegisterWithFallback(2, "freeze", Saved(saved, "freeze", "F9"));
            RegisterWithFallback(3, "pin", Saved(saved, "pin", "F10"));
            RegisterWithFallback(4, "fullShot", Saved(saved, "fullShot", "F11"));
            RegisterWithFallback(5, "memo", Saved(saved, "memo", "F6"));
            RegisterWithFallback(6, "emergency", Saved(saved, "emergency", "Shift+F12"));
            Save();
        }

        public HotkeyRegistration[] Registrations
        {
            get { return registrations.ToArray(); }
        }

        public void Dispose()
        {
            foreach (int id in actions.Keys) UnregisterHotKey(window, id);
            actions.Clear();
            source.RemoveHook(Hook);
        }

        public HotkeyRegistration Reassign(string action, string combo)
        {
            int id = ActionId(action);
            if (id == 0) throw new ArgumentException("Unknown hotkey action.", "action");
            if (actions.ContainsKey(id))
            {
                UnregisterHotKey(window, id);
                actions.Remove(id);
            }
            for (int index = registrations.Count - 1; index >= 0; index--) if (registrations[index].Action == action) registrations.RemoveAt(index);
            RegisterWithFallback(id, action, combo);
            Save();
            return registrations[registrations.Count - 1];
        }

        private void Register(int id, string action, string combo, uint modifiers, uint key)
        {
            bool registered = RegisterHotKey(window, id, modifiers, key);
            HotkeyRegistration item = new HotkeyRegistration();
            item.Action = action;
            item.Combo = combo;
            item.Registered = registered;
            item.Reason = registered ? null : "HOTKEY-TAKEN";
            registrations.Add(item);
            if (registered) actions[id] = action;
        }

        private void RegisterWithFallback(int id, string action, string requested)
        {
            uint modifiers;
            uint key;
            if (!Parse(requested, out modifiers, out key)) requested = Default(action);
            Parse(requested, out modifiers, out key);
            bool registered = RegisterHotKey(window, id, modifiers, key);
            HotkeyRegistration item = new HotkeyRegistration();
            item.Action = action;
            item.Combo = requested;
            item.Registered = registered;
            if (registered)
            {
                actions[id] = action;
                registrations.Add(item);
                return;
            }
            string alternative = (modifiers & MOD_SHIFT) == 0 ? "Shift+" + KeyName(key) : "Ctrl+" + requested;
            uint alternativeModifiers;
            uint alternativeKey;
            Parse(alternative, out alternativeModifiers, out alternativeKey);
            bool alternativeRegistered = RegisterHotKey(window, id, alternativeModifiers, alternativeKey);
            item.Combo = alternativeRegistered ? alternative : requested;
            item.Registered = alternativeRegistered;
            item.Reason = alternativeRegistered ? "HOTKEY-TAKEN " + requested + "; registered alternative " + alternative + "." : "HOTKEY-TAKEN " + requested + " and alternative " + alternative + ".";
            if (alternativeRegistered) actions[id] = action;
            registrations.Add(item);
        }

        private void Save()
        {
            if (String.IsNullOrWhiteSpace(settingsPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath)));
                StringBuilder text = new StringBuilder();
                for (int index = 0; index < registrations.Count; index++) text.Append(registrations[index].Action).Append('=').AppendLine(registrations[index].Combo);
                File.WriteAllText(settingsPath, text.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static Dictionary<string, string> Load(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int index = 0; index < lines.Length; index++)
                {
                    int separator = lines[index].IndexOf('=');
                    if (separator > 0) result[lines[index].Substring(0, separator).Trim()] = lines[index].Substring(separator + 1).Trim();
                }
            }
            catch
            {
            }
            return result;
        }

        private static string Saved(Dictionary<string, string> values, string action, string fallback)
        {
            string value;
            return values.TryGetValue(action, out value) ? value : fallback;
        }

        private static bool Parse(string combo, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (String.IsNullOrWhiteSpace(combo)) return false;
            string[] pieces = combo.Split('+');
            string keyText = pieces[pieces.Length - 1].Trim();
            for (int index = 0; index < pieces.Length - 1; index++)
            {
                string modifier = pieces[index].Trim();
                if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_SHIFT;
                else if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_CONTROL;
                else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= MOD_ALT;
                else return false;
            }
            if (keyText.Length >= 2 && (keyText[0] == 'F' || keyText[0] == 'f'))
            {
                int number;
                if (Int32.TryParse(keyText.Substring(1), out number) && number >= 1 && number <= 24)
                {
                    key = unchecked((uint)(0x70 + number - 1));
                    return true;
                }
            }
            return false;
        }

        private static string KeyName(uint key) { return "F" + (key - 0x70 + 1); }
        private static int ActionId(string action) { return action == "toggle" ? 1 : (action == "freeze" ? 2 : (action == "pin" ? 3 : (action == "fullShot" ? 4 : (action == "memo" ? 5 : (action == "emergency" ? 6 : 0))))); }
        private static string Default(string action) { return action == "toggle" ? "F8" : (action == "freeze" ? "F9" : (action == "pin" ? "F10" : (action == "fullShot" ? "F11" : (action == "memo" ? "F6" : "Shift+F12")))); }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WM_HOTKEY)
            {
                string action;
                if (actions.TryGetValue(wParam.ToInt32(), out action))
                {
                    Action<string> handler = Pressed;
                    if (handler != null) handler(action);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
