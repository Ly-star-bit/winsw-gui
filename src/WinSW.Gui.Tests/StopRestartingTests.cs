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

        // Two consoles over one file -------------------------------------------------------

        /// <summary>
        /// Two consoles, an elevated one beside a standard one, each remembering a service: the
        /// second to write does not write the first one's out of the file with the copy it read
        /// before.
        /// </summary>
        [Fact]
        public void TwoConsolesKeepEachOthersStartTypes()
        {
            var first = new RememberedStartTypes(this.FilePath);
            var second = new RememberedStartTypes(this.FilePath);
            Assert.Null(second.For("web"));

            first.Remember("web", "auto");
            second.Remember("api", "demand");

            var later = new RememberedStartTypes(this.FilePath);
            Assert.Equal("auto", later.For("web"));
            Assert.Equal("demand", later.For("api"));

            // The second console knows of the first one's from its own change on.
            Assert.Equal("auto", second.For("web"));
        }

        /// <summary>Nor does it put back what the other has forgotten since it read the file.</summary>
        [Fact]
        public void WhatAnotherConsoleForgotStaysForgotten()
        {
            var first = new RememberedStartTypes(this.FilePath);
            first.Remember("web", "auto");
            var second = new RememberedStartTypes(this.FilePath);
            Assert.Equal("auto", second.For("web"));

            first.Forget("web");
            second.Remember("api", "demand");

            var later = new RememberedStartTypes(this.FilePath);
            Assert.Null(later.For("web"));
            Assert.Equal("demand", later.For("api"));
        }

        /// <summary>Forgetting what another console remembered after this one read the file forgets it in the file.</summary>
        [Fact]
        public void WhatAnotherConsoleRememberedCanBeForgottenHere()
        {
            var first = new RememberedStartTypes(this.FilePath);
            Assert.Null(first.For("web"));
            new RememberedStartTypes(this.FilePath).Remember("web", "auto");

            first.Forget("web");

            Assert.Null(new RememberedStartTypes(this.FilePath).For("web"));
        }

        /// <summary>Written beside the file and moved over it: nothing is left beside it afterwards.</summary>
        [Fact]
        public void NothingIsLeftBesideTheFile()
        {
            var memory = new RememberedStartTypes(this.FilePath);
            memory.Remember("web", "auto");
            memory.Remember("api", "demand");
            memory.Forget("web");

            Assert.Equal(new[] { this.FilePath }, Directory.GetFiles(this.directory));
        }

        /// <summary>
        /// A change that could not be written is kept for the session, and goes into the file with
        /// the next change rather than being lost when the file is read again for it.
        /// </summary>
        [Fact]
        public void AChangeThatCouldNotBeWrittenIsWrittenWithTheNext()
        {
            // A file where the directory should be: nothing can be written under it.
            File.WriteAllText(this.directory, string.Empty);
            var memory = new RememberedStartTypes(this.FilePath);
            memory.Remember("web", "auto");
            Assert.Equal("auto", memory.For("web"));

            File.Delete(this.directory);
            memory.Remember("api", "demand");

            var later = new RememberedStartTypes(this.FilePath);
            Assert.Equal("auto", later.For("web"));
            Assert.Equal("demand", later.For("api"));
        }
    }
}
