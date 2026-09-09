using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A notification-area icon: lets the window hide to the tray and carries the balloon
    /// notifications for services that stop unexpectedly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF has no tray control. Windows Forms has one, and using it meant compiling the whole
    /// Windows Forms framework into the application — about 15 MB of the self-contained build
    /// — for a single class in a single file. This calls <c>Shell_NotifyIcon</c> directly, as
    /// that class does, and the menu is an ordinary WPF <see cref="ContextMenu"/>, so it
    /// follows the application's theme rather than sitting outside it.
    /// </para>
    /// <para>
    /// The icon is the application's own, read from the same resource the windows use, so the
    /// tray and the taskbar cannot drift apart. <c>System.Drawing</c> is kept for the decoding:
    /// it picks the frame matching the notification area's size out of a multi-resolution .ico,
    /// which is display-scaling-dependent and not worth reimplementing.
    /// </para>
    /// </remarks>
    public sealed class TrayIcon : IDisposable
    {
        /// <summary>Distinguishes our icon within this process. Only one is ever added.</summary>
        private const int IconId = 1;

        /// <summary>Explorer's "the taskbar exists again" broadcast, resolved at startup.</summary>
        private static readonly int TaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        private readonly HwndSource sink;
        private readonly Icon icon;
        private readonly ContextMenu menu;
        private readonly MenuItem open;
        private readonly MenuItem exit;

        private string? lastNotificationTag;
        private bool added;
        private bool visible;
        private bool disposed;

        public TrayIcon()
        {
            this.icon = LoadIcon();

            this.open = new MenuItem();
            this.exit = new MenuItem();
            this.open.Click += (_, _) => this.OpenRequested?.Invoke();
            this.exit.Click += (_, _) => this.ExitRequested?.Invoke();

            this.menu = new ContextMenu();
            this.menu.Items.Add(this.open);
            this.menu.Items.Add(new Separator());
            this.menu.Items.Add(this.exit);

            // A message-only window: never shown, never enumerated, but it has a queue, which
            // is all Shell_NotifyIcon needs somewhere to send its callbacks.
            this.sink = new HwndSource(new HwndSourceParameters("WinSW.Gui.TrayIcon")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = NativeMethods.HWND_MESSAGE,

                // WS_CHILD alone. The default style carries WS_VISIBLE, and a parented window
                // asking to be visible is not what HWND_MESSAGE is for.
                WindowStyle = NativeMethods.WS_CHILD,
            });

            this.sink.AddHook(this.OnMessage);

            this.Relabel();
            Localizer.Changed += this.Relabel;
        }

        public event Action? OpenRequested;

        public event Action? ExitRequested;

        /// <summary>Raised with the tag given to <see cref="Notify"/> when its balloon is clicked.</summary>
        public event Action<string>? NotificationClicked;

        public bool Visible
        {
            get => this.visible;
            set
            {
                if (this.visible == value)
                {
                    return;
                }

                this.visible = value;
                if (value)
                {
                    this.Add();
                }
                else
                {
                    this.Remove();
                }
            }
        }

        public void Notify(string title, string text, bool isError, string? tag = null)
        {
            if (this.disposed)
            {
                return;
            }

            this.lastNotificationTag = tag;

            // A balloon needs an icon in the notification area to hang off. If the window is
            // not in the tray there is none, so one is added for the balloon and taken away
            // again once the shell has it.
            bool wasVisible = this.added;
            this.Add();

            var data = this.Describe(NativeMethods.NIF_INFO);
            data.Info = Truncate(text, 255);
            data.InfoTitle = Truncate(title, 63);
            data.InfoFlags = isError ? NativeMethods.NIIF_ERROR : NativeMethods.NIIF_INFO;

            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);

            if (!wasVisible && !this.visible)
            {
                this.Remove();
            }
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            Localizer.Changed -= this.Relabel;

            this.Remove();

            this.sink.RemoveHook(this.OnMessage);
            this.sink.Dispose();
            this.icon.Dispose();
        }

        /// <summary>
        /// The application icon at the size the notification area asks for, which is not
        /// always 16 pixels: it follows the display scaling.
        /// </summary>
        private static Icon LoadIcon()
        {
            var resource = Application.GetResourceStream(new Uri("/WinSW.Gui;component/Assets/WinSW.Gui.ico", UriKind.Relative));
            using var stream = resource!.Stream;

            int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
            return new Icon(stream, size > 0 ? size : 16, size > 0 ? size : 16);
        }

        /// <summary>
        /// Shell_NotifyIcon copies fixed-size buffers out of the structure and does not
        /// tolerate being handed more than they hold.
        /// </summary>
        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max);

        private NativeMethods.NOTIFYICONDATA Describe(int extraFlags)
        {
            return new NativeMethods.NOTIFYICONDATA
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                Window = this.sink.Handle,
                Id = IconId,
                Flags = NativeMethods.NIF_ICON | NativeMethods.NIF_MESSAGE | NativeMethods.NIF_TIP | extraFlags,
                CallbackMessage = NativeMethods.WM_TRAYICON,
                Icon = this.icon.Handle,
                Tip = Truncate(Localizer.Get("M.Tray.Tip"), 127),
                Info = string.Empty,
                InfoTitle = string.Empty,
                Version = NativeMethods.NOTIFYICON_VERSION_4,
            };
        }

        private void Add()
        {
            if (this.added || this.disposed)
            {
                return;
            }

            var data = this.Describe(0);
            if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data))
            {
                // Explorer may not be ready yet — at logon this call can run before the
                // taskbar exists. TaskbarCreated will bring us back.
                return;
            }

            // Has to follow the add, and asks for the richer callbacks.
            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
            this.added = true;
        }

        private void Remove()
        {
            if (!this.added)
            {
                return;
            }

            var data = this.Describe(0);
            _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            this.added = false;
        }

        private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Explorer restarted and took every notification icon with it.
            if (message == TaskbarCreated && TaskbarCreated != 0)
            {
                if (this.visible)
                {
                    this.added = false;
                    this.Add();
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (message != NativeMethods.WM_TRAYICON)
            {
                return IntPtr.Zero;
            }

            // Under version 4 the notification is in the low word of lParam; the cursor
            // position is in wParam, which the WPF menu does not need.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NativeMethods.NIN_SELECT:
                case NativeMethods.NIN_KEYSELECT:
                case NativeMethods.WM_LBUTTONDBLCLK:
                    this.OpenRequested?.Invoke();
                    break;

                case NativeMethods.NIN_BALLOONUSERCLICK:
                    this.OpenRequested?.Invoke();
                    if (this.lastNotificationTag is { } tag)
                    {
                        this.NotificationClicked?.Invoke(tag);
                    }

                    break;

                case NativeMethods.WM_CONTEXTMENU:
                case NativeMethods.WM_RBUTTONUP:
                    this.ShowMenu();
                    break;
            }

            handled = true;
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            // Without this the menu stays up after a click elsewhere on the desktop: a popup
            // dismisses on losing activation, and it never had any.
            _ = NativeMethods.SetForegroundWindow(this.sink.Handle);

            this.menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            this.menu.IsOpen = true;
        }

        private void Relabel()
        {
            this.open.Header = Localizer.Get("M.Tray.Open");
            this.exit.Header = Localizer.Get("M.Tray.Exit");

            if (this.added)
            {
                // The tooltip is part of the icon's own data, so a language change has to be
                // pushed to the shell rather than merely stored.
                var data = this.Describe(0);
                _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
            }
        }
    }
}
