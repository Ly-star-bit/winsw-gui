using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Where a window goes to fit the screen it opens on. The failure this guards against is a
    /// console closed wide in a large remote desktop session and reopened in a smaller one,
    /// with its right side — the detail panel and its buttons — off the screen. The window
    /// itself, its monitor and the display events need Windows, which a test does not have.
    /// </summary>
    public class WindowFitTests
    {
        // The main window as it is laid out: 1280 by 820, no smaller than 960 by 600 where the
        // screen has room for that.
        private const double MinWidth = 960;
        private const double MinHeight = 600;
        private const double DesignWidth = 1280;
        private const double DesignHeight = 820;

        // Work areas: the screen less a 40-pixel taskbar along the bottom.
        private static readonly WindowBounds Screen1920 = new(0, 0, 1920, 1040);
        private static readonly WindowBounds Screen1280 = new(0, 0, 1280, 984);
        private static readonly WindowBounds Screen1366 = new(0, 0, 1366, 728);
        private static readonly WindowBounds Screen1024 = new(0, 0, 1024, 728);

        [Fact]
        public void AWindowThatFitsIsLeftWhereItIs()
        {
            var window = new WindowBounds(100, 50, 1000, 700);

            var fitted = Fit(window, Screen1280);

            Assert.Equal(window, fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        /// <summary>
        /// The case this is for: closed at Left 300, 1500 wide in a large session, reopened in a
        /// 1280-wide one. The width comes down to the screen's and the window moves to its left
        /// edge; the height already fitted and stays.
        /// </summary>
        [Fact]
        public void ASizeSavedInAWiderSessionIsBroughtWithinTheScreen()
        {
            var fitted = Fit(new WindowBounds(300, 50, 1500, 900), Screen1280);

            Assert.Equal(new WindowBounds(0, 50, 1280, 900), fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        /// <summary>
        /// At 1024 by 768 not even the size the window is laid out for fits, so it is maximized;
        /// its normal bounds still fit, so leaving maximized puts it on the screen too.
        /// </summary>
        [Fact]
        public void WhereNotEvenTheDesignedSizeFitsTheWindowIsMaximized()
        {
            var fitted = Fit(new WindowBounds(300, 100, 1500, 900), Screen1024);

            Assert.Equal(new WindowBounds(0, 0, 1024, 728), fitted.Bounds);
            Assert.True(fitted.Maximize);
        }

        /// <summary>
        /// A first launch at 1366 by 768: centring a window taller than the screen puts its title
        /// bar above the top edge.
        /// </summary>
        [Fact]
        public void AFirstLaunchTallerThanTheScreenIsMaximizedWithItsTitleBarOnScreen()
        {
            var centred = new WindowBounds((1366 - 1280) / 2, (728 - 820) / 2, 1280, 820);

            var fitted = Fit(centred, Screen1366);

            Assert.Equal(new WindowBounds(43, 0, 1280, 728), fitted.Bounds);
            Assert.True(fitted.Maximize);
        }

        /// <summary>
        /// Somebody who sized the window down to fit a small screen keeps it that way, although
        /// the size it is laid out for would not fit there.
        /// </summary>
        [Fact]
        public void ASmallerSizeChosenForASmallScreenIsKept()
        {
            var window = new WindowBounds(10, 10, 1000, 650);

            var fitted = Fit(window, Screen1366);

            Assert.Equal(window, fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        [Fact]
        public void AWindowPastTheRightEdgeIsMovedBackWholeAtItsOwnSize()
        {
            var fitted = Fit(new WindowBounds(800, 100, 1000, 700), Screen1280);

            Assert.Equal(new WindowBounds(280, 100, 1000, 700), fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        [Fact]
        public void AWindowPastTheBottomIsMovedUp()
        {
            var fitted = Fit(new WindowBounds(100, 500, 1000, 700), Screen1280);

            Assert.Equal(new WindowBounds(100, 284, 1000, 700), fitted.Bounds);
        }

        [Fact]
        public void ATitleBarAboveTheScreenIsBroughtDown()
        {
            var fitted = Fit(new WindowBounds(100, -30, 1000, 700), Screen1280);

            Assert.Equal(new WindowBounds(100, 0, 1000, 700), fitted.Bounds);
        }

        /// <summary>A monitor that is gone: the window was placed on the nearest one, off its right.</summary>
        [Fact]
        public void AWindowWhollyOffTheScreenIsBroughtOntoIt()
        {
            var fitted = Fit(new WindowBounds(2500, 100, 1000, 700), Screen1920);

            Assert.Equal(new WindowBounds(920, 100, 1000, 700), fitted.Bounds);
        }

        [Fact]
        public void ATooTallWindowKeepsItsWidthAndStartsAtTheTop()
        {
            var fitted = Fit(new WindowBounds(200, 150, 1100, 1200), Screen1920);

            Assert.Equal(new WindowBounds(200, 0, 1100, 1040), fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        /// <summary>
        /// A monitor to the left of the primary one is at negative x, and a taskbar along the
        /// top moves the work area's top down.
        /// </summary>
        [Fact]
        public void TheWorkAreaNeedNotStartAtTheOrigin()
        {
            var leftMonitor = new WindowBounds(-1920, 40, 1920, 1040);

            var fitted = Fit(new WindowBounds(-2100, 0, 1000, 700), leftMonitor);

            Assert.Equal(new WindowBounds(-1920, 40, 1000, 700), fitted.Bounds);
        }

        /// <summary>
        /// A screen smaller than the window's minimum: the minimum gives way to the screen, so
        /// that the normal bounds a restore returns to fit it as well as the maximized window.
        /// </summary>
        [Fact]
        public void AScreenSmallerThanTheMinimumLowersTheMinimumToIt()
        {
            var fitted = Fit(new WindowBounds(50, 50, 1280, 820), new WindowBounds(0, 0, 900, 560));

            Assert.Equal(new WindowBounds(0, 0, 900, 560), fitted.Bounds);
            Assert.True(fitted.Maximize);
            Assert.Equal(900, fitted.MinWidth);
            Assert.Equal(560, fitted.MinHeight);
        }

        /// <summary>
        /// 1024 by 768 over remote desktop at 125 % and at 150 %, with a 40-pixel taskbar: both
        /// are under the main window's minimum of 960 by 600. Restoring down from maximized
        /// used to put the window's right side some 140 pixels past the screen's edge.
        /// </summary>
        [Theory]
        [InlineData(1.25)]
        [InlineData(1.5)]
        public void At1024By768ScaledUpTheRestoredWindowFitsTheScreen(double scale)
        {
            var workArea = new WindowBounds(0, 0, 1024 / scale, (768 - 40) / scale);

            var fitted = Fit(new WindowBounds(0, 0, 1280, 820), workArea);

            Assert.Equal(workArea, fitted.Bounds);
            Assert.True(fitted.Maximize);
            Assert.Equal(workArea.Width, fitted.MinWidth);
            Assert.Equal(workArea.Height, fitted.MinHeight);
        }

        [Fact]
        public void OnAScreenLargeEnoughTheMinimumIsTheOneTheWindowIsLaidOutWith()
        {
            foreach (var screen in new[] { Screen1920, Screen1280, Screen1366, Screen1024 })
            {
                var fitted = Fit(new WindowBounds(10, 10, 1000, 650), screen);

                Assert.Equal(MinWidth, fitted.MinWidth);
                Assert.Equal(MinHeight, fitted.MinHeight);
            }
        }

        /// <summary>
        /// Shrunk on a small screen, then back on a large one: the minimum returns, WPF brings
        /// the window up to it, and it is at that size that the window is kept on the screen.
        /// </summary>
        [Fact]
        public void AWindowShrunkBelowTheMinimumIsBroughtUpToItWithinTheScreen()
        {
            var fitted = Fit(new WindowBounds(1000, 500, 819, 582), Screen1280);

            Assert.Equal(new WindowBounds(320, 384, MinWidth, MinHeight), fitted.Bounds);
            Assert.False(fitted.Maximize);
            Assert.Equal(MinWidth, fitted.MinWidth);
        }

        /// <summary>
        /// A window sized by hand to fill the screen comes back from pixels a fraction off the
        /// work area. That is not a window that does not fit, and it is not to be maximized for it.
        /// </summary>
        [Fact]
        public void AFractionOfAPixelPastTheEdgeStillFits()
        {
            var window = new WindowBounds(-0.4, 0, 1366.6, 728.5);

            var fitted = Fit(window, Screen1366);

            Assert.Equal(window, fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        /// <summary>
        /// What has been fitted fits: fitting it again changes nothing and never maximizes, so a
        /// window left maximized can be put back to its normal size and stay there.
        /// </summary>
        [Theory]
        [InlineData(300, 100, 1500, 900, 1280, 984)]
        [InlineData(300, 100, 1500, 900, 1024, 728)]
        [InlineData(43, -46, 1280, 820, 1366, 728)]
        [InlineData(2500, 100, 1000, 700, 1920, 1040)]
        [InlineData(-500, 900, 800, 700, 1280, 984)]
        [InlineData(0, 0, 1280, 820, 819.2, 582.4)]
        [InlineData(50, 50, 1280, 820, 682.666, 485.333)]
        public void FittingAgainChangesNothing(double left, double top, double width, double height, double screenWidth, double screenHeight)
        {
            var screen = new WindowBounds(0, 0, screenWidth, screenHeight);
            var once = Fit(new WindowBounds(left, top, width, height), screen);

            var twice = Fit(once.Bounds, screen);

            Assert.Equal(once.Bounds, twice.Bounds);
            Assert.False(twice.Maximize);
        }

        /// <summary>A window with no size of its own to fall back on is never maximized for it.</summary>
        [Fact]
        public void NoDesignedSizeNeverMaximizes()
        {
            var fitted = WindowFit.Fit(new WindowBounds(300, 100, 1500, 900), Screen1024, MinWidth, MinHeight, double.NaN, double.NaN);

            Assert.Equal(new WindowBounds(0, 0, 1024, 728), fitted.Bounds);
            Assert.False(fitted.Maximize);
        }

        [Fact]
        public void PixelsAreConvertedAtTheWindowsScale()
        {
            var rect = new NativeMethods.RECT { Left = -2880, Top = 60, Right = 0, Bottom = 1620 };

            Assert.Equal(new WindowBounds(-1920, 40, 1920, 1040), WindowFit.FromPixels(rect, 1.5, 1.5));
            Assert.Equal(new WindowBounds(-2880, 60, 2880, 1560), WindowFit.FromPixels(rect, 1, 1));
            Assert.Equal(new WindowBounds(-2304, 48, 2304, 1248), WindowFit.FromPixels(rect, 1.25, 1.25));
        }

        private static FittedWindow Fit(WindowBounds window, WindowBounds workArea) =>
            WindowFit.Fit(window, workArea, MinWidth, MinHeight, DesignWidth, DesignHeight);
    }
}
