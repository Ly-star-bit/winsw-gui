using WinSW.Gui.Views;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The editor's XML preview gives its column back to the form while it is hidden, and comes
    /// back at the width it was dragged to.
    /// </summary>
    public class PreviewPaneWidthTests
    {
        /// <summary>The form's minimum and the splitter with its margins, as the view has them.</summary>
        private const double FormMinimum = 420;

        private const double Splitter = 16;

        [Fact]
        public void HiddenTheColumnTakesNothing()
        {
            var width = new PreviewPaneWidth();

            Assert.Equal(0, width.Hide(PreviewPaneWidth.DefaultWidth));
        }

        [Fact]
        public void ShownBeforeAnyDragItHasItsDefaultWidth()
        {
            var width = new PreviewPaneWidth();

            Assert.Equal(PreviewPaneWidth.DefaultWidth, width.Show(PreviewPaneWidth.DefaultWidth));
            Assert.Equal(0, width.Hide(PreviewPaneWidth.DefaultWidth));
            Assert.Equal(PreviewPaneWidth.DefaultWidth, width.Show(0));
        }

        [Fact]
        public void TheDraggedWidthComesBack()
        {
            var width = new PreviewPaneWidth();

            width.Hide(612);

            Assert.Equal(612, width.Show(0));
        }

        /// <summary>
        /// The property can be raised with the preview already hidden, and the column is then at
        /// nothing: that must not replace the width worth going back to.
        /// </summary>
        [Fact]
        public void HidingTwiceKeepsTheDraggedWidth()
        {
            var width = new PreviewPaneWidth();

            width.Hide(612);
            width.Hide(0);

            Assert.Equal(612, width.Show(0));
        }

        /// <summary>A preview that is showing already keeps what the splitter has given it.</summary>
        [Fact]
        public void ShowingWhatIsShownChangesNothing()
        {
            var width = new PreviewPaneWidth();

            width.Hide(612);

            Assert.Equal(530, width.Show(530));
        }

        /// <summary>
        /// The splitter can drag the column shut. Bringing that back would leave the box ticked
        /// and nothing on screen, so the preview returns at its default width instead.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(40)]
        [InlineData(PreviewPaneWidth.MinimumUsefulWidth - 1)]
        public void ANarrowDragComesBackAtTheDefault(double dragged)
        {
            var width = new PreviewPaneWidth();

            width.Hide(dragged);

            Assert.Equal(PreviewPaneWidth.DefaultWidth, width.Show(0));
        }

        [Fact]
        public void ADragAtTheUsefulMinimumIsKept()
        {
            var width = new PreviewPaneWidth();

            width.Hide(PreviewPaneWidth.MinimumUsefulWidth);

            Assert.Equal(PreviewPaneWidth.MinimumUsefulWidth, width.Show(0));
        }

        /// <summary>
        /// 1024 px with the navigation rail open leaves the page about 736 px: a 420 px form and
        /// the splitter leave the preview 300, not the 420 that ran past the edge.
        /// </summary>
        [Fact]
        public void AtTenTwentyFourThePreviewFitsBesideTheForm()
        {
            Assert.Equal(300, PreviewPaneWidth.MaximumBeside(736, FormMinimum, Splitter));
        }

        /// <summary>At the window's default 1280 px the default width fits, and nothing changes.</summary>
        [Fact]
        public void AtTheDefaultWindowSizeTheDefaultWidthFits()
        {
            Assert.True(PreviewPaneWidth.MaximumBeside(992, FormMinimum, Splitter) >= PreviewPaneWidth.DefaultWidth);
        }

        [Fact]
        public void TooNarrowForTheFormLeavesThePreviewNothing()
        {
            Assert.Equal(0, PreviewPaneWidth.MaximumBeside(400, FormMinimum, Splitter));
        }
    }
}
