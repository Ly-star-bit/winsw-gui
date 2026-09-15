using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The classification is what the hook answers the window manager with; the hook itself
    /// needs a window and a message loop, which a test does not have.
    /// </summary>
    public class WindowResizeBorderTests
    {
        // A window at (100, 200) that is 800 by 600, with a 4-pixel border.
        private static readonly NativeMethods.RECT Window = new() { Left = 100, Top = 200, Right = 900, Bottom = 800 };

        [Theory]
        [InlineData(100, 200, NativeMethods.HTTOPLEFT)]
        [InlineData(103, 203, NativeMethods.HTTOPLEFT)]
        [InlineData(500, 200, NativeMethods.HTTOP)]
        [InlineData(500, 203, NativeMethods.HTTOP)]
        [InlineData(899, 200, NativeMethods.HTTOPRIGHT)]
        [InlineData(896, 203, NativeMethods.HTTOPRIGHT)]
        [InlineData(100, 500, NativeMethods.HTLEFT)]
        [InlineData(103, 500, NativeMethods.HTLEFT)]
        [InlineData(899, 500, NativeMethods.HTRIGHT)]
        [InlineData(896, 500, NativeMethods.HTRIGHT)]
        [InlineData(100, 799, NativeMethods.HTBOTTOMLEFT)]
        [InlineData(500, 799, NativeMethods.HTBOTTOM)]
        [InlineData(500, 796, NativeMethods.HTBOTTOM)]
        [InlineData(899, 799, NativeMethods.HTBOTTOMRIGHT)]
        public void TheBandInsideEachEdgeIsThatEdge(int x, int y, int expected)
        {
            Assert.Equal(expected, WindowResizeBorder.Classify(x, y, Window, 4, 4));
        }

        [Theory]
        [InlineData(104, 204)]
        [InlineData(500, 204)]
        [InlineData(895, 500)]
        [InlineData(500, 795)]
        [InlineData(500, 500)]
        public void InsideTheBandIsNowhere(int x, int y)
        {
            Assert.Equal(NativeMethods.HTNOWHERE, WindowResizeBorder.Classify(x, y, Window, 4, 4));
        }

        [Theory]
        [InlineData(99, 500)]
        [InlineData(900, 500)]
        [InlineData(500, 199)]
        [InlineData(500, 800)]
        public void OutsideTheWindowIsNowhere(int x, int y)
        {
            Assert.Equal(NativeMethods.HTNOWHERE, WindowResizeBorder.Classify(x, y, Window, 4, 4));
        }

        [Fact]
        public void ABorderOfNothingClaimsNothing()
        {
            Assert.Equal(NativeMethods.HTNOWHERE, WindowResizeBorder.Classify(100, 200, Window, 0, 0));
        }

        [Fact]
        public void TheBandScalesWithTheBorder()
        {
            Assert.Equal(NativeMethods.HTTOP, WindowResizeBorder.Classify(500, 207, Window, 8, 8));
            Assert.Equal(NativeMethods.HTNOWHERE, WindowResizeBorder.Classify(500, 207, Window, 4, 4));
        }
    }
}
