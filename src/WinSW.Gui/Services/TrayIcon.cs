using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A notification-area icon: lets the window hide to the tray and carries the balloon
    /// notifications for services that stop unexpectedly.
    /// </summary>
    /// <remarks>
    /// WPF has no tray control, so the Windows Forms one is used. The icon is the
    /// application's own, read from the same resource the windows use, so the tray and the
    /// taskbar cannot drift apart.
    /// </remarks>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon icon;
        private readonly ToolStripMenuItem open;
        private readonly ToolStripMenuItem exit;
        private string? lastNotificationTag;

        public TrayIcon()
        {
            this.open = new ToolStripMenuItem();
            this.exit = new ToolStripMenuItem();
            this.open.Click += (_, _) => this.OpenRequested?.Invoke();
            this.exit.Click += (_, _) => this.ExitRequested?.Invoke();

            var menu = new ContextMenuStrip();
            menu.Items.Add(this.open);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(this.exit);

            this.icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "WinSW",
                ContextMenuStrip = menu,
                Visible = false,
            };

            this.icon.DoubleClick += (_, _) => this.OpenRequested?.Invoke();
            this.icon.BalloonTipClicked += (_, _) =>
            {
                this.OpenRequested?.Invoke();
                if (this.lastNotificationTag != null)
                {
                    this.NotificationClicked?.Invoke(this.lastNotificationTag);
                }
            };

            this.Relabel();
            Localizer.Changed += this.Relabel;
        }

        public event Action? OpenRequested;

        public event Action? ExitRequested;

        /// <summary>Raised with the tag given to <see cref="Notify"/> when its balloon is clicked.</summary>
        public event Action<string>? NotificationClicked;

        public bool Visible
        {
            get => this.icon.Visible;
            set => this.icon.Visible = value;
        }

        public void Notify(string title, string text, bool isError, string? tag = null)
        {
            this.lastNotificationTag = tag;
            bool wasVisible = this.icon.Visible;
            this.icon.Visible = true;
            this.icon.ShowBalloonTip(8000, title, text, isError ? ToolTipIcon.Error : ToolTipIcon.Info);

            // A balloon needs a visible icon; keep it only if the window is in the tray.
            if (!wasVisible)
            {
                this.icon.Visible = false;
            }
        }

        private void Relabel()
        {
            this.open.Text = Localizer.Get("M.Tray.Open");
            this.exit.Text = Localizer.Get("M.Tray.Exit");
        }

        /// <summary>
        /// The application icon at the size the notification area asks for, which is not
        /// always 16 pixels: it follows the display scaling.
        /// </summary>
        private static Icon LoadIcon()
        {
            var resource = Application.GetResourceStream(new Uri("/WinSW.Gui;component/Assets/WinSW.Gui.ico", UriKind.Relative));
            using var stream = resource!.Stream;
            return new Icon(stream, SystemInformation.SmallIconSize);
        }

        public void Dispose()
        {
            Localizer.Changed -= this.Relabel;
            this.icon.Visible = false;
            this.icon.Dispose();
        }
    }
}
