using System;
using System.IO;
using System.ServiceProcess;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// "Stop restarting": the sc.exe steps that disable a service, and the start type it had,
    /// remembered so that it can be put back.
    /// </summary>
    public sealed class StopRestartingTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        private string FilePath => Path.Combine(this.directory, "start-types.json");

        public void Dispose()
        {
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }

        // The command ----------------------------------------------------------------------

        [Fact]
        public void DisablingIsOneStepAndItsExitCodeIsTheScripts()
        {
            var steps = WinSwCli.StartTypeSteps("demo", "disabled");

            Assert.Equal(new[] { "sc.exe config \"demo\" start= disabled" }, steps);
        }

        /// <summary>
        /// Nothing is asked to stop, whatever state the service is in: the service control manager
        /// refuses a stop to a service still starting, so a stop step would only fail unseen. The
        /// name is quoted, spaces and all.
        /// </summary>
        [Fact]
        public void NothingIsStoppedAndTheNameIsQuoted()
        {
            var steps = WinSwCli.StartTypeSteps("demo api", "disabled");

            Assert.Equal(new[] { "sc.exe config \"demo api\" start= disabled" }, steps);
        }

        [Theory]
        [InlineData("auto")]
        [InlineData("delayed-auto")]
        [InlineData("demand")]
        public void ARememberedStartTypeCanBePutBack(string startType)
        {
            Assert.Equal(new[] { $"sc.exe config \"demo\" start= {startType}" }, WinSwCli.StartTypeSteps("demo", startType));
        }

        [Theory]
        [InlineData("boot")]
        [InlineData("disabled & calc")]
        [InlineData("")]
        public void NoOtherStartTypeGoesOnTheCommandLine(string startType)
        {
            Assert.Null(WinSwCli.StartTypeSteps("demo", startType));
        }

        /// <summary>Quoted, cmd still expands a percent sign and ends at a quote.</summary>
        [Theory]
        [InlineData("demo\" & calc & \"")]
        [InlineData("%PATH%")]
        [InlineData("50%off")]
        [InlineData(" ")]
        public void ANameCmdWouldReadSomethingIntoIsRefused(string serviceName)
        {
            Assert.Null(WinSwCli.StartTypeSteps(serviceName, "disabled"));
        }

        // Remembering ----------------------------------------------------------------------

        [Theory]
        [InlineData(ServiceStartMode.Automatic, false, "auto")]
        [InlineData(ServiceStartMode.Automatic, true, "delayed-auto")]
        [InlineData(ServiceStartMode.Manual, false, "demand")]
        [InlineData(ServiceStartMode.Manual, true, "demand")]
        public void AStartTypeIsRememberedInScExesWords(ServiceStartMode startType, bool delayed, string token)
        {
            Assert.Equal(token, RememberedStartTypes.TokenFor(startType, delayed));
            Assert.Equal(startType, RememberedStartTypes.StartTypeOf(token)!.Value.StartType);
        }

        /// <summary>Already disabled, a service has nothing of its own to go back to; boot and system starts are drivers'.</summary>
        [Theory]
        [InlineData(ServiceStartMode.Disabled)]
        [InlineData(ServiceStartMode.Boot)]
        [InlineData(ServiceStartMode.System)]
        [InlineData(null)]
        public void OtherStartTypesAreNotRemembered(ServiceStartMode? startType)
        {
            Assert.Null(RememberedStartTypes.TokenFor(startType, false));
        }

        [Fact]
        public void ARememberedStartTypeOutlivesTheConsole()
        {
            new RememberedStartTypes(this.FilePath).Remember("Demo", "delayed-auto");

            var later = new RememberedStartTypes(this.FilePath);

            // Service names are not case-sensitive.
            Assert.Equal("delayed-auto", later.For("demo"));
            Assert.Null(later.For("other"));
        }

        [Fact]
        public void AForgottenStartTypeIsGone()
        {
            var memory = new RememberedStartTypes(this.FilePath);
            memory.Remember("demo", "auto");

            memory.Forget("DEMO");

            Assert.Null(memory.For("demo"));
            Assert.Null(new RememberedStartTypes(this.FilePath).For("demo"));
        }

        /// <summary>
        /// What is read back goes onto a command line that runs elevated, so a file edited by hand
        /// is read for the three start types alone.
        /// </summary>
        [Fact]
        public void OnlyTheThreeStartTypesAreReadBack()
        {
            Directory.CreateDirectory(this.directory);
            File.WriteAllText(this.FilePath, "{ \"good\": \"demand\", \"bad\": \"disabled & calc\", \"worse\": \"boot\" }");

            var memory = new RememberedStartTypes(this.FilePath);

            Assert.Equal("demand", memory.For("good"));
            Assert.Null(memory.For("bad"));
            Assert.Null(memory.For("worse"));
        }

        [Fact]
        public void AnUnreadableFileRemembersNothing()
        {
            Directory.CreateDirectory(this.directory);
            File.WriteAllText(this.FilePath, "not json");

            Assert.Null(new RememberedStartTypes(this.FilePath).For("demo"));
        }

        [Fact]
        public void OnlyAStartTypeToGoBackToIsRemembered()
        {
            var memory = new RememberedStartTypes(this.FilePath);

            Assert.Throws<ArgumentException>(() => memory.Remember("demo", "disabled"));
            Assert.Null(memory.For("demo"));
        }
    }
}
