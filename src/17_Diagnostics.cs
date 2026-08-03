namespace AppStudio
{
    using Microsoft.Win32;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Security.Principal;
    using System.Windows.Forms;

    public static class Diagnostics
    {
        public static JsonObject Collect(string baseDir, string diagnosticsPath)
        {
            JsonObject report = new JsonObject();
            report.Add("capturedAt", DateTimeOffset.Now);
            report.Add("os", CollectOs());
            report.Add("user", CollectUser());
            report.Add("process", CollectProcess());
            report.Add("dotnet", CollectDotNet());
            report.Add("uia", CollectUia());
            report.Add("monitors", CollectMonitors());
            report.Add("webview2", CollectWebView2());
            report.Add("powershell", CollectPowerShell());
            report.Add("appLockerPolicyPresent", Unknown("Not measured during phase 1 startup."));
            report.Add("hotkeys", new object[0]);
            report.Add("writeTargets", new object[] {
                new JsonObject().Add("path", Path.GetFullPath(diagnosticsPath)).Add("purpose", "startup diagnostics")
            });
            report.Add("baseDirectory", Path.GetFullPath(baseDir));
            return report;
        }

        public static string Summary(JsonObject report)
        {
            return JsonWriter.Write(report);
        }

        public static JsonObject Unknown(string reason)
        {
            return new JsonObject().Add("value", null).Add("reason", reason);
        }

        private static JsonObject CollectOs()
        {
            return new JsonObject()
                .Add("caption", Unknown("OS caption requires an optional management provider and was not queried."))
                .Add("build", Environment.OSVersion.Version.Build)
                .Add("version", Environment.OSVersion.Version.ToString())
                .Add("arch", Environment.Is64BitOperatingSystem ? "x64" : "x86")
                .Add("locale", CultureInfo.CurrentCulture.Name)
                .Add("timeZone", TimeZoneInfo.Local.Id);
        }

        private static JsonObject CollectUser()
        {
            bool isAdmin = false;
            try
            {
                WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                isAdmin = false;
            }
            return new JsonObject()
                .Add("name", Environment.UserName)
                .Add("isAdmin", isAdmin)
                .Add("integrityLevel", Unknown("The current process token integrity level was not collected."));
        }

        private static JsonObject CollectProcess()
        {
            return new JsonObject()
                .Add("bitness", Environment.Is64BitProcess ? "x64" : "x86")
                .Add("dpiAwareness", new JsonObject().Add("value", DpiAwareness.State).Add("reason", DpiAwareness.Reason))
                .Add("elevated", IsElevated());
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static JsonObject CollectDotNet()
        {
            object release = null;
            try
            {
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key != null)
                    {
                        release = key.GetValue("Release");
                    }
                }
            }
            catch
            {
                release = null;
            }
            return new JsonObject()
                .Add("version", Environment.Version.ToString())
                .Add("release", release == null ? (object)Unknown(".NET Framework Release registry value was unavailable.") : Convert.ToInt64(release, CultureInfo.InvariantCulture));
        }

        private static JsonObject CollectUia()
        {
            bool available = false;
            string reason = null;
            try
            {
                // A plain simple-name load cannot resolve strong-named GAC
                // assemblies, so the full display name is required here.
                Assembly.Load("UIAutomationClient, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                available = true;
            }
            catch (Exception exception)
            {
                reason = exception.GetType().Name + ": " + exception.Message;
            }
            if (!available)
            {
                try
                {
                    #pragma warning disable 618
                    Assembly fallback = Assembly.LoadWithPartialName("UIAutomationClient");
                    #pragma warning restore 618
                    if (fallback != null)
                    {
                        available = true;
                        reason = null;
                    }
                }
                catch (Exception exception)
                {
                    reason = reason + " / LoadWithPartialName: " + exception.GetType().Name;
                }
            }
            string corePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "UIAutomationCore.dll");
            object coreVersion = File.Exists(corePath) ? (object)FileVersionInfo.GetVersionInfo(corePath).FileVersion : Unknown("UIAutomationCore.dll was not found in System32.");
            return new JsonObject()
                .Add("available", available)
                .Add("reason", available ? null : reason)
                .Add("interfaceLevel", available ? (object)"managed UIA" : Unknown("UIAutomationClient could not be loaded."))
                .Add("coreFileVersion", coreVersion);
        }

        private static object[] CollectMonitors()
        {
            List<object> monitors = new List<object>();
            Screen[] screens = Screen.AllScreens;
            for (int index = 0; index < screens.Length; index++)
            {
                Screen screen = screens[index];
                monitors.Add(new JsonObject()
                    .Add("id", screen.DeviceName)
                    .Add("rect", Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height))
                    .Add("dpi", DpiTools.GetDpiAt(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2))
                    .Add("scale", Math.Round(DpiTools.GetDpiAt(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2) / 96.0, 3))
                    .Add("primary", screen.Primary)
                    .Add("orientation", Unknown("Display orientation was not queried.")));
            }
            return monitors.ToArray();
        }

        private static JsonObject CollectWebView2()
        {
            string version = FindWebView2Version();
            return new JsonObject()
                .Add("installed", version != null)
                .Add("version", version == null ? (object)Unknown("Evergreen WebView2 registry entry was not found.") : version);
        }

        private static string FindWebView2Version()
        {
            string[] roots = new string[] {
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F1E7E8D5-7F18-4C16-9E6B-8B0F1B1F1B1F}",
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F1E7E8D5-7F18-4C16-9E6B-8B0F1B1F1B1F}"
            };
            for (int index = 0; index < roots.Length; index++)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(roots[index]))
                    {
                        if (key != null)
                        {
                            object value = key.GetValue("pv");
                            if (value != null)
                            {
                                return Convert.ToString(value, CultureInfo.InvariantCulture);
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static JsonObject CollectPowerShell()
        {
            return new JsonObject()
                .Add("version", Environment.GetEnvironmentVariable("APPSTUDIO_PS_VERSION") ?? (object)Unknown("PowerShell host did not provide its version."))
                .Add("edition", Environment.GetEnvironmentVariable("APPSTUDIO_PS_EDITION") ?? (object)Unknown("PowerShell host did not provide its edition."))
                .Add("executionPolicy", Environment.GetEnvironmentVariable("APPSTUDIO_PS_EXECUTION_POLICY") ?? (object)Unknown("PowerShell host did not provide its execution policy."))
                .Add("languageMode", Environment.GetEnvironmentVariable("APPSTUDIO_PS_LANGUAGE_MODE") ?? (object)Unknown("PowerShell host did not provide its language mode."));
        }

        private static JsonObject Rect(int x, int y, int width, int height)
        {
            return new JsonObject().Add("x", x).Add("y", y).Add("width", width).Add("height", height);
        }
    }
}
