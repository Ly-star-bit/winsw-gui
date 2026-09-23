using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.Model;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class DashboardView : UserControl
    {
        private DashboardViewModel? attached;

        public DashboardView()
        {
            this.InitializeComponent();
            this.DataContextChanged += this.OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.attached != null)
            {
                this.attached.PropertyChanged -= this.OnViewModelPropertyChanged;
            }

            this.attached = e.NewValue as DashboardViewModel;

            if (this.attached != null)
            {
                this.attached.PropertyChanged += this.OnViewModelPropertyChanged;
            }
        }

        // Keyboard users land on Cancel when the confirmation opens; Enter is bound to Confirm.
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DashboardViewModel.ConfirmVisible) && this.attached?.ConfirmVisible == true)
            {
                this.Dispatcher.BeginInvoke(() => this.ConfirmCancelButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // A group is committed when the field is left, on Enter, or when a group is picked, and
        // it is committed to the service the field shows: its own data context, not whatever
        // the list has selected by then.
        private void OnGroupCommit(object? sender, System.EventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is Model.ServiceEntry entry && this.attached != null)
            {
                this.attached.AssignGroup(entry, combo.Text ?? string.Empty);
            }
        }

        private void OnGroupKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                this.OnGroupCommit(sender, e);
                e.Handled = true;
            }
        }

        // ListBox.SelectedItems is not bindable; hand the multi-selection to the view model here.
        private void OnServiceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.attached != null)
            {
                this.attached.SelectedEntries = this.ServiceList.SelectedItems.OfType<ServiceEntry>().ToList();
            }
        }

        private void OnServiceDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (this.attached?.ViewLogsCommand.CanExecute(null) == true)
            {
                this.attached.ViewLogsCommand.Execute(null);
            }
        }

        // A ContextMenu is not in the visual tree, so it does not inherit the DataContext.
        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu is { } menu)
            {
                menu.DataContext = this.DataContext;
                menu.PlacementTarget = button;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }
    }
}
