using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The check before a configuration runs — the wizard's review step, the editor's Install and
    /// Try run — for what already listens on the ports it names, over a port table and a process
    /// snapshot built by hand. Localized text reads back as its key here.
    /// </summary>
    [Collection("install root")]
    public class PortCheckTests
    {
        private const int Console = 900;

        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);

        // The ports a configuration names ---------------------------------------------------

        [Fact]
        public void ThePortsComeFromTheArgumentsAndTheVariables()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Arguments = "main:app --host 0.0.0.0 --port 8000";
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "ADMIN_PORT", Value = "9001" });

            Assert.Equal(new[] { 8000, 9001 }, PortCheck.PortsOf(model));
        }

        /// <summary>The wrapper starts the program with the start arguments when there are any, and with those alone.</summary>
        [Fact]
        public void StartArgumentsTakeThePlaceOfTheArguments()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Arguments = "--port 8000";
            model.StartArguments = "--port 9000";

            Assert.Equal(new[] { 9000 }, PortCheck.PortsOf(model));

            model.StartArguments = " ";
            Assert.Equal(new[] { 8000 }, PortCheck.PortsOf(model));
        }

        /// <summary>A configuration that names no port is not looked into: nothing of the machine is read.</summary>
        [Fact]
        public void NothingIsReadWhenNoPortIsNamed()
        {
            Assert.Empty(PortCheck.Find(Array.Empty<int>(), consoleProcessId: null));
        }

        // Who holds them --------------------------------------------------------------------

        [Fact]
        public void AHolderIsNamedWithItsPid()
        {
            var snapshot = Snapshot(P(3000, 1, "cmd.exe"), P(4312, 3000, "python.exe"));

            var held = PortCheck.Holders(new[] { 8000, 8001 }, Table((8000, 4312)), snapshot, consoleProcessId: null);

            Assert.Equal(new[] { new PortInUse(8000, "python.exe", 4312) }, held);
        }

        /// <summary>
        /// Another service's program is in the way as much as anyone's: unlike the dashboard's
        /// banner, which is about one service, this names it.
        /// </summary>
        [Fact]
        public void AnotherServicesProgramIsNamedToo()
        {
            var snapshot = Snapshot(P(20, 1, "WinSW.exe"), P(4312, 20, "python.exe"));

            var held = PortCheck.Holders(new[] { 8000 }, Table((8000, 4312)), snapshot, consoleProcessId: null);

            Assert.Equal(4312, Assert.Single(held).ProcessId);
        }

        /// <summary>HTTP.sys listens in System's name; that is who holds the port.</summary>
        [Fact]
        public void SystemIsNamedAndTheIdleProcessIsNot()
        {
            var snapshot = Snapshot(P(0, 0, "Idle"), P(4, 0, "System"));

            var held = PortCheck.Holders(new[] { 80, 81 }, Table((80, 4), (81, 0)), snapshot, consoleProcessId: null);

            Assert.Equal(new[] { new PortInUse(80, "System", 4) }, held);
        }

        /// <summary>Gone from the snapshot, taken after the table: exited meanwhile, with its socket.</summary>
        [Fact]
        public void AHolderThatHasExitedIsNotNamed()
        {
            Assert.Empty(PortCheck.Holders(new[] { 8000 }, Table((8000, 4312)), Snapshot(P(3000, 1, "cmd.exe")), consoleProcessId: null));
        }

        /// <summary>
        /// Before Install, this console's try run does not count: it has been asked to end. Before
        /// a try run, or on the wizard's review step, everything does.
        /// </summary>
        [Fact]
        public void TheConsolesTryRunCountsOnlyWhenAskedTo()
        {
            var snapshot = Snapshot(P(Console, 1, "WinSW.Gui.exe"), P(4312, Console, "cmd.exe"), P(4400, 4312, "python.exe"));
            var table = Table((8000, 4400));

            Assert.Empty(PortCheck.Holders(new[] { 8000 }, table, snapshot, Console));
            Assert.Equal(4400, Assert.Single(PortCheck.Holders(new[] { 8000 }, table, snapshot, consoleProcessId: null)).ProcessId);
        }

        /// <summary>
        /// Before a try run of an installed service's file, that service's own program, running,
        /// is named as the service's: it is not a process to end but a service to stop. Anything
        /// else holding a port, another service's program included, is named as before.
        /// </summary>
        [Fact]
        public void TheServicesOwnProgramIsNamedAsTheService()
        {
            var snapshot = Snapshot(
                P(20, 1, "WinSW.exe"),
                P(30, 20, "cmd.exe"),
                P(4312, 30, "python.exe"),
                P(40, 1, "WinSW.exe"),
                P(4400, 40, "python.exe"));
            var table = Table((8000, 4312), (8001, 4400));

            var held = PortCheck.Holders(new[] { 8000, 8001 }, table, snapshot, consoleProcessId: null, service: ("api", 20));

            Assert.Equal(
                new[] { new PortInUse(8000, "python.exe", 4312, "api"), new PortInUse(8001, "python.exe", 4400) },
                held);

            // Asked about no service, or about one whose wrapper is not above the holder.
            Assert.All(PortCheck.Holders(new[] { 8000 }, table, snapshot, consoleProcessId: null), p => Assert.Null(p.Service));
            Assert.All(PortCheck.Holders(new[] { 8000 }, table, snapshot, consoleProcessId: null, service: ("api", 40)), p => Assert.Null(p.Service));
        }

        /// <summary>
        /// A wrapper whose PID now belongs to a process started after the holder is not its
        /// ancestor: the service restarted, and the ID came round again.
        /// </summary>
        [Fact]
        public void AReusedWrapperPidIsNotTheServices()
        {
            // The holder's parent chain stops at 20, which started after it.
            var snapshot = Snapshot(P(4312, 20, "python.exe"), new ProcessRecord(20, 1, "WinSW.exe", T0.AddHours(1), TimeSpan.Zero, 0, 0));

            var held = PortCheck.Holders(new[] { 8000 }, Table((8000, 4312)), snapshot, consoleProcessId: null, service: ("api", 20));

            Assert.Null(Assert.Single(held).Service);
        }

        /// <summary>Every process listening on the port is named, port by port, lowest PID first.</summary>
        [Fact]
        public void EveryHolderIsNamedInOrder()
        {
            var snapshot = Snapshot(P(500, 1, "a.exe"), P(600, 1, "b.exe"), P(700, 1, "c.exe"));

            var held = PortCheck.Holders(new[] { 9000, 8000 }, Table((8000, 600), (8000, 500), (9000, 700)), snapshot, consoleProcessId: null);

            Assert.Equal(new[] { 700, 500, 600 }, held.Select(p => p.ProcessId));
        }

        // On the wizard's review step --------------------------------------------------------

        /// <summary>
        /// What holds a port the new program is told to use is a warning with the machine's
        /// findings, and holds nothing up.
        /// </summary>
        [Fact]
        public async Task TheReviewStepNamesWhatHoldsThePort()
        {
            IReadOnlyList<int>? asked = null;
            var wizard = new WizardViewModel(
                default,
                () => ServiceNames.None,
                new FakeServiceMachine(),
                ports =>
                {
                    asked = ports;
                    return new[] { new PortInUse(8000, "python.exe", 4312) };
                })
            {
                TargetPath = @"C:\apps\api\server.exe",
                ServiceId = "api",
                Arguments = "main:app --port 8000",
            };

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Equal(new[] { 8000 }, asked);
            Assert.Contains("M.Port.InUse", wizard.EnvironmentWarnings);
            Assert.DoesNotContain("M.Port.InUse", wizard.Problems);
        }

        /// <summary>A port check that fails costs the review step nothing else: the names are still checked.</summary>
        [Fact]
        public async Task APortCheckThatFailsLeavesTheNamesStanding()
        {
            var wizard = new WizardViewModel(
                default,
                () => new ServiceNames(new[] { new InstalledService("api", "API") }),
                new FakeServiceMachine(),
                _ => throw new IOException("unreadable"))
            {
                TargetPath = @"C:\apps\api\server.exe",
                ServiceId = "api",
                Arguments = "--port 8000",
            };

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.IdTaken", wizard.Problems);
            Assert.DoesNotContain("M.Port.InUse", wizard.EnvironmentWarnings);
        }

        private static PortTable Table(params (int Port, int ProcessId)[] listeners)
        {
            var all = new List<ListeningPort>();
            foreach (var (port, processId) in listeners)
            {
                all.Add(new ListeningPort("0.0.0.0", port, processId));
                all.Add(new ListeningPort("::", port, processId));
            }

            return new PortTable(all);
        }

        private static ProcessRecord P(int id, int parent, string name) =>
            new(id, parent, name, id <= 4 ? null : T0.AddMinutes(id / 100.0), TimeSpan.Zero, 0, 0);

        private static ProcessSnapshot Snapshot(params ProcessRecord[] records) => new(records);
    }
}
