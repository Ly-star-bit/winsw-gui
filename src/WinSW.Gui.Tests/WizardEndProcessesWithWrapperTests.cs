using System;
using System.IO;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A service the wizard creates ends its processes with its wrapper
    /// (<c>&lt;endProcessesWithWrapper&gt;</c>), so that a wrapper that crashes or is ended
    /// leaves nothing running on the port; a copy says what its source said, and a desktop task,
    /// whose wrapper runs as a console, is left at the wrapper's default.
    /// </summary>
    public sealed class WizardEndProcessesWithWrapperTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-endprocs-" + Guid.NewGuid().ToString("N"));

        public WizardEndProcessesWithWrapperTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void ANewServiceEndsItsProcessesWithTheWrapper()
        {
            var wizard = Wizard();

            Assert.True(Written(wizard).EndProcessesWithWrapper);

            wizard.Step = WizardViewModel.LastStep;
            Assert.True(wizard.EndsProcessesWithWrapper);
        }

        [Fact]
        public void ADesktopTaskIsLeftAtTheWrappersDefault()
        {
            var wizard = Wizard();
            wizard.DesktopTask = true;

            Assert.False(Written(wizard).EndProcessesWithWrapper);

            wizard.Step = WizardViewModel.LastStep;
            Assert.False(wizard.EndsProcessesWithWrapper);
        }

        /// <summary>A copy is its source's configuration: on when the source had it, off when it did not.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACopyKeepsItsSourcesSetting(bool source)
        {
            string config = Path.Combine(this.directory, "api.xml");
            File.WriteAllText(
                config,
                "<service><id>api</id><executable>server.exe</executable>"
                + (source ? "<endProcessesWithWrapper>true</endProcessesWithWrapper>" : string.Empty)
                + "</service>");
            var wizard = new WizardViewModel(default, () => ServiceNames.None)
            {
                CloneSource = new ServiceEntry("api", "API", Path.Combine(this.directory, "WinSW.exe"), config),
            };

            Assert.Equal(source, Written(wizard).EndProcessesWithWrapper);

            wizard.Step = WizardViewModel.LastStep;
            Assert.Equal(source, wizard.EndsProcessesWithWrapper);
        }

        /// <summary>The model as the file will have it: written, and read back.</summary>
        private static ServiceConfigModel Written(WizardViewModel wizard) =>
            ServiceConfigModel.FromXml(wizard.BuildModel().ToXmlString(), null);

        private static WizardViewModel Wizard() =>
            new(default, () => ServiceNames.None) { TargetPath = @"C:\apps\api\server.exe", ServiceId = "api" };
    }
}
