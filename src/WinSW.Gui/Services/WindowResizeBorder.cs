using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Gives a window back the resize border its title bar covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console draws its own title bar across the top of the window, and the control that
    /// draws it — WPF-UI 3.1's TitleBar — answers WM_NCHITTEST for the whole of its own area
    /// with "this is the caption": a drag anywhere on it moves the window. That area takes in
    /// the four pixels along the top edge and down both sides that the window chrome had set
    /// aside for resizing, so the top edge and both top corners could not be dragged at all,
    /// and the left and right edges not for the height of the title bar. WPF-UI fixed this
    /// upstream in 4.1 and 4.2 (lepoco/wpfui #1496, #1560, #1604); this is the same answer,
    /// given from this side, for the version in use.
    /// </para>
    /// <para>
    /// A window's hooks are asked newest first. The title bar installs its hook when the
    /// window's content has rendered, so this one is installed from OnContentRendered, after
    /// the base call has raised that event, and is therefore asked before it. Everything it
    /// says the chrome would have said too, had it been asked; only the band the title bar
    /// covers is a change.
    /// </para>
    /// </remarks>
    internal static class WindowResizeBorder
    {
        /// <summary>WPF-UI's own resize border, for a window whose chrome does not say.</summary>
        private const double DefaultBorderDips = 4;

        /// <summary>
        /// Installs the hook. Call from <see cref="Window.OnContentRendered"/> after the base
        /// call, and once: a hook is never removed.
        /// </summary>
        public static void Attach(Window window)
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source)
            {
                return;
            }

            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != NativeMethods.WM_NCHITTEST
                    || window.WindowState != WindowState.Normal
                    || window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
                {
                    return IntPtr.Zero;
                }

                int hit = HitTest(hwnd, window, lParam);
                if (hit == NativeMethods.HTNOWHERE)
                {
                    return IntPtr.Zero;
                }

                handled = true;
                return new IntPtr(hit);
            });
        }

        private static int HitTest(IntPtr hwnd, Window window, IntPtr lParam)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return NativeMethods.HTNOWHERE;
            }

            // Screen coordinates, packed as two signed 16-bit values: a monitor to the left
            // of the primary one is at negative x.
            long packed = lParam.ToInt64();
            int x = (short)(packed & 0xFFFF);
            int y = (short)((packed >> 16) & 0xFFFF);

            // The chrome's own border, in device pixels; the value is in device-independent
            // ones and the window may be on a scaled display.
            var chrome = WindowChrome.GetWindowChrome(window);
            double dipsX = chrome is null ? DefaultBorderDips : Math.Max(chrome.ResizeBorderThickness.Left, chrome.ResizeBorderThickness.Right);
            double dipsY = chrome is null ? DefaultBorderDips : Math.Max(chrome.ResizeBorderThickness.Top, chrome.ResizeBorderThickness.Bottom);
            var dpi = VisualTreeHelper.GetDpi(window);

            return Classify(x, y, rect, (int)Math.Round(dipsX * dpi.DpiScaleX), (int)Math.Round(dipsY * dpi.DpiScaleY));
        }

        /// <summary>
        /// Which edge or corner of <paramref name="rect"/> the point is on, as an HT code, or
        /// <see cref="NativeMethods.HTNOWHERE"/> when it is not on one. The band is measured
        /// inward from the outer edge: with this chrome the window has no frame of its own.
        /// </summary>
        /// <remarks>
        /// A corner wins over an edge, and the border wins over whatever is drawn under it,
        /// including the close button in the top-right corner: four pixels of it, the same
        /// choice upstream made.
        /// </remarks>
        internal static int Classify(int x, int y, in NativeMethods.RECT rect, int borderX, int borderY)
        {
            if (borderX <= 0 || borderY <= 0 || x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom)
            {
                return NativeMethods.HTNOWHERE;
            }

            bool left = x < rect.Left + borderX;
            bool right = x >= rect.Right - borderX;
            bool top = y < rect.Top + borderY;
            bool bottom = y >= rect.Bottom - borderY;

            if (top)
            {
                return left ? NativeMethods.HTTOPLEFT : right ? NativeMethods.HTTOPRIGHT : NativeMethods.HTTOP;
            }

            if (bottom)
            {
                return left ? NativeMethods.HTBOTTOMLEFT : right ? NativeMethods.HTBOTTOMRIGHT : NativeMethods.HTBOTTOM;
            }

            return left ? NativeMethods.HTLEFT : right ? NativeMethods.HTRIGHT : NativeMethods.HTNOWHERE;
        }
    }
}
