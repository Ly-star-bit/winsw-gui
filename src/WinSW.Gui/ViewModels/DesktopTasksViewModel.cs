using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>
    /// The desktop-task panel: programs with a user interface, hosted in the logged-on
    /// session by the task scheduler rather than in session 0 by the service control manager.
    /// </summary>
    public sealed class DesktopTasksViewModel : ObservableObject
    {
        /// <summary>
        /// Slower than the service poll: every tick is a round trip through COM for the whole
        /// folder, and a task's state changes far less often than a service's.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

        /// <summary>
        /// How often the folder is read while the page is not shown, whichever page is and whether
        /// or not the window is in the tray: often enough to see most robots that die, which the
        /// keep-alive trigger starts again within the minute; seldom enough to cost nothing.
        /// </summary>
        private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(30);

        private readonly DispatcherTimer statusTimer;

        /// <summary>The background reading; see <see cref="WatchAsync"/>.</summary>
        private readonly DispatcherTimer watchTimer;

        /// <summary>Which stops are told, and how; fed every reading, told through <see cref="StopNoticed"/>.</summary>
        private readonly DesktopTaskWatch watch = new();

        /// <summary>
        /// The tasks an operation from this page is working on, until the reading after it: a stop
        /// seen meanwhile is that operation's, and not told.
        /// </summary>
        private readonly OperationsInFlight inFlight = new();

        /// <summary>This is the page in front, between <see cref="Activate"/> and <see cref="Deactivate"/>.</summary>
        private bool pageShown;

        private DesktopTaskEntry? selectedTask;
        private string searchText = string.Empty;
        private string statusMessage = string.Empty;
        private bool isBusy;
        private bool isScanning;
        private bool confirmVisible;
        private string confirmTitle = string.Empty;
        private string confirmMessage = string.Empty;
        private string confirmActionLabel = string.Empty;
        private Func<Task>? pendingAction;
        private string? pendingSelection;
        private bool reloading;

        public DesktopTasksViewModel()
        {
            this.TasksView = CollectionViewSource.GetDefaultView(this.Tasks);
            this.TasksView.Filter = this.Matches;

            this.ReloadCommand = new AsyncRelayCommand(() => this.ReloadAsync(quiet: false));
            this.StartCommand = new AsyncRelayCommand(this.StartAsync, () => this.selectedTask?.CanStart == true);
            this.StopCommand = new AsyncRelayCommand(this.StopAsync, () => this.selectedTask?.CanStop == true);
            this.RestartCommand = new AsyncRelayCommand(this.RestartAsync, () => this.selectedTask != null);

            this.ToggleEnabledCommand = new AsyncRelayCommand(this.ToggleEnabledAsync, () => this.selectedTask != null);

            this.DeleteCommand = new RelayCommand(
                () => this.Ask(
                    Localizer.Get("M.Task.DeleteTitle"),
                    Localizer.Format("M.Task.DeleteBody", this.selectedTask!.Name),
                    Localizer.Get("M.Task.DeleteAction"),
                    this.DeleteAsync),
                () => this.selectedTask != null);

            this.EditConfigCommand = new RelayCommand(
                () => this.OpenConfigRequested?.Invoke(this.selectedTask!),
                () => !string.IsNullOrEmpty(this.selectedTask?.ConfigPath));

            this.ViewLogsCommand = new RelayCommand(
                () => this.OpenLogsRequested?.Invoke(this.selectedTask!),
                () => !string.IsNullOrEmpty(this.selectedTask?.ConfigPath));

            this.OpenFolderCommand = new RelayCommand(this.OpenContainingFolder, () => this.selectedTask != null);
            this.OpenSchedulerCommand = new RelayCommand(OpenTaskScheduler);
            this.CreateTaskCommand = new RelayCommand(() => this.CreateTaskRequested?.Invoke());

            this.ConfirmCommand = new AsyncRelayCommand(this.ExecuteConfirmedAsync);
            this.CancelConfirmCommand = new RelayCommand(() => this.ConfirmVisible = false);

            this.statusTimer = new DispatcherTimer { Interval = PollInterval };
            this.statusTimer.Tick += async (_, _) => await this.ReloadAsync(quiet: true).ConfigureAwait(true);

            // From the start, for as long as the console runs: a console started with Windows sits
            // in the tray and may never show this page at all.
            this.watchTimer = new DispatcherTimer { Interval = WatchInterval };
            this.watchTimer.Tick += (_, _) => ErrorLog.Observe(this.WatchAsync(), "desktop task watch");
            if (this.IsAvailable)
            {
                this.watchTimer.Start();
            }

            Localizer.Changed += () =>
            {
                foreach (var task in this.Tasks)
                {
                    task.RefreshLocalized();
                }

                this.Raise(nameof(this.UnavailableText));
            };
        }

        /// <summary>Raised when the user asks to edit the configuration behind a task.</summary>
        public event Action<DesktopTaskEntry>? OpenConfigRequested;

        /// <summary>Raised when the user asks to see a task's logs.</summary>
        public event Action<DesktopTaskEntry>? OpenLogsRequested;

        /// <summary>Raised when the user asks for the wizard.</summary>
        public event Action? CreateTaskRequested;

        public event Action<string, bool>? Toast;

        /// <summary>
        /// Raised for each notice about a task's stops — a crash, a restart loop's count at the end
        /// of its window, a crashed task running again — marked <see cref="StopNotice.DesktopTask"/>.
        /// On the UI thread, in the order they are to be told, only while notifications are on and
        /// only for a task whose own alerts are on; see <see cref="DesktopTaskWatch"/>.
        /// </summary>
        public event Action<StopNotice>? StopNoticed;

        public ObservableCollection<DesktopTaskEntry> Tasks { get; } = new();

        public ICollectionView TasksView { get; }

        public AsyncRelayCommand ReloadCommand { get; }

        public AsyncRelayCommand StartCommand { get; }

        public AsyncRelayCommand StopCommand { get; }

        public AsyncRelayCommand RestartCommand { get; }

        public AsyncRelayCommand ToggleEnabledCommand { get; }

        public RelayCommand DeleteCommand { get; }

        public RelayCommand EditConfigCommand { get; }

        public RelayCommand ViewLogsCommand { get; }

        public RelayCommand OpenFolderCommand { get; }

        public RelayCommand OpenSchedulerCommand { get; }

        public RelayCommand CreateTaskCommand { get; }

        public AsyncRelayCommand ConfirmCommand { get; }

        public RelayCommand CancelConfirmCommand { get; }

        /// <summary>False on a machine whose task scheduler cannot be reached at all.</summary>
        public bool IsAvailable { get; } = DesktopTasks.IsAvailable;

        public string UnavailableText => Localizer.Get("M.Task.Unavailable");

        public bool IsEmpty => this.Tasks.Count == 0 && !this.isScanning;

        public int TotalCount => this.Tasks.Count;

        public int RunningCount => this.Tasks.Count(t => t.Health == ServiceHealth.Running);

        public DesktopTaskEntry? SelectedTask
        {
            get => this.selectedTask;
            set
            {
                if (this.Set(ref this.selectedTask, value))
                {
                    this.RefreshCommands();
                    this.Raise(nameof(this.SelectedTaskAlerts));
                }
            }
        }

        /// <summary>
        /// Whether the selected task's unexpected stops are told, in the tray and the group chat:
        /// the task's checkbox. On unless it has been turned off for the task, which is kept in the
        /// settings by name; see <see cref="AppSettings.QuietDesktopTasks"/>. The setting that turns
        /// every notification off turns these off with the services'.
        /// </summary>
        public bool SelectedTaskAlerts
        {
            get => this.selectedTask is { } task && AlertsOn(task.Name);
            set
            {
                if (this.selectedTask is not { } task || value == AlertsOn(task.Name))
                {
                    return;
                }

                var quiet = AppSettings.Current.QuietDesktopTasks ??= new List<string>();
                if (value)
                {
                    quiet.RemoveAll(name => string.Equals(name, task.Name, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    quiet.Add(task.Name);
                }

                AppSettings.Current.Save();
                this.Raise();
            }
        }

        public string SearchText
        {
            get => this.searchText;
            set
            {
                if (this.Set(ref this.searchText, value))
                {
                    this.TasksView.Refresh();
                }
            }
        }

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        public bool IsBusy
        {
            get => this.isBusy;
            private set => this.Set(ref this.isBusy, value);
        }

        public bool IsScanning
        {
            get => this.isScanning;
            private set
            {
                if (this.Set(ref this.isScanning, value))
                {
                    this.Raise(nameof(this.IsEmpty));
                }
            }
        }

        public bool ConfirmVisible
        {
            get => this.confirmVisible;
            set => this.Set(ref this.confirmVisible, value);
        }

        public string ConfirmTitle
        {
            get => this.confirmTitle;
            private set => this.Set(ref this.confirmTitle, value);
        }

        public string ConfirmMessage
        {
            get => this.confirmMessage;
            private set => this.Set(ref this.confirmMessage, value);
        }

        public string ConfirmActionLabel
        {
            get => this.confirmActionLabel;
            private set => this.Set(ref this.confirmActionLabel, value);
        }

        /// <summary>Brings a task into view once the next scan has found it.</summary>
        public void SelectWhenReady(string name) => this.pendingSelection = name;

        /// <summary>Selects a task by name now, or once the next scan has found it: from a tray notification.</summary>
        public void SelectByName(string name)
        {
            if (this.Tasks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                this.SelectedTask = match;
            }
            else
            {
                this.pendingSelection = name;
            }
        }

        public void Activate()
        {
            if (!this.IsAvailable)
            {
                return;
            }

            this.pageShown = true;
            ErrorLog.Observe(this.ReloadAsync(quiet: this.Tasks.Count > 0), "desktop task list");
            this.statusTimer.Start();
        }

        /// <summary>
        /// The page is no longer in front. Its four-second reading stops; the half-minute one
        /// behind it (<see cref="WatchAsync"/>) goes on telling stops.
        /// </summary>
        public void Deactivate()
        {
            this.pageShown = false;
            this.statusTimer.Stop();
        }

        /// <summary>
        /// Re-reads the whole folder. The task scheduler hands back a registered task, not a
        /// status word, so the page's reading is a full one; behind the page, the state alone is
        /// read instead (<see cref="WatchAsync"/>).
        /// </summary>
        public async Task ReloadAsync(bool quiet)
        {
            if (!this.IsAvailable || this.reloading)
            {
                return;
            }

            this.reloading = true;

            if (!quiet)
            {
                this.IsScanning = true;
                this.StatusMessage = Localizer.Get("M.Task.Scanning");
            }

            try
            {
                var requested = this.Requested();
                var found = await Task.Run(DesktopTasks.List).ConfigureAwait(true);
                this.Merge(found);
                this.AnnounceStops(requested);

                if (!quiet)
                {
                    this.StatusMessage = Localizer.Format("M.Task.Found", found.Count);
                }
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Task.ScanFailed", e.Message);
            }
            finally
            {
                this.IsScanning = false;
                this.reloading = false;
            }
        }

        /// <summary>
        /// A stop would be told: the tray notification is on, or a webhook is set. The Services
        /// page's rule for reading behind another page, for the same reason.
        /// </summary>
        private static bool WatchesForStops => AppSettings.Current.NotifyOnUnexpectedStop || AlertWebhook.IsConfigured;

        /// <summary>
        /// The reading every half-minute while the page is not shown: the state of the tasks the
        /// page's last reading found, and nothing else, handed on to be told. Not while the page
        /// is shown, whose own reading every four seconds is a full one and tells the same; not
        /// while another reading is under way; not while no stop would be told.
        /// </summary>
        /// <remarks>
        /// Tasks it does not know are left alone — the unattended alert's, which is in the same
        /// folder, and one registered since the last full reading, which the page's next reading
        /// adds. The wizard's own registrations are read in at once.
        /// </remarks>
        private async Task WatchAsync()
        {
            if (this.pageShown || this.reloading || this.Tasks.Count == 0 || !WatchesForStops)
            {
                return;
            }

            this.reloading = true;
            try
            {
                var requested = this.Requested();
                var readings = await Task.Run(DesktopTasks.ReadStates).ConfigureAwait(true);
                foreach (var reading in readings)
                {
                    if (this.Tasks.FirstOrDefault(t => string.Equals(t.Name, reading.Name, StringComparison.OrdinalIgnoreCase)) is { } entry)
                    {
                        entry.ApplyState(reading);
                    }
                }

                this.Raise(nameof(this.RunningCount));
                this.RefreshCommands();
                this.AnnounceStops(requested);
            }
            catch (Exception e) when (e is COMException or IOException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // The task scheduler could not be asked this time. The next reading asks again,
                // and the page says what is wrong when it is opened; a line in the error log
                // twice a minute for as long as it cannot be asked would say nothing more.
            }
            finally
            {
                this.reloading = false;
            }
        }

        /// <summary>
        /// The tasks an operation from this page holds as a reading begins. A reading counts a task
        /// as held when it was held then or is held when the reading is applied: an operation that
        /// lets go while a reading is out would otherwise have its own stop told by that reading.
        /// </summary>
        private HashSet<string> Requested() =>
            new(this.Tasks.Select(t => t.Name).Where(this.inFlight.Contains), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Hands every task's state to the watch and raises what it decides to tell. Every reading
        /// is handed over whatever the settings say, so that the watch is right the moment they
        /// allow it to tell; only the raising depends on them, as on the Services page.
        /// </summary>
        private void AnnounceStops(HashSet<string> requestedAtStart)
        {
            bool notify = AppSettings.Current.NotifyOnUnexpectedStop;
            var now = DateTime.UtcNow;

            foreach (var entry in this.Tasks)
            {
                var notices = this.watch.Observe(
                    entry.Name,
                    entry.State,
                    entry.LastResult,
                    requested: requestedAtStart.Contains(entry.Name) || this.inFlight.Contains(entry.Name),
                    now);

                if (!notify || !AlertsOn(entry.Name))
                {
                    continue;
                }

                foreach (var notice in notices)
                {
                    this.StopNoticed?.Invoke(notice);
                }
            }
        }

        /// <summary>A task's stops are told unless its checkbox has been cleared; see <see cref="SelectedTaskAlerts"/>.</summary>
        private static bool AlertsOn(string taskName) =>
            AppSettings.Current.QuietDesktopTasks?.Contains(taskName, StringComparer.OrdinalIgnoreCase) != true;

        /// <summary>
        /// Folds a scan into the collection in place, so that the selection, the scroll
        /// position and the row identity survive a refresh that changes nothing.
        /// </summary>
        private void Merge(IReadOnlyList<DesktopTaskInfo> found)
        {
            var byName = new Dictionary<string, DesktopTaskInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in found)
            {
                byName[info.Name] = info;
            }

            for (int i = this.Tasks.Count - 1; i >= 0; i--)
            {
                var existing = this.Tasks[i];
                if (byName.TryGetValue(existing.Name, out var info))
                {
                    existing.Apply(info);
                }
                else
                {
                    this.watch.Forget(existing.Name);
                    this.Tasks.RemoveAt(i);
                }
            }

            var known = new HashSet<string>(this.Tasks.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var info in found)
            {
                if (known.Add(info.Name))
                {
                    this.Tasks.Add(new DesktopTaskEntry(info));
                }
            }

            this.Raise(nameof(this.TotalCount));
            this.Raise(nameof(this.RunningCount));
            this.Raise(nameof(this.IsEmpty));
            this.RefreshCommands();

            if (this.pendingSelection is { } pending)
            {
                var match = this.Tasks.FirstOrDefault(t => string.Equals(t.Name, pending, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    this.pendingSelection = null;
                    this.SelectedTask = match;
                }
            }
        }

        private Task StartAsync() => this.RunAsync(DesktopTaskOperation.Start, entry => DesktopTasks.Start(entry.Name));

        private Task StopAsync() => this.RunAsync(DesktopTaskOperation.Stop, entry => DesktopTasks.Stop(entry.Name, entry.Name, this.GraceFor(entry)));

        private Task RestartAsync() => this.RunAsync(DesktopTaskOperation.Restart, entry =>
        {
            if (entry.State == DesktopTaskState.Running)
            {
                DesktopTasks.Stop(entry.Name, entry.Name, this.GraceFor(entry));
            }

            DesktopTasks.Start(entry.Name);
        });

        private Task ToggleEnabledAsync() => this.RunAsync(
            this.selectedTask?.Enabled == true ? DesktopTaskOperation.Disable : DesktopTaskOperation.Enable,
            entry => DesktopTasks.SetEnabled(entry.Name, !entry.Enabled));

        private Task DeleteAsync() => this.RunAsync(DesktopTaskOperation.Delete, entry =>
        {
            if (entry.State == DesktopTaskState.Running)
            {
                DesktopTasks.Stop(entry.Name, entry.Name, this.GraceFor(entry));
            }

            DesktopTasks.Delete(entry.Name);
        });

        /// <summary>
        /// Runs one operation against the selected task off the interface thread. Every call
        /// here is a blocking COM round trip, and a stop waits for the program to shut down.
        /// </summary>
        private async Task RunAsync(DesktopTaskOperation kind, Action<DesktopTaskEntry> operation)
        {
            var entry = this.selectedTask;
            if (entry is null)
            {
                return;
            }

            string verb = Localizer.Get(DesktopTasks.VerbKey(kind));

            // The log is English whatever the interface language, like every line in it.
            string logged = "task " + kind.ToString().ToLowerInvariant();

            this.IsBusy = true;
            this.StatusMessage = Localizer.Format("M.Task.Running", verb, entry.Name);

            // Held until the state has been read back afterwards: the task ending meanwhile is this
            // operation's doing, not a crash to tell. Taken last before the try, so that nothing
            // can leave it held.
            var held = this.inFlight.Begin(new[] { entry.Name });
            try
            {
                await Task.Run(() => operation(entry)).ConfigureAwait(true);
                ActionLog.Record(logged, entry.Name, "ok");
                this.StatusMessage = Localizer.Format("M.Task.Completed", verb, entry.Name);
                this.Toast?.Invoke(this.StatusMessage, false);
            }
            catch (Exception e)
            {
                ActionLog.Record(logged, entry.Name, "failed: " + e.Message);

                // Named, because the toast can outlive the selection it was about.
                this.StatusMessage = Localizer.Format("M.Task.Failed", verb, entry.Name, e.Message);
                this.Toast?.Invoke(this.StatusMessage, true);
            }
            finally
            {
                this.IsBusy = false;
                try
                {
                    // Read back by a reading of this operation's own, not by one already under way
                    // when it ended, which may have read the task before it stopped: the next
                    // reading would then find the stop with nothing holding the task, and tell
                    // it as a crash. A reading always lets go of the flag when it ends.
                    while (this.reloading)
                    {
                        await Task.Delay(100).ConfigureAwait(true);
                    }

                    await this.ReloadAsync(quiet: true).ConfigureAwait(true);
                }
                finally
                {
                    held.Dispose();
                }
            }
        }

        /// <summary>
        /// How long to let the wrapper shut its child down before the task scheduler
        /// terminates it: the configured stop timeout, with room for the wrapper's own
        /// bookkeeping on either side of it.
        /// </summary>
        private TimeSpan GraceFor(DesktopTaskEntry entry)
        {
            try
            {
                if (!string.IsNullOrEmpty(entry.ConfigPath) && File.Exists(entry.ConfigPath))
                {
                    var model = ServiceConfigModel.Load(entry.ConfigPath);
                    if (!string.IsNullOrWhiteSpace(model.StopTimeout) && ServiceConfigModel.TryParseTime(model.StopTimeout!, out var stopTimeout))
                    {
                        return stopTimeout + TimeSpan.FromSeconds(10);
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
            }

            return TimeSpan.FromSeconds(25);
        }

        private bool Matches(object item)
        {
            if (this.searchText.Length == 0)
            {
                return true;
            }

            return item is DesktopTaskEntry entry
                && (entry.Name.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
                    || entry.DisplayName.Contains(this.searchText, StringComparison.OrdinalIgnoreCase)
                    || entry.ConfigPath.Contains(this.searchText, StringComparison.OrdinalIgnoreCase));
        }

        private void Ask(string title, string message, string actionLabel, Func<Task> action)
        {
            this.ConfirmTitle = title;
            this.ConfirmMessage = message;
            this.ConfirmActionLabel = actionLabel;
            this.pendingAction = action;
            this.ConfirmVisible = true;
        }

        private async Task ExecuteConfirmedAsync()
        {
            var action = this.pendingAction;
            this.pendingAction = null;
            this.ConfirmVisible = false;

            if (action != null)
            {
                await action().ConfigureAwait(true);
            }
        }

        private void OpenContainingFolder()
        {
            string? target = this.selectedTask?.ConfigPath;
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                this.StatusMessage = Localizer.Get("M.Dash.NothingToReveal");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Common.ExplorerFailed", e.Message);
            }
        }

        /// <summary>Opens the Windows task scheduler, for anything this panel does not cover.</summary>
        private static void OpenTaskScheduler()
        {
            try
            {
                Process.Start(new ProcessStartInfo("mmc.exe", "taskschd.msc") { UseShellExecute = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        private void RefreshCommands()
        {
            this.StartCommand.RaiseCanExecuteChanged();
            this.StopCommand.RaiseCanExecuteChanged();
            this.RestartCommand.RaiseCanExecuteChanged();
            this.ToggleEnabledCommand.RaiseCanExecuteChanged();
            this.DeleteCommand.RaiseCanExecuteChanged();
            this.EditConfigCommand.RaiseCanExecuteChanged();
            this.ViewLogsCommand.RaiseCanExecuteChanged();
            this.OpenFolderCommand.RaiseCanExecuteChanged();
        }
    }
}
