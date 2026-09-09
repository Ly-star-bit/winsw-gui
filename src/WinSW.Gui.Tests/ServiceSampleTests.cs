using System;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The half of the status poll that writes. Reading is a round trip to the service control
    /// manager and belongs to the machine; what the reading then does to an entry is this
    /// code's, and it is what runs on the UI thread.
    /// </summary>
    public class ServiceSampleTests
    {
        [Fact]
        public void AServiceThatCannotBeQueriedLosesItsStateAndItsMetrics()
        {
            var entry = Running();

            // default: Queried is false, which is what Sample returns for a service that has
            // been uninstalled between the scan and the reading.
            ServiceDiscovery.Apply(entry, default);

            Assert.Null(entry.Status);
            Assert.Equal(0, entry.ProcessId);
            Assert.Equal("—", entry.MemoryText);
            Assert.Null(entry.StartedAt);
        }

        [Fact]
        public void AStoppedServiceKeepsItsStateAndDropsItsMetrics()
        {
            var entry = Running();

            ServiceDiscovery.Apply(entry, new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Stopped,
                ProcessId = 0,
                LastExitCode = 1067,
            });

            Assert.Equal(ServiceControllerStatus.Stopped, entry.Status);
            Assert.Equal(0, entry.ProcessId);
            Assert.Equal(1067, entry.LastExitCode);
            Assert.Equal("—", entry.MemoryText);
        }

        /// <summary>
        /// A running service whose process could not be opened — a LocalSystem process seen
        /// from a standard user — still has a state worth showing.
        /// </summary>
        [Fact]
        public void AProcessThatCouldNotBeOpenedLeavesTheStateIntact()
        {
            var entry = Running();

            ServiceDiscovery.Apply(entry, new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Running,
                ProcessId = 4321,
                HasProcess = false,
            });

            Assert.Equal(ServiceControllerStatus.Running, entry.Status);
            Assert.Equal(4321, entry.ProcessId);

            // The process ID is still shown — it is what the service control manager said —
            // but nothing was read out of that process, so the counters are cleared rather
            // than left displaying the previous reading.
            Assert.Equal(0, entry.WorkingSetBytes);
            Assert.Equal(0, entry.HandleCount);
            Assert.Null(entry.StartedAt);
        }

        [Fact]
        public void AReadingWithMetricsReachesTheEntry()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            var started = DateTime.Now.AddMinutes(-5);

            ServiceDiscovery.Apply(entry, new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Running,
                ProcessId = 4321,
                HasProcess = true,
                ProcessorTime = TimeSpan.FromSeconds(3),
                WorkingSet = 64 * 1024 * 1024,
                Handles = 512,
                StartedAt = started,
            });

            Assert.Equal(ServiceControllerStatus.Running, entry.Status);
            Assert.Equal(4321, entry.ProcessId);
            Assert.Equal(512, entry.HandleCount);
            Assert.Equal(64 * 1024L * 1024L, entry.WorkingSetBytes);
            Assert.Equal(started, entry.StartedAt);
            Assert.Contains("64", entry.MemoryText);
        }

        /// <summary>
        /// CPU is a delta, so the first reading establishes the baseline and only the second
        /// can produce a figure. The poll takes readings off the UI thread and applies them
        /// here, which is what keeps that running history in one place.
        /// </summary>
        [Fact]
        public void CpuNeedsTwoReadingsBeforeItSaysAnything()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");

            var first = new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Running,
                ProcessId = 4321,
                HasProcess = true,
                ProcessorTime = TimeSpan.FromSeconds(1),
                WorkingSet = 1024,
                Handles = 10,
            };

            ServiceDiscovery.Apply(entry, first);
            Assert.Empty(entry.CpuHistory);

            ServiceDiscovery.Apply(entry, first with { ProcessorTime = TimeSpan.FromSeconds(2) });
            Assert.Single(entry.CpuHistory);
        }

        /// <summary>A reading is plain values, so it can safely cross back from a worker.</summary>
        [Fact]
        public void AReadingCarriesNoEntry()
        {
            foreach (var property in typeof(ServiceSample).GetProperties())
            {
                Assert.True(
                    property.PropertyType.IsValueType || property.PropertyType == typeof(string),
                    $"{property.Name} is a {property.PropertyType.Name}, which is not a value to hand between threads");
            }
        }

        private static ServiceEntry Running()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            ServiceDiscovery.Apply(entry, new ServiceSample
            {
                Queried = true,
                Status = ServiceControllerStatus.Running,
                ProcessId = 4321,
                HasProcess = true,
                ProcessorTime = TimeSpan.FromSeconds(1),
                WorkingSet = 32 * 1024 * 1024,
                Handles = 100,
                StartedAt = DateTime.Now.AddHours(-1),
            });

            return entry;
        }
    }
}
