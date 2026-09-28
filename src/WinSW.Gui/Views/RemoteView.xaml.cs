using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class RemoteView : UserControl
    {
        public RemoteView()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Enter in the machine box connects, as the button does. While the drop-down is open
        /// the key is the ComboBox's, and picks the highlighted machine; the next Enter connects.
        /// </summary>
        private void OnMachineKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter
                && sender is ComboBox { IsDropDownOpen: false }
                && this.DataContext is RemoteViewModel model
                && model.RefreshCommand.CanExecute(null))
            {
                model.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
