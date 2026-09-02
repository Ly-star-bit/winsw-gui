using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

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
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }
}
