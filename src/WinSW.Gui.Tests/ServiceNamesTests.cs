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
    /// The wizard's ID and display name are checked against every service on the machine, not
    /// only the ones WinSW hosts. Windows refuses a name another service has with 1073, and a
    /// display name another service has as its name or display name with 1078 — after the UAC
    /// prompt, with the configuration already written. An ID another service shows as its
    /// display name is not a documented refusal, and is only warned about.
    /// </summary>
    [Collection("install root")]
    public class ServiceNamesTests
    {
        private static readonly ServiceNames Machine = new(new[]
        {
            new InstalledService("Spooler", "Print Spooler"),
            new InstalledService("nginx-svc", "nginx"),
            new InstalledService("api", "Orders API"),
        });

        [Theory]
        [InlineData("Spooler")]
        [InlineData("spooler")]
        [InlineData(" SPOOLER ")]
        public void AnIdIsTakenByAnothersName(string id)
        {
            Assert.Equal("Spooler", Machine.ClashWithId(id)?.Name);
        }

        /// <summary>Another service's display name is no documented refusal for an ID: it is found, but as shown, not taken.</summary>
        [Theory]
        [InlineData("Print Spooler", "Spooler")]
        [InlineData("NGINX", "nginx-svc")]
        public void AnIdAnotherServiceShowsIsNotTaken(string id, string shower)
        {
            Assert.Null(Machine.ClashWithId(id));
            Assert.Equal(shower, Machine.ShownAs(id)?.Name);
        }

        [Theory]
        [InlineData("orders")]
        [InlineData("Spooler2")]
        [InlineData("")]
        [InlineData("  ")]
        public void AFreeIdClashesWithNothing(string id)
        {
            Assert.Null(Machine.ClashWithId(id));
        }

        /// <summary>A service named like the text is its name's clash, not also one that shows it.</summary>
        [Fact]
        public void AServiceNamedLikeItIsNotCountedAsShowingIt()
        {
            var names = new ServiceNames(new[]
            {
                new InstalledService("api", "api"),
                new InstalledService("legacy", "API"),
            });

            Assert.Equal("api", names.ClashWithId("API")?.Name);
            Assert.Equal("legacy", names.ShownAs("API")?.Name);
            Assert.Null(new ServiceNames(new[] { new InstalledService("api", "api") }).ShownAs("api"));
        }

        [Theory]
        [InlineData("print spooler", "orders", "Spooler")]
        [InlineData("Spooler", "orders", "Spooler")]
        [InlineData("  Orders API ", "orders", "api")]
        public void ADisplayNameIsTakenByAnothersNameOrDisplayName(string displayName, string id, string taker)
        {
            Assert.Equal(taker, Machine.ClashWithDisplayName(displayName, id)?.Name);
        }

        /// <summary>
        /// A blank display name is the ID's check, and so is a service named like a display name
        /// that is the ID over again: that clash is reported once, as the ID's.
        /// </summary>
        [Theory]
        [InlineData(null, "Spooler")]
        [InlineData("", "Spooler")]
        [InlineData(" ", "Spooler")]
        [InlineData("spooler", "Spooler")]
        [InlineData("Orders", "orders")]
        public void ADisplayNameThatIsTheIdLeavesNamesToTheIdCheck(string? displayName, string id)
        {
            Assert.Null(Machine.ClashWithDisplayName(displayName, id));
        }

        /// <summary>A display name that is the ID is still a display name, and may not be another service's.</summary>
        [Fact]
        public void ADisplayNameThatIsTheIdMayNotBeAnothersDisplayName()
        {
            Assert.Equal("nginx-svc", Machine.ClashWithDisplayName("nginx", "NGINX")?.Name);
        }

        [Theory]
        [InlineData("Spooler", "Print Spooler", "Print Spooler (Spooler)")]
        [InlineData("api", "API", "api")]
        [InlineData("api", "", "api")]
        public void AServiceIsNamedByBothItsNames(string name, string displayName, string label)
        {
            Assert.Equal(label, new InstalledService(name, displayName).Label);
        }

        /// <summary>Every Windows machine has services; anywhere else the reading is empty, and that is no error.</summary>
        [Fact]
        public void ReadingTheMachineNeverThrows()
        {
            var names = ServiceNames.Read();

            Assert.Equal(OperatingSystem.IsWindows(), names.Count > 0);
        }

        [Fact]
        public void MoreCanBeAddedToAReading()
        {
            var both = ServiceNames.None.With(new[] { new InstalledService("api", "Orders API") });

            Assert.Equal(1, both.Count);
            Assert.Equal("api", both.ClashWithId("API")?.Name);
            Assert.Equal("api", both.ShownAs("orders api")?.Name);
        }

        /// <summary>A service WinSW does not host holds step 2 once the machine has been read.</summary>
        [Fact]
        public async Task Step2IsCheckedAgainstEveryServiceOnTheMachine()
        {
            var wizard = new WizardViewModel(NetFrameworkInfo.Unknown, () => Machine) { TargetPath = @"C:\apps\spooler.exe" };

            wizard.Step = 2;
            await wizard.MachineCheck;

            wizard.ServiceId = "spooler";
            wizard.DisplayName = "My spooler";
            Assert.True(wizard.IdInUse);
            Assert.False(wizard.DisplayNameInUse);
            Assert.False(wizard.NextCommand.CanExecute(null));

            wizard.ServiceId = "Print-Spooler";
            wizard.DisplayName = "Print Spooler";
            Assert.False(wizard.IdInUse);
            Assert.True(wizard.DisplayNameInUse);

            // Another service's display name as the ID holds nothing up; the review step warns.
            wizard.ServiceId = "nginx";
            wizard.DisplayName = "My nginx";
            Assert.False(wizard.IdInUse);
            Assert.False(wizard.DisplayNameInUse);
            Assert.True(wizard.NextCommand.CanExecute(null));

            wizard.ServiceId = "orders";
            wizard.DisplayName = "nginx";
            Assert.False(wizard.IdInUse);
            Assert.True(wizard.DisplayNameInUse);
            Assert.False(wizard.NextCommand.CanExecute(null));

            wizard.DisplayName = "Orders";
            Assert.False(wizard.DisplayNameInUse);
            Assert.True(wizard.NextCommand.CanExecute(null));
        }

        /// <summary>Before the first reading comes back, the dashboard's WinSW services are still checked, by both names.</summary>
        [Fact]
        public void TheDashboardsServicesAreCheckedBeforeTheMachineIsRead()
        {
            var wizard = new WizardViewModel(NetFrameworkInfo.Unknown, () => ServiceNames.None)
            {
                Sources = new[] { new ServiceEntry("api", "Orders API", Path.Combine(Path.GetTempPath(), "WinSW.exe"), null) },
                ServiceId = "API",
            };

            Assert.True(wizard.IdInUse);

            wizard.ServiceId = "orders";
            wizard.DisplayName = "orders api";
            Assert.True(wizard.DisplayNameInUse);
        }

        /// <summary>A desktop task is no service: only task names can stand in its way.</summary>
        [Fact]
        public async Task ADesktopTaskIsNotHeldByServices()
        {
            var wizard = new WizardViewModel(NetFrameworkInfo.Unknown, () => Machine) { DesktopTask = true };

            wizard.Step = 2;
            await wizard.MachineCheck;
            wizard.ServiceId = "Spooler";
            wizard.DisplayName = "Print Spooler";

            Assert.False(wizard.IdInUse);
            Assert.False(wizard.DisplayNameInUse);
        }

        /// <summary>Each visit to step 2 reads the machine again: a service installed meanwhile takes its names with it.</summary>
        [Fact]
        public async Task EveryVisitToStep2ReadsTheMachineAgain()
        {
            int reads = 0;
            var wizard = new WizardViewModel(NetFrameworkInfo.Unknown, () => Interlocked.Increment(ref reads) == 1 ? ServiceNames.None : Machine)
            {
                ServiceId = "spooler",
            };

            wizard.Step = 2;
            await wizard.MachineCheck;
            Assert.False(wizard.IdInUse);

            wizard.Step = 1;
            wizard.Step = 2;
            await wizard.MachineCheck;
            Assert.True(wizard.IdInUse);
            Assert.Equal(2, reads);
        }

        /// <summary>A reading overtaken by a later one is dropped, however late it comes back.</summary>
        [Fact]
        public async Task AnOvertakenReadingIsDropped()
        {
            using var slow = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            int reads = 0;
            var wizard = new WizardViewModel(NetFrameworkInfo.Unknown, () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    started.Set();
                    slow.Wait(TimeSpan.FromSeconds(10));
                    return Machine;
                }

                return ServiceNames.None;
            })
            {
                ServiceId = "spooler",
            };

            wizard.Step = 2;
            var first = wizard.MachineCheck;

            // Both readings run on the thread pool, so the second could otherwise be the one
            // that reads first — and be the slow one — on a busy machine.
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            wizard.Step = 1;
            wizard.Step = 2;
            await wizard.MachineCheck;

            slow.Set();
            await first;

            Assert.False(wizard.IdInUse);
        }
    }
}
