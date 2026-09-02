using System.Linq;
using System.Windows.Controls;
using WinSW.Gui.Model;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            this.InitializeComponent();
        }

        // ListBox.SelectedItems is not bindable; hand the multi-selection to the view model here.
        private void OnServiceSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.DataContext is DashboardViewModel viewModel)
            {
                viewModel.SelectedEntries = this.ServiceList.SelectedItems.OfType<ServiceEntry>().ToList();
            }
        }
    }
}
