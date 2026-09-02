using System;
using System.Collections.ObjectModel;
using System.Linq;
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

        public ShellViewModel()
        {
            this.Dashboard = new DashboardViewModel();
            this.Editor = new ConfigEditorViewModel();
            this.Logs = new LogViewerViewModel();
            this.Wizard = new WizardViewModel();

            this.Items = new ObservableCollection<NavigationItem>
            {
                new("", "M.Nav.Services", "M.Nav.ServicesSub", this.Dashboard),
                new("", "M.Nav.Config", "M.Nav.ConfigSub", this.Editor),
                new("", "M.Nav.Logs", "M.Nav.LogsSub", this.Logs),
                new("", "M.Nav.New", "M.Nav.NewSub", this.Wizard),
            };

            this.Themes = new[]
            {
                new ThemeOption(ThemeChoice.System, "M.Theme.System"),
                new ThemeOption(ThemeChoice.Light, "M.Theme.Light"),
                new ThemeOption(ThemeChoice.Dark, "M.Theme.Dark"),
            };
            this.selectedTheme = this.Themes.First(t => t.Choice == ThemeManager.Current);

            this.RestartElevatedCommand = new RelayCommand(() => Elevation.RestartElevated(), () => !this.IsElevated);

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

            this.Wizard.Completed += () =>
            {
                this.Navigate(this.Dashboard);
                this.Dashboard.ReloadCommand.Execute(null);
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
            };
        }

        public DashboardViewModel Dashboard { get; }

        public ConfigEditorViewModel Editor { get; }

        public LogViewerViewModel Logs { get; }

        public WizardViewModel Wizard { get; }

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
                }

                switch (value)
                {
                    case DashboardViewModel dashboard:
                        dashboard.Activate();
                        break;
                    case LogViewerViewModel logs:
                        logs.Activate();
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
