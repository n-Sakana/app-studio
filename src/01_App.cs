namespace AppStudio
{
    using System;
    using System.IO;
    using System.Windows;

    public static class App
    {
        public const string Name = "App Studio";
        public const string Version = "0.1.0";

        [STAThread]
        public static void Run(string baseDir, string diagnosticsPath, int autoCloseMs)
        {
            try
            {
                DpiAwareness.Enable();
                JsonObject diagnostics = Diagnostics.Collect(baseDir, diagnosticsPath);
                JsonWriter.WriteFile(diagnosticsPath, diagnostics);
                Probe.Configure(baseDir, false);
                Application application = new Application();
                application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                StudioWindow window = new StudioWindow(baseDir, diagnostics, autoCloseMs);
                application.Run(window);
                Probe.Shutdown();
            }
            catch (Exception exception)
            {
                Probe.Shutdown();
                MessageBox.Show(exception.ToString(), Name + " - fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        public static void RunHeadless(string baseDir, string diagnosticsPath)
        {
            JsonObject diagnostics = Diagnostics.Collect(baseDir, diagnosticsPath);
            JsonWriter.WriteFile(diagnosticsPath, diagnostics);
        }
    }
}
