using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        private bool wrappersOnly;

        /// <summary>
        /// Set while <see cref="RecentMachines"/> is being reordered, when whatever the machine
        /// box writes back is the drop-down's doing, not the user's; see <see cref="Remember"/>.
        /// </summary>
        private bool rememberingMachine;
        private RemoteServiceStatus? selectedService;

        public RemoteViewModel()
        {
            foreach (string name in MachineHistory.Load(AppSettings.Current.RemoteMachines))
            {
                this.RecentMachines.Add(name);
            }

            // The last machine is filled in, not connected to: a page opened for a look at the
            // list should not first wait on the network.
            this.machine = this.RecentMachines.FirstOrDefault() ?? string.Empty;
            this.wrappersOnly = AppSettings.Current.RemoteWrappersOnly;

            this.ServicesView = CollectionViewSource.GetDefaultView(this.Services);
            this.ServicesView.Filter = this.MatchesFilter;
            this.RefreshCommand = new AsyncRelayCommand(() => this.RefreshAsync(reclassify: true), () => !string.IsNullOrWhiteSpace(this.machine) && !this.isBusy);
            this.StartCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Start), () => !this.isBusy && this.selectedService?.CanStart == true);
            this.StopCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Stop), () => !this.isBusy && this.selectedService?.CanStop == true);
            this.RestartCommand = new AsyncRelayCommand(() => this.ControlAsync(RemoteAction.Restart), () => !this.isBusy && this.selectedService?.Status != null);
            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += async (_, _) =>
            {
                // A refresh reads whatever the box names. Another name there is one being typed,
                // or one that failed to answer, and waits for Connect: a poll would otherwise
                // switch the list to a half-typed name, or retry a failed one unasked.
                if (this.autoRefresh && this.Services.Count > 0 && this.BoxNamesLoadedMachine && this.RefreshCommand.CanExecute(null))
                {
                    await this.RefreshAsync(reclassify: false).ConfigureAwait(true);
                }
            };

            this.statusMessage = Localizer.Get("M.Remote.Hint");
            Localizer.Changed += () =>
            {
                foreach (var row in this.Services)
                {
                    row.RefreshLocalized();
                }

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

        /// <summary>
        /// Computers connected to before, most recent first, for the machine box's drop-down.
        /// Names only; the connection is always made with the current user's own credentials.
        /// </summary>
        public ObservableCollection<string> RecentMachines { get; } = new();

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
                if (this.rememberingMachine)
                {
                    return;
                }

                if (this.Set(ref this.machine, value ?? string.Empty))
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

        /// <summary>
        /// List only the services a WinSW wrapper hosts. A service that could not be told either
        /// way stays in the list: hiding it would pass off a machine that refused the question as
        /// one with nothing of ours on it.
        /// </summary>
        public bool WrappersOnly
        {
            get => this.wrappersOnly;
            set
            {
                if (this.Set(ref this.wrappersOnly, value))
                {
                    this.ServicesView.Refresh();
                    if (this.loadedMachine != null && !this.lastRefreshFailed)
                    {
                        this.ShowCounts();
                    }

                    AppSettings.Current.RemoteWrappersOnly = value;
                    AppSettings.Current.Save();

                    // Rows listed while the switch was off were never asked, which costs round
                    // trips nobody needed then; they are asked now, as Connect would. Only for
                    // the machine on screen: another name in the box waits for Connect, which
                    // asks anyway.
                    if (value
                        && this.BoxNamesLoadedMachine
                        && this.Services.Any(s => s.IsWrapper == null)
                        && this.RefreshCommand.CanExecute(null))
                    {
                        this.RefreshCommand.Execute(null);
                    }
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

        /// <summary>
        /// Whether the machine box still names the machine the list was read from. Everything
        /// the page does on its own — a poll, the reading after a start or stop, asking which
        /// services are WinSW's — waits for Connect while it names another.
        /// </summary>
        private bool BoxNamesLoadedMachine =>
            this.loadedMachine != null && string.Equals(this.machine.Trim(), this.loadedMachine, StringComparison.OrdinalIgnoreCase);

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

            string verb = Localizer.Get(RemoteMonitor.VerbKey(action));
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

            // The log is English whatever the interface language, like every line in it.
            ActionLog.Record("remote " + action.ToString().ToLowerInvariant(), on + "\\" + target.ServiceName, outcome);

            // The row shows the state it settled in; the line under the list says what was done.
            // Only while the box still names that machine: a refresh reads whatever it names.
            if (this.BoxNamesLoadedMachine)
            {
                await this.RefreshAsync(reclassify: false).ConfigureAwait(true);
            }

            this.StatusMessage = message;
        }

        public void Deactivate() => this.timer.Stop();

        /// <param name="reclassify">
        /// Ask every service again whether a wrapper hosts it: true for Connect, including on the
        /// machine already listed, so a refusal the first time is not kept until another machine
        /// is chosen. A poll asks only about services not on screen yet. Nothing is asked while
        /// <see cref="WrappersOnly"/> is off, when nothing uses the answer.
        /// </param>
        private async Task RefreshAsync(bool reclassify)
        {
            string target = this.machine.Trim();
            this.IsBusy = true;

            // Copies: the worker reads them.
            bool classify = this.wrappersOnly;
            bool sameMachine = string.Equals(target, this.loadedMachine, StringComparison.OrdinalIgnoreCase);
            var classified = classify && sameMachine && !reclassify
                ? this.Services.Select(s => s.ServiceName).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                : ImmutableHashSet<string>.Empty;

            try
            {
                var list = await Task.Run(() => RemoteMonitor.List(target, classify, classified)).ConfigureAwait(true);

                if (string.Equals(target, this.loadedMachine, StringComparison.OrdinalIgnoreCase))
                {
                    this.Merge(list);

                    // The answers may have changed on rows already shown, which the view does
                    // not re-filter on its own.
                    if (reclassify && classify)
                    {
                        this.ServicesView.Refresh();
                    }
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
                    this.Remember(target);
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
        /// Puts a machine that answered at the top of <see cref="RecentMachines"/>, and keeps the
        /// list for next time. Only after a connect that worked: a mistyped name is not worth
        /// remembering.
        /// </summary>
        private void Remember(string name)
        {
            // An editable ComboBox can clear its text when its selected item moves or goes, and
            // the two-way binding would carry that into Machine: Connect disabled, and the refresh
            // after a start or stop skipped because the box no longer names the machine. The
            // write is ignored while the list changes, and Machine put back on screen after.
            bool changed;
            this.rememberingMachine = true;
            try
            {
                changed = MachineHistory.Remember(this.RecentMachines, name);
            }
            finally
            {
                this.rememberingMachine = false;
            }

            this.Raise(nameof(this.Machine));

            if (changed)
            {
                AppSettings.Current.RemoteMachines = this.RecentMachines.ToList();
                AppSettings.Current.Save();
            }
        }

        /// <summary>
        /// Counts what the filter shows, not what the machine has; with a filter set or only
        /// WinSW services shown, the total is given alongside so a short list is not mistaken
        /// for a machine with few services.
        /// </summary>
        private void ShowCounts()
        {
            int shown = this.ServicesView.Cast<object>().Count();
            this.Raise(nameof(this.RunningCount));
            this.StatusMessage = this.filterNeedle.Length == 0 && !this.wrappersOnly
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
            if (item is not RemoteServiceStatus status)
            {
                return true;
            }

            if (this.wrappersOnly && status.IsWrapper == false)
            {
                return false;
            }

            string needle = this.filterNeedle;
            return needle.Length == 0
                || status.ServiceName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || status.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The Remote page's list of machines it has connected to: most recent first, each once,
    /// and only the last few.
    /// </summary>
    internal static class MachineHistory
    {
        /// <summary>How many machines are kept; a drop-down, not an inventory.</summary>
        internal const int Limit = 8;

        /// <summary>
        /// The saved list made fit to show: blanks and repeats dropped, names trimmed, cut to
        /// <see cref="Limit"/>. The file is the user's to edit, and may hold anything.
        /// </summary>
        internal static List<string> Load(IEnumerable<string?>? saved)
        {
            var names = new List<string>();
            foreach (string? entry in saved ?? Enumerable.Empty<string?>())
            {
                string name = entry?.Trim() ?? string.Empty;
                if (name.Length > 0 && IndexOf(names, name) < 0 && names.Count < Limit)
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>
        /// Moves <paramref name="machine"/> to the top of <paramref name="recent"/>, adding it
        /// when new and dropping the oldest past <see cref="Limit"/>. Names are compared the way
        /// Windows compares them, ignoring case; the spelling last connected with is kept. The
        /// list is changed in place, a move rather than a remove and insert, so a drop-down
        /// showing it keeps its selection where it can.
        /// </summary>
        /// <returns>False when nothing changed, so there is nothing to save.</returns>
        internal static bool Remember(ObservableCollection<string> recent, string machine)
        {
            string name = machine.Trim();
            if (name.Length == 0)
            {
                return false;
            }

            int index = IndexOf(recent, name);
            if (index == 0 && string.Equals(recent[0], name, StringComparison.Ordinal))
            {
                return false;
            }

            if (index < 0)
            {
                recent.Insert(0, name);
            }
            else
            {
                if (index > 0)
                {
                    recent.Move(index, 0);
                }

                if (!string.Equals(recent[0], name, StringComparison.Ordinal))
                {
                    recent[0] = name;
                }
            }

            while (recent.Count > Limit)
            {
                recent.RemoveAt(recent.Count - 1);
            }

            return true;
        }

        private static int IndexOf(IList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
