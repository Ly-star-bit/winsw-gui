using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        private readonly Dictionary<string, ServiceHealth> lastHealth = new(StringComparer.OrdinalIgnoreCase);

        private ServiceEntry? selectedService;
        private string searchText = string.Empty;
        private string statusMessage = string.Empty;
        private bool isBusy;
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
        private readonly Dictionary<string, (DateTime At, int Count)> notified = new(StringComparer.OrdinalIgnoreCase);

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

            this.ReloadCommand = new AsyncRelayCommand(() => this.ReloadAsync(quiet: false));
            this.StartCommand = new AsyncRelayCommand(() => this.RunAsync("start", (w, c) => WinSwCli.StartAsync(w, c)), () => this.selectedService?.CanStart == true);
            this.StopCommand = new AsyncRelayCommand(() => this.StopAsync(force: false), () => this.selectedService?.CanStop == true);
            this.RestartCommand = new AsyncRelayCommand(() => this.RestartAsync(force: false), () => this.selectedService != null);
            this.RefreshConfigCommand = new AsyncRelayCommand(() => this.RunAsync("refresh", (w, c) => WinSwCli.RefreshAsync(w, c)), () => this.selectedService != null);

            this.KillCommand = new RelayCommand(
                () => this.Ask(
                    Localizer.Get("M.Dash.KillTitle"),
                    Localizer.Format("M.Dash.KillBody", this.selectedService?.ServiceName),
                    Localizer.Get("M.Dash.KillAction"),
                    this.KillAsync),
                () => this.selectedService != null);

            this.UninstallCommand = new RelayCommand(
                () => this.Ask(
                    Localizer.Get("M.Dash.UninstallTitle"),
                    Localizer.Format("M.Dash.UninstallBody", this.selectedService?.ServiceName),
                    Localizer.Get("M.Dash.UninstallAction"),
                    () => this.RunAsync("uninstall", (w, c) => WinSwCli.UninstallAsync(w, c))),
                () => this.selectedService != null);

            this.EditConfigCommand = new RelayCommand(
                () => this.OpenConfigRequested?.Invoke(this.selectedService!),
                () => this.selectedService?.ConfigPath != null);

            this.ViewLogsCommand = new RelayCommand(
                () => this.OpenLogsRequested?.Invoke(this.selectedService!),
                () => this.selectedService?.ConfigPath != null);

            this.OpenFolderCommand = new RelayCommand(this.OpenContainingFolder, () => this.selectedService != null);

            // Unlike Reveal, this one has to read the configuration to know where to go.
            this.OpenWorkingDirectoryCommand = new RelayCommand(this.OpenWorkingDirectory, () => this.selectedService?.ConfigPath != null);

            this.ConfirmCommand = new AsyncRelayCommand(this.ExecuteConfirmedAsync);
            this.CancelConfirmCommand = new RelayCommand(() => this.ConfirmVisible = false);

            this.StartSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("start"), () => this.HasMultipleSelected);
            this.StopSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("stop"), () => this.HasMultipleSelected);
            this.RestartSelectedCommand = new AsyncRelayCommand(() => this.RunOnSelectedAsync("restart"), () => this.HasMultipleSelected);

            this.SetFilterCommand = new RelayCommand(p => this.HealthFilter = p as string ?? "all");
            this.CreateServiceCommand = new RelayCommand(() => this.CreateServiceRequested?.Invoke());
            this.OpenConfigFileCommand = new RelayCommand(() => this.OpenConfigFileRequested?.Invoke());

            this.ExportScriptCommand = new RelayCommand(this.ExportScript, () => this.selectedService?.ConfigPath != null);
            this.DiagnosticsCommand = new AsyncRelayCommand(this.CreateDiagnosticsAsync, () => this.selectedService != null);
            this.UpgradeWrapperCommand = new AsyncRelayCommand(this.UpgradeWrapperAsync, () => this.WrapperUpdateAvailable);

            this.statusTimer = new DispatcherTimer { Interval = PollInterval };
            this.statusTimer.Tick += async (_, _) =>
            {
                // A reading that outlasts the interval must not have another started on top of
                // it. The explicit refreshes elsewhere are deliberately not gated: they are
                // what makes the panel answer at once after an operation, and overlapping is
                // harmless because every write happens on this thread.
                if (!this.polling)
                {
                    await this.RefreshStatusesAsync().ConfigureAwait(true);
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
                if (seconds > 0 && !this.isScanning && !this.isBusy)
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
            };
        }

        /// <summary>Raised when the user asks to edit the selected service's configuration.</summary>
        public event Action<ServiceEntry>? OpenConfigRequested;

        /// <summary>Raised when the user asks to tail the selected service's logs.</summary>
        public event Action<ServiceEntry>? OpenLogsRequested;

        /// <summary>Raised when a service goes from running to stopped without this GUI asking it to.</summary>
        public event Action<ServiceEntry>? UnexpectedStop;

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
                    _ = this.RefreshProcessTreeAsync();
                    this.RaiseWrapperUpdate();
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
                    this.ServicesView.Refresh();
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
            set => this.Set(ref this.isBusy, value);
        }

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

        public int ProblemCount => this.Services.Count(s => s.Health == ServiceHealth.Broken);

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

        // Lifetime ------------------------------------------------------------

        public void Activate()
        {
            this.statusTimer.Start();
            this.rescanTimer.Start();
            if (this.Services.Count == 0)
            {
                this.ReloadCommand.Execute(null);
            }
        }

        /// <summary>
        /// Only the status poll pauses when the page is hidden; the slow rescan and the
        /// crash detection it feeds keep running so notifications still arrive.
        /// </summary>
        public void Deactivate()
        {
            this.statusTimer.Stop();

            // A page that comes back later should not come back polling at the burst rate.
            this.EndBurst();
        }

        /// <summary>
        /// Polls quickly for a few seconds, so the row follows the service through its
        /// pending states as they happen rather than at the next scheduled tick.
        /// </summary>
        private void BurstPolling()
        {
            this.burstUntil = DateTime.UtcNow + BurstLength;
            this.statusTimer.Interval = BurstInterval;
        }

        private void EndBurst()
        {
            this.burstUntil = default;
            this.statusTimer.Interval = PollInterval;
        }

        /// <summary>Called by the shell when the window is in the tray, so watching continues.</summary>
        public void KeepWatching() => this.statusTimer.Start();

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

                    this.lastHealth.Remove(row.ServiceName);
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

        private Task StopAsync(bool force) =>
            this.RunAsync("stop", (w, c) => WinSwCli.StopAsync(w, c, force, this.TimeoutFor(c)));

        private Task RestartAsync(bool force) =>
            this.RunAsync("restart", (w, c) => WinSwCli.RestartAsync(w, c, force, this.TimeoutFor(c)));

        private Task KillAsync() =>
            this.RunAsync("dev kill", (w, c) => WinSwCli.KillAsync(w, c));

        private async Task RunAsync(string label, Func<string, string, Task<CommandResult>> operation)
        {
            var entry = this.selectedService;
            if (entry?.ConfigPath is null)
            {
                this.StatusMessage = Localizer.Get("M.Dash.NoConfig");
                return;
            }

            this.IsBusy = true;
            this.StatusMessage = Localizer.Format("M.Dash.Running", label, entry.ServiceName);

            try
            {
                var result = await operation(entry.WrapperPath, entry.ConfigPath).ConfigureAwait(true);

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

                // The two outcomes that deserve a follow-up question rather than a message.
                if (result.HasDependents && (label == "stop" || label == "restart"))
                {
                    this.Ask(
                        Localizer.Get("M.Dash.DependentsTitle"),
                        Localizer.Format("M.Dash.DependentsBody", entry.ServiceName),
                        Localizer.Get("M.Dash.DependentsAction"),
                        () => label == "stop" ? this.StopAsync(force: true) : this.RestartAsync(force: true));
                }
                else if (result.TimedOut)
                {
                    this.Ask(
                        Localizer.Get("M.Dash.TimeoutTitle"),
                        Localizer.Format("M.Dash.TimeoutBody", label, entry.ServiceName),
                        Localizer.Get("M.Dash.TimeoutAction"),
                        this.KillAsync);
                }
            }
            finally
            {
                this.IsBusy = false;
                this.BurstPolling();
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
        /// </remarks>
        private async Task RefreshStatusesAsync()
        {
            // Read on the UI thread: Services can be rebuilt by a rescan while this awaits,
            // and the tree is wanted for whatever was selected when the reading started.
            var entries = this.Services.ToArray();
            var selectedAtStart = this.selectedService;
            int selectedIndex = selectedAtStart is null ? -1 : Array.IndexOf(entries, selectedAtStart);

            // Held across the writing as well as the reading. Dropped after the await, the
            // next tick could start a second reading while this one was still applying the
            // first — which is the pile-up the flag is here to prevent.
            this.polling = true;
            try
            {
                var (samples, tree) = await Task.Run(() =>
                {
                    using var reading = new StatusReading();

                    var read = new ServiceSample[entries.Length];
                    for (int i = 0; i < read.Length; i++)
                    {
                        read[i] = reading.Sample(entries[i].ServiceName);
                    }

                    // Built from the reading just taken rather than from the entry, so a
                    // service that started this tick shows its tree this tick.
                    var node = selectedIndex >= 0 && read[selectedIndex].ProcessId > 0
                        ? reading.Tree(read[selectedIndex].ProcessId)
                        : null;

                    return (read, node);
                }).ConfigureAwait(true);

                for (int i = 0; i < entries.Length; i++)
                {
                    ServiceDiscovery.Apply(entries[i], samples[i]);
                }

                this.AnnounceUnexpectedStops(entries);

                this.RaiseCounts();
                this.RefreshCommandStates();

                // The selection may have moved while the reading was in flight, in which case
                // this tree belongs to a service the panel is no longer showing.
                if (!ReferenceEquals(this.selectedService, selectedAtStart))
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
        /// Raises <see cref="UnexpectedStop"/> for anything that stopped without being asked.
        /// Runs on the UI thread, which the tray icon that listens to it requires.
        /// </summary>
        private void AnnounceUnexpectedStops(IReadOnlyList<ServiceEntry> entries)
        {
            foreach (var entry in entries)
            {
                var health = entry.Health;
                if (this.lastHealth.TryGetValue(entry.ServiceName, out var previous)
                    && previous == ServiceHealth.Running
                    && health == ServiceHealth.Stopped
                    && !this.isBusy
                    && AppSettings.Current.NotifyOnUnexpectedStop)
                {
                    // A crash-looping service would otherwise raise a balloon every poll.
                    // One notice per five minutes, carrying how many times it has happened.
                    var now = DateTime.UtcNow;
                    this.notified.TryGetValue(entry.ServiceName, out var record);
                    int count = now - record.At < TimeSpan.FromMinutes(5) ? record.Count + 1 : 1;
                    bool announce = count == 1 || now - record.At >= TimeSpan.FromMinutes(5);
                    this.notified[entry.ServiceName] = (announce ? now : record.At, count);
                    entry.CrashCount = count;

                    if (announce)
                    {
                        this.UnexpectedStop?.Invoke(entry);
                    }
                }

                this.lastHealth[entry.ServiceName] = health;
            }
        }

        private void RaiseWrapperUpdate()
        {
            this.Raise(nameof(this.WrapperUpdateAvailable));
            this.Raise(nameof(this.WrapperUpdateText));
            this.UpgradeWrapperCommand.RaiseCanExecuteChanged();
        }

        private async Task RunOnSelectedAsync(string command)
        {
            var targets = this.selectedEntries.Where(e => e.ConfigPath != null).Select(e => (e.WrapperPath, e.ConfigPath!)).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            // Normally one prompt covers the whole batch. Past what cmd will accept on one
            // command line it cannot, and a second prompt appearing unannounced looks like
            // something has gone wrong, so the count is said up front instead.
            int prompts = WinSwCli.PromptCountFor(command, targets);

            this.IsBusy = true;
            this.StatusMessage = prompts > 1
                ? Localizer.Format("M.Dash.RunningManyPrompts", command, targets.Count, prompts)
                : Localizer.Format("M.Dash.RunningMany", command, targets.Count);
            try
            {
                var result = await WinSwCli.RunOnManyAsync(command, targets).ConfigureAwait(true);
                this.StatusMessage = result.Cancelled
                    ? Localizer.Get("M.Common.ElevationDeclined")
                    : Localizer.Format("M.Dash.RanMany", command, targets.Count);
                await this.RefreshStatusesAsync().ConfigureAwait(true);
            }
            finally
            {
                this.IsBusy = false;
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

            this.IsBusy = true;
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
                this.IsBusy = false;
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

            // The bundled wrapper is the .NET Framework build. A service currently hosted by
            // a self-contained one would gain that dependency. The name compared against is
            // upstream's, which is how ReleaseAssetFor reports a framework build.
            //
            // .NET Framework 4.6.2 or later, since the wrapper moved to net462.
            if (WrapperKind.ReleaseAssetFor(entry.WrapperPath) is { } asset && asset != "WinSW-net461.exe")
            {
                warnings.Add(Localizer.Get("M.Dash.UpgradeFrameworkBuild"));
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
                () => this.RunAsync("upgrade", async (wrapper, _) =>
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
                this.ServicesView.SortDescriptions.Clear();
                if (this.sortByStatus)
                {
                    this.ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceEntry.SortRank), ListSortDirection.Ascending));
                }

                this.ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceEntry.ServiceName), ListSortDirection.Ascending));
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
                "problem" => entry.Health == ServiceHealth.Broken,
                _ => true,
            };

            if (!healthOk)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(this.searchText))
            {
                return true;
            }

            string needle = this.searchText.Trim();
            return Contains(entry.ServiceName) || Contains(entry.DisplayName) || Contains(entry.ConfigPath);

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
