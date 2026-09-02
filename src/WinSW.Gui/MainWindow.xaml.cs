using System;
using System.ComponentModel;
using System.Windows;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui
{
    public partial class MainWindow
    {
        private readonly ShellViewModel shell;
        private readonly TrayIcon tray;
        private bool exiting;

        public MainWindow()
        {
            this.InitializeComponent();

            this.shell = new ShellViewModel();
            this.DataContext = this.shell;

            this.tray = new TrayIcon();
            this.tray.OpenRequested += this.RestoreFromTray;
            this.tray.ExitRequested += () =>
            {
                this.exiting = true;
                this.Close();
            };

            this.shell.Dashboard.UnexpectedStop += entry =>
                this.tray.Notify(
                    Localizer.Get("M.Dash.UnexpectedStopTitle"),
                    Localizer.Format("M.Dash.UnexpectedStopBody", entry.ServiceName),
                    isError: true);

            this.RestoreWindowPlacement();
            this.StateChanged += this.OnStateChanged;

            if (App.StartupConfigPath is { } startupPath)
            {
                this.shell.OpenStartupPath(startupPath);
            }
        }

        // Window placement -------------------------------------------------------

        private void RestoreWindowPlacement()
        {
            var settings = AppSettings.Current;
            if (settings.WindowWidth is double width && settings.WindowHeight is double height
                && settings.WindowLeft is double left && settings.WindowTop is double top)
            {
                // Only honour a position that is still on a screen; monitors come and go.
                var area = SystemParameters.VirtualScreenWidth;
                var areaHeight = SystemParameters.VirtualScreenHeight;
                if (left >= SystemParameters.VirtualScreenLeft - 8 && top >= SystemParameters.VirtualScreenTop - 8
                    && left + 100 < SystemParameters.VirtualScreenLeft + area && top + 100 < SystemParameters.VirtualScreenTop + areaHeight)
                {
                    this.WindowStartupLocation = WindowStartupLocation.Manual;
                    this.Left = left;
                    this.Top = top;
                    this.Width = Math.Max(this.MinWidth, width);
                    this.Height = Math.Max(this.MinHeight, height);
                }
            }

            if (settings.WindowMaximized)
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void SaveWindowPlacement()
        {
            var settings = AppSettings.Current;
            var bounds = this.WindowState == WindowState.Normal ? new Rect(this.Left, this.Top, this.Width, this.Height) : this.RestoreBounds;

            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.WindowMaximized = this.WindowState == WindowState.Maximized;
            settings.Save();
        }

        // Tray ----------------------------------------------------------------------

        private void OnStateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized && AppSettings.Current.MinimizeToTray)
            {
                this.Hide();
                this.tray.Visible = true;

                // Keep polling while hidden so an unexpected stop still produces a notification.
                this.shell.Dashboard.KeepWatching();
            }
        }

        private void RestoreFromTray()
        {
            this.tray.Visible = false;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!this.exiting && AppSettings.Current.MinimizeToTray)
            {
                // Closing behaves like minimizing when the tray is on; Exit lives in the tray menu.
                e.Cancel = true;
                this.WindowState = WindowState.Minimized;
                return;
            }

            this.SaveWindowPlacement();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            this.tray.Dispose();
            base.OnClosed(e);
        }
    }
}
