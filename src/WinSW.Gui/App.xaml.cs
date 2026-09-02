using System.Text;
using System.Windows;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Theme;

namespace WinSW.Gui
{
    public partial class App : Application
    {
        /// <summary>A .xml given as the first argument: "WinSW.Gui.exe myapp.xml" or the Explorer verb.</summary>
        public static string? StartupConfigPath { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 && e.Args[0].EndsWith(".xml", System.StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(e.Args[0]))
            {
                StartupConfigPath = System.IO.Path.GetFullPath(e.Args[0]);
            }

            // A crash dialog with the message beats the process silently disappearing,
            // which is what an unhandled exception on the dispatcher otherwise produces.
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;

            // GBK and the other legacy code pages are not in .NET's default encoding set;
            // the log viewer needs them for output from console programs.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Before base.OnStartup, which creates the main window from StartupUri.
            Localizer.Initialize();
            ThemeManager.Initialize();

            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                e.Exception.ToString(),
                "WinSW — unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
