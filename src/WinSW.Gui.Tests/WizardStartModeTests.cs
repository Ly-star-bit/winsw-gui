using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The wizard's start mode and the start after installing. Windows refuses to start a
    /// Disabled service (1058); the wizard installed one and then started it, reported the
    /// install as failed, and failed again with 1073 when asked to try once more.
    /// </summary>
    public class WizardStartModeTests
    {
        /// <summary>Disabled unticks the box and greys it out; the install goes by the box.</summary>
        [Fact]
        public void ADisabledServiceIsInstalledWithoutBeingStarted()
        {
            var wizard = Wizard();
            Assert.True(wizard.StartServiceAfterInstall);

            wizard.StartMode = "Disabled";

            Assert.False(wizard.StartServiceAfterInstall);
            Assert.False(wizard.CanStartServiceAfterInstall);
        }

        /// <summary>The choice made before Disabled was picked is still there once another mode is.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LeavingDisabledShowsTheChoiceAgain(bool start)
        {
            var wizard = Wizard();
            wizard.StartServiceAfterInstall = start;

            wizard.StartMode = "Disabled";
            wizard.StartMode = "Manual";

            Assert.True(wizard.CanStartServiceAfterInstall);
            Assert.Equal(start, wizard.StartServiceAfterInstall);
        }

        /// <summary>The greyed-out box cannot be ticked from behind either.</summary>
        [Fact]
        public void TheBoxCannotBeTickedWhileDisabled()
        {
            var wizard = Wizard();
            wizard.StartMode = "Disabled";

            wizard.StartServiceAfterInstall = true;

            Assert.False(wizard.StartServiceAfterInstall);
        }

        /// <summary>The picker writes null back when its list lacks the mode shown; the mode stays.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void ABlankWriteBackKeepsTheMode(string? blank)
        {
            var wizard = Wizard();
            wizard.StartMode = "Manual";

            wizard.StartMode = blank!;

            Assert.Equal("Manual", wizard.StartMode);
            Assert.Equal("Manual", wizard.BuildModel().StartMode);
        }

        private static WizardViewModel Wizard() =>
            new(default, () => ServiceNames.None) { TargetPath = @"C:\apps\api\server.exe", ServiceId = "api" };
    }
}
