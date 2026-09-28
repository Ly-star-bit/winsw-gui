using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The review step checks the new service against this machine, off the UI thread: every
    /// service's names, what <c>ValidateEnvironment</c> finds in the configuration, and whether
    /// the wrapper is a .NET Framework build the machine cannot run. Localized text reads back
    /// as its key here, as everywhere in these tests.
    /// </summary>
    [Collection("install root")]
    public sealed class WizardMachineCheckTests : IDisposable
    {
        private static readonly ServiceNames Machine = new(new[]
        {
            new InstalledService("Spooler", "Print Spooler"),
            new InstalledService("nginx-svc", "nginx"),
        });

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-machine-" + Guid.NewGuid().ToString("N"));

        public WizardMachineCheckTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose()
        {
            Directory.Delete(this.directory, recursive: true);
        }

        /// <summary>The review step reads the machine again, and swaps in what the newer reading says.</summary>
        [Fact]
        public async Task TheReviewStepNamesTheServiceInTheWay()
        {
            var wizard = this.Wizard(() => Machine);
            wizard.ServiceId = "spooler";

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.IdTaken", wizard.Problems);
            Assert.True(wizard.IdInUse);
        }

        [Theory]
        [InlineData("api", "nginx", "M.Wiz.DisplayNameTaken")]
        [InlineData("api", "spooler", "M.Wiz.DisplayNameTaken")]
        [InlineData("nginx", "nginx", "M.Wiz.DisplayNameTaken")]
        public async Task ADisplayNameClashingIsAProblem(string id, string displayName, string problem)
        {
            var wizard = this.Wizard(() => Machine);
            wizard.ServiceId = id;
            wizard.DisplayName = displayName;

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains(problem, wizard.Problems);
        }

        /// <summary>
        /// An ID another service shows as its display name is no refusal Windows documents: it is
        /// warned about with the machine's findings, and holds nothing up.
        /// </summary>
        [Fact]
        public async Task AnIdAnotherServiceShowsIsAWarning()
        {
            var wizard = this.Wizard(() => Machine);
            wizard.ServiceId = "nginx";
            wizard.DisplayName = "My nginx";

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.IdIsDisplayName", wizard.EnvironmentWarnings);
            Assert.DoesNotContain("M.Wiz.IdIsDisplayName", wizard.Problems);
            Assert.False(wizard.IdInUse);
        }

        /// <summary>A service uninstalled since the last reading no longer stands in the way once the step is reopened.</summary>
        [Fact]
        public async Task AClashThatIsGoneIsNoLongerListed()
        {
            int reads = 0;
            var wizard = this.Wizard(() => Interlocked.Increment(ref reads) == 1 ? Machine : ServiceNames.None);
            wizard.ServiceId = "spooler";

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;
            Assert.Contains("M.Wiz.IdTaken", wizard.Problems);

            wizard.Step = 3;
            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.DoesNotContain("M.Wiz.IdTaken", wizard.Problems);
            Assert.False(wizard.IdInUse);
        }

        /// <summary>What the machine says is kept apart from the warnings listed as the step opens.</summary>
        [Fact]
        public async Task TheEnvironmentsFindingsArriveWithTheWarnings()
        {
            var wizard = this.Wizard(() => ServiceNames.None, Path.Combine(this.directory, "missing", "server.exe"));

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Warn.ExecutableMissing", wizard.EnvironmentWarnings);
            Assert.DoesNotContain("M.Warn.ExecutableMissing", wizard.Warnings);
            Assert.True(wizard.HasWarnings);
        }

        /// <summary>
        /// The service's own folder does not exist before the install, and the wrapper makes its
        /// log directory on the first start: that is no finding. A log directory elsewhere that
        /// is missing still is, as it is in the editor.
        /// </summary>
        /// <remarks>
        /// Asked of a machine made up for the purpose, which has the program and no folders: the
        /// real one, asked about a program that is there, reads where the user profiles are from
        /// the registry, which only Windows has, and a check that cannot finish says nothing.
        /// </remarks>
        [Fact]
        public async Task ALogDirectoryInTheNewFolderIsNotReportedMissing()
        {
            // The wizard's own default, %BASE%\logs, spelled with this system's separator.
            var machine = new FakeServiceMachine();
            machine.Files.Add(Path.Combine(this.directory, "server.exe"));
            var wizard = this.Wizard(() => ServiceNames.None, machine: machine);
            wizard.LogPath = Path.Combine("%BASE%", "logs");

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;
            Assert.DoesNotContain("M.Warn.LogDirectoryMissing", wizard.EnvironmentWarnings);

            wizard.Step = 3;
            wizard.LogPath = Path.Combine(this.directory, "elsewhere", "logs");
            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;
            Assert.Contains("M.Warn.LogDirectoryMissing", wizard.EnvironmentWarnings);
        }

        /// <summary>
        /// A desktop task runs as the user who registers it, in their session: what the check
        /// says of a service running as LocalSystem — a program in a user's profile — is not said
        /// of it.
        /// </summary>
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public async Task WhatOnlyAServiceMeetsIsNotSaidOfADesktopTask(bool desktopTask, bool said)
        {
            const string program = @"C:\Users\ops\apps\robot.exe";
            var machine = new FakeServiceMachine();
            machine.Files.Add(program);
            var wizard = this.Wizard(() => ServiceNames.None, program, machine: machine);
            wizard.DesktopTask = desktopTask;

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Equal(said, wizard.EnvironmentWarnings.Contains("M.Warn.UserProfile"));
        }

        [Theory]
        [InlineData("M.Warn.UserProfile", false, true)]
        [InlineData("M.Warn.OnUserPathOnly", false, true)]
        [InlineData("M.Warn.NetworkShare", true, true)]
        [InlineData("M.Warn.MappedDrive", false, true)]
        [InlineData("M.Warn.MappedDrive", true, false)]
        [InlineData("M.Warn.WrapperOnMappedDrive", true, false)]
        [InlineData("M.Warn.AccountUnknown", false, true)]
        [InlineData("M.Warn.DriverStartMode", true, true)]
        [InlineData("M.Warn.ExecutableMissing", false, false)]
        [InlineData("M.Warn.OnMachinePath", false, false)]
        public void FindingsAboutAnotherAccountOrSignInAreAServicesAlone(string key, bool elevated, bool serviceOnly)
        {
            Assert.Equal(serviceOnly, WizardViewModel.OnlyForAService(key, elevated));
        }

        [Fact]
        public async Task TheBundledWrapperOnAnOldFrameworkIsReported()
        {
            var wizard = this.Wizard(() => ServiceNames.None, framework: new NetFrameworkInfo(378675));
            wizard.UseBundledWrapper = true;

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.NetFxWrapper", wizard.EnvironmentWarnings);
        }

        /// <summary>A picked wrapper of a few hundred kilobytes is upstream's .NET Framework build, with the same need.</summary>
        [Fact]
        public async Task APickedFrameworkBuildOnAnOldFrameworkIsReported()
        {
            string wrapper = Path.Combine(this.directory, "WinSW.exe");
            File.WriteAllBytes(wrapper, new byte[1024]);
            var wizard = this.Wizard(() => ServiceNames.None, framework: new NetFrameworkInfo(378675));
            wizard.UseBundledWrapper = false;
            wizard.WrapperPath = wrapper;

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.NetFxWrapper", wizard.EnvironmentWarnings);
        }

        [Fact]
        public async Task TheBundledWrapperOnACurrentFrameworkIsNot()
        {
            var wizard = this.Wizard(() => ServiceNames.None, framework: new NetFrameworkInfo(528040));
            wizard.UseBundledWrapper = true;

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.DoesNotContain("M.Wiz.NetFxWrapper", wizard.EnvironmentWarnings);
        }

        private WizardViewModel Wizard(Func<ServiceNames> services, string? program = null, NetFrameworkInfo framework = default, FakeServiceMachine? machine = null)
        {
            string target = program ?? Path.Combine(this.directory, "server.exe");
            if (program is null)
            {
                File.WriteAllBytes(target, Array.Empty<byte>());
            }

            return new WizardViewModel(framework, services, machine) { TargetPath = target, ServiceId = "api", DisplayName = "API" };
        }
    }
}
