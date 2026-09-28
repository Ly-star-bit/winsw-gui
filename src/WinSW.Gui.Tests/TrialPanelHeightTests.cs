using WinSW.Gui.Views;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The editor's try-run panel keeps its full height where the page has room, and gives way
    /// on a small screen so the form above it keeps a share of the page.
    /// </summary>
    public class TrialPanelHeightTests
    {
        [Fact]
        public void ATallPageGivesTheFullHeight()
        {
            Assert.Equal(TrialPanelHeight.Full, TrialPanelHeight.For(900));
        }

        [Fact]
        public void A1024By768PageGivesTheFormTheRest()
        {
            // About 530 px of body at 1024×768 and 100 %.
            double height = TrialPanelHeight.For(530);

            Assert.True(height < TrialPanelHeight.Full);
            Assert.Equal(530 * TrialPanelHeight.ShareOfBody, height);
        }

        [Fact]
        public void ASmallPageStopsAtTheMinimum()
        {
            Assert.Equal(TrialPanelHeight.Minimum, TrialPanelHeight.For(300));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(double.NaN)]
        public void ABodyNotYetMeasuredGivesTheFullHeight(double body)
        {
            Assert.Equal(TrialPanelHeight.Full, TrialPanelHeight.For(body));
        }
    }
}
