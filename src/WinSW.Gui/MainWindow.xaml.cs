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
        private bool closeConfirmed;

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
                    entry.CrashCount > 1
                        ? Localizer.Format("M.Dash.UnexpectedStopRepeated", entry.ServiceName, entry.CrashCount)
                        : Localizer.Format("M.Dash.UnexpectedStopBody", entry.ServiceName),
                    isError: true,
                    tag: entry.ServiceName);
            this.tray.NotificationClicked += serviceName => this.shell.ShowService(serviceName);

            this.shell.ExitDecided += this.OnExitDecided;

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

        /// <summary>
        /// Acts on the answer to the unsaved-changes prompt. A save that did not take —
        /// an invalid configuration, a declined elevation, a cancelled Save As — leaves the
        /// window open with the editor showing why, rather than closing over the changes.
        /// </summary>
        private async void OnExitDecided(bool save)
        {
            if (save && !await this.shell.Editor.TrySaveAsync().ConfigureAwait(true))
            {
                return;
            }

            this.closeConfirmed = true;
            this.Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Everything below is the question "should this window really close". Once it has
            // been answered, the second pass has nothing left to ask.
            if (!this.closeConfirmed)
            {
                if (!this.exiting && AppSettings.Current.MinimizeToTray)
                {
                    // Closing behaves like minimizing when the tray is on; Exit lives in the tray menu.
                    e.Cancel = true;
                    this.WindowState = WindowState.Minimized;
                    return;
                }

                if (this.shell.Editor.IsDirty)
                {
                    e.Cancel = true;

                    // A prompt in a window that is hidden in the tray is a hang, not a question.
                    if (!this.IsVisible || this.WindowState == WindowState.Minimized)
                    {
                        this.RestoreFromTray();
                    }

                    // Exit was asked for and then interrupted; the next close starts over.
                    this.exiting = false;

                    // Answer it looking at the thing that is unsaved.
                    this.shell.Navigate(this.shell.Editor);
                    this.shell.AskToExit();
                    return;
                }
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
