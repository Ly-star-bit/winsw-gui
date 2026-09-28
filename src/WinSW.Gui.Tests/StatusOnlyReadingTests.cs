using System;
using System.Collections.Immutable;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The reading the dashboard keeps taking while its page is hidden, so that a crash is still
    /// seen within seconds: states alone, with no process snapshot behind them. What it must not
    /// do is read "nothing was looked at" as "there is no process".
    /// </summary>
    public class StatusOnlyReadingTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>Switching to the log page for a minute must not wipe the sparkline, the memory trace or the uptime.</summary>
        [Fact]
        public void ARunningProcessKeepsItsCountersAndTraces()
        {
            var entry = Running();
            var started = entry.StartedAt;
            int cpuPoints = entry.CpuHistory.Count;

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Running, 4321));

            Assert.Equal(ServiceControllerStatus.Running, entry.Status);
            Assert.Equal(cpuPoints, entry.CpuHistory.Count);
            Assert.Single(entry.MemoryHistory);
            Assert.Equal(32 * 1024L * 1024L, entry.WorkingSetBytes);
            Assert.Equal(100, entry.HandleCount);
            Assert.Equal(started, entry.StartedAt);
        }

        [Fact]
        public void AStoppedServiceTakesItsStateAndDropsItsCounters()
        {
            var entry = Running();

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Stopped, 0, exitCode: 1067));

            Assert.Equal(ServiceControllerStatus.Stopped, entry.Status);
            Assert.Equal(0, entry.ProcessId);
            Assert.Equal(1067, entry.LastExitCode);
            Assert.Equal(0, entry.WorkingSetBytes);
            Assert.Null(entry.StartedAt);
            Assert.Empty(entry.CpuHistory);
            Assert.Empty(entry.MemoryHistory);
        }

        /// <summary>
        /// Restarted by Windows' recovery while the page was hidden: the figures belong to the
        /// process that is gone, and the next full reading starts from nothing rather than from
        /// its CPU time. What the last run had running is kept: it is what the next full reading
        /// looks for beside the new wrapper.
        /// </summary>
        [Fact]
        public void AReplacedProcessTakesItsFiguresWithIt()
        {
            var entry = Running();
            entry.RememberedProcesses = ImmutableArray.Create(new ProcessMark(5000, T0, "python.exe"));

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Running, 9876));

            Assert.Equal(9876, entry.ProcessId);
            Assert.Empty(entry.CpuHistory);
            Assert.Equal(0, entry.WorkingSetBytes);
            Assert.Single(entry.RememberedProcesses);
        }

        /// <summary>The same process keeps what was noted under it, for the stray check once it stops.</summary>
        [Fact]
        public void TheSameProcessKeepsWhatWasNotedUnderIt()
        {
            var entry = Running();
            entry.RememberedProcesses = ImmutableArray.Create(new ProcessMark(5000, T0, "python.exe"));

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Running, 4321));

            Assert.Single(entry.RememberedProcesses);
        }

        /// <summary>
        /// Nothing is looked for behind a hidden page, so the finding of the last full reading stays
        /// in every state until the next one: a leftover is told while the service starts and runs
        /// again, and clearing it here would have the banner wait out its seconds anew at every
        /// restart of a loop.
        /// </summary>
        [Fact]
        public void AStrayFindingStaysUntilTheNextFullReading()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            ServiceDiscovery.Apply(entry, States(ServiceControllerStatus.Stopped, 0));
            var stray = new StrayFinding(new ProcessMark(5000, T0, "python.exe"), null);
            entry.NoteStray(stray, T0);
            entry.NoteStray(stray, T0.AddSeconds(3));
            Assert.True(entry.HasStrayProcess);

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Stopped, 0));
            Assert.True(entry.HasStrayProcess);

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.StartPending, 0));
            Assert.True(entry.HasStrayProcess);

            ServiceDiscovery.ApplyStatus(entry, States(ServiceControllerStatus.Running, 9876));
            Assert.True(entry.HasStrayProcess);

            // The next full reading is what tells: here, that nothing is left.
            ServiceDiscovery.Apply(entry, States(ServiceControllerStatus.Running, 9876));
            Assert.False(entry.HasStrayProcess);
        }

        [Fact]
        public void AServiceThatCannotBeQueriedLosesItsStateAsUnderAFullReading()
        {
            var entry = Running();

            ServiceDiscovery.ApplyStatus(entry, default);

            Assert.Null(entry.Status);
            Assert.Equal(0, entry.ProcessId);
            Assert.Empty(entry.CpuHistory);
        }

        private static ServiceSample States(ServiceControllerStatus status, int processId, int exitCode = 0) => new()
        {
            Queried = true,
            Status = status,
            ProcessId = processId,
            LastExitCode = exitCode,
        };

        /// <summary>A running service after two full readings: a CPU figure, a memory point, an uptime.</summary>
        private static ServiceEntry Running()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            var full = new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Running,
                ProcessId = 4321,
                HasProcess = true,
                ProcessorTime = TimeSpan.FromSeconds(1),
                WorkingSet = 32 * 1024 * 1024,
                Handles = 100,
                StartedAt = DateTime.Now.AddHours(-1),
            };

            ServiceDiscovery.Apply(entry, full);
            ServiceDiscovery.Apply(entry, full with { ProcessorTime = TimeSpan.FromSeconds(2) });
            return entry;
        }
    }
}
