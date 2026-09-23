using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;
using WinSW.Gui.Theme;

namespace WinSW.Gui
{
    public partial class App : Application
    {
        private SingleInstance? instance;

        /// <summary>A .xml given as the first argument: "WinSW.Gui.exe myapp.xml" or the Explorer verb.</summary>
        public static string? StartupConfigPath { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0 && e.Args[0].EndsWith(".xml", System.StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(e.Args[0]))
            {
                StartupConfigPath = System.IO.Path.GetFullPath(e.Args[0]);
            }

            bool startInTray = HasArgument(e, Autostart.TrayArgument);

            // A configuration to open is something the running copy was not told about, so that
            // launch goes ahead on its own; see SingleInstance.
            if (StartupConfigPath is null)
            {
                this.instance = SingleInstance.Claim(replacing: HasArgument(e, Elevation.ReplaceArgument), wake: !startInTray);
                if (this.instance is null)
                {
                    this.Shutdown();
                    return;
                }
            }

            // A crash dialog with the message beats the process silently disappearing,
            // which is what an unhandled exception on the dispatcher otherwise produces.
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;

            // The dispatcher hook only sees the UI thread. A command that fails inside an
            // awaited continuation, and a task nobody awaited, arrive by their own routes.
            AsyncRelayCommand.UnhandledException += OnCommandFailed;
            AppDomain.CurrentDomain.UnhandledException += OnBackgroundThreadException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // GBK and the other legacy code pages are not in .NET's default encoding set;
            // the log viewer needs them for output from console programs.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Localizer.Initialize();
            ThemeManager.Initialize();

            base.OnStartup(e);

            // Created here rather than from StartupUri, which would show it: a start at sign-in
            // puts only the tray icon on screen.
            var window = new MainWindow();
            this.MainWindow = window;
            if (startInTray)
            {
                window.StartInTray();
            }
            else
            {
                window.Show();
            }

            this.instance?.OnShowRequested(() => this.Dispatcher.BeginInvoke(window.BringToFront));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            this.instance?.Dispose();
            base.OnExit(e);
        }

        private static bool HasArgument(StartupEventArgs e, string argument) =>
            Array.Exists(e.Args, a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Report(e.Exception, fatal: false);
            e.Handled = true;
        }

        /// <summary>A command that threw. The application is intact; the operation is not.</summary>
        private static void OnCommandFailed(Exception exception) => Report(exception, fatal: false);

        /// <summary>
        /// A background thread threw. The runtime is on its way down and nothing here can stop
        /// it, so the only thing worth doing is saying what happened before it goes.
        /// </summary>
        private static void OnBackgroundThreadException(object sender, UnhandledExceptionEventArgs e) =>
            Report(e.ExceptionObject as Exception, fatal: true);

        /// <summary>
        /// A faulted task nobody awaited. Since .NET 4.5 this no longer kills the process, and
        /// it is not worth a dialog — but it is worth not being invisible, because it is how a
        /// fire-and-forget refresh fails.
        /// </summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        }

        /// <summary>
        /// Shows the failure in the application's own language. The exception text is kept —
        /// it is what makes a report actionable — but it is put below a sentence that says
        /// what happened, rather than being the whole message.
        /// </summary>
        private static void Report(Exception? exception, bool fatal)
        {
            string headline = Localizer.Get(fatal ? "M.App.CrashFatal" : "M.App.CrashMessage");
            string detail = exception?.ToString() ?? Localizer.Get("M.App.CrashUnknown");

            void Show() => MessageBox.Show(
                headline + Environment.NewLine + Environment.NewLine + detail,
                Localizer.Get("M.App.CrashTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Report can arrive on any thread; MessageBox has to be shown on one with a
            // dispatcher, and on the way down there may no longer be one to marshal to.
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Show();
            }
            else
            {
                dispatcher.Invoke(Show);
            }
        }
    }
}
