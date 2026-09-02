using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A notification-area icon: lets the window hide to the tray and carries the balloon
    /// notifications for services that stop unexpectedly.
    /// </summary>
    /// <remarks>
    /// WPF has no tray control; the Windows Forms one is used, which the project already
    /// references for its folder picker. The icon is drawn at runtime so the project needs
    /// no binary asset.
    /// </remarks>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon icon;
        private readonly ToolStripMenuItem open;
        private readonly ToolStripMenuItem exit;

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
                Icon = DrawIcon(),
                Text = "WinSW",
                ContextMenuStrip = menu,
                Visible = false,
            };

            this.icon.DoubleClick += (_, _) => this.OpenRequested?.Invoke();
            this.icon.BalloonTipClicked += (_, _) => this.OpenRequested?.Invoke();

            this.Relabel();
            Localizer.Changed += this.Relabel;
        }

        public event Action? OpenRequested;

        public event Action? ExitRequested;

        public bool Visible
        {
            get => this.icon.Visible;
            set => this.icon.Visible = value;
        }

        public void Notify(string title, string text, bool isError)
        {
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

        private static Icon DrawIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(0x7C, 0x6C, 0xFF), Color.FromArgb(0x22, 0xD3, 0xEE), 45f);
                using var path = RoundedRectangle(new Rectangle(1, 1, 30, 30), 8);
                graphics.FillPath(fill, path);

                using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString("W", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                // Clone so the icon owns its handle and DestroyIcon can be called on ours.
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            Localizer.Changed -= this.Relabel;
            this.icon.Visible = false;
            this.icon.Dispose();
        }
    }
}
