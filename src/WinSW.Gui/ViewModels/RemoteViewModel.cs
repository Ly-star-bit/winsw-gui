using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>
    /// Services on another machine: their status, and starting, stopping and restarting them
    /// through its service control manager with the current user's rights there. Nothing that
    /// needs the wrapper on that machine — installing, uninstalling, applying a configuration —
    /// is offered: that is a WinRM or PsExec job, with a different trust model.
    /// </summary>
    public sealed class RemoteViewModel : ObservableObject
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private readonly DispatcherTimer timer;
        private string machine = string.Empty;
        private string filter = string.Empty;

        /// <summary>
        /// <see cref="Filter"/> trimmed once, when it changes. The filter runs over the list
        /// already fetched, so typing never waits on the network.
        /// </summary>
        private string filterNeedle = string.Empty;
        private string statusMessage = string.Empty;

        /// <summary>The machine <see cref="Services"/> was read from; a different one starts over.</summary>
        private string? loadedMachine;

        /// <summary>Whether the last query failed, so its message stays up instead of the counts.</summary>
        private bool lastRefreshFailed;
        private bool isBusy;
        private bool autoRefresh = true;
        private RemoteServiceStatus? selectedService;

        public RemoteViewModel()
        {
            this.ServicesView = CollectionViewSource.GetDefaultView(this.Services);
            this.ServicesView.Filter = this.MatchesFilter;
            this.RefreshCommand = new AsyncRelayCommand(this.RefreshAsync, () => !string.IsNullOrWhiteSpace(this.machine) && !this.isBusy);
            this.StartCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Start), () => !this.isBusy && this.selectedService?.CanStart == true);
            this.StopCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Stop), () => !this.isBusy && this.selectedService?.CanStop == true);
            this.RestartCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Restart), () => !this.isBusy && this.selectedService?.Status != null);
            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += async (_, _) =>
            {
                if (this.autoRefresh && this.Services.Count > 0 && this.RefreshCommand.CanExecute(null))
                {
                    await this.RefreshAsync().ConfigureAwait(true);
                }
            };

            this.statusMessage = Localizer.Get("M.Remote.Hint");
            Localizer.Changed += () =>
            {
                if (this.Services.Count == 0)
                {
                    this.StatusMessage = Localizer.Get("M.Remote.Hint");
                }
                else if (!this.lastRefreshFailed)
                {
                    this.ShowCounts();
                }
            };
        }

        public ObservableCollection<RemoteServiceStatus> Services { get; } = new();

        public ICollectionView ServicesView { get; }

        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand StartCommand { get; }

        public AsyncRelayCommand StopCommand { get; }

        public AsyncRelayCommand RestartCommand { get; }

        public RemoteServiceStatus? SelectedService
        {
            get => this.selectedService;
            set
            {
                if (this.Set(ref this.selectedService, value))
                {
                    this.RefreshControlCommands();
                }
            }
        }

        /// <summary>Computer name or address; the current user's credentials are used.</summary>
        public string Machine
        {
            get => this.machine;
            set
            {
                if (this.Set(ref this.machine, value))
                {
                    this.RefreshCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string Filter
        {
            get => this.filter;
            set
            {
                if (this.Set(ref this.filter, value))
                {
                    this.filterNeedle = (value ?? string.Empty).Trim();
                    this.ServicesView.Refresh();
                    if (this.loadedMachine != null && !this.lastRefreshFailed)
                    {
                        this.ShowCounts();
                    }
                }
            }
        }

        public bool AutoRefresh
        {
            get => this.autoRefresh;
            set => this.Set(ref this.autoRefresh, value);
        }

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        public bool IsBusy
        {
            get => this.isBusy;
            private set
            {
                if (this.Set(ref this.isBusy, value))
                {
                    this.RefreshCommand.RaiseCanExecuteChanged();
                    this.RefreshControlCommands();
                }
            }
        }

        /// <summary>Running services among the rows the filter lets through.</summary>
        public int RunningCount => this.ServicesView.Cast<RemoteServiceStatus>().Count(s => s.IsRunning);

        public void Activate() => this.timer.Start();

        private void RefreshControlCommands()
        {
            this.StartCommand.RaiseCanExecuteChanged();
            this.StopCommand.RaiseCanExecuteChanged();
            this.RestartCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Starts, stops or restarts the selected service on the machine the list was read
        /// from, then reads the list again so the row shows what happened.
        /// </summary>
        private async Task ControlAsync(RemoteAction action)
        {
            var target = this.selectedService;
            string? on = this.loadedMachine;
            if (target is null || on is null)
            {
                return;
            }

            string verb = action.ToString().ToLowerInvariant();
            this.IsBusy = true;
            this.StatusMessage = Localizer.Format("M.Remote.Controlling", verb, target.ServiceName, on);

            string message;
            string outcome;
            try
            {
                await Task.Run(() => RemoteMonitor.Control(on, target.ServiceName, action)).ConfigureAwait(true);
                message = Localizer.Format("M.Remote.Controlled", verb, target.ServiceName, on);
                outcome = "ok";
            }
            catch (InvalidOperationException e)
            {
                message = Localizer.Format("M.Remote.ControlFailed", verb, target.ServiceName, e.Message);
                outcome = "failed: " + e.Message;
            }
            finally
            {
                this.IsBusy = false;
            }

            ActionLog.Record("remote " + verb, on + "\\" + target.ServiceName, outcome);

            // The row shows the state it settled in; the line under the list says what was done.
            // Only while the box still names that machine: a refresh reads whatever it names.
            if (string.Equals(this.machine.Trim(), on, StringComparison.OrdinalIgnoreCase))
            {
                await this.RefreshAsync().ConfigureAwait(true);
            }

            this.StatusMessage = message;
        }

        public void Deactivate() => this.timer.Stop();

        private async Task RefreshAsync()
        {
            string target = this.machine.Trim();
            this.IsBusy = true;

            try
            {
                var list = await Task.Run(() => RemoteMonitor.List(target)).ConfigureAwait(true);

                if (string.Equals(target, this.loadedMachine, StringComparison.OrdinalIgnoreCase))
                {
                    this.Merge(list);
                }
                else
                {
                    // No DeferRefresh here: the view still forwards each Add while deferred,
                    // and the ListBox reading it back then throws.
                    this.Services.Clear();
                    foreach (var item in list)
                    {
                        this.Services.Add(item);
                    }

                    this.loadedMachine = target;
                }

                this.lastRefreshFailed = false;
                this.ShowCounts();
            }
            catch (InvalidOperationException e)
            {
                this.lastRefreshFailed = true;
                this.StatusMessage = Localizer.Format("M.Remote.Failed", target, e.Message);
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        /// <summary>
        /// Counts what the filter shows, not what the machine has; with a filter set, the total
        /// is given alongside so a short list is not mistaken for a machine with few services.
        /// </summary>
        private void ShowCounts()
        {
            int shown = this.ServicesView.Cast<object>().Count();
            this.Raise(nameof(this.RunningCount));
            this.StatusMessage = this.filterNeedle.Length == 0
                ? Localizer.Format("M.Remote.Loaded", shown, this.loadedMachine, this.RunningCount)
                : Localizer.Format("M.Remote.LoadedFiltered", shown, this.loadedMachine, this.RunningCount, this.Services.Count);
        }

        /// <summary>
        /// Brings <see cref="Services"/> in line with a fresh, sorted reading of the same machine
        /// by updating, moving, inserting and removing rows one at a time. Clearing and refilling
        /// would reset the list and throw the reader back to the top every poll.
        /// </summary>
        private void Merge(IReadOnlyList<RemoteServiceStatus> fresh)
        {
            var freshNames = new HashSet<string>(fresh.Select(s => s.ServiceName), StringComparer.OrdinalIgnoreCase);
            for (int i = this.Services.Count - 1; i >= 0; i--)
            {
                if (!freshNames.Contains(this.Services[i].ServiceName))
                {
                    this.Services.RemoveAt(i);
                }
            }

            var existing = this.Services.ToDictionary(s => s.ServiceName, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fresh.Count; i++)
            {
                var item = fresh[i];
                if (!existing.TryGetValue(item.ServiceName, out var row))
                {
                    this.Services.Insert(i, item);
                    continue;
                }

                row.CopyFrom(item);

                // Almost always already in place; only a renamed display name moves a row.
                if (!ReferenceEquals(this.Services[i], row))
                {
                    this.Services.Move(this.Services.IndexOf(row), i);
                }
            }
        }

        private bool MatchesFilter(object item)
        {
            string needle = this.filterNeedle;
            if (needle.Length == 0 || item is not RemoteServiceStatus status)
            {
                return true;
            }

            return status.ServiceName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || status.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }
}
