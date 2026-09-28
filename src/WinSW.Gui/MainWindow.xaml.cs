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
        private bool resizeBorderAttached;

        /// <summary>
        /// Whether the window has ever been shown. A console started in the tray may never be,
        /// and a window that never had a handle has no placement worth saving: its restore
        /// bounds are an empty rectangle, whose infinite edges the settings file cannot hold.
        /// </summary>
        private bool everShown;

        /// <summary>
        /// Started at sign-in, in the tray — or restarted as administrator from a console that
        /// was. Such a console keeps to the tray whatever the minimize-to-tray setting says:
        /// closing its window must not end the watching that starting with Windows was asked for.
        /// </summary>
        private bool watchesFromTray;

        /// <summary>Close and minimize put the window in the tray rather than ending or iconizing it.</summary>
        private bool KeepsToTray => AppSettings.Current.MinimizeToTray || this.watchesFromTray;

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
            this.shell.RestartElevatedDecided += this.OnRestartElevatedDecided;

            // The size the window is laid out for, read before a saved one replaces it: when a
            // window that does not fit the screen would not fit at that size either, it opens
            // maximized. The saved placement came from whatever screen the last session had.
            double designWidth = this.Width;
            double designHeight = this.Height;
            this.RestoreWindowPlacement();
            WindowFit.Attach(this, designWidth, designHeight);
            this.StateChanged += this.OnStateChanged;

            if (App.StartupConfigPath is { } startupPath)
            {
                this.shell.OpenStartupPath(startupPath);
            }
        }

        /// <summary>
        /// Starts with only the tray icon showing: the start at sign-in. The dashboard is the
        /// page in front, so its polling is already running, and the notifications with it.
        /// </summary>
        public void StartInTray()
        {
            this.watchesFromTray = true;
            this.tray.Visible = true;
            this.shell.Dashboard.KeepWatching();
        }

        /// <summary>
        /// Watches from the tray as the copy this one replaced did, without starting there: that
        /// copy restarted as administrator, and the window is what the user is waiting for.
        /// Closing or minimizing it puts the console in the tray, where the dashboard goes on
        /// polling, as it would for a console started in the tray.
        /// </summary>
        public void KeepTrayWatch() => this.watchesFromTray = true;

        /// <summary>Shows the window and puts it in front: a second launch asked for this copy.</summary>
        public void BringToFront()
        {
            if (!this.IsVisible || this.WindowState == WindowState.Minimized)
            {
                this.RestoreFromTray();
            }
            else
            {
                this.Activate();
            }
        }

        /// <summary>
        /// Opens a configuration a later launch handed to this copy: "Open in WinSW" while this
        /// one watches from the tray.
        /// </summary>
        public void OpenHandedOver(string path)
        {
            // On screen first: opening it may ask about unsaved changes, and a prompt in a window
            // hidden in the tray is a hang, not a question.
            this.BringToFront();
            this.shell.OpenHandedOverPath(path);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            this.everShown = true;
        }

        /// <summary>
        /// Puts the resize border back under the title bar. After the base call, which is what
        /// raises the event the title bar installs its own hook on; see WindowResizeBorder.
        /// </summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (!this.resizeBorderAttached)
            {
                this.resizeBorderAttached = true;
                WindowResizeBorder.Attach(this);
            }
        }

        // Window placement -------------------------------------------------------

        private void RestoreWindowPlacement()
        {
            var settings = AppSettings.Current;
            if (settings.WindowWidth is double width && settings.WindowHeight is double height
                && settings.WindowLeft is double left && settings.WindowTop is double top)
            {
                // Only honour a position that is still on a screen; monitors come and go. The
                // screen may also be smaller than it was: WindowFit brings the window within it.
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
            if (this.WindowState == WindowState.Minimized && this.KeepsToTray)
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

        /// <summary>
        /// Starts the copy that replaces this one as administrator, and makes way for it. The
        /// unsaved changes have been asked about already.
        /// </summary>
        /// <remarks>
        /// A declined UAC prompt changes nothing: this copy stays, and so do any changes in the
        /// editor, even after "restart without saving", which only said not to write them.
        /// </remarks>
        private void OnRestartElevatedDecided(string? configPath)
        {
            if (!Elevation.RestartElevated(configPath, keepTray: this.watchesFromTray))
            {
                return;
            }

            // Closed as an exit already answered for. Kept to the tray, the window would hide
            // instead and go on holding the session the new copy is waiting for; and it would ask
            // again about changes that were just discarded.
            this.closeConfirmed = true;
            this.Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Everything below is the question "should this window really close". Once it has
            // been answered, the second pass has nothing left to ask.
            if (!this.closeConfirmed)
            {
                if (!this.exiting && this.KeepsToTray)
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

            if (this.everShown)
            {
                this.SaveWindowPlacement();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            this.tray.Dispose();
            this.shell.Dispose();
            base.OnClosed(e);
        }
    }
}
