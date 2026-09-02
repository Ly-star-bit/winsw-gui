using System.Windows;
using System.Windows.Threading;

namespace WinSW.Gui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // A crash dialog with the message beats the process silently disappearing,
            // which is what an unhandled exception on the dispatcher otherwise produces.
            this.DispatcherUnhandledException += OnDispatcherUnhandledException;
            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                e.Exception.ToString(),
                "WinSW — unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
