using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Globalization;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One choice in the scheduled-restart picker: off, every day, or one weekday.</summary>
    public sealed class RestartChoice : ObservableObject
    {
        public RestartChoice(bool isOff, DayOfWeek? day)
        {
            this.IsOff = isOff;
            this.Day = day;
        }

        public bool IsOff { get; }

        /// <summary>The weekday, or null for every day.</summary>
        public DayOfWeek? Day { get; }

        /// <summary>Weekday names come from the interface language's own calendar.</summary>
        public string Label => this.IsOff
            ? Localizer.Get("M.Sched.Off")
            : this.Day is DayOfWeek day
                ? Localizer.Format("M.Sched.Weekly", CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName(day))
                : Localizer.Get("M.Sched.Daily");

        public void RefreshLocalized() => this.Raise(nameof(this.Label));
    }

    /// <summary>A service's group as its heading: the ungrouped are headed as such, in the interface's language.</summary>
    public sealed class GroupHeadingConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string group && group.Length > 0 ? group : Localizer.Get("M.Group.None");

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The service management panel: what is installed, what state it is in, and the
    /// operations that change that state.
    /// </summary>
    public sealed class DashboardViewModel : ObservableObject
    {
        /// <summary>
        /// How often service state is re-read. Fast enough that a start or stop feels
        /// immediate, slow enough that a machine with hundreds of services stays idle.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The poll rate for a short while after an operation. A start or stop moves the
        /// service through pending states over a few seconds, and at two seconds a tick the
        /// row could sit on the old state for a whole cycle per transition — long enough to
        /// look as though the click had done nothing.
        /// </summary>
        private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(400);

        private static readonly TimeSpan BurstLength = TimeSpan.FromSeconds(10);

        private readonly DispatcherTimer statusTimer;
        private readonly DispatcherTimer rescanTimer;

        /// <summary>Which stops are told, and how; fed every reading, raised through <see cref="UnexpectedStop"/> and <see cref="StopNoticed"/>.</summary>
        private readonly CrashAnnouncer announcer = new();

        /// <summary>The services a command from this panel is working on; see <see cref="IsOperationInFlight"/>.</summary>
        private readonly OperationsInFlight inFlight = new();

        /// <summary>
        /// This is the page in front, between <see cref="Activate"/> and <see cref="Deactivate"/>.
        /// Behind another page the poll reads states alone; see <see cref="Deactivate"/>.
        /// </summary>
        private bool pageShown;

        /// <summary>
        /// The window is in the tray, between <see cref="KeepWatching"/> and <see cref="LeaveTray"/>.
        /// Nobody sees the page then either, in front or not, and the poll reads states alone.
        /// </summary>
        private bool inTray;

        private ServiceEntry? selectedService;
        private string searchText = string.Empty;

        /// <summary>
        /// <see cref="SearchText"/> trimmed once, when it changes, rather than once per row
        /// inside the filter on every keystroke. The log filter had the same allocation.
        /// </summary>
        private string searchNeedle = string.Empty;
        private string statusMessage = string.Empty;

        /// <summary>
        /// Operations running, of any kind. A count rather than a flag: operations on different
        /// services overlap, and the first to finish must not take the progress bar away from
        /// the ones still running.
        /// </summary>
        private int busyCount;
        private bool isScanning;
        private bool polling;
        private DateTime burstUntil;
        private ProcessNode? processTree;
        private bool confirmVisible;
        private string confirmTitle = string.Empty;
        private string confirmMessage = string.Empty;
        private string confirmActionLabel = "Confirm";
        private Func<Task>? pendingAction;
        private IReadOnlyList<ServiceEntry> selectedEntries = Array.Empty<ServiceEntry>();
        private string? pendingConfigPath;
        private string? pendingServiceName;
        private string healthFilter = "all";
        private bool sortByStatus = AppSettings.Current.SortServicesByStatus;
        private bool groupServices = AppSettings.Current.GroupServices;

        /// <summary>
        /// How long the selection has to rest before its restart schedule is read. The read is a
        /// connection to the task scheduler, and moving down the list with the arrow keys is not
        /// a request for one per row passed over.
        /// </summary>
        private static readonly TimeSpan ScheduleReadDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How long a stopped service has to stay selected before why it stopped is read. Moving
        /// down the list is not a request for a read of every stopped row passed over; and at the
        /// first reading that sees a service stopped, the wrapper may still be writing its last
        /// lines and Windows its record of the stop.
        /// </summary>
        private static readonly TimeSpan LastStopReadDelay = TimeSpan.FromSeconds(1.5);

        /// <summary>A service started from this panel, watched for falling over straight after; see <see cref="StartWatch"/>.</summary>
        private ServiceEntry? startWatched;
        private StartWatch? startWatch;

        private RestartChoice selectedRestartChoice;
        private string restartTime = "03:00";
        private string restartScheduleNote = string.Empty;
        private RestartSchedule? restartSchedule;
        private bool restartScheduleKnown;

        public DashboardViewModel()
        {
            this.ServicesView = CollectionViewSource.GetDefaultView(this.Services);
            this.ServicesView.Filter = this.MatchesSearch;
            if (this.ServicesView is ICollectionViewLiveShaping live)
            {
                // Rows move as their state changes when sorting by status, without a manual
                // refresh. Whether it is switched on is ApplySort's decision, not this one.
                live.LiveSortingProperties.Add(nameof(ServiceEntry.SortRank));
            }

            this.ApplySort();

            // Each command reads the selection once, when it is clicked, and everything after that
            // — the operation, and any question it asks on the way — runs on the service it read.
            // The selection can move while a stop waits, or while a question is up: the list stays
            // usable, and a click on a tray notification selects the service it names.
            this.ReloadCommand = new AsyncRelayCommand(() => this.ReloadAsync(quiet: false));
            this.StartCommand = new AsyncRelayCommand(
                () => this.RunAsync(this.selectedService, "start", (w, c) => WinSwCli.StartAsync(w, c)),
                () => this.selectedService?.CanStart == true && this.IsIdle(this.selectedService, "start"));
            this.StopCommand = new AsyncRelayCommand(
                () => this.StopAsync(this.selectedService, force: false),
                () => this.selectedService?.CanStop == true && this.IsIdle(this.selectedService, "stop"));

            // Not for a disabled service, which Windows would stop and then refuse to start again.
            this.RestartCommand = new AsyncRelayCommand(
                () => this.RestartAsync(this.selectedService, force: false),
                () => this.selectedService?.IsStartable == true && this.IsIdle(this.selectedService, "restart"));

            this.RefreshConfigCommand = new AsyncRelayCommand(
                () => this.RunAsync(this.selectedService, "refresh", (w, c) => WinSwCli.RefreshAsync(w, c)),
                () => this.IsIdle(this.selectedService, "refresh"));

            // Ending a stray holds nothing, but is not offered while a command is working on the
            // same service: the banner was read before that command began, and it is about to
            // change what is running.
            this.TerminateStrayCommand = new RelayCommand(this.AskTerminateStray, () => this.selectedService?.CanEndStray == true && this.IsIdle(this.selectedService, null));
            this.EndStrayParentCommand = new RelayCommand(this.AskEndStrayParent, () => this.selectedService?.CanEndStrayParent == true && this.IsIdle(this.selectedService, null));

            // Neither needs the configuration: the start type is changed through sc.exe, by name.
            this.StopRestartingCommand = new RelayCommand(this.AskStopRestarting, () => this.selectedService?.CanStopRestarting == true && this.IsIdle(this.selectedService, null));
            this.RestoreStartTypeCommand = new AsyncRelayCommand(this.RestoreStartTypeAsync, () => this.CanRestoreStartType && this.IsIdle(this.selectedService, null));

            this.KillCommand = new RelayCommand(this.AskKill, () => this.IsIdle(this.selectedService, "dev kill"));
            this.UninstallCommand = new RelayCommand(this.AskUninstall, () => this.IsIdle(this.selectedService, "uninstall"));

            this.EditConfigCommand = new RelayCommand(
                () => this.OpenConfigRequested?.Invoke(this.selectedService!),
                () => this.selectedService?.ConfigPath != null);

            this.ViewLogsCommand = new RelayCommand(
                () => this.OpenLogsRequested?.Invoke(this.selectedService!),
                () => this.selectedService?.ConfigPath != null);

            this.OpenErrorLogCommand = new RelayCommand(this.OpenErrorLog, () => this.selectedService?.LastStop?.ErrorLog is { Exists: true });
            this.ReadLastStopAgainCommand = new RelayCommand(this.ReadLastStopAgain, () => this.selectedService is { IsStopped: true, LastStop: not null });

            this.OpenFolderCommand = new RelayCommand(this.OpenContainingFolder, () => this.selectedService != null);

            // Unlike Reveal, this one has to read the configuration to know where to go.
            this.OpenWorkingDirectoryCommand = new RelayCommand(this.OpenWorkingDirectory, () => this.selectedService?.ConfigPath != null);

            this.ConfirmCommand = new AsyncRelayCommand(this.ExecuteConfirmedAsync);
            this.CancelConfirmCommand = new RelayCommand(() => this.ConfirmVisible = false);

            this.StartSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("start"), () => this.CanRunOnSelected("start"));
            this.StopSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("stop"), () => this.CanRunOnSelected("stop"));
            this.RestartSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("restart"), () => this.CanRunOnSelected("restart"));

            this.SetFilterCommand = new RelayCommand(p => this.ChooseFilter(p as string ?? "all"));
            this.CreateServiceCommand = new RelayCommand(() => this.CreateServiceRequested?.Invoke());
            this.OpenConfigFileCommand = new RelayCommand(() => this.OpenConfigFileRequested?.Invoke());

            this.ExportScriptCommand = new RelayCommand(this.ExportScript, () => this.selectedService?.ConfigPath != null);
            this.DiagnosticsCommand = new AsyncRelayCommand(this.CreateDiagnosticsAsync, () => this.selectedService != null);
            this.UpgradeWrapperCommand = new AsyncRelayCommand(this.UpgradeWrapperAsync, () => this.WrapperUpdateAvailable && this.IsIdle(this.selectedService, "upgrade"));

            this.RestartChoices = new[] { new RestartChoice(true, null), new RestartChoice(false, null) }
                .Concat(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
                    .Select(day => new RestartChoice(false, day)))
                .ToArray();
            this.selectedRestartChoice = this.RestartChoices[0];
            this.ApplyRestartScheduleCommand = new AsyncRelayCommand(this.ApplyRestartScheduleAsync, () => this.selectedService?.ConfigPath != null);

            // An operation beginning or ending changes which services' commands are available.
            this.inFlight.Changed += this.OnOperationsChanged;

            this.statusTimer = new DispatcherTimer { Interval = PollInterval };
            this.statusTimer.Tick += async (_, _) =>
            {
                // Behind another page, or with the window in the tray, a reading is taken only
                // while there is someone to tell about a stop, and it is of states alone; see
                // Deactivate. Decided here, tick by tick, rather than when the page was left:
                // notifications or a webhook turned on on the settings page take effect from the
                // next tick.
                bool shown = this.pageShown && !this.inTray;

                // A reading that outlasts the interval must not have another started on top of
                // it. The explicit refreshes elsewhere are deliberately not gated: they are
                // what makes the panel answer at once after an operation, and overlapping is
                // harmless because every write happens on this thread.
                if (!this.polling && (shown || WatchesForStops))
                {
                    await this.RefreshStatusesAsync(statesOnly: !shown).ConfigureAwait(true);
                }

                if (this.burstUntil != default && DateTime.UtcNow > this.burstUntil)
                {
                    this.EndBurst();
                }
            };

            // Services installed by other tools, or by a second copy of this GUI, appear
            // without the user having to remember the rescan button.
            int seconds = AppSettings.Current.AutoRescanSeconds;
            this.rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds > 0 ? seconds : 30) };
            this.rescanTimer.Tick += async (_, _) =>
            {
                if (seconds > 0 && !this.isScanning && !this.IsBusy)
                {
                    await this.ReloadAsync(quiet: true).ConfigureAwait(true);
                }
            };

            Localizer.Changed += () =>
            {
                foreach (var entry in this.Services)
                {
                    entry.RefreshLocalized();
                }

                this.RaiseWrapperUpdate();
                this.Raise(nameof(this.SelectedCountText));
                this.Raise(nameof(this.RestoreStartTypeText));

                foreach (var choice in this.RestartChoices)
                {
                    choice.RefreshLocalized();
                }

                // The ungrouped heading is in the interface's language.
                if (this.groupServices)
                {
                    this.ApplySort();
                }

                if (this.restartScheduleNote.Length > 0)
                {
                    this.RestartScheduleNote = Localizer.Get("M.Sched.Unreadable");
                }
            };
        }

        /// <summary>Raised when the user asks to edit the selected service's configuration.</summary>
        public event Action<ServiceEntry>? OpenConfigRequested;

        /// <summary>Raised when the user asks to tail the selected service's logs.</summary>
        public event Action<ServiceEntry>? OpenLogsRequested;

        /// <summary>
        /// Raised when the user asks for one particular file of a service's logs — its err.log, from
        /// the Last stop card — with the service and the file's full path. Without a handler, the
        /// request goes out as <see cref="OpenLogsRequested"/>, which opens the newest file.
        /// </summary>
        public event Action<ServiceEntry, string>? OpenLogFileRequested;

        /// <summary>
        /// Raised when a service crashes — goes from running to stopped, with a failure exit code,
        /// without this GUI asking it to — and it is the first crash in a while: the rest of a
        /// restart loop is counted and comes through <see cref="StopNoticed"/>. The entry's
        /// <see cref="ServiceEntry.CrashCount"/> is 1 when this is raised. Only while
        /// notifications are on; see <see cref="CrashAnnouncer"/> for the rule.
        /// </summary>
        public event Action<ServiceEntry>? UnexpectedStop;

        /// <summary>
        /// Raised for every other notice about a service's stops: the count of a restart loop
        /// when its window ends (<see cref="StopNoticeKind.RepeatedStops"/>), a stop with exit
        /// code 0 (<see cref="StopNoticeKind.CleanStop"/>), and a crashed service running again
        /// (<see cref="StopNoticeKind.Recovered"/>). On the UI thread, only while notifications
        /// are on, in the order the notices are to be told.
        /// </summary>
        public event Action<StopNotice>? StopNoticed;

        /// <summary>Raised with the outcome of an operation, for a transient on-screen notice.</summary>
        public event Action<string, bool>? Toast;

        public event Action? CreateServiceRequested;

        public event Action? OpenConfigFileRequested;

        public RelayCommand SetFilterCommand { get; }

        public RelayCommand CreateServiceCommand { get; }

        public RelayCommand OpenConfigFileCommand { get; }

        /// <summary>"all", "running", "stopped" or "problem"; the stat cards set it.</summary>
        public string HealthFilter
        {
            get => this.healthFilter;
            set
            {
                if (this.Set(ref this.healthFilter, value ?? "all"))
                {
                    this.ServicesView.Refresh();
                }
            }
        }

        public bool SortByStatus
        {
            get => this.sortByStatus;
            set
            {
                if (this.Set(ref this.sortByStatus, value))
                {
                    AppSettings.Current.SortServicesByStatus = value;
                    AppSettings.Current.Save();
                    this.ApplySort();
                }
            }
        }

        /// <summary>The list under group headings, ungrouped services last.</summary>
        public bool GroupServices
        {
            get => this.groupServices;
            set
            {
                if (this.Set(ref this.groupServices, value))
                {
                    AppSettings.Current.GroupServices = value;
                    AppSettings.Current.Save();
                    this.ApplySort();
                }
            }
        }

        /// <summary>Every group in use, for the picker in the detail panel.</summary>
        public IReadOnlyList<string> KnownGroups =>
            AppSettings.Current.ServiceGroups.Values
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>
        /// Files <paramref name="entry"/> under <paramref name="text"/>, or under nothing when it is
        /// blank. Given the entry rather than reading the selection: the edit belongs to the
        /// service the field was showing, whatever has been selected since.
        /// </summary>
        public void AssignGroup(ServiceEntry entry, string text)
        {
            string group = text.Trim();
            if (string.Equals(group, entry.Group, StringComparison.Ordinal))
            {
                return;
            }

            var groups = AppSettings.Current.ServiceGroups;
            if (group.Length == 0)
            {
                groups.Remove(entry.ServiceName);
            }
            else
            {
                groups[entry.ServiceName] = group;
            }

            AppSettings.Current.Save();

            // The picker's list first, the entry's group after it: an editable ComboBox whose
            // items are replaced can clear its text, and the binding from the entry is what
            // has to have the last word.
            this.Raise(nameof(this.KnownGroups));
            entry.Group = group;

            if (this.groupServices || this.searchNeedle.Length > 0)
            {
                this.ServicesView.Refresh();
            }
        }

        /// <summary>No services at all, and not because a scan is still running: show the getting-started card.</summary>
        public bool IsEmpty => this.Services.Count == 0 && !this.isScanning;

        /// <summary>Selects a service by ID once the next scan has finished (used after the wizard installs one).</summary>
        public void SelectServiceWhenReady(string serviceName) => this.pendingServiceName = serviceName;

        public void SelectByName(string serviceName)
        {
            var match = this.Services.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                this.SelectedService = match;
            }
        }

        public ObservableCollection<ServiceEntry> Services { get; } = new();

        public ICollectionView ServicesView { get; }

        public AsyncRelayCommand ReloadCommand { get; }

        public AsyncRelayCommand StartCommand { get; }

        public AsyncRelayCommand StopCommand { get; }

        public AsyncRelayCommand RestartCommand { get; }

        public AsyncRelayCommand RefreshConfigCommand { get; }

        public RelayCommand KillCommand { get; }

        public RelayCommand UninstallCommand { get; }

        public RelayCommand EditConfigCommand { get; }

        public RelayCommand ViewLogsCommand { get; }

        /// <summary>Opens the stopped service's err.log on the Logs page, from the Last stop card.</summary>
        public RelayCommand OpenErrorLogCommand { get; }

        /// <summary>Reads why the service stopped again, for a report read before everything was written.</summary>
        public RelayCommand ReadLastStopAgainCommand { get; }

        public RelayCommand OpenFolderCommand { get; }

        public RelayCommand OpenWorkingDirectoryCommand { get; }

        public AsyncRelayCommand ConfirmCommand { get; }

        public RelayCommand CancelConfirmCommand { get; }

        public AsyncRelayCommand StartSelectedCommand { get; }

        public AsyncRelayCommand StopSelectedCommand { get; }

        public AsyncRelayCommand RestartSelectedCommand { get; }

        public RelayCommand ExportScriptCommand { get; }

        public AsyncRelayCommand DiagnosticsCommand { get; }

        public AsyncRelayCommand UpgradeWrapperCommand { get; }

        /// <summary>Every highlighted row; the view keeps this in step with the list's multi-selection.</summary>
        public IReadOnlyList<ServiceEntry> SelectedEntries
        {
            get => this.selectedEntries;
            set
            {
                this.selectedEntries = value ?? Array.Empty<ServiceEntry>();
                this.Raise();
                this.Raise(nameof(this.HasMultipleSelected));
                this.Raise(nameof(this.SelectedCountText));
                this.StartSelectedCommand.RaiseCanExecuteChanged();
                this.StopSelectedCommand.RaiseCanExecuteChanged();
                this.RestartSelectedCommand.RaiseCanExecuteChanged();
            }
        }

        public bool HasMultipleSelected => this.selectedEntries.Count > 1;

        public string SelectedCountText => Localizer.Format("M.Dash.SelectedCount", this.selectedEntries.Count);

        // Wrapper updates ------------------------------------------------------

        /// <summary>
        /// The wrapper an upgrade installs is the one carried inside this application, not
        /// the newest upstream release: this branch is ahead of that release, the file is
        /// already on disk, and the upgrade therefore works with no network at all.
        /// </summary>
        public bool WrapperUpdateAvailable =>
            this.selectedService is { } service
            && !string.IsNullOrEmpty(service.WrapperVersion)
            && BundledWrapper.Version is { } bundled
            && UpdateChecker.IsNewer(bundled, service.WrapperVersion);

        public string WrapperUpdateText => BundledWrapper.Version is not { } bundled
            ? string.Empty
            : this.WrapperUpdateAvailable
                ? Localizer.Format("M.Dash.WrapperUpdate", bundled)
                : Localizer.Format("M.Dash.WrapperCurrent", bundled);

        public ServiceEntry? SelectedService
        {
            get => this.selectedService;
            set
            {
                if (this.Set(ref this.selectedService, value))
                {
                    this.ProcessTree = null;
                    this.RefreshCommandStates();

                    // Only the tree. Moving down the list with the arrow keys used to re-read
                    // every service on the machine for each keypress — the panel needs one
                    // process tree, and the states on screen are at most one tick old.
                    ErrorLog.Observe(this.RefreshProcessTreeAsync(), "process tree");
                    this.RaiseWrapperUpdate();
                    this.RaiseStartTypeRestore();
                    ErrorLog.Observe(this.LoadRestartScheduleAsync(), "restart schedule");
                    ErrorLog.Observe(this.LoadLastStopAsync(), "last stop");
                }
            }
        }

        /// <summary>
        /// A configuration path given on the command line. Applied after the first scan:
        /// selects the matching installed service, or is handed to the editor if none.
        /// </summary>
        public event Action<string>? OpenUninstalledConfigRequested;

        public void OpenConfigPathWhenReady(string path) => this.pendingConfigPath = path;

        public string SearchText
        {
            get => this.searchText;
            set
            {
                if (this.Set(ref this.searchText, value))
                {
                    this.searchNeedle = (value ?? string.Empty).Trim();
                    this.ServicesView.Refresh();
                }
            }
        }

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        /// <summary>Something is running, anywhere on the page; shows the progress bar.</summary>
        public bool IsBusy => this.busyCount > 0;

        /// <summary>
        /// True while a command from this panel is working on <paramref name="serviceName"/>: the
        /// service it was given for, the ones depending on it for a stop or a restart, every one
        /// sharing its wrapper for an upgrade. A change of state seen meanwhile is that command's
        /// doing, and the service's own commands are unavailable until it is over.
        /// </summary>
        public bool IsOperationInFlight(string serviceName) => this.inFlight.Contains(serviceName);

        public bool IsScanning
        {
            get => this.isScanning;
            set
            {
                if (this.Set(ref this.isScanning, value))
                {
                    this.Raise(nameof(this.IsEmpty));
                }
            }
        }

        /// <summary>The process tree of the selected service; only replaced when its shape changes.</summary>
        public ProcessNode? ProcessTree
        {
            get => this.processTree;
            set
            {
                if (this.Set(ref this.processTree, value))
                {
                    this.Raise(nameof(this.ProcessTreeRoots));
                }
            }
        }

        /// <summary>A single-element sequence, because TreeView binds to a collection.</summary>
        public ProcessNode[] ProcessTreeRoots =>
            this.processTree is null ? Array.Empty<ProcessNode>() : new[] { this.processTree };

        public int TotalCount => this.Services.Count;

        public int RunningCount => this.Services.Count(s => s.Health == ServiceHealth.Running);

        public int StoppedCount => this.Services.Count(s => s.Health == ServiceHealth.Stopped);

        /// <summary>
        /// Services that want a look for any reason, not only an unusable configuration: see
        /// <see cref="ServiceEntry.Attention"/>. Some of them are also counted as running or stopped.
        /// </summary>
        public int ProblemCount => this.Services.Count(s => s.NeedsAttention);

        // Confirmation overlay -----------------------------------------------

        public bool ConfirmVisible
        {
            get => this.confirmVisible;
            set => this.Set(ref this.confirmVisible, value);
        }

        public string ConfirmTitle
        {
            get => this.confirmTitle;
            set => this.Set(ref this.confirmTitle, value);
        }

        public string ConfirmMessage
        {
            get => this.confirmMessage;
            set => this.Set(ref this.confirmMessage, value);
        }

        public string ConfirmActionLabel
        {
            get => this.confirmActionLabel;
            set => this.Set(ref this.confirmActionLabel, value);
        }

        // A program left running ------------------------------------------------

        /// <summary>Ends the selected service's program that is running outside it; see <see cref="StrayProcesses"/>.</summary>
        public RelayCommand TerminateStrayCommand { get; }

        /// <summary>
        /// Ends what keeps starting the stray program, and with it the program: ending the program
        /// alone only has its parent start another. Never offered for a parent that is one of
        /// Windows' own; see <see cref="StrayProcesses.MayEnd"/>.
        /// </summary>
        public RelayCommand EndStrayParentCommand { get; }

        private void AskEndStrayParent()
        {
            if (this.selectedService is not { StrayProcess: { Process: var stray, Parent: { } parent } finding } entry || !entry.CanEndStrayParent)
            {
                return;
            }

            // As for the process itself: a port holder is ended for the port, not to be run again,
            // and an earlier run's leftover beside a running service leaves the service running.
            this.Ask(
                Localizer.Get("M.Dash.StrayParentTitle"),
                finding.HoldsPort
                    ? Localizer.Format("M.Dash.PortParentBody", parent.Name, parent.ProcessId, stray.Name, stray.ProcessId, finding.Port, entry.ServiceName)
                    : Localizer.Format(finding.EarlierRun ? "M.Dash.StrayEarlierRunParentBody" : "M.Dash.StrayParentBody", parent.Name, parent.ProcessId, stray.Name, stray.ProcessId, entry.ServiceName),
                Localizer.Get("M.Dash.StrayAction"),
                () => this.TerminateStrayAsync(entry, parent));
        }

        /// <summary>
        /// Asks before ending the process on the selected service's banner. Never for one of
        /// Windows' own or the kernel, which a port can be held by as readily as by a leftover.
        /// </summary>
        private void AskTerminateStray()
        {
            if (this.selectedService is not { StrayProcess: { Process: var stray } finding } entry || !StrayProcesses.MayEnd(stray))
            {
                return;
            }

            // A port holder need not be the service's program at all; the question names the port
            // it is ended for rather than calling it left behind. An earlier run's leftover beside
            // a running service is ended with the service left running, not to be started after.
            this.Ask(
                Localizer.Get(finding.HoldsPort ? "M.Dash.PortEndTitle" : "M.Dash.StrayTitle"),
                finding.HoldsPort
                    ? Localizer.Format("M.Dash.PortEndBody", stray.Name, stray.ProcessId, finding.Port, entry.ServiceName)
                    : Localizer.Format(finding.EarlierRun ? "M.Dash.StrayEarlierRunBody" : "M.Dash.StrayBody", stray.Name, stray.ProcessId, entry.ServiceName),
                Localizer.Get("M.Dash.StrayAction"),
                () => this.TerminateStrayAsync(entry, stray));
        }

        /// <summary>
        /// Ends the process and what it started, then reads the states again. The service is not
        /// started afterwards: whether to is the user's call, and Start is beside the banner.
        /// </summary>
        private async Task TerminateStrayAsync(ServiceEntry entry, ProcessMark stray)
        {
            // The last word before anything is ended, whichever question led here: the buttons and
            // the questions check the same, but a process tree ended by mistake is not undone, and
            // Windows' own — explorer.exe, a service host — are never this console's to end.
            if (!StrayProcesses.MayEnd(stray))
            {
                return;
            }

            this.BeginBusy();
            try
            {
                var result = await StrayProcesses.TerminateAsync(stray).ConfigureAwait(true);
                ActionLog.Record("end stray process", $"{entry.ServiceName} ({stray.Name}, PID {stray.ProcessId})", result);

                this.StatusMessage = result switch
                {
                    { Cancelled: true } => Localizer.Get("M.Common.ElevationDeclined"),
                    { Succeeded: true } => Localizer.Format("M.Dash.StrayEnded", stray.Name, stray.ProcessId),
                    _ => Localizer.Format("M.Dash.StrayFailed", result.Error ?? result.ExitCode.ToString(CultureInfo.InvariantCulture)),
                };
                this.Toast?.Invoke(this.StatusMessage, !result.Succeeded && !result.Cancelled);
            }
            finally
            {
                this.EndBusy();
            }

            await this.RefreshStatusesAsync().ConfigureAwait(true);
        }

        // Windows restarting a failed service -----------------------------------

        /// <summary>
        /// Sets the selected service to Disabled, after asking, so that Windows' recovery stops
        /// starting it again; see <see cref="ServiceEntry.CanStopRestarting"/>. The start type it had
        /// is remembered, and <see cref="RestoreStartTypeCommand"/> puts it back.
        /// </summary>
        public RelayCommand StopRestartingCommand { get; }

        /// <summary>Puts back the start type "Stop restarting" replaced, while the service is still disabled.</summary>
        public AsyncRelayCommand RestoreStartTypeCommand { get; }

        /// <summary>The selected service is disabled, and this console remembers what it was before it disabled it.</summary>
        public bool CanRestoreStartType => RememberedStartTypeOf(this.selectedService) != null;

        /// <summary>"Restore start type (Automatic)", naming what it goes back to.</summary>
        public string RestoreStartTypeText =>
            RememberedStartTypeOf(this.selectedService) is { } token && RememberedStartTypes.StartTypeOf(token) is { } type
                ? Localizer.Format("M.Dash.RestoreStartType", ServiceDiscovery.DescribeStartMode(type.StartType, type.Delayed))
                : string.Empty;

        /// <summary>
        /// The start type remembered for <paramref name="entry"/>, while it is still disabled. Once it
        /// is anything else, somebody has already chosen what it is to be, and the memory is not
        /// offered over their choice.
        /// </summary>
        private static string? RememberedStartTypeOf(ServiceEntry? entry) =>
            entry is { StartType: ServiceStartMode.Disabled } ? RememberedStartTypes.Current.For(entry.ServiceName) : null;

        /// <summary>
        /// The restore depends on the start type, which only a rescan reads, and on the selection:
        /// raised when either may have moved, rather than on every poll.
        /// </summary>
        private void RaiseStartTypeRestore()
        {
            this.Raise(nameof(this.CanRestoreStartType));
            this.Raise(nameof(this.RestoreStartTypeText));
            this.RestoreStartTypeCommand.RaiseCanExecuteChanged();
        }

        /// <summary>Asks before disabling the selected service, and disables that one whatever is selected by the answer.</summary>
        private void AskStopRestarting()
        {
            if (this.selectedService is not { CanStopRestarting: true } entry)
            {
                return;
            }

            this.Ask(
                Localizer.Get("M.Dash.StopRestartingTitle"),
                Localizer.Format("M.Dash.StopRestartingBody", entry.ServiceName, entry.StartMode),
                Localizer.Get("M.Dash.StopRestarting"),
                () => this.StopRestartingAsync(entry));
        }

        /// <summary>
        /// Disables the service and remembers the start type it had. Disabled rather than any
        /// gentler setting because it is the one Windows' recovery cannot get past: a restart it has
        /// already scheduled fails, and so does every one after it, whatever the failure actions say.
        /// </summary>
        private async Task StopRestartingAsync(ServiceEntry entry)
        {
            // Nothing is stopped. A start under way is not undone by the start type, only the next
            // one is refused, and it cannot be stopped either: the service control manager refuses
            // a stop to a service still starting. Running, it is left to run; it is the next
            // failure that will not be answered, and Stop is there if it should not run at all.
            string? previous = RememberedStartTypes.TokenFor(entry.StartType, entry.DelayedAutoStart);

            // Held like any command on the service until the states are read back, so that nothing
            // else is started on it meanwhile.
            var held = this.inFlight.Begin(new[] { entry.ServiceName });
            this.BeginBusy();
            try
            {
                var result = await WinSwCli.SetStartTypeAsync(entry.ServiceName, "disabled").ConfigureAwait(true);
                ActionLog.Record("stop restarting (start= disabled)", entry.ServiceName, result);

                // Only what it was before this console disabled it. Already disabled, it has nothing
                // of its own to go back to, and what was remembered the first time is kept.
                if (result.Succeeded && previous != null)
                {
                    RememberedStartTypes.Current.Remember(entry.ServiceName, previous);
                }

                this.StatusMessage = result switch
                {
                    { Cancelled: true } => Localizer.Get("M.Common.ElevationDeclined"),
                    { Succeeded: true } => Localizer.Format("M.Dash.RestartingStopped", entry.ServiceName),
                    { ExitCode: > 0 } => Localizer.Format("M.Dash.StartTypeFailed", result.ExitCode),
                    _ => result.Error ?? Localizer.Format("M.Dash.StartTypeFailed", result.ExitCode),
                };
                this.Toast?.Invoke(this.StatusMessage, !result.Succeeded && !result.Cancelled);

                // The start type is the rescan's to read, and it is what ends the restarting state
                // and offers the restore.
                if (result.Succeeded)
                {
                    await this.ReloadAsync(quiet: true).ConfigureAwait(true);
                }
            }
            finally
            {
                held.Dispose();
                this.EndBusy();
                this.BurstPolling();
            }
        }

        /// <summary>Sets the selected service back to the start type "Stop restarting" replaced. Starts nothing.</summary>
        private async Task RestoreStartTypeAsync()
        {
            if (this.selectedService is not { } entry || RememberedStartTypeOf(entry) is not { } token)
            {
                return;
            }

            var held = this.inFlight.Begin(new[] { entry.ServiceName });
            this.BeginBusy();
            try
            {
                var result = await WinSwCli.SetStartTypeAsync(entry.ServiceName, token).ConfigureAwait(true);
                ActionLog.Record("restore start type (start= " + token + ")", entry.ServiceName, result);

                string restored = RememberedStartTypes.StartTypeOf(token) is { } type
                    ? ServiceDiscovery.DescribeStartMode(type.StartType, type.Delayed)
                    : token;
                this.StatusMessage = result switch
                {
                    { Cancelled: true } => Localizer.Get("M.Common.ElevationDeclined"),
                    { Succeeded: true } => Localizer.Format("M.Dash.StartTypeRestored", entry.ServiceName, restored),
                    { ExitCode: > 0 } => Localizer.Format("M.Dash.StartTypeFailed", result.ExitCode),
                    _ => result.Error ?? Localizer.Format("M.Dash.StartTypeFailed", result.ExitCode),
                };
                this.Toast?.Invoke(this.StatusMessage, !result.Succeeded && !result.Cancelled);

                if (result.Succeeded)
                {
                    RememberedStartTypes.Current.Forget(entry.ServiceName);
                    await this.ReloadAsync(quiet: true).ConfigureAwait(true);
                }
            }
            finally
            {
                held.Dispose();
                this.EndBusy();
            }
        }

        // Why it stopped --------------------------------------------------------

        /// <summary>
        /// Reads why the selected service stopped, for the Last stop card, when it is stopped and
        /// that has not been read yet: once per stop, off this thread, after the selection has
        /// rested on it for a moment. The report is kept on the entry until its state or its exit
        /// code moves on, so going back to a service shows it without a second read.
        /// </summary>
        private async Task LoadLastStopAsync()
        {
            var entry = this.selectedService;
            if (entry?.BeginLastStopRead() is not int stop)
            {
                return;
            }

            LastStopReport? report = null;
            try
            {
                await Task.Delay(LastStopReadDelay).ConfigureAwait(true);

                // Moved on meanwhile: read when it is selected again. A service that has left the
                // stop is not read either; the entry would drop the report anyway.
                if (!ReferenceEquals(entry, this.selectedService) || !entry.IsStopped)
                {
                    return;
                }

                // What the worker needs, read here where the entry belongs.
                string serviceName = entry.ServiceName;
                string displayName = entry.DisplayName;
                string? configPath = entry.ConfigPath;
                var encoding = AppSettings.Current.LogEncoding;

                report = await Task.Run(() => LastStopReader.Read(
                    configPath,
                    encoding,
                    () => EventLogReader.Read(serviceName, displayName, LastStopReader.EventsToSearch))).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                // Said on the card rather than read again at every tick. The reader already says
                // what it could not read; this is for what it did not expect.
                report = new LastStopReport(null, null, null, null, null, readError: e.Message);
            }
            finally
            {
                entry.EndLastStopRead(stop, report);
                if (ReferenceEquals(entry, this.selectedService))
                {
                    this.OpenErrorLogCommand.RaiseCanExecuteChanged();
                    this.ReadLastStopAgainCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// Opens the stopped service's err.log on the Logs page. The file as the card read it: the
        /// newest a rolling mode has written, which the Logs page would not necessarily open first.
        /// </summary>
        private void OpenErrorLog()
        {
            if (this.selectedService is not { LastStop.ErrorLog: { Exists: true } errorLog } entry)
            {
                return;
            }

            if (this.OpenLogFileRequested is { } open)
            {
                open(entry, errorLog.Path);
            }
            else
            {
                this.OpenLogsRequested?.Invoke(entry);
            }
        }

        private void ReadLastStopAgain()
        {
            this.selectedService?.ForgetLastStop();
            this.ReadLastStopAgainCommand.RaiseCanExecuteChanged();
            this.OpenErrorLogCommand.RaiseCanExecuteChanged();
            ErrorLog.Observe(this.LoadLastStopAsync(), "last stop");
        }

        /// <summary>
        /// Checks the service watched since a start from this panel. Stopped again within the watch,
        /// the start did not hold, and the green notice that said it completed is replaced with one
        /// that says so and where to look.
        /// </summary>
        private void CheckStartWatch()
        {
            if (this.startWatch is not { } watch || this.startWatched is not { } entry)
            {
                return;
            }

            var outcome = watch.Observe(entry.Status, DateTime.UtcNow);
            if (outcome == StartWatchOutcome.Watching)
            {
                return;
            }

            this.startWatch = null;
            this.startWatched = null;
            if (outcome == StartWatchOutcome.StoppedAgain)
            {
                this.StatusMessage = Localizer.Format("M.Dash.StoppedAfterStart", entry.ServiceName);
                this.Toast?.Invoke(this.StatusMessage, true);
                ErrorLog.Observe(this.CheckWhatTheServiceSeesAsync(entry), "environment check");
            }
        }

        /// <summary>
        /// Checks the service's configuration as the service will run it — a bare name its PATH
        /// cannot find, a program installed for one user, a mapped drive, a virtual environment
        /// whose Python has gone — after a start from this panel failed or fell back to stopped
        /// straight after, and puts what it finds on the Last stop card. These all pass a try run
        /// in the signed-in session and fail at the service's first start. Off this thread: the
        /// check looks accounts up and touches paths, possibly on a share that is gone.
        /// </summary>
        private async Task CheckWhatTheServiceSeesAsync(ServiceEntry entry)
        {
            if (entry.ConfigPath is not { } configPath)
            {
                return;
            }

            int run = entry.BeginStartCheck();
            string wrapperPath = entry.WrapperPath;
            ImmutableArray<EnvironmentFinding> findings;
            try
            {
                findings = await Task.Run(() => ServiceConfigModel.Load(configPath).CheckEnvironment(wrapperPath).Findings.ToImmutableArray()).ConfigureAwait(true);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A configuration that cannot be read has nothing to check, and the card says so.
                return;
            }

            entry.EndStartCheck(run, findings);
        }

        // Scheduled restart ----------------------------------------------------

        /// <summary>Off, every day, then Monday to Sunday.</summary>
        public IReadOnlyList<RestartChoice> RestartChoices { get; }

        public RestartChoice SelectedRestartChoice
        {
            get => this.selectedRestartChoice;
            set
            {
                if (value != null && this.Set(ref this.selectedRestartChoice, value))
                {
                    this.Raise(nameof(this.RestartTimeEditable));
                }
            }
        }

        /// <summary>The time of day, as typed: HH:mm.</summary>
        public string RestartTime
        {
            get => this.restartTime;
            set => this.Set(ref this.restartTime, value);
        }

        public bool RestartTimeEditable => !this.selectedRestartChoice.IsOff;

        /// <summary>Why the picker may not be showing what is registered; empty when it is.</summary>
        public string RestartScheduleNote
        {
            get => this.restartScheduleNote;
            private set => this.Set(ref this.restartScheduleNote, value);
        }

        public AsyncRelayCommand ApplyRestartScheduleCommand { get; }

        /// <summary>
        /// Shows the selected service's restart schedule, read off this thread once the
        /// selection has settled. A reading for a service no longer selected is dropped.
        /// </summary>
        private async Task LoadRestartScheduleAsync()
        {
            var entry = this.selectedService;
            this.restartScheduleKnown = false;
            if (entry is null)
            {
                return;
            }

            await Task.Delay(ScheduleReadDelay).ConfigureAwait(true);
            if (!ReferenceEquals(entry, this.selectedService))
            {
                return;
            }

            RestartSchedule? schedule = null;
            bool known = true;
            try
            {
                schedule = await Task.Run(() => ScheduledRestart.Read(entry.ServiceName)).ConfigureAwait(true);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException or IOException or System.Xml.XmlException)
            {
                // Registered by an administrator and not readable here, or no task scheduler
                // to ask.
                known = false;
            }

            if (!ReferenceEquals(entry, this.selectedService))
            {
                return;
            }

            this.restartSchedule = schedule;
            this.restartScheduleKnown = known;
            this.RestartScheduleNote = known ? string.Empty : Localizer.Get("M.Sched.Unreadable");
            if (!known)
            {
                // Not the previous row's schedule, shown under this one's name: Off, with the
                // note beside it saying that this is not known to be the case.
                this.SelectedRestartChoice = this.RestartChoices[0];
                return;
            }

            if (schedule is { } registered)
            {
                this.SelectedRestartChoice = this.RestartChoices.First(c => !c.IsOff && c.Day == registered.Day);
                this.RestartTime = registered.At.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            }
            else
            {
                this.SelectedRestartChoice = this.RestartChoices[0];
            }
        }

        /// <summary>Registers, replaces or removes the schedule, under one elevation prompt.</summary>
        private async Task ApplyRestartScheduleAsync()
        {
            var entry = this.selectedService;
            if (entry?.ConfigPath is null)
            {
                return;
            }

            var choice = this.selectedRestartChoice;
            CommandResult result;
            string logged;

            if (choice.IsOff)
            {
                // Known to have none: nothing to remove, and no prompt to raise for it.
                if (this.restartScheduleKnown && this.restartSchedule is null)
                {
                    return;
                }

                logged = "unschedule restart";
                result = await ScheduledRestart.RemoveAsync(entry.ServiceName).ConfigureAwait(true);
            }
            else
            {
                if (!ScheduledRestart.TryParseTime(this.restartTime, out var at))
                {
                    this.StatusMessage = Localizer.Format("M.Sched.BadTime", this.restartTime);
                    this.Toast?.Invoke(this.StatusMessage, true);
                    return;
                }

                var schedule = new RestartSchedule(choice.Day, at);
                logged = "schedule restart " + (choice.Day?.ToString() ?? "daily") + " " + at.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                result = await ScheduledRestart.SetAsync(entry.ServiceName, entry.WrapperPath, entry.ConfigPath, schedule).ConfigureAwait(true);
            }

            ActionLog.Record(logged, entry.ServiceName, result);

            this.StatusMessage = result switch
            {
                { Cancelled: true } => Localizer.Get("M.Common.ElevationDeclined"),
                { Succeeded: true } => Localizer.Format(choice.IsOff ? "M.Sched.Removed" : "M.Sched.Saved", entry.ServiceName),

                // schtasks's own exit code, where there is one; otherwise the reason the
                // change was refused before it was sent, such as a path cmd would rewrite.
                { ExitCode: > 0 } => Localizer.Format("M.Sched.Failed", result.ExitCode),
                _ => result.Error ?? Localizer.Format("M.Sched.Failed", result.ExitCode),
            };
            this.Toast?.Invoke(this.StatusMessage, !result.Succeeded && !result.Cancelled);

            if (result.Succeeded)
            {
                await this.LoadRestartScheduleAsync().ConfigureAwait(true);
            }
        }

        // Lifetime ------------------------------------------------------------

        public void Activate()
        {
            this.pageShown = true;
            this.statusTimer.Start();
            this.rescanTimer.Start();
            if (this.Services.Count == 0)
            {
                this.ReloadCommand.Execute(null);
            }
            else if (!this.polling)
            {
                // Behind another page nothing but states was read, if anything was. The counters,
                // the process tree and the stray check are brought up to date now, rather than
                // showing what they were when the page was left for another tick.
                ErrorLog.Observe(this.RefreshStatusesAsync(), "service status");
            }
        }

        /// <summary>
        /// The page is no longer in front. The status poll goes on every two seconds while a
        /// stop would be told — notifications or a webhook are on — so that a crash the default
        /// ten-second recovery repairs is still seen; with neither on, it reads nothing until the
        /// page comes back. What it reads meanwhile is states alone: one query per service to
        /// the service control manager, and no process snapshot, counters, process tree or
        /// stray check. The rescan runs on its own timer as before, whatever the page, and
        /// takes a full reading when it does.
        /// </summary>
        public void Deactivate()
        {
            this.pageShown = false;

            // A page that comes back later should not come back polling at the burst rate.
            this.EndBurst();
        }

        /// <summary>
        /// A stop would be told: the tray notification is on, or a webhook is set. Checked once
        /// per tick, the notification setting first: the webhook's address is kept encrypted, and
        /// asking whether there is one decrypts it.
        /// </summary>
        private static bool WatchesForStops => AppSettings.Current.NotifyOnUnexpectedStop || AlertWebhook.IsConfigured;

        /// <summary>
        /// Polls quickly for a few seconds, so the row follows the service through its
        /// pending states as they happen rather than at the next scheduled tick.
        /// </summary>
        private void BurstPolling()
        {
            // Behind another page, or in the tray, there is no row to follow.
            if (!this.pageShown || this.inTray)
            {
                return;
            }

            this.burstUntil = DateTime.UtcNow + BurstLength;
            this.statusTimer.Interval = BurstInterval;
        }

        private void EndBurst()
        {
            this.burstUntil = default;
            this.statusTimer.Interval = PollInterval;
        }

        /// <summary>
        /// Called by the shell when the window goes to the tray, so watching continues. The poll
        /// runs from the first <see cref="Activate"/> on, so this only makes sure of it. Until
        /// <see cref="LeaveTray"/> it reads states alone, whichever page is in front, and only
        /// while a stop would be told, as behind another page; see <see cref="Deactivate"/>. A
        /// console left in the tray on this page used to take the full reading — a snapshot of
        /// every process, the counters, the tree, the ports — every two seconds for nobody.
        /// </summary>
        public void KeepWatching()
        {
            this.inTray = true;
            this.EndBurst();
            this.statusTimer.Start();
        }

        /// <summary>
        /// Called by the shell when the window comes back from the tray. With this page in front,
        /// what was not read meanwhile is read now, as when the page itself comes back; see
        /// <see cref="Activate"/>.
        /// </summary>
        public void LeaveTray()
        {
            if (!this.inTray)
            {
                return;
            }

            this.inTray = false;
            if (this.pageShown && this.Services.Count > 0 && !this.polling)
            {
                ErrorLog.Observe(this.RefreshStatusesAsync(), "service status");
            }
        }

        /// <summary>
        /// The editor has just written <paramref name="path"/>. The next rescan would see it
        /// within half a minute; the row the file belongs to says so now, while the user is
        /// still looking at the change.
        /// </summary>
        public void NoteConfigurationWritten(string path)
        {
            string full = Path.GetFullPath(path);
            var now = DateTime.Now;
            foreach (var entry in this.Services)
            {
                if (string.Equals(entry.ConfigPath, full, StringComparison.OrdinalIgnoreCase))
                {
                    entry.ConfigWrittenAt = now;
                }
            }
        }

        // Operations -----------------------------------------------------------

        public async Task ReloadAsync(bool quiet)
        {
            this.IsScanning = true;
            if (!quiet)
            {
                this.StatusMessage = Localizer.Get("M.Dash.Scanning");
            }

            try
            {
                // The registry sweep touches every installed service, so keep it off the UI thread.
                var found = await Task.Run(ServiceDiscovery.Discover).ConfigureAwait(true);

                int added = 0;
                int removed = 0;
                var byName = found.ToDictionary(e => e.ServiceName, StringComparer.OrdinalIgnoreCase);

                // Merge rather than clear-and-refill so the selection, scroll position and
                // per-row health history survive a background rescan.
                //
                // An entry whose executable or configuration file has changed is dropped here
                // rather than merged: the name is the same, but what is installed under it is
                // not, and the history recorded against the row describes the old one. It
                // comes back in the pass below as a new row, which is why the selection is
                // noted first and put back afterwards.
                string? selected = this.SelectedService?.ServiceName;

                for (int i = this.Services.Count - 1; i >= 0; i--)
                {
                    var row = this.Services[i];
                    if (byName.TryGetValue(row.ServiceName, out var fresh) && row.IsSameInstallationAs(fresh))
                    {
                        // Everything the registry sweep can see and the status poll cannot:
                        // start mode, account, description, dependencies, wrapper version.
                        row.MergeMetadataFrom(fresh);
                        continue;
                    }

                    this.announcer.Forget(row.ServiceName);
                    this.Services.RemoveAt(i);
                    removed++;
                }

                var existing = new HashSet<string>(this.Services.Select(s => s.ServiceName), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in found)
                {
                    if (existing.Contains(entry.ServiceName))
                    {
                        continue;
                    }

                    int index = 0;
                    while (index < this.Services.Count && string.Compare(this.Services[index].ServiceName, entry.ServiceName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        index++;
                    }

                    entry.Group = AppSettings.Current.ServiceGroups.TryGetValue(entry.ServiceName, out string? group) ? group : string.Empty;
                    this.Services.Insert(index, entry);
                    added++;
                }

                await this.RefreshStatusesAsync().ConfigureAwait(true);

                if (selected != null && this.SelectedService is null && byName.ContainsKey(selected))
                {
                    this.SelectByName(selected);
                }

                if (this.pendingServiceName is { } wanted)
                {
                    this.pendingServiceName = null;
                    this.SelectByName(wanted);
                }

                if (this.pendingConfigPath is { } pending)
                {
                    this.pendingConfigPath = null;
                    var match = this.Services.FirstOrDefault(s => string.Equals(s.ConfigPath, Path.GetFullPath(pending), StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        this.SelectedService = match;
                    }
                    else
                    {
                        this.OpenUninstalledConfigRequested?.Invoke(pending);
                    }
                }

                if (this.selectedService is null || !this.Services.Contains(this.selectedService))
                {
                    this.SelectedService = this.Services.FirstOrDefault();
                }

                // The rescan is what reads start types.
                this.RaiseStartTypeRestore();

                if (!quiet)
                {
                    this.StatusMessage = this.Services.Count == 0
                        ? Localizer.Get("M.Dash.NoneFound")
                        : Localizer.Format("M.Dash.Found", this.Services.Count);
                }
                else if (added > 0 || removed > 0)
                {
                    this.StatusMessage = Localizer.Format("M.Dash.Rescanned", added, removed);
                }
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Dash.ScanFailed", e.Message);
            }
            finally
            {
                this.IsScanning = false;
            }
        }

        private Task StopAsync(ServiceEntry? entry, bool force) =>
            this.RunAsync(entry, "stop", (w, c) => WinSwCli.StopAsync(w, c, force, this.TimeoutFor(c)));

        private Task RestartAsync(ServiceEntry? entry, bool force) =>
            this.RunAsync(entry, "restart", (w, c) => WinSwCli.RestartAsync(w, c, force, this.TimeoutFor(c)));

        private Task KillAsync(ServiceEntry? entry) =>
            this.RunAsync(entry, "dev kill", (w, c) => WinSwCli.KillAsync(w, c));

        /// <summary>Asks before terminating the selected service, and terminates that one whatever is selected by the answer.</summary>
        private void AskKill()
        {
            if (this.selectedService is not { } entry)
            {
                return;
            }

            this.Ask(
                Localizer.Get("M.Dash.KillTitle"),
                Localizer.Format("M.Dash.KillBody", entry.ServiceName),
                Localizer.Get("M.Dash.KillAction"),
                () => this.KillAsync(entry));
        }

        /// <summary>Asks before uninstalling the selected service, and uninstalls that one whatever is selected by the answer.</summary>
        private void AskUninstall()
        {
            if (this.selectedService is not { } entry)
            {
                return;
            }

            this.Ask(
                Localizer.Get("M.Dash.UninstallTitle"),
                Localizer.Format("M.Dash.UninstallBody", entry.ServiceName),
                Localizer.Get("M.Dash.UninstallAction"),
                () => this.RunAsync(entry, "uninstall", (w, c) => WinSwCli.UninstallAsync(w, c)));
        }

        /// <summary>
        /// Runs <paramref name="label"/> on <paramref name="entry"/>, the service the command was
        /// given for, and asks any follow-up question about that same service.
        /// </summary>
        private async Task RunAsync(ServiceEntry? entry, string label, Func<string, string, Task<CommandResult>> operation)
        {
            if (entry?.ConfigPath is null)
            {
                this.StatusMessage = Localizer.Get("M.Dash.NoConfig");
                return;
            }

            this.StatusMessage = Localizer.Format("M.Dash.Running", label, entry.ServiceName);

            // Held until the states have been read back afterwards. Until then a stop seen on any
            // of these services is this operation's doing, and a second command on one of them
            // would be racing it. Taken last before the try, so that nothing can leave it held.
            var held = this.inFlight.Begin(OperationsInFlight.NamesFor(label, entry, this.Services));
            this.BeginBusy();
            bool started = false;

            try
            {
                var result = await operation(entry.WrapperPath, entry.ConfigPath).ConfigureAwait(true);
                ActionLog.Record(label, entry.ServiceName, result);
                started = result.Succeeded && (label is "start" or "restart");

                this.StatusMessage = result switch
                {
                    { Cancelled: true } => Localizer.Get("M.Common.ElevationDeclined"),
                    { Succeeded: true } => Localizer.Format("M.Dash.Completed", label, entry.ServiceName),
                    _ => result.Error ?? Localizer.Format("M.Dash.Failed", label),
                };
                this.Toast?.Invoke(this.StatusMessage, !result.Succeeded && !result.Cancelled);

                // An uninstall removes the entry entirely; anything else only moves its state.
                if (label == "uninstall" && result.Succeeded)
                {
                    await this.ReloadAsync(quiet: false).ConfigureAwait(true);
                }
                else
                {
                    // An upgrade replaced the wrapper's own file. The version was read once at
                    // discovery, so without this the panel keeps showing the version that has
                    // just been overwritten — and keeps offering the upgrade. Every service
                    // running from that same file was upgraded by the one copy, so they are
                    // re-read too: under the install root they all share one wrapper.
                    if (label == "upgrade" && result.Succeeded)
                    {
                        foreach (var sharing in this.Services.Where(e => string.Equals(e.WrapperPath, entry.WrapperPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            ServiceDiscovery.RefreshWrapperVersion(sharing);
                        }

                        this.RaiseWrapperUpdate();
                    }

                    await this.RefreshStatusesAsync().ConfigureAwait(true);
                }

                // A start that did not take: what the service will see is checked, now that the
                // state it is in has been read back; see CheckWhatTheServiceSeesAsync. A start that
                // took and then fell over is the start watch's to catch. Only a service the start
                // left stopped, or still starting: a restart that failed in its stop half leaves the
                // service running, or stopping, as it was, never started, and what the check found
                // would wait on the entry for a stop hours away.
                if ((label is "start" or "restart") && !result.Succeeded && !result.Cancelled
                    && entry.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StartPending)
                {
                    ErrorLog.Observe(this.CheckWhatTheServiceSeesAsync(entry), "environment check");
                }

                // The two outcomes that deserve a follow-up question rather than a message. Both
                // act on the entry this operation ran on: the selection may have moved to another
                // service while it waited, and the answer is about the one the question names.
                if (result.HasDependents && (label == "stop" || label == "restart"))
                {
                    this.Ask(
                        Localizer.Get("M.Dash.DependentsTitle"),
                        Localizer.Format("M.Dash.DependentsBody", entry.ServiceName),
                        Localizer.Get("M.Dash.DependentsAction"),
                        () => label == "stop" ? this.StopAsync(entry, force: true) : this.RestartAsync(entry, force: true));
                }
                else if (result.TimedOut)
                {
                    this.Ask(
                        Localizer.Get("M.Dash.TimeoutTitle"),
                        Localizer.Format("M.Dash.TimeoutBody", label, entry.ServiceName),
                        Localizer.Get("M.Dash.TimeoutAction"),
                        () => this.KillAsync(entry));
                }
            }
            finally
            {
                held.Dispose();
                this.EndBusy();
                this.BurstPolling();

                // After the hold is let go, so that a hold from here on is another command's: see
                // OnOperationsChanged. A stop the reading just taken has already seen is seen again
                // by the next, a burst tick away.
                if (started)
                {
                    this.startWatched = entry;
                    this.startWatch = new StartWatch(entry.ServiceName, DateTime.UtcNow + BurstLength);
                }
            }
        }

        /// <summary>
        /// The wrapper waits <c>stoptimeout</c> before killing the child; give it that plus
        /// room to spare before deciding it is stuck.
        /// </summary>
        private TimeSpan TimeoutFor(string configPath)
        {
            try
            {
                var model = ServiceConfigModel.Load(configPath);
                if (!string.IsNullOrWhiteSpace(model.StopTimeout) && ServiceConfigModel.TryParseTime(model.StopTimeout!, out var stopTimeout))
                {
                    return stopTimeout + TimeSpan.FromSeconds(45);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }

            return WinSwCli.DefaultTimeout;
        }

        /// <summary>
        /// Re-reads every service's state and the selected one's process tree.
        /// </summary>
        /// <remarks>
        /// The reading happens on a worker; only the writing happens here. It used to be all
        /// one pass on the UI thread, and on a machine with a dozen wrapped services that was
        /// some fifty round trips to the service control manager, a process opened and asked
        /// for its counters per running service, and a snapshot of every process on the
        /// machine for the tree — every two seconds, on the thread that is also drawing.
        /// <para>
        /// The reading is one <see cref="StatusReading"/>: a single connection to the service
        /// control manager, one round trip per service against it, and a single snapshot of
        /// the machine's processes that answers every counter and the tree. The process
        /// opened per running service was costing a snapshot of its own each time; see the
        /// remarks there.
        /// </para>
        /// <para>
        /// What comes back is plain values. Applying them, raising the counts and touching the
        /// tree all stay here, which is what keeps ServiceEntry's bindings and the tray
        /// notification on the thread they require.
        /// </para>
        /// <para>
        /// With <paramref name="statesOnly"/>, the reading the poll takes behind another page: no
        /// snapshot, so no counters, no tree, no stray check and no ports, and what is left is one
        /// query per service to the service control manager. That is all a stop needs to be seen
        /// and told, and what is on the page catches up when it is shown again.
        /// </para>
        /// </remarks>
        private async Task RefreshStatusesAsync(bool statesOnly = false)
        {
            // Read on the UI thread: Services can be rebuilt by a rescan while this awaits,
            // and the tree is wanted for whatever was selected when the reading started.
            var entries = this.Services.ToArray();
            var selectedAtStart = this.selectedService;
            int selectedIndex = selectedAtStart is null ? -1 : Array.IndexOf(entries, selectedAtStart);

            // What the stray-process check and the ports need, read here where the rows belong: the
            // worker gets plain values. Which service is on screen, which banners name a port, what
            // each service's runs are remembered to have had and when its run began. Any wrapper on
            // the list owns what runs under it, including a desktop task's, which shares the
            // wrapper under the install root.
            var wrapperNames = new HashSet<string>(entries.Select(e => Path.GetFileName(e.WrapperPath)), StringComparer.OrdinalIgnoreCase) { "WinSW.exe" };
            var polled = entries
                .Select(e => new PolledService(
                    e.ServiceName,
                    ReferenceEquals(e, selectedAtStart),
                    e.StrayProcess is { HoldsPort: true },
                    e.RememberedProcesses,
                    e.RunStartedAt,
                    e.ExecutablePath))
                .ToArray();

            // Held across the writing as well as the reading. Dropped after the await, the
            // next tick could start a second reading while this one was still applying the
            // first — which is the pile-up the flag is here to prevent.
            this.polling = true;
            try
            {
                var (samples, tree) = await Task.Run(() =>
                {
                    using var reading = new StatusReading(withProcesses: !statesOnly);

                    var read = new ServiceSample[entries.Length];
                    for (int i = 0; i < read.Length; i++)
                    {
                        read[i] = reading.Sample(entries[i].ServiceName);
                        if (statesOnly || reading.Processes is not { } snapshot)
                        {
                            continue;
                        }

                        // Running: note what is under the wrapper. In every state: see whether
                        // anything a run of the service left is still up beside it, which state by
                        // state means something different; see StrayWatch. What is remembered goes
                        // to the file as well, for the next start of the console.
                        if (read[i].HasProcess)
                        {
                            read[i] = read[i] with { Descendants = reading.DescendantsOf(read[i].ProcessId) };
                        }

                        read[i] = StrayWatch.Look(read[i], polled[i], snapshot, wrapperNames, Environment.ProcessId, NativeMethods.ImagePathOf);
                        StrayWatch.Keep(RememberedRuns.Current, polled[i].Name, read[i].Remembered, DateTime.Now);
                    }

                    // The machine's listening ports, read once for every service and only when one
                    // of them needs them: what each running service listens on, and what holds a
                    // port a stopped one last did. See PortWatch.
                    if (reading.Processes is { } processes)
                    {
                        PortWatch.Look(read, polled, processes, PortTable.Read, RememberedRuns.Current, wrapperNames, Environment.ProcessId, DateTime.Now);
                    }

                    // Built from the reading just taken rather than from the entry, so a
                    // service that started this tick shows its tree this tick.
                    var node = !statesOnly && selectedIndex >= 0 && read[selectedIndex].ProcessId > 0
                        ? reading.Tree(read[selectedIndex].ProcessId)
                        : null;

                    return (read, node);
                }).ConfigureAwait(true);

                for (int i = 0; i < entries.Length; i++)
                {
                    if (statesOnly)
                    {
                        ServiceDiscovery.ApplyStatus(entries[i], samples[i]);
                    }
                    else
                    {
                        ServiceDiscovery.Apply(entries[i], samples[i]);
                    }
                }

                this.AnnounceUnexpectedStops(entries);
                this.CheckStartWatch();

                this.RaiseCounts();
                this.RefreshCommandStates();

                // A stop the selected service has just come to. Not behind another page, where the
                // card is not on screen: it is read when the page comes back.
                if (!statesOnly)
                {
                    ErrorLog.Observe(this.LoadLastStopAsync(), "last stop");
                }

                // The selection may have moved while the reading was in flight, in which case
                // this tree belongs to a service the panel is no longer showing. A reading of
                // states alone has no tree, and leaves the one on the page as it is.
                if (statesOnly || !ReferenceEquals(this.selectedService, selectedAtStart))
                {
                    return;
                }

                if (tree is null)
                {
                    this.ProcessTree = null;
                }
                else if (!ProcessTreeProvider.SameShape(tree, this.processTree))
                {
                    this.ProcessTree = tree;
                }
            }
            finally
            {
                this.polling = false;
            }
        }

        /// <summary>
        /// Rebuilds the process tree for the selected service, and nothing else.
        /// </summary>
        /// <remarks>
        /// The snapshot it takes covers every process on the machine, so it stays off the UI
        /// thread like the rest of the sampling. A tree that comes back for a service the
        /// selection has since moved off is dropped.
        /// </remarks>
        private async Task RefreshProcessTreeAsync()
        {
            var selected = this.selectedService;
            int processId = selected?.ProcessId ?? 0;
            if (selected is null || processId <= 0)
            {
                this.ProcessTree = null;
                return;
            }

            var tree = await Task.Run(() => ProcessTreeProvider.Build(processId)).ConfigureAwait(true);

            if (!ReferenceEquals(this.selectedService, selected))
            {
                return;
            }

            if (tree is null)
            {
                this.ProcessTree = null;
            }
            else if (!ProcessTreeProvider.SameShape(tree, this.processTree))
            {
                this.ProcessTree = tree;
            }
        }

        /// <summary>
        /// Hands every service's state to the <see cref="CrashAnnouncer"/> and raises what it
        /// decides to tell: <see cref="UnexpectedStop"/> for the first crash in a while,
        /// <see cref="StopNoticed"/> for the rest. Runs on the UI thread, which the tray icon
        /// that listens to both requires.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A stop is held back only for a service an operation from this panel is working on,
        /// where it is that operation's doing. It used to be held back for every service while
        /// anything at all was running, so a crash during a three-minute stop of another service,
        /// a diagnostics bundle or the ending of a stray process was noted and never told.
        /// </para>
        /// <para>
        /// Every reading is handed over whether notifications are on or off, so that the last
        /// state and the count are right the moment they are turned on, and so that each entry can
        /// take the reading and the count into whether Windows is about to restart it; see
        /// <see cref="ServiceEntry.NoteRecovery"/>. Only the raising depends on the setting. The
        /// state is read from the status rather than from <see cref="ServiceEntry.Health"/>, which
        /// says Broken for a service whose configuration cannot be read however it is running, and
        /// would hide its crashes — and says Pending for one Windows is about to restart.
        /// </para>
        /// </remarks>
        private void AnnounceUnexpectedStops(IReadOnlyList<ServiceEntry> entries)
        {
            bool notify = AppSettings.Current.NotifyOnUnexpectedStop;
            var now = DateTime.UtcNow;

            foreach (var entry in entries)
            {
                var notices = this.announcer.Observe(
                    entry.ServiceName,
                    entry.Status,
                    entry.LastExitCode ?? 0,
                    held: this.inFlight.Contains(entry.ServiceName),
                    now);
                entry.CrashCount = this.announcer.CountFor(entry.ServiceName);
                entry.RecentStops = this.announcer.RecentStopsFor(entry.ServiceName);

                // After the count, which tells how far into its failure actions the service is.
                entry.NoteRecovery(now);
                entry.NoteRun(now);

                if (!notify)
                {
                    continue;
                }

                foreach (var notice in notices)
                {
                    // The first crash keeps its own event, which the tray and the webhook already
                    // word: the count is 1, and they say "stopped unexpectedly".
                    if (notice.Kind == StopNoticeKind.UnexpectedStop)
                    {
                        this.UnexpectedStop?.Invoke(entry);
                    }
                    else
                    {
                        this.StopNoticed?.Invoke(notice);
                    }
                }
            }
        }

        private void RaiseWrapperUpdate()
        {
            this.Raise(nameof(this.WrapperUpdateAvailable));
            this.Raise(nameof(this.WrapperUpdateText));
            this.UpgradeWrapperCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Several rows are highlighted, and none of the services the batch would work on is
        /// being worked on already: the rule each row's own commands follow, for all of them.
        /// </summary>
        private bool CanRunOnSelected(string command) =>
            this.HasMultipleSelected
            && this.selectedEntries.Where(e => e.ConfigPath != null).All(e => this.IsIdle(e, command));

        private async Task RunOnSelectedAsync(string command)
        {
            var chosen = this.selectedEntries.Where(e => e.ConfigPath != null).ToList();
            var targets = chosen.Select(e => (e.WrapperPath, e.ConfigPath!)).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            // Normally one prompt covers the whole batch. Past what cmd will accept on one
            // command line it cannot, and a second prompt appearing unannounced looks like
            // something has gone wrong, so the count is said up front instead.
            int prompts = WinSwCli.PromptCountFor(command, targets);

            this.StatusMessage = prompts > 1
                ? Localizer.Format("M.Dash.RunningManyPrompts", command, targets.Count, prompts)
                : Localizer.Format("M.Dash.RunningMany", command, targets.Count);

            // Everything each row's own command would hold, held for the whole batch.
            var held = this.inFlight.Begin(chosen.SelectMany(e => OperationsInFlight.NamesFor(command, e, this.Services)));
            this.BeginBusy();
            try
            {
                var result = await WinSwCli.RunOnManyAsync(command, targets).ConfigureAwait(true);
                ActionLog.Record(command, string.Join(", ", chosen.Select(e => e.ServiceName)), result);
                this.StatusMessage = result.Cancelled
                    ? Localizer.Get("M.Common.ElevationDeclined")
                    : Localizer.Format("M.Dash.RanMany", command, targets.Count);
                await this.RefreshStatusesAsync().ConfigureAwait(true);
            }
            finally
            {
                held.Dispose();
                this.EndBusy();
                this.BurstPolling();
            }
        }

        private void ExportScript()
        {
            var entry = this.selectedService;
            if (entry?.ConfigPath is null)
            {
                return;
            }

            if (Dialogs.PickFolder(Localizer.Get("M.Dlg.ExportFolder")) is not { } folder)
            {
                return;
            }

            try
            {
                string script = InstallScriptExporter.Export(entry, folder);
                this.StatusMessage = Localizer.Format("M.Dash.Exported", script);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                this.StatusMessage = Localizer.Format("M.Dash.ExportFailed", e.Message);
            }
        }

        private async Task CreateDiagnosticsAsync()
        {
            var entry = this.selectedService;
            if (entry is null)
            {
                return;
            }

            string suggested = $"{entry.ServiceName}-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip";
            if (Dialogs.PickSaveFile(Localizer.Get("M.Dlg.SaveDiagnostics"), "Zip|*.zip", suggested) is not { } path)
            {
                return;
            }

            // Busy, for the progress bar, but holding no service: reading a service's files and
            // logs changes nothing about it, and its commands stay available meanwhile.
            this.BeginBusy();
            this.StatusMessage = Localizer.Get("M.Dash.Collecting");
            try
            {
                await Task.Run(() => DiagnosticsBundle.Create(entry, path)).ConfigureAwait(true);
                this.StatusMessage = Localizer.Format("M.Dash.Collected", path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Dash.CollectFailed", e.Message);
            }
            finally
            {
                this.EndBusy();
            }
        }

        /// <summary>
        /// Replaces an installed service's wrapper with the one carried inside this
        /// application: stop, swap the executable, start again if it was running.
        /// </summary>
        private Task UpgradeWrapperAsync()
        {
            var entry = this.selectedService;
            if (entry?.ConfigPath is null || BundledWrapper.Version is not { } bundled)
            {
                return Task.CompletedTask;
            }

            if (BundledWrapper.Extract() is not { } source)
            {
                this.StatusMessage = Localizer.Get("M.Wiz.UnpackFailed");
                return Task.CompletedTask;
            }

            var warnings = new List<string>();

            // Swapping a 2.x wrapper for a 3.x one leaves a 2.x configuration behind it, and
            // 3.x renamed or dropped a dozen elements. The service would install fine and
            // then fail to start, which is the worst way to find out.
            if (MajorVersionOf(entry.WrapperVersion) is { } installedMajor
                && MajorVersionOf(bundled) is { } bundledMajor
                && bundledMajor > installedMajor)
            {
                warnings.Add(Localizer.Format("M.Dash.UpgradeMajor", installedMajor, bundledMajor));
            }

            // Under the install root one wrapper serves every service, and a running process
            // locks its own image: the whole group is stopped, the file replaced once, and
            // the ones that were running are started again.
            var group = this.Services
                .Where(e => e.ConfigPath != null && string.Equals(e.WrapperPath, entry.WrapperPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (group.Count > 1)
            {
                warnings.Add(Localizer.Format(
                    "M.Dash.UpgradeShared",
                    group.Count,
                    string.Join(", ", group.Where(e => !ReferenceEquals(e, entry)).Select(e => e.ServiceName))));
            }

            // The bundled wrapper is the .NET Framework build, which needs 4.6.2 or later since
            // the wrapper moved to net462. Said as what this machine has when that is too old,
            // which would leave the service unable to start; see UpgradeFramework.
            if (UpgradeFramework.Note(WrapperKind.ReleaseAssetFor(entry.WrapperPath), NetFramework.Installed, Localizer.Format) is { } frameworkNote)
            {
                warnings.Add(frameworkNote);
            }

            string body = Localizer.Format("M.Dash.UpgradeBody", entry.ServiceName, entry.WrapperVersion, bundled);
            if (warnings.Count > 0)
            {
                body += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, warnings);
            }

            var plan = group
                .Select(e => (ConfigPath: e.ConfigPath!, WasRunning: e.Status == ServiceControllerStatus.Running))
                .ToList();

            this.Ask(
                Localizer.Get("M.Dash.UpgradeTitle"),
                body,
                Localizer.Get("M.Dash.UpgradeAction"),
                () => this.RunAsync(entry, "upgrade", async (wrapper, _) =>
                {
                    var result = await WinSwCli.UpgradeWrapperAsync(wrapper, source, plan).ConfigureAwait(true);
                    if (result.Cancelled)
                    {
                        return result;
                    }

                    // The copy's own exit code is lost behind the restarts that follow it, so
                    // the file is asked directly whether it was replaced.
                    ServiceDiscovery.RefreshWrapperVersion(entry);
                    return string.Equals(entry.WrapperVersion, bundled, StringComparison.OrdinalIgnoreCase)
                        ? CommandResult.Ok()
                        : CommandResult.Failed(Localizer.Format("M.Dash.UpgradeNotReplaced", entry.WrapperVersion));
                }));

            return Task.CompletedTask;
        }

        /// <summary>The leading number of a file version such as "2.9.0.0", or null.</summary>
        private static int? MajorVersionOf(string? version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return null;
            }

            int dot = version.IndexOf('.');
            string head = dot > 0 ? version[..dot] : version;
            return int.TryParse(head, out int major) ? major : null;
        }

        private void RaiseCounts()
        {
            this.Raise(nameof(this.IsEmpty));
            this.Raise(nameof(this.TotalCount));
            this.Raise(nameof(this.RunningCount));
            this.Raise(nameof(this.StoppedCount));
            this.Raise(nameof(this.ProblemCount));
        }

        private void RefreshCommandStates()
        {
            this.StartCommand.RaiseCanExecuteChanged();
            this.StopCommand.RaiseCanExecuteChanged();
            this.RestartCommand.RaiseCanExecuteChanged();
            this.RefreshConfigCommand.RaiseCanExecuteChanged();
            this.KillCommand.RaiseCanExecuteChanged();
            this.UninstallCommand.RaiseCanExecuteChanged();
            this.EditConfigCommand.RaiseCanExecuteChanged();
            this.ViewLogsCommand.RaiseCanExecuteChanged();
            this.OpenFolderCommand.RaiseCanExecuteChanged();
            this.OpenWorkingDirectoryCommand.RaiseCanExecuteChanged();
            this.ApplyRestartScheduleCommand.RaiseCanExecuteChanged();
            this.TerminateStrayCommand.RaiseCanExecuteChanged();
            this.EndStrayParentCommand.RaiseCanExecuteChanged();
            this.StopRestartingCommand.RaiseCanExecuteChanged();
            this.RestoreStartTypeCommand.RaiseCanExecuteChanged();
            this.OpenErrorLogCommand.RaiseCanExecuteChanged();
            this.ReadLastStopAgainCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// An operation has begun or ended. Besides the selected row's commands, which every poll
        /// re-asks anyway, the upgrade and the batch commands: nothing else in a poll changes
        /// their answer, so they are left out of it.
        /// </summary>
        private void OnOperationsChanged()
        {
            // A command now working on the service watched since its start: a stop from here on
            // is that command's doing, or at least not the start's.
            if (this.startWatch is { } watch && this.inFlight.Contains(watch.ServiceName))
            {
                this.startWatch = null;
                this.startWatched = null;
            }

            this.RefreshCommandStates();
            this.UpgradeWrapperCommand.RaiseCanExecuteChanged();
            this.StartSelectedCommand.RaiseCanExecuteChanged();
            this.StopSelectedCommand.RaiseCanExecuteChanged();
            this.RestartSelectedCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// <paramref name="entry"/> is there, and nothing <paramref name="command"/> would work on
        /// is being worked on already: the service itself, and for a stop, a restart or an upgrade
        /// the other services that command acts on too, so that two commands from this panel do
        /// not work on one service at once. A null command asks about the service alone.
        /// </summary>
        private bool IsIdle([NotNullWhen(true)] ServiceEntry? entry, string? command) =>
            entry != null
            && (command is null
                ? !this.inFlight.Contains(entry.ServiceName)
                : !this.inFlight.ContainsAny(OperationsInFlight.NamesFor(command, entry, this.Services)));

        private void BeginBusy()
        {
            if (this.busyCount++ == 0)
            {
                this.Raise(nameof(this.IsBusy));
            }
        }

        private void EndBusy()
        {
            if (--this.busyCount == 0)
            {
                this.Raise(nameof(this.IsBusy));
            }
        }

        private void ApplySort()
        {
            // Only worth having while status is part of the ordering. Left on regardless, the
            // view watches SortRank on every row and re-evaluates the placement of each one
            // whose status changes — which, with a two-second poll over every service on the
            // machine, is work done to arrive back where it started.
            if (this.ServicesView is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = this.sortByStatus;
            }

            using (this.ServicesView.DeferRefresh())
            {
                this.ServicesView.GroupDescriptions.Clear();
                this.ServicesView.SortDescriptions.Clear();

                // Grouped, the groups come first in the ordering, so each is one run of rows.
                if (this.groupServices)
                {
                    this.ServicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServiceEntry.Group), new GroupHeadingConverter()));
                    this.ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceEntry.GroupSortKey), ListSortDirection.Ascending));
                }

                if (this.sortByStatus)
                {
                    this.ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceEntry.SortRank), ListSortDirection.Ascending));
                }

                this.ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceEntry.ServiceName), ListSortDirection.Ascending));
            }
        }

        /// <summary>
        /// A stat card was clicked. The list is filtered then, not as the rows change under it, so
        /// that a row being worked on does not vanish from under the selection the moment it stops
        /// needing attention; clicking the card already chosen filters again, for who is in it now.
        /// </summary>
        private void ChooseFilter(string filter)
        {
            if (string.Equals(filter, this.healthFilter, StringComparison.Ordinal))
            {
                this.ServicesView.Refresh();
            }
            else
            {
                this.HealthFilter = filter;
            }
        }

        private bool MatchesSearch(object item)
        {
            if (item is not ServiceEntry entry)
            {
                return false;
            }

            bool healthOk = this.healthFilter switch
            {
                "running" => entry.Health == ServiceHealth.Running,
                "stopped" => entry.Health == ServiceHealth.Stopped,
                "problem" => entry.NeedsAttention,
                _ => true,
            };

            if (!healthOk)
            {
                return false;
            }

            string needle = this.searchNeedle;
            if (needle.Length == 0)
            {
                return true;
            }

            return Contains(entry.ServiceName) || Contains(entry.DisplayName) || Contains(entry.ConfigPath) || Contains(entry.Group);

            bool Contains(string? haystack) =>
                haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
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
            string? target = this.selectedService?.ConfigPath ?? this.selectedService?.WrapperPath;
            if (target is null || !File.Exists(target))
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

        /// <summary>
        /// Opens the directory the service's own process runs in, which is not always the one
        /// holding the configuration: <c>&lt;workingdirectory&gt;</c> overrides it, and that is
        /// where the program's own files — its data, its own logs — are to be found.
        /// </summary>
        private void OpenWorkingDirectory()
        {
            string? configPath = this.selectedService?.ConfigPath;
            if (configPath is null || !File.Exists(configPath))
            {
                this.StatusMessage = Localizer.Get("M.Dash.NothingToReveal");
                return;
            }

            string directory;
            try
            {
                directory = ConfigPaths.ResolveWorkingDirectory(ServiceConfigModel.Load(configPath), configPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                this.StatusMessage = Localizer.Format("M.Dash.ConfigUnreadable", e.Message);
                return;
            }

            if (!Directory.Exists(directory))
            {
                this.StatusMessage = Localizer.Format("M.Warn.WorkingDirectoryMissing", directory);
                return;
            }

            try
            {
                // The path itself rather than an explorer.exe command line: a directory ending
                // in a backslash would put one in front of the closing quote and take it with it.
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Common.ExplorerFailed", e.Message);
            }
        }
    }
}
