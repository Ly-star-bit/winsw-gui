using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
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

        private readonly DispatcherTimer timer;
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

            this.ReloadCommand = new AsyncRelayCommand(this.ReloadAsync);
            this.StartCommand = new AsyncRelayCommand(() => this.RunAsync("start", (w, c) => WinSwCli.StartAsync(w, c)), () => this.selectedService?.CanStart == true);
            this.StopCommand = new AsyncRelayCommand(() => this.RunAsync("stop", (w, c) => WinSwCli.StopAsync(w, c, force: true)), () => this.selectedService?.CanStop == true);
            this.RestartCommand = new AsyncRelayCommand(() => this.RunAsync("restart", (w, c) => WinSwCli.RestartAsync(w, c, force: true)), () => this.selectedService != null);
            this.RefreshConfigCommand = new AsyncRelayCommand(() => this.RunAsync("refresh", (w, c) => WinSwCli.RefreshAsync(w, c)), () => this.selectedService != null);

            this.KillCommand = new RelayCommand(
                () => this.Ask(
                    "Terminate the service process?",
                    $"'{this.selectedService?.ServiceName}' and every process it started will be killed without a graceful shutdown. Use this only when the service has stopped responding.",
                    "Terminate",
                    () => this.RunAsync("dev kill", (w, c) => WinSwCli.KillAsync(w, c))),
                () => this.selectedService != null);

            this.UninstallCommand = new RelayCommand(
                () => this.Ask(
                    "Uninstall this service?",
                    $"'{this.selectedService?.ServiceName}' will be removed from the service control manager. Its configuration file and logs are left on disk.",
                    "Uninstall",
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

            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += (_, _) => this.RefreshStatuses();
        }

        /// <summary>Raised when the user asks to edit the selected service's configuration.</summary>
        public event Action<ServiceEntry>? OpenConfigRequested;

        /// <summary>Raised when the user asks to tail the selected service's logs.</summary>
        public event Action<ServiceEntry>? OpenLogsRequested;

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

        /// <summary>The process tree of the selected service, refreshed with its status.</summary>
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
            this.timer.Start();
            if (this.Services.Count == 0)
            {
                this.ReloadCommand.Execute(null);
            }
        }

        public void Deactivate() => this.timer.Stop();

        // Operations -----------------------------------------------------------

        public async Task ReloadAsync()
        {
            this.IsScanning = true;
            this.StatusMessage = "Scanning installed services…";

            try
            {
                string? previous = this.selectedService?.ServiceName;

                // The registry sweep touches every installed service, so keep it off the UI thread.
                var found = await Task.Run(ServiceDiscovery.Discover).ConfigureAwait(true);

                this.Services.Clear();
                foreach (var entry in found)
                {
                    this.Services.Add(entry);
                }

                this.RefreshStatuses();
                this.RaiseCounts();

                this.SelectedService = previous is null
                    ? this.Services.FirstOrDefault()
                    : this.Services.FirstOrDefault(s => s.ServiceName == previous) ?? this.Services.FirstOrDefault();

                this.StatusMessage = this.Services.Count == 0
                    ? "No WinSW-managed services are installed on this machine."
                    : $"Found {this.Services.Count} WinSW-managed service{(this.Services.Count == 1 ? string.Empty : "s")}.";
            }
            catch (Exception e)
            {
                this.StatusMessage = $"Scan failed: {e.Message}";
            }
            finally
            {
                this.IsScanning = false;
            }
        }

        private async Task RunAsync(string label, Func<string, string, Task<CommandResult>> operation)
        {
            var entry = this.selectedService;
            if (entry?.ConfigPath is null)
            {
                this.StatusMessage = "This service has no usable configuration file, so it cannot be controlled from here.";
                return;
            }

            this.IsBusy = true;
            this.StatusMessage = $"Running 'winsw {label}' for '{entry.ServiceName}'…";

            try
            {
                var result = await operation(entry.WrapperPath, entry.ConfigPath).ConfigureAwait(true);

                this.StatusMessage = result switch
                {
                    { Cancelled: true } => "Elevation was declined, so nothing was changed.",
                    { Succeeded: true } => $"'winsw {label}' completed for '{entry.ServiceName}'.",
                    _ => result.Error ?? $"'winsw {label}' failed.",
                };

                // An uninstall removes the entry entirely; anything else only moves its state.
                if (label == "uninstall" && result.Succeeded)
                {
                    await this.ReloadAsync().ConfigureAwait(true);
                }
                else
                {
                    this.RefreshStatuses();
                }
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        private void RefreshStatuses()
        {
            foreach (var entry in this.Services)
            {
                ServiceDiscovery.RefreshStatus(entry);
            }

            this.RaiseCounts();
            this.RefreshCommandStates();

            var selected = this.selectedService;
            if (selected is null || selected.ProcessId <= 0)
            {
                this.ProcessTree = null;
                return;
            }

            // Rebuilt from a fresh snapshot each tick, so the tree stays honest about
            // children the service has since spawned or lost.
            this.ProcessTree = ProcessTreeProvider.Build(selected.ProcessId);
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
                this.StatusMessage = "There is nothing to reveal: the file no longer exists.";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                this.StatusMessage = $"Could not open Explorer: {e.Message}";
            }
        }
    }
}
