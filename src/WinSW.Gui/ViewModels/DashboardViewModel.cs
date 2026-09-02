using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        private readonly DispatcherTimer statusTimer;
        private readonly DispatcherTimer rescanTimer;
        private readonly Dictionary<string, ServiceHealth> lastHealth = new(StringComparer.OrdinalIgnoreCase);

        private ServiceEntry? selectedService;
        private string searchText = string.Empty;
        private string statusMessage = string.Empty;
        private bool isBusy;
        private bool isScanning;
        private ProcessNode? processTree;
        private bool confirmVisible;
        private string confirmTitle = string.Empty;
        private string confirmMessage = string.Empty;
        private string confirmActionLabel = "Confirm";
        private Func<Task>? pendingAction;

        public DashboardViewModel()
        {
            this.ServicesView = CollectionViewSource.GetDefaultView(this.Services);
            this.ServicesView.Filter = this.MatchesSearch;

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

            this.ConfirmCommand = new AsyncRelayCommand(this.ExecuteConfirmedAsync);
            this.CancelConfirmCommand = new RelayCommand(() => this.ConfirmVisible = false);

            this.statusTimer = new DispatcherTimer { Interval = PollInterval };
            this.statusTimer.Tick += (_, _) => this.RefreshStatuses();

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
            };
        }

        /// <summary>Raised when the user asks to edit the selected service's configuration.</summary>
        public event Action<ServiceEntry>? OpenConfigRequested;

        /// <summary>Raised when the user asks to tail the selected service's logs.</summary>
        public event Action<ServiceEntry>? OpenLogsRequested;

        /// <summary>Raised when a service goes from running to stopped without this GUI asking it to.</summary>
        public event Action<ServiceEntry>? UnexpectedStop;

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

        public AsyncRelayCommand ConfirmCommand { get; }

        public RelayCommand CancelConfirmCommand { get; }

        public ServiceEntry? SelectedService
        {
            get => this.selectedService;
            set
            {
                if (this.Set(ref this.selectedService, value))
                {
                    this.ProcessTree = null;
                    this.RefreshCommandStates();
                    this.RefreshStatuses();
                }
            }
        }

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
            set => this.Set(ref this.isScanning, value);
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
        public void Deactivate() => this.statusTimer.Stop();

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
                for (int i = this.Services.Count - 1; i >= 0; i--)
                {
                    if (!byName.ContainsKey(this.Services[i].ServiceName))
                    {
                        this.lastHealth.Remove(this.Services[i].ServiceName);
                        this.Services.RemoveAt(i);
                        removed++;
                    }
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

                this.RefreshStatuses();

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

                // An uninstall removes the entry entirely; anything else only moves its state.
                if (label == "uninstall" && result.Succeeded)
                {
                    await this.ReloadAsync(quiet: false).ConfigureAwait(true);
                }
                else
                {
                    this.RefreshStatuses();
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

        private void RefreshStatuses()
        {
            foreach (var entry in this.Services)
            {
                ServiceDiscovery.RefreshStatus(entry);

                var health = entry.Health;
                if (this.lastHealth.TryGetValue(entry.ServiceName, out var previous)
                    && previous == ServiceHealth.Running
                    && health == ServiceHealth.Stopped
                    && !this.isBusy
                    && AppSettings.Current.NotifyOnUnexpectedStop)
                {
                    this.UnexpectedStop?.Invoke(entry);
                }

                this.lastHealth[entry.ServiceName] = health;
            }

            this.RaiseCounts();
            this.RefreshCommandStates();

            var selected = this.selectedService;
            if (selected is null || selected.ProcessId <= 0)
            {
                this.ProcessTree = null;
                return;
            }

            var fresh = ProcessTreeProvider.Build(selected.ProcessId);
            if (!ProcessTreeProvider.SameShape(fresh, this.processTree))
            {
                this.ProcessTree = fresh;
            }
        }

        private void RaiseCounts()
        {
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
        }

        private bool MatchesSearch(object item)
        {
            if (string.IsNullOrWhiteSpace(this.searchText))
            {
                return true;
            }

            if (item is not ServiceEntry entry)
            {
                return false;
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
    }
}
