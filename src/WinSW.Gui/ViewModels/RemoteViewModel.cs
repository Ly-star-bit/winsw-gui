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
    /// Read-only status of services on another machine. Deliberately no control: changing
    /// a remote service means running the wrapper there, which is a WinRM/PsExec job with a
    /// different trust model than a local UAC prompt.
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

        public RemoteViewModel()
        {
            this.ServicesView = CollectionViewSource.GetDefaultView(this.Services);
            this.ServicesView.Filter = this.MatchesFilter;
            this.RefreshCommand = new AsyncRelayCommand(this.RefreshAsync, () => !string.IsNullOrWhiteSpace(this.machine) && !this.isBusy);
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
                }
            }
        }

        /// <summary>Running services among the rows the filter lets through.</summary>
        public int RunningCount => this.ServicesView.Cast<RemoteServiceStatus>().Count(s => s.IsRunning);

        public void Activate() => this.timer.Start();

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
