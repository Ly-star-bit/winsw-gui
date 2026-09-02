using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
        private string statusMessage = string.Empty;
        private bool isBusy;
        private bool autoRefresh = true;

        public RemoteViewModel()
        {
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
            };
        }

        public ObservableCollection<RemoteServiceStatus> Services { get; } = new();

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
            set => this.Set(ref this.filter, value);
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

        public int RunningCount => this.Services.Count(s => s.IsRunning);

        public void Activate() => this.timer.Start();

        public void Deactivate() => this.timer.Stop();

        private async Task RefreshAsync()
        {
            string target = this.machine.Trim();
            string filterText = this.filter.Trim();
            this.IsBusy = true;

            try
            {
                var list = await Task.Run(() => RemoteMonitor.List(target, filterText)).ConfigureAwait(true);

                this.Services.Clear();
                foreach (var item in list)
                {
                    this.Services.Add(item);
                }

                this.Raise(nameof(this.RunningCount));
                this.StatusMessage = Localizer.Format("M.Remote.Loaded", list.Count, target, this.RunningCount);
            }
            catch (InvalidOperationException e)
            {
                this.StatusMessage = Localizer.Format("M.Remote.Failed", target, e.Message);
            }
            finally
            {
                this.IsBusy = false;
            }
        }
    }
}
