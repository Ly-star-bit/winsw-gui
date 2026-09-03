using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;
using WinSW.Gui.Theme;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One entry in the navigation rail.</summary>
    public sealed class NavigationItem : ObservableObject
    {
        private readonly string titleKey;
        private readonly string subtitleKey;

        public NavigationItem(string glyph, string titleKey, string subtitleKey, object page)
        {
            this.Glyph = glyph;
            this.titleKey = titleKey;
            this.subtitleKey = subtitleKey;
            this.Page = page;
        }

        /// <summary>A Segoe Fluent / MDL2 glyph.</summary>
        public string Glyph { get; }

        public string Title => Localizer.Get(this.titleKey);

        public string Subtitle => Localizer.Get(this.subtitleKey);

        public object Page { get; }

        public void RefreshLocalized()
        {
            this.Raise(nameof(this.Title));
            this.Raise(nameof(this.Subtitle));
        }
    }

    /// <summary>A theme option, labelled for the picker.</summary>
    public sealed class ThemeOption : ObservableObject
    {
        private readonly string key;

        public ThemeOption(ThemeChoice choice, string key)
        {
            this.Choice = choice;
            this.key = key;
        }

        public ThemeChoice Choice { get; }

        public string Label => Localizer.Get(this.key);

        public void RefreshLocalized() => this.Raise(nameof(this.Label));
    }

    /// <summary>
    /// Owns the four pages and moves between them. Pages hand off to each other through
    /// events so no page needs a reference to another.
    /// </summary>
    public sealed class ShellViewModel : ObservableObject
    {
        private NavigationItem? selectedItem;
        private object? currentPage;
        private Language selectedLanguage = Localizer.Current;
        private ThemeOption selectedTheme;
        private ReleaseInfo? guiUpdate;
        private bool contextMenuRegistered = ShellIntegration.IsRegistered;
        private bool isRailCollapsed = AppSettings.Current.RailCollapsed;
        private string toastText = string.Empty;
        private bool toastVisible;
        private bool toastIsError;
        private readonly System.Windows.Threading.DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

        public ShellViewModel()
        {
            this.Dashboard = new DashboardViewModel();
            this.Editor = new ConfigEditorViewModel();
            this.Logs = new LogViewerViewModel();
            this.Wizard = new WizardViewModel { Sources = this.Dashboard.Services };
            this.Remote = new RemoteViewModel();
            this.Logs.Services = this.Dashboard.Services;

            // The settings page binds to this shell itself; its DataTemplate maps ShellViewModel → SettingsView.
            this.Items = new ObservableCollection<NavigationItem>
            {
                new("", "M.Nav.Services", "M.Nav.ServicesSub", this.Dashboard),
                new("", "M.Nav.Config", "M.Nav.ConfigSub", this.Editor),
                new("", "M.Nav.Logs", "M.Nav.LogsSub", this.Logs),
                new("", "M.Nav.New", "M.Nav.NewSub", this.Wizard),
                new("", "M.Nav.Remote", "M.Nav.RemoteSub", this.Remote),
                new("", "M.Nav.Settings", "M.Nav.SettingsSub", this),
            };

            this.ToggleRailCommand = new RelayCommand(() => this.IsRailCollapsed = !this.IsRailCollapsed);
            this.toastTimer.Tick += (_, _) =>
            {
                this.toastTimer.Stop();
                this.ToastVisible = false;
            };

            this.Dashboard.Toast += this.ShowToast;
            this.Editor.Toast += this.ShowToast;

            // A configuration installed from the editor is a service the dashboard has not
            // heard of yet.
            this.Editor.ServiceInstalled += _ => this.Dashboard.ReloadCommand.Execute(null);
            this.Dashboard.CreateServiceRequested += () => this.Navigate(this.Wizard);
            this.Dashboard.OpenConfigFileRequested += () =>
            {
                this.Editor.Open();
                this.Navigate(this.Editor);
            };

            this.Themes = new[]
            {
                new ThemeOption(ThemeChoice.System, "M.Theme.System"),
                new ThemeOption(ThemeChoice.Light, "M.Theme.Light"),
                new ThemeOption(ThemeChoice.Dark, "M.Theme.Dark"),
            };
            this.selectedTheme = this.Themes.First(t => t.Choice == ThemeManager.Current);

            this.RestartElevatedCommand = new RelayCommand(() => Elevation.RestartElevated(), () => !this.IsElevated);
            this.BrowseInstallRootCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Settings.InstallRoot"), AppSettings.Current.EffectiveInstallRoot) is { } path)
                {
                    this.InstallRoot = path;
                }
            });
            this.OpenGuiUpdateCommand = new RelayCommand(() =>
            {
                if (this.guiUpdate != null)
                {
                    SystemShell.OpenUrl(this.guiUpdate.Url);
                }
            });

            this.Dashboard.OpenUninstalledConfigRequested += path =>
            {
                this.Editor.Load(path);
                this.Navigate(this.Editor);
            };

            _ = this.CheckGuiUpdateAsync();

            this.Dashboard.OpenConfigRequested += entry =>
            {
                this.Editor.LoadFrom(entry);
                this.Navigate(this.Editor);
            };

            this.Dashboard.OpenLogsRequested += entry =>
            {
                this.Logs.Attach(entry);
                this.Navigate(this.Logs);
            };

            this.Wizard.Completed += serviceId =>
            {
                this.Dashboard.SelectServiceWhenReady(serviceId);
                this.Navigate(this.Dashboard);
                this.Dashboard.ReloadCommand.Execute(null);
                this.ShowToast(Localizer.Format("M.Wiz.Installed", serviceId), false);
            };

            this.SelectedItem = this.Items[0];

            Localizer.Changed += () =>
            {
                foreach (var item in this.Items)
                {
                    item.RefreshLocalized();
                }

                foreach (var theme in this.Themes)
                {
                    theme.RefreshLocalized();
                }

                this.Raise(nameof(this.ElevationLabel));
                this.Raise(nameof(this.ElevationHint));
                this.Raise(nameof(this.GuiUpdateText));
            };
        }

        public DashboardViewModel Dashboard { get; }

        public ConfigEditorViewModel Editor { get; }

        public LogViewerViewModel Logs { get; }

        public WizardViewModel Wizard { get; }

        public RemoteViewModel Remote { get; }

        public RelayCommand OpenGuiUpdateCommand { get; }

        public RelayCommand ToggleRailCommand { get; }

        /// <summary>Icon-only rail; remembered across sessions.</summary>
        public bool IsRailCollapsed
        {
            get => this.isRailCollapsed;
            set
            {
                if (this.Set(ref this.isRailCollapsed, value))
                {
                    AppSettings.Current.RailCollapsed = value;
                    AppSettings.Current.Save();
                }
            }
        }

        // Toast ------------------------------------------------------------------

        public string ToastText
        {
            get => this.toastText;
            private set => this.Set(ref this.toastText, value);
        }

        public bool ToastVisible
        {
            get => this.toastVisible;
            private set => this.Set(ref this.toastVisible, value);
        }

        public bool ToastIsError
        {
            get => this.toastIsError;
            private set => this.Set(ref this.toastIsError, value);
        }

        /// <summary>A short notice near the top of the content; replaces the previous one.</summary>
        public void ShowToast(string text, bool isError)
        {
            this.ToastText = text;
            this.ToastIsError = isError;
            this.ToastVisible = true;
            this.toastTimer.Stop();
            this.toastTimer.Start();
        }

        /// <summary>Brings a service into view, e.g. from a tray notification.</summary>
        public void ShowService(string serviceName)
        {
            this.Navigate(this.Dashboard);
            this.Dashboard.SelectByName(serviceName);
        }

        public string GuiVersion => "v" + UpdateChecker.CurrentGuiVersion;

        /// <summary>A newer GUI release, when one exists and the network allowed asking.</summary>
        public ReleaseInfo? GuiUpdate
        {
            get => this.guiUpdate;
            private set
            {
                if (this.Set(ref this.guiUpdate, value))
                {
                    this.Raise(nameof(this.HasGuiUpdate));
                    this.Raise(nameof(this.GuiUpdateText));
                }
            }
        }

        public bool HasGuiUpdate => this.guiUpdate != null;

        public string GuiUpdateText => this.guiUpdate is null ? string.Empty : Localizer.Format("M.Shell.GuiUpdate", this.guiUpdate.Version);

        /// <summary>"Open in WinSW" on the right-click menu of .xml files, for this user.</summary>
        public bool ContextMenuRegistered
        {
            get => this.contextMenuRegistered;
            set
            {
                if (this.contextMenuRegistered == value)
                {
                    return;
                }

                try
                {
                    if (value)
                    {
                        ShellIntegration.Register(Localizer.Get("M.Shell.OpenInWinSW"));
                    }
                    else
                    {
                        ShellIntegration.Unregister();
                    }

                    this.contextMenuRegistered = value;
                }
                catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
                {
                    // HKCU is normally writable; if not, the checkbox simply snaps back.
                }

                this.Raise();
            }
        }

        /// <summary>Handles a configuration path passed on the command line or from the shell verb.</summary>
        public void OpenStartupPath(string path)
        {
            this.Dashboard.OpenConfigPathWhenReady(path);
            this.Navigate(this.Dashboard);
        }

        private async Task CheckGuiUpdateAsync()
        {
            var latest = await UpdateChecker.LatestGuiAsync().ConfigureAwait(true);
            if (latest != null && UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentGuiVersion))
            {
                this.GuiUpdate = latest;
            }
        }

        public ObservableCollection<NavigationItem> Items { get; }

        public RelayCommand RestartElevatedCommand { get; }

        /// <summary>
        /// Shown in the rail so the user knows what to expect: elevated sessions get no
        /// UAC prompts, standard ones get one per change.
        /// </summary>
        public bool IsElevated => Elevation.IsElevated;

        public string ElevationLabel => Localizer.Get(this.IsElevated ? "M.Shell.Admin" : "M.Shell.Standard");

        public string ElevationHint => Localizer.Get(this.IsElevated ? "M.Shell.AdminHint" : "M.Shell.StandardHint");

        public Language[] Languages => Localizer.Languages;

        /// <summary>Changing this re-renders the UI in place and remembers the choice.</summary>
        public Language SelectedLanguage
        {
            get => this.selectedLanguage;
            set
            {
                if (value != null && this.Set(ref this.selectedLanguage, value))
                {
                    Localizer.Apply(value);
                }
            }
        }

        public ThemeOption[] Themes { get; }

        public ThemeOption SelectedTheme
        {
            get => this.selectedTheme;
            set
            {
                if (value != null && this.Set(ref this.selectedTheme, value))
                {
                    ThemeManager.Apply(value.Choice);
                }
            }
        }

        public bool MinimizeToTray
        {
            get => AppSettings.Current.MinimizeToTray;
            set
            {
                if (AppSettings.Current.MinimizeToTray != value)
                {
                    AppSettings.Current.MinimizeToTray = value;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        /// <summary>
        /// Where the wizard installs services: one folder per service, sharing a wrapper in
        /// <c>bin</c>. Blank falls back to <see cref="DefaultInstallRoot"/>.
        /// </summary>
        public string InstallRoot
        {
            get => AppSettings.Current.InstallRoot ?? string.Empty;
            set
            {
                string trimmed = value.Trim();
                if (!string.Equals(AppSettings.Current.InstallRoot ?? string.Empty, trimmed, StringComparison.Ordinal))
                {
                    AppSettings.Current.InstallRoot = trimmed.Length == 0 ? null : trimmed;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        public string DefaultInstallRoot => AppSettings.Current.EffectiveInstallRoot;

        public RelayCommand BrowseInstallRootCommand { get; }

        public bool NotifyOnUnexpectedStop
        {
            get => AppSettings.Current.NotifyOnUnexpectedStop;
            set
            {
                if (AppSettings.Current.NotifyOnUnexpectedStop != value)
                {
                    AppSettings.Current.NotifyOnUnexpectedStop = value;
                    AppSettings.Current.Save();
                    this.Raise();
                }
            }
        }

        public NavigationItem? SelectedItem
        {
            get => this.selectedItem;
            set
            {
                if (this.Set(ref this.selectedItem, value) && value != null)
                {
                    this.CurrentPage = value.Page;
                }
            }
        }

        public object? CurrentPage
        {
            get => this.currentPage;
            private set
            {
                var previous = this.currentPage;
                if (!this.Set(ref this.currentPage, value))
                {
                    return;
                }

                // Only the visible page polls; the timers of the others stay idle.
                switch (previous)
                {
                    case DashboardViewModel dashboard:
                        dashboard.Deactivate();
                        break;
                    case LogViewerViewModel logs:
                        logs.Deactivate();
                        break;
                    case RemoteViewModel remote:
                        remote.Deactivate();
                        break;
                }

                switch (value)
                {
                    case DashboardViewModel dashboard:
                        dashboard.Activate();
                        break;
                    case LogViewerViewModel logs:
                        logs.Activate();
                        break;
                    case RemoteViewModel remote:
                        remote.Activate();
                        break;
                }
            }
        }

        public void Navigate(object page)
        {
            foreach (var item in this.Items)
            {
                if (ReferenceEquals(item.Page, page))
                {
                    this.SelectedItem = item;
                    return;
                }
            }
        }
    }
}
