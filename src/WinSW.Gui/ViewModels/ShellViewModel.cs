using System.Collections.ObjectModel;
using System.Security.Principal;
using WinSW.Gui.Mvvm;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One entry in the navigation rail.</summary>
    public sealed class NavigationItem
    {
        public NavigationItem(string glyph, string title, string subtitle, object page)
        {
            this.Glyph = glyph;
            this.Title = title;
            this.Subtitle = subtitle;
            this.Page = page;
        }

        /// <summary>A Segoe Fluent / MDL2 glyph.</summary>
        public string Glyph { get; }

        public string Title { get; }

        public string Subtitle { get; }

        public object Page { get; }
    }

    /// <summary>
    /// Owns the four pages and moves between them. Pages hand off to each other through
    /// events so no page needs a reference to another.
    /// </summary>
    public sealed class ShellViewModel : ObservableObject
    {
        private NavigationItem? selectedItem;
        private object? currentPage;

        public ShellViewModel()
        {
            this.Dashboard = new DashboardViewModel();
            this.Editor = new ConfigEditorViewModel();
            this.Logs = new LogViewerViewModel();
            this.Wizard = new WizardViewModel();

            this.Items = new ObservableCollection<NavigationItem>
            {
                new("", "Services", "Status and control", this.Dashboard),
                new("", "Configuration", "Edit service XML", this.Editor),
                new("", "Logs", "Live output", this.Logs),
                new("", "New service", "Guided install", this.Wizard),
            };

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

            this.IsElevated = DetectElevation();
            this.SelectedItem = this.Items[0];
        }

        public DashboardViewModel Dashboard { get; }

        public ConfigEditorViewModel Editor { get; }

        public LogViewerViewModel Logs { get; }

        public WizardViewModel Wizard { get; }

        public ObservableCollection<NavigationItem> Items { get; }

        /// <summary>
        /// Shown in the rail so the user knows what to expect: elevated sessions get no
        /// UAC prompts, standard ones get one per change.
        /// </summary>
        public bool IsElevated { get; }

        public string ElevationLabel => this.IsElevated ? "Administrator" : "Standard user";

        public string ElevationHint => this.IsElevated
            ? "Service changes apply without prompts."
            : "Each service change asks for elevation.";

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

        private static bool DetectElevation()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
