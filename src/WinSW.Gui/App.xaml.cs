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
        /// <summary>Which failures get a dialog, and when; see <see cref="ErrorDialogGate"/>.</summary>
        private static readonly ErrorDialogGate ErrorDialogs = new();

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

            // A configuration to open is handed to the running copy, when there is one that
            // takes it; see SingleInstance.
            this.instance = SingleInstance.Claim(replacing: HasArgument(e, Elevation.ReplaceArgument), wake: !startInTray, configPath: StartupConfigPath);
            if (this.instance is null)
            {
                this.Shutdown();
                return;
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

            this.instance.OnShowRequested(() => this.Dispatcher.BeginInvoke(window.BringToFront));
            this.instance.OnOpenRequested(path => this.Dispatcher.BeginInvoke(() => window.OpenHandedOver(path)));
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
            Report(e.Exception, "UI thread", fatal: false);
            e.Handled = true;
        }

        /// <summary>A command that threw. The application is intact; the operation is not.</summary>
        private static void OnCommandFailed(Exception exception) => Report(exception, "command", fatal: false, command: true);

        /// <summary>
        /// A background thread threw. The runtime is on its way down and nothing here can stop
        /// it, so the only thing worth doing is saying what happened before it goes.
        /// </summary>
        private static void OnBackgroundThreadException(object sender, UnhandledExceptionEventArgs e) =>
            Report(e.ExceptionObject as Exception, "background thread, fatal", fatal: true);

        /// <summary>
        /// A faulted task nobody awaited. Since .NET 4.5 this no longer kills the process, and
        /// it is not worth a dialog — but it is worth not being invisible, because it is how a
        /// fire-and-forget refresh fails. It arrives only when the task is garbage-collected,
        /// which may be long after; <see cref="ErrorLog.Observe"/> records such a failure as it
        /// happens.
        /// </summary>
        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ErrorLog.Record("unobserved task", e.Exception);
            e.SetObserved();
        }

        /// <summary>
        /// Records the failure, then shows it in the application's own language. The exception
        /// text is kept — it is what makes a report actionable — but it is put below a sentence
        /// that says what happened, rather than being the whole message.
        /// </summary>
        /// <remarks>
        /// On disk first: on the fatal path the runtime is going down, and the record must not
        /// wait for a dialog that nobody may answer. The fatal dialog is the process's last act
        /// and is always shown; any other goes through <see cref="ErrorDialogs"/>, which keeps
        /// it to one at a time.
        /// </remarks>
        private static void Report(Exception? exception, string source, bool fatal, bool command = false)
        {
            ErrorLog.Record(source, exception);

            int? repeats = fatal ? 0 : ErrorDialogs.TryOpen(ErrorDialogGate.SignatureOf(exception), DateTime.UtcNow, command);
            if (repeats is null)
            {
                return;
            }

            var text = new StringBuilder(Localizer.Get(fatal ? "M.App.CrashFatal" : "M.App.CrashMessage"));
            if (repeats > 0)
            {
                text.AppendLine().AppendLine().Append(Localizer.Format("M.App.CrashRepeats", repeats));
            }

            text.AppendLine().AppendLine().Append(exception?.ToString() ?? Localizer.Get("M.App.CrashUnknown"));
            text.AppendLine().AppendLine().Append(Localizer.Format("M.App.CrashRecorded", ErrorLog.FilePath));

            void Show()
            {
                if (fatal)
                {
                    MessageBox.Show(text.ToString(), Localizer.Get("M.App.CrashTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Released however the dialog ends: a gate left taken would silence every
                // failure after this one.
                int more;
                try
                {
                    MessageBox.Show(text.ToString(), Localizer.Get("M.App.CrashTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    more = ErrorDialogs.Close(DateTime.UtcNow);
                }

                ShowFolded(more);
            }

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

        /// <summary>
        /// Says how many failures arrived while the dialog was open, and offers the file they
        /// are in. Any that arrive while this notice is open are told in another straight
        /// after; since each one counted, other than a command's, is kept quiet for a while,
        /// that ends as soon as a notice is dismissed with nothing new behind it.
        /// </summary>
        private static void ShowFolded(int count)
        {
            while (count > 0 && ErrorDialogs.TryOpenNotice())
            {
                string text = Localizer.Format("M.App.CrashFolded", count, (int)ErrorDialogGate.QuietPeriod.TotalMinutes)
                    + Environment.NewLine + Environment.NewLine
                    + Localizer.Get("M.App.CrashOpenLog");

                MessageBoxResult answer;
                try
                {
                    answer = MessageBox.Show(text, Localizer.Get("M.App.CrashTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                }
                finally
                {
                    count = ErrorDialogs.Close(DateTime.UtcNow);
                }

                if (answer == MessageBoxResult.Yes)
                {
                    ErrorLog.Open();
                }
            }
        }
    }
}
