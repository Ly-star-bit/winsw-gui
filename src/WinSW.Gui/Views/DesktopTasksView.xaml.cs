using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class DesktopTasksView : UserControl
    {
        private DesktopTasksViewModel? attached;

        public DesktopTasksView()
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

            this.attached = e.NewValue as DesktopTasksViewModel;

            if (this.attached != null)
            {
                this.attached.PropertyChanged += this.OnViewModelPropertyChanged;
            }
        }

        // Keyboard users land on Cancel when the confirmation opens; Enter is bound to Confirm.
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DesktopTasksViewModel.ConfirmVisible) && this.attached?.ConfirmVisible == true)
            {
                this.Dispatcher.BeginInvoke(() => this.ConfirmCancelButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void OnTaskDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (this.attached?.ViewLogsCommand.CanExecute(null) == true)
            {
                this.attached.ViewLogsCommand.Execute(null);
            }
        }
    }
}
