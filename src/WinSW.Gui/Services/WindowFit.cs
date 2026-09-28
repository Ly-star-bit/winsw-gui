using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinSW.Gui.Services
{
    /// <summary>A window's or a work area's position and size, in device-independent pixels.</summary>
    internal readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
    {
        public double Right => this.Left + this.Width;

        public double Bottom => this.Top + this.Height;
    }

    /// <summary>Where a window goes to fit its screen, and whether it is better maximized there.</summary>
    /// <param name="Bounds">The normal bounds: where the window is put, and where it returns from maximized.</param>
    /// <param name="Maximize">The window does not fit, and would not at the size it is laid out for either.</param>
    /// <param name="MinWidth">The window's minimum width on this screen: the one it is laid out with, or the screen's where that is less.</param>
    /// <param name="MinHeight">The same, down.</param>
    internal readonly record struct FittedWindow(WindowBounds Bounds, bool Maximize, double MinWidth, double MinHeight);

    /// <summary>
    /// Keeps a window within the work area of the screen it opens on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console is used over remote desktop, and a session's screen is whatever the client
    /// asked for this time. A window closed at 1500 pixels wide in a large session reopened in
    /// a 1280- or 1024-wide one with its right side off the screen, and with it the detail
    /// panel and its buttons; nor does a first launch fit at 1366 by 768, where the window is
    /// taller than the screen and centring it puts the title bar above the top edge. Windows
    /// only keeps a window's position on some screen, never its size.
    /// </para>
    /// <para>
    /// So when the window gets its handle — placed, whether by the saved placement or by
    /// centring, but not yet on screen — it is sized down to the work area of the monitor under
    /// its title bar and moved inside it. When it did not fit and even the size it is laid out
    /// for would not, it is maximized as well, which is the most it can show. The same happens
    /// when the display changes under a running console, which is what a remote desktop
    /// reconnecting at another resolution looks like. A window that was
    /// maximized or in the tray at either moment still has normal bounds set for another
    /// screen, and is fitted when it returns to them: then without the maximizing, which would
    /// leave no way out of it. Any other return to the normal size is left where the user had
    /// it — straddling two monitors, or half off the screen on purpose.
    /// </para>
    /// <para>
    /// A window's minimum size gives way to a screen smaller than it: 1024 by 768 at 125 % is
    /// a work area of about 819 by 582 device-independent pixels, under the main window's
    /// minimum of 960 by 600. Windows keeps a window at its minimum even maximized, and a
    /// window returned to normal bounds that the minimum had kept wider than the screen had its
    /// right side off it — the very thing this is for. The minimum it is laid out with comes
    /// back on a screen large enough for it.
    /// </para>
    /// </remarks>
    internal static class WindowFit
    {
        /// <summary>
        /// How far past the work area still counts as inside it. Sizes converted between
        /// device-independent and device pixels on a scaled display come back a fraction of a
        /// pixel off, and a window sized to fill the screen by hand is not to be moved for that.
        /// </summary>
        private const double Slack = 1;

        /// <summary>
        /// Fits <paramref name="window"/> to its screen when it is first placed and whenever the
        /// display changes, or, when it is maximized or minimized then, as it returns to its
        /// normal size. Call once, before the window is shown.
        /// </summary>
        /// <param name="window">The window to keep on screen.</param>
        /// <param name="designWidth">The width the window is laid out for, before any saved one.</param>
        /// <param name="designHeight">The height the window is laid out for, before any saved one.</param>
        public static void Attach(Window window, double designWidth, double designHeight)
        {
            // The minimum the window is laid out with, which a small screen lowers for a while.
            double minWidth = window.MinWidth;
            double minHeight = window.MinHeight;

            // Whether the normal bounds may have been set for another screen: until the window
            // has been fitted once, and again after every display change. Touched on the
            // window's thread only.
            bool pending = true;

            void FitNow(bool mayMaximize)
            {
                if (Refit(window, minWidth, minHeight, designWidth, designHeight, mayMaximize))
                {
                    pending = false;
                }
            }

            // Raised on the thread that watches for system events, not on the window's.
            void OnDisplayChanged(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(() =>
            {
                pending = true;
                FitNow(mayMaximize: true);
            });

            window.SourceInitialized += (_, _) => FitNow(mayMaximize: true);
            window.StateChanged += (_, _) =>
            {
                // After the change has settled: the event is raised from within the resize.
                if (pending && window.WindowState == WindowState.Normal)
                {
                    window.Dispatcher.BeginInvoke(() => FitNow(mayMaximize: false));
                }
            };

            // The static event holds on to whoever subscribes; a dialog that did not let go of
            // it would be kept for the life of the process, and asked to fit after closing.
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            window.Closed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        /// <summary>
        /// Where <paramref name="window"/> goes to fit <paramref name="workArea"/>: each side no
        /// larger than the work area and no smaller than the minimum, then moved inside it, as
        /// little as it takes. A window that already fits, give or take <see cref="Slack"/>, is
        /// left exactly as it is. A minimum larger than the work area is lowered to it, so that
        /// what is returned always fits.
        /// </summary>
        /// <remarks>
        /// Maximizing is kept for a window that does not fit and could not at the size it is
        /// laid out for either — a first launch on a small screen, or a larger saved size. A
        /// smaller size somebody chose for this screen is theirs to keep.
        /// </remarks>
        internal static FittedWindow Fit(
            WindowBounds window,
            WindowBounds workArea,
            double minWidth,
            double minHeight,
            double designWidth,
            double designHeight)
        {
            // Not above the room there is: kept, it would hold the window past the screen's
            // edge however it was placed. Otherwise the minimum it is laid out with.
            double fittedMinWidth = Math.Min(minWidth, workArea.Width);
            double fittedMinHeight = Math.Min(minHeight, workArea.Height);

            // A window below the minimum is one a smaller screen let shrink, whose minimum is
            // back now: WPF brings it up to that, and it is at that size that it has to fit.
            double wantedWidth = Math.Max(window.Width, fittedMinWidth);
            double wantedHeight = Math.Max(window.Height, fittedMinHeight);

            bool widthFits = FitsIn(wantedWidth, workArea.Width);
            bool heightFits = FitsIn(wantedHeight, workArea.Height);

            double width = widthFits ? wantedWidth : workArea.Width;
            double height = heightFits ? wantedHeight : workArea.Height;

            var bounds = new WindowBounds(
                Place(window.Left, width, workArea.Left, workArea.Right),
                Place(window.Top, height, workArea.Top, workArea.Bottom),
                width,
                height);

            bool designFits = FitsIn(designWidth, workArea.Width) && FitsIn(designHeight, workArea.Height);
            return new FittedWindow(bounds, Maximize: !(widthFits && heightFits) && !designFits, fittedMinWidth, fittedMinHeight);
        }

        /// <summary>A rectangle in device pixels, in device-independent ones at the given scale.</summary>
        /// <param name="rect">The rectangle, in device pixels.</param>
        /// <param name="scaleX">Device pixels to a device-independent one across: 1.5 at 144 dpi.</param>
        /// <param name="scaleY">Device pixels to a device-independent one down.</param>
        internal static WindowBounds FromPixels(in NativeMethods.RECT rect, double scaleX, double scaleY) => new(
            rect.Left / scaleX,
            rect.Top / scaleY,
            (rect.Right - rect.Left) / scaleX,
            (rect.Bottom - rect.Top) / scaleY);

        /// <summary>No larger than the room, give or take <see cref="Slack"/>. A size not given fits.</summary>
        private static bool FitsIn(double size, double room) => !(size > room + Slack);

        /// <summary>
        /// Where a side of the given length starts, to lie between two edges. When it cannot, it
        /// starts at the first: the title bar, and the window's own controls, are at the top left.
        /// </summary>
        private static double Place(double start, double length, double low, double high)
        {
            if (start >= low - Slack && start + length <= high + Slack)
            {
                return start;
            }

            return Math.Max(low, Math.Min(start, high - length));
        }

        /// <summary>
        /// Fits a window at its normal size to the monitor under the middle of its title bar;
        /// false when it could not be measured or was not at its normal size. Nothing is done
        /// for a window that has no handle yet — a console started in the tray, which is fitted
        /// when it is first shown — nor for one minimized. One maximized has only its minimum
        /// set for the screen, so that it is maximized to the screen and no larger; its normal
        /// bounds are fitted when it returns to them.
        /// </summary>
        /// <param name="minWidth">The minimum width the window is laid out with.</param>
        /// <param name="minHeight">The minimum height the window is laid out with.</param>
        private static bool Refit(Window window, double minWidth, double minHeight, double designWidth, double designHeight, bool mayMaximize)
        {
            // A minimized window's rectangle is its icon's, far off any screen.
            if (window.WindowState == WindowState.Minimized
                || PresentationSource.FromVisual(window) is not HwndSource { CompositionTarget: { } target } source
                || !NativeMethods.GetWindowRect(source.Handle, out var rect))
            {
                return false;
            }

            // In device pixels, as Windows has the window now: the saved placement, or the
            // position centring worked out, whichever put it there; or maximized, on the monitor
            // it is maximized on. A maximized window's frame reaches past that monitor's edges,
            // its title bar onto a monitor above, so its middle is what says which one it is on.
            bool maximized = window.WindowState == WindowState.Maximized;
            var probe = new NativeMethods.POINT
            {
                X = rect.Left + ((rect.Right - rect.Left) / 2),
                Y = maximized ? rect.Top + ((rect.Bottom - rect.Top) / 2) : rect.Top,
            };
            var monitor = NativeMethods.MonitorFromPoint(probe, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                return false;
            }

            // At the window's own scale, which is the one WPF converts its Left and Top with.
            // It is the scale of the monitor found above unless the window reaches onto it from
            // a monitor of another; Windows then rescales the window as it moves across.
            var toDevice = target.TransformToDevice;
            var current = FromPixels(rect, toDevice.M11, toDevice.M22);
            var workArea = FromPixels(info.WorkArea, toDevice.M11, toDevice.M22);
            if (workArea.Width <= 0 || workArea.Height <= 0)
            {
                return false;
            }

            var fitted = Fit(current, workArea, minWidth, minHeight, designWidth, designHeight);

            // The minimum first, maximized or not, and before a size below the old one is set,
            // which it would otherwise hold up.
            if (window.MinWidth != fitted.MinWidth)
            {
                window.MinWidth = fitted.MinWidth;
            }

            if (window.MinHeight != fitted.MinHeight)
            {
                window.MinHeight = fitted.MinHeight;
            }

            if (window.WindowState != WindowState.Normal)
            {
                return false;
            }

            // Only what changed: a side read back from pixels is a fraction off the one WPF
            // holds, and setting it would resize the window by a pixel for nothing.
            if (fitted.Bounds.Width != current.Width)
            {
                window.Width = fitted.Bounds.Width;
            }

            if (fitted.Bounds.Height != current.Height)
            {
                window.Height = fitted.Bounds.Height;
            }

            if (fitted.Bounds.Left != current.Left)
            {
                window.Left = fitted.Bounds.Left;
            }

            if (fitted.Bounds.Top != current.Top)
            {
                window.Top = fitted.Bounds.Top;
            }

            // After the normal bounds are set, so that leaving maximized returns to a window
            // that fits.
            if (fitted.Maximize && mayMaximize)
            {
                window.WindowState = WindowState.Maximized;
            }

            return true;
        }
    }
}
