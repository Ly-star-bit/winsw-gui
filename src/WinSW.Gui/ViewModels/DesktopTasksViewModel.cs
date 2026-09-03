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

        private readonly DispatcherTimer statusTimer;

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

        public void Activate()
        {
            if (!this.IsAvailable)
            {
                return;
            }

            _ = this.ReloadAsync(quiet: this.Tasks.Count > 0);
            this.statusTimer.Start();
        }

        public void Deactivate() => this.statusTimer.Stop();

        /// <summary>
        /// Re-reads the whole folder. There is no cheaper "just the state" call: the task
        /// scheduler hands back a registered task, not a status word, so a full read is the
        /// only read there is.
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
                var found = await Task.Run(DesktopTasks.List).ConfigureAwait(true);
                this.Merge(found);

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

        private Task StartAsync() => this.RunAsync("start", entry => DesktopTasks.Start(entry.Name));

        private Task StopAsync() => this.RunAsync("stop", entry => DesktopTasks.Stop(entry.Name, entry.Name, this.GraceFor(entry)));

        private Task RestartAsync() => this.RunAsync("restart", entry =>
        {
            if (entry.State == DesktopTaskState.Running)
            {
                DesktopTasks.Stop(entry.Name, entry.Name, this.GraceFor(entry));
            }

            DesktopTasks.Start(entry.Name);
        });

        private Task ToggleEnabledAsync() => this.RunAsync(
            this.selectedTask?.Enabled == true ? "disable" : "enable",
            entry => DesktopTasks.SetEnabled(entry.Name, !entry.Enabled));

        private Task DeleteAsync() => this.RunAsync("delete", entry =>
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
        private async Task RunAsync(string label, Action<DesktopTaskEntry> operation)
        {
            var entry = this.selectedTask;
            if (entry is null)
            {
                return;
            }

            this.IsBusy = true;
            this.StatusMessage = Localizer.Format("M.Task.Running", label, entry.Name);

            try
            {
                await Task.Run(() => operation(entry)).ConfigureAwait(true);
                this.StatusMessage = Localizer.Format("M.Task.Completed", label, entry.Name);
                this.Toast?.Invoke(this.StatusMessage, false);
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Task.Failed", label, e.Message);
                this.Toast?.Invoke(this.StatusMessage, true);
            }
            finally
            {
                this.IsBusy = false;
                await this.ReloadAsync(quiet: true).ConfigureAwait(true);
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
