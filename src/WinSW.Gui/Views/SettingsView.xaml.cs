using System.Windows;
using System.Windows.Controls;
using WinSW.Gui.Services;

namespace WinSW.Gui.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            this.InitializeComponent();
        }

        private void OnOpenLink(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url })
            {
                SystemShell.OpenUrl(url);
            }
        }
    }
}
