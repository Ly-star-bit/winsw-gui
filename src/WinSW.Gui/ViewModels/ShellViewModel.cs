using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;
using WinSW.Gui.Theme;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One entry in the navigation rail.</summary>
    public sealed class NavigationItem : ObservableObject
    {
        private readonly string titleKey;
        private readonly string subtitleKey;

        public NavigationItem(string glyph, string titleKey, string subtitleKey, object page)
        {
            this.Glyph = glyph;
            this.titleKey = titleKey;
            this.subtitleKey = subtitleKey;
            this.Page = page;
        }

        /// <summary>A Fluent System Icons glyph; see IconFont in Theme/Palette.xaml.</summary>
        public string Glyph { get; }

        public string Title => Localizer.Get(this.titleKey);

        public string Subtitle => Localizer.Get(this.subtitleKey);

        public object Page { get; }

        public void RefreshLocalized()
        {
            this.Raise(nameof(this.Title));
            this.Raise(nameof(this.Subtitle));
        }
    }

    /// <summary>A theme option, labelled for the picker.</summary>
    public sealed class ThemeOption : ObservableObject
    {
        private readonly string key;

        public ThemeOption(ThemeChoice choice, string key)
        {
            this.Choice = choice;
            this.key = key;
        }

        public ThemeChoice Choice { get; }

        public string Label => Localizer.Get(this.key);

        public void RefreshLocalized() => this.Raise(nameof(this.Label));
    }

    /// <summary>
    /// Owns the four pages and moves between them. Pages hand off to each other through
    /// events so no page needs a reference to another.
    /// </summary>
    public sealed class ShellViewModel : ObservableObject, IDisposable
    {
        /// <summary>How often a console that stays open asks for a newer release.</summary>
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);

        private NavigationItem? selectedItem;
        private object? currentPage;
        private Language selectedLanguage = Localizer.Current;
        private ThemeOption selectedTheme;
        private ReleaseInfo? guiUpdate;
        private bool contextMenuRegistered = ShellIntegration.IsRegistered;
        private bool startWithWindows = Autostart.IsRegistered;
        private string alertUrl = AlertWebhook.Url;
        private string alertSecret = AlertWebhook.Secret;
        private string alertTestStatus = string.Empty;

        /// <summary>The unattended alert's task is registered and enabled, as last read.</summary>
        private bool alertWhenNobodySignedIn;

        /// <summary>Turning it on or off is under way: the elevated step has not returned yet.</summary>
        private bool unattendedAlertBusy;

        /// <summary>Why the last attempt to turn it on or off did not go through; blank when it did.</summary>
        private string unattendedAlertError = string.Empty;

        /// <summary>The unattended alert as last read; null until the first reading comes back.</summary>
        private UnattendedAlertStatus? unattendedAlert;
        private bool isRailCollapsed = AppSettings.Current.RailCollapsed;
        private string toastText = string.Empty;
        private bool toastVisible;
        private bool toastIsError;
        private bool unsavedPromptVisible;

        /// <summary>The configuration the unsaved-changes prompt stands before; null when it stands before exiting.</summary>
        private string? pathToOpen;

        /// <summary>The unsaved-changes prompt stands before restarting as administrator.</summary>
        private bool restartingElevated;

        /// <summary>The unsaved-changes prompt stands before handing over to this console; see <see cref="ReplaceWith"/>.</summary>
        private Replacement? replacementToStart;

        /// <summary>"Update now" is fetching the release.</summary>
        private bool updating;

        /// <summary>How "Update now" is getting on, or why it stopped; blank before it is first used.</summary>
        private string updateStatus = string.Empty;

        /// <summary>
        /// The release "Update now" fetched and checked, waiting beside the executable while the
        /// unsaved changes are answered; null when there is none.
        /// </summary>
        private Replacement? fetchedUpdate;

        /// <summary>When a newer release was last asked for, in UTC.</summary>
        private DateTime lastUpdateCheck;
        private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

        /// <summary>
        /// Looks each hour whether a day has passed since the last update check. Not a timer of a
        /// day: one that a server spent asleep through would fire a day after it woke.
        /// </summary>
        private readonly System.Windows.Threading.DispatcherTimer updateCheckTimer = new() { Interval = TimeSpan.FromHours(1) };

        public ShellViewModel()
        {
            this.Dashboard = new DashboardViewModel();
            this.Editor = new ConfigEditorViewModel();
            this.Logs = new LogViewerViewModel();
            this.Tasks = new DesktopTasksViewModel();
            this.Wizard = new WizardViewModel { Sources = this.Dashboard.Services, TaskSources = this.Tasks.Tasks };
            this.Remote = new RemoteViewModel();
            this.Logs.Services = this.Dashboard.Services;

            // The settings page binds to this shell itself; its DataTemplate maps ShellViewModel → SettingsView.
            // The glyphs are escapes rather than literal characters: they live in the Unicode
            // private use area, and a tool that does not know that has dropped them before.
            // They are Fluent System Icons, SymbolRegular 20 in WPF-UI: Home, Desktop, Edit,
            // DocumentText, Add, Globe, Settings.
            this.Items = new ObservableCollection<NavigationItem>
            {
                new("\uF480", "M.Nav.Services", "M.Nav.ServicesSub", this.Dashboard),
                new("\uF359", "M.Nav.Tasks", "M.Nav.TasksSub", this.Tasks),
                new("\uF3DD", "M.Nav.Config", "M.Nav.ConfigSub", this.Editor),
                new("\uE557", "M.Nav.Logs", "M.Nav.LogsSub", this.Logs),
                new("\uF109", "M.Nav.New", "M.Nav.NewSub", this.Wizard),
                new("\uF45A", "M.Nav.Remote", "M.Nav.RemoteSub", this.Remote),
                new("\uF6A9", "M.Nav.Settings", "M.Nav.SettingsSub", this),
            };

            this.ToggleRailCommand = new RelayCommand(() => this.IsRailCollapsed = !this.IsRailCollapsed);

            this.RefreshPageCommand = new RelayCommand(() => ExecuteIfAllowed(this.PageRefresh()));
            this.CancelPageCommand = new RelayCommand(() => ExecuteIfAllowed(this.PageCancel()));

            this.UnsavedSaveCommand = new AsyncRelayCommand(() => this.DecideUnsavedAsync(save: true));
            this.UnsavedDiscardCommand = new AsyncRelayCommand(() => this.DecideUnsavedAsync(save: false));
            this.UnsavedCancelCommand = new RelayCommand(() =>
            {
                // A release fetched for an update stays where it was downloaded: "Update now"
                // restarts into it without fetching it again, and the next start deletes it.
                this.pathToOpen = null;
                this.restartingElevated = false;
                this.replacementToStart = null;
                this.UnsavedPromptVisible = false;
            });
            this.toastTimer.Tick += (_, _) =>
            {
                this.toastTimer.Stop();
                this.ToastVisible = false;
            };

            this.Dashboard.Toast += this.ShowToast;
            this.Editor.Toast += this.ShowToast;
            this.Tasks.Toast += this.ShowToast;

            this.Tasks.CreateTaskRequested += () =>
            {
                this.Wizard.DesktopTask = true;
                this.Navigate(this.Wizard);
            };

            this.Tasks.OpenConfigRequested += task =>
            {
                this.Editor.Load(task.ConfigPath);
                this.Navigate(this.Editor);
            };

            this.Tasks.OpenLogsRequested += task =>
            {
                this.Logs.AttachConfiguration(task.ConfigPath, task.Name);
                this.Navigate(this.Logs);
            };

            // A configuration installed from the editor is a service the dashboard has not
            // heard of yet.
            this.Editor.ServiceInstalled += _ => this.Dashboard.ReloadCommand.Execute(null);

            // A saved configuration is one the running service has not read yet.
            this.Editor.Saved += path => this.Dashboard.NoteConfigurationWritten(path);

            // Where the tray notification goes, the group chat's goes too: the first crash, and
            // after it the loop's count, a clean stop, a recovery. The dashboard has already held a
            // crash-looping service to one count per five minutes; see CrashAnnouncer. A desktop
            // task's are told by the same rule, from the task page's own reading.
            this.Dashboard.UnexpectedStop += entry => ErrorLog.Observe(
                this.PostStopAsync(new StopNotice(entry.ServiceName, StopNoticeKind.UnexpectedStop, entry.CrashCount, entry.LastExitCode ?? 0), CauseOf(entry)),
                "alert webhook");
            this.Dashboard.StopNoticed += notice => ErrorLog.Observe(this.PostStopAsync(notice, this.CauseFor(notice)), "alert webhook");
            this.SendTestAlertCommand = new AsyncRelayCommand(this.SendTestAlertAsync, () => !string.IsNullOrWhiteSpace(this.alertUrl));
            this.ApplyUnattendedAlertCommand = new AsyncRelayCommand(() => this.SetUnattendedAlertAsync(true), () => !this.unattendedAlertBusy);
            this.Dashboard.CreateServiceRequested += () =>
            {
                // The wizard keeps whichever mode it was last used in; arriving from a page
                // that is about one of the two says which one is meant.
                this.Wizard.DesktopTask = false;
                this.Navigate(this.Wizard);
            };
            this.Dashboard.OpenConfigFileRequested += () =>
            {
                this.Editor.Open();
                this.Navigate(this.Editor);
            };

            this.Themes = new[]
            {
                new ThemeOption(ThemeChoice.System, "M.Theme.System"),
                new ThemeOption(ThemeChoice.Light, "M.Theme.Light"),
                new ThemeOption(ThemeChoice.Dark, "M.Theme.Dark"),
            };
            this.selectedTheme = this.Themes.First(t => t.Choice == ThemeManager.Current);

            this.RestartElevatedCommand = new RelayCommand(this.RestartElevated, () => !this.IsElevated);
            this.BrowseInstallRootCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Settings.InstallRoot"), AppSettings.Current.EffectiveInstallRoot) is { } path)
                {
                    this.InstallRoot = path;
                }
            });
            this.BrowseTaskRootCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Settings.TaskRoot"), AppSettings.Current.EffectiveTaskRoot) is { } path)
                {
                    this.TaskRoot = path;
                }
            });
            this.OpenActionLogCommand = new RelayCommand(ActionLog.Open);
            this.OpenErrorLogCommand = new RelayCommand(ErrorLog.Open);
            this.OpenGuiUpdateCommand = new RelayCommand(() =>
            {
                if (this.guiUpdate != null)
                {
                    SystemShell.OpenUrl(this.guiUpdate.Url);
                }
            });
            this.UpdateNowCommand = new AsyncRelayCommand(this.UpdateNowAsync, () => this.guiUpdate != null && this.CanUpdateInPlace && !this.updating);

            this.Dashboard.OpenUninstalledConfigRequested += this.OpenInEditor;

            // Nobody awaits this or the reload below, so a failure is recorded as it happens
            // rather than going nowhere.
            ErrorLog.Observe(this.CheckGuiUpdateAsync(), "update check");
            ErrorLog.Observe(this.RefreshUnattendedAlertAsync(), "unattended alert status");

            // A console started with Windows stays open for weeks, and used to ask once, at
            // start. It asks again once a day.
            this.updateCheckTimer.Tick += (_, _) =>
            {
                if (DateTime.UtcNow - this.lastUpdateCheck >= UpdateCheckInterval)
                {
                    ErrorLog.Observe(this.CheckGuiUpdateAsync(), "update check");
                }
            };
            this.updateCheckTimer.Start();

            // Once, in the background: the wizard needs to know which task names are taken
            // before anyone has opened the desktop-task page.
            ErrorLog.Observe(this.Tasks.ReloadAsync(quiet: true), "desktop task list");

            this.Dashboard.OpenConfigRequested += entry =>
            {
                this.Editor.LoadFrom(entry);
                this.Navigate(this.Editor);
            };

            this.Dashboard.OpenLogsRequested += entry =>
            {
                this.Logs.Attach(entry);
                this.Navigate(this.Logs);
            };

            this.Wizard.DesktopTaskCompleted += name =>
            {
                this.Tasks.SelectWhenReady(name);
                this.Navigate(this.Tasks);
                ErrorLog.Observe(this.Tasks.ReloadAsync(quiet: false), "desktop task list");
                this.ShowToast(Localizer.Format("M.Wiz.Registered", name), false);
            };

            this.Wizard.Completed += serviceId =>
            {
                this.Dashboard.SelectServiceWhenReady(serviceId);
                this.Navigate(this.Dashboard);
                this.Dashboard.ReloadCommand.Execute(null);
                this.ShowToast(Localizer.Format("M.Wiz.Installed", serviceId), false);
            };

            this.SelectedItem = this.Items[0];

            Localizer.Changed += () =>
            {
                foreach (var item in this.Items)
                {
                    item.RefreshLocalized();
                }

                foreach (var theme in this.Themes)
                {
                    theme.RefreshLocalized();
                }

                this.Raise(nameof(this.ElevationLabel));
                this.Raise(nameof(this.ElevationHint));
                this.Raise(nameof(this.GuiUpdateText));
                this.Raise(nameof(this.UnsavedPromptNext));
                this.Raise(nameof(this.UnsavedSaveLabel));
                this.Raise(nameof(this.UnsavedDiscardLabel));
                this.Raise(nameof(this.SettingsFileProblem));
                this.RaiseUnattendedAlertNotice();
                this.Raise(nameof(this.LastAlertText));
            };

            // The defaults on screen must not pass for the user's own choices. The page keeps
            // saying so; this is for whoever is looking at the window as it opens.
            if (this.SettingsFileProblem.Length > 0)
            {
                this.ShowToast(this.SettingsFileProblem, isError: true);
            }
        }

        public DashboardViewModel Dashboard { get; }

        /// <summary>
        /// Releases what the pages hold outside this process: the trial run's child process,
        /// and the handle on the log being tailed.
        /// </summary>
        public void Dispose()
        {
            this.updateCheckTimer.Stop();
            this.Editor.Dispose();
            this.Logs.Dispose();
        }

        public ConfigEditorViewModel Editor { get; }

        public LogViewerViewModel Logs { get; }

        public DesktopTasksViewModel Tasks { get; }

        public WizardViewModel Wizard { get; }

        public RemoteViewModel Remote { get; }

        public RelayCommand OpenGuiUpdateCommand { get; }

        /// <summary>The log in whatever opens .log files, or its folder when nothing has been recorded yet.</summary>
        public RelayCommand OpenActionLogCommand { get; }

        public string ActionLogPath => ActionLog.FilePath;

        /// <summary>The console's own failures; see <see cref="ErrorLog"/>. Opened the same way as the action log.</summary>
        public RelayCommand OpenErrorLogCommand { get; }

        public string ErrorLogPath => ErrorLog.FilePath;

        public RelayCommand ToggleRailCommand { get; }

        // Keys that act on the page on screen ---------------------------------------

        /// <summary>
        /// F5. The binding is the window's, so the page has to be asked for here: bound to the
        /// dashboard's rescan directly, F5 on any other page swept the services behind it and
        /// left the page in front unchanged.
        /// </summary>
        public RelayCommand RefreshPageCommand { get; }

        /// <summary>
        /// Esc. The dashboard, the task list and the log page each have a confirmation of their
        /// own; this dismisses the one on screen, where the window's binding used to reach only
        /// the dashboard's.
        /// </summary>
        public RelayCommand CancelPageCommand { get; }

        private ICommand? PageRefresh() => this.currentPage switch
        {
            DashboardViewModel dashboard => dashboard.ReloadCommand,
            DesktopTasksViewModel tasks => tasks.ReloadCommand,
            LogViewerViewModel logs => logs.RescanCommand,
            RemoteViewModel remote => remote.RefreshCommand,

            // The editor's Reload reads the file back over unsaved changes without asking.
            // A key pressed out of habit must not be able to do that.
            _ => null,
        };

        private ICommand? PageCancel() => this.currentPage switch
        {
            DashboardViewModel dashboard => dashboard.CancelConfirmCommand,
            DesktopTasksViewModel tasks => tasks.CancelConfirmCommand,
            LogViewerViewModel logs => logs.CancelCleanupCommand,
            _ => null,
        };

        /// <summary>
        /// A key binding asks CanExecute before it executes, but only of the command bound to
        /// it; the page's command behind that one has to be asked here. AsyncRelayCommand does
        /// not ask for itself.
        /// </summary>
        private static void ExecuteIfAllowed(ICommand? command)
        {
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }

        // Unsaved-changes prompt -----------------------------------------------------

        /// <summary>
        /// Raised once the user has answered the unsaved-changes prompt on the way out, with
        /// true when the configuration is to be written first. Cancelling raises nothing.
        /// </summary>
        public event Action<bool>? ExitDecided;

        /// <summary>
        /// Raised when the console is to restart as administrator, with the configuration the new
        /// copy is to open, if any; the window starts that copy and closes. Unsaved changes have
        /// been written or let go by then: this is raised only once that has been answered.
        /// </summary>
        public event Action<string?>? RestartElevatedDecided;

        /// <summary>
        /// Raised when another console is to take over from this one — a later launch's
        /// executable, or the release "Update now" fetched — with the configuration it is to
        /// open, if any; the window starts it and closes. Raised, as the restart is, only once the
        /// unsaved changes have been answered; see <see cref="ReplaceWith"/>.
        /// </summary>
        public event Action<Replacement, string?>? ReplaceDecided;

        /// <summary>Writes the changes, then goes on: out, to the other configuration, or to the restart.</summary>
        public AsyncRelayCommand UnsavedSaveCommand { get; }

        /// <summary>Goes on without writing them.</summary>
        public AsyncRelayCommand UnsavedDiscardCommand { get; }

        /// <summary>Stays with the changes, which is the safe answer.</summary>
        public RelayCommand UnsavedCancelCommand { get; }

        /// <summary>Shown over the whole window, so it covers the page the changes are on.</summary>
        public bool UnsavedPromptVisible
        {
            get => this.unsavedPromptVisible;
            private set => this.Set(ref this.unsavedPromptVisible, value);
        }

        /// <summary>The file that would lose its changes; blank for one never saved.</summary>
        public string UnsavedPromptFile => this.Editor.FilePath ?? string.Empty;

        /// <summary>
        /// What comes after the answer: the configuration waiting to be opened, or the restart;
        /// blank when the prompt stands before exiting, which its buttons say.
        /// </summary>
        public string UnsavedPromptNext => this.replacementToStart is { } replacement
            ? (replacement.IsUpdate
                ? Localizer.Format("M.Update.Next", replacement.Version)
                : Localizer.Format("M.Replace.Next", replacement.Version, replacement.ExecutablePath))
            : this.restartingElevated
            ? Localizer.Get("M.Restart.Next")
            : this.pathToOpen is null ? string.Empty : Localizer.Format("M.Open.Next", this.pathToOpen);

        public string UnsavedSaveLabel => Localizer.Get(this.ForNextStep("M.Exit.Save", "M.Open.Save", "M.Restart.Save"));

        public string UnsavedDiscardLabel => Localizer.Get(this.ForNextStep("M.Exit.Discard", "M.Open.Discard", "M.Restart.Discard"));

        /// <summary>Asks what to do about the editor's unsaved changes before the window closes.</summary>
        public void AskToExit() => this.AskAboutUnsavedChanges(null);

        /// <summary>
        /// The one of three keys that fits what the prompt stands before. Handing over to another
        /// console is a restart as far as the buttons are concerned.
        /// </summary>
        private string ForNextStep(string exit, string open, string restart) =>
            this.restartingElevated || this.replacementToStart != null ? restart : this.pathToOpen is null ? exit : open;

        /// <summary>
        /// Puts the prompt up, standing before exiting or, with <paramref name="next"/>, before
        /// opening that configuration, with <paramref name="restart"/> before restarting as
        /// administrator, or with <paramref name="replacement"/> before handing over to that
        /// console. The latest request is the one answered: an exit that was interrupted starts
        /// over at the next close anyway.
        /// </summary>
        private void AskAboutUnsavedChanges(string? next, bool restart = false, Replacement? replacement = null)
        {
            this.pathToOpen = next;
            this.restartingElevated = restart;
            this.replacementToStart = replacement;
            this.Raise(nameof(this.UnsavedPromptFile));
            this.Raise(nameof(this.UnsavedPromptNext));
            this.Raise(nameof(this.UnsavedSaveLabel));
            this.Raise(nameof(this.UnsavedDiscardLabel));
            this.UnsavedPromptVisible = true;
        }

        private async Task DecideUnsavedAsync(bool save)
        {
            string? next = this.pathToOpen;
            bool restart = this.restartingElevated;
            var replacement = this.replacementToStart;
            this.pathToOpen = null;
            this.restartingElevated = false;
            this.replacementToStart = null;
            this.UnsavedPromptVisible = false;

            if (next is null && !restart && replacement is null)
            {
                this.ExitDecided?.Invoke(save);
                return;
            }

            // As on the way out: a save that did not take — an invalid configuration, a
            // declined elevation, a cancelled Save As — leaves the changes in the editor, with
            // the editor showing why, rather than opening the other file over them or
            // restarting without them.
            if (save && !await this.Editor.TrySaveAsync().ConfigureAwait(true))
            {
                return;
            }

            if (replacement != null)
            {
                this.ReplaceDecided?.Invoke(replacement, replacement.ConfigPath ?? this.ConfigurationToReopen);
            }
            else if (restart)
            {
                this.RestartElevatedDecided?.Invoke(this.ConfigurationToReopen);
            }
            else if (next != null)
            {
                this.Editor.Load(next);
                this.Navigate(this.Editor);
            }
        }

        /// <summary>
        /// "Restart as administrator". The new copy is another process, and the editor's
        /// changes do not go with it; it used to be started straight over them. Now they are
        /// asked about first, as on the way out.
        /// </summary>
        private void RestartElevated()
        {
            if (this.Editor.IsDirty)
            {
                // Answer it looking at the thing that is unsaved.
                this.Navigate(this.Editor);
                this.AskAboutUnsavedChanges(null, restart: true);
                return;
            }

            this.RestartElevatedDecided?.Invoke(this.ConfigurationToReopen);
        }

        /// <summary>
        /// Hands this console's place to <paramref name="replacement"/>: the executable a later
        /// launch came from, when the user said so, or the release "Update now" fetched. As for
        /// "Restart as administrator", unsaved changes in the editor are asked about first; then
        /// <see cref="ReplaceDecided"/> is raised, and the window starts the new console and
        /// closes. The configuration the launch came with is the one the new console opens;
        /// otherwise, the one the restart would.
        /// </summary>
        public void ReplaceWith(Replacement replacement)
        {
            if (this.Editor.IsDirty)
            {
                // Answer it looking at the thing that is unsaved.
                this.Navigate(this.Editor);
                this.AskAboutUnsavedChanges(null, replacement: replacement);
                return;
            }

            this.ReplaceDecided?.Invoke(replacement, replacement.ConfigPath ?? this.ConfigurationToReopen);
        }

        /// <summary>
        /// The console that was to take over did not start, and this one stays; see
        /// <see cref="Replacement.Start"/>. Says why, and records it.
        /// </summary>
        public void ReportReplacementFailed(Replacement replacement, string error)
        {
            ActionLog.Record(replacement.IsUpdate ? "update console" : "replace console", replacement.ExecutablePath, "not started: " + error);

            string text = Localizer.Format("M.Replace.Failed", replacement.ExecutablePath, error);
            if (replacement.IsUpdate)
            {
                // The fetched file has been put back where it was downloaded, or could not be;
                // either way the next "Update now" fetches the release again rather than trust it.
                this.fetchedUpdate = null;
                this.UpdateStatus = text;
            }

            this.ShowToast(text, isError: true);
        }

        /// <summary>
        /// The console that takes over has started, and this one is closing; recorded with what
        /// took over, which the log would otherwise only learn from the next start's version.
        /// </summary>
        public static void RecordReplacementStarted(Replacement replacement) =>
            ActionLog.Record(
                replacement.IsUpdate ? "update console" : "replace console",
                replacement.ExecutablePath,
                "v" + UpdateChecker.CurrentGuiVersion + " handed over to v" + replacement.Version);

        /// <summary>
        /// The configuration the restarted copy opens, as the command line would: the file in the
        /// editor, when the editor is the page on screen — which it is once the prompt has been
        /// answered. None for a configuration never saved.
        /// </summary>
        private string? ConfigurationToReopen => ReferenceEquals(this.currentPage, this.Editor) ? this.Editor.FilePath : null;

        /// <summary>
        /// Opens a configuration no installed service uses — one given at start, or handed over
        /// by a later launch — in the editor. Over unsaved changes to another configuration,
        /// only once the user has said what becomes of them.
        /// </summary>
        private void OpenInEditor(string path)
        {
            if (this.Editor.IsDirty)
            {
                this.Navigate(this.Editor);

                // The file being edited already: what the editor holds is newer than the disk,
                // and reading the disk back over it would be the very loss the prompt prevents.
                if (!IsSameFile(path, this.Editor.FilePath))
                {
                    this.AskAboutUnsavedChanges(path);
                }

                return;
            }

            this.Editor.Load(path);
            this.Navigate(this.Editor);
        }

        private static bool IsSameFile(string path, string? other) =>
            other != null && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);

        /// <summary>Icon-only rail; remembered across sessions.</summary>
        public bool IsRailCollapsed
        {
            get => this.isRailCollapsed;
            set
            {
                if (this.Set(ref this.isRailCollapsed, value))
                {
                    AppSettings.Current.RailCollapsed = value;
                    AppSettings.Current.Save();
                }
            }
        }

        // Toast ------------------------------------------------------------------

        public string ToastText
        {
            get => this.toastText;
            private set => this.Set(ref this.toastText, value);
        }

        public bool ToastVisible
        {
            get => this.toastVisible;
            private set => this.Set(ref this.toastVisible, value);
        }

        public bool ToastIsError
        {
            get => this.toastIsError;
            private set => this.Set(ref this.toastIsError, value);
        }

        /// <summary>A short notice near the top of the content; replaces the previous one.</summary>
        public void ShowToast(string text, bool isError)
        {
            this.ToastText = text;
            this.ToastIsError = isError;
            this.ToastVisible = true;
            this.toastTimer.Stop();
            this.toastTimer.Start();
        }

        /// <summary>Brings a service into view, e.g. from a tray notification.</summary>
        public void ShowService(string serviceName)
        {
            this.Navigate(this.Dashboard);
            this.Dashboard.SelectByName(serviceName);
        }

        /// <summary>Brings a desktop task into view, from its tray notification.</summary>
        public void ShowTask(string taskName)
        {
            this.Navigate(this.Tasks);
            this.Tasks.SelectByName(taskName);
        }

        /// <summary>
        /// Brings up what a clicked tray notification was about: the service, or the desktop task,
        /// its tag names; see <see cref="StopNoticeText.TagFor"/>.
        /// </summary>
        public void ShowNotified(string tag)
        {
            if (StopNoticeText.IsTaskTag(tag, out string taskName))
            {
                this.ShowTask(taskName);
            }
            else
            {
                this.ShowService(tag);
            }
        }

        public string GuiVersion => "v" + UpdateChecker.CurrentGuiVersion;

        /// <summary>A newer GUI release, when one exists and the network allowed asking.</summary>
        public ReleaseInfo? GuiUpdate
        {
            get => this.guiUpdate;
            private set
            {
                if (this.Set(ref this.guiUpdate, value))
                {
                    this.Raise(nameof(this.HasGuiUpdate));
                    this.Raise(nameof(this.GuiUpdateText));
                    this.UpdateNowCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool HasGuiUpdate => this.guiUpdate != null;

        public string GuiUpdateText => this.guiUpdate is null ? string.Empty : Localizer.Format("M.Shell.GuiUpdate", this.guiUpdate.Version);

        /// <summary>
        /// This executable is one a release carries, so "Update now" can put the new one in its
        /// place; see <see cref="SelfUpdate.AssetForThisBuild"/>. A build from source is not, and
        /// is offered only the release page.
        /// </summary>
        public bool CanUpdateInPlace { get; } = SelfUpdate.AssetForThisBuild != null && Environment.ProcessPath != null;

        /// <summary>
        /// Fetches the release's executable for this build, checks it against the release's
        /// checksum list and restarts into it; see <see cref="SelfUpdate"/>. The release page
        /// used to be all there was, and on Windows Server that opens in Internet Explorer,
        /// whose enhanced security blocks the download.
        /// </summary>
        public AsyncRelayCommand UpdateNowCommand { get; }

        /// <summary>How "Update now" is getting on, or why it stopped; blank before it is first used.</summary>
        public string UpdateStatus
        {
            get => this.updateStatus;
            private set => this.Set(ref this.updateStatus, value);
        }

        private async Task UpdateNowAsync()
        {
            var release = this.guiUpdate;
            string? executable = Environment.ProcessPath;
            if (release is null || executable is null)
            {
                return;
            }

            // Fetched already, and the unsaved changes were not answered then: straight to them
            // again, without fetching the release a second time.
            if (this.fetchedUpdate is { DownloadedFile: { } waiting } fetched
                && string.Equals(fetched.Version, release.Version, StringComparison.OrdinalIgnoreCase)
                && System.IO.File.Exists(waiting))
            {
                this.ReplaceWith(fetched);
                return;
            }

            this.fetchedUpdate = null;
            this.SetUpdating(true);
            UpdateDownload download;
            try
            {
                this.UpdateStatus = Localizer.Format("M.Update.Downloading", release.Version, 0);

                // Created here, so that it reports on this thread. A report that arrives after
                // the download has finished is dropped: it would overwrite what came of it.
                var progress = new Progress<int>(percent =>
                {
                    if (this.updating)
                    {
                        this.UpdateStatus = Localizer.Format("M.Update.Downloading", release.Version, percent);
                    }
                });

                download = await SelfUpdate.DownloadAsync(release, executable, progress, CancellationToken.None).ConfigureAwait(true);
            }
            finally
            {
                this.SetUpdating(false);
            }

            if (download.File is null)
            {
                ActionLog.Record("update console", "v" + release.Version, "not fetched: " + download.Problem + (download.Detail is null ? string.Empty : " (" + download.Detail + ")"));
                this.UpdateStatus = DescribeUpdateProblem(download);
                return;
            }

            ActionLog.Record("update console", "v" + release.Version, "fetched and checked against " + SelfUpdate.ChecksumsAsset + ": " + download.File);
            this.fetchedUpdate = new Replacement(executable, release.Version, configPath: null, downloadedFile: download.File);
            this.UpdateStatus = Localizer.Format("M.Update.Ready", release.Version);
            this.ReplaceWith(this.fetchedUpdate);
        }

        private void SetUpdating(bool busy)
        {
            this.updating = busy;
            this.UpdateNowCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Why "Update now" has nothing to restart into, in words that say what to do instead.</summary>
        private static string DescribeUpdateProblem(UpdateDownload download)
        {
            string detail = download.Detail ?? string.Empty;
            return download.Problem switch
            {
                UpdateProblem.NoAsset => Localizer.Format("M.Update.NoAsset", detail),
                UpdateProblem.NoChecksum => Localizer.Format("M.Update.NoChecksum", detail, SelfUpdate.ChecksumsAsset),
                UpdateProblem.FolderNotWritable => Localizer.Format("M.Update.FolderNotWritable", detail),
                UpdateProblem.DownloadFailed => Localizer.Format("M.Update.DownloadFailed", detail),

                // In the console's words; the detail is English, for the action log.
                UpdateProblem.DownloadStalled => Localizer.Format("M.Update.DownloadFailed", Localizer.Format("M.Update.Stalled", (int)UpdateChecker.StallTimeout.TotalSeconds)),
                UpdateProblem.DownloadTruncated => Localizer.Format("M.Update.DownloadFailed", Localizer.Get("M.Update.Truncated")),
                UpdateProblem.ChecksumMismatch => Localizer.Format("M.Update.Mismatch", detail, SelfUpdate.ChecksumsAsset),
                _ => Localizer.Get("M.Update.NotThisBuild"),
            };
        }

        /// <summary>"Open in WinSW" on the right-click menu of .xml files, for this user.</summary>
        public bool ContextMenuRegistered
        {
            get => this.contextMenuRegistered;
            set
            {
                if (this.contextMenuRegistered == value)
                {
                    return;
                }

                try
                {
                    if (value)
                    {
                        ShellIntegration.Register(Localizer.Get("M.Shell.OpenInWinSW"));
                    }
                    else
                    {
                        ShellIntegration.Unregister();
                    }

                    this.contextMenuRegistered = value;
                }
                catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
                {
                    // HKCU is normally writable; if not, the checkbox simply snaps back.
                }

                this.Raise();
            }
        }

        /// <summary>Handles a configuration path passed on the command line or from the shell verb.</summary>
        public void OpenStartupPath(string path)
        {
            this.Dashboard.OpenConfigPathWhenReady(path);
            this.Navigate(this.Dashboard);
        }

        /// <summary>
        /// The same for a path a later launch handed to this copy; see <see cref="ConfigHandoff"/>.
        /// </summary>
        /// <remarks>
        /// The path is applied at the end of a scan. At start the first one is under way; here
        /// the list was loaded long ago, and the next background rescan may be half a minute
        /// off, or never come when rescanning is turned off, so one is asked for now. A scan
        /// already under way picks the path up when it finishes.
        /// </remarks>
        public void OpenHandedOverPath(string path)
        {
            this.OpenStartupPath(path);
            if (!this.Dashboard.IsScanning)
            {
                this.Dashboard.ReloadCommand.Execute(null);
            }
        }

        /// <summary>
        /// Asks for a newer release: at start, then once a day. A check that fails waits for the
        /// next day's, as the one at start always did.
        /// </summary>
        private async Task CheckGuiUpdateAsync()
        {
            this.lastUpdateCheck = DateTime.UtcNow;
            var latest = await UpdateChecker.LatestGuiAsync().ConfigureAwait(true);
            if (latest is null || !UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentGuiVersion))
            {
                return;
            }

            // The same release again changes nothing: a file fetched for it is still the one to
            // restart into. A later one takes its place, and "Update now" fetches that instead.
            if (!string.Equals(latest.Version, this.guiUpdate?.Version, StringComparison.OrdinalIgnoreCase))
            {
                this.GuiUpdate = latest;
                if (!this.updating)
                {
                    this.UpdateStatus = string.Empty;
                }
            }
        }

        public ObservableCollection<NavigationItem> Items { get; }

        public RelayCommand RestartElevatedCommand { get; }

        /// <summary>
        /// Shown in the rail so the user knows what to expect: elevated sessions get no
        /// UAC prompts, standard ones get one per change.
        /// </summary>
        public bool IsElevated => Elevation.IsElevated;

        public string ElevationLabel => Localizer.Get(this.IsElevated ? "M.Shell.Admin" : "M.Shell.Standard");

        public string ElevationHint => Localizer.Get(this.IsElevated ? "M.Shell.AdminHint" : "M.Shell.StandardHint");

        public Language[] Languages => Localizer.Languages;

        /// <summary>Changing this re-renders the UI in place and remembers the choice.</summary>
        public Language SelectedLanguage
        {
            get => this.selectedLanguage;
            set
            {
                if (value != null && this.Set(ref this.selectedLanguage, value))
                {
                    Localizer.Apply(value);
                }
            }
        }

        public ThemeOption[] Themes { get; }

        public ThemeOption SelectedTheme
        {
            get => this.selectedTheme;
            set
            {
                if (value != null && this.Set(ref this.selectedTheme, value))
                {
                    ThemeManager.Apply(value.Choice);
                }
            }
        }

        // Alerts ----------------------------------------------------------------------

        /// <summary>The webhook unexpected stops are posted to; stored encrypted, see <see cref="AlertWebhook"/>.</summary>
        public string AlertUrl
        {
            get => this.alertUrl;
            set
            {
                if (this.Set(ref this.alertUrl, value ?? string.Empty))
                {
                    AlertWebhook.Url = this.alertUrl;
                    this.AlertTestStatus = string.Empty;
                    this.SendTestAlertCommand.RaiseCanExecuteChanged();
                    this.RaiseUnattendedAlertNotice();
                }
            }
        }

        /// <summary>The DingTalk or Feishu signing secret, if the robot has one.</summary>
        public string AlertSecret
        {
            get => this.alertSecret;
            set
            {
                if (this.Set(ref this.alertSecret, value ?? string.Empty))
                {
                    AlertWebhook.Secret = this.alertSecret;
                    this.AlertTestStatus = string.Empty;
                    this.RaiseUnattendedAlertNotice();
                }
            }
        }

        public string AlertTestStatus
        {
            get => this.alertTestStatus;
            private set => this.Set(ref this.alertTestStatus, value);
        }

        public AsyncRelayCommand SendTestAlertCommand { get; }

        private async Task SendTestAlertAsync()
        {
            this.AlertTestStatus = Localizer.Get("M.Alert.Sending");
            string url = this.alertUrl;
            string? error = await AlertWebhook.SendAsync(url, this.alertSecret, Localizer.Format("M.Alert.Test", Environment.MachineName)).ConfigureAwait(true);
            ActionLog.Record("alert test", AlertWebhook.KindOf(url).ToString(), error is null ? "ok" : "failed: " + error);
            this.AlertTestStatus = error is null ? Localizer.Get("M.Alert.TestOk") : Localizer.Format("M.Alert.TestFailed", error);
        }

        /// <summary>
        /// Where the lines saying why a service stopped are read from, taken from its entry here,
        /// on the UI thread, which the entry and the dictionaries belong to.
        /// </summary>
        private static StopCauseSource CauseOf(ServiceEntry entry) => new(
            entry.ConfigPath,
            AppSettings.Current.LogEncoding,
            StartedAt: null,
            LastSeenStart: entry.RunStartedAt,
            Stray: entry.StrayProcess?.Describe(entry.ServiceName, Localizer.Format).Banner);

        /// <summary>
        /// The same for a notice, from the service or the desktop task it names as the list has it
        /// now; null when it has no cause to give (a clean stop, a recovery) or is no longer listed.
        /// </summary>
        private StopCauseSource? CauseFor(StopNotice notice)
        {
            if (!StopCause.Explains(notice.Kind))
            {
                return null;
            }

            return this.Dashboard.Services.FirstOrDefault(s => string.Equals(s.ServiceName, notice.ServiceName, StringComparison.OrdinalIgnoreCase)) is { } entry
                ? CauseOf(entry)
                : null;
        }

        /// <summary>
        /// Posts the group chat's copy of a tray notification, and keeps how it went for the
        /// settings page. Written here, on the UI thread, which is the thread that changes the
        /// settings everywhere else.
        /// </summary>
        private async Task PostStopAsync(StopNotice notice, StopCauseSource? cause)
        {
            var outcome = await AlertWebhook.NotifyStopAsync(notice, cause).ConfigureAwait(true);
            if (outcome is null)
            {
                return;
            }

            var settings = AppSettings.Current;
            settings.LastAlertAt = outcome.At;
            settings.LastAlertService = outcome.Service;
            settings.LastAlertError = outcome.Error;
            settings.Save();
            this.Raise(nameof(this.LastAlertText));
        }

        /// <summary>
        /// How the last alert went — this console's or, when it is newer, the unattended
        /// alert's — and when; blank before the first one.
        /// </summary>
        public string LastAlertText
        {
            get
            {
                var settings = AppSettings.Current;
                var console = settings.LastAlertAt is { } at
                    ? new AlertOutcome { At = at, Service = settings.LastAlertService ?? string.Empty, Error = settings.LastAlertError }
                    : null;

                var (last, byTask) = AlertOutcome.Latest(console, this.unattendedAlert?.Last);
                if (last is null)
                {
                    return string.Empty;
                }

                string result = last.Error is null ? Localizer.Get("M.Alert.LastSent") : Localizer.Format("M.Alert.LastNotSent", last.Error);
                string text = Localizer.Format("M.Alert.Last", last.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), last.Service, result);
                return byTask ? text + " " + Localizer.Get("M.Alert.LastByTask") : text;
            }
        }

        // Alerts when nobody is signed in ---------------------------------------------------

        /// <summary>
        /// "Also alert when nobody is signed in": the task that posts from the failures Windows
        /// records, as last read; see <see cref="Services.UnattendedAlert"/>. Setting it turns the
        /// task on or off, which takes an elevation prompt and a few seconds. Meanwhile the box
        /// shows what was asked for and cannot be changed; then it shows what is registered.
        /// </summary>
        public bool AlertWhenNobodySignedIn
        {
            get => this.alertWhenNobodySignedIn;
            set
            {
                if (value == this.alertWhenNobodySignedIn || this.unattendedAlertBusy)
                {
                    this.Raise();
                    return;
                }

                ErrorLog.Observe(this.SetUnattendedAlertAsync(value), "unattended alert");
            }
        }

        public bool CanChangeUnattendedAlert => !this.unattendedAlertBusy;

        /// <summary>
        /// Under the box: that the change is under way; otherwise why the last one did not go
        /// through, and that the task's copy of the webhook or of the console is older than this
        /// console's, each on a line of its own; blank when there is neither.
        /// </summary>
        public string UnattendedAlertNotice =>
            this.unattendedAlertBusy ? Localizer.Get("M.Alert.UnattendedWorking")
            : string.Join("\n", new[] { this.unattendedAlertError, this.UnattendedAlertOutdated() }.Where(text => text.Length > 0));

        /// <summary>
        /// The task's copy is out of date, and <see cref="ApplyUnattendedAlertCommand"/> is
        /// offered — also after an attempt that failed, which is when trying again is wanted.
        /// </summary>
        public bool UnattendedAlertStale => !this.unattendedAlertBusy && this.UnattendedAlertOutdated().Length > 0;

        /// <summary>Copies this console's webhook and executable to the task again, with one elevation prompt.</summary>
        public AsyncRelayCommand ApplyUnattendedAlertCommand { get; }

        /// <summary>
        /// Why the task's copy no longer matches this console, or blank. The address and secret
        /// typed on this page reach the task only when they are applied: a prompt per keystroke
        /// is not something to put anyone through.
        /// </summary>
        private string UnattendedAlertOutdated()
        {
            if (this.unattendedAlert is not { Active: true, Manifest: { } manifest })
            {
                return string.Empty;
            }

            if (!UnattendedAlert.SameWebhook(manifest, this.alertUrl, this.alertSecret))
            {
                return Localizer.Get("M.Alert.UnattendedStale");
            }

            return string.Equals(manifest.Version, UpdateChecker.CurrentGuiVersion, StringComparison.Ordinal)
                ? string.Empty
                : Localizer.Format("M.Alert.UnattendedOld", manifest.Version, UpdateChecker.CurrentGuiVersion);
        }

        private void RaiseUnattendedAlertNotice()
        {
            this.Raise(nameof(this.UnattendedAlertNotice));
            this.Raise(nameof(this.UnattendedAlertStale));
        }

        private void SetUnattendedAlertBusy(bool busy)
        {
            this.unattendedAlertBusy = busy;
            this.Raise(nameof(this.CanChangeUnattendedAlert));
            this.ApplyUnattendedAlertCommand.RaiseCanExecuteChanged();
            this.RaiseUnattendedAlertNotice();
        }

        /// <summary>Turns the unattended alert on — or brings its copy up to date — or off.</summary>
        private async Task SetUnattendedAlertAsync(bool on)
        {
            this.unattendedAlertError = string.Empty;
            this.SetUnattendedAlertBusy(true);
            try
            {
                if (on && !AlertWebhook.IsConfigured)
                {
                    this.unattendedAlertError = Localizer.Get("M.Alert.UnattendedNoUrl");
                    return;
                }

                var result = on
                    ? await UnattendedAlert.TurnOnAsync(AlertWebhook.Url, AlertWebhook.Secret, Localizer.Current.Code).ConfigureAwait(true)
                    : await UnattendedAlert.TurnOffAsync().ConfigureAwait(true);

                ActionLog.Record("unattended alert", on ? "on" : "off", result);

                // A declined prompt changed nothing, which the box going back says well enough.
                if (!result.Succeeded && !result.Cancelled)
                {
                    this.unattendedAlertError = result.Error ?? ActionLog.Describe(result);
                }
            }
            finally
            {
                // Whatever happened, the box ends up showing what is registered now.
                await this.RefreshUnattendedAlertAsync(force: true).ConfigureAwait(true);
                this.SetUnattendedAlertBusy(false);
            }
        }

        /// <summary>
        /// Reads the task, its manifest and its last message off the UI thread, and shows them.
        /// Left alone while a change is under way, except by that change itself, so that the box
        /// does not jump back to the old state in the middle of it.
        /// </summary>
        private async Task RefreshUnattendedAlertAsync(bool force = false)
        {
            if (this.unattendedAlertBusy && !force)
            {
                return;
            }

            var status = await Task.Run(UnattendedAlert.ReadStatus).ConfigureAwait(true);
            if (this.unattendedAlertBusy && !force)
            {
                return;
            }

            this.unattendedAlert = status;
            this.alertWhenNobodySignedIn = status.Active;
            this.Raise(nameof(this.AlertWhenNobodySignedIn));
            this.RaiseUnattendedAlertNotice();
            this.Raise(nameof(this.LastAlertText));
        }

        /// <summary>The sign-in entry that starts this console in the tray; see <see cref="Autostart"/>.</summary>
        public bool StartWithWindows
        {
            get => this.startWithWindows;
            set
            {
                if (this.startWithWindows == value)
                {
                    return;
                }

                try
                {
                    if (value)
                    {
                        Autostart.Register();
                    }
                    else
                    {
                        Autostart.Unregister();
                    }

                    this.startWithWindows = value;
                }
                catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
                {
                    // HKCU is normally writable; if not, the checkbox simply snaps back.
                }

                this.Raise();
            }
        }

        public bool MinimizeToTray
        {
            get => AppSettings.Current.MinimizeToTray;
            set
            {
                if (AppSettings.Current.MinimizeToTray != value)
                {
                    AppSettings.Current.MinimizeToTray = value;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        /// <summary>
        /// Where the wizard installs services: one folder per service, sharing a wrapper in
        /// <c>bin</c>. Blank falls back to <see cref="DefaultInstallRoot"/>.
        /// </summary>
        public string InstallRoot
        {
            get => AppSettings.Current.InstallRoot ?? string.Empty;
            set
            {
                string trimmed = value.Trim();
                if (!string.Equals(AppSettings.Current.InstallRoot ?? string.Empty, trimmed, StringComparison.Ordinal))
                {
                    AppSettings.Current.InstallRoot = trimmed.Length == 0 ? null : trimmed;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        public string DefaultInstallRoot => AppSettings.Current.EffectiveInstallRoot;

        public RelayCommand BrowseInstallRootCommand { get; }

        /// <summary>
        /// Where desktop tasks are installed. A per-user location by default: the task runs as
        /// one account with no elevation, and its logs have to be writable by that account.
        /// </summary>
        public string TaskRoot
        {
            get => AppSettings.Current.TaskRoot ?? string.Empty;
            set
            {
                string trimmed = value.Trim();
                if (!string.Equals(AppSettings.Current.TaskRoot ?? string.Empty, trimmed, StringComparison.Ordinal))
                {
                    AppSettings.Current.TaskRoot = trimmed.Length == 0 ? null : trimmed;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        public string DefaultTaskRoot => AppSettings.Current.EffectiveTaskRoot;

        public RelayCommand BrowseTaskRootCommand { get; }

        /// <summary>
        /// Said when settings.json could not be read at start, so that the defaults in use do
        /// not pass for the user's own choices; blank otherwise. See <see cref="AppSettings.Load"/>.
        /// </summary>
        public string SettingsFileProblem => AppSettings.Current switch
        {
            { SetAsidePath: { } kept } => Localizer.Format("M.Settings.SetAside", kept),
            { IsFileUnreadable: true } => Localizer.Format("M.Settings.Unreadable", AppSettings.FilePath),
            _ => string.Empty,
        };

        public bool NotifyOnUnexpectedStop
        {
            get => AppSettings.Current.NotifyOnUnexpectedStop;
            set
            {
                if (AppSettings.Current.NotifyOnUnexpectedStop != value)
                {
                    AppSettings.Current.NotifyOnUnexpectedStop = value;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        public NavigationItem? SelectedItem
        {
            get => this.selectedItem;
            set
            {
                if (this.Set(ref this.selectedItem, value) && value != null)
                {
                    this.CurrentPage = value.Page;
                }
            }
        }

        public object? CurrentPage
        {
            get => this.currentPage;
            private set
            {
                var previous = this.currentPage;
                if (!this.Set(ref this.currentPage, value))
                {
                    return;
                }

                // Only the visible page polls; the timers of the others stay idle.
                switch (previous)
                {
                    case DashboardViewModel dashboard:
                        dashboard.Deactivate();
                        break;
                    case LogViewerViewModel logs:
                        logs.Deactivate();
                        break;
                    case RemoteViewModel remote:
                        remote.Deactivate();
                        break;
                    case DesktopTasksViewModel tasks:
                        tasks.Deactivate();
                        break;
                }

                switch (value)
                {
                    case DashboardViewModel dashboard:
                        dashboard.Activate();
                        break;
                    case LogViewerViewModel logs:
                        logs.Activate();
                        break;
                    case RemoteViewModel remote:
                        remote.Activate();
                        break;
                    case DesktopTasksViewModel tasks:
                        tasks.Activate();
                        break;
                }

                // The settings page shows the unattended alert as it is now: another
                // administrator may have turned it on or off since, and it may have posted.
                if (ReferenceEquals(value, this))
                {
                    ErrorLog.Observe(this.RefreshUnattendedAlertAsync(), "unattended alert status");
                }
            }
        }

        public void Navigate(object page)
        {
            foreach (var item in this.Items)
            {
                if (ReferenceEquals(item.Page, page))
                {
                    this.SelectedItem = item;
                    return;
                }
            }
        }
    }
}
