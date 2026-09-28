using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The names a copy is suggested under. A copy is picked on step 1, before the machine has
    /// been read, so it was numbered against the dashboard's WinSW services alone and never
    /// against display names: a non-WinSW service called api-2, or another showing "API (2)",
    /// left step 2 red with nothing suggesting the next number.
    /// </summary>
    /// <remarks>Messages are compared by key, which is what the localizer hands back outside the application.</remarks>
    public sealed class WizardCloneNamesTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-copy-names-" + Guid.NewGuid().ToString("N"));

        /// <summary>Holds the machine's services back until a test lets them through.</summary>
        private readonly ManualResetEventSlim released = new(false);

        public WizardCloneNamesTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose()
        {
            // Opened rather than disposed: a reading nobody waited for may still be on its way in.
            this.released.Set();
            Directory.Delete(this.directory, recursive: true);
        }

        /// <summary>A service that is not WinSW's is not on the dashboard; the reading taken when the copy is picked finds it.</summary>
        [Fact]
        public async Task AnIdOnlyTheMachineKnowsMovesTheCopyOn()
        {
            var wizard = this.Wizard(new InstalledService("api-2", "Something else"));
            this.released.Set();

            wizard.CloneSource = this.Source();
            await wizard.MachineCheck;

            Assert.Equal("api-3", wizard.ServiceId);
            Assert.Equal("API (3)", wizard.DisplayName);
            Assert.False(wizard.IdInUse);
            Assert.False(wizard.DisplayNameInUse);
        }

        /// <summary>Windows refuses a display name another service shows (1078), so that number is skipped too.</summary>
        [Fact]
        public async Task ADisplayNameAnotherServiceShowsMovesTheCopyOn()
        {
            var wizard = this.Wizard(new InstalledService("orders", "API (2)"));
            this.released.Set();

            wizard.CloneSource = this.Source();
            await wizard.MachineCheck;

            Assert.Equal("api-3", wizard.ServiceId);
            Assert.Equal("API (3)", wizard.DisplayName);
            Assert.False(wizard.DisplayNameInUse);
        }

        /// <summary>What the dashboard lists is known at once, display names included.</summary>
        [Fact]
        public void ADisplayNameTheDashboardListsIsSkippedAtOnce()
        {
            var wizard = this.Wizard();
            wizard.Sources = new[] { new ServiceEntry("orders", "API (2)", Path.Combine(this.directory, "WinSW.exe"), null) };

            wizard.CloneSource = this.Source();

            Assert.Equal("api-3", wizard.ServiceId);
            Assert.Equal("API (3)", wizard.DisplayName);
        }

        /// <summary>An ID typed before the reading came back stays as typed; the display name, still the suggestion, moves on.</summary>
        [Fact]
        public async Task ANameTypedWhileTheMachineIsReadIsKept()
        {
            var wizard = this.Wizard(new InstalledService("api-2", "Something else"));
            wizard.CloneSource = this.Source();
            Assert.Equal("api-2", wizard.ServiceId);

            wizard.ServiceId = "mine";
            this.released.Set();
            await wizard.MachineCheck;

            Assert.Equal("mine", wizard.ServiceId);
            Assert.Equal("API (3)", wizard.DisplayName);
        }

        /// <summary>
        /// The review step lists what is taken rather than change the names under the preview it
        /// shows; back on step 2, the suggestion moves on.
        /// </summary>
        [Fact]
        public async Task TheReviewStepReportsAndStep2Renumbers()
        {
            var wizard = this.Wizard(new InstalledService("api-2", "Something else"));
            wizard.CloneSource = this.Source();

            wizard.Step = WizardViewModel.LastStep;
            this.released.Set();
            await wizard.MachineCheck;

            Assert.Equal("api-2", wizard.ServiceId);
            Assert.Contains("M.Wiz.IdTaken", wizard.Problems);

            wizard.Step = 2;
            await wizard.MachineCheck;

            Assert.Equal("api-3", wizard.ServiceId);
            Assert.Equal("API (3)", wizard.DisplayName);
        }

        /// <summary>
        /// A wizard whose machine holds <paramref name="services"/>, read only once the test lets
        /// the reading through with <see cref="released"/>.
        /// </summary>
        private WizardViewModel Wizard(params InstalledService[] services)
        {
            var machine = new ServiceNames(services);
            return new WizardViewModel(NetFrameworkInfo.Unknown, () =>
            {
                this.released.Wait();
                return machine;
            });
        }

        private ServiceEntry Source()
        {
            string config = Path.Combine(this.directory, "api.xml");
            File.WriteAllText(config, "<service><id>api</id><name>API</name><executable>server.exe</executable></service>");
            return new ServiceEntry("api", "API", Path.Combine(this.directory, "WinSW.exe"), config);
        }
    }
}
