using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>The console's window, once it is made; what later launches ask for goes to it.</summary>
        private MainWindow? window;

        /// <summary>What later launches asked for before there was a window, done once there is; null from then on.</summary>
        private List<Action<MainWindow>>? beforeWindow = new();

        /// <summary>
        /// A .xml given on the command line: "WinSW.Gui.exe myapp.xml", the Explorer verb, or the
        /// configuration a copy restarted as administrator had open; see <see cref="StartupArguments"/>.
        /// </summary>
        public static string? StartupConfigPath { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // The unattended alert's runs: the task scheduler's, as SYSTEM with nobody signed
            // in, and the elevated steps that turn it on and off. None of them is a console, so
            // they come before anything a console does — claiming the session, the dialogs for
            // failures, a window — and end the process when they are done.
            if (UnattendedAlert.ParseHeadless(e.Args) is { } headless)
            {
                int exitCode = UnattendedAlert.RunHeadless(headless);

                // Main returns nothing, so this is what the process exits with.
                Environment.ExitCode = exitCode;
                this.Shutdown(exitCode);
                return;
            }

            var arguments = StartupArguments.Parse(e.Args, System.IO.File.Exists);
            StartupConfigPath = arguments.ConfigPath;

            // A configuration to open is handed to the running copy, when there is one that
            // takes it; see SingleInstance. A copy restarted as administrator comes with one as
            // well, and still waits for the copy it replaces rather than handing the file to it.
            // A running copy from before a launch could say who it is may be ended here, when it
            // is not this console and the user says so.
            this.instance = SingleInstance.Claim(replacing: arguments.Replacing, wake: !arguments.Tray, configPath: StartupConfigPath, replaceOlder: ReplaceOlderCopies);
            if (this.instance is null)
            {
                this.Shutdown();
                return;
            }

            // Listening starts as soon as the session is this copy's, not once its window is up.
            // Making the window takes a moment, and a launch in that moment — a second
            // double-click on an .xml — found no pipe and waited out its timeout. The copy it
            // could not reach being this same console, there was nothing to offer to replace,
            // and it opened the file in a second full console, which announced every stop again.
            // What arrives before the window is kept for it.
            this.instance.OnOpenRequested(
                path => this.Dispatcher.BeginInvoke(() => this.ToWindow(target => target.OpenHandedOver(path))),
                launch => this.Dispatcher.BeginInvoke(() => this.ToWindow(target => target.OnLaunched(launch))));

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
            if (arguments.Tray)
            {
                window.StartInTray();
            }
            else
            {
                if (arguments.KeepTray)
                {
                    window.KeepTrayWatch();
                }

                window.Show();
            }

            this.instance.OnShowRequested(() => this.Dispatcher.BeginInvoke(window.BringToFront));

            this.window = window;
            var waiting = this.beforeWindow!;
            this.beforeWindow = null;
            foreach (var action in waiting)
            {
                action(window);
            }

            // What the last update left beside the executable. By the copy that holds the
            // session, which is the one an update restarts into; the copy it restarted from may
            // still be ending, so this keeps trying for a while in the background.
            if (this.instance.HoldsSession && Environment.ProcessPath is { } executable)
            {
                ErrorLog.Observe(Task.Run(() => SelfUpdate.CleanUpAsync(executable)), "update clean-up");
            }
        }

        /// <summary>
        /// Does <paramref name="action"/> to the window, or keeps it for when there is one. On
        /// the UI thread, which is where the window is made.
        /// </summary>
        private void ToWindow(Action<MainWindow> action)
        {
            if (this.window is { } shown)
            {
                action(shown);
            }
            else
            {
                this.beforeWindow?.Add(action);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            this.instance?.Dispose();
            base.OnExit(e);
        }

        /// <summary>
        /// Offers to end the consoles found running in this session that could not be told about
        /// this launch, and ends them on yes; see <see cref="SingleInstance.Claim"/>. True once they
        /// are gone, and this launch takes their place — the sign-in entry and the Explorer verb
        /// with it, when they started one of them.
        /// </summary>
        /// <remarks>
        /// Asked before there is a window: the question is the first thing this launch shows, and
        /// on no it shows nothing else, as the running copy comes forward instead.
        /// </remarks>
        private static bool ReplaceOlderCopies(IReadOnlyList<RunningCopy> copies)
        {
            // Nothing has needed the strings until now. A launch that defers to the running copy
            // exits without them.
            Localizer.Initialize();

            string self = Environment.ProcessPath ?? string.Empty;
            string title = Localizer.Get("M.Replace.Title");
            string running = string.Join(Environment.NewLine, copies.Select(c => "v" + c.Version + "  " + c.ExecutablePath));
            string question = string.Join(
                Environment.NewLine + Environment.NewLine,
                Localizer.Get("M.Replace.OlderFound") + Environment.NewLine + running,
                Localizer.Format("M.Replace.Started", UpdateChecker.CurrentGuiVersion, self),
                Localizer.Get("M.Replace.OlderAsk"));
            if (MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return false;
            }

            if (!RunningCopy.TryEnd(copies, out var failed, out string? reason))
            {
                string why = reason ?? Localizer.Get("M.Replace.EndTimeout");
                ActionLog.Record("replace console", failed!.ExecutablePath, "not ended: " + why);
                MessageBox.Show(Localizer.Format("M.Replace.EndFailed", failed.ExecutablePath, why), title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            foreach (var copy in copies)
            {
                ActionLog.Record("replace console", copy.ExecutablePath, "ended v" + copy.Version + " for v" + UpdateChecker.CurrentGuiVersion + " at " + self);
                Replacement.Repoint(copy.ExecutablePath, self, Localizer.Get("M.Shell.OpenInWinSW"));
            }

            return true;
        }

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
